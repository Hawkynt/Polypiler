// The JavaScript binding of the C library functions LibC.Linux.cs and LibC.Windows.cs call (#1332), for
// translate --bind libc.so.6=./libc-bindings.mjs (or msvcrt=...). It follows the value contract of
// docs/reference/foreign-calls.md and serves every JavaScript level: a 64-bit integer arrives as a BigInt or,
// below ES2020, as its halves { hi, lo }, and an integer result may go back as a Number. The functions are those
// of ISO C: strlen (7.24.6.3), abs and div (7.22.6.1, 7.22.6.2), memset (7.24.6.1) and strtol (7.22.1.4); abs of
// the most negative int is that int, as glibc and msvcrt give it. Written for this repository.

/** A 64-bit integer of the module as a Number. */
const number = (value) => typeof value === "object" ? value.hi * 4294967296 + value.lo : Number(value);

/** The bytes of a string as C sees it, UTF-8, before the terminating zero. */
export function strlen(text) {
  return new TextEncoder().encode(text).length;
}

export function abs(value) {
  return Math.abs(value) | 0;
}

export function div(numerator, denominator) {
  return { Quot: (numerator / denominator) | 0, Rem: numerator % denominator };
}

export function memset(buffer, value, count) {
  for (let index = 0; index < number(count); ++index)
    buffer[index] = value & 255;
}

/** The end pointer is a handle the module never reads; the binding leaves it as it is. */
export function strtol(text, end, radix) {
  const value = parseInt(text, radix);
  return Number.isNaN(value) ? 0 : value;
}
