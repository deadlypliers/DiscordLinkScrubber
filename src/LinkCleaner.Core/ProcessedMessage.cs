namespace LinkCleaner.Core;

public sealed record ProcessedMessage(
    string Content,
    string RewrittenContent,
    IReadOnlyList<LinkResult> Links)
{
    /// <summary>True if any link carried tracking params: the message should be reposted.</summary>
    public bool HasTrackingRemoved => Links.Any(l => l.TrackingRemoved);

    /// <summary>Replacement links (mirror or cleaned) for links whose tracking was removed.</summary>
    public IReadOnlyList<string> ReplacementLinks =>
        Links.Where(l => l.TrackingRemoved).Select(l => l.Replacement).Distinct().ToList();

    /// <summary>Mirror links for already-clean links on login-walled sites: these get a reply, no repost.</summary>
    public IReadOnlyList<string> MirrorReplyLinks =>
        Links.Where(l => !l.TrackingRemoved && l.Mirror is not null).Select(l => l.Mirror!).Distinct().ToList();

    /// <summary>True if there is anything for the bot to do.</summary>
    public bool HasWork => HasTrackingRemoved || MirrorReplyLinks.Count > 0;
}
