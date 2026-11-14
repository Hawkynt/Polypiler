# Adler-32 as RFC 1950 section 8.2 defines it: two sums modulo 65521, packed as (b << 16) | a.
MOD_ADLER = 65521


def adler32(data):
  a, b = 1, 0
  for byte in data:
    a = (a + byte) % MOD_ADLER
    b = (b + a) % MOD_ADLER
  return (b << 16) | a


if __name__ == "__main__":
  print(adler32(b"abc"))
