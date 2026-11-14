using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// One thing the emitter can write: a piece of syntax, a global, a function of the library or a
/// method, with the edition of ECMA-262 that introduced it and what the emitter writes below that.
/// </summary>
/// <param name="Name">
/// The thing as the code spells it: a keyword (<c>let</c>), a global (<c>BigInt</c>), a member of a
/// global (<c>Math.imul</c>), or a method of an object of the library with a leading dot (<c>.fill</c>).
/// </param>
/// <param name="Since">The edition of ECMA-262 that has it first: 5, 2015, 2016 and so on.</param>
/// <param name="Replacement">
/// What the emitter writes for it in a profile that lacks it, or <see langword="null"/> when there
/// is nothing: then a module that needs it cannot be expressed in such a profile.
/// </param>
/// <param name="TypedArray">
/// Whether it belongs to the typed arrays, which engines had before ECMAScript 2015 standardized
/// them: a profile below that level has it when it says it has typed arrays.
/// </param>
/// <param name="Emitted">Whether the emitter ever writes it. What it never writes is listed so that a check can prove it.</param>
public sealed record JavaScriptCapability(string Name, int Since, string? Replacement = null, bool TypedArray = false, bool Emitted = true);

/// <summary>
/// The table every decision of the emitter is taken from: what each language level has, and what
/// stands in for it where it is missing. Another operation is another entry.
/// </summary>
public sealed class JavaScriptCapabilities {

  private const string Halves = "{ hi, lo }";
  private const string PlainArray = "Array";
  private const string Portable = "the portable implementation";

  private readonly Dictionary<string, JavaScriptCapability> _byName;

  /// <summary>Makes a table.</summary>
  /// <param name="entries">The entries; a name stands in at most one of them.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public JavaScriptCapabilities(IEnumerable<JavaScriptCapability> entries) {
    this.Entries = [.. entries];
    this._byName = [];
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary>What ECMA-262 5.1, 2015, 2017, 2020 and 2022 have of the things the emitter writes.</summary>
  public static JavaScriptCapabilities Standard { get; } = new([
    // Syntax.
    new("var", 5),
    new("function", 5),
    new("\"use strict\"", 5),
    new("let", 2015, "var"),
    new("const", 2015, "var"),
    new("export", 2015, "exports"),
    new("=>", 2015, "function", Emitted: false),
    new("**", 2016, "Math.pow", Emitted: false),
    new("numeric separators", 2021, "digits", Emitted: false),
    new("| 0", 5),
    new(">>> 0", 5),
    new("object literal", 5),
    new("shorthand property", 2015, "key: key"),

    // Globals and the functions of Math.
    new("NaN", 5),
    new("Infinity", 5),
    new("RangeError", 5),
    new("Math", 5),
    new("Math.abs", 5),
    new("Math.min", 5),
    new("Math.max", 5),
    new("Math.sqrt", 5),
    new("Math.floor", 5),
    new("Math.ceil", 5),
    new("Math.round", 5),
    new("Math.imul", 2015, "$imul"),
    new("Math.clz32", 2015, "$clz32"),
    new("Math.fround", 2015, "$fround"),
    new("Math.trunc", 2015, "$trunc"),
    new("Math.sign", 2015, "$sign"),
    // The functions of Math that compute a floating-point operation with an ulp(n) contract. Below the
    // level that has one, or where its error is not admitted, the operation is its portable
    // implementation, which legalization puts in before the module is written.
    new("Math.sin", 5), new("Math.cos", 5), new("Math.tan", 5), new("Math.atan2", 5), new("Math.exp", 5), new("Math.log", 5), new("Math.pow", 5),
    new("Math.log2", 2015, Portable), new("Math.log10", 2015, Portable), new("Math.sinh", 2015, Portable), new("Math.cosh", 2015, Portable),
    new("Math.tanh", 2015, Portable), new("Math.cbrt", 2015, Portable), new("Math.hypot", 2015, Portable),
    // Without a measurement of V8's function ADR 0005 admits none of these: never written today.
    new("Math.asin", 5, Emitted: false), new("Math.acos", 5, Emitted: false), new("Math.atan", 5, Emitted: false),
    new("Math.asinh", 2015, Portable, Emitted: false), new("Math.acosh", 2015, Portable, Emitted: false), new("Math.atanh", 2015, Portable, Emitted: false),
    new("Math.expm1", 2015, Portable, Emitted: false), new("Math.log1p", 2015, Portable, Emitted: false),
    new("Number", 5),
    new("Number.isNaN", 2015, "!==", Emitted: false),
    new("globalThis", 2020, "this", Emitted: false),

    // Typed arrays, which an engine of ECMAScript 5 may or may not have.
    new("Int8Array", 2015, PlainArray, TypedArray: true),
    new("Uint8Array", 2015, PlainArray, TypedArray: true),
    new("Int16Array", 2015, PlainArray, TypedArray: true),
    new("Uint16Array", 2015, PlainArray, TypedArray: true),
    new("Int32Array", 2015, PlainArray, TypedArray: true),
    new("Uint32Array", 2015, PlainArray, TypedArray: true),
    new("Float32Array", 2015, PlainArray, TypedArray: true),
    new("Float64Array", 2015, PlainArray, TypedArray: true),
    new("ArrayBuffer", 2015, "$bits64", TypedArray: true),
    new("DataView", 2015, "$bits64", TypedArray: true),
    new(".set", 2015, "$copya", TypedArray: true),
    new(".subarray", 2015, "$copya", TypedArray: true),
    new(".setFloat64", 2015, "$bits64", TypedArray: true),
    new(".getFloat64", 2015, "$float64", TypedArray: true),
    new(".setFloat32", 2015, "$bits32", TypedArray: true),
    new(".getFloat32", 2015, "$float32", TypedArray: true),
    new(".setInt32", 2015, "$float32", TypedArray: true),
    new(".getInt32", 2015, "$bits32", TypedArray: true),
    new(".setUint32", 2015, "$float64", TypedArray: true),
    new(".getUint32", 2015, "$bits64", TypedArray: true),
    new(".fill", 2015, "$fill"),

    // 64-bit integers.
    new("BigInt", 2020, Halves),
    new("BigInt.asIntN", 2020, Halves),
    new("BigInt.asUintN", 2020, Halves),
    new("BigInt64Array", 2020, PlainArray),
    new("BigUint64Array", 2020, PlainArray),
    new(".getBigInt64", 2020, ".getUint32"),
    new(".setBigInt64", 2020, ".setUint32"),

    // The views of #1325: a subarray of a typed array, checked by $slice first, or an object { a, o, n } over a plain array.
    new("subarray as a view", 2015, "$vobj", TypedArray: true),
    new(".getInt16", 2015, "$vat", TypedArray: true),
    new(".getUint16", 2015, "$vat", TypedArray: true),
    new(".setInt16", 2015, "$vat", TypedArray: true),
    new(".setUint16", 2015, "$vat", TypedArray: true),
    new(".getBigUint64", 2020, "$load64"),
    new(".setBigUint64", 2020, "$store64")
  ]);

