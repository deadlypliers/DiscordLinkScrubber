using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LinkCleaner.Bot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Configuration sources, later ones win: appsettings.json, appsettings.{Environment}.json,
// user-secrets (Development only), environment variables, command-line args.
var builder = Host.CreateApplicationBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["DISCORD_TOKEN"]))
{
    Console.Error.WriteLine(
        "DISCORD_TOKEN is not set. Set it as an environment variable, or for local debugging run:\n" +
        "  dotnet user-secrets set DISCORD_TOKEN <token> --project src/LinkCleaner.Bot\n" +
        "See README.md.");
    return 1;
}

var databasePath = builder.Configuration["LINKCLEANER_DB"]
                   ?? Path.Combine(AppContext.BaseDirectory, "data", "linkcleaner.db");

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(new ConfigStore(databasePath));
builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
{
    // Message Content is a privileged intent: enable it in the Discord developer portal.
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = false,
    LogLevel = LogSeverity.Info,
}));
builder.Services.AddSingleton(sp => new InteractionService(
    sp.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig { DefaultRunMode = RunMode.Async, LogLevel = LogSeverity.Info }));
builder.Services.AddSingleton<MessageHandler>();
builder.Services.AddHostedService<BotService>();

await builder.Build().RunAsync();
return Environment.ExitCode; // non-zero if the bot shut itself down (e.g. rejected token)
