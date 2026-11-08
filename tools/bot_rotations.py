"""Playerbot rotations per class (OURS), built from tools/data/class-skills.json.

Every value here is our own choice: the official game has no playerbots. A rotation is an
ordered priority list; the bot casts the first entry it may cast now (learned at its level,
off cooldown, enough MP, its `when` conditions true). See docs/PLAYERBOTS.md.

Rule used to build the default lists (damage skills only until heal/buff effects land):
  * an entry is an Active skill of the class with at least one Damage row and the class's
    weapon (the Dodge and self buffs carry no Damage row and stay out);
  * long cooldowns first (the big hitters open), then shorter ones, then the cooldown-free
    fillers, MP fillers before the free one, so the bot always has something to press;
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
OUT = ROOT / 'tools/data/bot-rotations.json'

# Bot playable classes (Novice/Test/Tutorial are not player classes).
CLASSES = ('Gladiator', 'Templar', 'Assassin', 'Cleric', 'Ranger', 'Elementalist', 'Sorcerer', 'Chanter')
# OURS: how close the bot stands to its target before it casts (cm). Melee weapons close in,
# ranged ones keep their distance. The client's per-skill ranges are not in class-skills.json.
RANGE = {'Greatsword': 300, 'Sword': 300, 'Dagger': 250, 'Mace': 300, 'Staff': 300,
         'Bow': 1800, 'Orb': 1800, 'Magicbook': 1800}
# OURS: roles each class may take (mod-playerbots style); heal entries come with the heal effects.
ROLES = {'Gladiator': ['tank', 'dps'], 'Templar': ['tank', 'dps'], 'Assassin': ['dps'], 'Ranger': ['dps'],
         'Elementalist': ['dps'], 'Sorcerer': ['dps'], 'Cleric': ['heal', 'dps'], 'Chanter': ['heal', 'dps']}
# Entries appended after the generated ones, per class (none yet: heal/buff entries go here,
# e.g. {"skill": 17100000, "when": {"target": "ally", "target_hp_below": 60}, "roles": ["heal"]}).
EXTRA = {}
GLOBAL_COOLDOWN_MS = 1000


def load():
    return json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))


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


def rotation(data, name):
    skills = damage_skills(data, name)
    # Longest cooldown first; among equals the more Damage rows; fillers (no cooldown) last,
    # the ones that cost MP before the free one; the skill id breaks ties (stable output).
    skills.sort(key=lambda e: (e[2]['cooldown_ms'] == 0, -e[2]['cooldown_ms'], -e[2]['cost']['mp'] if e[2]['cooldown_ms'] == 0 else 0,
                               -len(e[2]['damage']), e[0]))
    entries = [{'skill': sid, 'name': skill['name'], 'level': lvl, 'when': {'target': 'enemy'}}
               for sid, lvl, skill in skills]
    return entries + EXTRA.get(name, [])


def build(data=None):
    data = data or load()
    return {
        'marker': 'OURS',
        'about': 'Playerbot rotations (docs/PLAYERBOTS.md); built by tools/bot_rotations.py from class-skills.json.',
        'global_cooldown_ms': GLOBAL_COOLDOWN_MS,
        'classes': {name: {'weapon_type': data['classes'][name]['weapon_type'], 'range': RANGE[data['classes'][name]['weapon_type']],
                           'roles': ROLES[name], 'rotation': rotation(data, name)} for name in CLASSES},
    }


def text(data=None):
    return json.dumps(build(data), indent=1, ensure_ascii=False) + '\n'


def validate(rotations, data=None):
    """Problems in a rotations document: unknown classes or skills, skills of another class, wrong levels."""
    data = data or load()
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
            if when.get('target', 'enemy') not in ('enemy', 'self', 'ally', 'leader'):
                problems.append(f'{name}[{i}]: unknown target {when.get("target")}')
            unknown = set(when) - {'target', 'target_hp_below', 'target_hp_above', 'self_hp_below', 'self_mp_above', 'in_combat'}
            if unknown:
                problems.append(f'{name}[{i}]: unknown conditions {sorted(unknown)}')
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
