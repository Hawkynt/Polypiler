// A framework of the shape of Cipher's AlgorithmFramework.js, small enough for the subset the
// translation takes today: the registry the harness of polypiler-tooling cipher kat reads, a base
// class of the algorithms and one of their instances. Written for the tests of #1365.
const Algorithms = [];

class Algorithm {
  constructor() {
    this.name = '';
  }

  /** @param {bool} isInverse */
  CreateInstance(isInverse) { throw new Error('CreateInstance() not implemented'); }
}

class IAlgorithmInstance {
  constructor() {
    this.input = [];
  }

  /** @param {uint8[]} data */
  Feed(data) {
    for (let i = 0; i < data.length; i++)
      this.input.push(data[i]);
  }

  Result() { throw new Error('Result() not implemented'); }
}

/** @param {Algorithm} algorithm */
function RegisterAlgorithm(algorithm) {
  Algorithms.push(algorithm);
}

module.exports = { Algorithms, Algorithm, IAlgorithmInstance, RegisterAlgorithm };
