// Arrow functions, nested functions and closures. Measured with node 22: forVar() 9, forLet() 3,
// forLetAssigned() 9, counter() 123, interleaved() 21, late(2) 10, fact(5) 120, fact(0) 1,
// composed(4) 15; callNull() throws TypeError "f is not a function". Written for this repository.

// A var is one variable of the whole loop: every closure sees the 3 it ends with.
export function forVar() {
  const fs = [null, null, null];
  for (var i = 0; i < 3; i++)
    fs[i] = () => i;

  return (fs[0]() + fs[1]() + fs[2]()) | 0;
}

// A let is a variable of every pass.
export function forLet() {
  const fs = [null, null, null];
  for (let i = 0; i < 3; i++)
    fs[i] = () => i;

  return (fs[0]() + fs[1]() + fs[2]()) | 0;
}

// The next pass starts from what the body made of the variable: the closures see 1, 3 and 5.
export function forLetAssigned() {
  const fs = [null, null, null];
  let k = 0;
  for (let i = 0; i < 6; i++) {
    fs[k] = () => i;
    k++;
    i++;
  }

  return (fs[0]() + fs[1]() + fs[2]()) | 0;
}

function makeCounter() {
  let n = 0;
  return () => ++n;
}

export function counter() {
  const a = makeCounter();
  return a() * 100 + a() * 10 + a();
}

export function interleaved() {
  const a = makeCounter();
  a();
  const b = makeCounter();
  return a() * 10 + b();
}

// The closure sees the write made after it.
export function late(x) {
  const g = () => x;
  x = x * 5;
  return g();
}

export function fact(n) {
  return f(n);

  function f(m) {
    return m <= 1 ? 1 : m * f(m - 1);
  }
}

export function composed(x) {
  const compose = (f, g) => (v) => g(f(v));
  return compose((v) => v + 1, (v) => v * 3)(x);
}

// f holds a function only where g() is 0, which it is not.
export function callNull() {
  let f = null;
  const g = () => 1;
  if (g() === 0)
    f = g;

  return f();
}
