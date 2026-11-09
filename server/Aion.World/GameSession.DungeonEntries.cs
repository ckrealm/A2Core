using Aion.Application.Gameplay;

namespace Aion.World;

// Daily dungeon entry limits (Aion:Dungeons, OURS; off by default). Checked when the move into a dungeon is decided
// (EnterDungeon: refused like the MapEntrance rules, nothing sent) and counted once the copy is joined at the map load,
// once per copy: going back into the party copy already counted is free, a fresh copy counts again. Counts are kept per
// character, dungeon and counting day (game.character_dungeon_entries, migration 039); a new day starts at
// the shared reset hour (Aion:Resets, ResetClock; Aion:Dungeons:ResetHour overrides it). Staff are neither limited nor counted.
public sealed partial class GameSession
{
    readonly Dictionary<int, int> dungeonEntries = [];
    string dungeonEntryPeriod = "";
    /// <summary>The dungeon whose entry was allowed and waits for its copy to count it (0: none).</summary>
    int pendingEntryCount;
    /// <summary>The last copy counted (re-entering it is free).</summary>
    DungeonCopy? countedCopy;

    DungeonOptions DungeonRules => options.Dungeons;

    /// <summary>Today's entries by dungeon id (the counting day of <paramref name="now"/>).</summary>
    public IReadOnlyDictionary<int, int> DungeonEntriesToday(long now)
    {
        RollEntryPeriod(now);
        return dungeonEntries;
    }

    void LoadDungeonEntries(IReadOnlyList<DungeonEntryRecord> rows, long now)
    {
        dungeonEntries.Clear();
        dungeonEntryPeriod = DungeonRules.Period(now);
        foreach (var row in rows)
            if (row.Period == dungeonEntryPeriod && row.Count > 0) dungeonEntries[row.DungeonId] = row.Count;
    }

    /// <summary>A new counting day empties the counts (the stored rows of older days are pruned at the next save).</summary>
    void RollEntryPeriod(long now)
    {
        var period = DungeonRules.Period(now);
        if (period == dungeonEntryPeriod) return;
        dungeonEntryPeriod = period;
        dungeonEntries.Clear();
    }

    /// <summary>Whether this entry stays within the dungeon's daily limit; an allowed limited entry waits to be counted at the copy.</summary>
    bool WithinEntryLimit(int dungeonId, DungeonInfo room, DungeonCopy? joining, long now, out string why)
    {
        why = "";
        pendingEntryCount = 0;
        if (DungeonRules.LimitOf(dungeonId, room) is not { } limit) return true;
        if (joining is not null && ReferenceEquals(joining, countedCopy)) return true;   // back into the copy already counted
        RollEntryPeriod(now);
        var used = dungeonEntries.GetValueOrDefault(dungeonId);
        if (used >= limit) { why = $"{used} of {limit} daily entries used; the count resets at {DungeonRules.EffectiveResetHour:00}:00"; return false; }
        pendingEntryCount = dungeonId;
        return true;
    }

    /// <summary>At the copy's join: the allowed entry counts (once per copy) and is stored.</summary>
    void CountDungeonEntry(int dungeonId, long now)
    {
        if (pendingEntryCount == 0 || pendingEntryCount != dungeonId || copy is null) return;
        pendingEntryCount = 0;
        if (ReferenceEquals(copy, countedCopy)) return;
        countedCopy = copy;
        RollEntryPeriod(now);
        var count = dungeonEntries.GetValueOrDefault(dungeonId) + 1;
        dungeonEntries[dungeonId] = count;
        if (Store is not { } store || CharacterId is not { } character) return;
        var record = new DungeonEntryRecord(dungeonId, dungeonEntryPeriod, count);
        _ = InvokeAsync(async _ => (true, await Stored(async () => await store.SaveDungeonEntryAsync(character, record, CancellationToken.None) ? [] : [])));
    }

    /// <summary>Staff ResetDungeonEntries: this character's counts (memory and storage) back to 0; the number of dungeons cleared.</summary>
    public async Task<int> ResetDungeonEntriesAsync(CancellationToken ct)
    {
        var cleared = dungeonEntries.Count;
        dungeonEntries.Clear();
        countedCopy = null;
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { cleared = Math.Max(cleared, await store.ResetDungeonEntriesAsync(character, ct)); return []; });
        return cleared;
    }
}
