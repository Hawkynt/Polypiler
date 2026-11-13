/* Arbitrary-precision reference for the ADR 0005 input set, computed with GNU MPFR.
 *
 * For every input line it writes
 *   <correctly rounded result> exact|nan
 *   <correctly rounded result> inexact <lo> <q> <frac>
 * where lo is the double adjacent to the exact result on the side of zero, 2^q is the spacing of
 * doubles at the exact result and frac in (0, 1) is the position of the exact result between lo
 * and its neighbour away from zero. Rounding to double is done here in integer arithmetic, so the
 * program does not depend on MPFR's exponent-range emulation. */
#include <inttypes.h>
#include <math.h>
#include <mpfr.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define FUNCTION_PRECISION 400   /* bits; the transcendental results */
#define EXACT_PRECISION 4400     /* bits; holds a + b, a * b and a * b + c of doubles without rounding */

static double from_bits(uint64_t b) { double d; memcpy(&d, &b, 8); return d; }
static uint64_t to_bits(double d) { uint64_t b; memcpy(&b, &d, 8); return b; }

int main(void) {
  char name[16], cls[16], line[128];
  mpfr_t x, y, z, e, t, k;
  mpfr_inits2(64, x, y, z, (mpfr_ptr)0);
  mpfr_init2(e, EXACT_PRECISION);
  mpfr_init2(t, EXACT_PRECISION);
  mpfr_init2(k, EXACT_PRECISION);
  while (fgets(line, sizeof line, stdin)) {
    uint64_t b[3] = {0, 0, 0};
    sscanf(line, "%15s %15s %" SCNx64 " %" SCNx64 " %" SCNx64, name, cls, &b[0], &b[1], &b[2]);
    mpfr_set_d(x, from_bits(b[0]), MPFR_RNDN);
    mpfr_set_d(y, from_bits(b[1]), MPFR_RNDN);
    mpfr_set_d(z, from_bits(b[2]), MPFR_RNDN);
    int arithmetic = !strcmp(name, "add") || !strcmp(name, "sub") || !strcmp(name, "mul") || !strcmp(name, "div")
      || !strcmp(name, "fma");
    mpfr_set_prec(e, arithmetic ? EXACT_PRECISION : FUNCTION_PRECISION);
    if (!strcmp(name, "sin")) mpfr_sin(e, x, MPFR_RNDN);
    else if (!strcmp(name, "cos")) mpfr_cos(e, x, MPFR_RNDN);
    else if (!strcmp(name, "tan")) mpfr_tan(e, x, MPFR_RNDN);
    else if (!strcmp(name, "exp")) mpfr_exp(e, x, MPFR_RNDN);
    else if (!strcmp(name, "log")) mpfr_log(e, x, MPFR_RNDN);
    else if (!strcmp(name, "log2")) mpfr_log2(e, x, MPFR_RNDN);
    else if (!strcmp(name, "log10")) mpfr_log10(e, x, MPFR_RNDN);
    else if (!strcmp(name, "pow")) mpfr_pow(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "atan2")) mpfr_atan2(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "sinh")) mpfr_sinh(e, x, MPFR_RNDN);
    else if (!strcmp(name, "cosh")) mpfr_cosh(e, x, MPFR_RNDN);
    else if (!strcmp(name, "tanh")) mpfr_tanh(e, x, MPFR_RNDN);
    else if (!strcmp(name, "cbrt")) mpfr_cbrt(e, x, MPFR_RNDN);
    else if (!strcmp(name, "hypot")) mpfr_hypot(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "add")) mpfr_add(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "sub")) mpfr_sub(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "mul")) mpfr_mul(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "div")) mpfr_div(e, x, y, MPFR_RNDN);
    else if (!strcmp(name, "sqrt")) mpfr_sqrt(e, x, MPFR_RNDN);
    else if (!strcmp(name, "fma")) mpfr_fma(e, x, y, z, MPFR_RNDN);
    else { fprintf(stderr, "unknown function %s\n", name); return 1; }

    if (mpfr_nan_p(e)) { puts("7ff8000000000000 nan"); continue; }
    if (mpfr_inf_p(e) || mpfr_zero_p(e)) { printf("%016" PRIx64 " exact\n", to_bits(mpfr_get_d(e, MPFR_RNDN))); continue; }

    int negative = mpfr_sgn(e) < 0;
    mpfr_abs(t, e, MPFR_RNDN);
    mpfr_exp_t exponent = mpfr_get_exp(t);             /* t in [2^(exponent-1), 2^exponent) */
    if (exponent > 1024) {                             /* beyond the largest finite double and its rounding boundary */
      printf("%016" PRIx64 " exact\n", to_bits(negative ? -INFINITY : INFINITY));
      continue;
    }
    long q = exponent - 53 < -1074 ? -1074 : (long)(exponent - 53);
    mpfr_mul_2si(t, t, -q, MPFR_RNDN);                 /* in units of the spacing; exact */
    mpfr_floor(k, t);
    mpfr_sub(t, t, k, MPFR_RNDN);                      /* fractional part in [0, 1) */
    double steps = mpfr_get_d(k, MPFR_RNDN);           /* < 2^53, exact */
    double lo = ldexp(steps, (int)q);
    double hi = ldexp(steps + 1, (int)q);              /* +inf when the result rounds past the largest double */
    if (mpfr_zero_p(t)) {
      printf("%016" PRIx64 " exact\n", to_bits(negative ? -lo : lo));
      continue;
    }
    int half = mpfr_cmp_d(t, 0.5);
    int up = half > 0 || (half == 0 && fmod(steps, 2.0) != 0.0);
    double rounded = up ? hi : lo;
    mpfr_printf("%016" PRIx64 " inexact %016" PRIx64 " %ld %.18Rf\n",
      to_bits(negative ? -rounded : rounded), to_bits(negative ? -lo : lo), q, t);
  }
  return 0;
}
