// Bytes to hexadecimal text through a constant alphabet, and back. Written for this repository.

const DIGITS = "0123456789abcdef";

/**
 * @param {Uint8Array} data
 * @returns {string}
 */
export function encode(data) {
  let text = "";
  for (let i = 0; i < data.length; i++) {
    text += DIGITS.charAt(data[i] >> 4) + DIGITS.charAt(data[i] & 15);
  }
  return text;
}

/**
 * @param {string} text
 * @returns {Uint8Array}
 */
export function decode(text) {
  const data = new Uint8Array(text.length >> 1);
  for (let i = 0; i < data.length; i++) {
    data[i] = (DIGITS.indexOf(text.charAt(2 * i)) << 4) | DIGITS.indexOf(text.charAt(2 * i + 1));
  }
  return data;
}
