# Fun features (`Aion:Fun`)

Friends-server extras. All values are OURS (our own choice, not retail). Everything is on by default; each part can be switched off.

## Field bosses

Before this, every open-world monster respawned on the flat `Aion:Rules:RespawnSeconds` timer (30 s), bosses included.

- **Field bosses**: open-world HeroMonsters whose spawn name says "Boss" (`M_L1_FieldBoss_Kashapa_01_1`, ...) or of grade 6 and up. The current data has 13: four per side in zone 1 (level 48–51) and two or three per side in zone 2 (level 80). Each kill picks a random respawn time between `BossRespawnMinMinutes` and `BossRespawnMaxMinutes` (60–90). Retail uses timers of several hours; for 5–10 players a shorter window lets everyone see them.
- **Named elites**: every other open-world HeroMonster (59 of them). They respawn after 10–15 minutes (`NamedRespawn*`) and are not announced.
- **Notices** (`BossAnnouncements`): a boss kill ("Brandon and 2 others defeated the field boss Kashapa (level 48) in the Light lands!") and its return ("The field boss Kashapa (level 48) has appeared in the Light lands.") go to everyone, on screen and in chat. Names come from the spawn names, because the data has no display names.
- **Shared loot** (`SharedBossLoot`): each fighter rolls the boss's or named elite's full loot for themselves. Fighters are the killer, everyone who damaged it, and the killer's party (so healers and buffers count), as long as they are alive and within 100 m when it dies. The killer's roll is not split with the party for these monsters.
- Timers are kept in memory, so after a server restart every boss is up.
- Dungeons and scripted layers are not affected.

## Lucky drops

Gear grades in this client, from lowest to highest: Common, Rare, Epic (named "Legend" in the client data), Unique. In the client data the enhance cap is +5 for Common and Rare, +10 for Epic and +15 for Unique. Per the Aion 2 guides, higher grades also get more manastone slots and option rolls.

- **One grade up**: a monster's equipment drop may become the next grade, with the same category, race and the nearest item level within 5. Common or Rare becomes Epic, and Epic becomes Unique. The chance depends on the monster: `UpgradeChanceNormal` 2%, `UpgradeChanceElite` 8%, `UpgradeChanceHero` 25% (named elites and bosses).
- **Pre-enchanted**: each equipment piece looted from a monster (picked up, or shared through party or boss loot) may arrive enchanted. Chances are `EnchantLowChance` 25% for +1 to +3, `EnchantMidChance` 5% for +4 to +6, and `EnchantHighChance` 1% for +7 up to the item's max. The level never passes the item's cap.
- Anvil enchant odds are not changed here.

## Chat commands (every player)

- `//buff`: the best drink, food and scroll buffs from the item data (HP regen and resistance, damage +16 and defense +160, +15% move speed, +15% attack speed, the damage up/down scroll, +15% max HP) for `BuffMinutes` (30). `BuffCooldownSeconds` (0) sets the wait between uses. `BuffSkills` lists the item skill IDs used. Not while dead.
- `//hub` lists the teleports for your race, and `//hub <number>` uses one. Light: Verteron (Dawn Legion Base), Eltnen (Changarnerk Camp), the Abyss. Dark: Altgard (Fugitive Village), the zone 2 home point, the Abyss (level `HubAbyssMinLevel`, 45). It doesn't work in a dungeon, a duel, an arena match, while dead, or during a fight, which includes any living monster on the map still having you on its hate list.

## Duels

`DuelRestore`: when a duel ends, for any reason, each duelist gets back the HP and MP the duel cost them. They return to what they had when the battle started (or keep more if they healed), so a duel is never a free full heal.

## Settings

Set these in `/etc/aion/game.env` (or as environment variables), for example `Aion__Fun__BossRespawnMinMinutes=45`.

| Key | Default |
|---|---|
| `Aion:Fun:FieldBosses` | true |
| `Aion:Fun:BossRespawnMinMinutes` / `MaxMinutes` | 60 / 90 |
| `Aion:Fun:NamedRespawnMinMinutes` / `MaxMinutes` | 10 / 15 |
| `Aion:Fun:BossAnnouncements` | true |
| `Aion:Fun:SharedBossLoot` | true |
| `Aion:Fun:LuckyDrops` | true |
| `Aion:Fun:UpgradeChanceNormal` / `Elite` / `Hero` (percent) | 2 / 8 / 25 |
| `Aion:Fun:EnchantLowChance` / `Mid` / `High` (percent) | 25 / 5 / 1 |
| `Aion:Fun:BuffCommand`, `BuffMinutes`, `BuffCooldownSeconds`, `BuffSkills` | true, 30, 0, six item skills |
| `Aion:Fun:HubCommand`, `HubAbyssMinLevel` | true, 45 |
| `Aion:Fun:DuelRestore` | true |

The host logs the effective values at startup ("Fun features: ...").

Checks: `tests/Aion.Native.Checks` ("fun feature checks").
