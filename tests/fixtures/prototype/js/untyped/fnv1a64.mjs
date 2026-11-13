// FNV-1a with 64 bits: offset basis 0xCBF29CE484222325, prime 0x100000001B3. A BigInt has no width
// of its own; BigInt.asUintN(64, …) is what bounds the product.

export function hash(data) {
  let h = 0xCBF29CE484222325n;
  for (let i = 0; i < data.length; i++)
    h = BigInt.asUintN(64, (h ^ BigInt(data[i])) * 0x100000001B3n);

  return h;
}

// The published check value: FNV-1a of "a" is 0xAF63DC4C8601EC8C.
export function check() {
  return hash(new Uint8Array([97])) === 0xAF63DC4C8601EC8Cn;
}
