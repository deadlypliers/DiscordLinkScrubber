namespace LinkCleaner.Core;

/// <summary>One link found in a message that belongs to a configured site.</summary>
/// <param name="Original">The link exactly as written.</param>
/// <param name="Start">Offset of the link in the message content.</param>
/// <param name="Site">Name of the matching <see cref="SiteRule"/>.</param>
/// <param name="Cleaned">The link with tracking params removed (equals Original if none).</param>
/// <param name="TrackingRemoved">True if at least one tracking param was stripped.</param>
/// <param name="Mirror">Embed-friendly mirror of the cleaned link, if the site has one and applies.</param>
public sealed record LinkResult(
    string Original,
    int Start,
    string Site,
    string Cleaned,
    bool TrackingRemoved,
    string? Mirror)
{
    public int Length => Original.Length;

    /// <summary>What replaces the original link when tracking was removed: the mirror if any, else the cleaned link.</summary>
    public string Replacement => Mirror ?? Cleaned;
}
