"""Boss phases and hand-made waves (OURS), built from the client's own dungeon and NPC data.

The client has no boss AI tables: its MapEvent scripts spawn, talk, open doors and start waves, and NpcData lists each
NPC's skills (Skill.dat range, cooldown, cast, damage). The official bosses' phases are run by server code we have never
seen, so everything this file decides is ours, built by role and mode from what the data does say:

  * which bosses: the Season 1 Expeditions (Krao Cave, Urugugu Canyon, Fire Temple, Draupnir Cave, Vakron Sky Island,
    Ferocious Horn Den: Easy, Normal and Hard), the Transcendence dungeons (Deus Research Base, Shattered Arkanis) and the
    raid Abyssal Forge: Ludra (ReforgedAbyss_Normal). A "final" boss is the dungeon's reward-cube boss
    (map-events.json dungeons[].cube_boss); a "mid" boss is another HeroMonster with at least MID_MIN_SKILLS skills;
  * the slam (a telegraphed area attack): the boss's own longest-cast damaging skill (KraoCave_Boss_1's 8 s 1800140),
    winding up for its own cast time; a boss whose skills all cast fast gets its basic attack with a 3 s wind-up and
    3x damage. Every player within the radius when it lands is hit; walking out is the counter;
  * the adds: the most common mobile monster of the same dungeon map;
  * the frenzy: Expedition hard modes only, 4 minutes into the fight (global's hard-mode rule since Oct 21, 2026);
  * the raid's waves are the client's own (spawn entries "{wave}_{n}"). Fierce Horn Den's totem wave names no spawns,
    so WAVES makes one: copies of the den's Lycan come around the totem while it lives.

Rebuild with `python tools/boss_phases.py`; `--check` fails when tools/data/boss-phases.json is stale.
Change the rules here (ROLES, MODE, NAMES), not the JSON.
"""
import json
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
OUT = ROOT / 'tools/data/boss-phases.json'

EXPEDITIONS = {
    'KraoCave': 'Krao Cave', 'Urugugu_Canyon': 'Urugugu Canyon', 'Fire_Temple': 'Fire Temple', 'Draupnir': 'Draupnir Cave',
    'BakronIsland': 'Vakron Sky Island', 'HornCave': 'Ferocious Horn Den',
}
TRANSCENDENCE = {'DeusCenter_G': 'Deus Research Base', 'Arkanis_G': 'Shattered Arkanis'}
RAID = 'ReforgedAbyss_Normal'
MID_MIN_SKILLS = 6
# Proper names the spawn entries carry (the rest are called after their dungeon).
NAMES = {
    'Bakarma': 'Bakarma', 'Lakshmi': 'Lakshmi', 'Saraswati': 'Saraswati', 'Rotan': 'Rotan', 'Ferk': 'Ferk',
    'KalnifElite': 'the Kalnif chieftain', 'Lycan_04': 'the Lycan alpha', 'Lagta': 'Lagta', 'Kashapa': 'Kashapa',
    'Durbati': 'Durbati',
}
# Per mode: slam interval and radius of a final boss, adds per call, the extra phases.
MODE = {
    'Easy': {'every': 30000, 'radius': 500, 'adds': 2, 'enrage': False, 'second_adds': 0, 'frenzy': None},
    'Normal': {'every': 24000, 'radius': 600, 'adds': 3, 'enrage': True, 'second_adds': 0, 'frenzy': None},
    'Hard': {'every': 18000, 'radius': 600, 'adds': 4, 'enrage': True, 'second_adds': 2, 'frenzy': 240000},
}
SLOW_CAST_MS = 1500        # a skill casting at least this long can be a slam
FALLBACK_WIND_UP_MS = 3000  # the basic attack as a slam
FALLBACK_SLAM_DAMAGE = 3.0
SLAM_FLOOR = 2.5            # a slam hits at least this many basic attacks


def load():
    return json.loads(MAP_EVENTS.read_text(encoding='utf-8')), json.loads(WORLD_NPCS.read_text(encoding='utf-8'))


def label(spawn_name, place):
    for key, name in NAMES.items():
        if key in spawn_name:
            return name
    return f'The {place} boss'


def mean_damage(skill):
    return (skill['damage_min'] + skill['damage_max']) / 2


def slam_for(template, npc_skills, every, radius, who):
    """The boss's slam: its longest-cast damaging skill, else its basic attack wound up (3 s, 3x)."""
    skills = [(sid, npc_skills.get(str(sid))) for sid in template['skills']]
    skills = [(sid, s) for sid, s in skills if s and s['damage_max'] > 0]
    warning = f'{who} gathers power: get out of reach!'
    if not skills:
        # No damaging skill in the extracted data (Durbati's only skill has no damage row): its first skill's animation
        # and a hit of FALLBACK_SLAM_DAMAGE times the server's base NPC hit.
        if not template['skills']:
            return None
        return {'skill': template['skills'][0], 'every_ms': every, 'wind_up_ms': FALLBACK_WIND_UP_MS, 'radius': radius,
                'damage': FALLBACK_SLAM_DAMAGE, 'warning': warning}
    basic_id, basic = skills[0]
    slow = [(sid, s) for sid, s in skills[1:] if s['cast'] >= SLOW_CAST_MS]
    if slow:
        sid, s = max(slow, key=lambda x: (x[1]['cast'], mean_damage(x[1]), -x[0]))
        damage = round(max(1.0, SLAM_FLOOR * mean_damage(basic) / max(1, mean_damage(s))), 2)
        return {'skill': sid, 'every_ms': every, 'wind_up_ms': s['cast'], 'radius': radius, 'damage': damage, 'warning': warning}
    return {'skill': basic_id, 'every_ms': every, 'wind_up_ms': FALLBACK_WIND_UP_MS, 'radius': radius,
            'damage': FALLBACK_SLAM_DAMAGE, 'warning': warning}


