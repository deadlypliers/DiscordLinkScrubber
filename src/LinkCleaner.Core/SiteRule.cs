namespace LinkCleaner.Core;

/// <summary>
/// Cleaning rules for one site. Everything is data so it can be edited at runtime
/// (via slash commands) and persisted as JSON.
/// </summary>
public sealed record SiteRule
{
    /// <summary>Unique, human-friendly key, e.g. "youtube".</summary>
    public required string Name { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Hostnames covered by this rule. A host matches if it equals an entry or is a
    /// subdomain of it, so "youtube.com" also covers "m.youtube.com". Listing "x.com"
    /// and "twitter.com" together makes both aliases of one site.
    /// </summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>
    /// Query parameter names to strip (case-insensitive). A trailing '*' is a prefix
    /// wildcard, e.g. "utm_*". Every parameter not listed is kept, which is how
    /// functional params such as YouTube's "v", "t" and "list" are preserved.
    /// </summary>
    public IReadOnlyList<string> StripParams { get; init; } = [];

    /// <summary>
    /// Embed-friendly mirror host (e.g. "fixupx.com") for sites that need a login to
    /// view. Null or empty means no mirror. This host is also treated as an alias of
    /// the site, so params get stripped from links that already use the mirror.
    /// </summary>
    public string? MirrorHost { get; init; }

    /// <summary>
    /// Regex applied to the URL path to decide which links get a mirror (e.g. posts but
    /// not profiles). Null means every path except the site root.
    /// </summary>
    public string? MirrorPathPattern { get; init; }
}
