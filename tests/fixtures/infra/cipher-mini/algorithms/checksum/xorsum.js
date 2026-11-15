// An algorithm of the shape of a Cipher algorithm file: it requires the framework, extends its
// classes and registers itself when it is loaded. An 8-bit XOR checksum with a seed, and a key that
// is XORed in as well. Written for the tests of #1365.
const AlgorithmFramework = require('../../AlgorithmFramework');
const { Algorithm, IAlgorithmInstance, RegisterAlgorithm } = AlgorithmFramework;

class XorSum extends Algorithm {
  /** @param {string} name @param {int32} seed */
  constructor(name, seed) {
    super();
    this.name = name;
    this.seed = seed;
  }

  /** @param {bool} isInverse */
  CreateInstance(isInverse) {
    return new XorSumInstance(this);
  }
}

class XorSumInstance extends IAlgorithmInstance {
  /** @param {XorSum} algorithm */
  constructor(algorithm) {
    super();
    this.seed = algorithm.seed;
    this.key = 0;
  }

  /** @param {int32} key */
  setKey(key) {
    this.key = key & 0xFF;
  }

  Explode() {
    throw new Error('boom');
  }

  Result() {
    let sum = this.seed ^ this.key;
    for (let i = 0; i < this.input.length; i++)
      sum = (sum ^ this.input[i]) & 0xFF;
    return [sum];
  }
}

RegisterAlgorithm(new XorSum('XorSum-0', 0));
RegisterAlgorithm(new XorSum('XorSum-7', 7));

module.exports = { XorSum, XorSumInstance };
