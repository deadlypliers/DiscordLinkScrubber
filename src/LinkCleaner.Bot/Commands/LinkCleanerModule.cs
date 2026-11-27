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
public sealed class LinkCleanerModule(ConfigStore store, AttachmentDownloader downloader) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("export", "Download this server's rules and settings as a file you can import elsewhere.")]
    public async Task Export()
    {
        var json = ConfigTransfer.Export(store.Get(Context.Guild.Id));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await Context.Interaction.RespondWithFileAsync(stream, "linkcleaner-config.json",
            "Sites, the server-wide rule, info link, mirror prefix and the ping setting. Channel rules, exempt roles, the mod log and members' ping choices stay behind.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("import", "Load rules and settings from an exported file.")]
    public async Task Import(
        [Summary(description: "The linkcleaner-config.json file")] IAttachment file,
        [Summary(description: "True to make this server's sites exactly match the file (default: merge by name)")] bool replace = false,
        [Summary(description: "Required with replace: confirms you want to discard sites not in the file")] bool confirm = false)
    {
        if (replace && !confirm)
        {
            await this.Say("`replace` removes every site that isn't in the file. Run it again with `confirm:true` if that's what you want, or leave `replace` off to merge.");
            return;
        }

        await DeferAsync(ephemeral: true);
        string text;
        try
        {
            text = await downloader.DownloadTextAsync(file.Url, ConfigTransfer.MaxBytes);
        }
        catch (InvalidOperationException ex)
        {
            await FollowupAsync(ex.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (!ConfigTransfer.TryParse(text, out var bundle, out var error))
        {
            await FollowupAsync($"Nothing was changed. {error}", ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        store.ImportBundle(Context.Guild.Id, bundle, replace);
        await FollowupAsync(
            $"Imported {bundle.Sites.Count} site(s) ({(replace ? "replacing your list" : "merged by name")}) and the file's settings. Use `/linkcleaner status` to review.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

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
        var outdated = cfg.OutdatedBuiltIns;
        if (outdated.Count > 0)
            sb.AppendLine($"\n⚠️ Newer built-in rules exist for {string.Join(", ", outdated.Select(n => $"`{n}`"))}, but you've edited " +
                          (outdated.Count == 1 ? "that site" : "those sites") + ". `/linkcleaner site reset` loads the new rules (your edits are lost).");
        sb.AppendLine();
        sb.AppendLine($"**Every link, any site:** {CommandHelpers.DescribeGlobal(cfg.GlobalRule)}");
        sb.AppendLine($"**Info link:** <{cfg.InfoLink}>");
        sb.AppendLine($"**Mirror prefix:** {(cfg.MirrorPrefix.Length > 0 ? cfg.MirrorPrefix : "_none_")}");
        sb.AppendLine($"**Mod log:** {(cfg.ModLogChannelId is { } id ? $"<#{id}>" : "_off_")}");
        sb.AppendLine($"**Enforcement:** {(cfg.Enforcement == Enforcement.IncludeCosmetic ? "tracking and cosmetic params" : "tracking only (cosmetic params are tidied alongside)")}");
        sb.AppendLine($"**Ping poster:** {(cfg.PingAuthor ? "on" : "off")} ({cfg.PingOptOuts.Count} opted out)");
        sb.AppendLine($"**Only in channels:** {(cfg.IncludedChannels.Count > 0 ? string.Join(", ", cfg.IncludedChannels.Select(c => $"<#{c}>")) : "_all_")}");
        sb.AppendLine($"**Ignored channels:** {(cfg.ExcludedChannels.Count > 0 ? string.Join(", ", cfg.ExcludedChannels.Select(c => $"<#{c}>")) : "_none_")}");
        sb.AppendLine($"**Exempt roles:** {(cfg.ExemptRoles.Count > 0 ? string.Join(", ", cfg.ExemptRoles.Select(r => $"<@&{r}>")) : "_none_")}");
        await this.Say(sb.ToString());
    }

    [SlashCommand("test", "Show what the bot would do with a message, without posting anything.")]
    public async Task Test([Summary(description: "Paste a message or link to test")] string text)
    {
        var cfg = store.Get(Context.Guild.Id);
        var result = LinkProcessor.Process(text, cfg.Sites, cfg.GlobalRule, cfg.Enforcement);
        if (!result.HasWork)
        {
            await this.Say("Nothing to do: no tracking params found and no mirror applies.");
            return;
        }

        var sb = new StringBuilder();
        if (result.HasTrackingRemoved)
        {
            var (repost, _) = MessageHandler.ComposeRepost(Context.User.Mention, result, cfg.InfoLink, ping: false, hasFiles: false);
            sb.AppendLine($"**Repost would look like:**\n{string.Join('\n', repost.Split('\n').Select(l => "> " + l))}\n");
        }
        if (result.MirrorReplyLinks.Count > 0)
            sb.AppendLine($"**Mirror reply:**\n{string.Join('\n', result.MirrorReplyLinks.Select(l => $"<{l}>"))}");
        await this.Say(sb.ToString());
    }

    [SlashCommand("infolink", "Set the 'Why did this happen?' link shown on reposts.")]
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
        if (emoji.Length > Limits.MirrorPrefixLength)
        {
            await this.Say($"Keep the prefix short ({Limits.MirrorPrefixLength} characters or fewer).");
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

    [SlashCommand("enforcement", "Choose whether harmless extras (like ?feature=shared) are cleaned on their own.")]
    public async Task Enforce([Summary(description: "True to also clean cosmetic params on their own, false for tracking IDs only")] bool cosmetic)
    {
        store.SetSetting(Context.Guild.Id, ConfigStore.EnforcementKey, cosmetic ? "cosmetic" : null);
        await this.Say(cosmetic
            ? "Links with only cosmetic extras (e.g. `?feature=shared`) will now be cleaned and reposted too."
            : "Only links with tracking IDs are acted on. Cosmetic extras are removed only when a tracking ID is present.");
    }

    [SlashCommand("pings", "Choose whether the original poster is pinged when their link is reposted.")]
    public async Task Pings([Summary(description: "True to ping the poster, false for a mention without a notification")] bool enabled)
    {
        store.SetSetting(Context.Guild.Id, ConfigStore.PingAuthorKey, enabled ? "1" : "0");
        await this.Say(enabled
            ? "The original poster will be pinged when their link is reposted. Members can opt out with `/linkcleaner-pings off`."
            : "The original poster will still be named, but not pinged.");
    }

    // ---- /linkcleaner site … ------------------------------------------------------------

    [Group("site", "Manage the sites the bot cleans.")]
    public sealed class SiteGroup(ConfigStore store) : SiteCommandModule(store)
    {
        [SlashCommand("list", "List all sites.")]
        public Task List() =>
            this.Say(string.Join("\n\n", Store.Get(Context.Guild.Id).Sites.Select(CommandHelpers.Describe)) is { Length: > 0 } text
                ? text
                : "No sites configured.");

        [SlashCommand("show", "Show one site's rules.")]
        public Task Show([Autocomplete(typeof(SiteAutocomplete))] string site) =>
            WithSite(site, rule => this.Say(CommandHelpers.Describe(rule)));

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
                await this.Say(CommandHelpers.InvalidHost);
                return;
            }
            Save(new SiteRule { Name = name, Hosts = [normalized] });
            await this.Say($"Added `{name}` for `{normalized}`. Now add params with `/linkcleaner param add`.");
        }

        [SlashCommand("remove", "Delete a site.")]
        public Task Remove([Autocomplete(typeof(SiteAutocomplete))] string site) => WithSite(site, async rule =>
        {
            Store.DeleteSite(Context.Guild.Id, rule.Name);
            await this.Say($"Removed `{site}`.");
        });

        [SlashCommand("reset", "Restore a built-in site to its defaults.")]
        public async Task Reset([Autocomplete(typeof(SiteAutocomplete))] string site)
        {
            var original = DefaultSites.Create().FirstOrDefault(s => s.Name == site);
            if (original is null)
            {
                await this.Say($"`{site}` isn't a built-in site, so there are no defaults to restore.");
                return;
            }
            Save(original, customises: false);
            await this.Say($"Restored `{site}` to its defaults. It will now receive future built-in updates.");
        }

        private Task SetEnabled(string site, bool enabled) => WithSite(site, async rule =>
        {
            Save(rule with { Enabled = enabled }, customises: false);
            await this.Say($"`{site}` is now {(enabled ? "enabled" : "disabled")}.");
        });
    }

    // ---- /linkcleaner param … -----------------------------------------------------------

    [Group("param", "Manage the query parameters stripped from a site's links.")]
    public sealed class ParamGroup(ConfigStore store) : SiteCommandModule(store)
    {
        [SlashCommand("add", "Strip a parameter from this site's links.")]
        public Task Add(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Parameter name; end with * for a prefix, e.g. utm_*")] string name,
            [Summary(description: "Cosmetic: only removed from links that also carry a stripped param")] bool tidy = false) =>
            WithSite(site, async rule =>
            {
                name = name.Trim();
                if (!CommandHelpers.IsValidParamName(name)) { await this.Say(CommandHelpers.InvalidParam); return; }
                if (rule.StripParams.Concat(rule.TidyParams).Contains(name, StringComparer.OrdinalIgnoreCase))
                { await this.Say($"`{name}` is already on `{site}`'s list."); return; }
                if ((tidy ? rule.TidyParams : rule.StripParams).Count >= Limits.EntriesPerList)
                { await this.Say(CommandHelpers.ListFull(Limits.EntriesPerList)); return; }

                if (tidy)
                {
                    Save(rule with { TidyParams = [.. rule.TidyParams, name] });
                    await this.Say($"`{name}` will be removed from `{site}` links, but only when they also carry a stripped param.");
                }
                else
                {
                    Save(rule with { StripParams = [.. rule.StripParams, name] });
                    await this.Say($"`{site}` links will now have `{name}` removed.");
                }
            });

        [SlashCommand("remove", "Stop stripping a parameter from this site's links.")]
        public Task Remove(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Parameter name exactly as listed")] string name) =>
            WithSite(site, async rule =>
            {
                name = name.Trim();
                var strip = CommandHelpers.Without(rule.StripParams, name, StringComparer.OrdinalIgnoreCase);
                var tidy = CommandHelpers.Without(rule.TidyParams, name, StringComparer.OrdinalIgnoreCase);
                if (strip is null && tidy is null) { await this.Say($"`{name}` isn't in `{site}`'s list."); return; }

                Save(rule with { StripParams = strip ?? rule.StripParams, TidyParams = tidy ?? rule.TidyParams });
                await this.Say($"`{site}` links will keep `{name}` from now on.");
            });
    }

    // ---- /linkcleaner global … ----------------------------------------------------------

    [Group("global", "Parameters removed from links on ANY site, listed or not (click IDs, tracking utm_*).")]
    public sealed class GlobalGroup(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("list", "Show the server-wide strip list.")]
        public Task List() => this.Say(CommandHelpers.DescribeGlobal(store.Get(Context.Guild.Id).GlobalRule));

        [SlashCommand("add", "Strip a parameter from every link (e.g. fbclid, gclid, msclkid, or a prefix like utm_*).")]
        public async Task Add([Summary(description: "Parameter name; end with * for a prefix, e.g. utm_*")] string name)
        {
            name = name.Trim();
            var global = store.Get(Context.Guild.Id).GlobalRule;
            if (!CommandHelpers.IsValidParamName(name)) { await this.Say(CommandHelpers.InvalidParam); return; }
            if (global.StripParams.Contains(name, StringComparer.OrdinalIgnoreCase)) { await this.Say($"`{name}` is already on the list."); return; }
            if (global.StripParams.Count >= Limits.EntriesPerList) { await this.Say(CommandHelpers.ListFull(Limits.EntriesPerList)); return; }

            store.SetGlobalParams(Context.Guild.Id, [.. global.StripParams, name]);
            await this.Say($"`{name}` will now be removed from every link." + (global.Enabled ? "" : " (The global rule is currently disabled: `/linkcleaner global enable`.)"));
        }

        [SlashCommand("remove", "Stop stripping a parameter from every link.")]
        public async Task Remove([Summary(description: "Parameter name exactly as listed")] string name)
        {
            name = name.Trim();
            var remaining = CommandHelpers.Without(store.Get(Context.Guild.Id).GlobalRule.StripParams, name, StringComparer.OrdinalIgnoreCase);
            if (remaining is null) { await this.Say($"`{name}` isn't on the list."); return; }

            store.SetGlobalParams(Context.Guild.Id, remaining);
            await this.Say($"`{name}` is no longer removed from every link. Site rules still apply.");
        }

        [SlashCommand("generic-add", "Treat a utm_* value as generic (harmless), e.g. copy-link.")]
        public async Task GenericAdd([Summary(description: "The value, e.g. copy-link")] string value)
        {
            value = value.Trim();
            var global = store.Get(Context.Guild.Id).GlobalRule;
            if (!CommandHelpers.IsValidGenericValue(value)) { await this.Say("That isn't a valid value (no spaces, 64 characters max)."); return; }
            if (global.EffectiveGenericValues.Contains(value, StringComparer.OrdinalIgnoreCase)) { await this.Say($"`{value}` is already generic."); return; }
            if (global.EffectiveGenericValues.Count >= Limits.EntriesPerList) { await this.Say(CommandHelpers.ListFull(Limits.EntriesPerList)); return; }

            store.SetGenericValues(Context.Guild.Id, [.. global.EffectiveGenericValues, value]);
            await this.Say($"`utm_*` params with the value `{value}` will now be left alone.");
        }

        [SlashCommand("generic-remove", "Stop treating a utm_* value as generic.")]
        public async Task GenericRemove([Summary(description: "The value exactly as listed")] string value)
        {
            value = value.Trim();
            var remaining = CommandHelpers.Without(store.Get(Context.Guild.Id).GlobalRule.EffectiveGenericValues, value, StringComparer.OrdinalIgnoreCase);
            if (remaining is null) { await this.Say($"`{value}` isn't on the list."); return; }

            store.SetGenericValues(Context.Guild.Id, remaining);
            await this.Say($"`utm_*` params with the value `{value}` now count as tracking.");
        }

        [SlashCommand("enable", "Turn the server-wide rule on.")]
        public async Task Enable()
        {
            store.SetSetting(Context.Guild.Id, ConfigStore.GlobalEnabledKey, "1");
            await this.Say("The server-wide rule is on.");
        }

        [SlashCommand("disable", "Turn the server-wide rule off (listed sites keep their own params).")]
        public async Task Disable()
        {
            store.SetSetting(Context.Guild.Id, ConfigStore.GlobalEnabledKey, "0");
            await this.Say("The server-wide rule is off. Only the per-site rules apply now.");
        }

        [SlashCommand("reset", "Back to the default: on, click IDs and tracking utm_* removed.")]
        public async Task Reset()
        {
            store.SetSetting(Context.Guild.Id, ConfigStore.GlobalEnabledKey, null);
            store.SetGlobalParams(Context.Guild.Id, null);
            store.SetGenericValues(Context.Guild.Id, null);
            await this.Say("Restored the default: on, removing click IDs and `utm_*` values that aren't generic labels.");
        }
    }

    // ---- /linkcleaner path … ------------------------------------------------------------

    [Group("path", "Remove trackers that live in a link's path (e.g. Amazon's /ref=…).")]
    public sealed class PathGroup(ConfigStore store) : SiteCommandModule(store)
    {
        [SlashCommand("add", "Remove text matching a regex from this site's link paths.")]
        public Task Add(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Regex matched against the path only, e.g. /ref=[^/?#]*")] string pattern) =>
            WithSite(site, async rule =>
            {
                if (!CommandHelpers.IsValidRegex(pattern)) { await this.Say(CommandHelpers.InvalidRegex); return; }
                if (rule.StripPathPatterns.Contains(pattern)) { await this.Say("That pattern is already listed."); return; }
                if (rule.StripPathPatterns.Count >= Limits.PathPatternsPerSite) { await this.Say(CommandHelpers.ListFull(Limits.PathPatternsPerSite)); return; }

                Save(rule with { StripPathPatterns = [.. rule.StripPathPatterns, pattern] });
                await this.Say($"`{site}` link paths will have text matching `{pattern}` removed.");
            });

        [SlashCommand("remove", "Stop removing a path pattern.")]
        public Task Remove([Autocomplete(typeof(SiteAutocomplete))] string site, [Summary(description: "The pattern exactly as listed")] string pattern) =>
            WithSite(site, async rule =>
            {
                var remaining = CommandHelpers.Without(rule.StripPathPatterns, pattern);
                if (remaining is null) { await this.Say("That pattern isn't listed."); return; }

                Save(rule with { StripPathPatterns = remaining });
                await this.Say($"Removed the path pattern from `{site}`.");
            });
    }

    // ---- /linkcleaner host … ------------------------------------------------------------

    [Group("host", "Manage the hostnames a site rule covers (aliases).")]
    public sealed class HostGroup(ConfigStore store) : SiteCommandModule(store)
    {
        [SlashCommand("add", "Treat another hostname as part of this site.")]
        public Task Add([Autocomplete(typeof(SiteAutocomplete))] string site, string host) => WithSite(site, async rule =>
        {
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say(CommandHelpers.InvalidHost); return; }
            if (rule.Hosts.Contains(normalized)) { await this.Say($"`{normalized}` is already covered."); return; }
            if (rule.Hosts.Count >= Limits.HostsPerSite) { await this.Say(CommandHelpers.ListFull(Limits.HostsPerSite)); return; }

            Save(rule with { Hosts = [.. rule.Hosts, normalized] });
            await this.Say($"`{site}` now also covers `{normalized}` and its subdomains.");
        });

        [SlashCommand("remove", "Stop covering a hostname.")]
        public Task Remove([Autocomplete(typeof(SiteAutocomplete))] string site, string host) => WithSite(site, async rule =>
        {
            if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say(CommandHelpers.InvalidHost); return; }
            var remaining = CommandHelpers.Without(rule.Hosts, normalized);
            if (remaining is null) { await this.Say($"`{normalized}` isn't in `{site}`'s list."); return; }

            Save(rule with { Hosts = remaining });
            await this.Say($"`{site}` no longer covers `{normalized}`.");
        });
    }

    // ---- /linkcleaner mirror … ----------------------------------------------------------

    [Group("mirror", "Choose the embed-friendly mirror used for login-walled sites.")]
    public sealed class MirrorGroup(ConfigStore store) : SiteCommandModule(store)
    {
        [SlashCommand("set", "Use a mirror host for this site.")]
        public Task Set(
            [Autocomplete(typeof(SiteAutocomplete))] string site,
            [Summary(description: "Mirror hostname, e.g. fixupx.com")] string host,
            [Summary(description: "Optional regex for which URL paths get a mirror; omit for every non-root path")] string? pathPattern = null) =>
            WithSite(site, async rule =>
            {
                if (!CommandHelpers.TryNormalizeHost(host, out var normalized)) { await this.Say(CommandHelpers.InvalidHost); return; }
                if (pathPattern is not null && !CommandHelpers.IsValidRegex(pathPattern)) { await this.Say(CommandHelpers.InvalidRegex); return; }

                Save(rule with { MirrorHost = normalized, MirrorPathPattern = pathPattern ?? rule.MirrorPathPattern });
                await this.Say($"`{site}` links will be mirrored via `{normalized}`.");
            });

        [SlashCommand("clear", "Stop mirroring this site.")]
        public Task Clear([Autocomplete(typeof(SiteAutocomplete))] string site) => WithSite(site, async rule =>
        {
            Save(rule with { MirrorHost = null });
            await this.Say($"`{site}` will no longer be mirrored.");
        });
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
