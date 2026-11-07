using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LinkCleaner.Bot;
using LinkCleaner.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LinkCleaner.Tests;

/// <summary>Thin tests for the bot host: storage and command wiring. No Discord connection needed.</summary>
public sealed class BotTests : IDisposable
{
    private const ulong Guild = 1234;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkcleaner-tests-" + Guid.NewGuid().ToString("N"));

    private ConfigStore NewStore() => new(Path.Combine(_dir, "test.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void New_guild_gets_default_sites_and_settings()
    {
        var cfg = NewStore().Get(Guild);

        Assert.Equal(DefaultSites.Create().Select(s => s.Name).Order(), cfg.Sites.Select(s => s.Name).Order());
        Assert.Equal(ConfigStore.DefaultInfoLink, cfg.InfoLink);
        Assert.Equal(ConfigStore.DefaultMirrorPrefix, cfg.MirrorPrefix);
        Assert.Null(cfg.ModLogChannelId);
        Assert.True(cfg.AppliesTo(1, null));
    }

    [Fact]
    public void Site_edits_persist_across_restarts()
    {
        var store = NewStore();
        var x = store.Get(Guild).Sites.Single(s => s.Name == "x");
        store.SaveSite(Guild, x with { StripParams = [.. x.StripParams, "custom"], Enabled = false });

        var reloaded = NewStore().Get(Guild).Sites.Single(s => s.Name == "x");
        Assert.Contains("custom", reloaded.StripParams);
        Assert.False(reloaded.Enabled);
        Assert.Equal("fixupx.com", reloaded.MirrorHost);
    }

    [Fact]
    public void Deleted_default_site_stays_deleted()
    {
        var store = NewStore();
        store.DeleteSite(Guild, "twitch");

        Assert.DoesNotContain(NewStore().Get(Guild).Sites, s => s.Name == "twitch");
    }

    [Fact]
    public void Guilds_are_isolated()
    {
        var store = NewStore();
        store.SetSetting(Guild, ConfigStore.InfoLinkKey, "https://example.com/info");

        Assert.Equal("https://example.com/info", store.Get(Guild).InfoLink);
        Assert.Equal(ConfigStore.DefaultInfoLink, store.Get(Guild + 1).InfoLink);
    }

    [Fact]
    public void Settings_can_be_set_emptied_and_reset()
    {
        var store = NewStore();

        store.SetSetting(Guild, ConfigStore.MirrorPrefixKey, "");
        Assert.Equal("", store.Get(Guild).MirrorPrefix);

        store.SetSetting(Guild, ConfigStore.MirrorPrefixKey, null);
        Assert.Equal(ConfigStore.DefaultMirrorPrefix, store.Get(Guild).MirrorPrefix);

        store.SetSetting(Guild, ConfigStore.ModLogKey, "987654321012345678");
        Assert.Equal(987654321012345678UL, store.Get(Guild).ModLogChannelId);
    }

    [Fact]
    public void Channel_rules_exclude_include_and_follow_threads_to_their_parent()
    {
        var store = NewStore();

        store.SetChannelFilter(Guild, 10, ConfigStore.ExcludeKind);
        var cfg = store.Get(Guild);
        Assert.False(cfg.AppliesTo(10, null));
        Assert.False(cfg.AppliesTo(99, 10)); // thread inside an excluded channel
        Assert.True(cfg.AppliesTo(11, null));

        store.SetChannelFilter(Guild, 20, ConfigStore.IncludeKind);
        cfg = store.Get(Guild);
        Assert.True(cfg.AppliesTo(20, null));
        Assert.True(cfg.AppliesTo(99, 20));
        Assert.False(cfg.AppliesTo(11, null)); // not included
        Assert.False(cfg.AppliesTo(10, null)); // excluded still wins

        store.ClearChannelFilters(Guild);
        Assert.True(store.Get(Guild).AppliesTo(10, null));
    }

    [Fact]
    public void Exempt_roles_round_trip()
    {
        var store = NewStore();
        store.AddExemptRole(Guild, 5);
        store.AddExemptRole(Guild, 5);
        Assert.Equal([5UL], store.Get(Guild).ExemptRoles);

        store.RemoveExemptRole(Guild, 5);
        Assert.Empty(store.Get(Guild).ExemptRoles);
    }

    [Fact]
    public void Pinging_the_poster_defaults_on_and_the_setting_persists()
    {
        var store = NewStore();
        Assert.True(store.Get(Guild).PingAuthor);

        store.SetSetting(Guild, ConfigStore.PingAuthorKey, "0");
        Assert.False(NewStore().Get(Guild).PingAuthor);

        store.SetSetting(Guild, ConfigStore.PingAuthorKey, "1");
        Assert.True(NewStore().Get(Guild).PingAuthor);
        Assert.True(store.Get(Guild + 1).PingAuthor); // other guilds unaffected
    }

    [Fact]
    public void Ping_opt_outs_round_trip_and_are_per_guild()
    {
        var store = NewStore();
        store.AddPingOptOut(Guild, 7);
        store.AddPingOptOut(Guild, 7); // idempotent

        Assert.Equal([7UL], store.Get(Guild).PingOptOuts);
        Assert.Empty(store.Get(Guild + 1).PingOptOuts);
        Assert.Equal([7UL], NewStore().Get(Guild).PingOptOuts); // survives restart

        store.RemovePingOptOut(Guild, 7);
        Assert.Empty(store.Get(Guild).PingOptOuts);
    }

    [Fact]
    public void Only_the_poster_is_ever_pinged_and_only_when_allowed()
    {
        var cfg = NewStore().Get(Guild);

        var on = MessageHandler.BuildAllowedMentions(cfg, 42);
        // Discord.Net throws if the Users flag is combined with a UserIds list (this failed live),
        // so only the explicit ID may be allowed, with no blanket type flags at all.
        Assert.False((on.AllowedTypes ?? AllowedMentionTypes.None).HasFlag(AllowedMentionTypes.Users));
        Assert.False((on.AllowedTypes ?? AllowedMentionTypes.None).HasFlag(AllowedMentionTypes.Everyone));
        Assert.False((on.AllowedTypes ?? AllowedMentionTypes.None).HasFlag(AllowedMentionTypes.Roles));
        Assert.Equal([42UL], on.UserIds);                                // never anyone else in the text
        Assert.False(on.MentionRepliedUser);                             // never the replied-to author
        Assert.True(MessageHandler.ShouldPing(cfg, 42));

        var serverOff = cfg with { PingAuthor = false };
        Assert.False(MessageHandler.ShouldPing(serverOff, 42));
        Assert.Equal(AllowedMentionTypes.None, MessageHandler.BuildAllowedMentions(serverOff, 42).AllowedTypes ?? AllowedMentionTypes.None);

        var optedOut = cfg with { PingOptOuts = new HashSet<ulong> { 42 } };
        Assert.False(MessageHandler.ShouldPing(optedOut, 42));
        Assert.True(MessageHandler.ShouldPing(optedOut, 43)); // someone else still gets pinged
        Assert.Equal(AllowedMentionTypes.None, MessageHandler.BuildAllowedMentions(optedOut, 42).AllowedTypes ?? AllowedMentionTypes.None);
    }

    [Fact]
    public void Global_rule_defaults_on_with_utm_and_persists_changes_per_guild()
    {
        var store = NewStore();
        Assert.Equal(GlobalRule.Default.StripParams, store.Get(Guild).GlobalRule.StripParams);
        Assert.True(store.Get(Guild).GlobalRule.Enabled);

        store.SetGlobalParams(Guild, ["utm_*", "fbclid"]);
        store.SetSetting(Guild, ConfigStore.GlobalEnabledKey, "0");

        var reloaded = NewStore().Get(Guild).GlobalRule;
        Assert.Equal(["utm_*", "fbclid"], reloaded.StripParams);
        Assert.False(reloaded.Enabled);
        Assert.True(store.Get(Guild + 1).GlobalRule.Enabled); // other guilds unaffected

        store.SetSetting(Guild, ConfigStore.GlobalEnabledKey, null);
        store.SetGlobalParams(Guild, null);
        Assert.Equal(GlobalRule.Default, NewStore().Get(Guild).GlobalRule with { StripParams = GlobalRule.Default.StripParams });
        Assert.Equal(GlobalRule.Default.StripParams, NewStore().Get(Guild).GlobalRule.StripParams);
    }

    [Fact]
    public void Corrupt_global_params_fall_back_to_the_default()
    {
        var store = NewStore();
        store.SetSetting(Guild, ConfigStore.GlobalParamsKey, "not json");

        Assert.Equal(GlobalRule.Default.StripParams, store.Get(Guild).GlobalRule.StripParams);
    }

    [Fact]
    public void Path_patterns_survive_a_save_and_old_rows_without_them_still_load()
    {
        var store = NewStore();
        var x = store.Get(Guild).Sites.Single(s => s.Name == "x");
        store.SaveSite(Guild, x with { StripPathPatterns = ["/ref=[^/?#]*"] });
        Assert.Equal(["/ref=[^/?#]*"], NewStore().Get(Guild).Sites.Single(s => s.Name == "x").StripPathPatterns);

        var legacy = System.Text.Json.JsonSerializer.Deserialize<SiteRule>("""{"Name":"old","Hosts":["old.example"],"StripParams":["a"]}""")!;
        Assert.Empty(legacy.StripPathPatterns);
    }

    // ---- Default revisions --------------------------------------------------------------

    private void Raw(string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "test.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void SaveLegacy(string name, SiteRule rule) => Raw(
        $"UPDATE sites SET json = '{System.Text.Json.JsonSerializer.Serialize(rule with { DefaultRevision = 0, Customised = false }).Replace("'", "''")}' " +
        $"WHERE guild_id = {Guild} AND name = '{name}'");

    /// <summary>Rewinds a guild to how revision 1 stored it: only the original sites, no revision markers.</summary>
    private ConfigStore MakeLegacyGuild(Action<ConfigStore>? tweak = null)
    {
        var store = NewStore();
        _ = store.Get(Guild);
        Raw($"DELETE FROM sites WHERE guild_id = {Guild} AND name NOT IN ('youtube','instagram','x','facebook','reddit','tiktok','spotify','twitch')");
        foreach (var original in DefaultSites.Create().Where(s => DefaultSites.OriginalNames.Contains(s.Name)))
            SaveLegacy(original.Name, original);
        SaveLegacy("youtube", DefaultSites.Create().Single(s => s.Name == "youtube") with
            { StripParams = ["si", "feature", "pp", "utm_*", "fbclid", "gclid"] }); // revision 1 had no "is"
        Raw($"DELETE FROM settings WHERE guild_id = {Guild} AND key = '{ConfigStore.DefaultsRevisionKey}'");
        tweak?.Invoke(store);
        return NewStore();
    }

    [Fact]
    public void Legacy_guild_gets_new_sites_and_unedited_sites_are_refreshed()
    {
        var cfg = MakeLegacyGuild().Get(Guild);

        Assert.All(DefaultSites.Create(), d => Assert.Contains(cfg.Sites, s => s.Name == d.Name));
        Assert.Contains("is", cfg.Sites.Single(s => s.Name == "youtube").StripParams);
        Assert.Empty(cfg.OutdatedBuiltIns);
    }

    [Fact]
    public void Legacy_guild_keeps_deleted_originals_deleted_and_edited_sites_untouched()
    {
        var edited = DefaultSites.Create().Single(s => s.Name == "x") with { StripParams = ["only_this"] };
        var cfg = MakeLegacyGuild(_ =>
        {
            Raw($"DELETE FROM sites WHERE guild_id = {Guild} AND name = 'twitch'"); // deleted before revisions existed
            SaveLegacy("x", edited);
        }).Get(Guild);

        Assert.DoesNotContain(cfg.Sites, s => s.Name == "twitch");
        var x = cfg.Sites.Single(s => s.Name == "x");
        Assert.Equal(["only_this"], x.StripParams);
        Assert.True(x.Customised);
        Assert.Equal(["x"], cfg.OutdatedBuiltIns);
    }

    [Fact]
    public void Refreshing_a_site_keeps_its_enabled_flag()
    {
        var cfg = MakeLegacyGuild(_ =>
            SaveLegacy("youtube", DefaultSites.Create().Single(s => s.Name == "youtube") with
                { Enabled = false, StripParams = ["si", "feature", "pp", "utm_*", "fbclid", "gclid"] })).Get(Guild);

        var youtube = cfg.Sites.Single(s => s.Name == "youtube");
        Assert.False(youtube.Enabled);
        Assert.Contains("is", youtube.StripParams);
    }

    [Fact]
    public void A_deleted_builtin_is_not_restored_by_a_later_revision()
    {
        var store = NewStore();
        store.DeleteSite(Guild, "amazon");
        Raw($"UPDATE settings SET value = '1' WHERE guild_id = {Guild} AND key = '{ConfigStore.DefaultsRevisionKey}'");

        Assert.DoesNotContain(NewStore().Get(Guild).Sites, s => s.Name == "amazon");

        // ...but an admin adding a site of that name again is honoured.
        store.SaveSite(Guild, new SiteRule { Name = "amazon", Hosts = ["amazon.example"] });
        Assert.Contains(NewStore().Get(Guild).Sites, s => s.Name == "amazon");
    }

    [Fact]
    public void Edits_mark_a_site_customised_but_toggling_and_resetting_do_not()
    {
        var store = NewStore();
        var google = store.Get(Guild).Sites.Single(s => s.Name == "google");
        Assert.False(google.Customised);

        store.SaveSite(Guild, google with { Enabled = false }, customises: false);
        Assert.False(store.Get(Guild).Sites.Single(s => s.Name == "google").Customised);

        store.SaveSite(Guild, google with { StripParams = [.. google.StripParams, "extra"] });
        Assert.True(store.Get(Guild).Sites.Single(s => s.Name == "google").Customised);

        store.SaveSite(Guild, DefaultSites.Create().Single(s => s.Name == "google"), customises: false);
        var reset = store.Get(Guild).Sites.Single(s => s.Name == "google");
        Assert.False(reset.Customised);
        Assert.Equal(DefaultSites.Revision, reset.DefaultRevision);
    }

    [Fact]
    public async Task Slash_commands_build_into_a_valid_definition()
    {
        using var client = new DiscordSocketClient();
        var interactions = new InteractionService(client);
        var services = new ServiceCollection().AddSingleton(NewStore()).BuildServiceProvider();

        await interactions.AddModulesAsync(typeof(ConfigStore).Assembly, services);

        var commands = interactions.SlashCommands.Select(c => c.ToString()).ToList();
        Assert.NotEmpty(commands);
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "site" && c.Name == "enable");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "linkcleaner" && c.Name == "status");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "linkcleaner" && c.Name == "pings");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "global" && c.Name == "add");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "global" && c.Name == "disable");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "path" && c.Name == "add");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "linkcleaner-pings" && c.Name == "off");
        Assert.Contains(interactions.SlashCommands, c => c.Module.SlashGroupName == "linkcleaner-pings" && c.Name == "on");

        // Members must be able to use the opt-out: it must not inherit the admin-only default permission.
        var optOut = interactions.SlashCommands.First(c => c.Module.SlashGroupName == "linkcleaner-pings");
        Assert.Null(optOut.DefaultMemberPermissions);
        Assert.Null(optOut.Module.DefaultMemberPermissions);
    }
}
