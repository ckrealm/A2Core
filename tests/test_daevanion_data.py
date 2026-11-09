"""tools/daevanion_data.py: the board tables of a WorldProbe export become tools/data/daevanion.json."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import daevanion_data  # noqa: E402


def _export(directory, tables):
    packages = []
    for index, (path, rows) in enumerate(tables.items()):
        name = f'table-{index:03d}.json'
        (directory / name).write_text(json.dumps({'Properties': {'Data': rows}}), encoding='utf-8')
        packages.append({'path': path, 'status': 'table_decoded', 'file': name})
    (directory / 'summary.json').write_text(json.dumps({'packages': packages}), encoding='utf-8')


class DaevanionDataTests(unittest.TestCase):
    def test_build_reads_boards_nodes_capacity_and_level_points(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            _export(directory, {
                'Aion2/Content/Data/Daevanion/DaevanionBoard.dat': [
                    {'ID': {'Value': 101}, 'Class': 'ECharacterClass::Gladiator', 'NeedLevel': 10, 'Order': 1,
                     'CostPointType': 'EDaevanionPointType::DaevanionCrystal', 'ResetCostCurrencyType': 'ECurrencyCategory::Gold', 'Title': 'Nezakan'}],
                'Aion2/Content/Data/Daevanion/DaevanionNode.dat': [
                    {'ID': {'Value': 1001}, 'Board': {'Value': 101}, 'Row': 0, 'Col': 0, 'Grade': 'EItemGrade::Common',
                     'Type': 'EDaevanionNodeType::Start', 'bAutoLearn': True, 'NeedLevel': 0, 'NeedConditionType': 'EDaevanionNodeConditionType::None',
                     'NeedConditionValue': '', 'CostDaevanionPoint': 0, 'ResetGold': 0, 'Value01': '', 'Value02': ''},
                    {'ID': {'Value': 1002}, 'Board': {'Value': 101}, 'Row': 0, 'Col': 1, 'Grade': 'EItemGrade::Rare',
                     'Type': 'EDaevanionNodeType::Stat', 'bAutoLearn': False, 'NeedLevel': 12, 'NeedConditionType': 'EDaevanionNodeConditionType::PrevNode_Grade',
                     'NeedConditionValue': 'Rare', 'CostDaevanionPoint': 3, 'ResetGold': 5000, 'Value01': 'HPMax', 'Value02': '500', 'Value03': ''}],
                'Aion2/Content/Data/Daevanion/DaevanionMaxCapacity.dat': [
                    {'DaevanionPointType': 'EDaevanionPointType::DaevanionCrystal', 'MaxValue': 300, 'CurrencyCategory': 'ECurrencyCategory::DaevanionPoint'}],
                'Aion2/Content/Data/Exp.dat': [
                    {'Level': 10, 'DaevanionPointMap': [{'Key': 'EDaevanionPointType::DaevanionCrystal', 'Value': 2}]},
                    {'Level': 11, 'DaevanionPointMap': []}],
                'Aion2/Content/Data/Item/ItemUseEffect.dat': [
                    {'ID': {'Value': 900}, 'UseEffect': 'EItemUseEffect::GetDaevanionPoint', 'UseEffectValues': ['1', '10']},
                    {'ID': {'Value': 901}, 'UseEffect': 'EItemUseEffect::GetKina'}],
            })
            data = daevanion_data.build(directory)
        self.assertEqual(data['boards']['101'], {'class': 'Gladiator', 'level': 10, 'order': 1, 'point': 1, 'reset': 'Gold', 'title': 'Nezakan'})
        self.assertEqual(data['nodes']['1001']['t'], 1)
        self.assertTrue(data['nodes']['1001']['auto'])
        self.assertEqual(data['nodes']['1001']['v'], [])
        node = data['nodes']['1002']
        self.assertEqual((node['b'], node['r'], node['c'], node['g'], node['t'], node['lv'], node['ct'], node['cv'], node['cost'], node['gold'], node['v']),
                         (101, 0, 1, 'Rare', 2, 12, 1, 'Rare', 3, 5000, ['HPMax', '500']))
        self.assertEqual(data['capacity'], {'1': 300})
        self.assertEqual(data['level_points'], {'10': {'1': 2}})
        self.assertEqual([p['row']['ID']['Value'] for p in data['point_items']], [900])
        self.assertEqual(data['stats'].get('HPMax'), 193)

    def test_select_takes_the_board_tables_and_leaves_maps_and_text(self):
        with tempfile.TemporaryDirectory() as tmp:
            inventory = Path(tmp) / 'inv.json'
            inventory.write_text(json.dumps(['A/Data/Daevanion/DaevanionNode.dat', 'A/Data/Exp.dat', 'A/Data/Map/1000/MapData.dat',
                                             'A/Data/L10N/en/DaevanionString.dat', 'A/Data/Quest.dat', 'A/Data/CurrencyInfo.dat']), encoding='utf-8')
            self.assertEqual(daevanion_data.select(inventory), ['A/Data/CurrencyInfo.dat', 'A/Data/Daevanion/DaevanionNode.dat', 'A/Data/Exp.dat'])

    def test_bundled_file_has_the_layout_the_server_reads(self):
        data = json.loads(daevanion_data.DATA.read_text(encoding='utf-8'))
        for key in ('boards', 'nodes', 'capacity', 'level_points', 'stats'):
            self.assertIn(key, data)
        self.assertEqual(data['stats']['Critical'], 128)


if __name__ == '__main__':
    unittest.main()
