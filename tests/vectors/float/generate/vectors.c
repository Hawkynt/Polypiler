/* Reference vectors of the floating-point operations, computed with GNU MPFR.
 *
 * Usage: vectors <core-math src directory> <output directory>
 *
 * For every function and format it writes <name>.<f32|f64>.txt with one line per input:
 *
 *   <class> <operand>... <result> exact
 *   <class> <operand>... <result> nan
 *   <class> <operand>... <result> inexact <lo> <frac>
 *
 * Operands and results are the bits of the format in hexadecimal (8 digits for f32, 16 for f64);
 * the integer operand of rootn is written in decimal. <result> is the correctly rounded value
 * (round to nearest, ties to even). For an inexact result, <lo> is the value of the format adjacent
 * to the exact result on the side of zero and <frac> in (0, 1) the position of the exact result
 * between <lo> and its neighbour away from zero, so that the reference error of a computed value c
 * is |(c - lo) / (hi - lo) - frac| as in ADR 0005.
 *
 * The rounding follows docs/adr/evidence/0005/reference.c: the exact value is computed with 400
 * bits (4400 for fma, remainder and sqrt, where it is then exact) and rounded to the format in
 * integer arithmetic, so the program does not depend on MPFR's exponent-range emulation.
 *
 * Classes: core (1000 values in the range the function is mostly used in), domain (1000 values
 * log-uniform over the whole domain, subnormals included), special (the special values, all pairs
 * for two operands), edge (the domain edges and the overflow and underflow thresholds with their
 * neighbours), reduction (multiples of pi/2 near 2^20, 2^52, 10^22 and the largest finite value, for
 * the trigonometric functions) and worst (hard-to-round cases from the CORE-MATH worst-case files,
 * at most 400 per function, taken evenly from the file).
 *
 * The random values come from SplitMix64 seeded with 2026 plus the index of the function and
 * format, so the files are identical on every host. */
#include <ctype.h>
#include <inttypes.h>
#include <math.h>
#include <mpfr.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SEED 2026
#define GRID 1000
#define WORST 400
#define FUNCTION_PRECISION 400
#define EXACT_PRECISION 4400

typedef struct { int p, emin, emax, ebits, width; } format_t;
static const format_t F32 = {24, -126, 127, 8, 32};
static const format_t F64 = {53, -1022, 1023, 11, 64};

/* SplitMix64 */
static uint64_t state;
static uint64_t next(void) {
  uint64_t z = (state += 0x9E3779B97F4A7C15ull);
  z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
  z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
  return z ^ (z >> 31);
}
static double unit(void) { return (double)(next() >> 11) / 9007199254740992.0; }

/* A value of a format is carried as its bits; an mpfr_t holds it exactly. */
static uint64_t sign_bit(const format_t *f) { return 1ull << (f->width - 1); }
static uint64_t inf_bits(const format_t *f) { return ((1ull << f->ebits) - 1) << (f->p - 1); }
static uint64_t nan_bits(const format_t *f) { return inf_bits(f) | (1ull << (f->p - 2)); }
static uint64_t max_bits(const format_t *f) { return inf_bits(f) - 1; }
static uint64_t one_bits(const format_t *f) { return (uint64_t)((1 << (f->ebits - 1)) - 1) << (f->p - 1); }

static void to_mpfr(mpfr_t r, const format_t *f, uint64_t b) {
  uint64_t magnitude = b & ~sign_bit(f);
  int negative = (b & sign_bit(f)) != 0;
  if (magnitude > inf_bits(f)) { mpfr_set_nan(r); return; }
  if (magnitude == inf_bits(f)) { mpfr_set_inf(r, negative ? -1 : 1); return; }
  if (magnitude == 0) { mpfr_set_zero(r, negative ? -1 : 1); return; }
  uint64_t exponent = magnitude >> (f->p - 1);
  uint64_t significand = magnitude & ((1ull << (f->p - 1)) - 1);
  long e;
  if (exponent == 0) {
    e = f->emin - (f->p - 1);
  } else {
    significand |= 1ull << (f->p - 1);
    e = (long)exponent - (1 << (f->ebits - 1)) + 1 - (f->p - 1);
  }
  mpfr_set_prec(r, 64);
  mpfr_set_uj_2exp(r, significand, e, MPFR_RNDN);
  if (negative) mpfr_neg(r, r, MPFR_RNDN);
}

