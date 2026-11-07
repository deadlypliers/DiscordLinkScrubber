using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LinkCleaner.Bot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISCORD_TOKEN")))
{
    Console.Error.WriteLine("DISCORD_TOKEN is not set. See README.md.");
    return 1;
}

var databasePath = Environment.GetEnvironmentVariable("LINKCLEANER_DB")
                   ?? Path.Combine(AppContext.BaseDirectory, "data", "linkcleaner.db");

var builder = Host.CreateApplicationBuilder(args);

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
return 0;
