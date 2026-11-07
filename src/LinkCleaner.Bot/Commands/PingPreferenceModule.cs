using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace LinkCleaner.Bot.Commands;

/// <summary>
/// /linkcleaner-pings … — deliberately separate from /linkcleaner so every member can use it
/// (the admin group is restricted to Manage Server). Preferences are stored per server.
/// </summary>
[Group("linkcleaner-pings", "Choose whether the bot pings you when it reposts your link.")]
[CommandContextType(InteractionContextType.Guild)]
public sealed class PingPreferenceModule(ConfigStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("off", "Stop pinging me when my link is reposted (you're still named, just not notified).")]
    public async Task Off()
    {
        store.AddPingOptOut(Context.Guild.Id, Context.User.Id);
        await this.Say("Done. You won't be pinged any more in this server. Use `/linkcleaner-pings on` to change your mind.");
    }

    [SlashCommand("on", "Ping me when my link is reposted.")]
    public async Task On()
    {
        store.RemovePingOptOut(Context.Guild.Id, Context.User.Id);
        await this.Say(store.Get(Context.Guild.Id).PingAuthor
            ? "Done. You'll be pinged when the bot reposts your link."
            : "Saved, but pings are currently turned off for this whole server, so nobody is being pinged.");
    }

    [SlashCommand("status", "Show whether you get pinged.")]
    public async Task Status()
    {
        var cfg = store.Get(Context.Guild.Id);
        var text = !cfg.PingAuthor
            ? "Pings are turned off for this whole server."
            : cfg.PingOptOuts.Contains(Context.User.Id)
                ? "You've opted out: the bot names you but doesn't ping you."
                : "You get pinged when the bot reposts your link. Use `/linkcleaner-pings off` to opt out.";
        await this.Say(text);
    }
}
