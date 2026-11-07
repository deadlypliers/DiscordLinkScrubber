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
    }
}
