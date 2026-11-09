using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Aion:Boost (OURS, FriendsRules.cs): "//boost" in chat. Once any character of the account has reached the required level,
// a character below the boost level pays the price in Kinah and is raised to it (the usual level-up messages), its race's
// released main story, regional and exploration quests (sealed dungeons included) up to the quest level are marked
// rewarded (no rewards paid), a gear set of the best items its class can wear at that level lands in its bag, and the map
// is revealed. "//boost" alone says what it costs and whether this character may use it; "//boost confirm" buys it.
public sealed partial class GameSession
{
    /// <summary>The setting that records a boost (per character).</summary>
    public const string BoostedSetting = "boosted";
    static readonly string[] BoostQuestTypes = ["Hero", "District", "Exploration"];
    static readonly string[] BoostArmor = ["Helmet", "Shoulder", "Torso", "Pants", "Gloves", "Boots", "Cape", "Necklace"];

    /// <summary>The quests a boost marks done for a race: released (titled) main story, regional and exploration quests up to the quest level.</summary>
    public static List<string> BoostQuests(QuestBook book, int race, int questLevel) =>
        book.Quests.Where(q => BoostQuestTypes.Contains(q.Value.Type) && QuestBook.RaceMatches(q.Value, race)
                && !string.IsNullOrWhiteSpace(q.Value.Title) && q.Value.Title != "???"
                && q.Value.RecommendedLevel <= questLevel && q.Value.Level <= questLevel
                && (q.Value.Type != "Exploration" || q.Key.StartsWith(race == 2 ? "EQ2" : "EQ1", StringComparison.Ordinal)))
            .Select(q => q.Key).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The boost's gear set for a class and race: for each slot the highest item level wearable at the boost level (lowest id
    /// on a tie): the class weapon (and a shield for a Templar), helmet, shoulders, torso, pants, gloves, boots, cape, necklace,
    /// two earrings, two rings and a bracelet (a Templar's shield takes the bracelet's place).
    /// </summary>
    public static List<int> BoostGear(GameData data, string? className, int race, int level)
    {
        IEnumerable<int> Best(string category, int count) => data.Items
            .Where(i => i.Item.Category == category && i.Item.PermitLevel <= level && (i.Item.Race == 0 || i.Item.Race == race))
            .OrderByDescending(i => i.Item.Level).ThenBy(i => i.Id).Take(count).Select(i => i.Id);
        var gear = new List<int>();
        if (data.ClassWeapon(className) is { } weapon and not "None") gear.AddRange(Best(weapon, 1));
        foreach (var slot in BoostArmor) gear.AddRange(Best(slot, 1));
        gear.AddRange(Best("Earring", 2));
        gear.AddRange(Best("Ring", 2));
        gear.AddRange(className == "Templar" ? Best("Guarder", 1) : Best("Bracelet", 1));
        return gear;
    }

    /// <summary>Whether //boost is on for this host (Aion:Boost:Enabled).</summary>
    public bool BoostEnabled => options.Boost.Enabled;

    /// <summary>A quest's status in this character's journal (EQuestStatus), or 0 when it is not there.</summary>
    public int QuestStatus(string code) => questLog?.Get(code)?.Status ?? 0;

