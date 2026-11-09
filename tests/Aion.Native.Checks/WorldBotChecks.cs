using Aion.Application.Chat;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>World bots (docs/WORLDBOTS.md): hunting grounds, the roster, auto gear, roaming and fighting in a shared world, and bot chat.</summary>
static class WorldBotChecks
{
    sealed class FakeModel(Func<IReadOnlyList<(string Role, string Content)>, string?> answer) : IChatModel
    {
        public List<IReadOnlyList<(string Role, string Content)>> Calls { get; } = [];
        public Task<string?> CompleteAsync(IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct)
        {
            Calls.Add(messages);
            return Task.FromResult(answer(messages));
        }
    }

    sealed class GoTo(Vector3d goal) : IBotWander
    {
        public Vector3d Goal => goal;
        public Target? Pull(long now, Vector3d me) => null;
        public Vector3d? Destination(long now, Vector3d me) => goal;
    }

    sealed class Directory(Dictionary<Guid, ChatCharacter> characters) : IChatDirectory
    {
        public Task<IReadOnlyList<(ChatCharacter Character, string WireId)>> InWorldAsync(Guid account, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(ChatCharacter, string)>>([]);
        public ChatCharacter? Current(Guid character, Guid account) => characters.GetValueOrDefault(account);
    }

