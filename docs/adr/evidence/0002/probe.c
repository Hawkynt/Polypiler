#include <stdio.h>
#include <limits.h>
#include <math.h>
#include <stdlib.h>
int main(int argc, char **argv) {
  volatile int max = INT_MAX, min = INT_MIN, one = 1, zero = 0, m1 = -1, s32 = 32, s33 = 33;
  volatile unsigned umax = UINT_MAX, uone = 1;
  volatile double big = 3e9, nbig = -3e9, nan = NAN, inf = INFINITY;
  int sel = argc > 1 ? atoi(argv[1]) : 0;
  switch (sel) {
  case 0: printf("unsigned add: %u  unsigned mul: %u  unsigned sub: %u\n", umax + uone, 65536u * (uone * 65536u), 0u - uone);
          printf("-7 / 3: %d  -7 %% 3: %d  7 %% -3: %d\n", -7 / (one * 3), -7 % (one * 3), 7 % (m1 * 3));
          printf("-8 >> 1: %d  (int)2.7: %d  (int)-2.7: %d  (unsigned char)300: %d\n", -8 >> one, (int)(2.7 * one), (int)(-2.7 * one), (unsigned char)(300 * one));
          printf("1.0 / 0: %f  lrint(2.5): %ld  lround(2.5): %ld\n", 1.0 / zero, lrint(2.5 * one), lround(2.5 * one)); break;
  case 1: printf("signed add: %d\n", max + one); break;
  case 2: printf("signed mul: %d\n", 65536 * (one * 65536)); break;
  case 3: printf("1 << 32: %d\n", one << s32); break;
  case 4: printf("1 << 33: %d\n", one << s33); break;
  case 5: printf("1 << -1: %d\n", one << m1); break;
  case 6: printf("7 / 0: %d\n", 7 / zero); break;
  case 7: printf("7 %% 0: %d\n", 7 % zero); break;
  case 8: printf("MIN / -1: %d\n", min / m1); break;
  case 9: printf("MIN %% -1: %d\n", min % m1); break;
  case 10: printf("(int)3e9: %d\n", (int)big); break;
  case 11: printf("(int)-3e9: %d\n", (int)nbig); break;
  case 12: printf("(int)NaN: %d\n", (int)nan); break;
  case 13: printf("(int)Inf: %d\n", (int)inf); break;
  case 14: printf("signed sub: %d\n", min - one); break;
  case 15: printf("-1 << 1: %d\n", m1 << one); break;
  case 16: printf("-MIN: %d\n", -min); break;
  }
  return 0;
}
