"""Stigmas: the global Aion 2 rules (tools/data/stigmas.json) and each class's stigma skills.

How global plays (October 2026 guides, CANDIDATE: no capture of the official server yet; docs/STIGMAS.md):

- The stigma tab opens at level 22 (the third Ascension quest gives the first Stigma Shard). Global has 4 stigma slots,
  at character levels 22, 27, 32 and 37; Korea has 6 (slots 5 and 6: levels not published, OURS 42 and 45).
- Each class has 13 stigma skills. Learning one costs 1 Stigma Shard (it is level 1); levels 2-5 cost 1 shard each,
  6-10 cost 2, 11-15 cost 4 and 16-20 cost 8 (75 shards for one stigma at 20). Global's ceiling is 20.
- Shards from levelling: 1 per level from 23 to 39, 2 per level from 40 to 45 (29 at 45), plus the Ascension one.
- A stigma only works while it sits in a stigma slot. Resetting refunds its shards (global's own reset price is not
  published: OURS free, as Korea since Chapter 1).

The client keeps the same values in its own tables, which we have not exported: Exp.dat BonusStigmaPoint and
StigmaSkillContextSlotMax (shards and slots per level) and the SkillAcquireData rows with AcquireType Stigma and
CostStigmaPoint (the stigma skills and their level costs). When those exports exist, `build` should read them instead.

The stigma skills per class are CANDIDATE: the client's developer kit Class_<class> (tools/data/dev-menu.json) teaches a
level-45 Ascension-3 character its whole kit; the active skills of that kit that no auto-learned row gives the class are
the ones the class_skills.py filter dropped for their stigma cost. That gives 11 or 12 per class (global lists 13: the
newest one, added in March 2026, is not in the developer kit). The Brawler (client class "Fighter") is the same: the
active kit skills without Mastery rows.

Rebuild the generated part with `python tools/stigmas.py`.
"""
import json
from pathlib import Path

try:
    from . import progression
except ImportError:
    import progression

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/stigmas.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
DEV_MENU = ROOT / 'tools/data/dev-menu.json'
# The playable classes plus the Brawler, whose client name is Fighter.
CLASSES = ('Gladiator', 'Templar', 'Assassin', 'Ranger', 'Sorcerer', 'Elementalist', 'Cleric', 'Chanter', 'Fighter')


def kit(class_name, dev_buttons):
    return [int(line.split()[1]) for line in dev_buttons.get(f'Class_{class_name}', [])
            if line.startswith('LearnSkill ') and len(line.split()) >= 2]


def class_stigmas(class_name, rules, dev_buttons):
    """The class's stigma skills (CANDIDATE), sorted by ID: active developer-kit skills nothing else gives the class."""
    known = {s for s, _, _ in rules['common_unlocks']}
    cls = rules['classes'].get(class_name)
    if cls and cls['unlocks']:
        known |= {s for s, _, _ in cls['unlocks']}
    else:
        # A class with no auto-learned rows (Fighter): its Mastery-levelled skills are its normal kit.
        family = class_name_family(class_name, dev_buttons)
        known |= {int(s) for s in rules['levels'] if family is not None and int(s) // 1_000_000 == family}
    return sorted(s for s in set(kit(class_name, dev_buttons)) - known if not progression.is_passive(s) and s % 1_000_000 >= 10_000)


def class_name_family(class_name, dev_buttons):
    """The class's skill ID family (the first two digits of its kit skills, 11 for Gladiator)."""
    ids = kit(class_name, dev_buttons)
    return max(set(i // 1_000_000 for i in ids), key=[i // 1_000_000 for i in ids].count) if ids else None


def shard_totals(params, max_level):
    """Stigma Shards a character has had in all at each level (levelling grants plus the Ascension shard)."""
    total, out = 0, {}
    for level in range(1, max_level + 1):
        if level == params['unlock_level']:
            total += params['ascension_shards']
        for band in params['shards_per_level']:
            if band['from'] <= level <= band['to']:
                total += band['shards']
        out[str(level)] = total
    return out


def build(params, rules, dev_buttons):
    classes = {c: class_stigmas(c, rules, dev_buttons) for c in CLASSES}
    return {'classes': {c: s for c, s in classes.items() if s},
            'shard_totals': shard_totals(params, rules['max_level'])}


def regenerate(path=DATA, class_skills_path=CLASS_SKILLS, dev_menu_path=DEV_MENU):
    document = json.loads(Path(path).read_text(encoding='utf-8'))
    rules = json.loads(Path(class_skills_path).read_text(encoding='utf-8'))
    dev = json.loads(Path(dev_menu_path).read_text(encoding='utf-8'))['buttons']
    document['generated'] = build(document['params'], rules, dev)
    Path(path).write_text(json.dumps(document, indent=1) + '\n', encoding='utf-8')
    return document


def main():
    document = regenerate()
    for name, skills in document['generated']['classes'].items():
        print(f'{name}: {len(skills)} stigma skills')
    totals = document['generated']['shard_totals']
    print(f"Stigma Shards: {totals['22']} at 22, {totals['37']} at 37, {totals['45']} at 45")


if __name__ == '__main__':
    main()
