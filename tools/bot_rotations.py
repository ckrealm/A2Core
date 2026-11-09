"""Playerbot rotations per class (OURS), built from the class data and a hand-made plan per class.

Every value here is our own choice: the official game has no playerbots. A rotation is an
ordered priority list; the bot casts the first entry it may cast now (learned at its level,
its role, off cooldown, enough MP, its `when` conditions true). See docs/PLAYERBOTS.md.

How a class's list is built:
  * support entries first, from tools/data/skill-effects.json (the class's own Active skills
    with a heal, hot, buff or resurrect effect), in the priority heal > resurrection (out of
    combat) > buff upkeep (SUPPORT below gives the conditions per effect and role);
  * then the class's PLAN: its damage, control and threat entries in the order a player of the
    class would press them in a dungeon (mod-playerbots' "strategies", one per role). The plans
    follow the public Aion 2 skill descriptions and guides (thegameswiki.com class skill pages,
    allthings.how, iggm, gamerblurb and roonby class guides, 2026): interrupts first, then gap
    closers, combo follow-ups, cooldowns (big ones on elites and bosses), area skills on packs,
    debuff upkeep, and the fillers last;
  * the class's skills past level 25 (tools/data/progression.json, OURS fallback skills with no
    known behaviour) go just before the fillers, long cooldowns first;
  * a heal-role bot only spends MP on damage while it has HEAL_ROLE_MP_FLOOR % left.

Skills the bots never use, because the server has no event they need, are listed in SKIPPED with
the reason (now only the Elementalist's spirit skills). Crit-only skills use `after_critical` (the
bot's own hit was a critical lately, GameSession.LastCriticalAt); block, evasion and Dodge procs use
`after_block`, `after_evasion` and `after_dodge` (GameSession.GuardHit, LastDodgeAt). Staggered-only
skills use `target_stunned`: a stunned monster, or a boss whose stagger broke
(GameSession.BossStaggerStuns).

Rebuild with `python tools/bot_rotations.py`; `python tools/bot_rotations.py --check`
fails when the tracked file is stale or invalid. Change a plan here, not the JSON.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
SKILL_EFFECTS = ROOT / 'tools/data/skill-effects.json'
PROGRESSION = ROOT / 'tools/data/progression.json'
OUT = ROOT / 'tools/data/bot-rotations.json'

# Bot playable classes (Novice/Test/Tutorial are not player classes).
CLASSES = ('Gladiator', 'Templar', 'Assassin', 'Cleric', 'Ranger', 'Elementalist', 'Sorcerer', 'Chanter')
# OURS: how close the bot stands to its target before it casts (cm). Melee weapons close in (4 m
# skills), ranged ones keep their distance (20 m skills, the bot stands at 18 m).
RANGE = {'Greatsword': 300, 'Sword': 300, 'Dagger': 250, 'Mace': 300, 'Staff': 300,
         'Bow': 1800, 'Orb': 1800, 'Magicbook': 1800}
# The Cleric fights with 20 m spells although it holds a mace; a heal Cleric stands a little closer to its group.
CLASS_RANGE = {'Cleric': 1800}
ROLE_RANGES = {'Cleric': {'heal': 1500}}
# Where a melee bot stands: "behind" works from the monster's back while it attacks someone else.
POSITION = {'Gladiator': 'behind', 'Templar': 'front', 'Assassin': 'behind', 'Chanter': 'behind',
            'Cleric': 'ranged', 'Ranger': 'ranged', 'Elementalist': 'ranged', 'Sorcerer': 'ranged'}
# OURS: roles each class may take (mod-playerbots style); the first is a new bot's role.
ROLES = {'Gladiator': ['dps', 'tank'], 'Templar': ['tank', 'dps'], 'Assassin': ['dps'], 'Ranger': ['dps'],
         'Elementalist': ['dps'], 'Sorcerer': ['dps'], 'Cleric': ['heal', 'dps'], 'Chanter': ['heal', 'dps']}
# OURS: the support entries made from a skill-effects.json entry, by (its target, its effect type).
# Each is (when, roles): roles 'heal' keeps the entry to heal bots, 'other' to the class's other
# roles, None to every role. Percentages are of the chosen target's HP. A buff whose duration is
# shorter than its cooldown is a combat buff (in_combat true); a longer one is kept up always.
SUPPORT = {
    ('friendly', 'heal'): [({'target': 'ally', 'target_hp_below': 70}, 'heal'),
                           ({'target': 'ally', 'target_hp_below': 35}, 'other')],
    ('self', 'heal'): [({'target': 'self', 'target_hp_below': 50}, None)],
    ('friendly', 'hot'): [({'target': 'tank', 'in_combat': True, 'buff_missing': True}, 'heal'),
                          ({'target': 'ally', 'target_hp_below': 90, 'buff_missing': True}, 'heal'),
                          ({'target': 'self', 'target_hp_below': 70, 'buff_missing': True}, 'other')],
    ('self', 'hot'): [({'target': 'self', 'target_hp_below': 80, 'buff_missing': True}, None)],
    ('dead_friendly', 'resurrect'): [({'target': 'dead_ally', 'in_combat': False}, None)],
    ('self', 'buff'): [({'target': 'self', 'buff_missing': True}, None)],
    ('friendly', 'buff'): [({'target': 'ally', 'buff_missing': True}, None)],
}
# Priority among the support entries (heal > resurrection > buff upkeep, mod-playerbots' order).
SUPPORT_ORDER = ('heal', 'hot', 'resurrect', 'buff')
# The effect types a target of a rotation entry may use, and the skill-effects targets it may name.
FRIENDLY_TARGETS = {'self': {'self', 'friendly'}, 'ally': {'friendly'}, 'leader': {'friendly'}, 'tank': {'friendly'},
                    'dead_ally': {'dead_friendly'}}
CONDITIONS = {'target', 'target_hp_below', 'target_hp_above', 'self_hp_below', 'self_mp_above', 'self_mp_below', 'in_combat',
              'buff_missing', 'target_casting', 'target_stunned', 'target_slowed', 'target_elite', 'target_boss', 'target_on_self',
              'target_effect_missing', 'target_has', 'target_distance_above', 'enemies_at_least', 'after', 'after_within_ms',
              'after_critical', 'after_block', 'after_evasion', 'after_dodge'}
# Conditions that only make sense on an entry aimed at the fight's target.
ENEMY_ONLY = {'target_casting', 'target_stunned', 'target_slowed', 'target_elite', 'target_boss', 'target_on_self',
              'target_effect_missing', 'target_has', 'target_distance_above', 'enemies_at_least'}
# Enemy effect types that mean "an interrupt" for target_casting entries.
INTERRUPTS = {'stun', 'interrupt'}
AREAS = ('self', 'target')
GLOBAL_COOLDOWN_MS = 1000
# OURS: a tank bot's damage counts this many times in a monster's hate table (NpcAi), so it holds
# the monsters off the healers and damage dealers.
TANK_HATE_MULTIPLIER = 3
# OURS: a heal bot keeps this much MP (%) for its heals: its MP-costing damage entries need more.
HEAL_ROLE_MP_FLOOR = 40
# OURS: area entries need this many monsters fighting the group inside the area (the server's 4 m).
PACK = 2
BIG_PACK = 3


def E(skill, roles=None, area=None, filler=False, **when):
    """A plan entry aimed at the fight's target (or an area around the bot / the target)."""
    step = {'skill': skill, 'when': {'target': 'enemy', **when}}
    if roles:
        step['roles'] = list(roles)
    if area:
        step['area'] = area
    if filler:
        step['filler'] = True
    return step


