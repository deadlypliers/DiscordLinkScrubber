namespace LinkCleaner.Core;

/// <summary>
/// Query parameters stripped from every http(s) link, whatever site it points to.
/// Unlike <see cref="SiteRule"/>, this also applies to sites that are not on the site list.
/// </summary>
/// <param name="Enabled">Master switch.</param>
/// <param name="StripParams">Same syntax as <see cref="SiteRule.StripParams"/> ("utm_*" is a prefix wildcard).</param>
public sealed record GlobalRule(bool Enabled, IReadOnlyList<string> StripParams)
{
    /// <summary>Marketing tags are removed everywhere unless an admin turns this off.</summary>
    public static GlobalRule Default { get; } = new(true, ["utm_*"]);

    /// <summary>The name reported for links that matched no site rule.</summary>
    public const string SiteName = "any site";
}
