# Rules-channel message

Paste-ready text for the server's rules channel. **1,570 characters** (Discord's limit is 2000). Copy
everything between the two horizontal rules.

If the bot's data handling changes (new stored data, new logging, the mod log turned off), update both this
message and the audit table below.

---

## 🧹 About LinkScrubber (our link-cleaning bot)

**What's a tracking link?**
Share buttons on YouTube, Instagram, X, Facebook, TikTok, Amazon, Google and others add a hidden ID to links (like `?si=abc123` or `?igsh=…`). That ID can tie a link to the person who shared it, and to everyone who clicks it, so these sites can map who knows who and who shares with whom. It does nothing for the person actually watching.

**What the bot does**
• Spots links with tracking IDs (and `utm_` marketing tags on any link), deletes that message, and reposts it with a clean link, your text intact, and a tag so you know it was yours.
• For X and Instagram posts, adds a mirror link so the post shows up here without anyone needing to log in.
• Don't want the tag-ping? Use `/linkcleaner-pings off`.

**What it reads and keeps**
✅ Reads message text in channels it can see, only to look for links. It's checked in memory, then forgotten.
✅ Saves server settings (which sites and parameters to clean, channel and role rules) and the IDs of members who opted out of pings.
✅ Staff-only mod log: when the bot acts, it records who, which channel, and the original and cleaned link.
❌ Doesn't store, log or sell your messages, and it can't see DMs.
❌ Doesn't open or follow links. It only edits the text.
❌ No ads, no analytics, nothing about what you click.

One thing to know: the mirror links (fixupx.com, kkinstagram.com) are run by other people. When Discord previews one, or you click it, that site sees the request. Skip the mirror link if that bothers you.

Questions? Ask a mod.

---

## Notes

- The mod-log bullet is true only while `/linkcleaner modlog` is set to a channel. Delete the bullet if you turn it off.
- The "`utm_` marketing tags on any link" wording is true only while the server-wide rule is on (the default).
  If you run `/linkcleaner global disable`, delete that parenthesis. If you add parameters to the list
  (`/linkcleaner global add fbclid`), the wording still holds.
- The site names in the "What's a tracking link?" paragraph are examples of built-in sites. Edit them if you
  remove sites or add your own.
- Discord's own retention of messages is outside the bot's control. The message deliberately doesn't promise
  that a deleted original disappears from Discord's systems.
- "Can tie a link to the person who shared it" is hedged on purpose. The article the bot links to explains
  the mechanism in more detail.

## Claim audit

| Claim in the message | Backed by |
| --- | --- |
| Reads text only to find links, in memory | `MessageHandler.HandleAsync` calls `LinkProcessor.Process`; nothing is persisted; `MessageCacheSize = 0` in `Program.cs` |
| Saves settings and opt-out IDs only | `ConfigStore` tables: `sites`, `settings`, `channel_filters`, `exempt_roles`, `ping_optouts`, `deleted_defaults` (names of built-in sites an admin removed). Exported config files contain only sites, the server-wide rule, info link, mirror prefix and the ping setting, never member IDs |
| Mod log records who, where, original and cleaned link | `MessageHandler.ModLogAsync`, only when a mod-log channel is set |
| Doesn't log your messages | Every `log.Log*` call records message id, channel name, counts or site names; never text, URLs or user ids |
| Doesn't open or follow links | The only `HttpClient` use is `AttachmentDownloader`, which fetches an admin's `/linkcleaner import` file and refuses any host except Discord's attachment hosts (and never follows redirects). Nothing fetches URLs from messages |
| `utm_` tags removed from any link | `GlobalRule.Default` (on, `utm_*`), applied by `LinkProcessor.Evaluate` to unlisted sites too; admins can change it |
| Can't see DMs | Intents are `Guilds`, `GuildMessages`, `MessageContent`; the handler ignores non-guild channels |
| Mirror sites see requests | They are third-party services; the bot only writes their URL into a message |
| No ads, analytics or selling | No such code or dependencies |
