// World bot capacity benchmark (docs/WORLDBOTS.md "How many bots"): runs N world bots (and optional idle players) in one shared world
// with the embedded data, ticking at the real 50 ms rate in simulated time as fast as the machine allows, and reports what a tick costs.
// Usage: dotnet run -c Release --project tests/Aion.Bots.Bench -- [bots ...] [--seconds 120] [--players 10] [--parallel]
using System.Diagnostics;
using Aion.Protocol.Native;
using Aion.World;

var counts = new List<int>();
var seconds = 120;
var players = 0;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds") seconds = int.Parse(args[++i]);
    else if (args[i] == "--players") players = int.Parse(args[++i]);
    else counts.Add(int.Parse(args[i]));
}
if (counts.Count == 0) counts = [25, 50, 100, 200];
var data = GameData.Embedded;
Console.WriteLine($"Hunting grounds: {HuntingGrounds.Embedded.Count} on maps {string.Join(", ", HuntingGrounds.Embedded.GroupBy(g => g.Map).Select(g => $"{g.Key} ({g.Count()}, race {g.First().Race})"))}");
Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores; simulated {seconds} s per run, {players} idle players");
Console.WriteLine("| Bots | Players | ms per 50 ms tick (avg) | p99 | worst | Bot loop share of one core | Heap MB | Kills/min | Deaths/min | Levels gained | Casts sent/accepted |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
foreach (var count in counts) await Run(count);

async Task Run(int count)
{
    GC.Collect();
    var world = new SharedWorld(data);
    var options = new WorldBotOptions { Enabled = true, Count = count, MinLevel = 10, MaxLevel = 50, LoginsPerSecond = 1000, Seed = 7 };
    var bots = new WorldBots(data, options, world, new GameSessionOptions { World = world });
    long t = 1_800_000_000_000;
    // Idle players standing at hunting grounds (they tick like a connection: their own timers, NPC AI around them, broadcasts).
    var humans = new List<GameSession>();
    for (var p = 0; p < players; p++)
    {
        var ground = HuntingGrounds.Embedded[(p * 37) % HuntingGrounds.Embedded.Count];
        var s = new GameSession(data, new GameSessionOptions { World = world, CharacterLevel = ground.Level, DevelopmentMap = ground.Map }, new Random(p));
        await s.HandleAsync(BotRequests.GameLogin($"player:{p}"), t);
        await s.HandleAsync(BotRequests.Create($"Player{(char)('a' + p % 26)}{(char)('a' + p / 26)}", ground.Race == 2 ? 9 : 1), t);
        var sel = await s.HandleAsync(BotRequests.Select(65536), t);
        await s.HandleAsync(BotRequests.LoadComplete(1), t);
        s.Teleport(ground.Map, ground.Centre, t);
        await s.HandleAsync(BotRequests.LoadComplete(1), t);
        humans.Add(s);
    }
    // Log everyone in first (not timed).
    for (var k = 0; k < 20 && bots.InWorld.Count < count; k++) await bots.TickAsync(t += 50);
    var deaths = 0;
    var perBot = new Dictionary<string, int>();
    var lastSeen = new Dictionary<string, (int, string, int)>();
    var wasDead = new Dictionary<string, bool>();
    var startLevels = bots.InWorld.ToDictionary(b => b.Name, b => b.Level);
    var npcDeaths = 0;
    var ticks = seconds * 1000 / 50;
    var samples = new double[ticks];
    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    var deadNpcs = new HashSet<(int, int)>();
    for (var i = 0; i < ticks; i++)
    {
        t += 50;
        var sw = Stopwatch.StartNew();
        await bots.TickAsync(t);
        foreach (var h in humans) await h.TickAsync(t);
        samples[i] = sw.Elapsed.TotalMilliseconds;
        if (i % 4 == 0)
        {
            foreach (var b in bots.InWorld)
            {
                var dead = b.Session!.Dead;
                var onMe = b.Session.SharedMap?.Ai.Brains.Values.Count(x => x.Engaged && !x.VictimIsNpc && x.VictimKey == b.Session.PlayerKey) ?? 0;
                if (!dead) lastSeen[b.Name] = (onMe, b.Activity, b.Session.Resource.Hp * 100 / Math.Max(1, b.Session.Resource.HpMax));
                if (dead && !wasDead.GetValueOrDefault(b.Name) && Environment.GetEnvironmentVariable("BENCH_DETAIL") == "1") Console.WriteLine($"   death {b.Name} L{b.Level} at {i * 50 / 1000}s: before {lastSeen.GetValueOrDefault(b.Name)}");
                if (dead && !wasDead.GetValueOrDefault(b.Name)) { deaths++; perBot[b.Name] = perBot.GetValueOrDefault(b.Name) + 1; }
                wasDead[b.Name] = dead;
            }
            foreach (var map in world.Maps)
                foreach (var npc in map.Targets.Values)
                    if (npc.Dead) { if (deadNpcs.Add((map.MapId, npc.Key))) npcDeaths++; }
                    else deadNpcs.Remove((map.MapId, npc.Key));
        }
    }
    wall.Stop();
    var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
    Array.Sort(samples);
    var live = bots.InWorld;
    var levels = live.Sum(b => b.Level - startLevels.GetValueOrDefault(b.Name, b.Level));
    var sent = live.Sum(b => b.Brain?.CastsSent ?? 0);
    var accepted = live.Sum(b => b.Brain?.CastsAccepted ?? 0);
    var minutes = seconds / 60.0;
    Console.WriteLine($"| {live.Count} | {players} | {samples.Average():F2} | {samples[(int)(ticks * 0.99)]:F2} | {samples[^1]:F1} | {samples.Average() / 50 * 100:F0}% | {GC.GetTotalMemory(false) / 1048576} | {npcDeaths / minutes:F0} | {deaths / minutes:F1} | {levels} | {sent}/{accepted} |");
    if (Environment.GetEnvironmentVariable("BENCH_DETAIL") == "1")
        foreach (var b in live.Take(12))
            Console.WriteLine($"   {b.Name} {b.ClassName} L{b.StartLevel}->{b.Level} map {b.Session!.MapId} ground {b.Roam?.Ground?.Map}/{b.Roam?.Ground?.Level} {b.Activity} deaths {perBot.GetValueOrDefault(b.Name)} hp {b.Session.Resource.Hp}/{b.Session.Resource.HpMax} def {b.Session.Defense} casts {b.Brain!.CastsSent}/{b.Brain.CastsAccepted} moves {b.Brain.MovesSent} rejects {b.Session.SpeedRejects}");
    await bots.StopAsync();
    foreach (var h in humans) h.ExitSharedWorld();
}