/* The bits of the value of a format with the given count of spacings 2^q, positive. */
static uint64_t from_steps(const format_t *f, mpz_t steps, long q) {
  /* steps < 2^p; value = steps * 2^q, q >= emin - p + 1 */
  uint64_t s = mpz_get_ui(steps);   /* steps <= 2^p */
  long qmin = f->emin - f->p + 1;
  if (s == 0) return 0;
  /* normalize: when s >= 2^p it is 2^p exactly after a carry */
  while (s >= (1ull << f->p)) { s >>= 1; ++q; }
  if (q == qmin && s < (1ull << (f->p - 1))) return s;          /* subnormal */
  while (s < (1ull << (f->p - 1))) { s <<= 1; --q; }
  long biased = q + (f->p - 1) + (1 << (f->ebits - 1)) - 1;
  if (biased >= (1 << f->ebits) - 1) return inf_bits(f);
  return ((uint64_t)biased << (f->p - 1)) | (s & ((1ull << (f->p - 1)) - 1));
}

typedef struct { uint64_t result, lo; int kind; char frac[40]; } rounded_t;

/* The bits of the value one step above steps * 2^q. */
static uint64_t hi_of(const format_t *f, mpz_t steps, long q) {
  mpz_t up;
  mpz_init(up);
  mpz_add_ui(up, steps, 1);
  uint64_t b = from_steps(f, up, q);
  mpz_clear(up);
  return b;
}
enum { EXACT, INEXACT, NOTANUMBER };

/* Rounds a value computed with 400 bits to the format, ties to even. The ternary value of the MPFR
 * function tells where the exact result lies when the 400 bits land on a value of the format or on a
 * midpoint between two: for atan(2^-1022 - 2^-1075) the exact result is below the midpoint by far
 * less than 400 bits can show. A negative ternary value says the computed value is below the exact
 * one, a positive one above, 0 that it is exact. */
static rounded_t round_to_side(const format_t *f, mpfr_t e, int ternary) {
  rounded_t r = {0, 0, EXACT, ""};
  if (mpfr_nan_p(e)) { r.result = nan_bits(f); r.kind = NOTANUMBER; return r; }
  int negative = mpfr_signbit(e);
  uint64_t sign = negative ? sign_bit(f) : 0;
  if (mpfr_inf_p(e)) { r.result = sign | inf_bits(f); return r; }
  if (mpfr_zero_p(e)) { r.result = sign; return r; }
  mpfr_t t, k;
  mpfr_init2(t, mpfr_get_prec(e) + 64);
  mpfr_init2(k, mpfr_get_prec(e) + 64);
  mpfr_abs(t, e, MPFR_RNDN);
  mpfr_exp_t exponent = mpfr_get_exp(t);               /* t in [2^(exponent-1), 2^exponent) */
  if (exponent > f->emax + 1) {
    r.result = sign | inf_bits(f);
    mpfr_clears(t, k, (mpfr_ptr)0);
    return r;
  }
  long qmin = f->emin - f->p + 1;
  long q = exponent - f->p < qmin ? qmin : (long)(exponent - f->p);
  mpfr_mul_2si(t, t, -q, MPFR_RNDN);                    /* exact */
  mpfr_floor(k, t);
  mpfr_sub(t, t, k, MPFR_RNDN);                         /* the fraction, exact */
  mpz_t steps, up;
  mpz_inits(steps, up, (mpz_ptr)0);
  mpfr_get_z(steps, k, MPFR_RNDN);
  mpz_add_ui(up, steps, 1);
  uint64_t lo = from_steps(f, steps, q);
  uint64_t hi = from_steps(f, up, q);
  /* above > 0: the exact magnitude is above the computed one, < 0 below */
  int above = ternary == 0 ? 0 : (ternary < 0) == !negative ? 1 : -1;
  if (mpfr_zero_p(t) && above < 0) {
    /* just below a value of the format: inexact, between the one below it and it */
    mpz_sub_ui(steps, steps, 1);
    lo = from_steps(f, steps, q);
    r.result = sign | hi_of(f, steps, q);
    r.lo = sign | lo;
    r.kind = INEXACT;
    strcpy(r.frac, "0.99999999999999999999");
  } else if (mpfr_zero_p(t) && above > 0) {
    r.result = sign | lo;
    r.lo = sign | lo;
    r.kind = INEXACT;
    strcpy(r.frac, "0.00000000000000000001");
  } else if (mpfr_zero_p(t)) {
    r.result = sign | lo;
  } else {
    int half = mpfr_cmp_d(t, 0.5);
    if (half == 0 && above != 0) half = above;
    int odd = mpz_odd_p(steps);
    r.result = sign | (half > 0 || (half == 0 && odd) ? hi : lo);
    r.lo = sign | lo;
    r.kind = INEXACT;
    mpfr_snprintf(r.frac, sizeof r.frac, "%.20Rf", t);
  }
  mpz_clears(steps, up, (mpz_ptr)0);
  mpfr_clears(t, k, (mpfr_ptr)0);
  return r;
}