  /// <summary>The entries, in the order they were given.</summary>
  public IReadOnlyList<JavaScriptCapability> Entries { get; }

  /// <summary>
  /// Whether a remainder computed from the quotient of the same operands, <c>a - q * b</c>, is cheaper
  /// than a second division for <c>a % b</c> - which the optimization O0127 asks. The standard table
  /// says no: an engine computes the remainder of two integers in one instruction, and the product
  /// needs <c>Math.imul</c> to stay exact.
  /// </summary>
  public bool RemainderFromQuotient { get; init; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public JavaScriptCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability table has no entry '{name}'.", nameof(name));

  /// <summary>Whether a profile has the thing an entry names.</summary>
  /// <param name="name">The name of an entry.</param>
  /// <param name="profile">The profile asked about.</param>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public bool Has(string name, JavaScriptProfile profile) {
    var entry = this[name];
    return (int)profile.Level >= entry.Since || entry.TypedArray && profile.TypedArrays;
  }

  /// <summary>The table with one entry added, or put in the place of the entry of the same name.</summary>
  /// <param name="entry">The entry.</param>
  public JavaScriptCapabilities With(JavaScriptCapability entry)
    => new(this._byName.ContainsKey(entry.Name)
      ? this.Entries.Select(existing => existing.Name == entry.Name ? entry : existing)
      : this.Entries.Append(entry));

  /// <summary>
  /// The identifiers of the global object the entries name: an entry that is an identifier, and the
  /// part in front of the dot of a member. No name of the emitted code may be one of them.
  /// </summary>
  public IReadOnlySet<string> Globals()
    => this.Entries
      .Select(entry => entry.Name.Split('.')[0])
      .Where(name => JavaScriptWords.IsIdentifier(name))
      .ToHashSet();

}
