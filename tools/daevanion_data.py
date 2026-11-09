"""Daevanion boards from the client's own tables (the server's tools/data/daevanion.json).

Sources (a WorldProbe export of the installed client, research/local/daevanion-tables, see docs/DAEVANION.md):
  DaevanionBoard.dat        DaevanionBoardTableItem: ID, Class (ECharacterClass), NeedLevel, Title, Order, CostPointType
                            (EDaevanionPointType), ResetCostCurrencyType (ECurrencyCategory)
  DaevanionNode.dat         DaevanionNodeTableItem: ID, Board, Row, Col, Grade (EItemGrade), Type (EDaevanionNodeType),
                            bAutoLearn, NeedLevel, NeedConditionType/Value, CostDaevanionPoint, ResetGold, Value01..Value10
  DaevanionMaxCapacity.dat  DaevanionMaxCapacityTableItem: DaevanionPointType, MaxValue, CurrencyCategory
  Exp.dat                   ExpTableItem: Level, DaevanionPointMap {EDaevanionPointType: points at that level}
  CurrencyInfo.dat          (optional) CurrencyInfoTableItem: the item behind each ECurrencyCategory
  any other selected table  rows naming EItemUseEffect GetDaevanionPoint / Fix_DaevanionPoint (the point items) are kept raw

Tables are found by their client path through the export's summary.json, so the selection order does not matter.

Output tools/data/daevanion.json:
  boards:   id -> {class: ECharacterClass name, level: NeedLevel, order, point: EDaevanionPointType, reset: ECurrencyCategory,
                   title}
  nodes:    id -> {b: board, r: row, c: col, g: EItemGrade name, t: EDaevanionNodeType (1 Start, 2 Stat, 3 Passive,
                   4 SkillLevel), auto: bAutoLearn, lv: NeedLevel, ct: EDaevanionNodeConditionType, cv: NeedConditionValue,
                   cost: CostDaevanionPoint, gold: ResetGold, v: [Value01..Value10, trailing blanks dropped], n: title}
  capacity: EDaevanionPointType -> MaxValue
  level_points: level -> {EDaevanionPointType: points} (the Exp.dat map; cumulative or per level is decided by the server)
  currency_items: ECurrencyCategory name -> item id (when CurrencyInfo.dat was exported)
  point_items: [raw rows] naming a Daevanion point use effect
  stats:    EStat name -> id (the usmap's enum: the stat sheet's wire ids)

    python tools/daevanion_data.py select <table-inventory.json> <selection.json>   # the WorldProbe selection
    python tools/daevanion_data.py build [--tables research/local/daevanion-tables]
    python tools/daevanion_data.py empty    # the placeholder (stat ids only) before any export
"""
import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/daevanion-tables'
DATA = ROOT / 'tools/data/daevanion.json'
USMAP = ROOT / 'research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap'

POINT_TYPES = {'None': 0, 'DaevanionCrystal': 1, 'ConquestCrystal': 2, 'BattleCrystal': 3, 'Season02Crystal01': 4, 'Season03Crystal01': 5}
NODE_TYPES = {'None': 0, 'Start': 1, 'Stat': 2, 'Passive': 3, 'SkillLevel': 4}
CONDITIONS = {'None': 0, 'PrevNode_Grade': 1}
WANTED = ('daevanion', '/exp.dat', 'currencyinfo', 'itemuse', 'useeffect', 'itemfunction')


def _short(value):
    return str(value).split('::')[-1] if value is not None else ''


def _id(value):
    if isinstance(value, dict):
        return int(value.get('Value') or 0)
    return int(value or 0)


def _text(value):
    """A TableString / TableL10NText as the export writes it: a plain string or an object with a key or text."""
    if value is None:
        return ''
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        for key in ('Text', 'SourceString', 'LocalizedString', 'Key', 'Value', 'Name'):
            if isinstance(value.get(key), str) and value[key]:
                return value[key]
    return ''


def _map(value):
    """A MapProperty as the export writes it: [{Key, Value}, ...] or {key: value}."""
    if isinstance(value, dict):
        return list(value.items())
    return [(entry.get('Key'), entry.get('Value')) for entry in value or [] if isinstance(entry, dict)]


def select(inventory):
    """The WorldProbe selection (at most 64 paths): every Daevanion table, Exp.dat, CurrencyInfo.dat and the item-use tables."""
    paths = json.loads(Path(inventory).read_text(encoding='utf-8'))
    chosen = [p for p in paths if p.lower().endswith('.dat') and any(w in p.lower() for w in WANTED)
              and 'l10n' not in p.lower() and '/map/' not in p.lower()]
    return sorted(chosen)[:64]


def _tables(tables):
    """{client path basename lower: rows} from the export's summary.json."""
    summary = json.loads((tables / 'summary.json').read_text(encoding='utf-8'))
    found = {}
    for package in summary.get('packages', []):
        if package.get('status') != 'table_decoded':
            continue
        data = json.loads((tables / package['file']).read_text(encoding='utf-8'))['Properties']['Data']
        found[package['path'].rsplit('/', 1)[-1].lower()] = data if isinstance(data, list) else list(data.values())
    return found


