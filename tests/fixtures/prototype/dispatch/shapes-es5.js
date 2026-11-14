// The shapes of shapes.js as constructor functions of ECMAScript 5, chained with Object.create; the
// derived constructors call the base one with call, and every method sits on a prototype. As a
// script, every function of it is called from outside.
// Written for this repository.

function Shape() {
}

Shape.prototype.area = function () {
  return 0;
};

Shape.prototype.sides = function () {
  return 0;
};

Shape.prototype.twice = function () {
  return 2 * this.area();
};

function Rect(width, height) {
  Shape.call(this);
  this.width = width;
  this.height = height;
}

Rect.prototype = Object.create(Shape.prototype);
Rect.prototype.constructor = Rect;

Rect.prototype.area = function () {
  return this.width * this.height;
};

Rect.prototype.sides = function () {
  return 4;
};

function Circle(radius) {
  Shape.call(this);
  this.radius = radius;
}

Circle.prototype = Object.create(Shape.prototype);
Circle.prototype.constructor = Circle;

Circle.prototype.area = function () {
  return 3 * this.radius * this.radius;
};

// 18 for Rect(2, 3) and Circle(1). A function of a script can be called from outside with
// anything, so the shapes are held where only this file writes: in variables of the base class.
function sumTwice(width, height, radius) {
  var shape = new Rect(width, height);
  var sum = shape.twice();
  shape = new Circle(radius);
  return sum + shape.twice();
}

// 4 for Rect(2, 3) and Circle(1).
function sumSides(width, height, radius) {
  var shape = new Rect(width, height);
  var sum = shape.sides();
  shape = new Circle(radius);
  return sum + shape.sides();
}

function isRect(radius, makeRect) {
  var shape = new Circle(radius);
  if (makeRect) {
    shape = new Rect(radius, radius);
  }
  return shape instanceof Rect;
}
