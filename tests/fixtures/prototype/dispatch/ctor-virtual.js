// The constructor of a base class calls init, which the derived class overrides to read a field
// it sets after super() returns: node gives undefined, where C# would have run the initializer
// first, so the translation is refused. Written for this repository.

export class Starter {
  constructor() {
    this.seen = -1;
    this.init();
  }

  init() {
  }
}

export class Derived extends Starter {
  constructor() {
    super();
    this.value = 42;
  }

  init() {
    this.seen = this.value;
  }
}

export function seen() {
  return new Derived().seen;
}
