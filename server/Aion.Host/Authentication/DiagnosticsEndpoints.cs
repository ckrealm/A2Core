using System.Diagnostics;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Diagnostics;
using Aion.World;
using Npgsql;

namespace Aion.Host.Authentication;

/// <summary>
/// GET /api/admin/diagnostics (staff): what /api/admin/server does not show, for finding why something went wrong:
/// process figures, open native connections, a SELECT 1 per database, sign-in outcomes, logged errors per source and the
/// game requests that got no reply, all since the host started. docs/DIAGNOSTICS.md describes each field.
/// </summary>
public static class DiagnosticsEndpoints
{
    static readonly string[] Databases = ["Aion", "Auth"];

    public static void MapDiagnostics(RouteGroupBuilder staff)
    {
        staff.MapGet("/diagnostics", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services,
            IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            if (services.GetService<HostDiagnostics>() is not { } diagnostics) return Results.NotFound();
            var presence = services.GetService<WorldPresence>();
            var databases = await Task.WhenAll(Databases.Select(name => PingAsync(name, config.GetConnectionString(name), ct)));
            var (cpuPercent, cpuCores, window) = diagnostics.Cpu();
            using var process = Process.GetCurrentProcess();
            return Results.Ok(new
            {
                startedAt = diagnostics.StartedAt,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - diagnostics.StartedAt).TotalSeconds,
                process = new
                {
                    cpuPercent, cpuCores, cpuWindowSeconds = window, cores = Environment.ProcessorCount,
                    workingSetMb = process.WorkingSet64 / 1048576, gcHeapMb = GC.GetTotalMemory(false) / 1048576,
                    gcCommittedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576,
                    gcCollections = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
                    threads = process.Threads.Count,
                },
                sessions = new { login = diagnostics.LoginConnections, game = presence?.Connections ?? 0, characters = presence?.All.Count ?? 0 },
                databases,
                logins = diagnostics.Logins(),
                errors = diagnostics.Errors(),
                unansweredOpcodes = diagnostics.UnansweredOpcodes(),
            });
        });
    }

    /// <summary>SELECT 1 on one connection string (3 s limit). Only the server and database names are shown, never the credentials.</summary>
    static async Task<object> PingAsync(string name, string? connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return new { name, configured = false, host = (string?)null, database = (string?)null, reachable = false, latencyMs = (double?)null, error = (string?)null };
        string? host = null, database = null;
        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(connectionString);
            (host, database) = (parsed.Host, parsed.Database);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(limit.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(limit.Token);
            return new { name, configured = true, host, database, reachable = true, latencyMs = (double?)Math.Round(watch.Elapsed.TotalMilliseconds, 2), error = (string?)null };
        }
        catch (Exception error) when (error is NpgsqlException or OperationCanceledException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            if (ct.IsCancellationRequested) throw;
            var reason = error is PostgresException pg ? $"{error.GetType().Name} {pg.SqlState}" : error.GetType().Name;
            return new { name, configured = true, host, database, reachable = false, latencyMs = (double?)null, error = (string?)reason };
        }
    }
}
