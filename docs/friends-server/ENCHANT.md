# Enchanting odds (`Aion:Enchant`)

The official enchant table stays as it is: the same levels, costs and per-level chances, and the same "each failure adds
to the next try" correction. On the friends server the odds are faster by default. Code: `server/Aion.World/EnchantRules.cs`
(applied in `GameSession.Enchant.cs`); data: `tools/data/enchant.json`.

| Key (env) | Default | What it does |
|---|---|---|
| `Aion__Enchant__SuccessMultiplier` | `2.0` | The table's success chance times this, capped at 100%. Must be above 0, at most 100. |
| `Aion__Enchant__PityMultiplier` | `2.0` | The chance each failure adds to the next attempt (FailCorrection, +5% officially on +11..+15) times this. 0 turns it off. |
| `Aion__Enchant__NoDowngrade` | `true` | A failure never lowers the level (officially Rune accessories lose one level). |
| `Aion__Enchant__KeepMaterialsOnFail` | `false` | A failure spends no Kina or materials. Off by default: the official cost stays. |

`1` / `1` / `false` / `false` is the official behaviour exactly. With `tools/server_env.py`: `--enchant-success`,
`--enchant-pity`, `--enchant-no-downgrade on|off`, `--enchant-keep-materials on|off` (unset: the defaults above). The
startup log line ends with `Enchant: ...` showing what is in force.

## What the defaults give

| Attempt | Official | Default (x2, correction +10%) |
|---|---|---|
| Weapons/armour +1..+10 | 100% | 100% |
| +10 -> +11 | 65% | 100% |
| +11 -> +12 | 50% | 100% |
| +12 -> +13 | 35% | 70%, then 80%, 90%, 100% after failures |
| +13 -> +14 | 25% | 50%, then 60%, 70%... |
| +14 -> +15 | 20% | 40%, then 50%, 60%... |
| Rune accessory +1 -> +2 ... +9 -> +10 | 80, 66, 50, 33, 25, 20, 15, 12, 10% | 100, 100, 100, 66, 50, 40, 30, 24, 20%; no level lost |

The correction resets after a success, as officially. It is stored per item (`upgrades.enchant_bonus`), so changing the
multipliers later does not change what an item has already built up.

## Notes
- The client's enchant window may still show the official chance: it reads its own copy of the table. The server's roll
  is the one that counts.
- The table stores the Rune failure penalty as `-1`; it is read as one level lost (before this change the server added a
  level on a Rune failure instead).
