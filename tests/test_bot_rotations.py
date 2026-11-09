import json
import unittest

from tools import bot_rotations


class BotRotationTests(unittest.TestCase):
    def setUp(self):
        self.data = bot_rotations.load()
        self.tracked = json.loads(bot_rotations.OUT.read_text(encoding='utf-8'))

    def test_tracked_file_is_current(self):
        self.assertEqual(bot_rotations.OUT.read_text(encoding='utf-8'), bot_rotations.text(self.data),
                         'run python tools/bot_rotations.py')

    def test_every_skill_exists_for_its_class(self):
        self.assertEqual(bot_rotations.validate(self.tracked, self.data), [])
        for name, entry in self.tracked['classes'].items():
            unlocks = {sid for sid, _, _ in self.data['classes'][name]['unlocks']}
            for step in entry['rotation']:
                with self.subTest(name=name, skill=step['skill']):
                    self.assertIn(str(step['skill']), self.data['skills'])
                    self.assertIn(step['skill'], unlocks)

    def test_every_playable_class_has_a_level_one_damage_skill(self):
        self.assertEqual(set(self.tracked['classes']), set(bot_rotations.CLASSES))
        for name, entry in self.tracked['classes'].items():
            with self.subTest(name):
                damage = [s for s in entry['rotation'] if s['when']['target'] == 'enemy']
                self.assertTrue(any(s['level'] == 1 for s in damage))
                self.assertTrue(all(self.data['skills'][str(s['skill'])]['damage'] for s in damage))
                # The last entry is a filler: no cooldown, so the bot always has something to cast.
                self.assertEqual(self.data['skills'][str(entry['rotation'][-1]['skill'])]['cooldown_ms'], 0)

    def test_validator_reports_foreign_and_unknown_skills(self):
        bad = {'classes': {'Gladiator': {'range': 300, 'rotation': [
            {'skill': 12010000, 'level': 1}, {'skill': 1, 'level': 1}, {'skill': 11020000, 'level': 2},
            {'skill': 11020000, 'level': 1, 'when': {'target': 'moon', 'mana': 3}}]}, 'Bard': {'range': 1}}}
        problems = bot_rotations.validate(bad, self.data)
        self.assertTrue(any('not a Gladiator skill' in p for p in problems))
        self.assertTrue(any('does not exist' in p for p in problems))
        self.assertTrue(any('unlocks at 1' in p for p in problems))
        self.assertTrue(any('unknown target' in p for p in problems))
        self.assertTrue(any('unknown conditions' in p for p in problems))
        self.assertTrue(any(p.startswith('Bard') for p in problems))

    def test_support_entries_come_from_skill_effects(self):
        effects = bot_rotations.load_effects()
        cleric = self.tracked['classes']['Cleric']['rotation']
        heals = [s for s in cleric if s['skill'] == 17100000]
        self.assertEqual([(s['when']['target'], s['roles']) for s in heals], [('ally', ['heal']), ('ally', ['dps'])])
        # Priority: every support entry comes before the first damage entry, heals before buffs.
        for name, entry in self.tracked['classes'].items():
            with self.subTest(name):
                targets = [s['when']['target'] for s in entry['rotation']]
                first_enemy = targets.index('enemy')
                self.assertTrue(all(t == 'enemy' for t in targets[first_enemy:]))
                kinds = [effects[str(s['skill'])]['effects'][0]['type'] for s in entry['rotation'][:first_enemy]]
                self.assertEqual(kinds, sorted(kinds, key=bot_rotations.SUPPORT_ORDER.index))
        chanter = self.tracked['classes']['Chanter']['rotation']
        self.assertTrue(any(s['skill'] == 18200000 and s['when'].get('buff_missing') for s in chanter), 'the Chanter keeps Defiance up')
        self.assertTrue(all(s['skill'] != 17100000 for s in self.tracked['classes']['Gladiator']['rotation']), 'no other class gets the Cleric heal')

    def test_resurrection_entries_wait_for_the_fight_to_end(self):
        effects = dict(bot_rotations.load_effects())
        effects['17100000'] = {'name': 'Rez', 'target': 'dead_friendly', 'effects': [{'type': 'resurrect', 'hp_ratio': [3000, 3000]}]}
        steps = bot_rotations.rotation(self.data, 'Cleric', effects)
        rez = [s for s in steps if s['skill'] == 17100000]
        self.assertEqual([s['when'] for s in rez], [{'target': 'dead_ally', 'in_combat': False}])
        self.assertLess(steps.index(rez[0]), [s['when']['target'] for s in steps].index('enemy'))

    def test_validator_checks_effects_against_targets(self):
        bad = {'classes': {'Cleric': {'range': 300, 'roles': ['heal', 'dps'], 'rotation': [
            {'skill': 17100000, 'level': 10, 'when': {'target': 'enemy'}},                      # a heal at a monster
            {'skill': 17120000, 'level': 12, 'when': {'target': 'ally'}},                       # a self-only heal on an ally
            {'skill': 17100000, 'level': 10, 'when': {'target': 'dead_ally'}},                  # no resurrect effect
            {'skill': 17100000, 'level': 10, 'when': {'target': 'ally', 'buff_missing': True}}, # a heal leaves no buff
            {'skill': 17040000, 'level': 1, 'when': {'target': 'self'}},                        # damage only, no effect
            {'skill': 17100000, 'level': 10, 'when': {'target': 'ally'}, 'roles': ['tank']}]}}}
        problems = bot_rotations.validate(bad, self.data)
        for i, text in enumerate(['not on an enemy', 'not on ally', 'no resurrect effect', 'leaves no buff',
                                  'no skill-effects.json entry', 'not roles of Cleric']):
            with self.subTest(text):
                self.assertTrue(any(p.startswith(f'Cleric[{i}]') and text in p for p in problems), problems)


if __name__ == '__main__':
    unittest.main()
