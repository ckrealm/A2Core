"""Class skill kits that follow character level, plus the cast data each skill needs.

Derived from offline exports: SkillAcquireData (research/local/starter-skills-tables
table-002), PCCreationInfo (table-000), PcStatLevel (table-007) and the item/skill
catalogue (research/local/game-catalog-v2). Rebuild the tracked summary with
`python tools/class_skills.py`.

Rule (server policy chosen by the user, not a recovered retail rule): a character
knows every auto-learned acquisition for its class and for all classes
(ECharacterClass::None) whose NeedCharacterLevel is at most the character level,
without ascension or other prerequisites. Skill-point upgrades (skill levels 2+),
stigma-point skills and ascension skills are separate systems and stay locked.

The auto-learned rows end at level 25. The rest of each class kit (the skills the client's
developer button Class_<class> teaches) unlocks by level from 26 to the cap through the OURS
table tools/data/progression.json (tools/progression.py); `install` seeds both into
game.skill_unlocks (docs/PROGRESSION.md).
"""
import json
from collections import Counter
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/class-skills.json'
PROGRESSION = ROOT / 'tools/data/progression.json'
TABLES = ROOT / 'research/local/starter-skills-tables'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
COMMON = 'None'


def _short(value):
    return str(value).split('::')[-1]


def damage_rows(entry):
    """Damage effect values of a skill, one entry per Damage row.

    Candidate reading of EffectValueList (not a recovered formula): [0..1] flat damage
    min/max, [2..3] weapon-attack ratio min/max in 1/10000, [4] kept raw; [21..24] the
    same four values again, read as the player-vs-player variant.

    Only Damage rows are kept: the heal, Abnormal_True (buff/debuff) and other EffectTypes of the same
    relationships.effects rows are not exported yet, so the server's heals and buffs come from the hand-made
    tools/data/skill-effects.json (OURS). Exporting those rows here would let that table be generated.
    """
    rows = []
    for group in entry.get('relationships', {}).get('effects', []):
        for row in group.get('rows', []):
            values = row.get('EffectValueList', [])
            if _short(row.get('EffectType')) != 'Damage' or len(values) < 4:
                continue
            number = lambda i: int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else None
            rows.append({'flat': [number(0), number(1)], 'ratio': [number(2), number(3)], 'value4': values[4] if len(values) > 4 else None,
                         'pvp': {'flat': [number(21), number(22)], 'ratio': [number(23), number(24)]} if len(values) > 24 else None,
                         'effect_id': row['ID']['Value']})
    return rows


