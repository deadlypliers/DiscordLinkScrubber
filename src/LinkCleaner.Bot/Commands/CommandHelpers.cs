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

    /// <summary>Every command answer is private to the admin who ran it, never pings anyone, and fits in one message.</summary>
    public static Task Say(this InteractionModuleBase<SocketInteractionContext> module, string text) =>
        module.Context.Interaction.RespondAsync(Limits.FitMessage(text), ephemeral: true, allowedMentions: AllowedMentions.None);

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

    public static readonly string InvalidRegex = $"That isn't a valid regex ({Limits.RegexLength} characters max).";
    public const string InvalidHost = "That doesn't look like a hostname.";
    public const string InvalidParam = "That isn't a valid parameter name.";

    public static string ListFull(int max) => $"That list is full ({max} entries max). Remove one first.";

    public static bool IsValidRegex(string? pattern)
    {
        if (pattern is not { Length: > 0 and <= Limits.RegexLength })
            return false;
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
               (s.TidyParams.Count > 0 ? $"Tidy params (only removed alongside a stripped one): {Join(s.TidyParams)}\n" : "") +
               (s.StripPathPatterns.Count > 0 ? $"Stripped path patterns: {Join(s.StripPathPatterns)}\n" : "") +
               $"Mirror: {mirror}" +
               (s.MirrorHost is { Length: > 0 } && s.MirrorPathPattern is not null ? $" (paths matching `{s.MirrorPathPattern}`)" : "");
    }

    public static string DescribeGlobal(GlobalRule g) =>
        $"{(g.Enabled ? "on" : "**off**")}, removing {Join(g.StripParams)}\n" +
        $"`utm_*` params are removed when their value isn't one of these generic labels: {Join(g.EffectiveGenericValues)}";

    /// <summary>A short label such as "copy-link": no whitespace or control characters, 64 characters max.</summary>
    public static bool IsValidGenericValue(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '&' && c != '#');

    public static string Join(IEnumerable<string> values)
    {
        var list = values.Select(v => $"`{v}`").ToList();
        return list.Count == 0 ? "_none_" : string.Join(", ", list);
    }

    /// <summary>The list without <paramref name="value"/>, or null if it wasn't on it.</summary>
    public static IReadOnlyList<string>? Without(IReadOnlyList<string> list, string value, StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        var remaining = list.Where(v => !comparer.Equals(v, value)).ToList();
        return remaining.Count == list.Count ? null : remaining;
    }
}

/// <summary>Base for command groups that act on one named site.</summary>
public abstract class SiteCommandModule(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
{
    protected ConfigStore Store => store;

    protected SiteRule? Find(string site) => Store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);

    /// <summary>Runs <paramref name="action"/> with the named site, or tells the admin there is no such site.</summary>
    protected Task WithSite(string site, Func<SiteRule, Task> action) =>
        Find(site) is { } rule ? action(rule) : this.Say($"No site named `{site}`.");

    /// <param name="customises">False for toggles and resets, which keep the site receiving built-in updates.</param>
    protected void Save(SiteRule rule, bool customises = true) => Store.SaveSite(Context.Guild.Id, rule, customises);
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
