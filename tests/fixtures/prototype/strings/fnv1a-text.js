// FNV-1a with 32 bits over the UTF-16 code units of a string. Written for this repository.

/**
 * @param {string} text
 * @returns {uint32}
 */
export function hash(text) {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}