def stat_ids(usmap=USMAP):
    """EStat name -> id from the usmap (empty when the mapping is absent)."""
    if not Path(usmap).exists():
        return {}
    sys.path.insert(0, str(ROOT / 'tools'))
    from usmap_dump import UsmapParser
    return {name: int(key) for key, name in UsmapParser(str(usmap)).enums.get('EStat', {}).items() if name not in ('None', 'Max')}


def empty():
    """The file before any client export: no boards, the stat ids only."""
    return {'source': 'placeholder: no client export yet (tools/daevanion_data.py build)', 'boards': {}, 'nodes': {}, 'capacity': {},
            'level_points': {}, 'currency_items': {}, 'point_items': [], 'stats': stat_ids()}


def build(tables=TABLES):
    tables = Path(tables)
    found = _tables(tables)

    def rows(name):
        if name.lower() not in found:
            raise SystemExit(f'{name} is not in the export {tables} (selection: python tools/daevanion_data.py select ...)')
        return found[name.lower()]

    boards = {}
    for row in rows('DaevanionBoard.dat'):
        boards[str(_id(row['ID']))] = {
            'class': _short(row.get('Class')), 'level': int(row.get('NeedLevel') or 0), 'order': int(row.get('Order') or 0),
            'point': POINT_TYPES.get(_short(row.get('CostPointType')), 1), 'reset': _short(row.get('ResetCostCurrencyType')),
            'title': _text(row.get('Title')),
        }
    nodes = {}
    for row in rows('DaevanionNode.dat'):
        values = [str(row.get(f'Value{i:02d}') or '').strip() for i in range(1, 11)]
        while values and not values[-1]:
            values.pop()
        nodes[str(_id(row['ID']))] = {
            'b': _id(row.get('Board')), 'r': int(row.get('Row') or 0), 'c': int(row.get('Col') or 0), 'g': _short(row.get('Grade')),
            't': NODE_TYPES.get(_short(row.get('Type')), 0), 'auto': bool(row.get('bAutoLearn')), 'lv': int(row.get('NeedLevel') or 0),
            'ct': CONDITIONS.get(_short(row.get('NeedConditionType')), 0), 'cv': str(row.get('NeedConditionValue') or ''),
            'cost': int(row.get('CostDaevanionPoint') or 0), 'gold': int(row.get('ResetGold') or 0), 'v': values, 'n': _text(row.get('Title')),
        }
    capacity = {str(POINT_TYPES.get(_short(r.get('DaevanionPointType')), 0)): int(r.get('MaxValue') or 0)
                for r in found.get('daevanionmaxcapacity.dat', [])}
    level_points = {}
    for row in found.get('exp.dat', []):
        points = {str(POINT_TYPES.get(_short(k), 0)): int(v or 0) for k, v in _map(row.get('DaevanionPointMap')) if int(v or 0)}
        if points:
            level_points[str(int(row.get('Level') or 0))] = points
    currency_items = {}
    for row in found.get('currencyinfo.dat', []):
        category = _short(row.get('CurrencyCategory'))
        item = next((_id(row[k]) for k in ('ItemId', 'Item', 'ItemID', 'ID') if k in row and _id(row[k])), 0)
        if category and item:
            currency_items[category] = item
    point_items = []
    marker = re.compile(r'(GetDaevanionPoint|Fix_DaevanionPoint)')
    for name, table in found.items():
        if 'daevanion' in name or name == 'exp.dat':
            continue
        for row in table:
            if marker.search(json.dumps(row)):
                point_items.append({'table': name, 'row': row})
    return {'source': 'DaevanionBoard/DaevanionNode/DaevanionMaxCapacity/Exp/CurrencyInfo .dat (tools/daevanion_data.py)',
            'boards': boards, 'nodes': nodes, 'capacity': capacity, 'level_points': level_points,
            'currency_items': currency_items, 'point_items': point_items, 'stats': stat_ids()}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    sel = sub.add_parser('select')
    sel.add_argument('inventory')
    sel.add_argument('output')
    bld = sub.add_parser('build')
    bld.add_argument('--tables', default=str(TABLES))
    bld.add_argument('--output', default=str(DATA))
    emp = sub.add_parser('empty')
    emp.add_argument('--output', default=str(DATA))
    args = parser.parse_args()
    if args.command == 'select':
        chosen = select(args.inventory)
        Path(args.output).write_text(json.dumps(chosen, indent=1) + '\n', encoding='utf-8')
        print('\n'.join(chosen))
        print(f'{len(chosen)} tables -> {args.output}', file=sys.stderr)
        return
    data = empty() if args.command == 'empty' else build(args.tables)
    Path(args.output).write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    classes = sorted({b['class'] for b in data['boards'].values()})
    print(f"{len(data['boards'])} boards ({', '.join(classes)}), {len(data['nodes'])} nodes, {len(data['level_points'])} levels with points, "
          f"{len(data['point_items'])} point items -> {args.output}")


if __name__ == '__main__':
    main()
