"""Character progression past the recovered tables: OURS fallbacks, data-driven (tools/data/progression.json).

Inputs, all tracked: tools/data/class-skills.json (tools/class_skills.py: SkillAcquireData, Skill.dat, PcStatLevel),
tools/data/dev-menu.json (DevTeleportDataTable: the client's own developer buttons) and the hand-edited "params" of
tools/data/progression.json. Rebuild the generated sections with `python tools/progression.py`.

Why this exists (docs/PROGRESSION.md has the details):

- SkillAcquireData's auto-learned rows stop at character level 25. The client's developer buttons Class_<class> list the
  class's whole level-45 kit (LearnSkill <id> <level>); 12-13 skills per class there are acquired some other way
  (stigma, skill points or ascension rows the class_skills.py policy filters out, CANDIDATE). OURS: those skills unlock
  by level, spread evenly over params.unlock.first_level .. last_level, and reach the DB through
  `python tools/class_skills.py install` like the recovered unlocks.
- Those skills, and a few recovered active skills, have no Damage rows (never exported, or not in the export at all).
  OURS: a fallback definition built from the class's comparable damage skills (same cooldown band): the band's median
  cooldown, MP cost, hit count and per-hit flat/ratio. Effect IDs follow the recovered pattern skill x 100 + 11.
- Fallback skills get skill-point levels 2-10 on the recovered cost ladder (params.mastery).

Nothing here is retail data; every generated entry carries "ours": true.
"""
import json
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/progression.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
DEV_MENU = ROOT / 'tools/data/dev-menu.json'
BANDS = ('short', 'mid', 'long')


