// A value that is not an Error, thrown where nothing observes it: a catch without a binding takes it.
// Written for this repository.

// 2: the object is thrown and caught; nothing reads it.
export function caught() {
  try {
    throw {};
  } catch {
    return 2;
  }
}
