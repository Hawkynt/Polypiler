// Words of a command line: split at blanks and commas, trimmed, joined again and scored by a switch
// on each word. Written for this repository.

const NAMES = ["add", "sub", "mul"];

/**
 * @param {string} line
 * @returns {int32}
 */
export function score(line) {
  const words = line.trim().split(" ");
  let total = 0;
  for (let i = 0; i < words.length; i++) {
    switch (words[i].trim()) {
      case "add":
      case "plus":
        total = (total + 1) | 0;
        break;
      case "sub":
        total = (total - 1) | 0;
        break;
      case "":
        break;
      default:
        total = (total + 100) | 0;
        break;
    }
  }
  return total;
}

/**
 * @param {string} line
 * @returns {string}
 */
export function normalize(line) {
  const parts = line.split(",");
  const trimmed = [];
  for (let i = 0; i < parts.length; i++) trimmed[i] = parts[i].trim();
  return trimmed.join(";");
}

/**
 * @param {int32} index
 * @returns {string}
 */
export function keyword(index) {
  return NAMES[index];
}
