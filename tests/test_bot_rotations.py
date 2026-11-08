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
                self.assertTrue(any(s['level'] == 1 for s in entry['rotation']))
                self.assertTrue(all(self.data['skills'][str(s['skill'])]['damage'] for s in entry['rotation']))
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


if __name__ == '__main__':
    unittest.main()
