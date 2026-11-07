using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LinkCleaner.Core;

namespace LinkCleaner.Bot.Commands;

internal static partial class CommandHelpers
{
    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex SiteNameRegex();

    /// <summary>Every command answer is private to the admin who ran it and never pings anyone.</summary>
    public static Task Say(this InteractionModuleBase<SocketInteractionContext> module, string text) =>
        module.Context.Interaction.RespondAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);

    public static bool IsValidSiteName(string name) => SiteNameRegex().IsMatch(name);

    /// <summary>Accepts "example.com", "https://www.example.com/path" etc. and returns just the host.</summary>
    public static bool TryNormalizeHost(string input, out string host)
    {
        host = "";
        input = input.Trim();
        if (input.Length == 0)
            return false;

        if (!input.Contains("://", StringComparison.Ordinal))
            input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
            return false;

        host = uri.Host.ToLowerInvariant();
        return host.Contains('.');
    }

    public static bool IsValidParamName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => !char.IsWhiteSpace(c) && c is not ('&' or '=' or '#' or '?'));

    public static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string Describe(SiteRule s)
    {
        var mirror = string.IsNullOrWhiteSpace(s.MirrorHost) ? "none" : $"`{s.MirrorHost}`";
        return $"**{s.Name}** — {(s.Enabled ? "enabled" : "**disabled**")}\n" +
               $"Hosts: {Join(s.Hosts)}\n" +
               $"Stripped params: {Join(s.StripParams)}\n" +
               (s.StripPathPatterns.Count > 0 ? $"Stripped path patterns: {Join(s.StripPathPatterns)}\n" : "") +
               $"Mirror: {mirror}" +
               (s.MirrorHost is { Length: > 0 } && s.MirrorPathPattern is not null ? $" (paths matching `{s.MirrorPathPattern}`)" : "");
    }

    public static string DescribeGlobal(GlobalRule g) =>
        $"{(g.Enabled ? "on" : "**off**")}, removing {Join(g.StripParams)}";

    public static string Join(IEnumerable<string> values)
    {
        var list = values.Select(v => $"`{v}`").ToList();
        return list.Count == 0 ? "_none_" : string.Join(", ", list);
    }
}

/// <summary>Suggests the server's site names while an admin types a command.</summary>
public sealed class SiteAutocomplete(ConfigStore store) : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var typed = autocompleteInteraction.Data.Current.Value as string ?? "";
        var suggestions = store.Get(context.Guild.Id).Sites
            .Where(s => s.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => new AutocompleteResult(s.Name, s.Name));
        return Task.FromResult(AutocompletionResult.FromSuccess(suggestions));
    }
}
