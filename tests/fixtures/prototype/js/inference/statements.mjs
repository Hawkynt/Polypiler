// The statement forms the importer accepts beyond if, while and for.

/** @param {int32} n */
export function doWhile(n) { let count = 0; do { count = (count + 1) | 0; n = (n / 2) | 0; } while (n !== 0); return count; }
/** @param {int32} n */
export function doWhileWithBreak(n) { let total = 0; do { if (n < 0) { total = -1; break; } total = n | 0; n = n >> 3; } while (n > 0); return total; }
/** @param {int32} n */
export function switchOnInt(n) {
  switch (n) {
    case -1:
      return 10;
    case 0:
    case 1:
      return 20;
    case 2147483647:
      return 30;
    default:
      return 40;
  }
}
/** @param {uint32} n */
export function switchOnUint(n) {
  let kind = 0;
  switch (n) {
    case 4294967295:
      kind = 1;
      break;
    default:
      kind = 2;
      break;
    case 7:
      kind = 3;
  }
  return kind;
}
/** @param {int32} n */
export function switchWithoutDefault(n) { let kind = 5; switch (n & 3) { case 1: kind = 6; break; case 2: } return kind; }
/** @param {int32} n */
export function switchInLoop(n) {
  let odd = 0;
  for (let i = 0; i < (n & 15); i++) {
    switch (i & 1) {
      case 0:
        continue;
      case 1:
        if (i > 9)
          break;

        odd = (odd + 1) | 0;
        break;
    }
    odd = (odd + 100) | 0;
  }
  return odd;
}
/** @param {int32} n */
export function breakOut(n) { let i = 0; while (true) { if (i >= (n & 31)) break; i = (i + 2) | 0; } return i; }
/** @param {int32} n */
export function continueInWhile(n) { let i = 0; let hits = 0; while (i < (n & 31)) { i = (i + 1) | 0; if ((i & 1) === 0) continue; hits = (hits + 1) | 0; } return hits; }
/** @param {int32} n */
export function continueInFor(n) { let hits = 0; for (let i = 0; i < (n & 31); i++) { if (i % 3 === 0) continue; hits += i; } return hits; }
/** @param {int32} n */
export function forWithTwoCounters(n) { let total = 0; for (let i = 0, j = n & 15; i < j; i++, j--) total += j - i; return total; }
/** @param {int32} n */
export function forWithoutParts(n) { let i = n & 7; for (;;) { if (i === 0) return 9; i = (i - 1) | 0; } }
/** @param {number} a @param {number} b */
export function compound(a, b) { let x = a; x += b; x -= 1; x *= b; x /= 2; x %= 7; return x; }
/** @param {int32} i @param {int32} j */
export function compoundBitwise(i, j) { let x = i; x |= j; x &= 0x0FF0; x ^= j; x <<= 2; x >>= 1; x >>>= 1; return x; }
/** @param {number} a */
export function stepsAsValues(a) { let x = a; const first = x++; const second = ++x; const third = x--; const fourth = --x; return first + second * 3 + third * 5 + fourth * 7 + x * 11; }
/** @param {int32} n */
export function stepAsIndex(n) { const items = new Int32Array(4); let i = 0; items[i++] = n; items[i++] = n >> 1; items[i] = i; return items[0] + items[1] * 3 + items[2] * 5; }
/** @param {int32} n */
export function assignedLater(n) { let x; if (n > 0) x = 1; else x = 2; return x; }
/** @param {int32} n */
export function assignedInEveryBranch(n) { let x; if (n > 0) { x = 1; } else if (n < 0) { return -1; } else { x = 0; } return x; }
/** @param {int32} n */
export function assignedBySwitch(n) { let x; switch (n & 3) { case 0: x = 10; break; case 1: return 11; default: x = 12; } return x; }
/** @param {int32} n */
export function assignedThenLooped(n) { let x; x = n & 7; let total = 0; while (x > 0) { total = (total + x) | 0; x = (x - 1) | 0; } return total; }
/** @param {int32} n */
export function localTable(n) { const table = [3, 1, 4, 1, 5, 9, 2, 6]; return table[n & 7] + table.length; }
/** @param {int32} n */
export function emptyStatements(n) { ; { } if (n > 0) ; return n; }
/** @param {int32} n */
export function nothingReturned(n) { if (n > 0) return; }
