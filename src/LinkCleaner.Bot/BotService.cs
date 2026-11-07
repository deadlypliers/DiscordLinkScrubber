using System.Reflection;
using Discord;
using Discord.Net;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkCleaner.Bot;

/// <summary>Owns the Discord connection: login, event wiring, slash command registration.</summary>
public sealed class BotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    MessageHandler messages,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<BotService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = configuration["DISCORD_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The DISCORD_TOKEN environment variable is not set.");

        client.Log += OnLog;
        interactions.Log += OnLog;

        client.MessageReceived += messages.OnMessageReceived;
        client.MessageUpdated += messages.OnMessageUpdated;
        client.GuildAvailable += OnGuildAvailable;
        client.JoinedGuild += guild => RegisterCommandsAsync(guild);
        client.InteractionCreated += OnInteractionCreated;
        interactions.SlashCommandExecuted += OnSlashCommandExecuted;

        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private async Task OnGuildAvailable(SocketGuild guild)
    {
        await RegisterCommandsAsync(guild);
        WarnAboutMissingPermissions(guild);
    }

    // One server: register commands per guild so changes show up immediately.
    private async Task RegisterCommandsAsync(SocketGuild guild)
    {
        try
        {
            await interactions.RegisterCommandsToGuildAsync(guild.Id);
            log.LogInformation("Registered slash commands in {Guild}", guild.Name);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not register slash commands in {Guild}", guild.Name);
        }
    }

    private void WarnAboutMissingPermissions(SocketGuild guild)
    {
        var lacking = guild.TextChannels
            .Where(c => c is not SocketThreadChannel)
            .Select(c => (Channel: c, Perms: guild.CurrentUser.GetPermissions(c)))
            .Where(x => x.Perms.ViewChannel && (!x.Perms.SendMessages || !x.Perms.ManageMessages || !x.Perms.ReadMessageHistory))
            .Select(x => "#" + x.Channel.Name)
            .ToList();

        if (lacking.Count > 0)
            log.LogWarning("In {Guild} the bot lacks Send Messages / Manage Messages / Read Message History in: {Channels}",
                guild.Name, string.Join(", ", lacking));
    }

    private async Task OnInteractionCreated(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(client, interaction);
            await interactions.ExecuteCommandAsync(context, services);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Interaction failed");
        }
    }

    private async Task OnSlashCommandExecuted(SlashCommandInfo command, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
            return;

        log.LogWarning("Command {Command} failed: {Error} {Reason}", command.Name, result.Error, result.ErrorReason);
        try
        {
            var message = $"That didn't work: {result.ErrorReason}";
            if (context.Interaction.HasResponded)
                await context.Interaction.FollowupAsync(message, ephemeral: true);
            else
                await context.Interaction.RespondAsync(message, ephemeral: true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not report command failure to user");
        }
    }

    private Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        log.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);

        // Discord.Net retries forever after errors that can only be fixed by a human, which looks healthy
        // but is dead. Say what is wrong and exit instead.
        if (FatalReason(message.Exception) is { } reason && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            log.LogCritical("{Reason} Shutting down.", reason);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        return Task.CompletedTask;
    }

    /// <summary>A human-readable cause if the error can't fix itself by reconnecting, otherwise null.</summary>
    private static string? FatalReason(Exception? ex) => ex switch
    {
        null => null,
        HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized } => TokenRejected,
        ArgumentException { ParamName: "token" } => TokenRejected,
        WebSocketClosedException { CloseCode: 4004 } => TokenRejected,
        WebSocketClosedException { CloseCode: 4014 } =>
            "Discord refused the connection (4014: disallowed intent). Enable \"Message Content Intent\" under " +
            "Bot > Privileged Gateway Intents in the Discord developer portal.",
        WebSocketClosedException { CloseCode: 4013 } => "Discord refused the connection (4013: invalid intents).",
        _ => FatalReason(ex.InnerException),
    };

    private const string TokenRejected = "Discord rejected the bot token. Check DISCORD_TOKEN.";
}
