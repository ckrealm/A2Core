namespace Aion.World;

/// <summary>
/// Aion:DungeonScaling (OURS: a server owner's choice for small groups, nothing observed; docs/friends-server/DUNGEON_SCALING.md).
/// A copy of a group dungeon (Dungeon.dat type in <see cref="Types"/>: Party and Raid by default) entered by fewer than
/// <see cref="GroupSize"/> members (players and playerbots alike) gets weaker monsters: per missing member their HP drops by
/// <see cref="HpPercentPerMissing"/> and their damage by <see cref="DamagePercentPerMissing"/>, never below
/// <see cref="MinHpPercent"/> / <see cref="MinDamagePercent"/>. The group is counted once, when the copy is created (the
/// first one in: its party, bots included, or the players already inside, whichever is more); the copy's monsters keep
/// that scale for the run (dungeon monsters do not respawn). A full group (one player and three bots too) is not scaled.
/// Open-world maps, story layers, solo and PvP instances are never scaled. Enabled=false changes nothing.
/// </summary>
public sealed record DungeonScaling
{
    public bool Enabled { get; init; } = true;
    /// <summary>The group the dungeons are balanced for (OURS: <see cref="Party.Limit"/>, our CANDIDATE party size).</summary>
    public int GroupSize { get; init; } = Party.Limit;
    public int HpPercentPerMissing { get; init; } = 10;
    public int DamagePercentPerMissing { get; init; } = 7;
    public int MinHpPercent { get; init; } = 70;
    public int MinDamagePercent { get; init; } = 80;
    /// <summary>Dungeon.dat types scaled, comma-separated (a string: list binding would add to the defaults).</summary>
    public string Types { get; init; } = "Party,Raid";

    public static DungeonScaling Default { get; } = new();

    public bool Scales(DungeonInfo room) =>
        Enabled && Types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(room.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>The HP and damage multipliers for a group of <paramref name="members"/> (1, 1 when full or off).</summary>
    public (double Hp, double Damage) For(int members)
    {
        var missing = Enabled ? Math.Max(0, GroupSize - Math.Max(1, members)) : 0;
        if (missing == 0) return (1, 1);
        return (Math.Max(MinHpPercent, 100 - missing * HpPercentPerMissing) / 100.0,
                Math.Max(MinDamagePercent, 100 - missing * DamagePercentPerMissing) / 100.0);
    }

    public DungeonScaling Validated()
    {
        static void Range(string key, int value, int low, int high)
        {
            if (value < low || value > high) throw new InvalidOperationException($"Aion:DungeonScaling:{key} must be between {low} and {high} (got {value}).");
        }
        Range(nameof(GroupSize), GroupSize, 1, 40);
        Range(nameof(HpPercentPerMissing), HpPercentPerMissing, 0, 50);
        Range(nameof(DamagePercentPerMissing), DamagePercentPerMissing, 0, 50);
        Range(nameof(MinHpPercent), MinHpPercent, 10, 100);
        Range(nameof(MinDamagePercent), MinDamagePercent, 10, 100);
        foreach (var type in Types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (!type.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new InvalidOperationException($"Aion:DungeonScaling:Types has an invalid Dungeon.dat type \"{type}\".");
        return this;
    }

    public override string ToString() => !Enabled ? "small-group scaling off"
        : $"small-group scaling on ({Types}): -{HpPercentPerMissing}% HP / -{DamagePercentPerMissing}% damage per member short of {GroupSize}, at least {MinHpPercent}% / {MinDamagePercent}%";
}

public sealed partial class GameSession
{
    /// <summary>
    /// After the map's targets are set (LoadComplete): a fresh group-dungeon copy takes its scale from the group entering it
    /// (Aion:DungeonScaling) and its monsters get it, once per target set (a shared copy's set is scaled by its first entrant).
    /// </summary>
    void ApplyDungeonScaling()
    {
        if (copy is not { } c || targets is null || ReferenceEquals(c.ScaledTargets, targets)) return;
        if (c.Scale is null)
        {
            var scaling = options.DungeonScaling;
            if (MapEventData.DungeonOfMap(worldMap) is not { } room || !scaling.Scales(room)) return;
            var party = worldPlayer is not null ? Social?.PartyOf(worldPlayer)?.Members.Count ?? 1 : 1;
            var inside = options.World?.Map(worldMap, instanceKey).Players.Count ?? 0;
            var members = Math.Max(1, Math.Max(party, inside));
            c.Scale = (members, scaling.For(members).Hp, scaling.For(members).Damage);
        }
        c.ScaledTargets = targets;
        if (c.Scale is not { } scale || scale.Hp == 1 && scale.Damage == 1) return;
        foreach (var t in targets.Values)
        {
            if (!t.SubType.EndsWith("Monster", StringComparison.Ordinal)) continue;
            var hpMax = Math.Max(1, (int)Math.Round(t.HpMax * scale.Hp));
            t.Hp = t.Hp >= t.HpMax ? hpMax : Math.Min(t.Hp, hpMax);
            t.HpMax = hpMax;
            t.DamageScale *= scale.Damage;
        }
        Note($"{MapEventData.DungeonOfMap(worldMap)?.Name}: {scale.Members} member(s), monsters at {scale.Hp * 100:0}% HP and {scale.Damage * 100:0}% damage (Aion:DungeonScaling)");
    }
}