    public static async Task<int> RunAsync(GameData data, Action<bool, string> check)
    {
        var count = 0;
        void Check(bool condition, string name) { check(condition, name); count++; }

        // Hunting grounds: the race maps from their friendly NPCs, the Abyss shared, clusters of ordinary hostile monsters.
        var grounds = HuntingGrounds.Embedded;
        Check(grounds.Any(g => g.Map == 1010 && g.Race == 1) && grounds.Any(g => g.Map == 1110 && g.Race == 2) && grounds.Where(g => g.Map == 20).All(g => g.Race == 0),
            "hunting grounds: Light maps (1010), Dark maps (1110), the Abyss shared (20)");
        Check(grounds.All(g => g.Monsters >= HuntingGrounds.MinMonsters && g.MinLevel <= g.Level && g.Level <= g.MaxLevel), "every ground has enough monsters and a median level");
        var fit = HuntingGrounds.For(grounds, 2, 30, shared: false, maps: null, below: 6, above: 0);
        Check(fit.Count > 5 && fit.All(g => g.Race == 2 && g.Level is >= 24 and <= 30), "a level 30 Asmodian gets Dark grounds of levels 24-30");
        Check(HuntingGrounds.For(grounds, 1, 30, false, [1011], 6, 0) is { Count: > 0 } far && far.All(g => g.Map == 1011),
            "Aion:WorldBots:Maps limits the maps (no fitting level there: the nearest levels)");

        // The roster: the seed decides it, every class appears, races alternate, levels spread.
        var options = new WorldBotOptions { Enabled = true, Count = 16, MinLevel = 10, MaxLevel = 40, Seed = 3, LoginsPerSecond = 100 };
        var roster = WorldBots.Roster(options, data);
        var again = WorldBots.Roster(options, data);
        Check(roster.Select(b => (b.Name, b.ClassName, b.StartLevel)).SequenceEqual(again.Select(b => (b.Name, b.ClassName, b.StartLevel))), "the same seed gives the same roster");
        Check(roster.Select(b => b.ClassName).Distinct().Count() == 8 && roster.Count(b => b.Race == 1) == 8 && roster.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 16,
            "every class appears, half Elyos and half Asmodian, unique names");
        Check(roster.Min(b => b.StartLevel) == 10 && roster.Max(b => b.StartLevel) == 40, "starting levels spread from MinLevel to MaxLevel");
        Check(WorldBots.Roster(options with { Names = ["Brandy", "Gm Bob", "Zed"] }, data).Take(2).Select(b => b.Name).SequenceEqual(["Brandy", "Zed"]),
            "configured names first (invalid ones skipped)");
        Check(Throws(() => new WorldBotOptions { Race = "orc" }.Validated()) && Throws(() => new WorldBotOptions { MinLevel = 20, MaxLevel = 10 }.Validated()),
            "Aion:WorldBots is validated");

        // Auto gear: the class weapon and every armour slot, worn at the level.
        var gear = WorldBots.Gear(data, "Templar", 1, 30);
        Check(gear.Count == 8 && gear.Any(g => g.Slot == 1 && data.Item(g.ItemId)!.Category == data.ClassWeaponType("Templar"))
              && gear.All(g => data.Item(g.ItemId)!.PermitLevel <= 30 && data.Item(g.ItemId)!.Race is 0 or 1), "auto gear: the class weapon and seven armour pieces the bot may wear");

        // In a shared world: bots log in through the client's requests, go to their grounds, fight, and others see them.
        var world = new SharedWorld(data);
        var presence = new WorldPresence();
        var bots = new WorldBots(data, options with { Count = 4, MinLevel = 12, MaxLevel = 18 }, world, new GameSessionOptions { World = world }, presence: presence);
        long t = 1_900_000_000_000;
        for (var k = 0; k < 4; k++) await bots.TickAsync(t += 50);   // LoginsPerSecond 100: one every 10 ms, so one per tick here
        Check(bots.InWorld.Count == 4 && bots.InWorld.All(b => b.Session!.InWorld && b.Session.Bot && presence.Find(b.Name)?.Session == b.Session),
            "world bots log in (0x3613, 0x361b, 0x361f, 0x3622) and are in the world");
        Check(bots.InWorld.All(b => b.Session!.MapId == b.Roam!.Ground!.Map && b.Roam.Ground.Race == b.Race
              && Math.Abs(b.Roam.Ground.Level - b.Level) <= options.LevelsBelow), "each is at a hunting ground of its race and level");
        Check(bots.InWorld.All(b => b.Session!.Defense > 1000 && b.Session.BotDefense > 0), "auto gear and Toughness raise their defense");
        var watched = bots.InWorld[0];
        var watcher = new GameSession(data, new GameSessionOptions { World = world, CharacterLevel = watched.Level, DevelopmentMap = watched.Session!.MapId }, new Random(1));
        await watcher.HandleAsync(BotRequests.GameLogin("watcher"), t);
        await watcher.HandleAsync(BotRequests.Create("Watcher", watched.Race == 2 ? 9 : 1), t);
        await watcher.HandleAsync(BotRequests.Select(65536), t);
        await watcher.HandleAsync(BotRequests.LoadComplete(1), t);
        watcher.Teleport(watched.Session.MapId!.Value, watched.Session.WorldPosition!.Value, t);
        await watcher.HandleAsync(BotRequests.LoadComplete(1), t);
        var seen = await watcher.TickAsync(t);
        Check(seen.Any(b => b.Length > 2 && (b[0] | b[1] << 8) == 0x3645), "a player near a world bot sees it appear (0x3645)");
        var levels = bots.InWorld.ToDictionary(b => b.Name, b => b.Level);
        var relayed = 0;
        for (var i = 0; i < 20 * 120; i++)
        {
            await bots.TickAsync(t += 50);
            if (i % 10 == 0) relayed += (await watcher.TickAsync(t)).Count(b => b.Length > 2 && (b[0] | b[1] << 8) is 0x371c or 0x3802);
        }
        var live = bots.InWorld;
        Check(live.Sum(b => b.Brain!.CastsAccepted) > 20 && live.All(b => b.Brain!.CastsSent == b.Brain.CastsAccepted),
            $"in two minutes they fight with their rotations, every cast accepted ({live.Sum(b => b.Brain!.CastsAccepted)} casts)");
        Check(live.Sum(b => b.Brain!.MovesSent) > 20 && live.All(b => b.Session!.SpeedRejects == 0), "they walk around without a speed reject");
        Check(relayed > 0, "the player sees bots run or cast (0x371c, 0x3802)");
        Check(live.All(b => b.Level >= levels[b.Name]) && live.Any(b => b.Session!.Experience > 0 || b.Level > levels[b.Name]), "kills give them experience");
        Check(bots.Summary().StartsWith("World bots: 4/4 in the world") && bots.Speakers().Count == 4 && bots.IsBot(live[0].Name.ToUpperInvariant()),
            "//bot world summary, the speakers chat sees, bot names for whispers");
        await bots.StopAsync();
        Check(bots.InWorld.Count == 0 && world.Online.Count == 1 && presence.Find(live[0].Name) is null, "at shutdown they leave the world");
        watcher.ExitSharedWorld();

        // Content bots (PvP fillers): a class, race, level and spot, the caller's wander, with Aion:WorldBots off.
        var contentWorld = new SharedWorld(data);
        var content = new WorldBots(data, new WorldBotOptions(), contentWorld, new GameSessionOptions { World = contentWorld });
        var spot = grounds.First(g => g.Map == 1110 && g.Level is >= 20 and <= 30);
        var goal = new GoTo(spot.Centre with { X = spot.Centre.X + 1500 });
        var filler = content.Spawn("Ranger", 2, 25, spot.Map, spot.Centre, _ => goal, name: "Filler");
        Check(content.All.Count == 1 && filler.Content && filler.State == WorldBot.Phase.Pending, "Spawn queues a content bot (no roster with Aion:WorldBots off)");
        await content.TickAsync(t += 50);
        Check(filler.State == WorldBot.Phase.InWorld && filler.Session!.ClassName == "Ranger" && filler.Session.Race == 2 && filler.Level == 25 && filler.Session.MapId == spot.Map
              && filler.Brain!.Wander == goal, "it enters at once at its spot with its class, race and level, and follows the caller's wander");
        // Up to a minute: a monster on the way, a fight or its loot may come first.
        for (var i = 0; i < 1200 && Math.Abs(filler.Session!.WorldPosition!.Value.X - goal.Goal.X) >= 300; i++) await content.TickAsync(t += 50);
        Check(Math.Abs(filler.Session!.WorldPosition!.Value.X - goal.Goal.X) < 300 || filler.Brain!.FightingKey is not null,
            $"it walks to the wander's goal ({filler.Session.WorldPosition}, {filler.Activity})");
        content.Dismiss(filler);
        await content.TickAsync(t += 50);
        Check(filler.State == WorldBot.Phase.Gone && content.All.Count == 0 && contentWorld.Online.Count == 0, "Dismiss takes it out of the world for good");

        // Bot chat: the prompt, cleaning the model's text, when bots talk, faction chat, whispers.
        Check(BotChatter.Clean("Kael: \"hey guys *waves* anyone up for a run?\"\nsecond line", "Kael") == "hey guys anyone up for a run?", "model text: name prefix, quotes, actions and extra lines removed");
        Check(BotChatter.Clean("<think>plan</think>sure thing", "X") == "sure thing" && BotChatter.Clean("As an AI language model I cannot", "X") is null
              && BotChatter.Clean(new string('a', 300), "X")!.Length == 100, "thinking tags dropped, AI disclaimers refused, cut to 100 characters");
        Check(BotChatter.PlaceName("AltamiaHighland_Eastern_Subzone") == "Altamia Highland Eastern" && BotChatter.PlaceName("L2_EironPlains_Subzone") == "Eiron Plains",
            "subzone names read like places");
        var clock = 0L;
        var speakers = new List<BotSpeaker>
        {
            new("Kael", 1, "Templar", 30, "sarcastic", "fighting monsters", "Eiron Plains"),
            new("Mira", 2, "Cleric", 22, "cheerful newbie", "resting after a fight", null),
        };
        var model = new FakeModel(m => m[1].Content.Contains("whispers") ? "sure, meet me at the gate" : "Kael: lol the mobs here hit hard");
        var chatOptions = new BotChatOptions { Enabled = true, ReplyChance = 0, BotReplyChance = 0, TypingMinSeconds = 1, TypingMaxSeconds = 1, AmbientMinSeconds = 60, AmbientMaxSeconds = 60 };
        var chatter = new BotChatter(chatOptions, () => speakers, model, new Random(4), () => clock);
        var prompt = chatter.Prompt(speakers[0], new ChatHeard("WORLD", "Brandon", "kael where are you?", 1), "reply");
        Check(prompt[0].Content.Contains("level 30 Elyos Templar") && prompt[0].Content.Contains("never say you are an AI") && prompt[1].Content.Contains("kael where are you?"),
            "the prompt: who the bot is, the rules, the message to answer");
        chatter.Hear(new ChatHeard("WORLD", "Brandon", "hey kael, any good drops?", 1));
        Check((await chatter.TickAsync(default)).Count == 0, "an answer waits for its typing time");
        clock += 1000;
        var said = await chatter.TickAsync(default);
        Check(said is [{ Name: "Kael", Text: "lol the mobs here hit hard", WhisperTo: null }] && model.Calls.Count == 1, "a player naming a bot gets an answer from it, cleaned");
        chatter.Hear(new ChatHeard("WORLD", "Brandon", "hey mira", 1));
        clock += 5000;
        Check((await chatter.TickAsync(default)).Count == 0, "faction chat: an Asmodian bot does not hear an Elyos player");
        var room = new ChatRoomKey("WORLD", "1204", "1204");
        chatter.Hear(new ChatHeard("WHISPER", "Brandon", "want to group?", 1, "mira", room, "NORMAL"));
        clock += 1000;
        Check(await chatter.TickAsync(default) is [{ Name: "Mira", WhisperTo: "Brandon", Text: "sure, meet me at the gate" } w] && w.WhisperRoom == room, "a whisper to a bot is answered by whisper");
        clock += 61_000;
        var ambient = await chatter.TickAsync(default);
        Check(ambient.Count == 1 && model.Calls[^1][1].Content.Contains("Say something"), "now and then a bot says something unprompted");
        var canned = new BotChatter(chatOptions with { Endpoint = "" }, () => speakers, null, new Random(1), () => clock);
        canned.Hear(new ChatHeard("WORLD", "Brandon", "kael?", 1));
        clock += 1000;
        Check(await canned.TickAsync(default) is [{ Name: "Kael", Text.Length: > 0 }], "without a model the bots use canned lines");
        Check(Throws(() => new BotChatOptions { Room = "GUILD" }.Validated()) && Throws(() => new BotChatOptions { ReplyChance = 2 }.Validated()), "Aion:BotChat is validated");

        // The relay: bots speak in the joined rooms (faction filtered), whispers to bots are echoed and heard.
        var light = Guid.NewGuid();
        var dark = Guid.NewGuid();
        var relay = new ChatRelay(TimeProvider.System, new Directory(new()
        {
            [light] = new ChatCharacter(Guid.NewGuid(), "Lumi", "Lumi", 0, 1),
            [dark] = new ChatCharacter(Guid.NewGuid(), "Dusk", "Dusk", 0, 2),
        }));
        var lumi = relay.Login(new ChatIdentity("1", "1204", "en_US", light, new ChatCharacter(Guid.NewGuid(), "Lumi", "Lumi", 0, 1)));
        var dusk = relay.Login(new ChatIdentity("2", "1204", "en_US", dark, new ChatCharacter(Guid.NewGuid(), "Dusk", "Dusk", 0, 2)));
        relay.Join(lumi, room);
        relay.Join(dusk, room);
        var both = relay.Speak("Kael", "WORLD", "hello all");
        var faction = relay.Speak("Kael", "WORLD", "hello elyos", race: 1);
        Check(both.Count == 2 && faction.Count == 1 && faction[0].SessionToken == lumi && faction[0].Notification.UserName == "Kael" && faction[0].Notification.Content == "hello elyos",
            "a bot speaks to the room; faction chat reaches its own race only");
        var heard = new List<ChatHeard>();
        relay.Heard += heard.Add;
        relay.Send(lumi, room, new ChatMessage("Lumi", "PUBLISH", "NORMAL", "anyone around?", ""));
        Check(heard is [{ RoomType: "WORLD", Speaker: "Lumi", Race: 1, Content: "anyone around?" }], "players' room messages are heard, with their race");
        relay.IsBot = name => name.Equals("Kael", StringComparison.OrdinalIgnoreCase);
        var (_, echo) = relay.Whisper(lumi, room, new ChatMessage("Lumi", "PUBLISH", "NORMAL", "psst", "", TargetCharacterName: "Kael"));
        Check(echo.Count == 1 && echo[0].SessionToken == lumi && heard[^1] is { WhisperTo: "Kael", Speaker: "Lumi" }, "a whisper to a bot echoes to the sender and is heard");
        var back = relay.WhisperFromBot("Kael", "Lumi", "hi there");
        Check(back.Count == 1 && back[0].SessionToken == lumi && back[0].Notification.UserName == "Kael" && back[0].Notification.ReceiverUserName == "Lumi", "the bot whispers back");
        Check(ChatRelay.Clip("a\u0001b " + new string('x', 120)).Length <= 100 && !ChatRelay.Clip("a\u0001b").Contains('\u0001'), "bot text keeps to the chat rules");
        return count;
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
