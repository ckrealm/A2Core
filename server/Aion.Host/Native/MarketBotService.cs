using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Runs the market bot (Aion:Market, docs/MARKET.md) every few seconds: expired listings back to their sellers, the seller
/// bot's restocks and the buyer bot's purchases, each at its own interval. A database error is logged and retried next tick.
/// </summary>
public sealed class MarketBotService(MarketBot bot, ILogger<MarketBotService> logger) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Market: {Options}; the seller bot picks from {Items} items.", bot.Options, bot.Pool.Count);
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await bot.TickAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or OverflowException)
            {
                logger.LogWarning("Market bot tick failed: {Error}; retrying", error.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
