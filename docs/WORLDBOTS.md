# World bots and bot chat

World bots are characters the server plays out in the open world, the way AzerothCore's random bots fill a WoW private
server: each one has a name, a class, a race and a level, goes to a hunting ground that suits it, walks around, fights
monsters with its class rotation, loots, rests, levels up and moves on. Players see them as ordinary players. With bot chat
on they also talk in the world (or faction) channel and answer whispers, written by a language model. The official game has
no bots, so everything here is **OURS**.

World bots are different from playerbots ([PLAYERBOTS.md](PLAYERBOTS.md)): a playerbot is one of a player's own characters
that follows them and fights with them; a world bot belongs to nobody and lives on its own. Both use the same brain
(`BotBrain`, the class rotations in `tools/data/bot-rotations.json`) and the same null transport.

## Turning them on

Both are off by default. On the Linux server (see [deploy/linux/README.md](../deploy/linux/README.md)):

```bash
sudo tee -a /etc/aion/game.env >/dev/null <<'ENV'
Aion__WorldBots__Enabled=true
Aion__WorldBots__Count=60
Aion__WorldBots__MinLevel=10
# 0: up to the level cap
Aion__WorldBots__MaxLevel=0
Aion__BotChat__Enabled=true
ENV
sudo systemctl restart aion-host
journalctl -u aion-host | grep -E "World bots on|Bot chat on"
```

In game, `//bot world` shows how many are in, and `//bot world <name>` what one of them is doing.

## What a world bot does

- **The roster.** `Count` bots, made from `Seed`: names (`Names` first, then generated two- or three-syllable names), classes
  dealt so every class appears, races alternating (or one race with `Race`), starting levels spread evenly from `MinLevel`
  to `MaxLevel`, and a personality for chat. The same seed gives the same bots after every restart.
- **Unsaved.** A world bot is an unsaved character, like a development login: no account or database row. It levels while
  the server runs and starts again at its starting level after a restart. Names are checked against players in the world,
  not against stored characters, so pick a `Seed` (or `Names`) that does not collide with your friends' names.
- **Logging in.** `LoginsPerSecond` bots a second go through the client's own requests (game login, create, select, load).
- **Hunting grounds** (`HuntingGrounds`). Built at start from the map spawns: on every map that is not an instance, ordinary
  hostile monsters (no elites, bosses or flyers) bucketed into 40 m squares; a square with at least 4 is a ground, with the
  median monster level. A map belongs to the race whose friendly NPCs are there (relationship 3 Light, 4 Dark); the Abyss maps
  have both and are used only with `Abyss=true`. The embedded data gives about 3,700 grounds: maps 1000/1010/1011 (Elyos),
  1100/1110/1111 (Asmodian) and the Abyss maps 20/22/23, levels 1 to 55.
- **Choosing a ground.** Grounds of the bot's race whose median level is from `level - LevelsBelow` to `level + LevelsAbove`
  (when none fit, the nearest levels below). It stays `GroundMinutes` (±25%), then moves to another one; it also moves on once
  it outlevels its ground. A nearby ground (up to 120 m) is walked to; anything farther, or on another map, is a teleport.
- **Hunting.** Every 750 ms it looks for a monster within 22 m: alive, hostile, not an elite, from `level - LevelsBelow` to
  `level + LevelsAbove`, inside its ground, and not already fighting another player (no kill stealing). The brain then
  fights it with the class rotation exactly as a playerbot does (interrupts, dodges, area skills, heals on itself).
  Monsters that attack it are fought back whatever their level.
- **Resting.** Below 65% HP or 30% MP and out of a fight it stops and waits for the session's regeneration up to 95% HP and
  80% MP. Between walks it idles 2 to 9 seconds at a random spot of its ground.
- **Loot and experience.** Its kills go through the normal kill path: experience, levels and its own drops, which it picks up.
- **Death.** It revives at the session's revive point after the normal delay and walks or teleports back.
- **Gear** (`WorldBots.Gear`). The class weapon and seven armour pieces (helmet, shoulders, torso, pants, gloves, boots, cape):
  for each slot the item it may wear (permit level at most its level, its race or any) with the highest item level, renewed
  every 3 levels it gains. On starter gear a bot dies to monsters of its own level.