# The plans (OURS orders, after the guides named in the module docstring). `filler` marks the
# entries the progression skills go in front of.
PLAN = {
    'Gladiator': [
        E(11290000, target_casting=True),                                  # Mocking Blade: knockdown, the interrupt
        E(11360000, after_dodge=True),                                     # Rush Strike: right after a Dodge
        E(11200000, after_block=True),                                     # Ankle Slice: right after a block
        E(11190000, target_distance_above=500),                            # Leaping Slam: leap in from up to 20 m
        E(11100000),                                                       # Ruinous Blow: Prepare for Battle, early and on cooldown
        E(11300000, target_stunned=True, target_elite=True),               # Aerial Snare: keeps a knocked-down elite in the air
        E(11170000, target_stunned=True),                                  # Overhead Slam: the knockdown payoff
        E(11170000, after=[11290000, 11300000]),                           # ... right after its opener (a boss is not held)
        E(11280000, target_stunned=True),                                  # Sword Aura Rampage: staggered targets only
        E(11290000, target_boss=False),                                    # Mocking Blade on cooldown (kept for a boss's casts)
        E(11050000, area='self', enemies_at_least=PACK),                   # Crushing Wave: up to 4 within 5 m
        E(11010000, area='self', enemies_at_least=PACK, self_mp_above=20, filler=True),   # Rending Blow on a pack
        E(11020000, area='self', enemies_at_least=PACK, filler=True),                      # Keen Strike on a pack
        E(11010000, self_mp_above=30, filler=True),                        # Rending Blow: the MP filler
        E(11020000, filler=True),                                          # Keen Strike: restores MP
    ],
    'Templar': [
        E(12100000, target_casting=True),                                  # Shield Smite: stun, the interrupt
        E(12430000, after_dodge=True),                                     # Shield Rush: right after a Dodge
        E(12270000, after_block=True),                                     # Debilitating Smash: right after a block
        E(12130000, roles=['tank'], target_on_self=False),                 # Poach: hook a monster off someone else (taunt)
        E(12090000, target_distance_above=600),                            # Punishment: the 20 m opener (Executor buff)
        E(12300000, target_stunned=True),                                  # Annihilate: on a stunned target
        E(12300000, after=[12100000]),
        E(12240000, after=[12100000, 12350000]),                           # Judgment: after a shield skill
        E(12340000, target_stunned=True),                                  # Flash Rampage: staggered targets only
        E(12100000, target_boss=False),                                    # Shield Smite on cooldown (kept for a boss's casts)
        E(12350000, roles=['tank'], self_hp_below=70),                     # Warding Strike: the tank's damage-tolerance window
        E(12350000, roles=['dps']),
        E(12090000),                                                       # Punishment on cooldown (Executor)
        E(12040000, area='self', enemies_at_least=PACK, self_mp_above=20, filler=True),   # Pummel: area threat
        E(12040000, roles=['tank'], self_mp_above=30, filler=True),        # Pummel: threat on one
        E(12010000, filler=True),                                          # Vicious Strike: threat, restores MP
    ],
    'Assassin': [
        E(13070000, target_casting=True),                                  # Shadowstrike: behind the target and stun
        E(13070000, target_distance_above=500),
        E(13360000, after_dodge=True),                                     # Infiltrate: right after a Dodge
        E(13210000, after_evasion=True),                                   # Whirlwind Slice: right after an evasion                            # ... and the opener from up to 20 m
        E(13220000, target_stunned=True),                                  # Shadow Fall: knockdown on a stunned target
        E(13220000, after=[13070000, 13130000]),
        E(13340000, target_stunned=True),                                  # Storm Rampage: staggered targets only
        E(13130000, area='target', enemies_at_least=PACK, after=[13100000], after_within_ms=10000),  # Insignia Explosion
        E(13130000, after=[13100000], after_within_ms=10000),
        E(13350000, area='self', enemies_at_least=PACK, after_critical=True),   # Heart Gore after a critical hit
        E(13350000, after_critical=True),
        E(13060000),                                                       # Ambush: the back attack
        E(13050000, area='target', enemies_at_least=PACK),                 # Flash Slice: up to 4 at the target
        E(13050000),
        E(13100000, area='self', enemies_at_least=PACK, self_mp_above=20, filler=True),   # Savage Roar engraves Insignia
        E(13100000, self_mp_above=30, filler=True),
        E(13010000, area='self', enemies_at_least=PACK, filler=True),      # Quick Slice on a pack
        E(13010000, filler=True),                                          # Quick Slice: restores MP
    ],
    'Ranger': [
        E(14070000, target_casting=True, after=[14090000], after_within_ms=10000),  # Suppressing Arrow (needs Precision)
        E(14170000, area='self', target_on_self=True),                     # Explosion Trap: peel a monster on the Ranger
        E(14110000, area='target', enemies_at_least=PACK),                 # Gale Arrow: the burst window on a pack
        E(14110000),
        E(14090000),                                                       # Marking Shot: Precision (+crit) on cooldown
        E(14330000, target_stunned=True),                                  # Arrow Scattershot: staggered targets only
        E(14130000, target_effect_missing=True),                           # Snare Shot: the slow Burst Arrow needs
        E(14080000, target_slowed=True),                                   # Burst Arrow on a slowed target
        E(14050000, after_critical=True),                                  # Drill Dart after a critical hit
        E(14010000, after=[14090000], after_within_ms=10000),              # Deadshot with Precision
        E(14070000, target_elite=True, target_boss=False, after=[14090000], after_within_ms=10000),   # Suppressing Arrow on an elite
        E(14010000, target_elite=True),
        E(14340000, area='target', enemies_at_least=PACK, self_mp_above=20, filler=True),  # Tempest Shot on a pack
        E(14340000, self_mp_above=40, filler=True),
        E(14020000, filler=True),                                          # Snipe: restores MP
    ],
    'Sorcerer': [
        E(15150000, target_casting=True),                                  # Frost: freeze, the interrupt
        E(15220000, area='target', target_stunned=True),                   # Frost Burst on a frozen target
        E(15220000, area='target', after=[15150000]),
        E(15010000, target_stunned=True),                                  # Flame Scattershot: staggered targets only
        E(15280000, target_on_self=True),                                  # Bittercold Wind: root a monster on the Sorcerer
        E(15060000, area='target', enemies_at_least=BIG_PACK),             # Hellfire on a pack
        E(15060000, target_elite=True),                                    # ... or an elite / boss
        E(15110000, area='target', enemies_at_least=BIG_PACK),             # Winter's Shackles on a pack
        E(15110000, target_elite=True),
        E(15150000, target_elite=True, target_boss=False),                 # Frost on an elite (a boss's casts keep it)
        E(15040000, self_mp_above=30),                                     # Firestorm: 5 hits
        E(15050000, after=[15210000, 15040000, 15060000], after_within_ms=10000),   # Blaze on a Fire Mark
        E(15090000, area='target', enemies_at_least=BIG_PACK, self_mp_above=40, filler=True),  # Ice Chain on a pack
        E(15210000, filler=True),                                          # Flame Arrow: restores MP, marks
    ],
    'Elementalist': [
        E(16070000, target_casting=True),                                  # Soul's Cry: fear, the interrupt
        E(16070000, target_on_self=True),                                  # ... or a monster on the Elementalist
        E(16340000, target_stunned=True),                                  # Rapid Scattershot: staggered targets only
        E(16140000, area='target', enemies_at_least=PACK),                 # Jointstrike: Curse on a pack
        E(16140000, target_effect_missing=True),                           # ... kept up on the target
        E(16040000, area='target', enemies_at_least=BIG_PACK, self_mp_above=25, filler=True),   # Combustion on a pack
        E(16040000, self_mp_above=50, filler=True),
        E(16010000, filler=True),                                          # Cold Shock: restores MP
    ],
    'Cleric': [
        E(17370000, target_stunned=True),                                  # Lightning Strike Scattershot: staggered only
        E(17070000, target_effect_missing=True),                           # Chain of Torment: keep it up
        E(17350000, target_has=[17070000]),                                # Condemnation on a Chain of Torment
        E(17080000, target_effect_missing=True),                           # Debilitating Mark: keep it up
        E(17150000),                                                       # Divine Aura: 5 s of damage
        E(17060000, area='target', enemies_at_least=BIG_PACK),             # Bolt on a pack
        E(17060000, target_elite=True),                                    # ... or an elite / boss
        E(17040000, area='target', enemies_at_least=BIG_PACK, self_mp_above=50, filler=True),   # Judgment Thunder on a pack
        E(17040000, self_mp_above=60, filler=True),
        E(17010000, filler=True),                                          # Earth's Retribution: restores MP
    ],
    'Chanter': [
        E(18060000, target_casting=True),                                  # Impactful Crush: stun, the interrupt
        E(18090000, target_distance_above=500),                            # Rushing Smash: charge in from up to 20 m
        E(18210000, after_dodge=True),                                     # Tremor Crush: right after a Dodge
        E(18150000, after_block=True),                                     # Heat Wave Blow: right after a block
        E(18100000, after=[18060000, 18290000]),                           # Dark Crush in its window
        E(18080000, target_stunned=True),                                  # Wave Blow: knockdown on a stunned target
        E(18300000, target_stunned=True),                                  # Gust Rampage: staggered targets only
        E(18290000, area='target', enemies_at_least=PACK),                 # Spinning Strike: crit damage buff, opens Dark Crush
        E(18290000),
        E(18060000, target_boss=False),                                    # Impactful Crush on cooldown (kept for a boss's casts)
        E(18090000),
        E(18040000, area='self', enemies_at_least=BIG_PACK, self_mp_above=50, filler=True),    # Incandescent Blow on a pack
        E(18010000, area='self', enemies_at_least=PACK, filler=True),      # Onslaught on a pack
        E(18040000, self_mp_above=60, filler=True),
        E(18010000, filler=True),                                          # Onslaught: restores MP
    ],
}

