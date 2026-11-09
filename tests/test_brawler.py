import json
import unittest

from tools import brawler, class_skills, equipment_rules
from tools.starter_skills import starter_profile


class BrawlerTests(unittest.TestCase):
    def setUp(self):
        self.rules = class_skills.data()

    def test_unlock_rule_holds_for_every_released_class(self):
        ok, bad = brawler.check_rule(self.rules)
        self.assertGreater(ok, 150)
        self.assertEqual(bad, 0)

    def test_fighter_kit_is_the_recovered_skills_to_25_then_ours(self):
        unlocks = self.rules['classes']['Fighter']['unlocks']
        self.assertEqual(unlocks, brawler.fighter_unlocks(self.rules))
        self.assertEqual(len(unlocks), 25)
        self.assertEqual(dict((s, n) for s, n, _ in unlocks)[19070000], 3)   # Mastery level 2 needs 3
        self.assertEqual(max(n for _, n, _ in unlocks), 25)
        at_cap = class_skills.learned_skills('Fighter', 100)
        self.assertGreater(len(at_cap), len(class_skills.learned_skills('Fighter', 25)))
        self.assertLessEqual(len(at_cap), 127)
        kit = {int(l.split()[1]) for l in json.loads((class_skills.ROOT / 'tools/data/dev-menu.json').read_text())['buttons']['Class_Fighter']
               if l.startswith('LearnSkill')}
        self.assertTrue(kit <= {s for s, _ in at_cap})

    def test_gauntlet_is_the_weapon_and_starter(self):
        self.assertEqual(self.rules['classes']['Fighter']['weapon_type'], 'Gauntlet')
        for race in (1, 2):
            self.assertEqual(class_skills.weapon_type(class_skills.starter_weapon('Fighter', race)), 'Gauntlet')

    def test_profiles_for_both_races_and_genders(self):
        for race, creation in brawler.CREATION.items():
            for gender in (1, 2):
                profile = starter_profile(creation, gender)
                self.assertEqual((profile['class'], profile['race']), ('Fighter', race))
                self.assertEqual([s for s, _ in class_skills.learned_skills('Fighter', 1)], profile['skills'])
                self.assertTrue(all(s in profile['skills'] for _, s in profile['slots']))

    def test_only_brawlers_get_gauntlet_rules(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Gauntlet', 'ItemRace': 'ERace::All'}
        [(slot, _, creations)] = equipment_rules.rules(raw, equipment_rules.profiles())
        self.assertEqual((slot, creations), (1, sorted(brawler.CREATION.values())))


if __name__ == '__main__':
    unittest.main()
