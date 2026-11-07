# Link Cleaner

A Discord bot that removes social-tracking parameters from shared links and makes login-walled
posts viewable.

1. **Tracking removal.** If a message contains a link with a tracking parameter (for example
   YouTube's `?si=…`), the bot posts a replacement and deletes the original. The replacement
   @-mentions the author (without pinging them), keeps what they wrote, swaps in the clean link,
   and says why with a "Why?" link.
2. **Mirrors for login-walled sites.** For X and Instagram posts the bot replies with an
   embed-friendly mirror link (`fixupx.com`, `kkinstagram.com`), so the post shows in chat without
   logging in. If a link has tracking *and* is on one of these sites, the replacement uses the
   mirror of the cleaned link.

## Behaviour details

- Only links on the configured **site list** are touched. Query strings on unknown sites are left alone.
- Parameters that make a link work (`v`, `t`, `list`, …) are kept. Only listed parameters are stripped.
- Links in `<angle brackets>`, inline code and code blocks are left alone. `||spoilers||`, replies and
  surrounding text are preserved. Pings in the original text are not repeated.
- Messages with attachments or stickers are not deleted. The bot replies with the clean links instead.
- Edits that add a link are handled like new messages.
- The bot ignores other bots and webhooks.
- The repost is always sent **before** the original is deleted, and the original is never deleted if the
  repost fails.
- Redirects (`instagram.com/share/…`, `t.co/…`) are **not** followed. Such links only have their
  visible parameters stripped.
- Only configuration is stored (SQLite). Message content is never saved or logged.

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
| `/linkcleaner param add` / `remove` | Choose which query parameters are stripped (`utm_*` style prefixes work) |
| `/linkcleaner host add` / `remove` | Treat more hostnames as the same site |
| `/linkcleaner mirror set` / `clear` | Choose the mirror host for a site |
| `/linkcleaner channel include` / `exclude` / `reset` / `clear` | Limit where the bot works (default: everywhere) |
| `/linkcleaner role exempt` / `unexempt` | Exempt roles from cleaning |
| `/linkcleaner infolink url:…` | Set the "Why?" link |
| `/linkcleaner prefix emoji:…` | Set (or remove) the emoji before mirror replies |
| `/linkcleaner modlog channel:…` | Log every action to a channel (off by default) |

Built-in sites: YouTube, Instagram, X/Twitter, Facebook, Reddit, TikTok, Spotify, Twitch. Only X and
Instagram have a mirror by default.

## Development

```sh
dotnet test
```

- `src/LinkCleaner.Core`: the pure cleaning logic (no Discord, network or storage code).
- `src/LinkCleaner.Bot`: Discord.Net host, SQLite config, slash commands.
- `tests/LinkCleaner.Tests`: table-driven tests for the core plus thin tests for the host.
