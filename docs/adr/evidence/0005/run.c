/* Evaluates the ADR 0005 input set with the C library (default) or with x87 instructions (-DX87).
 * Reads the input lines on stdin, writes one result per line: 16 hex digits, or "n/a". */
#include <inttypes.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static double from_bits(uint64_t b) { double d; memcpy(&d, &b, 8); return d; }
static uint64_t to_bits(double d) { uint64_t b; memcpy(&b, &d, 8); return b; }

#ifdef X87
/* Every operation is carried out in the 64-bit significand of the x87 stack (precision control
 * "extended", the Linux default) and rounded to double once, on the store. */
static long double x87_1(const char *n, long double x, int *ok) {
  long double r = 0;
  /* FSIN, FCOS and FPTAN leave an operand of magnitude 2^63 or more untouched and only set C2 */
  if ((!strcmp(n, "sin") || !strcmp(n, "cos") || !strcmp(n, "tan")) && fabsl(x) >= 0x1p63L) *ok = 0;
  else if (!strcmp(n, "sin")) __asm__("fsin" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "cos")) __asm__("fcos" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "tan")) __asm__("fptan; fstp %%st(0)" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "sqrt")) __asm__("fsqrt" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "log")) __asm__("fldln2; fxch; fyl2x" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "log2")) __asm__("fld1; fxch; fyl2x" : "=t"(r) : "0"(x));
  else if (!strcmp(n, "log10")) __asm__("fldlg2; fxch; fyl2x" : "=t"(r) : "0"(x));
  else *ok = 0;
  return r;
}
static long double x87_2(const char *n, long double a, long double b, int *ok) {
  long double r = 0;
  if (!strcmp(n, "add")) r = a + b;
  else if (!strcmp(n, "sub")) r = a - b;
  else if (!strcmp(n, "mul")) r = a * b;
  else if (!strcmp(n, "div")) r = a / b;
  else if (!strcmp(n, "atan2")) __asm__("fpatan" : "=t"(r) : "0"(b), "u"(a) : "st(1)");
  else *ok = 0;
  return r;
}
#endif

int main(void) {
  char name[16], cls[16], line[128];
  while (fgets(line, sizeof line, stdin)) {
    uint64_t b[3] = {0, 0, 0};
    int n = sscanf(line, "%15s %15s %" SCNx64 " %" SCNx64 " %" SCNx64, name, cls, &b[0], &b[1], &b[2]) - 2;
    volatile double x = from_bits(b[0]), y = from_bits(b[1]), z = from_bits(b[2]);
    volatile double r = 0;
    int ok = 1;
#ifdef X87
    if (n == 1) r = (double)x87_1(name, x, &ok);
    else if (n == 2) r = (double)x87_2(name, x, y, &ok);
    else ok = 0;
#else
    if (!strcmp(name, "sin")) r = sin(x);
    else if (!strcmp(name, "cos")) r = cos(x);
    else if (!strcmp(name, "tan")) r = tan(x);
    else if (!strcmp(name, "exp")) r = exp(x);
    else if (!strcmp(name, "log")) r = log(x);
    else if (!strcmp(name, "log2")) r = log2(x);
    else if (!strcmp(name, "log10")) r = log10(x);
    else if (!strcmp(name, "pow")) r = pow(x, y);
    else if (!strcmp(name, "atan2")) r = atan2(x, y);
    else if (!strcmp(name, "sinh")) r = sinh(x);
    else if (!strcmp(name, "cosh")) r = cosh(x);
    else if (!strcmp(name, "tanh")) r = tanh(x);
    else if (!strcmp(name, "cbrt")) r = cbrt(x);
    else if (!strcmp(name, "hypot")) r = hypot(x, y);
    else if (!strcmp(name, "add")) r = x + y;
    else if (!strcmp(name, "sub")) r = x - y;
    else if (!strcmp(name, "mul")) r = x * y;
    else if (!strcmp(name, "div")) r = x / y;
    else if (!strcmp(name, "sqrt")) r = sqrt(x);
    else if (!strcmp(name, "fma")) r = fma(x, y, z);
    else ok = 0;
    (void)n;
#endif
    if (ok) printf("%016" PRIx64 "\n", to_bits(r));
    else puts("n/a");
  }
  return 0;
}