# Active skills the bots leave out, with the reason (the server has no such trigger yet). The block,
# evasion and Dodge procs are in the plans: the server rolls blocks and evasions on monster hits and a
# victim bot dodges a monster's special (GameSession.GuardHit, BotBrain.DodgeSpecial).
SKIPPED = {
    16330000: 'Dimensional Control: fires when a spirit uses a skill (no spirits)',
    16300000: 'Elemental Fusion: needs Four Elements stacks from spirits (no spirits)',
}


def load():
    return json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))


def load_effects():
    return json.loads(SKILL_EFFECTS.read_text(encoding='utf-8'))['skills']


def load_progression():
    return json.loads(PROGRESSION.read_text(encoding='utf-8'))['generated']


def unlocks_of(data, name, progression=None):
    """{skill id: unlock level} of the class: the recovered unlocks, then the OURS ones past 25."""
    progression = load_progression() if progression is None else progression
    found = {}
    for sid, lvl, _ in data['classes'][name]['unlocks'] + progression['unlocks'].get(name, []):
        found[sid] = min(lvl, found.get(sid, lvl))
    return found


def skill_info(data, skill_id, progression=None):
    """The skill's definition: recovered, else the progression fallback; recovered ones without rows get the fallback rows."""
    progression = load_progression() if progression is None else progression
    skill = data['skills'].get(str(skill_id)) or progression['skills'].get(str(skill_id))
    if skill and not skill['damage'] and str(skill_id) in progression['damage']:
        skill = dict(skill, damage=progression['damage'][str(skill_id)])
    return skill


