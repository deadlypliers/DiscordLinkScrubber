namespace LinkCleaner.Core;

/// <summary>Out-of-the-box rules. Servers can edit all of this at runtime.</summary>
public static class DefaultSites
{
    public static IReadOnlyList<SiteRule> Create() =>
    [
        new SiteRule
        {
            Name = "youtube",
            Hosts = ["youtube.com", "youtu.be", "youtube-nocookie.com"],
            StripParams = ["si", "feature", "pp", "utm_*", "fbclid", "gclid"],
        },
        new SiteRule
        {
            Name = "instagram",
            Hosts = ["instagram.com"],
            StripParams = ["igsh", "igshid", "utm_*", "fbclid"],
            MirrorHost = "kkinstagram.com",
            MirrorPathPattern = @"^/(?:[^/]+/)?(?:p|reel|reels|tv)/[^/]+",
        },
        new SiteRule
        {
            Name = "x",
            Hosts = ["x.com", "twitter.com"],
            StripParams = ["s", "t", "ref_src", "ref_url", "utm_*"],
            MirrorHost = "fixupx.com",
            MirrorPathPattern = @"^/[^/]+/status/\d+",
        },
        new SiteRule
        {
            Name = "facebook",
            Hosts = ["facebook.com", "fb.com", "fb.watch"],
            StripParams = ["fbclid", "mibextid", "__cft__*", "__tn__", "rdid", "utm_*"],
        },
        new SiteRule
        {
            Name = "reddit",
            Hosts = ["reddit.com", "redd.it"],
            StripParams = ["utm_*", "share_id", "ref", "ref_source", "correlation_id", "rdt"],
        },
        new SiteRule
        {
            Name = "tiktok",
            Hosts = ["tiktok.com"],
            StripParams =
            [
                "_d", "_r", "_t", "is_from_webapp", "sender_device", "sender_web_id", "share_app_id",
                "share_link_id", "share_app_name", "share_item_id", "tt_from", "u_code", "user_id",
                "utm_*",
            ],
        },
        new SiteRule
        {
            Name = "spotify",
            Hosts = ["spotify.com"],
            StripParams = ["si", "utm_*", "nd", "dlsi", "go"],
        },
        new SiteRule
        {
            Name = "twitch",
            Hosts = ["twitch.tv"],
            StripParams = ["sr", "tt_medium", "tt_content", "utm_*"],
        },
    ];
}