/* Rounds an exact value to the format, ties to even. */
static rounded_t round_to(const format_t *f, mpfr_t e) { return round_to_side(f, e, 0); }

/* The bits of the value of the format nearest to a real number. */
static uint64_t nearest(const format_t *f, mpfr_t e) { return round_to(f, e).result; }

/* A positive value whose binary exponent is uniform in [lo, hi) and whose significand is random. */
static uint64_t log_uniform(const format_t *f, int lo, int hi) {
  int e = lo + (int)(unit() * (hi - lo));
  uint64_t m = next() & ((1ull << (f->p - 1)) - 1);
  if (e < f->emin) {
    int shift = f->emin - e;
    return shift >= f->p ? 1 : (((1ull << (f->p - 1)) | m) >> shift);
  }
  if (e > f->emax) e = f->emax;
  return ((uint64_t)(e - f->emin + 1) << (f->p - 1)) | m;
}

static uint64_t uniform(const format_t *f, double lo, double hi) {
  mpfr_t v;
  mpfr_init2(v, 64);
  mpfr_set_d(v, lo + (hi - lo) * unit(), MPFR_RNDN);
  uint64_t b = nearest(f, v);
  mpfr_clear(v);
  return b;
}

static uint64_t from_double(const format_t *f, double d) {
  mpfr_t v;
  mpfr_init2(v, 64);
  mpfr_set_d(v, d, MPFR_RNDN);
  uint64_t b = nearest(f, v);
  mpfr_clear(v);
  return b;
}

static uint64_t signed_random(const format_t *f, uint64_t b) { return next() & 1 ? b ^ sign_bit(f) : b; }
static uint64_t neighbour(const format_t *f, uint64_t b, int steps) {
  /* the value `steps` places further from zero (negative: towards zero), within the sign */
  uint64_t s = b & sign_bit(f), m = b & ~sign_bit(f);
  long long n = (long long)m + steps;
  if (n < 0) n = 0;
  if ((uint64_t)n > inf_bits(f)) n = (long long)inf_bits(f);
  return s | (uint64_t)n;
}

typedef enum { UNARY, BINARY, INTEGER, TERNARY } arity_t;
typedef enum { R_ANY, R_UNIT, R_POSITIVE, R_ONE_UP, R_ABOVE_MINUS_ONE } domain_t;
typedef struct {
  const char *name;
  arity_t arity;
  domain_t domain;       /* for the domain class */
  double core_lo, core_hi; /* uniform core range of f64; when core_lo == core_hi the core is log-uniform in [elo, ehi) */
  int elo, ehi;
  int core_signed;
  const char *worst;     /* worst-case file of binary64, relative to src */
  const char *worst32;   /* of binary32 */
  double core_lo32, core_hi32; /* the uniform core range of f32 where it differs from f64, otherwise 0 */
} function_t;

