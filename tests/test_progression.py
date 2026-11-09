import json
import unittest

from tools import class_skills, progression

CLASSES = ('Gladiator', 'Templar', 'Assassin', 'Cleric', 'Ranger', 'Elementalist', 'Sorcerer', 'Chanter')


def rules(unlocks, skills, levels=None):
    """A minimal class-skills.json for one class."""
    return {'max_level': 100, 'common_unlocks': [[301, 1, 1]], 'levels': levels or {},
            'classes': {'Gladiator': {'weapon_type': 'Greatsword', 'unlocks': unlocks}}, 'skills': skills}


def damage_skill(skill_id, cooldown, flat, ratio, hits=1, mp=0):
    return {'name': str(skill_id), 'type': 'Active', 'target': 'MainTarget', 'weapons': ['Greatsword'], 'cooldown_ms': cooldown,
            'casting_ms': 0, 'cost': {'mp': mp, 'hp': 0, 'sp': 0},
            'damage': [{'flat': [flat, flat], 'ratio': [ratio, ratio], 'pvp': {'flat': [flat, flat], 'ratio': [ratio, ratio]},
                        'effect_id': skill_id * 100 + 11}] * hits}


PARAMS = json.loads(progression.DATA.read_text(encoding='utf-8'))['params']


class BuilderTests(unittest.TestCase):
    def test_passive_rule_matches_the_recovered_families(self):
        self.assertTrue(progression.is_passive(11710000))    # Survival Stance
        self.assertTrue(progression.is_passive(11170037))    # Overhead Slam variant
        self.assertFalse(progression.is_passive(11010000))   # Rending Blow

    def test_extras_are_the_developer_kit_minus_recovered_unlocks(self):
        data = rules([[11010000, 1, 1]], {})
        dev = {'Class_Gladiator': ['ClassChange Gladiator', 'LearnSkill 11010000 1', 'LearnSkill 11080000 1', 'LearnSkill 301 1', 'LearnSkill 11700000 1']}
        self.assertEqual(progression.extra_skills('Gladiator', data['classes'], data['common_unlocks'], dev), [11080000, 11700000])

    def test_spread_runs_evenly_from_first_to_last(self):
        placed = progression.spread([11080000, 11110000, 11700000, 11710000], 26, 100)
        self.assertEqual([need for _, need in placed], [26, 51, 75, 100])
        self.assertEqual(sorted(s for s, _ in placed), [11080000, 11110000, 11700000, 11710000])
        self.assertEqual(progression.spread([11080000], 26, 100), [(11080000, 26)])

    def test_fallback_takes_the_band_median_of_comparable_skills(self):
        data = rules([[11010000, 1, 1], [11020000, 3, 1], [11030000, 5, 1], [11040000, 7, 1]],
                     {'11010000': damage_skill(11010000, 5000, 100, 10000, hits=2), '11020000': damage_skill(11020000, 8000, 300, 20000, hits=2),
                      '11030000': damage_skill(11030000, 20000, 500, 30000, mp=200), '11040000': damage_skill(11040000, 0, 50, 4000)})
        pool = progression.comparable('Gladiator', data, [10000, 30000])
        self.assertEqual(sorted(pool), ['mid', 'short'])   # the 0 ms basic attack is not comparable
        short = progression.fallback_definition(11080000, 'short', pool, 'Greatsword')
        self.assertEqual((short['cooldown_ms'], len(short['damage']), short['damage'][0]['flat'], short['damage'][0]['ratio']), (6500, 2, [200, 200], [15000, 15000]))
        self.assertEqual(short['damage'][0]['effect_id'], 1108000011)
        self.assertTrue(short['ours'])
        long = progression.fallback_definition(11090000, 'long', pool, 'Greatsword')   # no long skill: nearest band (mid)
        self.assertEqual((long['cooldown_ms'], long['cost']['mp'], long['damage'][0]['flat']), (20000, 200, [500, 500]))

    def test_build_marks_everything_ours_and_keeps_recovered_skills(self):
        data = rules([[11010000, 1, 1]], {'11010000': damage_skill(11010000, 5000, 100, 10000)})
        dev = {'Class_Gladiator': ['LearnSkill 11080000 1', 'LearnSkill 11700000 1']}
        out = progression.build(PARAMS, data, dev)
        self.assertEqual(out['unlocks']['Gladiator'], [[11080000, 26, 1], [11700000, 100, 1]])
        self.assertEqual(out['skills']['11700000']['type'], 'Passive')
        self.assertTrue(all(d['ours'] for d in out['skills'].values()))
        self.assertEqual(out['levels']['11080000'][0], [2, 1, 29])
        self.assertTrue(all(row[2] <= 100 for row in out['levels']['11080000']))


class TrackedTableTests(unittest.TestCase):
    def test_tracked_table_is_up_to_date(self):
        rules_data = json.loads(progression.CLASS_SKILLS.read_text(encoding='utf-8'))
        dev = json.loads(progression.DEV_MENU.read_text(encoding='utf-8'))['buttons']
        tracked = json.loads(progression.DATA.read_text(encoding='utf-8'))
        self.assertEqual(json.loads(json.dumps(progression.build(tracked['params'], rules_data, dev))), tracked['generated'])

    def test_unlocks_continue_to_the_cap_for_every_class(self):
        cap = class_skills.data()['max_level']
        for name in CLASSES:
            with self.subTest(name):
                at25 = dict(class_skills.learned_skills(name, 25))
                at_cap = dict(class_skills.learned_skills(name, cap))
                later = set(at_cap) - set(at25)
                self.assertGreaterEqual(len(later), 10)
                self.assertTrue(all(class_skills.skill(s) is not None for s in later))

    def test_install_rows_carry_the_ours_unlocks(self):
        rows = class_skills.unlock_rows()
        self.assertTrue(any(need > 25 for _, _, need, _ in rows))
        first = class_skills.class_unlocks('Gladiator')[-1]
        self.assertIn((1, first[0], first[1], 1), rows)

    def test_damage_fallback_only_where_the_export_has_none(self):
        self.assertTrue(class_skills.skill(17150000)['damage'])                     # Divine Aura: OURS rows
        self.assertEqual(class_skills.skill(11010000), class_skills.data()['skills']['11010000'])   # recovered rows stay


if __name__ == '__main__':
    unittest.main()
