// Shapes as ECMAScript 2015 classes: a base class whose area and sides the derived classes
// override, a method of the base class that calls the overridden one, and instanceof. JavaScript
// has no abstract methods: Shape answers 0, and no Shape is ever made. Written for this repository.

export class Shape {
  area() {
    return 0;
  }

  sides() {
    return 0;
  }

  twice() {
    return 2 * this.area();
  }
}

export class Rect extends Shape {
  constructor(width, height) {
    super();
    this.width = width;
    this.height = height;
  }

  area() {
    return this.width * this.height;
  }

  sides() {
    return 4;
  }
}

export class Circle extends Shape {
  constructor(radius) {
    super();
    this.radius = radius;
  }

  area() {
    return 3 * this.radius * this.radius;
  }
}

function twiceOf(shape) {
  return shape.twice();
}

function sidesOf(shape) {
  return shape.sides();
}

// 18 for Rect(2, 3) and Circle(1).
export function sumTwice(width, height, radius) {
  return twiceOf(new Rect(width, height)) + twiceOf(new Circle(radius));
}

// 4 for Rect(2, 3) and Circle(1).
export function sumSides(width, height, radius) {
  return sidesOf(new Rect(width, height)) + sidesOf(new Circle(radius));
}

export function isRect(radius, makeRect) {
  let shape = new Circle(radius);
  if (makeRect) {
    shape = new Rect(radius, radius);
  }
  return shape instanceof Rect;
}