static const function_t functions[] = {
  {"sin", UNARY, R_ANY, 0, 0, -20, 20, 1, "binary64/sin/sin.wc", 0},
  {"cos", UNARY, R_ANY, 0, 0, -20, 20, 1, "binary64/cos/cos.wc", 0},
  {"tan", UNARY, R_ANY, 0, 0, -20, 20, 1, "binary64/tan/tan.wc", 0},
  {"asin", UNARY, R_UNIT, -1, 1, 0, 0, 0, "binary64/asin/asin.wc", 0},
  {"acos", UNARY, R_UNIT, -1, 1, 0, 0, 0, "binary64/acos/acos.wc", 0},
  {"atan", UNARY, R_ANY, 0, 0, -30, 30, 1, "binary64/atan/atan.wc", 0},
  {"atan2", BINARY, R_ANY, 0, 0, -8, 8, 1, "binary64/atan2/atan2.wc", "binary32/atan2/atan2f.wc"},
  {"sinh", UNARY, R_ANY, 0, 0, -30, 9, 1, "binary64/sinh/sinh.wc", 0},
  {"cosh", UNARY, R_ANY, 0, 0, -30, 9, 1, "binary64/cosh/cosh.wc", 0},
  {"tanh", UNARY, R_ANY, 0, 0, -30, 5, 1, "binary64/tanh/tanh.wc", 0},
  {"asinh", UNARY, R_ANY, 0, 0, -30, 30, 1, "binary64/asinh/asinh.wc", 0},
  {"acosh", UNARY, R_ONE_UP, 1, 0, 0, 30, 0, "binary64/acosh/acosh.wc", 0},
  {"atanh", UNARY, R_UNIT, -1, 1, 0, 0, 0, "binary64/atanh/atanh.wc", 0},
  {"exp", UNARY, R_ANY, -745.2, 709.8, 0, 0, 0, "binary64/exp/exp.wc", 0, -103.9, 88.8},
  {"exp2", UNARY, R_ANY, -1075, 1024, 0, 0, 0, "binary64/exp2/exp2.wc", 0, -150, 128},
  {"exp10", UNARY, R_ANY, -323.7, 308.3, 0, 0, 0, "binary64/exp10/exp10.wc", 0, -45.2, 38.6},
  {"expm1", UNARY, R_ANY, -40, 709.8, 0, 0, 0, "binary64/expm1/expm1.wc", 0, -20, 88.8},
  {"log", UNARY, R_POSITIVE, 0.5, 2, 0, 0, 0, "binary64/log/log.wc", 0},
  {"log2", UNARY, R_POSITIVE, 0.5, 2, 0, 0, 0, "binary64/log2/log2.wc", 0},
  {"log10", UNARY, R_POSITIVE, 0.5, 2, 0, 0, 0, "binary64/log10/log10.wc", 0},
  {"log1p", UNARY, R_ABOVE_MINUS_ONE, -0.5, 1, 0, 0, 0, "binary64/log1p/log1p.wc", 0},
  {"logbase", BINARY, R_POSITIVE, 0, 0, -8, 8, 0, 0, 0},
  {"pow", BINARY, R_ANY, 0, 0, -8, 8, 0, "binary64/pow/pow.wc", "binary32/pow/powf.wc"},
  {"cbrt", UNARY, R_ANY, 0, 0, -8, 8, 1, "binary64/cbrt/cbrt.wc", 0},
  {"hypot", BINARY, R_ANY, 0, 0, -8, 8, 1, "binary64/hypot/hypot.wc", "binary32/hypot/hypotf.wc"},
  {"rsqrt", UNARY, R_POSITIVE, 0, 0, -8, 8, 0, "binary64/rsqrt/rsqrt.wc", 0},
  {"rootn", INTEGER, R_ANY, 0, 0, -8, 8, 1, 0, 0},
  {"sqrt", UNARY, R_POSITIVE, 0, 0, -8, 8, 0, 0, 0},
  {"remainder", BINARY, R_ANY, 0, 0, -8, 8, 1, 0, 0},
  {"fma", TERNARY, R_ANY, 0, 0, -8, 8, 1, 0, 0},
};

static const int rootn_degrees[] = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 31, 32, 33, 100, 1023, 1024, 1025,
                                    -1, -2, -3, -4, -5, -6, -7, -8, -9, -16, -17, -100, -1023, -1024};
#define DEGREES ((int)(sizeof rootn_degrees / sizeof rootn_degrees[0]))

