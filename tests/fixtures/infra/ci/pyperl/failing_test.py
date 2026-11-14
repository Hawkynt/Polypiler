# A module test with one assertion that cannot hold: unittest has to report the failure.
import unittest


class FailingTest(unittest.TestCase):

  def test_fails(self):
    self.assertEqual(1 + 1, 3)


if __name__ == "__main__":
  unittest.main()
