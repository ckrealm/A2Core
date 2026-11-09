using Aion.World;

namespace Aion.Host.Native;

/// <summary>Runs the playerbot loop (Aion:Bots, docs/PLAYERBOTS.md): every bot ticked every 50 ms, like a game connection.</summary>
public sealed class PlayerBotsService(PlayerBots bots, ILogger<PlayerBotsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Playerbots on: {Options}", bots.Options);
        try
        {
            // A failing tick is logged and the loop goes on: an exception out of ExecuteAsync would stop the whole host
            // (BackgroundServiceExceptionBehavior.StopHost), taking every player with it.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await bots.RunAsync(stoppingToken); }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(error, "Playerbots: a bot tick failed; the bot loop goes on");
                    await Task.Delay(PlayerBots.TickMs, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Shutdown: every bot leaves and saves (its owner's store is still open while the listeners drain).
            foreach (var owner in bots.All.Select(b => b.Owner).Distinct().ToList())
            {
                try { await bots.OwnerLeftAsync(owner, CancellationToken.None); }
                catch (Exception error) { logger.LogWarning(error, "Playerbots: leaving at shutdown failed"); }
            }
        }
    }
}
