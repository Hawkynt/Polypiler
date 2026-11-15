// A frozen object of named numbers, the enum of JavaScript: a member read is the member, a variable
// that only members reach is of the enum, and a member used as a number is that number.
// Written for this repository.

export const Color = Object.freeze({ Red: 0, Green: 5, Blue: 6 });

/**
 * @param {int32} c
 * @returns {int32}
 */
export function shade(c) {
  switch (c) {
    case Color.Red:
      return 10;
    case Color.Green:
      return 20;
    default:
      return 30;
  }
}

// Only members reach c: it is a Color.
function weight(c) {
  return c === Color.Blue ? 3 : 1;
}

export function total() {
  let favourite = Color.Green;
  if (weight(Color.Blue) > 2)
    favourite = Color.Blue;

  return (weight(favourite) + weight(Color.Red)) | 0;
}

/**
 * @param {int32} a
 * @returns {int32}
 */
export function mix(a) {
  return a | Color.Green;
}
