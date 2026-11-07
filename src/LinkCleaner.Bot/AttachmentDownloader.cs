using System.Text;

namespace LinkCleaner.Bot;

/// <summary>
/// Reads a config file an admin attached to /linkcleaner import. This is the only outbound HTTP the bot
/// makes: it fetches nothing but Discord's own attachment hosts, never links from members' messages.
/// </summary>
public sealed class AttachmentDownloader(HttpClient http)
{
    private static readonly string[] AllowedHosts = ["cdn.discordapp.com", "media.discordapp.net"];

    public static bool IsDiscordAttachmentUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <exception cref="InvalidOperationException">With a message that is safe to show the admin.</exception>
    public async Task<string> DownloadTextAsync(string url, int maxBytes, CancellationToken ct = default)
    {
        if (!IsDiscordAttachmentUrl(url))
            throw new InvalidOperationException("That isn't a Discord attachment.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException("That file is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new InvalidOperationException("That file is too large.");
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new InvalidOperationException("Couldn't download that file from Discord. Try again.");
        }
    }
}