def usable(data, name, skill_id, progression=None):
    """(unlock level, skill) of an Active skill of the class usable with its weapon, else None."""
    level = unlocks_of(data, name, progression).get(skill_id)
    skill = skill_info(data, skill_id, progression)
    if level is None or not skill or skill['type'] != 'Active':
        return None
    weapon = data['classes'][name]['weapon_type']
    if weapon not in skill['weapons'] and skill['weapons'] != ['None']:
        return None
    return level, skill


def support_entries(data, effects, name, progression=None):
    """The class's heal, heal-over-time, resurrection and buff entries from skill-effects.json."""
    roles = ROLES[name]
    found = []
    for key, entry in effects.items():
        owned = usable(data, name, int(key), progression)
        if owned is None:
            continue
        level, skill = owned
        for effect in entry['effects']:
            for when, only in SUPPORT.get((entry['target'], effect['type']), []):
                kept = [r for r in roles if only is None or (r == 'heal') == (only == 'heal')]
                if not kept:
                    continue
                when = dict(when)
                if effect['type'] == 'buff' and effect.get('duration_ms', 0) < skill['cooldown_ms']:
                    when['in_combat'] = True
                step = {'skill': int(key), 'name': entry.get('name') or skill['name'], 'level': level, 'when': when}
                if only is not None:
                    step['roles'] = kept
                found.append((SUPPORT_ORDER.index(effect['type']), int(key), step))
    found.sort(key=lambda e: (e[0], e[1]))
    return [step for _, _, step in found]


