"""Playerbot rotations per class (OURS), built from tools/data/class-skills.json.

Every value here is our own choice: the official game has no playerbots. A rotation is an
ordered priority list; the bot casts the first entry it may cast now (learned at its level,
off cooldown, enough MP, its `when` conditions true). See docs/PLAYERBOTS.md.

Rule used to build the default lists:
  * support entries first, from tools/data/skill-effects.json (the class's own Active skills
    with a heal, hot, buff or resurrect effect), in the priority heal > resurrection (out of
    combat) > buff upkeep (SUPPORT below gives the conditions per effect and role);
  * then the damage entries: an Active skill of the class with at least one Damage row and
    the class's weapon (the Dodge carries no Damage row and stays out);
  * damage: long cooldowns first (the big hitters open), then shorter ones, then the
    cooldown-free fillers, MP fillers before the free one, so the bot always has something to press;
  * `level` is the class unlock level, so the server can skip entries above the bot's level
    (the session refuses unlearned skills anyway).

Rebuild with `python tools/bot_rotations.py`; `python tools/bot_rotations.py --check`
fails when the tracked file is stale. Hand edits are welcome but are overwritten by a
rebuild: change the rule here or add to EXTRA.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
SKILL_EFFECTS = ROOT / 'tools/data/skill-effects.json'
OUT = ROOT / 'tools/data/bot-rotations.json'

# Bot playable classes (Novice/Test/Tutorial are not player classes).
CLASSES = ('Gladiator', 'Templar', 'Assassin', 'Cleric', 'Ranger', 'Elementalist', 'Sorcerer', 'Chanter')
# OURS: how close the bot stands to its target before it casts (cm). Melee weapons close in,
# ranged ones keep their distance. The client's per-skill ranges are not in class-skills.json.
RANGE = {'Greatsword': 300, 'Sword': 300, 'Dagger': 250, 'Mace': 300, 'Staff': 300,
         'Bow': 1800, 'Orb': 1800, 'Magicbook': 1800}
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
    ('friendly', 'hot'): [({'target': 'ally', 'target_hp_below': 90, 'buff_missing': True}, 'heal'),
                          ({'target': 'self', 'target_hp_below': 70, 'buff_missing': True}, 'other')],
    ('self', 'hot'): [({'target': 'self', 'target_hp_below': 80, 'buff_missing': True}, None)],
    ('dead_friendly', 'resurrect'): [({'target': 'dead_ally', 'in_combat': False}, None)],
    ('self', 'buff'): [({'target': 'self', 'buff_missing': True}, None)],
    ('friendly', 'buff'): [({'target': 'ally', 'buff_missing': True}, None)],
}
# Priority among the support entries (heal > resurrection > buff upkeep, mod-playerbots' order).
SUPPORT_ORDER = ('heal', 'hot', 'resurrect', 'buff')
# The effect types a target of a rotation entry may use, and the skill-effects targets it may name.
FRIENDLY_TARGETS = {'self': {'self', 'friendly'}, 'ally': {'friendly'}, 'leader': {'friendly'}, 'dead_ally': {'dead_friendly'}}
CONDITIONS = {'target', 'target_hp_below', 'target_hp_above', 'self_hp_below', 'self_mp_above', 'in_combat', 'buff_missing'}
# Entries appended after the generated ones, per class (hand-made additions).
EXTRA = {}
GLOBAL_COOLDOWN_MS = 1000
# OURS: a tank bot's damage counts this many times in a monster's hate table (NpcAi), so it holds
# the monsters off the healers and damage dealers.
TANK_HATE_MULTIPLIER = 3


def load():
    return json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))


def load_effects():
    return json.loads(SKILL_EFFECTS.read_text(encoding='utf-8'))['skills']


def class_skill(data, name, skill_id):
    """(unlock level, skill) of an Active skill of the class usable with its weapon, else None."""
    info = data['classes'][name]
    levels = [lvl for sid, lvl, _ in info['unlocks'] if sid == skill_id]
    skill = data['skills'].get(str(skill_id))
    if not levels or not skill or skill['type'] != 'Active':
        return None
    if info['weapon_type'] not in skill['weapons'] and skill['weapons'] != ['None']:
        return None
    return min(levels), skill


def support_entries(data, effects, name):
    """The class's heal, heal-over-time, resurrection and buff entries from skill-effects.json."""
    roles = ROLES[name]
    found = []
    for key, entry in effects.items():
        owned = class_skill(data, name, int(key))
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


