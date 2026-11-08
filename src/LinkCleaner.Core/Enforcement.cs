namespace LinkCleaner.Core;

/// <summary>How much of a link the bot insists on cleaning.</summary>
public enum Enforcement
{
    /// <summary>
    /// Only identifying params make the bot act. Cosmetic params (<see cref="SiteRule.TidyParams"/>, generic utm values)
    /// are removed only from a link that is being cleaned anyway.
    /// </summary>
    TrackingOnly = 0,

    /// <summary>Cosmetic params make the bot act too, even when nothing identifying is present.</summary>
    IncludeCosmetic = 1,
}
