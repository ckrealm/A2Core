import json
import unittest

from tools import fallback_loot


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


class FallbackLootTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.items = load(fallback_loot.ITEMS)['items']
        cls.top = fallback_loot.max_monster_level(load(fallback_loot.NPCS))

    def test_tracked_tables_match_the_builder(self):
        self.assertEqual(load(fallback_loot.FALLBACK), json.loads(json.dumps(fallback_loot.build_fallback(self.items, self.top))))
        self.assertEqual(load(fallback_loot.KINAH), json.loads(json.dumps(fallback_loot.build_kinah(self.top))))

    def test_fallback_uses_only_known_items_a_band_may_wear(self):
        table = load(fallback_loot.FALLBACK)
        self.assertEqual(table['bands'][0]['min'], 1)
        self.assertGreaterEqual(table['bands'][-1]['max'], self.top)
        for band in table['bands']:
            self.assertTrue(band['items'], band)
            for item in band['items']:
                row = self.items[str(item)]
                self.assertEqual(row['r'], 0)
                self.assertLessEqual(row['p'], band['min'])
        for chance in table['chance'].values():
            self.assertTrue(0 < chance <= 100_000_000)

    def test_bands_are_contiguous_and_kinah_grows_with_level(self):
        kinah = load(fallback_loot.KINAH)
        previous = 0
        for band in kinah['bands']:
            self.assertEqual(band['min'], previous + 1)
            low, high = band['kinah']
            self.assertTrue(0 < low <= high)
            previous = band['max']
        amounts = [b['kinah'][0] for b in kinah['bands']]
        self.assertEqual(amounts, sorted(amounts))
        self.assertEqual(set(kinah['chance']), set(kinah['multiplier']))

    def test_nearest_items_when_none_fit_the_band(self):
        items = {'1': {'c': 'Sword', 'l': 30, 'p': 1, 'r': 0}, '2': {'c': 'Sword', 'l': 3, 'p': 1, 'r': 1},
                 '3': {'c': 'Sword', 'l': 50, 'p': 1, 'r': 0}, '4': {'c': 'Sword', 'l': 2, 'p': 20, 'r': 0}}
        self.assertEqual(fallback_loot.band_items(items, 1, 5), [1])
        self.assertEqual(fallback_loot.band_items(items, 26, 30), [1])


if __name__ == '__main__':
    unittest.main()
