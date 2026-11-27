using LinkCleaner.Core;

namespace LinkCleaner.Tests;

public class LinkProcessorTests
{
    private static ProcessedMessage Run(string content, IReadOnlyList<SiteRule>? sites = null) =>
        LinkProcessor.Process(content, sites ?? DefaultSites.Create(), GlobalRule.Default);

    // ---- Tracking removal: the link is rewritten ----------------------------------------

    [Theory]
    // YouTube: share id removed, functional params kept
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/yATmNdBgydc?si=p00pbu77", "https://youtu.be/yATmNdBgydc")]
    [InlineData("https://youtu.be/yATmNdBgydc?is=p00pbu77", "https://youtu.be/yATmNdBgydc")]
    [InlineData("https://www.youtube.com/watch?v=abc&si=xyz", "https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtube.com/watch?si=x&v=abc", "https://youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc?t=30&si=zzz", "https://youtu.be/abc?t=30")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL1&feature=share&si=x", "https://www.youtube.com/watch?v=abc&list=PL1")]
    [InlineData("https://m.youtube.com/watch?v=abc&SI=Upper", "https://m.youtube.com/watch?v=abc")]
    [InlineData("https://music.youtube.com/watch?v=abc&si=q", "https://music.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc?si=x#t=10", "https://youtu.be/abc#t=10")]
    // Facebook: no mirror by default
    [InlineData("https://www.facebook.com/share/p/abc/?mibextid=wwXIfr", "https://www.facebook.com/share/p/abc/")]
    [InlineData("https://m.facebook.com/story.php?story_fbid=1&id=2&fbclid=IwAR0", "https://m.facebook.com/story.php?story_fbid=1&id=2")]
    // Instagram profile: cleaned but not mirrored (path pattern only covers posts)
    [InlineData("https://www.instagram.com/someone/?igsh=abc", "https://www.instagram.com/someone/")]
    // Other sites
    [InlineData("https://open.spotify.com/track/abc?si=123&utm_source=copy-link", "https://open.spotify.com/track/abc")]
    [InlineData("https://www.reddit.com/r/dotnet/comments/1/x/?utm_source=share&utm_medium=web3x&utm_term=1", "https://www.reddit.com/r/dotnet/comments/1/x/")]
    [InlineData("https://www.tiktok.com/@u/video/1?_r=1&_t=abc&is_from_webapp=1", "https://www.tiktok.com/@u/video/1")]
    [InlineData("https://www.twitch.tv/videos/123?sr=a&utm_source=x&t=1h2m", "https://www.twitch.tv/videos/123?t=1h2m")]
    // Already on a mirror host: params stripped, no second mirror
    [InlineData("https://fixupx.com/user/status/123?s=20", "https://fixupx.com/user/status/123")]
    [InlineData("https://kkinstagram.com/p/ABC/?igsh=x", "https://kkinstagram.com/p/ABC/")]
    public void Strips_tracking_params(string input, string expected)
    {
        var result = Run(input);

        var link = Assert.Single(result.Links);
        Assert.True(link.TrackingRemoved);
        Assert.Equal(expected, link.Cleaned);
        Assert.Null(link.Mirror);
        Assert.Equal(expected, result.RewrittenContent);
        Assert.True(result.HasTrackingRemoved);
    }

    // ---- Tracking removal on login-walled sites: cleaned AND converted to the mirror ----

    [Theory]
    [InlineData("https://x.com/user/status/123?s=20&t=abc", "https://fixupx.com/user/status/123")]
    [InlineData("https://twitter.com/user/status/123?ref_src=twsrc%5Etfw&s=20", "https://fixupx.com/user/status/123")]
    [InlineData("https://mobile.twitter.com/user/status/123?s=09", "https://fixupx.com/user/status/123")]
    [InlineData("https://www.instagram.com/p/ABC123/?igsh=MXg", "https://kkinstagram.com/p/ABC123/")]
    [InlineData("https://www.instagram.com/reel/XYZ/?igsh=abc&img_index=2", "https://kkinstagram.com/reel/XYZ/?img_index=2")]
    [InlineData("https://www.instagram.com/someone/p/ABC/?igshid=abc", "https://kkinstagram.com/someone/p/ABC/")]
    public void Strips_tracking_and_uses_mirror(string input, string expectedMirror)
    {
        var result = Run(input);

        var link = Assert.Single(result.Links);
        Assert.True(link.TrackingRemoved);
        Assert.Equal(expectedMirror, link.Mirror);
        Assert.Equal(expectedMirror, link.Replacement);
        Assert.Equal(expectedMirror, result.RewrittenContent);
        // A repost carries the mirror already, so no separate mirror reply is wanted.
        Assert.Empty(result.MirrorReplyLinks);
    }

    // ---- Clean links on login-walled sites: mirror reply only ---------------------------

    [Theory]
    [InlineData("https://x.com/user/status/123", "https://fixupx.com/user/status/123")]
    [InlineData("https://twitter.com/user/status/123?lang=en", "https://fixupx.com/user/status/123?lang=en")]
    [InlineData("https://www.instagram.com/p/ABC/", "https://kkinstagram.com/p/ABC/")]
    [InlineData("https://www.instagram.com/reel/XYZ/?img_index=2", "https://kkinstagram.com/reel/XYZ/?img_index=2")]
    public void Clean_login_walled_link_gets_mirror_reply_only(string input, string expectedMirror)
    {
        var result = Run(input);

        Assert.False(result.HasTrackingRemoved);
        Assert.Equal(input, result.RewrittenContent);
        Assert.Equal([expectedMirror], result.MirrorReplyLinks);
        Assert.True(result.HasWork);
    }

    // ---- Nothing to do ------------------------------------------------------------------

    [Theory]
    [InlineData("https://youtu.be/abc?t=30")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL1")]
    [InlineData("https://www.instagram.com/someone/")]          // profile: no tracking, no mirror
    [InlineData("https://x.com/someone")]                        // profile: no mirror
    [InlineData("https://www.facebook.com/share/p/abc/")]
    [InlineData("https://fixupx.com/user/status/123")]           // already a mirror
    [InlineData("https://kkinstagram.com/p/ABC/")]
    [InlineData("https://example.com/?utm_source=share&si=1")]   // unknown site: only global identifiers count
    [InlineData("https://notyoutube.com/watch?v=a&si=x")]        // lookalike host
    [InlineData("https://youtube.com.evil.example/watch?si=x")]  // suffix trick
    [InlineData("just words, no links at all")]
    [InlineData("")]
    public void Leaves_message_alone(string input)
    {
        var result = Run(input);

        Assert.False(result.HasWork);
        Assert.Equal(input, result.RewrittenContent);
    }

    [Fact]
    public void Null_content_is_safe()
    {
        var result = LinkProcessor.Process(null, DefaultSites.Create());
        Assert.False(result.HasWork);
    }

    [Fact]
    public void Disabled_site_is_ignored()
    {
        var sites = DefaultSites.Create().Select(s => s.Name == "youtube" ? s with { Enabled = false } : s).ToList();
        var result = Run("https://youtu.be/abc?si=x", sites);
        Assert.False(result.HasWork);
    }

    [Fact]
    public void Site_without_mirror_never_mirrors()
    {
        var sites = DefaultSites.Create().Select(s => s.Name == "x" ? s with { MirrorHost = null } : s).ToList();
        var result = Run("https://x.com/user/status/1?s=20", sites);

        var link = Assert.Single(result.Links);
        Assert.Equal("https://x.com/user/status/1", link.Cleaned);
        Assert.Null(link.Mirror);
    }

    [Fact]
    public void Custom_rule_is_honoured()
    {
        var sites = new[]
        {
            new SiteRule { Name = "shop", Hosts = ["shop.example"], StripParams = ["ref*"] },
        };
        var result = Run("https://shop.example/item/1?ref_id=9&color=red", sites);
        Assert.Equal("https://shop.example/item/1?color=red", result.RewrittenContent);
    }

    // ---- Where links appear in the message ----------------------------------------------

    [Theory]
    [InlineData("<https://youtu.be/abc?si=x>")]                      // embed-suppressed
    [InlineData("see `https://youtu.be/abc?si=x` for details")]       // inline code
    [InlineData("```\nhttps://youtu.be/abc?si=x\n```")]               // code block
    [InlineData("[vid](<https://youtu.be/abc?si=x>)")]               // masked, embed-suppressed
    public void Skips_suppressed_and_code_links(string input)
    {
        var result = Run(input);
        Assert.False(result.HasWork);
        Assert.Equal(input, result.RewrittenContent);
    }

    [Theory]
    [InlineData("||https://youtu.be/abc?si=x||", "||https://youtu.be/abc||")]
    [InlineData("look at https://youtu.be/abc?si=x.", "look at https://youtu.be/abc.")]
    [InlineData("wow! https://youtu.be/abc?si=x!!", "wow! https://youtu.be/abc!!")]
    [InlineData("(https://youtu.be/abc?si=x)", "(https://youtu.be/abc)")]
    [InlineData("[vid](https://youtu.be/abc?si=x)", "[vid](https://youtu.be/abc)")]
    [InlineData("**https://youtu.be/abc?si=x**", "**https://youtu.be/abc**")]
    [InlineData("a https://youtu.be/abc?si=x b", "a https://youtu.be/abc b")]
    [InlineData("line1\nhttps://youtu.be/abc?si=x\nline3", "line1\nhttps://youtu.be/abc\nline3")]
    [InlineData("https://youtu.be/abc?si=x, and https://youtu.be/def?si=y", "https://youtu.be/abc, and https://youtu.be/def")]
    public void Keeps_surrounding_text_intact(string input, string expected)
    {
        var result = Run(input);
        Assert.Equal(expected, result.RewrittenContent);
    }

    [Fact]
    public void Only_changed_links_are_replaced()
    {
        var result = Run("clean https://youtu.be/abc?t=5 dirty https://youtu.be/def?si=x other https://example.com/?si=1");

        Assert.Equal("clean https://youtu.be/abc?t=5 dirty https://youtu.be/def other https://example.com/?si=1", result.RewrittenContent);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal(["https://youtu.be/def"], result.ReplacementLinks);
    }

    [Fact]
    public void Mixed_message_repost_and_mirror_reply()
    {
        // A dirty YouTube link forces a repost; the clean X link still earns a mirror reply.
        var result = Run("https://youtu.be/abc?si=x and https://x.com/u/status/9");

        Assert.True(result.HasTrackingRemoved);
        Assert.Equal("https://youtu.be/abc and https://x.com/u/status/9", result.RewrittenContent);
        Assert.Equal(["https://fixupx.com/u/status/9"], result.MirrorReplyLinks);
    }

    [Fact]
    public void Duplicate_links_are_deduplicated_in_reply_lists()
    {
        var result = Run("https://x.com/u/status/9 https://twitter.com/u/status/9");
        Assert.Equal(["https://fixupx.com/u/status/9"], result.MirrorReplyLinks);
    }

    [Fact]
    public void Link_positions_point_at_the_original_text()
    {
        var content = "hey https://youtu.be/abc?si=x";
        var link = Assert.Single(Run(content).Links);
        Assert.Equal(content.IndexOf("https", StringComparison.Ordinal), link.Start);
        Assert.Equal("https://youtu.be/abc?si=x", content.Substring(link.Start, link.Length));
    }

    // ---- StripParams directly -----------------------------------------------------------

    [Theory]
    [InlineData("https://a.example/p?x=1", "x", true, "https://a.example/p")]
    [InlineData("https://a.example/p?x=1&y=2", "x", true, "https://a.example/p?y=2")]
    [InlineData("https://a.example/p?y=2&x=1", "x", true, "https://a.example/p?y=2")]
    [InlineData("https://a.example/p?x", "x", true, "https://a.example/p")]
    [InlineData("https://a.example/p?xy=1", "x", false, "https://a.example/p?xy=1")]
    [InlineData("https://a.example/p?utm_a=1&utm_b=2&k=3", "utm_*", true, "https://a.example/p?k=3")]
    [InlineData("https://a.example/p?%75tm_a=1", "utm_*", true, "https://a.example/p")]
    [InlineData("https://a.example/p?k=a%20b&x=1", "x", true, "https://a.example/p?k=a%20b")]
    [InlineData("https://a.example/p?k=1", "x", false, "https://a.example/p?k=1")]
    [InlineData("https://a.example/p", "x", false, "https://a.example/p")]
    public void StripParams_behaviour(string url, string pattern, bool removed, string expected)
    {
        var (result, wasRemoved) = LinkProcessor.StripParams(url, [pattern]);
        Assert.Equal(removed, wasRemoved);
        Assert.Equal(expected, result);
    }

    // ---- Global rule: applies to every site --------------------------------------------

    private static ProcessedMessage RunGlobal(string content, GlobalRule? global, IReadOnlyList<SiteRule>? sites = null) =>
        LinkProcessor.Process(content, sites ?? DefaultSites.Create(), global);

    [Theory]
    [InlineData("https://example.com/a?utm_source=jane42&id=5", "https://example.com/a?id=5")]
    [InlineData("https://news.example/story?utm_campaign=c", "https://news.example/story")]
    [InlineData("https://news.example/story?fbclid=1&utm_medium=web", "https://news.example/story")]
    [InlineData("read https://blog.example/p?UTM_Medium=m#top", "https://blog.example/p#top")]
    public void Global_rule_cleans_utm_on_sites_that_are_not_listed(string input, string cleaned)
    {
        var result = RunGlobal(input, GlobalRule.Default);

        Assert.True(result.HasTrackingRemoved);
        var link = Assert.Single(result.Links);
        Assert.Equal(GlobalRule.SiteName, link.Site);
        Assert.Null(link.Mirror);
        Assert.Contains(cleaned, result.RewrittenContent);
    }

    [Theory]
    [InlineData("https://example.com/a?id=5")]
    [InlineData("https://example.com/a")]
    [InlineData("https://example.com/a?utm_source=share&utm_medium=web3x")]
    [InlineData("https://example.com/a?UTM_SOURCE=Copy-Link")]
    [InlineData("https://example.com/a?utm_source=copy%2Dlink&utm_term=")]
    public void Global_rule_ignores_unlisted_links_without_tracking(string input)
    {
        var result = RunGlobal(input, GlobalRule.Default);

        Assert.Empty(result.Links);
        Assert.False(result.HasWork);
    }

    [Fact]
    public void Global_rule_can_be_disabled_or_absent()
    {
        const string url = "https://example.com/a?utm_source=jane42&fbclid=1";

        Assert.False(RunGlobal(url, GlobalRule.Default with { Enabled = false }).HasWork);
        Assert.False(RunGlobal(url, null).HasWork);
    }

    [Fact]
    public void Global_rule_params_are_configurable()
    {
        var global = new GlobalRule(true, ["fbclid", "ref_*"]);

        Assert.True(RunGlobal("https://example.com/a?fbclid=1", global).HasTrackingRemoved);
        Assert.True(RunGlobal("https://example.com/a?ref_src=1", global).HasTrackingRemoved);
        Assert.False(RunGlobal("https://example.com/a?gclid=1", global).HasWork); // not in this list
    }

    [Fact]
    public void Utm_values_decide_whether_a_link_is_acted_on()
    {
        // Generic labels alone: nothing to do, on any site.
        Assert.False(RunGlobal("https://youtu.be/abc?utm_source=copy-link", GlobalRule.Default).HasWork);
        // An unknown value might be a referral code, so it counts as tracking and takes the generic ones with it.
        var result = RunGlobal("https://youtu.be/abc?utm_source=share&utm_content=jane42&t=5", GlobalRule.Default);
        Assert.Equal("https://youtu.be/abc?t=5", result.RewrittenContent);
        // Generic utm params ride along with a real identifier.
        Assert.Equal("https://youtu.be/abc", RunGlobal("https://youtu.be/abc?si=x&utm_source=share", GlobalRule.Default).RewrittenContent);
    }

    [Fact]
    public void Generic_values_are_configurable()
    {
        var custom = GlobalRule.Default with { GenericValues = ["newsletter"] };

        Assert.False(RunGlobal("https://example.com/a?utm_source=newsletter", custom).HasWork);
        Assert.True(RunGlobal("https://example.com/a?utm_source=share", custom).HasWork); // no longer on the list
    }

    [Fact]
    public void An_explicit_utm_entry_in_the_strip_list_still_strips_unconditionally()
    {
        var legacy = new GlobalRule(true, ["utm_*"]);

        Assert.True(RunGlobal("https://example.com/a?utm_source=share", legacy).HasTrackingRemoved);
    }

    [Fact]
    public void Global_params_add_to_a_matching_sites_own_list()
    {
        // A site whose own list lost utm_* still gets it cleaned by the global rule.
        var youtube = DefaultSites.Create().Single(s => s.Name == "youtube") with { StripParams = ["si"] };

        var result = RunGlobal("https://www.youtube.com/watch?v=abc&utm_source=x&si=y", GlobalRule.Default, [youtube]);

        Assert.Equal("https://www.youtube.com/watch?v=abc", result.RewrittenContent);
        Assert.Equal("youtube", Assert.Single(result.Links).Site);
    }

    [Fact]
    public void Global_rule_keeps_the_mirror_for_listed_sites()
    {
        var result = RunGlobal("https://x.com/u/status/123?utm_source=a", GlobalRule.Default);

        Assert.Equal("https://fixupx.com/u/status/123", Assert.Single(result.ReplacementLinks));
    }

    // ---- Sites added in revision 2 ------------------------------------------------------

    [Theory]
    [InlineData("https://www.amazon.com/dp/B012345/ref=sr_1_1?tag=aff-20&th=1&psc=1&qid=99", "https://www.amazon.com/dp/B012345?th=1&psc=1")]
    [InlineData("https://www.amazon.co.uk/Some-Title/dp/B012345/ref=nav?pd_rd_w=a&pf_rd_p=b", "https://www.amazon.co.uk/Some-Title/dp/B012345")]
    [InlineData("https://www.ebay.com/itm/123?_trkparms=a&hash=item1", "https://www.ebay.com/itm/123?hash=item1")]
    [InlineData("https://www.aliexpress.com/item/1.html?algo_pvid=a&scm=b&sk=c&aff_request_id=z", "https://www.aliexpress.com/item/1.html")]
    [InlineData("https://www.etsy.com/listing/1/x?click_key=a&ref=y", "https://www.etsy.com/listing/1/x?ref=y")]
    [InlineData("https://www.linkedin.com/posts/a-1?trk=public&utm_source=s", "https://www.linkedin.com/posts/a-1")]
    [InlineData("https://music.apple.com/us/album/x/1?i=2&itscg=3&itsct=4&utm_source=x", "https://music.apple.com/us/album/x/1?i=2")]
    [InlineData("https://www.netflix.com/title/80?jbv=80&trackId=1&tctx=2&utm_source=x", "https://www.netflix.com/title/80?jbv=80")]
    [InlineData("https://store.steampowered.com/app/10/?snr=1_5&utm_source=x", "https://store.steampowered.com/app/10/")]
    [InlineData("https://www.google.com/search?q=cats&ved=1&ei=2&sxsrf=3&gs_lcp=4&utm_source=x", "https://www.google.com/search?q=cats")]
    [InlineData("https://www.bing.com/search?q=cats&cvid=1&form=QBLH&utm_source=x", "https://www.bing.com/search?q=cats")]
    [InlineData("https://medium.com/@a/post-1?source=x&sk=friendkey&utm_source=x", "https://medium.com/@a/post-1?sk=friendkey")]
    public void New_sites_strip_tracking_and_keep_functional_params(string input, string expected)
    {
        var result = RunGlobal(input, GlobalRule.Default);

        Assert.True(result.HasTrackingRemoved);
        Assert.Equal(expected, result.RewrittenContent);
    }

    // ---- Tidy params: cosmetic, never a reason to act on their own ----

    [Theory]
    [InlineData("https://youtu.be/7duzY5x1Kq8?feature=shared")]
    [InlineData("https://www.youtube.com/watch?v=abc&pp=ygUDY2F0")]
    [InlineData("https://twitter.com/user/status/123?ref_src=twsrc%5Etfw")]
    [InlineData("https://www.google.com/search?q=cats&ved=1&ei=2&sxsrf=3")]
    [InlineData("https://www.bing.com/search?q=cats&cvid=1&form=QBLH")]
    [InlineData("https://www.amazon.com/dp/B012345?qid=99&sr=8-1")]
    public void Cosmetic_params_alone_are_left_untouched(string input)
    {
        var result = RunGlobal(input, GlobalRule.Default);

        Assert.False(result.HasTrackingRemoved);
        Assert.Equal(input, result.RewrittenContent);
    }

    [Theory]
    [InlineData("https://youtu.be/7duzY5x1Kq8?si=abc&feature=shared", "https://youtu.be/7duzY5x1Kq8")]
    [InlineData("https://youtu.be/7duzY5x1Kq8?feature=shared&si=abc&t=30", "https://youtu.be/7duzY5x1Kq8?t=30")]
    [InlineData("https://www.amazon.com/dp/B012345/ref=sr_1_1?qid=99&th=1", "https://www.amazon.com/dp/B012345?th=1")]
    [InlineData("https://youtu.be/abc?feature=shared&utm_source=x", "https://youtu.be/abc")]
    public void Cosmetic_params_are_removed_along_with_an_identifying_one(string input, string expected)
    {
        var result = RunGlobal(input, GlobalRule.Default);

        Assert.True(result.HasTrackingRemoved);
        Assert.Equal(expected, result.RewrittenContent);
    }

    // ---- Enforcement.IncludeCosmetic: cosmetic params are a reason to act ----------------

    private static ProcessedMessage RunStrict(string content, GlobalRule? global) =>
        LinkProcessor.Process(content, DefaultSites.Create(), global, Enforcement.IncludeCosmetic);

    [Theory]
    [InlineData("https://youtu.be/7duzY5x1Kq8?feature=shared", "https://youtu.be/7duzY5x1Kq8")]
    [InlineData("https://youtu.be/abc?utm_source=share&t=5", "https://youtu.be/abc?t=5")]
    [InlineData("https://example.com/a?utm_source=copy-link&id=5", "https://example.com/a?id=5")]
    [InlineData("https://www.google.com/search?q=cats&ved=1&ei=2", "https://www.google.com/search?q=cats")]
    [InlineData("https://www.amazon.com/dp/B012345?th=1&qid=99", "https://www.amazon.com/dp/B012345?th=1")]
    public void Include_cosmetic_acts_on_cosmetic_params_alone(string input, string expected)
    {
        var result = RunStrict(input, GlobalRule.Default);

        Assert.True(result.HasTrackingRemoved);
        Assert.Equal(expected, result.RewrittenContent);
        // The same link is left alone in the default level.
        Assert.False(RunGlobal(input, GlobalRule.Default).HasWork);
    }

    [Theory]
    [InlineData("https://youtu.be/abc?t=30")]
    [InlineData("https://www.google.com/search?q=cats")]
    [InlineData("https://example.com/a?id=5")]
    public void Include_cosmetic_still_keeps_functional_params(string input)
    {
        Assert.False(RunStrict(input, GlobalRule.Default).HasWork);
    }

    [Fact]
    public void Include_cosmetic_without_the_global_rule_only_applies_site_tidy_params()
    {
        Assert.False(RunStrict("https://example.com/a?utm_source=share", null).HasWork);
        Assert.False(RunStrict("https://youtu.be/abc?utm_source=share", GlobalRule.Default with { Enabled = false }).HasWork);
        Assert.Equal("https://youtu.be/abc", RunStrict("https://youtu.be/abc?feature=shared", GlobalRule.Default with { Enabled = false }).RewrittenContent);
    }

    [Fact]
    public void No_two_default_sites_share_a_host_and_names_are_unique()
    {
        var sites = DefaultSites.Create();

        Assert.Equal(sites.Count, sites.Select(s => s.Name).Distinct().Count());
        var hosts = sites.SelectMany(s => s.Hosts).ToList();
        Assert.Equal(hosts.Count, hosts.Distinct().Count());
    }

    // ---- Path-based cleaning ------------------------------------------------------------

    private static readonly SiteRule PathSite = new()
    {
        Name = "shop",
        Hosts = ["shop.example"],
        StripPathPatterns = ["/ref=[^/?#]*"],
    };

    [Theory]
    [InlineData("https://shop.example/dp/B0123/ref=nav_logo", "https://shop.example/dp/B0123")]
    [InlineData("https://shop.example/dp/B0123/ref=nav_logo?th=1", "https://shop.example/dp/B0123?th=1")]
    [InlineData("https://shop.example/dp/B0123/ref=a#frag", "https://shop.example/dp/B0123#frag")]
    [InlineData("https://shop.example/ref=only", "https://shop.example/")]
    public void Path_patterns_remove_tracking_from_the_path(string input, string expected)
    {
        var result = Run(input, [PathSite]);

        Assert.True(result.HasTrackingRemoved);
        Assert.Equal(expected, result.RewrittenContent);
    }

    [Fact]
    public void Path_patterns_never_touch_the_query_or_clean_links()
    {
        Assert.False(Run("https://shop.example/dp/B0123?next=/ref=x", [PathSite]).HasTrackingRemoved);
        Assert.False(Run("https://shop.example/dp/B0123", [PathSite]).HasWork);
    }

    [Theory]
    [InlineData("([unclosed")]       // invalid regex
    [InlineData("(a+)+$")]           // catastrophic backtracking, must hit the timeout and be skipped
    public void Bad_path_patterns_are_ignored_instead_of_breaking_the_message(string pattern)
    {
        var site = PathSite with { StripPathPatterns = [pattern, "/ref=[^/?#]*"] };

        var result = Run("https://shop.example/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!/ref=x", [site]);

        Assert.True(result.HasTrackingRemoved); // the good pattern still ran
        Assert.DoesNotContain("/ref=x", result.RewrittenContent);
    }
}
