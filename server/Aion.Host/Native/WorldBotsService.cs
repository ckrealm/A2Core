using Aion.Application.Chat;
using Aion.Host.Chat;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>Runs the world bot loop (Aion:WorldBots, docs/WORLDBOTS.md): every bot every 50 ms; they leave the world at shutdown.</summary>
public sealed class WorldBotsService(WorldBots bots, ILogger<WorldBotsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("World bots on: {Count} bots, levels {Min}-{Max}, {Grounds} hunting grounds", bots.Options.Count, bots.Options.MinLevel,
            bots.Options.MaxLevel == 0 ? "cap" : bots.Options.MaxLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), bots.Grounds.Count);
        try { await bots.RunAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        finally
        {
            try { await bots.StopAsync(); }
            catch (Exception error) { logger.LogWarning(error, "World bots: leaving at shutdown failed"); }
        }
    }
}

/// <summary>
/// Bot chat (Aion:BotChat, docs/WORLDBOTS.md "Bot chat"): listens to the shared chat rooms, lets world bots answer and chatter, and
/// delivers their lines through the chat relay. The model runs one call at a time, off the game loop.
/// </summary>
public sealed class BotChatService(BotChatter chatter, WorldBots bots, ChatRelay relay, StompHub hub, ILogger<BotChatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = chatter.Options;
        logger.LogInformation("Bot chat on: room {Room}, faction only {Faction}, model {Model} at {Endpoint}{Key}", options.Room, options.FactionOnly,
            options.Endpoint.Length > 0 ? options.Model : "(canned lines)", options.Endpoint.Length > 0 ? options.Endpoint : "-",
            options.ApiKey.Length > 0 ? " (with an API key)" : "");
        relay.IsBot = bots.IsBot;
        relay.Heard += chatter.Hear;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var line in await chatter.TickAsync(stoppingToken))
                    {
                        var deliveries = line.WhisperTo is { } to
                            ? relay.WhisperFromBot(line.Name, to, line.Text, line.WhisperRoom, line.WhisperSubType)
                            : relay.Speak(line.Name, options.Room, line.Text, options.FactionOnly ? line.Race : 0);
                        await hub.DeliverAsync(deliveries);
                        logger.LogDebug("Bot chat {Name}: {Text}", line.Name, line.Text);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Bot chat failed"); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            relay.Heard -= chatter.Hear;
            relay.IsBot = null;
        }
    }
}
