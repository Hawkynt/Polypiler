// Async functions of JavaScript that await every promise where they make it (#1330): a staged sum,
// awaits of a resolved promise and of a value, a fan-out of independent squares with Promise.all, and
// a fault observed at await in try, catch and finally. Written for this repository.

async function step(x) {
  await Promise.resolve();
  return x * 3 + 1;
}

export async function run(n) {
  let sum = 0;
  for (let i = 0; i < n; i++) {
    sum += await step(i);
  }
  return sum;
}

export async function completed() {
  const five = await Promise.resolve(5);
  return five + await 2;
}

async function sq(x) {
  await Promise.resolve();
  return x * x;
}

export async function squares() {
  return await Promise.all([sq(2), sq(3)]);
}

async function thrower() {
  await Promise.resolve();
  throw new Error("boom");
}

export async function observed() {
  let trace = "t1";
  try {
    await thrower();
    trace += " none";
  } catch (e) {
    trace += " caught " + e.message;
  } finally {
    trace += " finally";
  }
  return trace;
}
