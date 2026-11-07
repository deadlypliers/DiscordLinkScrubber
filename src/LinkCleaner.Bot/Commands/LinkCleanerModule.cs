using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LinkCleaner.Core;

namespace LinkCleaner.Bot.Commands;

/// <summary>
/// /linkcleaner … — all configuration lives here. Visible only to members with Manage Server by
/// default; server admins can re-scope it under Settings → Integrations.
/// </summary>
[Group("linkcleaner", "Configure the link cleaner.")]
[CommandContextType(InteractionContextType.Guild)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class LinkCleanerModule(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("status", "Show the current configuration.")]
    public async Task Status()
    {
        var cfg = store.Get(Context.Guild.Id);
        var sb = new StringBuilder();
        sb.AppendLine("**Sites**");
        foreach (var s in cfg.Sites)
        {
            var mirror = string.IsNullOrWhiteSpace(s.MirrorHost) ? "" : $" → mirror `{s.MirrorHost}`";
            sb.AppendLine($"- {(s.Enabled ? "✅" : "⛔")} `{s.Name}`: {s.StripParams.Count} params{mirror}");
        }
        sb.AppendLine();
        sb.AppendLine($"**Info link:** <{cfg.InfoLink}>");
        sb.AppendLine($"**Mirror prefix:** {(cfg.MirrorPrefix.Length > 0 ? cfg.MirrorPrefix : "_none_")}");
        sb.AppendLine($"**Mod log:** {(cfg.ModLogChannelId is { } id ? $"<#{id}>" : "_off_")}");
        sb.AppendLine($"**Only in channels:** {(cfg.IncludedChannels.Count > 0 ? string.Join(", ", cfg.IncludedChannels.Select(c => $"<#{c}>")) : "_all_")}");
        sb.AppendLine($"**Ignored channels:** {(cfg.ExcludedChannels.Count > 0 ? string.Join(", ", cfg.ExcludedChannels.Select(c => $"<#{c}>")) : "_none_")}");
        sb.AppendLine($"**Exempt roles:** {(cfg.ExemptRoles.Count > 0 ? string.Join(", ", cfg.ExemptRoles.Select(r => $"<@&{r}>")) : "_none_")}");
        await this.Say(sb.ToString());
    }

    [SlashCommand("test", "Show what the bot would do with a message, without posting anything.")]
    public async Task Test([Summary(description: "Paste a message or link to test")] string text)
    {
        var result = LinkProcessor.Process(text, store.Get(Context.Guild.Id).Sites);
        if (!result.HasWork)
        {
            await this.Say("Nothing to do: no tracking params found and no mirror applies.");
            return;
        }

        var sb = new StringBuilder();
        if (result.HasTrackingRemoved)
            sb.AppendLine($"**Repost with:**\n{result.RewrittenContent}\n");
        if (result.MirrorReplyLinks.Count > 0)
            sb.AppendLine($"**Mirror reply:**\n{string.Join('\n', result.MirrorReplyLinks.Select(l => $"<{l}>"))}");
        await this.Say(sb.ToString());
    }

    [SlashCommand("infolink", "Set the 'Why?' link shown in the explanation.")]
    public async Task InfoLink([Summary(description: "An http(s) URL, e.g. a message link in your server")] string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await this.Say("That isn't an http(s) URL.");
            return;
        }
        store.SetSetting(Context.Guild.Id, ConfigStore.InfoLinkKey, uri.AbsoluteUri);
        await this.Say($"Info link set to <{uri.AbsoluteUri}>.");
    }

    [SlashCommand("prefix", "Set the emoji that starts mirror replies. Leave empty for none.")]
    public async Task Prefix([Summary(description: "Emoji or short text; omit to remove")] string? emoji = null)
    {
        emoji = emoji?.Trim() ?? "";
        if (emoji.Length > 16)
        {
            await this.Say("Keep the prefix short (16 characters or fewer).");
            return;
        }
        store.SetSetting(Context.Guild.Id, ConfigStore.MirrorPrefixKey, emoji);
        await this.Say(emoji.Length > 0 ? $"Mirror prefix set to {emoji}." : "Mirror replies will have no prefix.");
    }

    [SlashCommand("modlog", "Log every action to a channel. Leave empty to turn off.")]
    public async Task ModLog([Summary(description: "Channel to log to; omit to turn off")] ITextChannel? channel = null)
    {
        store.SetSetting(Context.Guild.Id, ConfigStore.ModLogKey, channel?.Id.ToString());
        await this.Say(channel is null ? "Mod log turned off." : $"Actions will be logged to {channel.Mention}.");
    }

    // ---- /linkcleaner site … ------------------------------------------------------------

    [Group("site", "Manage the sites the bot cleans.")]
    public sealed class SiteGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("list", "List all sites.")]
        public Task List() =>
            this.Say(string.Join("\n\n", store.Get(Context.Guild.Id).Sites.Select(CommandHelpers.Describe)) is { Length: > 0 } text
                ? (text.Length > 1900 ? text[..1900] + "…" : text)
                : "No sites configured.");

        [SlashCommand("show", "Show one site's rules.")]
        public async Task Show([Autocomplete(typeof(SiteAutocomplete))] string site)
        {
            var rule = Find(site);
            await this.Say(rule is null ? NotFound(site) : CommandHelpers.Describe(rule));
        }

        [SlashCommand("enable", "Turn a site on.")]
        public Task Enable([Autocomplete(typeof(SiteAutocomplete))] string site) => SetEnabled(site, true);

        [SlashCommand("disable", "Turn a site off.")]
        public Task Disable([Autocomplete(typeof(SiteAutocomplete))] string site) => SetEnabled(site, false);

        [SlashCommand("add", "Add a custom site.")]
        public async Task Add(
            [Summary(description: "Short name, e.g. 'shop' (a-z, 0-9, - _)")] string name,
            [Summary(description: "A hostname, e.g. shop.example.com")] string host)
        {
            name = name.Trim().ToLowerInvariant();
            if (!CommandHelpers.IsValidSiteName(name))
            {
                await this.Say("Site names are 1–32 characters of a-z, 0-9, `-` and `_`.");
                return;
            }
            if (Find(name) is not null)
            {
                await this.Say($"A site named `{name}` already exists.");
                return;
            }
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized))
            {
                await this.Say("That doesn't look like a hostname.");
                return;
            }
            store.SaveSite(Context.Guild.Id, new SiteRule { Name = name, Hosts = [normalized] });
            await this.Say($"Added `{name}` for `{normalized}`. Now add params with `/linkcleaner param add`.");
        }

        [SlashCommand("remove", "Delete a site.")]
        public async Task Remove([Autocomplete(typeof(SiteAutocomplete))] string site)
        {
            if (Find(site) is null)
            {
                await this.Say(NotFound(site));
                return;
            }
            store.DeleteSite(Context.Guild.Id, site);
            await this.Say($"Removed `{site}`.");
        }

        [SlashCommand("reset", "Restore a built-in site to its defaults.")]
        public async Task Reset([Autocomplete(typeof(SiteAutocomplete))] string site)
        {
            var original = DefaultSites.Create().FirstOrDefault(s => s.Name == site);
            if (original is null)
            {
                await this.Say($"`{site}` isn't a built-in site, so there are no defaults to restore.");
                return;
            }
            store.SaveSite(Context.Guild.Id, original);
            await this.Say($"Restored `{site}` to its defaults.");
        }

        private async Task SetEnabled(string site, bool enabled)
        {
            var rule = Find(site);
            if (rule is null)
            {
                await this.Say(NotFound(site));
                return;
            }
            store.SaveSite(Context.Guild.Id, rule with { Enabled = enabled });
            await this.Say($"`{site}` is now {(enabled ? "enabled" : "disabled")}.");
        }

        private SiteRule? Find(string site) => store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
        private static string NotFound(string site) => $"No site named `{site}`.";
    }

    // ---- /linkcleaner param … -----------------------------------------------------------

    [Group("param", "Manage the query parameters stripped from a site's links.")]
    public sealed class ParamGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("add", "Strip a parameter from this site's links.")]
        public async Task Add(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Parameter name; end with * for a prefix, e.g. utm_*")] string name)
        {
            name = name.Trim();
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }
            if (!CommandHelpers.IsValidParamName(name)) { await this.Say("That isn't a valid parameter name."); return; }
            if (rule.StripParams.Contains(name, StringComparer.OrdinalIgnoreCase)) { await this.Say($"`{name}` is already stripped on `{site}`."); return; }

            store.SaveSite(Context.Guild.Id, rule with { StripParams = [.. rule.StripParams, name] });
            await this.Say($"`{site}` links will now have `{name}` removed.");
        }

        [SlashCommand("remove", "Stop stripping a parameter from this site's links.")]
        public async Task Remove(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Parameter name exactly as listed")] string name)
        {
            name = name.Trim();
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }

            var remaining = rule.StripParams.Where(p => !p.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == rule.StripParams.Count) { await this.Say($"`{name}` isn't in `{site}`'s list."); return; }

            store.SaveSite(Context.Guild.Id, rule with { StripParams = remaining });
            await this.Say($"`{site}` links will keep `{name}` from now on.");
        }
    }

    // ---- /linkcleaner host … ------------------------------------------------------------

    [Group("host", "Manage the hostnames a site rule covers (aliases).")]
    public sealed class HostGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("add", "Treat another hostname as part of this site.")]
        public async Task Add([Autocomplete(typeof(SiteAutocomplete))] string site, string host)
        {
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say("That doesn't look like a hostname."); return; }
            if (rule.Hosts.Contains(normalized)) { await this.Say($"`{normalized}` is already covered."); return; }

            store.SaveSite(Context.Guild.Id, rule with { Hosts = [.. rule.Hosts, normalized] });
            await this.Say($"`{site}` now also covers `{normalized}` and its subdomains.");
        }

        [SlashCommand("remove", "Stop covering a hostname.")]
        public async Task Remove([Autocomplete(typeof(SiteAutocomplete))] string site, string host)
        {
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say("That doesn't look like a hostname."); return; }

            var remaining = rule.Hosts.Where(h => h != normalized).ToList();
            if (remaining.Count == rule.Hosts.Count) { await this.Say($"`{normalized}` isn't in `{site}`'s list."); return; }

            store.SaveSite(Context.Guild.Id, rule with { Hosts = remaining });
            await this.Say($"`{site}` no longer covers `{normalized}`.");
        }
    }

    // ---- /linkcleaner mirror … ----------------------------------------------------------

    [Group("mirror", "Choose the embed-friendly mirror used for login-walled sites.")]
    public sealed class MirrorGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("set", "Use a mirror host for this site.")]
        public async Task Set(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Mirror hostname, e.g. fixupx.com")] string host,
            [Summary(description: "Optional regex for which URL paths get a mirror; omit for every non-root path")] string? pathPattern = null)
        {
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say("That doesn't look like a hostname."); return; }
            if (pathPattern is not null && !CommandHelpers.IsValidRegex(pathPattern)) { await this.Say("That path pattern isn't a valid regex."); return; }

            store.SaveSite(Context.Guild.Id, rule with { MirrorHost = normalized, MirrorPathPattern = pathPattern ?? rule.MirrorPathPattern });
            await this.Say($"`{site}` links will be mirrored via `{normalized}`.");
        }

        [SlashCommand("clear", "Stop mirroring this site.")]
        public async Task Clear([Autocomplete(typeof(SiteAutocomplete))] string site)
        {
            var rule = store.Get(Context.Guild.Id).Sites.FirstOrDefault(s => s.Name == site);
            if (rule is null) { await this.Say($"No site named `{site}`."); return; }

            store.SaveSite(Context.Guild.Id, rule with { MirrorHost = null });
            await this.Say($"`{site}` will no longer be mirrored.");
        }
    }

    // ---- /linkcleaner channel … ---------------------------------------------------------

    [Group("channel", "Limit where the bot works.")]
    public sealed class ChannelGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("include", "Work only in the channels you include (replaces 'all channels').")]
        public async Task Include(IGuildChannel channel)
        {
            store.SetChannelFilter(Context.Guild.Id, channel.Id, ConfigStore.IncludeKind);
            await this.Say($"The bot will work only in included channels; {MentionOf(channel)} is now one of them.");
        }

        [SlashCommand("exclude", "Never work in this channel.")]
        public async Task Exclude(IGuildChannel channel)
        {
            store.SetChannelFilter(Context.Guild.Id, channel.Id, ConfigStore.ExcludeKind);
            await this.Say($"The bot will ignore {MentionOf(channel)}.");
        }

        [SlashCommand("reset", "Remove one channel from the include/exclude lists.")]
        public async Task Reset(IGuildChannel channel)
        {
            store.RemoveChannelFilter(Context.Guild.Id, channel.Id);
            await this.Say($"{MentionOf(channel)} is no longer listed.");
        }

        [SlashCommand("clear", "Back to the default: work in every channel.")]
        public async Task Clear()
        {
            store.ClearChannelFilters(Context.Guild.Id);
            await this.Say("Cleared all channel rules. The bot works in every channel it can access.");
        }

        private static string MentionOf(IGuildChannel channel) => $"<#{channel.Id}>";
    }

    // ---- /linkcleaner role … ------------------------------------------------------------

    [Group("role", "Exempt roles from link cleaning.")]
    public sealed class RoleGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("exempt", "Never touch messages from members with this role.")]
        public async Task Exempt(IRole role)
        {
            store.AddExemptRole(Context.Guild.Id, role.Id);
            await this.Say($"Members with <@&{role.Id}> are now exempt.");
        }

        [SlashCommand("unexempt", "Stop exempting this role.")]
        public async Task Unexempt(IRole role)
        {
            store.RemoveExemptRole(Context.Guild.Id, role.Id);
            await this.Say($"<@&{role.Id}> is no longer exempt.");
        }
    }
}
