"""The Brawler class (client ECharacterClass::Fighter = 12, weapon EWeaponType::Gauntlet = 12) from the data we have.

Brawler is Korea-only in the live game. The global 2.0.6.0 client still ships part of it:

- usmap: ECharacterClass::Fighter (12), EWeaponType::Gauntlet (12), Class_Fighter UI icons/tabs, GauntletDamageRatio.
- PcStatLevel: a Fighter row for every level (class-skills.json `stats.Fighter`).
- SkillAcquireData: 25 auto-learned Fighter skills (their cast data is in class-skills.json `skills`, 19xxxxxx) and
  Mastery rows for 22 of them (`levels`). Names are "???": the global client has no English text for them.
- DevTeleportDataTable: the level-45 kit Class_Fighter (41 skills; dev-menu.json).
- Item.dat: two level-1 gauntlets, 150960002 and 150960003 ("Brawler's Fist", equip_wp_Gauntlet_L/D_c_Lobby_001a_b: the
  creation-screen preview weapons). No
  equip_wp_Gauntlet_*_c_begin_001a_b and no gauntlet past level 1.
- PCCreationInfo has no Fighter row, so the client's creation screen does not offer it, and class_skills.build()
  (which builds classes from creation rows) dropped its unlocks.

What this script writes (OURS where the data is silent, each marked):

1. class-skills.json `classes.Fighter`: weapon Gauntlet, starter weapon 150960002 (Light) / 150960003 (Dark) (OURS
   pick), and the auto-learned unlocks rebuilt from the Mastery rows. Rule, checked on all 176 recovered unlocks of the
   8 released classes with no exception: a skill's unlock level is the NeedCharacterLevel of its skill level 2. The
   three base skills without Mastery rows take their own SkillAcquireData rows (19000000 and the dodge 19000100 at 1,
   19000001 at 4).
2. starter-skills.json: Brawler creation profiles (OURS creation IDs, see CREATION) for Light and Dark, male and
   female. The pc_id is the one setting the client must recognise; PC_IDS holds it.
3. progression.json `generated` (tools/progression.py): the 16 kit skills past level 25, like every class.

Run `python tools/brawler.py`; it is idempotent.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
STARTERS = ROOT / 'tools/data/starter-skills.json'
NAME = 'Fighter'
WEAPON = 'Gauntlet'
STARTER_WEAPONS = {'1': 150960002, '2': 150960003}
# SkillAcquireData auto-learn levels of the base skills without Mastery rows (catalogue relationships.acquisition,
# read on the 2.0.6.0 bundle 2026-11-09).
BASE_SKILLS = {19000000: 1, 19000001: 4, 19000100: 1}
COMMON_LEVEL_ONE = [301, 1008, 8000, 9079, 4900202, 4900220]
# OURS creation IDs: PCCreationInfo uses 1-20, 41 and 43; Brawler takes the next free pair (Light, Dark).
CREATION = {1: 45, 2: 46}
# pc_id per (race, gender). PcData numbers four rows per class (Light M/F, Dark M/F): Novice 1-4, Gladiator 5-8 ...
# Chanter 33-36, Test 37-40, Tutorial 41-44. CANDIDATE: Fighter continues at 45-48 (see docs/friends-server/BRAWLER.md).
PC_IDS = {(1, 1): 45, (1, 2): 46, (2, 1): 47, (2, 2): 48}
# OURS default hotbar: the level-one attacks first, then the next actives in unlock order (PCContextSkillSlot has no
# Fighter rows in the export).
BAR_SIZE = 8


def fighter_unlocks(rules):
    """[skill, need level, 1] for every recovered Fighter skill: Mastery level-2 need, else level 1."""
    skills = sorted(int(s) for s in rules['skills'] if s.startswith('19') and len(s) == 8)
    rows = []
    for skill in skills:
        mastery = rules['levels'].get(str(skill))
        if mastery:
            need = next(row[2] for row in mastery if row[0] == 2)
        elif skill in BASE_SKILLS:
            need = BASE_SKILLS[skill]
        else:
            raise ValueError(f'Fighter skill {skill} has no unlock evidence')
        rows.append([skill, need, 1])
    rows.sort(key=lambda r: (r[1], r[0]))
    return rows


def check_rule(rules):
    """The unlock-level rule on the released classes: (matches, mismatches)."""
    ok = bad = 0
    for cls in rules['classes'].values():
        for skill, need, _ in cls['unlocks']:
            mastery = rules['levels'].get(str(skill))
            if mastery:
                if next(r[2] for r in mastery if r[0] == 2) == need:
                    ok += 1
                else:
                    bad += 1
    return ok, bad


def is_active(rules, skill):
    return rules['skills'].get(str(skill), {}).get('type') == 'Active'


def profiles(rules, unlocks):
    level_one = sorted(COMMON_LEVEL_ONE + [s for s, need, _ in unlocks if need == 1])
    attacks = [s for s, need, _ in unlocks if is_active(rules, s) and s != 19000100 and rules['skills'][str(s)]['weapons'] == [WEAPON]]
    first = [s for s in attacks if s in level_one][:3]
    bar = (first + [s for s in attacks if s not in first])[:BAR_SIZE]
    out = {}
    for race, creation in CREATION.items():
        for gender in (1, 2):
            out[f'{creation}:{gender}'] = {
                'pc_id': PC_IDS[(race, gender)], 'class': NAME, 'race': race, 'resource': 'MP',
                'skills': level_one, 'slots': [[i, s] for i, s in enumerate(first)],
                'bar': [[i, s] for i, s in enumerate(bar)], 'ours': True}
    return out


def apply():
    rules = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))
    ok, bad = check_rule(rules)
    if bad:
        raise SystemExit(f'Unlock-level rule fails on {bad} released-class unlocks; not writing Fighter')
    unlocks = fighter_unlocks(rules)
    rules['classes'][NAME] = {'weapon_type': WEAPON, 'starter_weapons': dict(STARTER_WEAPONS), 'unlocks': unlocks, 'ours': {
        'unlocks': 'rebuilt from SkillAcquireData Mastery rows: unlock level = NeedCharacterLevel of skill level 2 '
                   f'({ok}/{ok} recovered unlocks of the released classes follow it); 19000000/19000100 at 1, 19000001 at 4 (their acquisition rows)',
        'starter_weapons': 'OURS: the only gauntlets in Item.dat (150960002/150960003, level 1); no c_begin gauntlet exists'}}
    CLASS_SKILLS.write_text(json.dumps(rules, indent=1) + '\n', encoding='utf-8')
    starters = json.loads(STARTERS.read_text(encoding='utf-8'))
    starters.update(profiles(rules, unlocks))
    STARTERS.write_text(json.dumps(starters, indent=2) + '\n', encoding='utf-8')
    try:
        from . import progression
    except ImportError:
        import progression
    generated = progression.regenerate()['generated']
    print(f'Fighter: {len(unlocks)} unlocks to level {unlocks[-1][1]}, '
          f"{len(generated['unlocks'].get(NAME, []))} OURS unlocks past 25, profiles {sorted(CREATION.values())}")


if __name__ == '__main__':
    apply()