- **Toughness.** Bots take 1/`Toughness` of monster damage (default 2). It is applied as extra defense (`GameSession.BotDefense`),
  standing in for the enchantments, stones and titles a player's gear has and the bots' does not. 1 treats them exactly like
  players.

### Content bots

`WorldBots.Spawn(className, race, level, map, at, wander, name)` adds a bot for content, such as filling the sides of a
battlefield: a generated character of that class, race and level that logs in on the next tick at that spot. `wander` is the
caller's goal (`IBotWander`, below); without one it hunts around its spot. Content bots are not in the roster, never come
back after `Dismiss(bot)` or a failure, and work with `Aion:WorldBots:Enabled=false` as long as the `WorldBots` loop runs.

`IBotWander` (BotBrain.cs) is how a leaderless bot is steered: `Pull(now, me)` names a monster to start a fight with, and
`Destination(now, me)` where to walk when it has no fight, heal or loot. The brain does everything else. `WorldBotRoam` is the
hunting-ground implementation.

### Turns and cost

Each bot step runs like a player's connection: the session's timers and its moves in its map's turn (maps run in parallel),
casts and teleports in the world turn. Bot threat checks walk the map's NPC grid near the bot, not every NPC on the map (a
levelling map has about 13,000).

## Bot chat

Bot chat lets world bots talk like players, after AzerothCore's mod-ollama-chat:

- **Chatter.** Every `AmbientMinSeconds` to `AmbientMaxSeconds` a random bot says something unprompted: about what it is doing
  and where, a question, a joke, or a reaction to the recent chat.
- **Answers.** A player's message in the bots' room that names a bot gets an answer from that bot (up to two named bots). Any
  other player message gets an answer from a random bot by chance (`ReplyChance`, a question counts 1.5 times). A bot's line
  may get one answer from another bot (`BotReplyChance`), never a chain.
- **Whispers.** A whisper to a bot is answered by whisper, always. The player sees their own whisper echoed as usual.
- **Faction chat.** With `FactionOnly` (default) a bot's lines reach players of its own race only, and bots answer only
  players of their own race. Off: everyone hears every bot.
- **Timing.** Answers wait 2 to 7 seconds (typing). No bot line comes sooner than `MinGapSeconds` after the last one, except
  answers to whispers and to a bot's name. Answers older than a minute are dropped. One model call runs at a time.
- **The prompt.** The bot's name, level, race, class, personality, what it is doing and where (its subzone), the recent
  channel lines (12), and rules: one short casual line, no emojis, never say it is an AI, PG-13. `ExtraPrompt` adds server
  flavour. The reply is cleaned (first line only, no name prefix, quotes, `*actions*` or `<think>` blocks; AI disclaimers are
  refused) and cut to the chat limit of 100 characters.
- **No model.** With `Endpoint` empty, or when the model fails or times out, the bot uses a short canned line instead.

Bot lines go through `ChatRelay.Speak` / `WhisperFromBot` to the players' chat sessions and into the staff chat log. Players'
messages reach bot chat through `ChatRelay.Heard`; a listener can never break or slow chat.

### Choosing a model (free options)

Any server that speaks the OpenAI chat completions API works (`POST {Endpoint}/chat/completions`).

| Option | Cost | Setup | Notes |
|---|---|---|---|
| **Ollama on the server** (default) | Free, no key, nothing leaves the server | `curl -fsSL https://ollama.com/install.sh \| sh` then `ollama pull llama3.2:3b` | `Endpoint=http://127.0.0.1:11434/v1`. On CPU a 3B model needs about 3 GB of RAM and answers in a few seconds; `llama3.2:1b` (about 1.5 GB) for small machines; anything bigger wants a GPU. |
| Ollama on another PC (a gaming PC with a GPU) | Free | Install Ollama there, set `OLLAMA_HOST=0.0.0.0`, open port 11434 to the server only | `Endpoint=http://<that PC>:11434/v1`. Bots fall back to canned lines while it is off. |
| LM Studio or llama.cpp server | Free | Start its OpenAI-compatible server | Same settings, their port. |
| Groq, OpenRouter (`:free` models), Google Gemini | Free tiers with an account | Create an API key on their site | Put the key in the environment only. Bot chat sends about 1 to 3 requests a minute, well inside free tiers. |