def damage_skills(data, name):
    """(skill id, unlock level, skill) of the class's Active damage skills."""
    info = data['classes'][name]
    found = {}
    for skill_id, level, _ in info['unlocks']:
        skill = data['skills'].get(str(skill_id))
        if not skill or skill['type'] != 'Active' or not skill['damage']:
            continue
        if info['weapon_type'] not in skill['weapons'] and skill['weapons'] != ['None']:
            continue
        if skill_id not in found or level < found[skill_id][0]:
            found[skill_id] = (level, skill)
    return [(sid, lvl, skill) for sid, (lvl, skill) in found.items()]


def rotation(data, name, effects=None):
    effects = load_effects() if effects is None else effects
    skills = [e for e in damage_skills(data, name) if effects.get(str(e[0]), {}).get('target', 'enemy') == 'enemy']
    # Longest cooldown first; among equals the more Damage rows; fillers (no cooldown) last,
    # the ones that cost MP before the free one; the skill id breaks ties (stable output).
    skills.sort(key=lambda e: (e[2]['cooldown_ms'] == 0, -e[2]['cooldown_ms'], -e[2]['cost']['mp'] if e[2]['cooldown_ms'] == 0 else 0,
                               -len(e[2]['damage']), e[0]))
    entries = [{'skill': sid, 'name': skill['name'], 'level': lvl, 'when': {'target': 'enemy'}}
               for sid, lvl, skill in skills]
    return support_entries(data, effects, name) + entries + EXTRA.get(name, [])


def build(data=None, effects=None):
    data = data or load()
    effects = load_effects() if effects is None else effects
    return {
        'marker': 'OURS',
        'about': 'Playerbot rotations (docs/PLAYERBOTS.md); built by tools/bot_rotations.py from class-skills.json and skill-effects.json.',
        'global_cooldown_ms': GLOBAL_COOLDOWN_MS,
        'tank_hate_multiplier': TANK_HATE_MULTIPLIER,
        'classes': {name: {'weapon_type': data['classes'][name]['weapon_type'], 'range': RANGE[data['classes'][name]['weapon_type']],
                           'roles': ROLES[name], 'rotation': rotation(data, name, effects)} for name in CLASSES},
    }


def text(data=None, effects=None):
    return json.dumps(build(data, effects), indent=1, ensure_ascii=False) + '\n'


def effect_problems(where, step, skill, effect_set):
    """A step's target against the skill's skill-effects.json entry (heals land on players, damage on the fight's target)."""
    target = step.get('when', {}).get('target', 'enemy')
    when = step.get('when', {})
    if target == 'enemy':
        if effect_set is not None and effect_set['target'] != 'enemy':
            return [f'{where}: skill {step["skill"]} lands on {effect_set["target"]}, not on an enemy']
        if not skill['damage'] and effect_set is None:
            return [f'{where}: skill {step["skill"]} has no damage and no effect for an enemy']
        return []
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


def validate(rotations, data=None, effects=None):
    """Problems in a rotations document: unknown classes or skills, skills of another class, wrong levels, effects that do not suit the target."""
    data = data or load()
    effects = load_effects() if effects is None else effects
    problems = []
    for name, entry in rotations.get('classes', {}).items():
        info = data['classes'].get(name)
        if info is None:
            problems.append(f'{name}: not a class of class-skills.json')
            continue
        unlocks = {sid: lvl for sid, lvl, _ in info['unlocks']}
        if not isinstance(entry.get('range'), int) or entry['range'] <= 0:
            problems.append(f'{name}: range must be a positive integer')
        for i, step in enumerate(entry.get('rotation', [])):
            sid = step.get('skill')
            skill = data['skills'].get(str(sid))
            if skill is None:
                problems.append(f'{name}[{i}]: skill {sid} does not exist')
            elif sid not in unlocks:
                problems.append(f'{name}[{i}]: skill {sid} is not a {name} skill')
            elif step.get('level') != unlocks[sid]:
                problems.append(f'{name}[{i}]: skill {sid} unlocks at {unlocks[sid]}, not {step.get("level")}')
            elif skill['type'] != 'Active':
                problems.append(f'{name}[{i}]: skill {sid} is {skill["type"]}, not Active')
            when = step.get('when', {})
            if when.get('target', 'enemy') not in ('enemy', *FRIENDLY_TARGETS):
                problems.append(f'{name}[{i}]: unknown target {when.get("target")}')
            unknown = set(when) - CONDITIONS
            if unknown:
                problems.append(f'{name}[{i}]: unknown conditions {sorted(unknown)}')
            if skill is not None and sid in unlocks:
                problems.extend(effect_problems(f'{name}[{i}]', step, skill, effects.get(str(sid))))
            bad_roles = set(step.get('roles', [])) - set(entry.get('roles', []))
            if bad_roles:
                problems.append(f'{name}[{i}]: roles {sorted(bad_roles)} are not roles of {name}')
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
    print(f'wrote {OUT.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