    /// <summary>"//boost [confirm]": the answer and the packets of a bought boost.</summary>
    public async Task<(string Message, List<byte[]> Packets)> BoostCommandAsync(string args, CancellationToken ct)
    {
        var boost = options.Boost;
        if (!boost.Enabled) return ("The level boost is off on this server.", []);
        if (Bot) return ("Bots cannot be boosted.", []);
        if (Store is not { } store || CharacterId is not { } character || questLog is null) return ("Enter the world with an account character first.", []);
        var price = boost.Price.ToString("N0", CultureInfo.InvariantCulture);
        var offer = $"The boost takes a character to level {boost.Level} with the main story, regional quests and sealed dungeons done"
            + (boost.Gear ? $" and a level-{boost.Level} gear set (item level about {BoostGear(data, className, profileRace, boost.Level).Sum(data.ItemLevel)})" : "")
            + $", for {price} Kinah.";
        // A boost that was paid but did not finish (a save failed) is finished at no charge.
        var settings = await store.CharacterSettingsAsync(character, ct);
        var record = settings.TryGetValue(BoostedSetting, out var saved) && saved.ValueKind == JsonValueKind.Object ? saved : (JsonElement?)null;
        if (record is { } r && (!r.TryGetProperty("done", out var d) || d.ValueKind != JsonValueKind.False))
            return ($"{offer} This character has already been boosted.", []);
        var pending = record is not null;
        var characters = await store.LobbyAsync(ct);
        var unlockedBy = characters.Where(c => c.Summary.Level >= boost.RequiredLevel).Select(c => c.Summary.Name).FirstOrDefault();
        if (unlockedBy is null && !pending) return ($"{offer} It unlocks once a character of this account reaches level {boost.RequiredLevel}.", []);
        if (level >= boost.Level && !pending) return ($"{offer} This character is already level {level}; use it on an alt below {boost.Level}.", []);
        var kinah = storedItems.Values.Where(i => i.TemplateId == KinaItemId && i.Location == "bag").Sum(i => i.Quantity);
        if (!string.Equals(args.Trim(), "confirm", StringComparison.OrdinalIgnoreCase))
            return pending
                ? ($"Your boost was paid but did not finish. Type //boost confirm to finish it (no charge).", [])
                : ($"{offer} Unlocked by {unlockedBy}. You have {kinah.ToString("N0", CultureInfo.InvariantCulture)} Kinah. Type //boost confirm to buy it for this character.", []);

        var replies = new List<byte[]>();
        var from = pending && record!.Value.TryGetProperty("from", out var f) && f.TryGetInt32(out var fromLevel) ? fromLevel : level;
        var gear = boost.Gear ? BoostGear(data, className, profileRace, boost.Level).Select(id => (id, 1L)).ToList() : [];
        const string unfinished = " Type //boost confirm again to finish it (no extra charge).";
        if (!pending)
        {
            // Pay and take the gear in one transaction, then record the paid boost before anything else is saved.
            var costs = new List<(Guid, long, int)>();
            if (boost.Price > 0)
            {
                if (CostStacks(KinaItemId, boost.Price) is not { } stacks) return ($"The boost costs {price} Kinah; you have {kinah.ToString("N0", CultureInfo.InvariantCulture)}.", []);
                costs.AddRange(stacks);
            }
            if (costs.Count > 0 || gear.Count > 0)
            {
                var paid = await Stored(async () => await store.ShopBuyAsync(character, costs, gear, [], ct) ? [[]] : []);
                if (paid.Count == 0) return ("The boost could not be paid: make room in your bag for the gear set and try again.", []);
                replies.AddRange(await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy));
            }
            var noted = await SaveBoostedAsync(store, character, from, false, ct);
            Note($"boost paid: {boost.Price} Kinah, {gear.Count} gear items" + (noted ? "" : " (the paid boost could not be recorded)"));
            if (!noted) return ($"The boost was paid but could not be recorded; ask the staff to finish it (//report).", replies);
        }

        // The level first: the experience up to the boost level, with the usual level-up messages.
        var needed = 0L;
        for (var l = level; l < boost.Level; l++) needed += data.NeedExp(l);
        needed = Math.Max(0, needed - experience);
        if (needed > 0 && level < boost.Level) replies.AddRange(await GainExperienceAsync(experience + needed, needed, ExpReasonQuest, ct));
        if (level < boost.Level) return ($"The boost is paid but the new level could not be saved.{unfinished}", replies);

        // Quests: rewarded (no rewards paid), in one save; the ones in the journal leave it.
        var unlockedBefore = QuestUnlockTypes().ToHashSet();
        var removed = new List<int>();
        var records = new List<QuestRecord>();
        foreach (var code in BoostQuests(questLog.Book, profileRace, boost.QuestLevel))
        {
            var existing = questLog.Get(code);
            if (existing is { Status: QuestLog.Rewarded }) continue;
            var definition = questLog.Book.Find(code)!;
            records.Add((existing ?? QuestLog.New(code, definition)) with { Status = QuestLog.Rewarded, Step = Math.Max(1, definition.Steps.Length) });
            if (existing is not null && existing.Uid != 0) removed.Add((int)existing.Uid);
        }
        if (records.Count > 0)
        {
            var questsSaved = await Stored(async () =>
            {
                foreach (var quest in await store.SaveQuestsAsync(character, records, ct)) questLog.Set(quest);
                return [[]];
            });
            if (questsSaved.Count == 0) return ($"Level {level} is saved but the quests could not be.{unfinished}", replies);
        }
        if (removed.Count > 0) replies.Add(GamePackets.QuestRemove(removed, 1));
        var unlocked = QuestUnlockTypes().Where(t => !unlockedBefore.Contains(t)).ToArray();
        if (unlocked.Length > 0) replies.Add(GamePackets.ContentsUnlockUpdate(unlocked));

        replies.AddRange(await RevealMapAsync(ct));
        await SaveBoostedAsync(store, character, from, true, ct);
        Note($"boost: {from} -> {level}, {records.Count} quests" + (pending ? " (finished a paid boost)" : ""));
        return ($"Boosted to level {level}: {records.Count} quests done" + (gear.Count > 0 && !pending ? $", the gear set ({gear.Count} items) is in your bag" : "")
            + ". Go back to character select and in again to refresh the quest journal.", replies);
    }

    /// <summary>Records the boost (done false: paid, not finished yet); whether it was saved.</summary>
    async Task<bool> SaveBoostedAsync(INativeCharacterStore store, Guid character, int from, bool done, CancellationToken ct) =>
        (await Stored(async () =>
        {
            var value = JsonSerializer.SerializeToElement(new { from, to = options.Boost.Level, done, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            return await store.SaveCharacterSettingAsync(character, BoostedSetting, value, ct) ? [[]] : [];
        })).Count > 0;
}
