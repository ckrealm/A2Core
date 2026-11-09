using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Aion:Abyss (docs/ABYSS_PVP.md): ticks the artifact sieges, the rift portals and the battlefield queue once a second, with the
/// world held exclusively (the same turn a connection takes for world work).
/// </summary>
public sealed class AbyssWarService(AbyssWar war, SharedWorld world, ILogger<AbyssWarService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = war.Options;
        logger.LogInformation("Abyss PvP on: siege {Siege} ({Days} {Hour:00}:{Minute:00}, {Artifacts} artifacts), rifts {Rifts} (every {Every}h), battlefield {Battlefield} ({Windows}), bots {Bots}",
            o.Siege.Enabled, string.Join("/", o.Siege.Days), o.Siege.Hour, o.Siege.Minute, war.Artifacts.Count, o.Rifts.Enabled, o.Rifts.EveryHours,
            o.Battlefield.Enabled, string.Join(", ", o.Battlefield.Windows), war.Bots is null ? "off" : "on");
        var turn = new WorldTurn(world);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await turn.EnterWorldAsync(stoppingToken);
                try { war.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                catch (Exception error) { logger.LogWarning(error, "Abyss PvP tick failed"); }
                finally { turn.Exit(); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
