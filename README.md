# Link Cleaner

A Discord bot that removes social-tracking parameters from shared links and makes login-walled
posts viewable.

1. **Tracking removal.** If a message contains a link with a tracking parameter (for example
   YouTube's `?si=…`), the bot posts a replacement and deletes the original. The replacement
   names the author ("From @user:"), keeps what they wrote, swaps in the clean link, and ends with a
   small "Why did this happen?" link.
2. **Mirrors for login-walled sites.** For X and Instagram posts the bot replies with an
   embed-friendly mirror link (`fixupx.com`, `kkinstagram.com`), so the post shows in chat without
   logging in. If a link has tracking *and* is on one of these sites, the replacement uses the
   mirror of the cleaned link.

A ready-to-paste explanation for your rules channel, including what the bot does and doesn't collect, is in
[`docs/rules-channel-message.md`](docs/rules-channel-message.md).

## Behaviour details

- Links on the configured **site list** get that site's parameters stripped. Links on other sites are left
  alone, with one exception: the **server-wide rule** removes known click/recipient IDs (`fbclid`, `gclid`, `msclkid`, …) and
  tracking `utm_*` tags from *any* link on *any* site (it also applies on top of every site's own list).
  `utm_*` is judged by value: generic share-button labels (`share`, `copy-link`, `web`, …) are harmless and
  left alone, but any other value could be a referral code or username, so it counts as tracking. Admins can
  edit both lists or switch the rule off with `/linkcleaner global …` (`generic-add` / `generic-remove` manage the labels).
- Parameters that make a link work (`v`, `t`, `list`, …) are kept. Only listed parameters are stripped.
- **Identifying vs. tidy parameters.** Each site has two lists. *Stripped* parameters carry an ID (`si`, `igsh`,
  `fbclid`, affiliate `tag`, …) and are the only thing that makes the bot act. *Tidy* parameters are cosmetic
  (`feature=shared`, `qid`, Google's `ved`/`ei`, …): they are removed from a link that is being cleaned anyway, but a
  link carrying only tidy parameters is left alone, with no repost and no ping (admins who want those cleaned too can
  set `/linkcleaner enforcement cosmetic:true`). Add a tidy parameter with `/linkcleaner param add … tidy:true`;
  `param remove` works on either list.
- Some trackers live in the path rather than the query (Amazon's `/ref=nav_logo`). Sites can list regexes to
  remove from the path with `/linkcleaner path add`.
- **Built-in updates reach existing servers.** When a new release adds sites or parameters, sites you haven't
  edited are refreshed automatically (their on/off switch is kept) and new built-in sites appear. A site you edited is
  never overwritten: `/linkcleaner status` warns when it is behind, and `/linkcleaner site reset <site>` loads the
  new rules. Built-in sites you deleted stay deleted.
- Links in `<angle brackets>`, inline code and code blocks are left alone. `||spoilers||`, replies and
  surrounding text are preserved. Pings in the original text are not repeated.
- **The original poster is not pinged by default.** They are named in the repost without a notification.
  Admins can turn pings on for the whole server (`/linkcleaner pings enabled:true`), and any member can then
  opt out for themselves with `/linkcleaner-pings off`. Only the poster is ever notified; `@everyone`, roles
  and other users inside their text stay silent, and mirror replies never ping anyone. When a repost does
  ping, its footer explains how to opt out.
- Messages with attachments or stickers are not deleted. The bot replies with the clean links instead.
- Edits that add a link are handled like new messages.
- The bot ignores other bots and webhooks.
- The repost is always sent **before** the original is deleted, and the original is never deleted if the
  repost fails.
- Redirects (`instagram.com/share/…`, `t.co/…`) are **not** followed. Such links only have their
  visible parameters stripped.
- Only configuration is stored (SQLite). Message content is never saved or logged.
- Links in messages are never opened. The bot's only outbound request besides Discord itself is downloading the
  config file an admin attaches to `/linkcleaner import`.

## Setup

1. Create an application at <https://discord.com/developers/applications>, add a bot, and copy its token.
2. Under **Bot → Privileged Gateway Intents**, enable **Message Content Intent** and click *Save Changes*.
   The other two intents stay off. Since this bot is for your own server, also turn **Public Bot** off so
   nobody else can invite it.
3. Invite the bot with scopes `bot applications.commands` and permissions *View Channels, Send Messages,
   Send Messages in Threads, Read Message History, Manage Messages, Embed Links*
   (permissions value `274878000128`). Embed Links is what lets the mirror previews show up:

   `https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot%20applications.commands&permissions=274878000128`

4. Run it.

### Docker (recommended)

```sh
echo "DISCORD_TOKEN=your-token-here" > .env
docker compose up -d --build
```

Configuration lives in the `linkcleaner-data` volume and survives rebuilds.

### Without Docker

```sh
export DISCORD_TOKEN=your-token-here      # PowerShell: $env:DISCORD_TOKEN = "..."
dotnet run --project src/LinkCleaner.Bot
```

The database defaults to `data/linkcleaner.db` next to the binary. Override it with `LINKCLEANER_DB`.

### Debugging locally (Rider / Visual Studio / `dotnet run`)

Use a **separate Discord application and token for development**, invited to a private test server, so
a half-finished build never touches your real server. Then store its token outside the repo with
user-secrets:

```sh
dotnet user-secrets set DISCORD_TOKEN <dev-token> --project src/LinkCleaner.Bot
```

User-secrets are a fallback: they are read only when no `DISCORD_TOKEN` came from the environment,
`appsettings*.json` or the command line, and they work in any IDE run configuration. (In the
Development environment, which the `LinkCleaner.Bot` launch profile selects, they load as a normal
configuration source instead.) Production (Docker) uses plain environment variables.

Local config is stored in `data/linkcleaner.db` next to the built binary (git-ignored). Delete it to reset,
or point `LINKCLEANER_DB` somewhere stable. If Discord rejects the token the app exits with a non-zero code.

## Configuration

Everything is configured with slash commands, available to members with **Manage Server** by default
(change that under *Server Settings → Integrations*). Answers are only visible to whoever ran the command.

| Command | What it does |
| --- | --- |
| `/linkcleaner status` | Show the current configuration |
| `/linkcleaner test text:…` | Preview what the bot would do with a message |
| `/linkcleaner site list` / `show` | Inspect site rules |
| `/linkcleaner site enable` / `disable` | Turn a site on or off |
| `/linkcleaner site add` / `remove` / `reset` | Add a custom site, delete one, or restore a built-in site's defaults |
| `/linkcleaner param add` / `remove` | Choose which query parameters are stripped (`foo_*` style prefixes work; `tidy:true` marks cosmetic ones) |
| `/linkcleaner path add` / `remove` | Remove regex matches from a site's link paths (e.g. `/ref=[^/?#]*`) |
| `/linkcleaner global list` / `add` / `remove` / `generic-add` / `generic-remove` / `enable` / `disable` / `reset` | The server-wide rule applied to **every** link on any site (default: on; click IDs plus `utm_*` values that aren't generic labels) |
| `/linkcleaner export` | Download rules and settings as `linkcleaner-config.json` |
| `/linkcleaner import file:… [replace] [confirm]` | Load an exported file: merges sites by name, or `replace:true confirm:true` makes the site list match the file. Also applies the file's server-wide rule, info link, mirror prefix and ping setting |
| `/linkcleaner host add` / `remove` | Treat more hostnames as the same site |
| `/linkcleaner mirror set` / `clear` | Choose the mirror host for a site |
| `/linkcleaner channel include` / `exclude` / `reset` / `clear` | Limit where the bot works (default: everywhere) |
| `/linkcleaner role exempt` / `unexempt` | Exempt roles from cleaning |
| `/linkcleaner enforcement cosmetic:…` | Default (false): only tracking IDs trigger a repost, cosmetic extras are tidied alongside. True: cosmetic extras like `?feature=shared` or generic `utm_*` labels are cleaned and reposted on their own too |
| `/linkcleaner pings enabled:…` | Turn pinging the original poster on or off (default) for the whole server |
| `/linkcleaner infolink url:…` | Set the "Why did this happen?" link |
| `/linkcleaner prefix emoji:…` | Set (or remove) the emoji before mirror replies |
| `/linkcleaner modlog channel:…` | Log every action to a channel (off by default) |

Available to **every member** (not just admins): `/linkcleaner-pings off` / `on` / `status`, to opt out of
or back into being pinged. Preferences are stored per server.

Exports leave out everything specific to one server: channel rules, exempt roles, the mod-log channel and
members' ping opt-outs. Importing downloads the attached file from Discord's attachment hosts only (no redirects,
64 KB limit) and validates it completely before changing anything.

Built-in sites: YouTube, Instagram, X/Twitter, Facebook, Reddit, TikTok, Spotify, Twitch, Amazon, eBay, AliExpress,
Etsy, LinkedIn, Apple Music/Podcasts, Netflix, Steam, Google, Bing, Medium. Only X and Instagram have a mirror by
default. Where each parameter list comes from is in [`docs/research/site-params.md`](docs/research/site-params.md);
anything not confirmed there is deliberately left out. Threads, Pinterest, SoundCloud, Substack and others can be
added with `/linkcleaner site add`.

## Development

```sh
dotnet test
```

- `src/LinkCleaner.Core`: the pure cleaning logic (no Discord, network or storage code).
- `src/LinkCleaner.Bot`: Discord.Net host, SQLite config, slash commands.
- `tests/LinkCleaner.Tests`: table-driven tests for the core plus thin tests for the host.
