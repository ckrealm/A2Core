using System.Text.Json;
using Aion.Application.Gameplay;

namespace Aion.World;

// Aion:Unlocks (OURS, FriendsRules.cs): at a character's world entry (character selection), every skin and every pet at its
// top level are given once per character and perk, and the creature boards raised to their top level; a character setting
// remembers each perk so it is not given again (and nothing is taken back when the switch is turned off).
public sealed partial class GameSession
{
    /// <summary>Gives the perks switched on and not yet given; which ones were given now.</summary>
    async Task<(bool Skins, bool Pets)> GrantUnlocksAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        var perks = options.Unlocks;
        if (!perks.AllSkins && !perks.AllPets) return (false, false);
        var settings = await store.CharacterSettingsAsync(character, ct);
        var skins = perks.AllSkins && !settings.ContainsKey(UnlockOptions.SkinsGivenSetting);
        var pets = perks.AllPets && !settings.ContainsKey(UnlockOptions.PetsGivenSetting);
        if (!skins && !pets) return (false, false);
        var top = Boxes.RideMaxLevel;
        var granted = await store.GrantUnlocksAsync(character, skins ? Skins.All : [], pets ? Boxes.AllRides.Select(id => (id, top)).ToList() : [], ct);
        if (!granted) return (false, false);
        var stamp = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (skins) await store.SaveCharacterSettingAsync(character, UnlockOptions.SkinsGivenSetting, stamp, ct);
        if (pets) await store.SaveCharacterSettingAsync(character, UnlockOptions.PetsGivenSetting, stamp, ct);
        Note($"Aion:Unlocks: {(skins ? $"{Skins.All.Count} skins" : "")}{(skins && pets ? " and " : "")}{(pets ? $"{Boxes.AllRides.Count} pets at level {top}" : "")} given");
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
}
