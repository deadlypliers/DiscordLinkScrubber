using System.Text.Json;
using LinkCleaner.Bot.Commands;
using LinkCleaner.Core;

namespace LinkCleaner.Bot;

/// <summary>What /linkcleaner export writes and /linkcleaner import reads.</summary>
/// <remarks>
/// Deliberately leaves out everything that belongs to one server: channel rules, exempt roles, the mod-log
/// channel and members' ping opt-outs.
/// </remarks>
public sealed record ConfigBundle(
    int Version,
    IReadOnlyList<SiteRule> Sites,
    GlobalRule Global,
    string InfoLink,
    string MirrorPrefix,
    bool PingAuthor,
    Enforcement Enforcement = Enforcement.TrackingOnly);

public static class ConfigTransfer
{
    public const int FormatVersion = 1;
    public const int MaxBytes = 64 * 1024;
    public const int MaxSites = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Export(GuildConfig cfg) => JsonSerializer.Serialize(
        new ConfigBundle(FormatVersion, cfg.Sites, cfg.GlobalRule, cfg.InfoLink, cfg.MirrorPrefix, cfg.PingAuthor, cfg.Enforcement),
        JsonOptions);

    /// <summary>Parses and fully validates an import file. Nothing is written until this succeeds.</summary>
    public static bool TryParse(string json, out ConfigBundle bundle, out string error)
    {
        bundle = null!;
        if (json.Length > MaxBytes)
            return Fail("That file is too large (64 KB max).", out error);

        ConfigBundle? parsed;
        try { parsed = JsonSerializer.Deserialize<ConfigBundle>(json); }
        catch (JsonException) { return Fail("That isn't a LinkCleaner config file (invalid JSON).", out error); }

        if (parsed is null || parsed.Sites is null || parsed.Global?.StripParams is null || parsed.InfoLink is null || parsed.MirrorPrefix is null)
            return Fail("That file is missing required fields.", out error);
        if (parsed.Version != FormatVersion)
            return Fail($"Unsupported config version {parsed.Version} (this bot reads version {FormatVersion}).", out error);
        if (parsed.Sites.Count > MaxSites)
            return Fail($"Too many sites (max {MaxSites}).", out error);
        if (!Uri.TryCreate(parsed.InfoLink, UriKind.Absolute, out var info) || info.Scheme is not ("http" or "https"))
            return Fail("The info link isn't an http(s) URL.", out error);
        if (!Enum.IsDefined(parsed.Enforcement))
            return Fail("The enforcement level is invalid.", out error);
        if (parsed.MirrorPrefix.Length > Limits.MirrorPrefixLength)
            return Fail("The mirror prefix is too long.", out error);
        if (!ValidParams(parsed.Global.StripParams))
            return Fail("The server-wide parameter list is invalid.", out error);
        if (parsed.Global.GenericValues is { } generic && !ValidGenericValues(generic))
            return Fail("The generic utm value list is invalid.", out error);

        var names = new HashSet<string>();
        foreach (var site in parsed.Sites)
        {
            if (site is null || site.Name is null || !CommandHelpers.IsValidSiteName(site.Name))
                return Fail("A site has an invalid name.", out error);
            if (!names.Add(site.Name))
                return Fail($"Site `{site.Name}` appears twice.", out error);
            if (site.Hosts is null || site.StripParams is null || site.StripPathPatterns is null)
                return Fail($"Site `{site.Name}` is missing fields.", out error);
            if (site.Hosts.Count > Limits.HostsPerSite || !site.Hosts.All(IsBareHost))
                return Fail($"Site `{site.Name}` has an invalid host list.", out error);
            if (!ValidParams(site.StripParams))
                return Fail($"Site `{site.Name}` has an invalid parameter list.", out error);
            if (site.TidyParams is null || !ValidParams(site.TidyParams))
                return Fail($"Site `{site.Name}` has an invalid tidy parameter list.", out error);
            if (site.StripPathPatterns.Count > Limits.PathPatternsPerSite || !site.StripPathPatterns.All(CommandHelpers.IsValidRegex))
                return Fail($"Site `{site.Name}` has an invalid path pattern.", out error);
            if (site.MirrorHost is not null && !IsBareHost(site.MirrorHost))
                return Fail($"Site `{site.Name}` has an invalid mirror host.", out error);
            if (site.MirrorPathPattern is not null && !CommandHelpers.IsValidRegex(site.MirrorPathPattern))
                return Fail($"Site `{site.Name}` has an invalid mirror path pattern.", out error);
            if (site.DefaultRevision < 0 || site.DefaultRevision > DefaultSites.Revision)
                return Fail($"Site `{site.Name}` has an invalid revision.", out error);
        }

        error = "";
        bundle = parsed;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool ValidParams(IReadOnlyList<string> values) =>
        values.Count <= Limits.EntriesPerList && values.All(v => v is not null && CommandHelpers.IsValidParamName(v));

    private static bool ValidGenericValues(IReadOnlyList<string> values) =>
        values.Count <= Limits.EntriesPerList && values.All(CommandHelpers.IsValidGenericValue);

    /// <summary>A plain hostname, exactly as the host commands would store it.</summary>
    private static bool IsBareHost(string host) =>
        host is not null && CommandHelpers.TryNormalizeHost(host, out var normalized) && normalized == host;
}
