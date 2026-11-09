# Loot rules (`Aion:Loot`)

The top grind complaint: equipment drops for the wrong class, duplicates of what you already have, and long dry streaks.
`Aion:Loot` fixes those while keeping the official drop tables. These are our own rules (**OURS**: nothing observed from
the official server). Code: `server/Aion.World/LootRules.cs`; it is used by `GameSession.Loot.cs`, `LootTables.cs` and
`MonsterRewards.cs`.

## Settings

| Key | Default | What it does |
|---|---|---|
| `Aion:Loot:ClassAware` | `true` | An equipment pick that your class or race can't use is drawn again from the same package or level band, with the same weights. If every draw is still unusable, the weapon is swapped for your class's weapon of the same grade. |
| `Aion:Loot:ClassRerolls` | `3` | How many times a wrong-class or duplicate pick is drawn again (0-20). With 0, a wrong-class weapon is always swapped straight away. |
| `Aion:Loot:AvoidDuplicates` | `true` | An equipment pick you already have is drawn again. "Already have" means in your bag, storage, equipped, on the ground for you, or picked earlier in the same kill. If no draw finds anything new, you keep the duplicate. |
| `Aion:Loot:BadLuckProtection` | `true` | Counts your "dry" kills (kills that gave you no equipment), per character and per monster. See below. |
| `Aion:Loot:DryKillBonus` | `0.25` | The equipment chance is multiplied by `1 + DryKillBonus × dry kills` (0-10). |
| `Aion:Loot:PityKills` | `8` | After this many dry kills, the next kill always gives you one equipment item for your class (1-1000). |
| `Aion:Loot:PityMonsters` | `EliteMonster,HeroMonster` | The NpcSubTypes that bad-luck protection counts, comma separated. Trash (`NormalMonster`) is left out, so the guarantee can't be farmed on weak monsters. |

Environment form (`/etc/aion/game.env`): `Aion__Loot__ClassAware=false`, `Aion__Loot__PityKills=5`, and so on.
`tools/server_env.py` takes `--class-loot on|off`, `--loot-rerolls N`, `--avoid-duplicate-loot on|off`,
`--bad-luck-protection on|off`, `--dry-kill-bonus X` and `--pity-kills N`. A bad value stops the host at startup with the
reason. The effective values are logged at startup (`Loot: ...`).

**With every switch off, the loot is exactly what it was before:** the same picks from the same random numbers. Checks
cover this.

## How "usable by your class" is known

`Item.dat` has no class field. `tools/data/items.json` keeps each item's EquipCategory, ItemLevel, PermitLevelMin and
ItemRace. As in `tools/equipment_rules.py`:

- Only **weapons** are class-bound. A class uses its own `weapon_type` from `class-skills.json`: Gladiator Greatsword,
  Templar Sword, Assassin Dagger, Ranger Bow, Sorcerer Magicbook, Elementalist Orb, Cleric Mace, Chanter Staff.
- **Guarders, armour and accessories** suit every class.
- **ItemRace** must match your race (0 means any race).

**Swap.** Item.dat numbers weapons `11CTxxxxx`, with the weapon type in the fourth digit, the same digit as each class's
starter weapon. Across all 1,218 class weapons in items.json, the sibling with your class's digit has the same ItemLevel,
PermitLevelMin and ItemRace (checked: all 9,730 sibling pairs that exist match; 14 siblings are missing). A swap is used only when items.json
confirms all of that for the item in hand. This reading of the numbering is a **CANDIDATE**, confirmed only by the data.

## Bad-luck protection in numbers

Dry kills are counted per character and per monster (NpcData id), so each boss has its own streak. Any equipment you get
from that monster resets the count.

| Dry kills | Equipment chance |
|---|---|
| 0 | × 1 (official) |
| 1 | × 1.25 |
| 4 | × 2 |
| 8 | × 3, and the next kill is guaranteed |

For example, an elite on the fallback table drops gear 15% of the time. With the defaults that rises to 45% after 8 dry
kills, and the 9th kill is certain. Without it, about 1 elite farmer in 25 would still be dry after 20 kills (0.85^20).

The guaranteed item comes from the monster's own equipment packages, picked by their chance and weights. A monster without
a reward table uses its level band instead. The guaranteed item is always class-aware and avoids duplicates when it can,
even if `ClassAware` is off.

## Parties (`Aion:Rules:PartyLoot`)

The kill is rolled once, for the killer (class-aware for the killer), and then shared as before.

- A member who receives another class's **weapon** gets their own class's weapon of the same grade. This is a swap only,
  because the roll is already over and can't be redrawn.
- Each member nearby counts their own dry kills against the monster, and gets their own guaranteed item when their
  streak is up, even if their share of that kill was empty.
- Playerbots never count dry kills or get a guaranteed item, whether they made the kill or were nearby.
- The chance boost only applies to the killer's roll.

## Limitations

- **Dry-kill counts live in memory**, one book for the whole host. They survive a relog but are lost on a host restart.
  No table holds them yet.
- **Duplicates.** Ownership is read from the session's own item rows, so it is cheap. A party member's share isn't
  redrawn for duplicates.
- **Level.** PermitLevelMin isn't checked: the tables and level bands already drop level-appropriate gear.
- **Kill-only.** Only the equipment packages of monster kills are affected. Boxes, quest rewards and dungeon or boss
  completion rewards are unchanged.
- **Gauntlets.** No class has the Gauntlet weapon type, so a Gauntlet is redrawn and never swapped.
