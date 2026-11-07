using LinkCleaner.Core;

namespace LinkCleaner.Bot;

/// <summary>Immutable snapshot of one server's settings.</summary>
public sealed record GuildConfig(
    IReadOnlyList<SiteRule> Sites,
    string InfoLink,
    string MirrorPrefix,
    ulong? ModLogChannelId,
    IReadOnlySet<ulong> IncludedChannels,
    IReadOnlySet<ulong> ExcludedChannels,
    IReadOnlySet<ulong> ExemptRoles)
{
    /// <summary>
    /// Excluded channels never apply. If any channels are explicitly included, only those do;
    /// otherwise every channel does. A thread follows its parent channel's rules.
    /// </summary>
    public bool AppliesTo(ulong channelId, ulong? parentChannelId)
    {
        bool Listed(IReadOnlySet<ulong> set) =>
            set.Contains(channelId) || (parentChannelId is { } p && set.Contains(p));

        if (Listed(ExcludedChannels))
            return false;
        return IncludedChannels.Count == 0 || Listed(IncludedChannels);
    }
}
