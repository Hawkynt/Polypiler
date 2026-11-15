// A return inside a finally block, which C# has no form for (CS0157): refused. Written for this repository.

export function f() {
  try {
    return 1;
  } finally {
    return 2;
  }
}
