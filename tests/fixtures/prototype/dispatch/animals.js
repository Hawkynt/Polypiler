// An animal and a dog as ECMAScript 2015 classes: the override of base reaches the method of the
// base class through super. Written for this repository.

export class Animal {
  legs() {
    return 0;
  }

  base() {
    return 1;
  }

  describe() {
    return this.legs() * 10 + this.base();
  }
}

export class Dog extends Animal {
  legs() {
    return 4;
  }

  base() {
    return super.base() + 1;
  }
}

// 42: 4 * 10 + (1 + 1).
export function dogDescribe() {
  return new Dog().describe();
}

// 1: 0 * 10 + 1.
export function animalDescribe() {
  return new Animal().describe();
}
