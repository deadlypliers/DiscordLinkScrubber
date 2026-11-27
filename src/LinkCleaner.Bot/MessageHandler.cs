using Discord;
using Discord.WebSocket;
using LinkCleaner.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LinkCleaner.Bot;

/// <summary>
/// Reacts to new and edited messages. Invariant: the repost is always sent before the original
/// is deleted, and the original is never deleted if the repost fails.
/// </summary>
public sealed class MessageHandler(
    DiscordSocketClient client,
    ConfigStore store,
    IMemoryCache cache,
    ILogger<MessageHandler> log)
{
    private const string OptOutHint = " · No pings: /linkcleaner-pings off";
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(6);

    private readonly object _gate = new();

    /// <summary>True if the poster should be notified: the server allows it and they haven't opted out.</summary>
    public static bool ShouldPing(GuildConfig config, ulong authorId) =>
        config.PingAuthor && !config.PingOptOuts.Contains(authorId);

    /// <summary>
    /// Only the poster can ever be notified. Mentions inside their own text (@everyone, roles, other
    /// users) and the replied-to author stay suppressed either way.
    /// </summary>
    public static AllowedMentions BuildAllowedMentions(GuildConfig config, ulong authorId) =>
        ShouldPing(config, authorId)
            ? new AllowedMentions(AllowedMentionTypes.None) { UserIds = [authorId], MentionRepliedUser = false }
            : AllowedMentions.None;

    /// <summary>
    /// Picks the repost layout. The original is replaced unless it has files (attachments or stickers,
    /// which can't be carried over) or the rewritten text is too long to send; then it is kept and the
    /// text carries just the cleaned links.
    /// </summary>
    public static (string Text, bool ReplacesOriginal) ComposeRepost(
        string mention, ProcessedMessage processed, string infoLink, bool ping, bool hasFiles)
    {
        var footer = $"-# [Why did this happen?](<{infoLink}>){(ping ? OptOutHint : "")}";

        if (!hasFiles)
        {
            var replacement = $"From {mention}:\n{processed.RewrittenContent}\n{footer}";
            if (replacement.Length <= Limits.MessageLength)
                return (replacement, true);
        }

        var links = processed.ReplacementLinks;
        var label = links.Count > 1 ? "Clean links" : "Clean link";
        return ($"{label} for {mention}:\n{string.Join('\n', links)}\n{footer}", false);
    }

    public Task OnMessageReceived(SocketMessage message)
    {
        _ = RunAsync(message);
        return Task.CompletedTask;
    }

    public Task OnMessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel)
    {
        // Discord also fires updates when embeds resolve: only react when the text itself changed.
        if (before.HasValue && before.Value.Content == after.Content)
            return Task.CompletedTask;

        _ = RunAsync(after);
        return Task.CompletedTask;
    }

    private async Task RunAsync(SocketMessage message)
    {
        try
        {
            await HandleAsync(message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error processing message {MessageId}", message.Id);
        }
    }

    private async Task HandleAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot || msg.Author.IsWebhook) return;
        if (msg.Type is not (MessageType.Default or MessageType.Reply)) return;
        if (msg.Channel is not SocketGuildChannel channel) return;

        var guild = channel.Guild;
        var config = store.Get(guild.Id);

        var thread = msg.Channel as SocketThreadChannel;
        if (!config.AppliesTo(channel.Id, thread?.ParentChannel?.Id))
        {
            log.LogDebug("Message {MessageId}: skipped, channel #{Channel} is excluded by channel rules", msg.Id, channel.Name);
            return;
        }
        if (msg.Author is SocketGuildUser member && member.Roles.Any(r => config.ExemptRoles.Contains(r.Id)))
        {
            log.LogDebug("Message {MessageId}: skipped, author has an exempt role", msg.Id);
            return;
        }

        var processed = LinkProcessor.Process(msg.Content, config.Sites, config.GlobalRule, config.Enforcement);
        if (!processed.HasWork)
        {
            if (processed.Links.Count > 0)
                log.LogInformation("Message {MessageId} in #{Channel}: {Count} link(s) on tracked sites ({Sites}) but nothing to strip or mirror",
                    msg.Id, channel.Name, processed.Links.Count, string.Join(", ", processed.Links.Select(l => l.Site).Distinct()));
            else if (string.IsNullOrEmpty(msg.Content) && msg.Attachments.Count == 0 && msg.Embeds.Count == 0)
                log.LogDebug("Message {MessageId}: empty content (is the Message Content Intent enabled?)", msg.Id);
            else
                log.LogDebug("Message {MessageId}: no links on tracked sites", msg.Id);
            return;
        }

        var perms = guild.CurrentUser.GetPermissions(channel);
        var canSend = thread is not null ? perms.SendMessagesInThreads : perms.SendMessages;
        if (!perms.ViewChannel || !canSend || !perms.ReadMessageHistory)
        {
            WarnOnce(channel, "send messages / read message history");
            return;
        }

        IUserMessage anchor = msg; // what a mirror reply attaches to

        if (processed.HasTrackingRemoved)
        {
            if (!perms.ManageMessages)
            {
                WarnOnce(channel, "manage messages");
                return;
            }
            if (!Claim($"tracked:{msg.Id}"))
                return;

            var repost = await RepostAsync(msg, processed, config);
            if (repost is null)
                return;
            anchor = repost;
        }

        if (processed.MirrorReplyLinks.Count > 0)
            await SendMirrorReplyAsync(msg, anchor, processed, config);
    }

    /// <summary>Returns the message that now carries the cleaned links, or null if nothing was sent.</summary>
    private async Task<IUserMessage?> RepostAsync(SocketUserMessage msg, ProcessedMessage processed, GuildConfig config)
    {
        var ping = ShouldPing(config, msg.Author.Id);
        var (text, replacesOriginal) = ComposeRepost(msg.Author.Mention, processed, config.InfoLink, ping,
            hasFiles: msg.Attachments.Count > 0 || msg.Stickers.Count > 0);

        // A replacement takes over whatever the original replied to; otherwise reply to the original itself.
        var replyToId = replacesOriginal
            ? msg.Reference?.MessageId.ToNullable()
            : msg.Id;
        var replyTo = replyToId is { } id ? new MessageReference(id, failIfNotExists: false) : null;

        IUserMessage sent;
        try
        {
            sent = await msg.Channel.SendMessageAsync(text,
                allowedMentions: BuildAllowedMentions(config, msg.Author.Id),
                messageReference: replyTo);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send repost for message {MessageId}; original left in place", msg.Id);
            return null;
        }

        if (replacesOriginal)
        {
            try
            {
                await msg.DeleteAsync();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Repost sent but deleting original message {MessageId} failed", msg.Id);
            }
        }

        log.LogInformation("Message {MessageId} in #{Channel}: removed tracking from {Count} link(s) ({Mode}, author {Pinged})",
            msg.Id, msg.Channel.Name, processed.Links.Count(l => l.TrackingRemoved),
            replacesOriginal ? "reposted and deleted original" : "replied, original kept",
            ping ? "pinged" : "not pinged");

        var lines = processed.Links.Where(l => l.TrackingRemoved).Select(l => $"<{l.Original}> → <{l.Replacement}>");
        await ModLogAsync(config, msg, "🧹 Removed tracking", lines);
        return sent;
    }

    private async Task SendMirrorReplyAsync(SocketUserMessage msg, IUserMessage anchor, ProcessedMessage processed, GuildConfig config)
    {
        List<string> fresh;
        lock (_gate)
        {
            var sentBefore = cache.GetOrCreate($"mirrors:{msg.Id}", e =>
            {
                e.AbsoluteExpirationRelativeToNow = DedupeWindow;
                return new HashSet<string>();
            })!;
            fresh = processed.MirrorReplyLinks.Where(sentBefore.Add).ToList();
        }
        if (fresh.Count == 0)
            return;

        var prefix = config.MirrorPrefix.Trim();
        var text = (prefix.Length > 0 ? prefix + " " : "") + string.Join('\n', fresh);

        try
        {
            await msg.Channel.SendMessageAsync(text,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(anchor.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send mirror reply for message {MessageId}", msg.Id);
            return;
        }

        log.LogInformation("Message {MessageId} in #{Channel}: posted {Count} mirror link(s)", msg.Id, msg.Channel.Name, fresh.Count);

        var lines = processed.Links
            .Where(l => !l.TrackingRemoved && l.Mirror is not null)
            .Select(l => $"<{l.Original}> → <{l.Mirror}>");
        await ModLogAsync(config, msg, "🪞 Posted mirror", lines);
    }

    private async Task ModLogAsync(GuildConfig config, SocketUserMessage msg, string title, IEnumerable<string> lines)
    {
        if (config.ModLogChannelId is not { } channelId || client.GetChannel(channelId) is not IMessageChannel modLog)
            return;

        var text = Limits.FitMessage($"{title} — {msg.Author.Mention} (`{msg.Author.Id}`) in <#{msg.Channel.Id}>\n{string.Join('\n', lines)}");

        try
        {
            await modLog.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not write to mod-log channel {ChannelId}", channelId);
        }
    }

    /// <summary>True the first time a key is seen within the dedupe window (so edits can't trigger a second repost).</summary>
    private bool Claim(string key)
    {
        lock (_gate)
        {
            if (cache.TryGetValue(key, out _))
                return false;
            cache.Set(key, true, DedupeWindow);
            return true;
        }
    }

    private void WarnOnce(SocketGuildChannel channel, string missing)
    {
        if (Claim($"warned:{channel.Id}"))
            log.LogWarning("Missing permission ({Missing}) in #{Channel} ({ChannelId}) of {Guild}; skipping messages there",
                missing, channel.Name, channel.Id, channel.Guild.Name);
    }
}