def progression_entries(data, name, planned, progression=None):
    """The class's Active damage skills past level 25 that no plan names: long cooldowns first (OURS fallback definitions)."""
    progression = load_progression() if progression is None else progression
    found = []
    for sid, lvl, _ in progression['unlocks'].get(name, []):
        if sid in planned or sid in SKIPPED or (owned := usable(data, name, sid, progression)) is None or not owned[1]['damage']:
            continue
        found.append((-owned[1]['cooldown_ms'], lvl, sid, owned[1]))
    found.sort()
    return [{'skill': sid, 'name': skill['name'], 'level': lvl, 'when': {'target': 'enemy'}} for _, lvl, sid, skill in found]


def heal_role_gate(name, step, skill):
    """A heal bot keeps MP for heals: an MP-costing damage entry splits into a gated heal copy and the other roles' copy."""
    if 'heal' not in ROLES[name] or not skill['cost']['mp'] or step.get('roles') or step['when'].get('self_mp_above', 0) >= HEAL_ROLE_MP_FLOOR \
            or step['when'].get('target_casting'):   # an interrupt is always worth its MP
        return [step]
    others = [r for r in ROLES[name] if r != 'heal']
    healer = json.loads(json.dumps(step))
    healer['roles'] = ['heal']
    healer['when']['self_mp_above'] = max(healer['when'].get('self_mp_above', 0), HEAL_ROLE_MP_FLOOR)
    other = dict(step, roles=others)
    return [healer, other]