def build(tables=TABLES, catalog=CATALOG):
    def rows(index):
        return json.loads((tables / f'table-{index:03}.json').read_text(encoding='utf-8'))['Properties']['Data']
    creations, acquire, stat_levels = rows(0), rows(2), rows(7)
    skills, begin_weapons, weapon_stats = {}, {}, {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry['kind'] == 'skills':
                skills[entry['id']] = entry
            elif entry['kind'] == 'items' and '_c_begin_001a_b' in entry['internal_name'] and entry['raw'].get('EquipCategory'):
                # equip_wp_<Type>_<L|D>_c_begin_001a_b: the level-one weapon of each type.
                side = entry['internal_name'].split('_')[3]
                begin_weapons.setdefault(entry['category'], {})['1' if side == 'L' else '2'] = entry['id']
                stats = {_short(s['Key']): s['Value'] for s in entry['facts'].get('MainStats', [])}
                weapon_stats[str(entry['id'])] = {'min': stats.get('WeaponMinDamage', 0), 'max': stats.get('WeaponDamage', 0),
                                                  'critical': stats.get('Critical', 0), 'accuracy': stats.get('WeaponAccuracy', 0)}

    unlocks = {}
    for row in acquire:
        if (not row['bAutoLearn'] or row['NeedPrevSkillDataList'] or row['CostItemDataList']
                or row['CostSkillPoint'] or row['CostStigmaPoint'] or row['CostStigmaSuperiorPoint']
                or row['NeedAscensionGrade'] != 'EAscensionGrade::None'):
            continue
        cls = _short(row['ClassType'])
        unlocks.setdefault(cls, []).append([row['SkillId']['Value'], row['NeedCharacterLevel'], row['SkillLevel']])
    for entries in unlocks.values():
        entries.sort(key=lambda e: (e[1], e[0], e[2]))
    # Skill levels bought with skill points (AcquireType Mastery, SkillLevel 2+): [level, CostSkillPoint, NeedCharacterLevel].
    levels = {}
    for row in acquire:
        if row['SkillLevel'] > 1 and _short(row['AcquireType']) == 'Mastery':
            levels.setdefault(str(row['SkillId']['Value']), []).append([row['SkillLevel'], row['CostSkillPoint'], row['NeedCharacterLevel']])
    for entries in levels.values():
        entries.sort()

    classes = {}
    for creation in creations:
        cls = _short(creation['Class'])
        if cls in classes:
            continue
        # The class weapon is the one its own auto-learned skills most often require.
        weapons = Counter(w for skill, _, _ in unlocks.get(cls, ())
                          for w in skills.get(skill, {}).get('facts', {}).get('NeedMainWeaponTypes', []))
        weapon = _short(weapons.most_common(1)[0][0]) if weapons else None
        classes[cls] = {'weapon_type': weapon, 'starter_weapons': begin_weapons.get(weapon, {}),
                        'unlocks': unlocks.get(cls, [])}

    cast = {}
    for skill in sorted({s for entries in unlocks.values() for s, _, _ in entries}):
        facts = skills.get(skill, {}).get('facts', {})
        cast[str(skill)] = {
            'name': skills.get(skill, {}).get('name'),
            'icon': facts.get('SkillIcon'),
            'type': _short(facts.get('SkillType', '')),
            'target': _short(facts.get('TargetProcessType', '')),
            'weapons': [_short(w) for w in facts.get('NeedMainWeaponTypes', [])],
            'cooldown_ms': int(facts.get('NeedCoolTime') or 0),
            'casting_ms': int(facts.get('CastingTime') or 0),
            'cost': {k: int(facts.get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')},
            'damage': damage_rows(skills.get(skill, {})),
        }
    # Per-class base stats by level (PcStatLevel): FixingDamage, Defense, HPMax, ...
    stats = {}
    for row in stat_levels:
        stats.setdefault(_short(row['Class']), {})[str(row['Level'])] = {_short(s['Key']): s['Value'] for s in row['StatList']}
    return {'max_level': max(r['Level'] for r in stat_levels), 'common': COMMON,
            'stats': stats, 'weapon_stats': weapon_stats,
            'classes': {k: v for k, v in classes.items()}, 'common_unlocks': unlocks.get(COMMON, []),
            'skills': cast, 'levels': levels,
            'evidence': {'acquire': 'SkillAcquireData bAutoLearn rows without costs/prerequisites/ascension',
                         'levels': 'SkillAcquireData Mastery rows of SkillLevel 2+: level, CostSkillPoint, NeedCharacterLevel',
                         'max_level': 'PcStatLevel highest Level',
                         'damage': 'Damage EffectValueList, candidate reading: flat min/max, weapon ratio min/max (1/10000)',
                         'weapon_type': "most frequent NeedMainWeaponTypes of the class's auto-learned skills",
                         'starter_weapons': 'Item.dat equip_wp_<Type>_<L|D>_c_begin_001a_b (L for race 1, D for race 2)'}}


@lru_cache(maxsize=1)
def data():
    return json.loads(DATA.read_text(encoding='utf-8'))


@lru_cache(maxsize=1)
def progression():
    """The OURS part of the kit (tools/data/progression.json 'generated', written by tools/progression.py)."""
    return json.loads(PROGRESSION.read_text(encoding='utf-8')).get('generated', {})


def class_unlocks(class_name):
    """[skill, need level, skill level] of a class: the recovered rows (to level 25), then the OURS rows to the cap."""
    return data()['classes'].get(class_name, {}).get('unlocks', []) + progression().get('unlocks', {}).get(class_name, [])


def learned_skills(class_name, level):
    """[(skill_id, skill_level)] known at `level`, sorted by skill ID."""
    known = {}
    rules = data()
    for skill, need, skill_level in class_unlocks(class_name) + rules['common_unlocks']:
        if need <= level:
            known[skill] = max(known.get(skill, 0), skill_level)
    return sorted(known.items())


def skill(skill_id):
    """Recovered cast data (with OURS Damage rows where the export has none), else an OURS fallback definition."""
    recovered = data()['skills'].get(str(skill_id))
    if recovered is None:
        return progression().get('skills', {}).get(str(skill_id))
    rows = progression().get('damage', {}).get(str(skill_id))
    return dict(recovered, damage=rows) if rows and not recovered['damage'] else recovered


def starter_weapon(class_name, race):
    """Level-one weapon of the class's weapon type for race 1 (Light) or 2 (Dark)."""
    return data()['classes'].get(class_name, {}).get('starter_weapons', {}).get(str(race))


def weapon_type(item_id):
    """Weapon type of a known starter weapon, or None."""
    for cls in data()['classes'].values():
        if item_id in cls.get('starter_weapons', {}).values():
            return cls['weapon_type']
    return None


def clamp_level(level):
    return max(1, min(int(level), data()['max_level']))


def unlock_rows():
    """(creation_id, skill_id, lowest unlock level, skill level) for every creation ID."""
    starters = json.loads((ROOT / 'tools/data/starter-skills.json').read_text(encoding='utf-8'))
    classes = {int(key.split(':')[0]): profile['class'] for key, profile in starters.items()}
    rows = []
    for creation, name in sorted(classes.items()):
        lowest = {}
        for skill_id, need, skill_level in class_unlocks(name) + data()['common_unlocks']:
            if skill_id not in lowest or need < lowest[skill_id][0]:
                lowest[skill_id] = (need, skill_level)
        rows += [(creation, skill_id, need, skill_level) for skill_id, (need, skill_level) in sorted(lowest.items())]
    return rows


def install_statements(build_id, rows):
    """Replace the build's unlock policy and resync every stored character, atomically."""
    try:
        from .postgres_dev import literal
    except ImportError:
        from postgres_dev import literal
    values = ',\n'.join(f'({literal(build_id)},{c},{s},{n},{l})' for c, s, n, l in rows)
    return '\n'.join([
        'BEGIN; SET LOCAL ROLE aion_owner;',
        f'DELETE FROM game.skill_unlocks WHERE build_id={literal(build_id)};',
        f'INSERT INTO game.skill_unlocks(build_id,creation_id,skill_id,need_level,skill_level) VALUES\n{values};',
        'SELECT count(game.sync_character_skills(character_id,creation_id,level)) FROM game.characters;',
        'COMMIT;'])


def main(argv=None):
    import argparse
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', nargs='?', choices=['build', 'install'], default='build')
    parser.add_argument('--build-id', help='catalogue build for install (default: the only imported one)')
    args = parser.parse_args(argv)
    if args.command == 'build':
        result = build()
        DATA.write_text(json.dumps(result, indent=1) + '\n', encoding='utf-8')
        for name, cls in result['classes'].items():
            print(f"{name}: {len(cls['unlocks'])} unlocks, weapon {cls['weapon_type']} {cls['starter_weapons']}")
        print(f"{len(result['common_unlocks'])} common unlocks, {len(result['skills'])} skills, max level {result['max_level']}")
        # The OURS unlocks and fallbacks past level 25 derive from this table: keep them in step.
        try:
            from . import progression as ours
        except ImportError:
            import progression as ours
        ours.regenerate()
        print('progression.json regenerated (OURS unlocks past level 25, fallback skills)')
        return
    try:
        from .postgres_dev import sql, literal
    except ImportError:
        from postgres_dev import sql, literal
    builds = sql('SELECT build_id FROM catalog.builds ORDER BY build_id;').stdout.split()
    build_id = args.build_id or (builds[0] if len(builds) == 1 else None)
    if build_id not in builds:
        raise SystemExit('Choose an imported catalogue with --build-id')
    rows = unlock_rows()
    found = {int(v) for v in sql(f'SELECT template_id FROM catalog.skills WHERE build_id={literal(build_id)};').stdout.split()}
    missing = sorted({s for _, s, _, _ in rows} - found)
    if missing:
        # OURS unlocks (tools/data/progression.json) come from the client's developer kits; a catalogue without one
        # of them only loses that unlock. Recovered unlocks stay strict.
        ours = {s for unlocks in json.loads((ROOT / 'tools/data/progression.json').read_text(encoding='utf-8'))
                .get('generated', {}).get('unlocks', {}).values() for s, *_ in unlocks}
        strict = [s for s in missing if s not in ours]
        if strict:
            raise SystemExit(f'Catalogue build lacks {len(strict)} unlock skills, e.g. {strict[:5]}')
        print(f'Warning: skipping {len(missing)} OURS unlock skills the catalogue lacks: {missing[:10]}')
        rows = [r for r in rows if r[1] not in set(missing)]
    synced = sql(install_statements(build_id, rows)).stdout.split()
    print(f'Installed {len(rows)} unlock rows for {len({c for c, *_ in rows})} creation IDs; '
          f'resynced {synced[-1] if synced else 0} characters.')


if __name__ == '__main__':
    main()
