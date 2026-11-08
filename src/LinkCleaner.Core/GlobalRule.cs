using System.Text.Json.Serialization;

namespace LinkCleaner.Core;

/// <summary>
/// Rules applied to every http(s) link, whatever site it points to.
/// Unlike <see cref="SiteRule"/>, this also applies to sites that are not on the site list.
/// </summary>
/// <param name="Enabled">Master switch.</param>
/// <param name="StripParams">
/// Params that always identify a click or recipient (same syntax as <see cref="SiteRule.StripParams"/>,
/// "x_*" is a prefix wildcard). Finding one makes the bot act.
/// </param>
/// <param name="GenericValues">
/// Values of <see cref="ValueGated"/> params (utm_*) that are known share-button labels and identify nobody.
/// Null means <see cref="DefaultGenericValues"/>. A utm param with any other value is treated as identifying,
/// because campaign values can carry a referral code or username.
/// </param>
public sealed record GlobalRule(
    bool Enabled,
    IReadOnlyList<string> StripParams,
    IReadOnlyList<string>? GenericValues = null)
{
    /// <summary>Known per-click and per-recipient identifiers, removed everywhere unless an admin turns this off.</summary>
    public static IReadOnlyList<string> DefaultStripParams { get; } =
    [
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "twclid", "ttclid", "yclid", "li_fat_id",
        "igshid", "mc_eid", "_hsenc", "_hsmi", "irclickid", "epik", "ScCid", "vero_id", "oly_enc_id",
        "oly_anon_id", "__s",
    ];

    /// <summary>Params whose value decides whether they identify anyone. Not editable.</summary>
    public static IReadOnlyList<string> ValueGated { get; } = ["utm_*"];

    /// <summary>Labels seen on real share buttons. Anything not listed counts as identifying.</summary>
    public static IReadOnlyList<string> DefaultGenericValues { get; } =
    [
        "share", "share_button", "copy-link", "copy_link", "web", "web2x", "web3x", "ios_app", "android_app",
        "ioscss", "androidcss", "social", "sms", "email",
    ];

    public static GlobalRule Default { get; } = new(true, DefaultStripParams);

    /// <summary>The name reported for links that matched no site rule.</summary>
    public const string SiteName = "any site";

    /// <summary>The allowlist actually in force.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveGenericValues => GenericValues ?? DefaultGenericValues;

    /// <summary>True if a gated param's value is known to be generic (an empty value identifies nobody).</summary>
    public bool IsGeneric(string value) =>
        value.Length == 0 || EffectiveGenericValues.Contains(value, StringComparer.OrdinalIgnoreCase);
}
