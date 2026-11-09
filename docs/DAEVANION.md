# Daevanion boards

The Daevanion boards are Aion 2's main power progression after levelling: each class has its own boards (on global and KR
the Nezakan, Zikel, Vaizel, Triniel, Ariel and Azphel boards), each a grid of nodes opened with Daevanion points. The server
follows the client's own tables, so every class the client ships boards for (the Brawler too, ECharacterClass `Fighter`)
gets them, and a board opens at the level the client says.

## How it plays

- **Boards** (DaevanionBoard.dat) belong to one class and open at their `NeedLevel`. When a board opens, its Start node (and
  any `bAutoLearn` node) opens with it for free.
- **Nodes** (DaevanionNode.dat) sit on a row/column grid. A node opens when it touches an opened node, the character has its
  `NeedLevel`, and there are enough points of the board's point type (`CostDaevanionPoint`).
- A node gives one of:
  - **Stat**: its stats join the stat sheet (HPMax, MPMax, Critical and defense also feed the HP/MP pools and combat; other
    stats are shown by their EStat id).
  - **SkillLevel**: extra levels of a skill, used for that skill's damage and heals (the bought level is unchanged).
  - **Passive**: a permanent passive abnormal (a skill's Abnormal_Passive rows, or the abnormal itself).
- **Closing** a node costs its `ResetGold` in Kinah and returns its points. A node other opened nodes hang on stays open
  (they must stay joined to the start). **Resetting** a board closes every node but the start for the sum of their Kinah.
- **Open all** opens every node the points reach, cheapest first; **open route** opens the client's chosen path in order.
- Opening nodes counts for the achievement objectives `DaevanionNodeActivate`, `...Grade` and `...Type`.

## Points

- From the level: Exp.dat's `DaevanionPointMap` (the client knows these itself). `Aion:Daevanion:LevelPoints` says how the
  map reads: `auto` (default: cumulative when the values never fall from level to level, else summed per level),
  `cumulative`, or `perlevel`. Check this once the real table is in: the board window's total must match the server's.
- From items: `GetDaevanionPoint` adds points, `Fix_DaevanionPoint` raises them to a fixed amount. They are kept per
  character.
- Capped by DaevanionMaxCapacity.dat per point type.

The server sends the **used** points (`DaevanionPointUpdate_NT`) and the **bonus** on top of the level points
(`UpdateItemDaevanionPoint_NT`: item points and the config below).

## Configuration: `Aion:Daevanion` (OURS)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no board can be used. |
| `PointRate` | `1` | Multiplies the level and item points (1 = official). The extra goes out as bonus points. |
| `ExtraPoints` | `0` | Flat extra points of every type. |
| `FreeReset` | `false` | Closing nodes and resetting boards cost no Kinah. |
| `MaxBoardLevel` | `0` | Hides boards whose `NeedLevel` is above this (0 = none hidden). Use it to keep KR-only boards out at the global level cap. |
| `DisabledBoards` | `[]` | Board ids to hide. |
| `LevelPoints` | `auto` | `auto`, `cumulative` or `perlevel` (see Points). |

## Data: `tools/data/daevanion.json`

Built by `tools/daevanion_data.py` from a WorldProbe export of the installed client (the tables sit in the encrypted
archive, so the archive key is needed):

```
python tools/daevanion_data.py select research/local/<inventory>/table-inventory.json research/local/sel-daevanion.json
dotnet run --project tools/WorldProbe -c Release -- "<install>\Aion2\Content\Paks" research/local/archive-key-validated.json "<oodle dll>" research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap research/local/sel-daevanion.json research/local/daevanion-tables
python tools/daevanion_data.py build
```

Until that runs, the bundled file is a placeholder with no boards (`python tools/daevanion_data.py empty`), and the boards
window stays empty as before. The point items are also picked up by `tools/box_data.py` (`GetDaevanionPoint`,
`Fix_DaevanionPoint`) the next time `boxes.json` is rebuilt.

## Protocol (CANDIDATE unless noted)

The 0xe2 run (catalogue index + 0xdd07):

| Opcode | Message | Layout |
|---|---|---|
| 0xe226 | DaevanionBoardInfo_NT | `[{board i32, [node i32]}]` (OBSERVED empty: `00`) |
| 0xe227 / 0xe228 / 0xe229 | DaevanionOpenNode_RQ / RS / NT | `{node}` / `{result u16, node}` / `[node]` |
| 0xe22a / 0xe22b | DaevanionCloseNode_RQ / RS | `{node}` / `{result u16, node}` |
| 0xe22c / 0xe22d | DaevanionResetBoard_RQ / RS | `{board}` / `{result u16, board}` |
| 0xe22e | DaevanionPointUpdate_NT | `[{type u8, used u16}]` (OBSERVED: `05 01 0000 02 0000 03 0000 04 0000 05 0000`) |
| 0xe22f | UpdateItemDaevanionPoint_NT | bonus points, same layout |
| 0xe2ee / 0xe2ef | DaevanionOpenAllNode_RQ / RS | `{board}` / `{result u16, board, [node]}` |
| 0xe2f0 / 0xe2f1 | DaevanionOpenRouteNode_RQ / RS | `{board, [node]}` / `{result u16, board, [node]}` |

Failures use the client's EResult codes (kDaevanion_Node_* 15575-15584, kDaevanion_Board_* 15585-15590).

## Not modelled yet

- `NeedConditionType` PrevNode_Grade (a node that needs a neighbour of some grade): the value's form is unknown; the
  condition is not checked.
- Primary and deity stats (STR, Justice, ...) are shown on the stat sheet but the server has no formula turning them into
  attack or defense yet.
- A closed Passive node's abnormal stays until the next login.
- Playerbots have no boards.
