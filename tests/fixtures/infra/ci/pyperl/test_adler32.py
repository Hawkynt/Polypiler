# The module test of adler32.py: one test, run by the standard library's unittest.
import unittest

from adler32 import adler32


class Adler32Test(unittest.TestCase):

  def test_checksums(self):
    self.assertEqual(adler32(b""), 1)
    self.assertEqual(adler32(b"a"), 6422626)
    self.assertEqual(adler32(b"abc"), 38600999)


if __name__ == "__main__":
  unittest.main()