def rotation(data, name, effects=None, progression=None):
    effects = load_effects() if effects is None else effects
    progression = load_progression() if progression is None else progression
    unlocks = unlocks_of(data, name, progression)
    plan = PLAN.get(name, [])
    planned = {e['skill'] for e in plan}
    damage = []
    extra = progression_entries(data, name, planned, progression)
    def gated(steps):
        return [g for st in steps for g in heal_role_gate(name, st, skill_info(data, st['skill'], progression))]
    for entry in plan:
        if entry.get('filler') and extra:
            damage.extend(gated(extra))
            extra = []
        skill = skill_info(data, entry['skill'], progression)
        step = {'skill': entry['skill'], 'name': skill['name'], 'level': unlocks[entry['skill']], 'when': dict(entry['when'])}
        for key in ('roles', 'area'):
            if key in entry:
                step[key] = entry[key]
        damage.extend(heal_role_gate(name, step, skill))
    damage.extend(gated(extra))
    return support_entries(data, effects, name, progression) + damage


def build(data=None, effects=None, progression=None):
    data = data or load()
    effects = load_effects() if effects is None else effects
    progression = load_progression() if progression is None else progression
    classes = {}
    for name in CLASSES:
        weapon = data['classes'][name]['weapon_type']
        entry = {'weapon_type': weapon, 'range': CLASS_RANGE.get(name, RANGE[weapon]), 'position': POSITION[name], 'roles': ROLES[name]}
        if name in ROLE_RANGES:
            entry['role_ranges'] = ROLE_RANGES[name]
        entry['rotation'] = rotation(data, name, effects, progression)
        classes[name] = entry
    return {
        'marker': 'OURS',
        'about': 'Playerbot rotations (docs/PLAYERBOTS.md); built by tools/bot_rotations.py from the class plans, class-skills.json, '
                 'progression.json and skill-effects.json.',
        'global_cooldown_ms': GLOBAL_COOLDOWN_MS,
        'tank_hate_multiplier': TANK_HATE_MULTIPLIER,
        'skipped': {str(k): v for k, v in sorted(SKIPPED.items())},
        'classes': classes,
    }


def text(data=None, effects=None, progression=None):
    return json.dumps(build(data, effects, progression), indent=1, ensure_ascii=False) + '\n'


def effect_problems(where, step, skill, effect_set):
    """A step's target against the skill's skill-effects.json entry (heals land on players, damage on the fight's target)."""
    target = step.get('when', {}).get('target', 'enemy')
    when = step.get('when', {})
    if target == 'enemy':
        problems = []
        if effect_set is not None and effect_set['target'] != 'enemy':
            problems.append(f'{where}: skill {step["skill"]} lands on {effect_set["target"]}, not on an enemy')
        elif not skill['damage'] and effect_set is None:
            problems.append(f'{where}: skill {step["skill"]} has no damage and no effect for an enemy')
        types = {e['type'] for e in effect_set['effects']} if effect_set and effect_set['target'] == 'enemy' else set()
        if when.get('target_casting') and not types & INTERRUPTS:
            problems.append(f'{where}: target_casting on skill {step["skill"]}, which does not interrupt (no stun/interrupt effect)')
        if when.get('target_effect_missing') and not any(e.get('duration_ms') for e in (effect_set or {}).get('effects', [])):
            problems.append(f'{where}: target_effect_missing on skill {step["skill"]}, which leaves nothing on the target')
        if step.get('area') and not skill['damage']:
            problems.append(f'{where}: area entry for skill {step["skill"]}, which has no damage')
        return problems
    if target not in FRIENDLY_TARGETS:
        return []
    if effect_set is None:
        return [f'{where}: skill {step["skill"]} has no skill-effects.json entry for a {target} target']
    problems = []
    if effect_set['target'] not in FRIENDLY_TARGETS[target]:
        problems.append(f'{where}: skill {step["skill"]} lands on {effect_set["target"]}, not on {target}')
    types = {e['type'] for e in effect_set['effects']}
    wanted = {'resurrect'} if target == 'dead_ally' else {'heal', 'hot', 'buff'}
    if not types & wanted:
        problems.append(f'{where}: skill {step["skill"]} has no {"/".join(sorted(wanted))} effect for a {target} target')
    if when.get('buff_missing') and not types & {'hot', 'buff'}:
        problems.append(f'{where}: buff_missing on skill {step["skill"]}, which leaves no buff')
    return problems