/* Evaluates a function on operands held exactly; returns the rounded result. */
static rounded_t evaluate(const function_t *fn, const format_t *f, uint64_t *a, long n) {
  mpfr_t x, y, z, e;
  mpfr_inits2(64, x, y, z, (mpfr_ptr)0);
  int exact = !strcmp(fn->name, "fma") || !strcmp(fn->name, "remainder") || !strcmp(fn->name, "sqrt");
  mpfr_init2(e, exact ? EXACT_PRECISION : FUNCTION_PRECISION);
  to_mpfr(x, f, a[0]);
  if (fn->arity == BINARY || fn->arity == TERNARY) to_mpfr(y, f, a[1]);
  if (fn->arity == TERNARY) to_mpfr(z, f, a[2]);
  const char *s = fn->name;
  int ternary = 0;
  if (!strcmp(s, "sin")) ternary = mpfr_sin(e, x, MPFR_RNDN);
  else if (!strcmp(s, "cos")) ternary = mpfr_cos(e, x, MPFR_RNDN);
  else if (!strcmp(s, "tan")) ternary = mpfr_tan(e, x, MPFR_RNDN);
  else if (!strcmp(s, "asin")) ternary = mpfr_asin(e, x, MPFR_RNDN);
  else if (!strcmp(s, "acos")) ternary = mpfr_acos(e, x, MPFR_RNDN);
  else if (!strcmp(s, "atan")) ternary = mpfr_atan(e, x, MPFR_RNDN);
  else if (!strcmp(s, "atan2")) ternary = mpfr_atan2(e, x, y, MPFR_RNDN);       /* atan2(y = first, x = second) */
  else if (!strcmp(s, "sinh")) ternary = mpfr_sinh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "cosh")) ternary = mpfr_cosh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "tanh")) ternary = mpfr_tanh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "asinh")) ternary = mpfr_asinh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "acosh")) ternary = mpfr_acosh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "atanh")) ternary = mpfr_atanh(e, x, MPFR_RNDN);
  else if (!strcmp(s, "exp")) ternary = mpfr_exp(e, x, MPFR_RNDN);
  else if (!strcmp(s, "exp2")) ternary = mpfr_exp2(e, x, MPFR_RNDN);
  else if (!strcmp(s, "exp10")) ternary = mpfr_exp10(e, x, MPFR_RNDN);
  else if (!strcmp(s, "expm1")) ternary = mpfr_expm1(e, x, MPFR_RNDN);
  else if (!strcmp(s, "log")) ternary = mpfr_log(e, x, MPFR_RNDN);
  else if (!strcmp(s, "log2")) ternary = mpfr_log2(e, x, MPFR_RNDN);
  else if (!strcmp(s, "log10")) ternary = mpfr_log10(e, x, MPFR_RNDN);
  else if (!strcmp(s, "log1p")) ternary = mpfr_log1p(e, x, MPFR_RNDN);
  else if (!strcmp(s, "logbase")) {
    /* log(x) / log(b), the real logarithm to the base b; NaN where either logarithm is or b = 1 */
    mpfr_t lb;
    mpfr_init2(lb, FUNCTION_PRECISION);
    mpfr_log(e, x, MPFR_RNDN);
    mpfr_log(lb, y, MPFR_RNDN);
    mpfr_div(e, e, lb, MPFR_RNDN);
    mpfr_clear(lb);
    ternary = 0;  /* two roundings: no side is known, the 400 bits decide */
  }
  else if (!strcmp(s, "pow")) ternary = mpfr_pow(e, x, y, MPFR_RNDN);
  else if (!strcmp(s, "cbrt")) ternary = mpfr_cbrt(e, x, MPFR_RNDN);
  else if (!strcmp(s, "hypot")) ternary = mpfr_hypot(e, x, y, MPFR_RNDN);
  else if (!strcmp(s, "rsqrt")) ternary = mpfr_rec_sqrt(e, x, MPFR_RNDN);
  else if (!strcmp(s, "rootn")) ternary = mpfr_rootn_si(e, x, n, MPFR_RNDN);
  else if (!strcmp(s, "sqrt")) ternary = mpfr_sqrt(e, x, MPFR_RNDN);
  else if (!strcmp(s, "remainder")) ternary = mpfr_remainder(e, x, y, MPFR_RNDN);
  else if (!strcmp(s, "fma")) ternary = mpfr_fma(e, x, y, z, MPFR_RNDN);
  else { fprintf(stderr, "unknown function %s\n", s); exit(1); }
  rounded_t r = round_to_side(f, e, ternary);
  mpfr_clears(x, y, z, e, (mpfr_ptr)0);
  return r;
}

static FILE *out;
static const format_t *fmt;
static const function_t *fn;

static void hex(uint64_t b) { fprintf(out, fmt->width == 32 ? " %08" PRIx64 : " %016" PRIx64, b); }

static void emit(const char *cls, uint64_t *a, long n) {
  rounded_t r = evaluate(fn, fmt, a, n);
  fputs(cls, out);
  hex(a[0]);
  if (fn->arity == BINARY) hex(a[1]);
  if (fn->arity == TERNARY) { hex(a[1]); hex(a[2]); }
  if (fn->arity == INTEGER) fprintf(out, " %ld", n);
  hex(r.result);
  if (r.kind == EXACT) fputs(" exact\n", out);
  else if (r.kind == NOTANUMBER) fputs(" nan\n", out);
  else { fputs(" inexact", out); hex(r.lo); fprintf(out, " %s\n", r.frac); }
}

static void emit1(const char *cls, uint64_t x) { uint64_t a[3] = {x, 0, 0}; emit(cls, a, 0); }
static void emit2(const char *cls, uint64_t x, uint64_t y) { uint64_t a[3] = {x, y, 0}; emit(cls, a, 0); }

