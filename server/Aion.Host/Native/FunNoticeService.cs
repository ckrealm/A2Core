using Aion.Host.Authentication;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>Shows the field boss notices (Aion:Fun, docs/friends-server/FUN.md) to everyone when they are due: screen and chat.</summary>
public sealed class FunNoticeService(FunFeatures fun, IServiceProvider services, ILogger<FunNoticeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Fun features: {Options}", fun.Options.Summary());
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var text in fun.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                {
                    logger.LogInformation("Notice: {Text}", text);
                    try { await StaffEndpoints.AnnounceAllAsync(services, text, "FFD54A"); }
                    catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Fun notice failed"); }
                }
        }
        catch (OperationCanceledException) { }
    }
}
