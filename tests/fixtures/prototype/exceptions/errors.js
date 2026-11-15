// Errors of JavaScript: a class that extends Error, instanceof tests of the class, of RangeError
// (a BigInt division by zero) and of Error, a message, a cause, and a rethrow. Written for this repository.

class X extends Error {
  constructor(m) {
    super(m);
    this.name = 'X';
  }
}

// 1 for k = 0 (an X), 2 for any other k (a RangeError of the division). The import takes BigInt of an
// integer only, which the coercions prove k - k to be, and a BigInt cut to 64 bits.
export function f(k) {
  k = k | 0;
  try {
    if (k === 0) throw new X('x');
    return BigInt.asIntN(64, 10n / BigInt((k - k) | 0)) > 0n ? 1 : 0;
  } catch (e) {
    if (e instanceof X) return 1;
    if (e instanceof RangeError) return 2;
    throw e;
  }
}

// "outer/cause": the message of an error and of its cause.
export function chain() {
  try {
    try {
      throw new X('cause');
    } catch (inner) {
      throw new Error('outer', { cause: inner });
    }
  } catch (e) {
    return e.message + '/' + e.cause.message;
  }
}

// 3 for k = 0, 4 for any other k: a test that does not stand first, and every error.
export function later(k) {
  try {
    if (k === 0) throw new X('first');
    throw new Error('second');
  } catch (e) {
    const length = e.message.length;
    if (e instanceof X) return length - 2;
    if (e instanceof Error) return length - 2;
    return 0;
  }
}

// 6: a finally block runs on a return, and the value is the one computed before it.
export function fin() {
  let n = 1;
  try {
    n = 6;
    return n;
  } finally {
    n = 7;
  }
}
