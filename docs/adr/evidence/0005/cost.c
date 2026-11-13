/* Cost per compared element of the comparison modes of ADR 0005, over 10 000 000 doubles. */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define N 10000000

static double seconds(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec * 1e-9; }
static int64_t ordinal(uint64_t b) { int64_t m = (int64_t)(b & INT64_MAX); return b >> 63 ? -m : m; }

int main(void) {
  uint64_t *a = malloc(N * 8), *b = malloc(N * 8), state = 20260101;
  for (int i = 0; i < N; ++i) {
    state = state * 6364136223846793005ULL + 1442695040888963407ULL;
    double v = (double)(state >> 11) / 9007199254740992.0;
    memcpy(&a[i], &v, 8);
    b[i] = a[i] + (i & 1);
  }
  volatile long passed = 0;
  double t = seconds();
  for (int i = 0; i < N; ++i) passed += a[i] == b[i];
  printf("exact      %.2f ns per element\n", (seconds() - t) * 1e9 / N);
  t = seconds();
  for (int i = 0; i < N; ++i) passed += llabs(ordinal(a[i]) - ordinal(b[i])) <= 2;
  printf("ulp        %.2f ns per element\n", (seconds() - t) * 1e9 / N);
  t = seconds();
  for (int i = 0; i < N; ++i) { double x, y; memcpy(&x, &a[i], 8); memcpy(&y, &b[i], 8); passed += fabs(y - x) <= 1e-12 * fabs(x); }
  printf("relative   %.2f ns per element\n", (seconds() - t) * 1e9 / N);
  t = seconds();
  for (int i = 0; i < N; ++i) { double x, y; memcpy(&x, &a[i], 8); memcpy(&y, &b[i], 8); passed += fabs(y - x) <= 1e-12; }
  printf("absolute   %.2f ns per element\n", (seconds() - t) * 1e9 / N);
  return 0;
}
