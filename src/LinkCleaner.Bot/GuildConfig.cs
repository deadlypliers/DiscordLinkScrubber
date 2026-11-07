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
    IReadOnlySet<ulong> ExemptRoles,
    bool PingAuthor,
    IReadOnlySet<ulong> PingOptOuts,
    GlobalRule? Global = null)
{
    /// <summary>The server-wide strip list; the built-in default (utm_*) unless an admin changed it.</summary>
    public GlobalRule GlobalRule => Global ?? GlobalRule.Default;

    /// <summary>Built-in sites the admin edited that are behind the current default revision (so they missed an update).</summary>
    public IReadOnlyList<string> OutdatedBuiltIns =>
        Sites.Where(s => s.Customised && s.DefaultRevision < DefaultSites.Revision &&
                         DefaultSites.Create().Any(d => d.Name == s.Name))
            .Select(s => s.Name).ToList();

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
