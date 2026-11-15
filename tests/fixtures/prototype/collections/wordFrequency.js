// A histogram of 1000 pseudo-random words drawn by xorshift32 from the seed 2026, with a word removed
// every 97 draws, printed in the order a Map enumerates: the order the words were first inserted
// since they were last removed. Written for this repository.

const words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
  "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu"];

function digits(value) {
  if (value === 0) {
    return "0";
  }
  let text = "";
  while (value > 0) {
    text = String.fromCharCode(48 + (value % 10)) + text;
    value = (value / 10) | 0;
  }
  return text;
}

/**
 * @param {number} draws
 */
export function histogram(draws) {
  const counts = new Map();
  let state = 2026;
  for (let draw = 0; draw < draws; draw++) {
    state = (state ^ (state << 13)) >>> 0;
    state = (state ^ (state >>> 17)) >>> 0;
    state = (state ^ (state << 5)) >>> 0;
    const word = words[state % words.length];
    counts.set(word, (counts.get(word) ?? 0) + 1);
    if (draw % 97 === 96) {
      counts.delete(words[state % 7]);
    }
  }
  let text = "";
  for (const [word, count] of counts) {
    text += word + "=" + digits(count) + ";";
  }
  return text;
}
