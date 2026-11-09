import json
import unittest

from tools import stigmas

DOCUMENT = json.loads(stigmas.DATA.read_text(encoding='utf-8'))
PARAMS = DOCUMENT['params']


class StigmaTests(unittest.TestCase):
    def test_global_slots_and_costs(self):
        self.assertEqual(PARAMS['slot_levels'][:4], [22, 27, 32, 37])
        cost = {level: band['shards'] for band in PARAMS['costs'] for level in range(band['from'], band['to'] + 1)}
        self.assertEqual(sorted(cost), list(range(1, PARAMS['max_skill_level'] + 1)))
        self.assertEqual(sum(cost.values()), 75)

    def test_shards_follow_the_levelling_table(self):
        totals = stigmas.shard_totals(PARAMS, 100)
        self.assertEqual((totals['21'], totals['22'], totals['27'], totals['37'], totals['40'], totals['45'], totals['100']), (0, 1, 6, 16, 20, 30, 30))

    def test_stigmas_are_the_active_kit_skills_nothing_else_gives(self):
        rules = {'common_unlocks': [[301, 1, 1]], 'levels': {},
                 'classes': {'Gladiator': {'unlocks': [[11010000, 1, 1]]}}}
        dev = {'Class_Gladiator': ['LearnSkill 11010000 1', 'LearnSkill 11080000 1', 'LearnSkill 11700000 1', 'LearnSkill 11000001 1', 'LearnSkill 301 1']}
        self.assertEqual(stigmas.class_stigmas('Gladiator', rules, dev), [11080000])

    def test_brawler_uses_its_mastery_rows_as_the_normal_kit(self):
        rules = {'common_unlocks': [], 'levels': {'19010000': [[2, 1, 1]]}, 'classes': {}}
        dev = {'Class_Fighter': ['LearnSkill 19000000 1', 'LearnSkill 19010000 1', 'LearnSkill 19270000 1', 'LearnSkill 19730000 1']}
        self.assertEqual(stigmas.class_stigmas('Fighter', rules, dev), [19270000])

    def test_recovered_rows_replace_the_candidates(self):
        built = stigmas.build({**PARAMS, 'recovered': {'Fighter': [[19460000, None, 30000], [19280000, None, 90000]]}},
                              {'max_level': 50, 'common_unlocks': [], 'levels': {}, 'classes': {}, 'skills': {}}, {})
        self.assertEqual(built['classes'], {'Fighter': [19280000, 19460000]})

    def test_tracked_file_is_current(self):
        rules = json.loads(stigmas.CLASS_SKILLS.read_text(encoding='utf-8'))
        dev = json.loads(stigmas.DEV_MENU.read_text(encoding='utf-8'))['buttons']
        from tools import progression
        self.assertEqual(DOCUMENT['generated'], stigmas.build(PARAMS, rules, dev, progression.load()['generated']['skills']))
        for name in ('Gladiator', 'Templar', 'Assassin', 'Ranger', 'Sorcerer', 'Elementalist', 'Cleric', 'Chanter', 'Fighter'):
            self.assertEqual(len(DOCUMENT['generated']['classes'][name]), 13, name)

    def test_every_stigma_is_active_with_its_recovered_cooldown(self):
        for class_name, entries in PARAMS['recovered'].items():
            for skill_id, name, cooldown in entries:
                definition = DOCUMENT['generated']['skills'][str(skill_id)]
                self.assertEqual((definition['type'], definition['cooldown_ms']), ('Active', cooldown), skill_id)
                self.assertTrue(definition['damage'])
        self.assertEqual(DOCUMENT['generated']['skills']['11700000']['name'], 'Assault Strike')


if __name__ == '__main__':
    unittest.main()
