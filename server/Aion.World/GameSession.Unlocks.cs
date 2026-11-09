using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Aion:Unlocks (OURS, FriendsRules.cs): at a character's world entry (character selection), every skin and every pet at its
// top level are given once per character and perk, and the creature boards raised to their top level; a character setting
// remembers each perk so it is not given again (and nothing is taken back when the switch is turned off). AllTraces is
// checked at every world entry instead: the feathers are achievement states, completed in memory before the achievement
// list goes out and written with the next achievement flush.
public sealed partial class GameSession
{
    /// <summary>Gives the perks switched on and not yet given; which ones were given now.</summary>
    async Task<(bool Skins, bool Pets)> GrantUnlocksAsync(INativeCharacterStore store, Guid character, int race, CancellationToken ct)
    {
        var perks = options.Unlocks;
        if (!perks.AllSkins && !perks.AllPets && !perks.AllWings) return (false, false);
        var settings = await store.CharacterSettingsAsync(character, ct);
        var skins = perks.AllSkins && !settings.ContainsKey(UnlockOptions.SkinsGivenSetting);
        var pets = perks.AllPets && !settings.ContainsKey(UnlockOptions.PetsGivenSetting);
        var wings = perks.AllWings && !settings.ContainsKey(UnlockOptions.WingsGivenSetting);
        if (!skins && !pets && !wings) return (false, false);
        var top = Boxes.RideMaxLevel;
        var wingIds = wings ? GamePackets.Wings(race).ToList() : [];
        var granted = await store.GrantUnlocksAsync(character, skins ? Skins.All : [], pets ? Boxes.AllRides.Select(id => (id, top)).ToList() : [], wingIds, ct);
        if (!granted) return (false, false);
        var stamp = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (skins) await store.SaveCharacterSettingAsync(character, UnlockOptions.SkinsGivenSetting, stamp, ct);
        if (pets) await store.SaveCharacterSettingAsync(character, UnlockOptions.PetsGivenSetting, stamp, ct);
        if (wings) await store.SaveCharacterSettingAsync(character, UnlockOptions.WingsGivenSetting, stamp, ct);
        var given = new List<string>();
        if (skins) given.Add($"{Skins.All.Count} skins");
        if (pets) given.Add($"{Boxes.AllRides.Count} pets at level {top}");
        if (wings) given.Add($"{wingIds.Count} wings");
        Note($"Aion:Unlocks: {string.Join(", ", given)} given");
        return (skins, pets);
    }

    /// <summary>Every creature board at its top level, its slots open (after the rides are loaded).</summary>
    async Task MaxBoardsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        foreach (var (type, board) in boards.ToList())
        {
            var top = Boxes.CreatureMaxLevel(type);
            if (board.Level >= top) continue;
            var maxed = WithOpenSlots(board with { Level = top, Exp = 0 });
            if (await store.SaveCreatureBoardAsync(character, ToRecord(maxed), new Dictionary<int, int>(), null, ct)) boards[type] = maxed;
        }
    }

    /// <summary>
    /// Aion:Unlocks:AllTraces: every Empyrean Trace ("feather") of the race and of all races marked collected, i.e. its
    /// Monolith fragment achievement complete (count at its target, completion time now), after <see cref="LoadAchievements"/>.
    /// The client's Monolith fragment window and world map read these states from AchievementList_NT. Returns how many
    /// were completed now (0 when the switch is off or all were already collected).
    /// </summary>
    public int CollectAllTraces(int race, long now)
    {
        if (!options.Unlocks.AllTraces) return 0;
        race = race == 2 ? 2 : 1;
        var completed = 0;
        foreach (var entry in Achievements.Traces)
        {
            if (Achievements.GroupOf(entry) is not { } group || (group.Race != 0 && group.Race != race)) continue;
            if (!achievements.TryGetValue(entry.Id, out var state)) achievements[entry.Id] = state = new AchievementState();
            if (state.CompletedMs != 0 && state.Count >= entry.Target) continue;
            state.Count = entry.Target;
            state.CompletedMs = now;
            state.UpdatedMs = now;
            achievementDirty.Add(entry.Id);
            completed++;
        }
        if (completed > 0) Note($"Aion:Unlocks: {completed} feathers (Empyrean Traces) collected");
        return completed;
    }
}