Hosted examples (`/etc/aion/game.env`, which is not in the repository):

```bash
# Groq (pick a small Llama model from their model list)
Aion__BotChat__Endpoint=https://api.groq.com/openai/v1
Aion__BotChat__Model=<model id>
Aion__BotChat__ApiKey=<your key>
# OpenRouter (a model id ending in :free)
Aion__BotChat__Endpoint=https://openrouter.ai/api/v1
# Google Gemini (its OpenAI-compatible endpoint; a Flash model)
Aion__BotChat__Endpoint=https://generativelanguage.googleapis.com/v1beta/openai
```

Never put an API key in `appsettings*.json` or any file in the repository. The host logs whether a key is set, never the key.

On the Windows/WSL test setup, Ollama for Windows listens on the Windows side: set `OLLAMA_HOST=0.0.0.0` there and point the
host at the Windows address as WSL sees it (`ip route show default`, the `via` address), port 11434.

Bot chat needs the chat port (`Aion:ChatPort`, set by `tools/server_env.py`) and world bots.

## Configuration

`Aion:WorldBots` (environment: `Aion__WorldBots__<Key>`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | World bots on. |
| `Count` | `20` | Bots in the world at once. |
| `MinLevel` / `MaxLevel` | `10` / `0` | Starting levels (0: the level cap). |
| `Race` | `both` | `light`, `dark` or `both` (half each). |
| `Abyss` | `false` | Also hunt on the shared Abyss maps (players of the other race can attack them there). |
| `Maps` | (all) | Only these maps, e.g. `Aion__WorldBots__Maps__0=1010`. |
| `Seed` | `1204` | The roster's seed. |
| `Names` | (none) | Names to use first, e.g. `Aion__WorldBots__Names__0=Kaelen`. |
| `GroundMinutes` | `12` | Minutes at one hunting ground (±25%). |
| `LoginsPerSecond` | `4` | Logins a second while the roster fills. |
| `Toughness` | `2` | Bots take 1/Toughness of monster damage. |
| `LevelsBelow` / `LevelsAbove` | `6` / `0` | The monster levels a bot hunts, relative to its own. |

`Aion:BotChat` (environment: `Aion__BotChat__<Key>`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Bot chat on (needs world bots and the chat port). |
| `Endpoint` | `http://127.0.0.1:11434/v1` | OpenAI-compatible base URL; empty: canned lines only. |
| `Model` | `llama3.2:3b` | The model name the endpoint knows. |
| `ApiKey` | (empty) | Bearer key for hosted providers. Environment only. |
| `TimeoutSeconds` / `MaxTokens` / `Temperature` | `30` / `60` / `0.9` | The model call. |
| `Room` | `WORLD` | `WORLD` (the server's global channel) or `REGION` (the common region channel). |
| `FactionOnly` | `true` | Bots talk to and answer their own race only. |
| `AmbientMinSeconds` / `AmbientMaxSeconds` | `90` / `300` | Time between unprompted lines. |
| `ReplyChance` / `BotReplyChance` | `0.35` / `0.2` | Chance of an answer to a player's message (naming no bot), and to a bot's line. |
| `TypingMinSeconds` / `TypingMaxSeconds` | `2` / `7` | The wait before an answer. |
| `MinGapSeconds` | `4` | The least time between two bot lines (answers to whispers and names excepted). |
| `ExtraPrompt` | (empty) | Added to every bot's instructions. |

## How many bots

`tests/Aion.Bots.Bench` runs N world bots (and idle players) in one shared world with the embedded data, at the real 50 ms tick
in simulated time, and reports what a tick costs:

```bash
dotnet run -c Release --project tests/Aion.Bots.Bench -- 25 50 100 200 300 400 --seconds 120 --players 10
```

Results on a 4-vCPU cloud container (2026-11-09; no terrain or navmesh files there, so movement is straight lines):

| Bots | Players | ms per 50 ms tick (avg) | p99 | Bot loop share of one core | Managed heap | Kills/min | Deaths/min |
|---|---|---|---|---|---|---|---|
| 25 | 10 | 2.4 | 8.9 | 5% | 231 MB | 84 | 8 |
| 50 | 10 | 3.0 | 8.9 | 6% | 236 MB | 190 | 11 |
| 100 | 10 | 5.9 | 11.6 | 12% | 232 MB | 366 | 22 |
| 200 | 10 | 12.6 | 21.7 | 25% | 236 MB | 738 | 40 |
| 300 | 10 | 19.6 | 35.6 | 39% | 240 MB | 1,026 | 62 |
| 400 | 10 | 26.8 | 42.8 | 54% | 241 MB | 1,356 | 86 |

What it means:

- A world bot costs about 0.07 ms of one core per 50 ms tick, about 1.3 ms of CPU a second. The bot loop is one thread, so
  it must stay well under 50 ms a tick: about 400 bots is the practical ceiling of one host today, and 600+ would need the
  loop split per map.
- Memory hardly grows with bots (the embedded game data is most of the heap); a few hundred bots add tens of megabytes.
- With the real navmesh and terrain files, paths cost more: plan on about twice the numbers above.
- Players cost more than bots (their connections, encryption, more messages), but 5 to 10 friends are small next to this.

Hardware guidance for a friends server (5 to 10 players):

| Setup | World bots | With local LLM chat (Ollama) |
|---|---|---|
| 2 cores, 4 GB | 50 | Not advised; use a hosted free tier, or `llama3.2:1b` with slow answers |
| 4 cores, 8 GB | 100 to 150 | `llama3.2:3b` on CPU, answers in about 2 to 6 s |
| 6 to 8 cores, 16 GB | 200 to 300 | `llama3.2:3b`, or an 8B model with patience |
| Any, plus a GPU (6 GB+) | as above | 8B models answer in under a second |

Plus PostgreSQL and the host's own memory (about 1 to 2 GB with the data loaded). The model is the heavy part of bot chat:
it runs one call at a time and bots fall back to canned lines when it is busy or slow.

## Known limits

- **Appearance.** Bots are made like development characters, with the minimal customizing data the playerbot checks use. They
  show the race's default look; whether every client renders them without trouble needs a live test.
- **Not saved.** Levels reset at a restart (see above).
- **Open world only.** No quests, dungeons, gathering, crafting, guilds or PvP on their own. They never join players' parties.
- **Balance past level 30.** Monster HP (and so their hits) grows about sevenfold from level 30 to 45 (`Combat.HpForLevel`), much
  faster than player HP and unenchanted gear. Even with Toughness 2, bots above 30 die every few minutes in the benchmark; bots
  below 30 rarely. Players with ordinary gear will feel the same.
- **Chat.** Bots cannot see items, maps or quests, so the model may be vague about game facts. It is told to say it does not
  know rather than invent things, but small models still do sometimes.

## Code

| File | What it holds |
|---|---|
| `server/Aion.World/WorldBots.cs` | `WorldBotOptions`, `WorldBot`, `WorldBots` (roster, login, the loop, gear, toughness, content bots, chat speakers), `WorldBotRoam` |
| `server/Aion.World/HuntingGrounds.cs` | Hunting grounds from the spawns, race maps, the level fit |
| `server/Aion.World/BotChat.cs` | `BotChatOptions`, `IChatModel`, `OpenAiChatModel`, `BotChatter` (when to talk, prompts, cleaning, canned lines) |
| `server/Aion.World/BotBrain.cs` | `IBotWander` and `BotBrain.Wander` |
| `server/Aion.World/GameSession.Bots.cs` | `Race`, `BotDefense`, `EquipUnsaved` |
| `server/Aion.Application/Chat/ChatRelay.cs` | `Heard`, `IsBot`, `Speak`, `WhisperFromBot`, `Clip` |
| `server/Aion.Host/Native/WorldBotsService.cs` | The world bot loop and the bot chat service; `Program.cs` binds `Aion:WorldBots` and `Aion:BotChat` |
| `server/Aion.World/GmCommands.cs` | `//bot world [name]` |
| `tests/Aion.Native.Checks/WorldBotChecks.cs` | The checks (40) |
| `tests/Aion.Bots.Bench` | The capacity benchmark |
