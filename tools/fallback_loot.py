"""Generic loot and Kinah for monsters (OURS: our own tables, nothing observed).

Only about a quarter of the spawned monsters have reward tables in the client (tools/data/loot.json, RewardData), and
none of those tables hold Kinah. This builder writes two server tables from the tracked data:

- tools/data/fallback-loot.json: for monsters without a reward table (Aion:Rules:FallbackLoot), one chance per loot roll
  by NpcSubType of one item picked evenly from the monster's level band. The items are the equipment of
  tools/data/items.json (the only item table with levels; it holds no consumables or materials) that every race can
  use (ItemRace 0), whose PermitLevelMin is at most the band's lowest level and whose ItemLevel is within
  ITEM_LEVEL_SLACK of the band. No item ID is made up: every one comes from items.json.
- tools/data/kinah-drops.json: Kinah for every monster, by level band and NpcSubType: a chance and a min-max amount
  (KINAH_BASE + KINAH_PER_LEVEL x the band's lowest level, up to KINAH_SPREAD times that), times the rank's multiplier.

Rebuild with `python tools/fallback_loot.py` after items.json or world-npcs.json changes.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ITEMS = ROOT / 'tools/data/items.json'
NPCS = ROOT / 'tools/data/world-npcs.json'
FALLBACK = ROOT / 'tools/data/fallback-loot.json'
KINAH = ROOT / 'tools/data/kinah-drops.json'

# OURS: level bands of five, item level within 5 of the band.
BAND = 5
ITEM_LEVEL_SLACK = 5
# OURS: chance (of 1e8, as loot.json) per loot roll of one generic item, by NpcData NpcSubType.
FALLBACK_CHANCE = {'NormalMonster': 3_000_000, 'EliteMonster': 15_000_000, 'HeroMonster': 50_000_000}
# OURS: Kinah per kill. Quest rewards pay ~1.7k Kinah at levels 1-4 and ~36k at 10-14 (tools/data/quests.json medians);
# a band's kill pays a small share of that.
KINAH_CHANCE = {'NormalMonster': 70_000_000, 'EliteMonster': 100_000_000, 'HeroMonster': 100_000_000}
KINAH_MULTIPLIER = {'NormalMonster': 1, 'EliteMonster': 3, 'HeroMonster': 10}
KINAH_BASE, KINAH_PER_LEVEL, KINAH_SPREAD = 5, 5, 2


def max_monster_level(npcs):
    levels = [t['level'] for t in npcs['templates'].values() if t.get('type') == 'Monster']
    return max(levels) if levels else 1


def bands(top):
    return [(low, low + BAND - 1) for low in range(1, top + 1, BAND)]


def band_items(items, low, high):
    """Race-free equipment a character of the band's lowest level may wear, near the band's level (nearest when none)."""
    usable = [(int(k), v) for k, v in items.items() if v.get('r', 0) == 0 and v.get('p', 1) <= low]
    near = [k for k, v in usable if low - ITEM_LEVEL_SLACK <= v.get('l', 1) <= high + ITEM_LEVEL_SLACK]
    if not near and usable:
        gap = min(min(abs(v['l'] - low), abs(v['l'] - high)) for _, v in usable)
        near = [k for k, v in usable if min(abs(v['l'] - low), abs(v['l'] - high)) == gap]
    return sorted(near)


def build_fallback(items, top):
    return {'source': 'OURS: items.json equipment (ItemRace 0) by PermitLevelMin/ItemLevel per level band (tools/fallback_loot.py)',
            'chance': FALLBACK_CHANCE,
            'bands': [{'min': low, 'max': high, 'items': band_items(items, low, high)} for low, high in bands(top)]}


def build_kinah(top):
    rows = []
    for low, high in bands(top):
        least = KINAH_BASE + KINAH_PER_LEVEL * low
        rows.append({'min': low, 'max': high, 'kinah': [least, least * KINAH_SPREAD]})
    return {'source': 'OURS: Kinah per monster kill by level band and NpcSubType (tools/fallback_loot.py)',
            'chance': KINAH_CHANCE, 'multiplier': KINAH_MULTIPLIER, 'bands': rows}


def main():
    items = json.loads(ITEMS.read_text(encoding='utf-8'))['items']
    top = max_monster_level(json.loads(NPCS.read_text(encoding='utf-8')))
    fallback = build_fallback(items, top)
    FALLBACK.write_text(json.dumps(fallback, separators=(',', ':')) + '\n', encoding='utf-8')
    KINAH.write_text(json.dumps(build_kinah(top), separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(fallback['bands'])} level bands to level {top} written to {FALLBACK.relative_to(ROOT)} and {KINAH.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