def add_npc(spawns, templates, prefer=None):
    """The most common mobile hostile monster of the map (not a boss), optionally among names containing `prefer`."""
    counts = Counter()
    for s in spawns:
        t = templates.get(str(s[1]))
        if not t or t['sub_type'] not in ('NormalMonster', 'EliteMonster') or t['run_speed'] <= 0 or t['relationship'] != 6:
            continue
        if prefer and prefer not in s[0]:
            continue
        counts[s[1]] += 1
    if not counts:
        return None
    return max(counts.items(), key=lambda kv: (kv[1], -kv[0]))[0]


def final_script(name, dungeon, who, template, npc_skills, mode, adds):
    m = MODE[mode]
    slam = slam_for(template, npc_skills, m['every'], m['radius'], who)
    phases = []
    if adds:
        phases.append({'at': 70, 'name': 'call', 'adds': {'npc': adds, 'count': m['adds'], 'radius': 500},
                       'notice': f'{who} calls for help!'})
    faster = dict(slam, every_ms=int(slam['every_ms'] * 0.75)) if slam else None
    phases.append({'at': 40, 'name': 'fury', 'damage': 1.15, **({'telegraph': faster} if faster else {}),
                   **({'adds': {'npc': adds, 'count': m['second_adds'], 'radius': 500}} if adds and m['second_adds'] else {}),
                   'notice': f'{who} grows furious.'})
    if m['enrage']:
        phases.append({'at': 20, 'name': 'enrage', 'damage': 1.3, 'haste': 0.8, 'notice': f'{who} is enraged!'})
    script = {'name': name, 'dungeon': dungeon, 'role': 'final', **({'telegraph': slam} if slam else {}), 'phases': phases}
    if m['frenzy']:
        script['frenzy'] = {'after_ms': m['frenzy'], 'damage': 3.0, 'haste': 0.5, 'notice': f'{who} goes into a frenzy!'}
    return script


def mid_script(name, dungeon, who, template, npc_skills):
    slam = slam_for(template, npc_skills, 30000, 450, who)
    script = {'name': name, 'dungeon': dungeon, 'role': 'mid', 'phases': [{'at': 50, 'name': 'fury', 'damage': 1.2}]}
    if slam:
        script['telegraph'] = slam
    return script


def raid_scripts(spawns, templates, npc_skills):
    out = {}
    adds = add_npc(spawns, templates)
    for s in spawns:
        t = templates.get(str(s[1]))
        if not t or t['sub_type'] != 'HeroMonster' or str(s[1]) in out:
            continue
        who = label(s[0], 'Abyssal Forge')
        if 'Durbati' in s[0]:
            slam = slam_for(t, npc_skills, 20000, 900, who)
            out[str(s[1])] = {'name': s[0], 'dungeon': RAID, 'role': 'raid_final', 'telegraph': slam, 'phases': [
                {'at': 75, 'name': 'call', 'adds': {'npc': adds, 'count': 4, 'radius': 700}, 'notice': f'{who} calls the forge guards!'},
                {'at': 50, 'name': 'fury', 'damage': 1.15, 'adds': {'npc': adds, 'count': 4, 'radius': 700},
                 'telegraph': dict(slam, every_ms=15000), 'notice': f'{who} grows furious.'},
                {'at': 25, 'name': 'enrage', 'damage': 1.35, 'haste': 0.8, 'notice': f'{who} is enraged!'}]}
        elif 'Lagta' in s[0] or 'Kashapa' in s[0]:
            slam = slam_for(t, npc_skills, 22000, 700, who)
            out[str(s[1])] = {'name': s[0], 'dungeon': RAID, 'role': 'raid', **({'telegraph': slam} if slam else {}), 'phases': [
                {'at': 60, 'name': 'call', 'adds': {'npc': adds, 'count': 3, 'radius': 600}, 'notice': f'{who} calls for help!'},
                {'at': 30, 'name': 'enrage', 'damage': 1.25, 'haste': 0.85, 'notice': f'{who} is enraged!'}]}
        # The Orcus Gem is the script's own encounter (its waves at 90/60/30 % HP): no phases of ours.
    return out


