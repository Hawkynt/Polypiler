// One function for every source of evidence the type inference accepts. In each, the local `x` has
// the type its one rule decides; the recorded explanation of the fixture names it.

export function fromBitwise(a, b) { const x = a ^ b; return x; }
export function fromUnsignedShift(a) { const x = a >>> 0; return x; }
export function fromImul(a, b) { const x = Math.imul(a, b); return x; }
export function fromClz32(a) { const x = Math.clz32(a); return x; }
export function fromElement(a) { const bytes = new Uint8Array(1); bytes[0] = a; const x = bytes[0]; return x; }
export function fromInt8Element(a) { const items = new Int8Array(1); items[0] = a; const x = items[0]; return x; }
export function fromInt16Element(a) { const items = new Int16Array(1); items[0] = a; const x = items[0]; return x; }
export function fromUint16Element(a) { const items = new Uint16Array(1); items[0] = a; const x = items[0]; return x; }
export function fromLength(a) { const x = new Uint8Array(a & 7).length; return x; }
export function fromLiteral() { const x = 42; return x; }
export function fromLargeLiteral() { const x = 4294967295; return x; }
export function fromNegativeLiteral() { const x = -2147483648; return x; }
export function fromFractionLiteral() { const x = 0.5; return x; }
export function fromHugeLiteral() { const x = 4294967296; return x; }
export function fromNegativeZero() { const x = -0; return x; }
export function fromVariable(a) { const y = a | 0; const x = y; return x; }
export function fromCoercedSum(a, b) { const x = ((a | 0) + (b | 0)) | 0; return x; }
export function fromCoercedDifference(a, b) { const x = ((a >>> 0) - (b >>> 0)) >>> 0; return x; }
export function fromMask8(a) { const x = a & 0xFF; return x; }
export function fromMask16(a) { const x = a & 0xFFFF; return x; }
export function fromSmallOr(a, b) { const x = (a & 0xF0) | (b & 0x0F); return x; }
export function fromSmallXor(a, b) { const x = (a & 0xFF00) ^ (b & 0x00FF); return x; }
export function fromPlainSum(a, b) { const x = a + b; return x; }
export function fromProduct(a, b) { const x = (a | 0) * (b | 0); return x; }
export function fromCoercedProduct(a, b) { const x = ((a | 0) * (b | 0)) | 0; return x; }
export function fromIntegerAndFraction(a, b) { let x = a | 0; if (b > 0) x = a / 2; return x; }
export function fromSignedAndUnsigned(a, b) { let x = a | 0; if (b > 0) x = a >>> 0; return x; }
export function fromByteAndWord(a, b) { let x = a & 0xFF; if (b > 0) x = a | 0; return x; }
export function fromLiterals(a) { let x = 1; if (a > 0) x = 300; return x; }
export function fromFround(a) { const x = Math.fround(a); return x; }
export function fromFroundAndDouble(a) { let x = Math.fround(a); if (a > 0) x = a; return x; }
export function fromLoop(a) { let x = 0; for (let i = 0; i < (a & 15); i++) x = (x + i) | 0; return x; }
export function fromStep(a) { let x = 0; while (x < (a & 15)) x++; return x; }
export function fromComparison(a, b) { const x = a < b; return x; }
export function fromConditional(a, b) { const x = b > 0 ? a | 0 : 7; return x; }
export function fromBigIntWrapper(a) { const x = BigInt.asUintN(64, BigInt(a | 0) * 3n); return x; }
export function fromSignedBigIntWrapper(a) { const x = BigInt.asIntN(64, BigInt(a | 0) * 3n); return x; }
export function fromBigIntLiteral() { const x = 123n; return x; }
export function fromLargeBigIntLiteral() { const x = 18446744073709551615n; return x; }
export function fromRecursion(a) { const x = (a & 7) === 0 ? 1 : fromRecursion((a & 7) - 1); return x; }