static uint64_t core_value(void) {
  if (fmt->width == 32 && fn->core_lo32 != fn->core_hi32)
    return uniform(fmt, fn->core_lo32, fn->core_hi32);
  if (fn->core_lo != fn->core_hi && !(fn->domain == R_ONE_UP))
    return uniform(fmt, fn->core_lo, fn->core_hi);
  uint64_t v = log_uniform(fmt, fn->elo, fn->ehi);
  if (fn->domain == R_ONE_UP) {          /* 1 + a positive value */
    mpfr_t t;
    mpfr_init2(t, 200);
    to_mpfr(t, fmt, v);
    mpfr_add_ui(t, t, 1, MPFR_RNDN);
    v = nearest(fmt, t);
    mpfr_clear(t);
  }
  return fn->core_signed ? signed_random(fmt, v) : v;
}

static uint64_t domain_value(void) {
  int top = fmt->emax + 1, bottom = fmt->emin - fmt->p + 1;
  uint64_t v;
  switch (fn->domain) {
  case R_UNIT: v = signed_random(fmt, log_uniform(fmt, bottom, 0)); break;
  case R_POSITIVE: v = log_uniform(fmt, bottom, top); break;
  case R_ONE_UP: v = log_uniform(fmt, 0, top); break;
  case R_ABOVE_MINUS_ONE:
    v = log_uniform(fmt, bottom, top);
    if (next() & 1) v = signed_random(fmt, log_uniform(fmt, bottom, 0));
    break;
  default: v = signed_random(fmt, log_uniform(fmt, bottom, top));
  }
  return v;
}

/* The special operands: zeros, infinities, NaN, the extreme subnormals and normals, 1 and its neighbours. */
static int specials(uint64_t *v, int pair) {
  int n = 0;
  uint64_t one = one_bits(fmt);
  v[n++] = 0; v[n++] = sign_bit(fmt); v[n++] = inf_bits(fmt); v[n++] = inf_bits(fmt) | sign_bit(fmt);
  v[n++] = nan_bits(fmt); v[n++] = 1; v[n++] = 1 | sign_bit(fmt);
  if (!pair) {
    v[n++] = (1ull << (fmt->p - 1)) - 1; v[n++] = ((1ull << (fmt->p - 1)) - 1) | sign_bit(fmt);
    v[n++] = 1ull << (fmt->p - 1); v[n++] = (1ull << (fmt->p - 1)) | sign_bit(fmt);
    v[n++] = one + 1; v[n++] = one - 1; v[n++] = (one + 1) | sign_bit(fmt); v[n++] = (one - 1) | sign_bit(fmt);
  }
  v[n++] = max_bits(fmt); v[n++] = max_bits(fmt) | sign_bit(fmt);
  v[n++] = one; v[n++] = one | sign_bit(fmt);
  v[n++] = from_double(fmt, 2); v[n++] = from_double(fmt, -2);
  v[n++] = from_double(fmt, 0.5); v[n++] = from_double(fmt, -0.5);
  v[n++] = from_double(fmt, 3); v[n++] = from_double(fmt, -3);
  if (pair) { v[n++] = from_double(fmt, 1.5); v[n++] = from_double(fmt, -1.5); }
  return n;
}

/* A value and its neighbours, two each side. */
static void around(const char *cls, uint64_t b) {
  for (int k = -2; k <= 2; ++k) emit1(cls, neighbour(fmt, b, k));
}

/* The nearest value of the format to the real function of a constant: op(c). */
static uint64_t threshold(int (*op)(mpfr_ptr, mpfr_srcptr, mpfr_rnd_t), uint64_t c) {
  mpfr_t x, e;
  mpfr_init2(x, 64);
  mpfr_init2(e, FUNCTION_PRECISION);
  to_mpfr(x, fmt, c);
  op(e, x, MPFR_RNDN);
  uint64_t b = nearest(fmt, e);
  mpfr_clears(x, e, (mpfr_ptr)0);
  return b;
}

