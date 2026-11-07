using LinkCleaner.Core;

namespace LinkCleaner.Tests;

public class LinkProcessorTests
{
    private static ProcessedMessage Run(string content, IReadOnlyList<SiteRule>? sites = null) =>
        LinkProcessor.Process(content, sites ?? DefaultSites.Create());

    // ---- Tracking removal: the link is rewritten ----------------------------------------

    [Theory]
    // YouTube: share id removed, functional params kept
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=abc&si=xyz", "https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtube.com/watch?si=x&v=abc", "https://youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc?t=30&si=zzz", "https://youtu.be/abc?t=30")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL1&feature=share", "https://www.youtube.com/watch?v=abc&list=PL1")]
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
    [InlineData("https://www.twitch.tv/videos/123?sr=a&t=1h2m", "https://www.twitch.tv/videos/123?t=1h2m")]
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
    [InlineData("https://twitter.com/user/status/123?ref_src=twsrc%5Etfw", "https://fixupx.com/user/status/123")]
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
    [InlineData("https://example.com/?utm_source=x&si=1")]       // unknown site: allowlist only
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

    // ---- "They said" text ---------------------------------------------------------------

    [Theory]
    [InlineData("https://youtu.be/abc?si=x", false)]
    [InlineData("  https://youtu.be/abc?si=x  ", false)]
    [InlineData("||https://youtu.be/abc?si=x||", false)]
    [InlineData("<https://youtu.be/abc?si=x>", false)]
    [InlineData("check this out https://youtu.be/abc?si=x", true)]
    [InlineData("https://youtu.be/abc?si=x lol", true)]
    [InlineData("🔥 https://youtu.be/abc?si=x", true)]
    public void Detects_whether_user_wrote_text(string input, bool expected)
    {
        Assert.Equal(expected, Run(input).HasUserText);
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
}
