/* Adler-32 of "abc" (RFC 1950 section 8.2): prints 38600999, or 1 for the empty input (-DEMPTY).
   -DUNUSED_VARIABLE adds a variable nothing reads, which -Wall -Werror turns into a build failure. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static uint32_t adler32(const unsigned char *data, size_t length) {
  uint32_t a = 1, b = 0;
  for (size_t i = 0; i < length; ++i) {
    a = (a + data[i]) % 65521u;
    b = (b + a) % 65521u;
  }
  return (b << 16) | a;
}

int main(void) {
#ifdef UNUSED_VARIABLE
  int unused = 0;
#endif
#ifdef EMPTY
  const char *text = "";
#else
  const char *text = "abc";
#endif
  printf("%lu\n", (unsigned long)adler32((const unsigned char *)text, strlen(text)));
  return 0;
}
