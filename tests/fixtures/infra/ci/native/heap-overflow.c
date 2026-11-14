/* Reads index INDEX (4 unless -DINDEX=<n>) of a 4-byte allocation: index 4 is one byte past its end.
   Size, index and pointer are volatile, so that no compiler sees the overflow and only the address
   sanitizer can: through a plain pointer gcc's object-size check would report it first. */
#include <stdio.h>
#include <stdlib.h>

#ifndef INDEX
#define INDEX 4
#endif

int main(void) {
  volatile size_t size = 4;
  volatile size_t index = INDEX;
  unsigned char *volatile block = malloc(size);
  if (block == NULL)
    return 2;
  for (size_t i = 0; i < size; ++i)
    block[i] = (unsigned char)(i + 1);
  int value = block[index];
  free(block);
  printf("%d\n", value);
  return 0;
}
