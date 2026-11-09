import json
import unittest

from tools import bot_rotations


class BotRotationTests(unittest.TestCase):
    def setUp(self):
        self.data = bot_rotations.load()
        self.effects = bot_rotations.load_effects()
        self.progression = bot_rotations.load_progression()
        self.tracked = json.loads(bot_rotations.OUT.read_text(encoding='utf-8'))

    def skill(self, sid):
        return bot_rotations.skill_info(self.data, sid, self.progression)

    def steps(self, name, sid):
        return [s for s in self.tracked['classes'][name]['rotation'] if s['skill'] == sid]

    def test_tracked_file_is_current(self):
        self.assertEqual(bot_rotations.OUT.read_text(encoding='utf-8'), bot_rotations.text(self.data),
                         'run python tools/bot_rotations.py')

    def test_every_skill_exists_for_its_class(self):
        self.assertEqual(bot_rotations.validate(self.tracked, self.data), [])
        for name, entry in self.tracked['classes'].items():
            unlocks = bot_rotations.unlocks_of(self.data, name, self.progression)
            for step in entry['rotation']:
                with self.subTest(name=name, skill=step['skill']):
                    self.assertIsNotNone(self.skill(step['skill']))
                    self.assertEqual(step['level'], unlocks[step['skill']])

    def test_every_playable_class_has_a_level_one_damage_skill_and_ends_on_a_filler(self):
        self.assertEqual(set(self.tracked['classes']), set(bot_rotations.CLASSES))
        for name, entry in self.tracked['classes'].items():
            with self.subTest(name):
                damage = [s for s in entry['rotation'] if s['when']['target'] == 'enemy']
                self.assertTrue(any(s['level'] == 1 and not set(s['when']) - {'target'} for s in damage))
                self.assertTrue(all(self.skill(s['skill'])['damage'] or self.effects.get(str(s['skill']), {}).get('target') == 'enemy' for s in damage))
                # The last entry has no conditions and no cooldown, so the bot always has something to cast.
                last = entry['rotation'][-1]
                self.assertEqual(self.skill(last['skill'])['cooldown_ms'], 0)
                self.assertEqual(last['when'], {'target': 'enemy'})
                self.assertNotIn('area', last)

    def test_every_class_skill_past_25_is_used(self):
        for name in bot_rotations.CLASSES:
            used = {s['skill'] for s in self.tracked['classes'][name]['rotation']}
            for sid, level, _ in self.progression['unlocks'][name]:
                if self.skill(sid)['type'] == 'Active':
                    with self.subTest(name=name, skill=sid):
                        self.assertIn(sid, used)
                        self.assertGreater(level, 25)

    def test_each_class_interrupts_with_a_stun(self):
        # The Cleric's level 1-25 kit has no stun (Bolt stuns only with a specialization), so it has no interrupt.
        for name in (c for c in bot_rotations.CLASSES if c != 'Cleric'):
            with self.subTest(name):
                interrupts = [s for s in self.tracked['classes'][name]['rotation'] if s['when'].get('target_casting')]
                self.assertTrue(interrupts, f'{name} has no interrupt')
                for s in interrupts:
                    self.assertIn('stun', {e['type'] for e in self.effects[str(s['skill'])]['effects']})
                    self.assertNotIn('self_mp_above', s['when'], 'an interrupt is never held back for MP')
                # Interrupts come before every other enemy entry but their own preparation.
                enemy = [s for s in self.tracked['classes'][name]['rotation'] if s['when']['target'] == 'enemy']
                self.assertTrue(enemy[0]['when'].get('target_casting'))

    def test_tank_plans(self):
        poach = self.steps('Templar', 12130000)
        self.assertEqual([(s['roles'], s['when'].get('target_on_self')) for s in poach], [(['tank'], False)])
        self.assertEqual(self.effects['12130000']['effects'], [{'type': 'taunt'}])
        self.assertTrue(any(e['type'] == 'threat' for e in self.effects['12040000']['effects']))
        self.assertEqual(self.tracked['classes']['Templar']['roles'][0], 'tank')
        self.assertEqual(self.tracked['classes']['Templar']['position'], 'front')

    def test_area_and_combo_entries(self):
        # Packs: Keen Strike (Gladiator) hits around the bot only with 2+ monsters, else the single-target copy.
        keen = self.steps('Gladiator', 11020000)
        self.assertEqual([(s.get('area'), s['when'].get('enemies_at_least')) for s in keen], [('self', 2), (None, None)])
        # Follow-ups: Judgment after a shield skill, Condemnation on the Cleric's own Chain of Torment.
        self.assertEqual(self.steps('Templar', 12240000)[0]['when']['after'], [12100000, 12350000])
        self.assertEqual(self.steps('Cleric', 17350000)[0]['when']['target_has'], [17070000])
        # Staggered-only skills wait for a stunned (or staggered) target.
        for name, sid in (('Gladiator', 11280000), ('Assassin', 13340000), ('Ranger', 14330000), ('Sorcerer', 15010000),
                          ('Elementalist', 16340000), ('Cleric', 17370000), ('Chanter', 18300000), ('Templar', 12340000)):
            with self.subTest(name):
                self.assertTrue(all(s['when'].get('target_stunned') for s in self.steps(name, sid)))

    def test_round_two_entries(self):
        # Crit-only follow-ups wait for the bot's own critical hit.
        for name, sid in (('Assassin', 13350000), ('Ranger', 14050000)):
            with self.subTest(name):
                self.assertTrue(self.steps(name, sid) and all(s['when'].get('after_critical') for s in self.steps(name, sid)))
        # Interrupt skills are kept for a boss's casts: their other copies skip bosses.
        for name, sid in (('Gladiator', 11290000), ('Templar', 12100000), ('Chanter', 18060000)):
            with self.subTest(name):
                others = [s for s in self.steps(name, sid) if not s['when'].get('target_casting')]
                self.assertTrue(others and all(s['when'].get('target_boss') is False for s in others))
        # The heal Cleric keeps its HoT on the tank in a fight.
        hot = [s for s in self.steps('Cleric', 17090000) if s['when']['target'] == 'tank']
        self.assertEqual([(s['roles'], s['when'].get('in_combat')) for s in hot], [(['heal'], True)])

    def test_skipped_skills_stay_out(self):
        for name, entry in self.tracked['classes'].items():
            for step in entry['rotation']:
                self.assertNotIn(step['skill'], bot_rotations.SKIPPED)
        self.assertEqual(set(self.tracked['skipped']), {str(k) for k in bot_rotations.SKIPPED})

    def test_healers_keep_mp_for_heals(self):
        cleric = self.tracked['classes']['Cleric']
        self.assertEqual(cleric['role_ranges'], {'heal': 1500})
        for step in cleric['rotation']:
            if step['when']['target'] != 'enemy' or not self.skill(step['skill'])['cost']['mp']:
                continue
            with self.subTest(step=step):
                if 'heal' in step.get('roles', cleric['roles']):
                    self.assertGreaterEqual(step['when'].get('self_mp_above', 0), bot_rotations.HEAL_ROLE_MP_FLOOR)

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

    def test_validator_checks_the_new_conditions(self):
        bad = {'classes': {'Gladiator': {'range': 300, 'roles': ['dps', 'tank'], 'position': 'sideways', 'rotation': [
            {'skill': 11020000, 'level': 1, 'when': {'target': 'enemy', 'target_casting': True}},       # Keen Strike stuns nothing
            {'skill': 11020000, 'level': 1, 'when': {'target': 'enemy', 'target_effect_missing': True}},  # leaves nothing
            {'skill': 11020000, 'level': 1, 'when': {'target': 'enemy', 'after': [17070000]}},          # a Cleric skill
            {'skill': 11260000, 'level': 16, 'when': {'target': 'self', 'enemies_at_least': 2}},        # enemy condition on self
            {'skill': 11020000, 'level': 1, 'area': 'moon', 'when': {'target': 'enemy'}},
            {'skill': 16330000, 'level': 7, 'when': {'target': 'enemy'}}]}}}                             # skipped (spirits)
        problems = bot_rotations.validate(bad, self.data)
        self.assertTrue(any('unknown position' in p for p in problems), problems)
        for i, text in enumerate(['does not interrupt', 'leaves nothing', 'not a Gladiator skill', 'enemy conditions',
                                  'area must be', 'is skipped']):
            with self.subTest(text):
                self.assertTrue(any(p.startswith(f'Gladiator[{i}]') and text in p for p in problems), problems)

    def test_block_evasion_and_dodge_procs_are_planned(self):
        procs = {'Gladiator': {11200000: 'after_block', 11360000: 'after_dodge'},
                 'Templar': {12270000: 'after_block', 12430000: 'after_dodge'},
                 'Assassin': {13210000: 'after_evasion', 13360000: 'after_dodge'},
                 'Chanter': {18150000: 'after_block', 18210000: 'after_dodge'}}
        for name, skills in procs.items():
            for sid, condition in skills.items():
                with self.subTest(name=name, skill=sid):
                    steps = self.steps(name, sid)
                    self.assertTrue(steps and all(s['when'].get(condition) for s in steps))
        self.assertEqual(set(bot_rotations.SKIPPED), {16330000, 16300000})

    def test_support_entries_come_from_skill_effects(self):
        cleric = self.tracked['classes']['Cleric']['rotation']
        heals = [s for s in cleric if s['skill'] == 17100000]
        self.assertEqual([(s['when']['target'], s['roles']) for s in heals], [('ally', ['heal']), ('ally', ['dps'])])
        self.assertEqual(cleric[0]['skill'], 17100000)
        # Priority: every support entry comes before the first damage entry, heals before buffs.
        for name, entry in self.tracked['classes'].items():
            with self.subTest(name):
                targets = [s['when']['target'] for s in entry['rotation']]
                first_enemy = targets.index('enemy')
                self.assertTrue(all(t == 'enemy' for t in targets[first_enemy:]))
                kinds = [self.effects[str(s['skill'])]['effects'][0]['type'] for s in entry['rotation'][:first_enemy]]
                self.assertEqual(kinds, sorted(kinds, key=bot_rotations.SUPPORT_ORDER.index))
        chanter = self.tracked['classes']['Chanter']['rotation']
        self.assertTrue(any(s['skill'] == 18200000 and s['when'].get('buff_missing') for s in chanter), 'the Chanter keeps Defiance up')
        self.assertTrue(all(s['skill'] != 17100000 for s in self.tracked['classes']['Gladiator']['rotation']), 'no other class gets the Cleric heal')

    def test_resurrection_entries_wait_for_the_fight_to_end(self):
        effects = dict(self.effects)
        effects['17100000'] = {'name': 'Rez', 'target': 'dead_friendly', 'effects': [{'type': 'resurrect', 'hp_ratio': [3000, 3000]}]}
        steps = bot_rotations.rotation(self.data, 'Cleric', effects)
        rez = [s for s in steps if s['skill'] == 17100000 and s['when']['target'] != 'enemy']
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
