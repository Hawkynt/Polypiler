// An accumulator as a constructor function of ECMAScript 5: a field assigned in the constructor,
// methods on the prototype and an accessor that Object.defineProperty defines. Written for this
// repository.

function Acc(start) {
  this.total = start;
  this.count = 0;
}

Acc.prototype.add = function (value) {
  this.total = this.total + value;
  this.count = this.count + 1;
  return this.total;
};

Acc.prototype.reset = function () {
  this.total = 0;
};

Object.defineProperty(Acc.prototype, "mean", {
  get: function () {
    return this.count === 0 ? 0 : this.total / this.count;
  },
  configurable: true
});

// 7: new Acc(1), add(2), add(4).
function sum(start, first, second) {
  var acc = new Acc(start);
  acc.add(first);
  return acc.add(second);
}

function mean(first, second) {
  var acc = new Acc(0);
  acc.add(first);
  acc.add(second);
  return acc.mean;
}
