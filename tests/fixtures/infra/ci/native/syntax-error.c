/* Does not compile: the parameter list of main is never closed. */
#include <stdio.h>

int main(void {
  printf("unreachable\n");
  return 0;
}
