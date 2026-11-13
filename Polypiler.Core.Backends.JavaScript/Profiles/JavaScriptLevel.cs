namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The language level generated JavaScript is written for. The number of a level is the edition of
/// ECMA-262 it names, which is what a <see cref="JavaScriptCapability"/> is compared with.
/// </summary>
public enum JavaScriptLevel {

  /// <summary>ECMAScript 5.1: <c>var</c>, function declarations, strict mode; no typed arrays of its own.</summary>
  Es5 = 5,

  /// <summary>ECMAScript 2015: <c>let</c> and <c>const</c>, modules, typed arrays, <c>Math.imul</c> and <c>Math.fround</c>.</summary>
  Es2015 = 2015,

  /// <summary>ECMAScript 2017.</summary>
  Es2017 = 2017,

  /// <summary>ECMAScript 2020: <c>BigInt</c> and the typed arrays of 64-bit integers.</summary>
  Es2020 = 2020,

  /// <summary>ECMAScript 2022.</summary>
  Es2022 = 2022

}

/// <summary>How a generated file hands its public functions to its user.</summary>
public enum JavaScriptModuleFormat {

  /// <summary>An ES module: every public function is an <c>export</c>.</summary>
  Esm,

  /// <summary>A CommonJS module: every public function is a property of <c>exports</c>.</summary>
  Cjs,

  /// <summary>A script: one global object, built by a function that is called at once, holds every public function.</summary>
  Iife

}
