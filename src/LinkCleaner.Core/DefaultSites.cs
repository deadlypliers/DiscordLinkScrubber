namespace LinkCleaner.Core;

/// <summary>Out-of-the-box rules. Servers can edit all of this at runtime.</summary>
public static class DefaultSites
{
    /// <summary>Bump on every change to the list below; servers with unedited sites pick the change up automatically.</summary>
    public const int Revision = 2;

    /// <summary>The sites that shipped in revision 1, used to tell deleted defaults from newly added ones on old servers.</summary>
    public static readonly IReadOnlySet<string> OriginalNames = new HashSet<string>
        { "youtube", "instagram", "x", "facebook", "reddit", "tiktok", "spotify", "twitch" };

    public static IReadOnlyList<SiteRule> Create() =>
        [.. Build().Select(s => s with { DefaultRevision = Revision })];

    private static IReadOnlyList<SiteRule> Build() =>
    [
        new SiteRule
        {
            Name = "youtube",
            Hosts = ["youtube.com", "youtu.be", "youtube-nocookie.com"],
            StripParams = ["si", "is", "feature", "pp", "utm_*", "fbclid", "gclid"],
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

        // ---- Added in revision 2. Every param below is confirmed by docs/research/site-params.md;
        // utm_* is handled by the server-wide rule. Functional params (psc, th, jbv, sk, smid, hash, q...) are kept.
        new SiteRule
        {
            Name = "amazon",
            Hosts =
            [
                "amazon.com", "amazon.co.uk", "amazon.de", "amazon.fr", "amazon.it", "amazon.es", "amazon.nl",
                "amazon.se", "amazon.pl", "amazon.ca", "amazon.com.au", "amazon.co.jp", "amazon.in",
                "amazon.com.br", "amazon.com.mx", "amazon.sg", "amazon.ae", "amazon.sa", "amazon.com.tr",
            ],
            StripParams =
            [
                "tag", "ascsubtag", "pf_rd_*", "pd_rd_*", "qid", "sr", "srs", "ref", "ref_", "linkCode",
                "creativeASIN", "camp", "creative", "content-id", "crid", "sprefix", "_encoding", "dib",
                "dib_tag", "social_share",
            ],
            StripPathPatterns = ["/ref=[^/?#]*"],
        },
        new SiteRule
        {
            Name = "ebay",
            Hosts = ["ebay.com", "ebay.co.uk", "ebay.de", "ebay.fr", "ebay.it", "ebay.es", "ebay.com.au", "ebay.ca"],
            StripParams = ["_trkparms", "_trksid", "_from"],
        },
        new SiteRule
        {
            Name = "aliexpress",
            Hosts = ["aliexpress.com", "aliexpress.us"],
            StripParams =
            [
                "ws_ab_test", "btsid", "algo_expid", "algo_pvid", "gps-id", "scm*", "sk", "terminal_id",
                "aff_request_id", "mall_affr",
            ],
        },
        new SiteRule
        {
            Name = "etsy",
            Hosts = ["etsy.com"],
            StripParams = ["click_key", "click_sum", "organic_search_click"],
        },
        new SiteRule
        {
            Name = "linkedin",
            Hosts = ["linkedin.com", "lnkd.in"],
            StripParams = ["trk", "trackingId", "refId", "lipi", "licu"],
        },
        new SiteRule
        {
            Name = "apple",
            Hosts = ["music.apple.com", "podcasts.apple.com", "itunes.apple.com", "tv.apple.com"],
            StripParams = ["itscg", "itsct", "ign-itsc*"],
        },
        new SiteRule
        {
            Name = "netflix",
            Hosts = ["netflix.com"],
            StripParams = ["trackId", "tctx"],
        },
        new SiteRule
        {
            Name = "steam",
            Hosts = ["steampowered.com"],
            StripParams = ["snr"],
        },
        new SiteRule
        {
            Name = "google",
            Hosts = ["google.com", "google.co.uk", "google.de", "google.fr", "google.ca", "google.com.au", "google.co.jp"],
            StripParams =
            [
                "ved", "ei", "oq", "uact", "aqs", "sourceid", "sxsrf", "rlz", "sca_esv", "sca_upv", "iflsig",
                "gs_*", "gws_*", "pcampaignid",
            ],
        },
        new SiteRule
        {
            Name = "bing",
            Hosts = ["bing.com"],
            StripParams = ["cvid", "sp", "sc", "qs", "qp", "form"],
        },
        new SiteRule
        {
            Name = "medium",
            Hosts = ["medium.com"],
            StripParams = ["source"],
        },
    ];
}
