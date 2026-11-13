// Counts the runs of one class of bytes: zero bytes (0), ASCII digits (1) or everything else (2).
// A run is a maximal stretch of bytes of the class; the class of a byte is decided by a switch.

export function runs(data, kind) {
  let count = 0;
  let inside = false;
  for (let i = 0; i < data.length; i++) {
    let matches;
    switch (data[i]) {
      case 0:
        matches = kind === 0;
        break;
      case 48:
      case 49:
      case 50:
      case 51:
      case 52:
      case 53:
      case 54:
      case 55:
      case 56:
      case 57:
        matches = kind === 1;
        break;
      default:
        matches = kind === 2;
    }
    if (matches && !inside)
      count = (count + 1) | 0;

    inside = matches;
  }
  return count;
}

// "12" 0 0 "a" "7" has two runs of digits, one of zero bytes and one of anything else.
export function check() {
  const data = new Uint8Array([49, 50, 0, 0, 97, 55]);
  return runs(data, 1) === 2 && runs(data, 0) === 1 && runs(data, 2) === 1;
}
