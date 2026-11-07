using System.Text;
using System.Text.RegularExpressions;

namespace LinkCleaner.Core;

/// <summary>
/// Pure link-cleaning logic: message text and site rules in, results out.
/// No Discord, network or storage code lives here.
/// </summary>
public static partial class LinkProcessor
{
    // '|' is excluded so spoilered links (||url||) stop at the closing bars.
    [GeneratedRegex(@"https?://[^\s<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // Fenced blocks first, then inline code.
    [GeneratedRegex(@"```.*?```|`[^`\n]+`", RegexOptions.Singleline)]
    private static partial Regex CodeRegex();

    private const string TrailingJunk = ".,;:!?'\"*~_";

    public static ProcessedMessage Process(string? content, IReadOnlyList<SiteRule> sites)
    {
        if (string.IsNullOrEmpty(content))
            return new ProcessedMessage("", "", [], false);

        var codeRanges = CodeRegex().Matches(content).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var links = new List<LinkResult>();

        foreach (Match match in UrlRegex().Matches(content))
        {
            if (codeRanges.Any(r => match.Index >= r.Index && match.Index < r.End))
                continue;
            // <https://...> is Discord's "suppress embed" form: the user chose to leave it alone.
            if (match.Index > 0 && content[match.Index - 1] == '<')
                continue;

            var raw = TrimTrailing(match.Value);
            var result = Evaluate(raw, match.Index, sites);
            if (result is not null)
                links.Add(result);
        }

        var sb = new StringBuilder();
        var pos = 0;
        foreach (var link in links.Where(l => l.TrackingRemoved))
        {
            sb.Append(content, pos, link.Start - pos).Append(link.Replacement);
            pos = link.Start + link.Length;
        }
        sb.Append(content, pos, content.Length - pos);

        return new ProcessedMessage(content, sb.ToString(), links, HasUserText(content));
    }

    private static LinkResult? Evaluate(string url, int start, IReadOnlyList<SiteRule> sites)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = NormalizeHost(uri.Host);
        foreach (var site in sites)
        {
            if (!site.Enabled)
                continue;

            var isMirrorHost = !string.IsNullOrWhiteSpace(site.MirrorHost) && HostMatches(host, site.MirrorHost);
            if (!isMirrorHost && !site.Hosts.Any(h => HostMatches(host, h)))
                continue;

            var (cleaned, removed) = StripParams(url, site.StripParams);
            var mirror = isMirrorHost ? null : BuildMirror(cleaned, uri.AbsolutePath, site);
            return new LinkResult(url, start, site.Name, cleaned, removed, mirror);
        }

        return null;
    }

    private static string? BuildMirror(string cleaned, string path, SiteRule site)
    {
        if (string.IsNullOrWhiteSpace(site.MirrorHost))
            return null;

        var applies = site.MirrorPathPattern is null
            ? path.Length > 1
            : Regex.IsMatch(path, site.MirrorPathPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (!applies)
            return null;

        // Swap only the authority so the rest of the URL keeps its exact original encoding.
        var authorityStart = cleaned.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = cleaned.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = cleaned.Length;
        return "https://" + site.MirrorHost.Trim().ToLowerInvariant() + cleaned[authorityEnd..];
    }

    /// <summary>Removes matching query params while leaving every other character of the URL untouched.</summary>
    public static (string Url, bool Removed) StripParams(string url, IReadOnlyList<string> patterns)
    {
        var hash = url.IndexOf('#');
        var fragment = hash >= 0 ? url[hash..] : "";
        var rest = hash >= 0 ? url[..hash] : url;

        var q = rest.IndexOf('?');
        if (q < 0)
            return (url, false);

        var kept = new List<string>();
        var removed = 0;
        foreach (var part in rest[(q + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=');
            var rawName = eq >= 0 ? part[..eq] : part;
            string name;
            try { name = Uri.UnescapeDataString(rawName); }
            catch (UriFormatException) { name = rawName; }

            if (part.Length > 0 && patterns.Any(p => ParamMatches(name, p)))
                removed++;
            else
                kept.Add(part);
        }

        if (removed == 0)
            return (url, false);

        var query = string.Join('&', kept.Where(k => k.Length > 0));
        return (rest[..q] + (query.Length > 0 ? "?" + query : "") + fragment, true);
    }

    private static bool ParamMatches(string name, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return false;
        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>True if host equals the entry or is a subdomain of it (never a lookalike like "notyoutube.com").</summary>
    private static bool HostMatches(string host, string entry)
    {
        entry = NormalizeHost(entry);
        return entry.Length > 0 && (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal));
    }

    private static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (TrailingJunk.Contains(last) || Unbalanced(url, last))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    private static bool Unbalanced(string url, char last) => last switch
    {
        ')' => url.Count(c => c == ')') > url.Count(c => c == '('),
        ']' => url.Count(c => c == ']') > url.Count(c => c == '['),
        _ => false,
    };

    private static bool HasUserText(string content)
    {
        var withoutUrls = UrlRegex().Replace(content, "");
        return withoutUrls.Any(c => !char.IsWhiteSpace(c) && "<>|*_~()[]".IndexOf(c) < 0);
    }
}