def validate(rotations, data=None, effects=None, progression=None):
    """Problems in a rotations document: unknown classes or skills, skills of another class, wrong levels, conditions and effects that do not fit."""
    data = data or load()
    effects = load_effects() if effects is None else effects
    progression = load_progression() if progression is None else progression
    problems = []
    for name, entry in rotations.get('classes', {}).items():
        info = data['classes'].get(name)
        if info is None:
            problems.append(f'{name}: not a class of class-skills.json')
            continue
        unlocks = unlocks_of(data, name, progression)
        if not isinstance(entry.get('range'), int) or entry['range'] <= 0:
            problems.append(f'{name}: range must be a positive integer')
        if entry.get('position', 'front') not in ('front', 'behind', 'ranged'):
            problems.append(f'{name}: unknown position {entry.get("position")}')
        for role, distance in entry.get('role_ranges', {}).items():
            if role not in entry.get('roles', []) or not isinstance(distance, int) or distance <= 0:
                problems.append(f'{name}: bad role range {role}={distance}')
        for i, step in enumerate(entry.get('rotation', [])):
            where = f'{name}[{i}]'
            sid = step.get('skill')
            skill = skill_info(data, sid, progression)
            if skill is None:
                problems.append(f'{where}: skill {sid} does not exist')
            elif sid not in unlocks:
                problems.append(f'{where}: skill {sid} is not a {name} skill')
            elif step.get('level') != unlocks[sid]:
                problems.append(f'{where}: skill {sid} unlocks at {unlocks[sid]}, not {step.get("level")}')
            elif skill['type'] != 'Active':
                problems.append(f'{where}: skill {sid} is {skill["type"]}, not Active')
            if sid in SKIPPED:
                problems.append(f'{where}: skill {sid} is skipped ({SKIPPED[sid]})')
            when = step.get('when', {})
            target = when.get('target', 'enemy')
            if target not in ('enemy', *FRIENDLY_TARGETS):
                problems.append(f'{where}: unknown target {target}')
            unknown = set(when) - CONDITIONS
            if unknown:
                problems.append(f'{where}: unknown conditions {sorted(unknown)}')
            if target != 'enemy' and set(when) & ENEMY_ONLY:
                problems.append(f'{where}: enemy conditions {sorted(set(when) & ENEMY_ONLY)} on a {target} entry')
            for key in ('after', 'target_has'):
                for other in when.get(key, []):
                    if other not in unlocks:
                        problems.append(f'{where}: {key} names {other}, not a {name} skill')
                    elif key == 'target_has' and not any(e.get('duration_ms') for e in effects.get(str(other), {}).get('effects', [])):
                        problems.append(f'{where}: target_has names {other}, which leaves nothing on the target')
            if 'area' in step and (step['area'] not in AREAS or target != 'enemy'):
                problems.append(f'{where}: area must be self or target, on an enemy entry')
            if skill is not None and sid in unlocks:
                problems.extend(effect_problems(where, step, skill, effects.get(str(sid))))
            bad_roles = set(step.get('roles', [])) - set(entry.get('roles', []))
            if bad_roles:
                problems.append(f'{where}: roles {sorted(bad_roles)} are not roles of {name}')
    return problems


def main(argv):
    if '--check' in argv:
        stale = OUT.read_text(encoding='utf-8') != text()
        problems = validate(json.loads(OUT.read_text(encoding='utf-8')))
        for p in problems:
            print(p)
        if stale:
            print(f'{OUT.relative_to(ROOT)} is stale: run python tools/bot_rotations.py')
        return 1 if stale or problems else 0
    OUT.write_text(text(), encoding='utf-8')
    problems = validate(json.loads(OUT.read_text(encoding='utf-8')))
    for p in problems:
        print(p)
    print(f'wrote {OUT.relative_to(ROOT)}')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