static void edges(void) {
  const char *s = fn->name;
  uint64_t one = one_bits(fmt), max = max_bits(fmt), sub = 1, normal = 1ull << (fmt->p - 1), neg = sign_bit(fmt);
  if (!strcmp(s, "asin") || !strcmp(s, "acos") || !strcmp(s, "atanh")) {
    around("edge", one); around("edge", one | neg);
  } else if (!strcmp(s, "acosh")) {
    around("edge", one);
  } else if (!strcmp(s, "log") || !strcmp(s, "log2") || !strcmp(s, "log10")) {
    around("edge", one); emit1("edge", 0); emit1("edge", neg); emit1("edge", one | neg);
  } else if (!strcmp(s, "log1p")) {
    around("edge", one | neg); emit1("edge", 0); emit1("edge", neg);
  } else if (!strcmp(s, "exp") || !strcmp(s, "expm1")) {
    around("edge", threshold(mpfr_log, max));
    around("edge", threshold(mpfr_log, sub) | neg);
    around("edge", threshold(mpfr_log, normal) | neg);
  } else if (!strcmp(s, "exp2")) {
    around("edge", threshold(mpfr_log2, max));
    around("edge", threshold(mpfr_log2, sub) | neg);
    around("edge", threshold(mpfr_log2, normal) | neg);
  } else if (!strcmp(s, "exp10")) {
    around("edge", threshold(mpfr_log10, max));
    around("edge", threshold(mpfr_log10, sub) | neg);
  } else if (!strcmp(s, "sinh") || !strcmp(s, "cosh")) {
    uint64_t t = threshold(mpfr_acosh, max);
    around("edge", t); around("edge", t | neg);
  } else if (!strcmp(s, "rsqrt") || !strcmp(s, "sqrt") || !strcmp(s, "cbrt")) {
    around("edge", normal); emit1("edge", neg | one);
  }
}

static void reduction(void) {
  /* k * pi / 2 rounded to the format, for k nearest to a magnitude, and the neighbours */
  const double magnitudes[] = {1048576.0, 4503599627370496.0, 1e22};
  mpfr_t pi, t, k;
  mpfr_inits2(FUNCTION_PRECISION, pi, t, k, (mpfr_ptr)0);
  mpfr_const_pi(pi, MPFR_RNDN);
  mpfr_div_2ui(pi, pi, 1, MPFR_RNDN);
  for (int m = 0; m < 3; ++m) {
    for (int d = -3; d <= 3; ++d) {
      mpfr_set_d(t, magnitudes[m], MPFR_RNDN);
      mpfr_div(k, t, pi, MPFR_RNDN);
      mpfr_round(k, k);
      mpfr_add_si(k, k, d, MPFR_RNDN);
      mpfr_mul(t, k, pi, MPFR_RNDN);
      uint64_t b = nearest(fmt, t);
      emit1("reduction", b); emit1("reduction", neighbour(fmt, b, 1)); emit1("reduction", neighbour(fmt, b, -1));
      emit1("reduction", b | sign_bit(fmt));
    }
  }
  around("reduction", max_bits(fmt));
  mpfr_clears(pi, t, k, (mpfr_ptr)0);
}

/* Parses one value of a worst-case file into the format; 0 when it is not a number of it. */
static int parse(const char **p, uint64_t *b) {
  while (**p == ' ' || **p == '\t' || **p == ',') ++*p;
  if (!strncmp(*p, "+snan", 5) || !strncmp(*p, "-snan", 5) || !strncmp(*p, "snan", 4)) return 0;
  char *end;
  mpfr_t v;
  mpfr_init2(v, 200);
  if (mpfr_strtofr(v, *p, &end, 0, MPFR_RNDN) != 0 || end == *p) { mpfr_clear(v); return 0; }
  *p = end;
  rounded_t r = round_to(fmt, v);
  mpfr_clear(v);
  if (r.kind != EXACT && r.kind != NOTANUMBER) return 0;    /* not a value of the format */
  *b = r.result;
  return 1;
}

static void worst(const char *root) {
  const char *file = fmt->width == 32 ? fn->worst32 : fn->worst;
  if (!file) return;
  char path[1024];
  snprintf(path, sizeof path, "%s/%s", root, file);
  FILE *in = fopen(path, "r");
  if (!in) { fprintf(stderr, "cannot open %s\n", path); exit(1); }
  char line[512];
  long lines = 0;
  while (fgets(line, sizeof line, in)) if (line[0] != '#' && line[0] != '\n') ++lines;
  rewind(in);
  long step = lines > WORST ? lines / WORST : 1, index = 0, taken = 0;
  while (fgets(line, sizeof line, in) && taken < WORST) {
    if (line[0] == '#' || line[0] == '\n') continue;
    if (index++ % step != 0) continue;
    const char *p = line;
    uint64_t a[3] = {0, 0, 0};
    if (!parse(&p, &a[0])) continue;
    if (fn->arity == BINARY && !parse(&p, &a[1])) continue;
    emit("worst", a, 0);
    ++taken;
  }
  fclose(in);
}

