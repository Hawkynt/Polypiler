/* Computes INT_MAX + ADDEND (1 unless -DADDEND=<n>) in int: + 1 is signed overflow, undefined in C.
   Both operands are volatile, so that the addition happens at run time. */
#include <limits.h>
#include <stdio.h>

#ifndef ADDEND
#define ADDEND 1
#endif

int main(void) {
  volatile int value = INT_MAX;
  volatile int addend = ADDEND;
  int sum = value + addend;
  printf("%d\n", sum);
  return 0;
}