def waves(events, world):
    """Hand-made waves for StartWave names of the Season 1 maps that no spawn entry "{wave}_{n}" carries."""
    out = {}
    for name, event in events['events'].items():
        room = events['dungeons'].get(str(event['map']))
        if not room or not room['name'].startswith('HornCave'):
            continue
        spawns = world['maps'].get(str(event['map']), [])
        names = {s[0] for s in spawns}
        for stage in event['stages']:
            anchor = next((c['v'][0] for group in stage.get('fail') or [] for c in group if c['t'] == 'NpcCombatState'), None)
            for step in stage['steps']:
                for o in step['obj']:
                    if o['t'] != 'ClearWave' or not anchor:
                        continue
                    wave = o['v'][0]
                    if any(re.fullmatch(re.escape(wave) + r'_\d+', n) for n in names):
                        continue
                    npc = add_npc(spawns, world['templates'], prefer='Lycan')
                    # The Easy and Hard scripts name the totem "..._001_Easy" / "..._001_hard"; the spawn entry is "..._001".
                    around = anchor
                    while around not in names and '_' in around:
                        around = around.rsplit('_', 1)[0]
                    if around not in names:
                        continue
                    out[wave] = {'map': event['map'], 'around': around, 'npc': npc, 'count': 3, 'every_ms': 15000, 'pulses': 6, 'radius': 600}
    return dict(sorted(out.items()))


def build(events, world):
    templates, npc_skills = world['templates'], world['npc_skills']
    bosses = {}
    for room in sorted(events['dungeons'].values(), key=lambda r: r['map']):
        name = room['name']
        family = next((f for f in EXPEDITIONS if name.startswith(f + '_')), None)
        transcendence = next((f for f in TRANSCENDENCE if name.startswith(f + '_')), None)
        spawns = world['maps'].get(str(room['map']), [])
        if name == RAID:
            bosses.update(raid_scripts(spawns, templates, npc_skills))
            continue
        if family:
            mode = name.rsplit('_', 1)[1]
            place = EXPEDITIONS[family]
        elif transcendence:
            mode, place = 'Normal', TRANSCENDENCE[transcendence]
        else:
            continue
        if mode not in MODE:
            continue
        adds = add_npc(spawns, templates)
        for s in spawns:
            t = templates.get(str(s[1]))
            if not t or t['sub_type'] != 'HeroMonster' or str(s[1]) in bosses:
                continue
            who = label(s[0], place)
            if s[1] == room.get('cube_boss'):
                bosses[str(s[1])] = final_script(s[0], name, who, t, npc_skills, mode, adds)
            elif family and len(t['skills']) >= MID_MIN_SKILLS:
                bosses[str(s[1])] = mid_script(s[0], name, who, t, npc_skills)
    return {
        'source': 'OURS: tools/boss_phases.py (the rules are in its docstring). Bosses, skills, cast times and adds are the '
                  "client's own (map-events.json, world-npcs.json); the phases, slam radii, timings and multipliers are ours, "
                  "except the hard modes' 4-minute frenzy (global's rule). Ludra's waves are the client's own spawn entries.",
        'bosses': dict(sorted(bosses.items(), key=lambda kv: int(kv[0]))),
        'waves': waves(events, world),
    }


def validate(data, world):
    """Problems: a boss or add that is not a placed NPC, a slam skill the boss does not have, phases out of order."""
    problems = []
    templates = world['templates']
    for npc, script in data['bosses'].items():
        t = templates.get(npc)
        if t is None:
            problems.append(f'{npc}: not an NpcData id')
            continue
        slams = [script.get('telegraph')] + [p.get('telegraph') for p in script.get('phases', [])]
        for slam in filter(None, slams):
            if slam['skill'] not in t['skills']:
                problems.append(f"{npc}: slam skill {slam['skill']} is not one of its skills")
        ats = [p['at'] for p in script.get('phases', [])]
        if ats != sorted(ats, reverse=True) or any(not 0 < a < 100 for a in ats):
            problems.append(f'{npc}: phase thresholds {ats} must fall from below 100')
        for p in script.get('phases', []):
            if p.get('adds') and str(p['adds']['npc']) not in templates:
                problems.append(f"{npc}: add {p['adds']['npc']} is not an NpcData id")
    for wave, w in data['waves'].items():
        if str(w['npc']) not in templates:
            problems.append(f'wave {wave}: {w["npc"]} is not an NpcData id')
        if w['around'] not in {s[0] for s in world['maps'].get(str(w['map']), [])}:
            problems.append(f"wave {wave}: {w['around']} is not a spawn entry of map {w['map']}")
    return problems


def text(data):
    return json.dumps(data, indent=2, ensure_ascii=False) + '\n'


def main(argv):
    events, world = load()
    data = build(events, world)
    problems = validate(data, world)
    if problems:
        print('\n'.join(problems))
        return 1
    if '--check' in argv:
        if not OUT.exists() or OUT.read_text(encoding='utf-8') != text(data):
            print(f'{OUT.relative_to(ROOT)} is stale: run python tools/boss_phases.py')
            return 1
        return 0
    OUT.write_text(text(data), encoding='utf-8')
    print(f"{len(data['bosses'])} bosses, {len(data['waves'])} hand-made waves -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
