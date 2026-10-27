import unittest
from inspect_samples import finite_number

class MissingCoordinateTests(unittest.TestCase):
    def test_blank_height_seen_in_lastrow_stays_missing(self):
        self.assertIsNone(finite_number(''))
        self.assertIsNone(finite_number('  '))
    def test_nan_tracking_value_stays_missing(self):
        self.assertIsNone(finite_number('NaN'))
    def test_actual_zero_is_not_missing(self):
        self.assertEqual(finite_number('0.0'),0.0)

if __name__=='__main__': unittest.main()