int main(int argc, char **argv) {
  if (argc != 3) { fprintf(stderr, "usage: vectors <core-math src> <output directory>\n"); return 1; }
  const format_t *formats[] = {&F32, &F64};
  int count = (int)(sizeof functions / sizeof functions[0]);
  for (int i = 0; i < count; ++i) {
    for (int w = 0; w < 2; ++w) {
      fn = &functions[i];
      fmt = formats[w];
      state = SEED + 2 * i + w;
      char path[1024];
      snprintf(path, sizeof path, "%s/%s.%s.txt", argv[2], fn->name, w ? "f64" : "f32");
      out = fopen(path, "w");
      if (!out) { fprintf(stderr, "cannot write %s\n", path); return 1; }
      fprintf(out, "# %s, %s: reference vectors computed with GNU MPFR %s by tests/vectors/float/generate/vectors.c, seed %d.\n",
        fn->name, w ? "f64" : "f32", mpfr_get_version(), SEED);
      fprintf(out, "# <class> <operands> <correctly rounded result> exact|nan|inexact [<lo> <frac>]\n");
      uint64_t sp[32];
      if (fn->arity == UNARY) {
        for (int g = 0; g < GRID; ++g) emit1("core", core_value());
        for (int g = 0; g < GRID; ++g) emit1("domain", domain_value());
        int n = specials(sp, 0);
        for (int k = 0; k < n; ++k) emit1("special", sp[k]);
        edges();
        if (!strcmp(fn->name, "sin") || !strcmp(fn->name, "cos") || !strcmp(fn->name, "tan")) reduction();
      } else if (fn->arity == BINARY) {
        for (int g = 0; g < GRID; ++g) {
          uint64_t first = core_value();
          emit2("core", first, strcmp(fn->name, "pow") ? core_value() : uniform(fmt, -40, 40));
        }
        for (int g = 0; g < GRID; ++g) emit2("domain", domain_value(), domain_value());
        if (!strcmp(fn->name, "pow")) {
          /* integral exponents, odd and even, and negative bases */
          for (int g = 0; g < 200; ++g) {
            uint64_t base = signed_random(fmt, log_uniform(fmt, -4, 4));
            emit2("core", base, from_double(fmt, (double)((int)(unit() * 80) - 40)));
          }
        }
        int n = specials(sp, 1);
        for (int a = 0; a < n; ++a)
          for (int b = 0; b < n; ++b) emit2("special", sp[a], sp[b]);
      } else if (fn->arity == INTEGER) {
        for (int g = 0; g < GRID; ++g) {
          uint64_t a[3] = {core_value(), 0, 0};
          emit("core", a, rootn_degrees[g % DEGREES]);
        }
        for (int g = 0; g < GRID; ++g) {
          uint64_t a[3] = {domain_value(), 0, 0};
          emit("domain", a, rootn_degrees[g % DEGREES]);
        }
        int n = specials(sp, 0);
        for (int k = 0; k < n; ++k)
          for (int d = 0; d < DEGREES; ++d) {
            uint64_t a[3] = {sp[k], 0, 0};
            emit("special", a, rootn_degrees[d]);
          }
      } else {
        for (int g = 0; g < GRID; ++g) {
          uint64_t a[3] = {core_value(), core_value(), 0};
          /* an addend that nearly cancels the product, half of the time */
          mpfr_t p, q;
          mpfr_inits2(200, p, q, (mpfr_ptr)0);
          to_mpfr(p, fmt, a[0]);
          to_mpfr(q, fmt, a[1]);
          mpfr_mul(p, p, q, MPFR_RNDN);
          mpfr_neg(p, p, MPFR_RNDN);
          a[2] = g % 2 ? core_value() : neighbour(fmt, nearest(fmt, p), (int)(next() % 5) - 2);
          mpfr_clears(p, q, (mpfr_ptr)0);
          emit("core", a, 0);
        }
        for (int g = 0; g < GRID; ++g) {
          uint64_t a[3] = {domain_value(), domain_value(), domain_value()};
          emit("domain", a, 0);
        }
        int n = specials(sp, 1);
        for (int a = 0; a < n; a += 2)
          for (int b = 0; b < n; b += 2)
            for (int c = 0; c < n; c += 3) {
              uint64_t v[3] = {sp[a], sp[b], sp[c]};
              emit("special", v, 0);
            }
      }
      worst(argv[1]);
      fclose(out);
    }
  }
  return 0;
}