def is_passive(skill_id):
    """CANDIDATE id rule (212 of 235 recovered class skills; the misses are the weapon-equip and dodge specials):
    the 7x/8x skill families and the variant IDs (non-zero last four digits) are passives."""
    return (skill_id // 10000) % 100 >= 70 or skill_id % 10000 != 0


def band_of(cooldown_ms, edges):
    """'short' up to edges[0] ms, 'mid' up to edges[1], else 'long'."""
    return BANDS[0] if cooldown_ms <= edges[0] else BANDS[1] if cooldown_ms <= edges[1] else BANDS[2]


def extra_skills(class_name, classes, common, dev_buttons):
    """Skills of the developer kit Class_<class> that no recovered unlock gives the class, sorted by ID."""
    kit = {int(line.split()[1]) for line in dev_buttons.get(f'Class_{class_name}', [])
           if line.startswith('LearnSkill ') and len(line.split()) >= 2}
    known = {s for s, _, _ in classes[class_name]['unlocks']} | {s for s, _, _ in common}
    return sorted(kit - known)


def spread(skills, first, last):
    """[(skill, need level)] with actives and passives interleaved, evenly from first to last."""
    actives = [s for s in skills if not is_passive(s)]
    passives = [s for s in skills if is_passive(s)]
    # Interleave by relative position so neither kind bunches at one end.
    order = sorted([(i / len(actives), 0, s) for i, s in enumerate(actives)] + [(i / len(passives), 1, s) for i, s in enumerate(passives)])
    ordered = [s for _, _, s in order]
    if len(ordered) == 1:
        return [(ordered[0], first)]
    return [(s, first + round(i * (last - first) / (len(ordered) - 1))) for i, s in enumerate(ordered)]


def comparable(class_name, rules, edges):
    """The class's recovered damage skills by cooldown band: {band: [definition]}."""
    pool = {}
    for skill, _, _ in rules['classes'][class_name]['unlocks']:
        definition = rules['skills'].get(str(skill))
        if definition and definition['type'] == 'Active' and definition['damage'] and definition['cooldown_ms'] > 0:
            pool.setdefault(band_of(definition['cooldown_ms'], edges), []).append(definition)
    return pool


def median_int(values):
    return int(round(statistics.median(values))) if values else 0


def fallback_definition(skill_id, band, pool, weapon, name=None, base=None):
    """An OURS definition from the band's comparable skills (or the nearest band that has any)."""
    sources = pool.get(band) or next((pool[b] for b in sorted(pool, key=lambda b: abs(BANDS.index(b) - BANDS.index(band)))), [])
    rows = [r for d in sources for r in d['damage']]
    hits = median_int([len(d['damage']) for d in sources]) or 1
    flat = median_int([r['flat'][0] or 0 for r in rows])
    ratio = median_int([r['ratio'][0] or 0 for r in rows])
    pvp_flat = median_int([r['pvp']['flat'][0] or 0 for r in rows if r.get('pvp')])
    pvp_ratio = median_int([r['pvp']['ratio'][0] or 0 for r in rows if r.get('pvp')])
    base = base or {}
    return {
        'name': base.get('name') or name, 'type': 'Active', 'target': base.get('target') or 'MainTarget',
        'weapons': base.get('weapons') or ([weapon] if weapon else ['None']),
        'cooldown_ms': base['cooldown_ms'] if base.get('cooldown_ms') else median_int([d['cooldown_ms'] for d in sources]),
        'casting_ms': base.get('casting_ms', 0),
        'cost': base.get('cost') or {'mp': median_int([d['cost']['mp'] for d in sources]), 'hp': 0, 'sp': 0},
        'damage': [{'flat': [flat, flat], 'ratio': [ratio, ratio], 'value4': None,
                    'pvp': {'flat': [pvp_flat, pvp_flat], 'ratio': [pvp_ratio, pvp_ratio]},
                    'effect_id': skill_id * 100 + 11} for _ in range(hits)],
        'ours': True, 'band': band, 'comparable': sorted({int(r['effect_id']) // 100 for r in rows}),
    }


def passive_definition(skill_id, weapon):
    return {'name': None, 'type': 'Passive', 'target': 'Self', 'weapons': [weapon] if weapon else ['None'], 'cooldown_ms': 0,
            'casting_ms': 0, 'cost': {'mp': 0, 'hp': 0, 'sp': 0}, 'damage': [], 'ours': True}


def mastery(need, params, max_level):
    """Skill levels 2.. on the recovered cost ladder, one step every params.step levels after the unlock."""
    return [[level, cost, min(max_level, need + params['step_levels'] * (level - 1))]
            for level, cost in enumerate(params['costs'], start=2)]


def build(params, rules, dev_buttons):
    unlock = params['unlock']
    edges = params['cooldown_bands_ms']
    max_level = rules['max_level']
    first, last = unlock['first_level'], unlock.get('last_level') or max_level
    unlocks, skills, levels = {}, {}, {}
    for class_name, cls in rules['classes'].items():
        extras = extra_skills(class_name, rules['classes'], rules['common_unlocks'], dev_buttons)
        if not extras or not cls['unlocks']:
            continue
        placed = spread(extras, first, last)
        unlocks[class_name] = [[s, need, 1] for s, need in placed]
        pool = comparable(class_name, rules, edges)
        active_index = 0
        for skill, need in placed:
            if str(skill) in rules['skills']:
                continue   # recovered cast data exists
            if is_passive(skill):
                skills[str(skill)] = passive_definition(skill, cls['weapon_type'])
                continue
            band = BANDS[active_index % len(BANDS)]   # unknown cooldown: the bands in turn
            active_index += 1
            skills[str(skill)] = fallback_definition(skill, band, pool, cls['weapon_type'])
            if str(skill) not in rules['levels']:
                levels[str(skill)] = mastery(need, params['mastery'], max_level)
    # Recovered active skills without Damage rows that do hit (params.damage_fallbacks, each with its reason).
    damage = {}
    for skill_id in sorted(params['damage_fallbacks'], key=int):
        base = rules['skills'].get(skill_id)
        owner = next((c for c, cls in rules['classes'].items() if any(str(s) == skill_id for s, _, _ in cls['unlocks'])), None)
        if base is None or owner is None or base['damage']:
            continue
        definition = fallback_definition(int(skill_id), band_of(base['cooldown_ms'], edges), comparable(owner, rules, edges),
                                         rules['classes'][owner]['weapon_type'], base=base)
        damage[skill_id] = definition['damage']
    return {'unlocks': unlocks, 'skills': skills, 'levels': levels, 'damage': damage}


def load(path=DATA):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def regenerate(path=DATA, class_skills_path=CLASS_SKILLS, dev_menu_path=DEV_MENU):
    document = load(path)
    rules = json.loads(Path(class_skills_path).read_text(encoding='utf-8'))
    dev = json.loads(Path(dev_menu_path).read_text(encoding='utf-8'))['buttons']
    document['generated'] = build(document['params'], rules, dev)
    Path(path).write_text(json.dumps(document, indent=1) + '\n', encoding='utf-8')
    return document


def main():
    document = regenerate()
    generated = document['generated']
    for name, rows in generated['unlocks'].items():
        print(f"{name}: {len(rows)} OURS unlocks at levels {rows[0][1]}..{rows[-1][1]}")
    print(f"{len(generated['skills'])} fallback definitions, {len(generated['damage'])} damage fallbacks, "
          f"{len(generated['levels'])} skill-level ladders")


if __name__ == '__main__':
    main()
