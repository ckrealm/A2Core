import json
import unittest

from tools import boss_phases


class BossPhaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.events, cls.world = boss_phases.load()
        cls.built = boss_phases.build(cls.events, cls.world)
        cls.tracked = json.loads(boss_phases.OUT.read_text(encoding='utf-8'))

    def test_tracked_file_is_current(self):
        self.assertEqual(boss_phases.OUT.read_text(encoding='utf-8'), boss_phases.text(self.built), 'run python tools/boss_phases.py')

    def test_valid(self):
        self.assertEqual(boss_phases.validate(self.tracked, self.world), [])

    def test_every_season_one_final_boss_is_scripted(self):
        finals = {str(room['cube_boss']) for room in self.events['dungeons'].values()
                  if room.get('cube_boss') and any(room['name'].startswith(f + '_') for f in boss_phases.EXPEDITIONS)
                  and room['name'].rsplit('_', 1)[1] in boss_phases.MODE}
        self.assertEqual(len(finals), 18)
        for npc in finals:
            with self.subTest(npc):
                self.assertEqual(self.tracked['bosses'][npc]['role'], 'final')

    def test_frenzy_only_on_hard_modes(self):
        for npc, script in self.tracked['bosses'].items():
            with self.subTest(npc):
                hard = script['dungeon'].endswith('_Hard') and script['role'] == 'final'
                self.assertEqual('frenzy' in script, hard)
                if hard:
                    self.assertEqual(script['frenzy']['after_ms'], 240000)

    def test_slams_use_the_bosses_own_long_casts(self):
        krao = self.tracked['bosses']['2320171']['telegraph']
        self.assertEqual((krao['skill'], krao['wind_up_ms']), (1800140, 8000))
        durbati = self.tracked['bosses']['2301014']['telegraph']
        self.assertEqual(durbati['wind_up_ms'], boss_phases.FALLBACK_WIND_UP_MS)

    def test_raid_waves_are_named_spawns_and_horn_den_is_hand_made(self):
        self.assertEqual(sorted(self.tracked['waves']), ['FierceHornCave_Easy_Wave_01', 'FierceHornCave_Hard_Wave_01', 'FierceHornCave_Wave_01'])
        names = {s[0] for s in self.world['maps']['600082']}
        self.assertIn('ReforgedAbyss_1PartyWave_09_2', names)


if __name__ == '__main__':
    unittest.main()
