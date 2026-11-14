using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;

namespace Polypiler.Tests.Backends.JavaScript;

/// <summary>
/// The capability table as data: what it lists, the level each entry starts at, and what the
/// emitter writes below that. The levels are those of ECMA-262 5.1, 2015, 2016, 2020 and 2021.
/// </summary>
[TestFixture]
public sealed class JavaScriptCapabilitiesTests {

  private static readonly JavaScriptCapabilities _table = JavaScriptCapabilities.Standard;

  private static JavaScriptProfile Profile(string level, bool typedArrays = false) => JavaScriptProfile.Parse(level, typedArrays: typedArrays);

  /// <summary>Every language feature and library function the issue names, with its first edition and its replacement.</summary>
  [TestCase("var", 5, null)]
  [TestCase("function", 5, null)]
  [TestCase("\"use strict\"", 5, null)]
  [TestCase("let", 2015, "var")]
  [TestCase("const", 2015, "var")]
  [TestCase("export", 2015, "exports")]
  [TestCase("=>", 2015, "function")]
  [TestCase("**", 2016, "Math.pow")]
  [TestCase("numeric separators", 2021, "digits")]
  [TestCase("| 0", 5, null)]
  [TestCase(">>> 0", 5, null)]
  [TestCase("object literal", 5, null)]
  [TestCase("shorthand property", 2015, "key: key")]
  [TestCase("NaN", 5, null)]
  [TestCase("Infinity", 5, null)]
  [TestCase("RangeError", 5, null)]
  [TestCase("Math", 5, null)]
  [TestCase("Math.abs", 5, null)]
  [TestCase("Math.min", 5, null)]
  [TestCase("Math.max", 5, null)]
  [TestCase("Math.sqrt", 5, null)]
  [TestCase("Math.floor", 5, null)]
  [TestCase("Math.ceil", 5, null)]
  [TestCase("Math.round", 5, null)]
  [TestCase("Math.imul", 2015, "$imul")]
  [TestCase("Math.clz32", 2015, "$clz32")]
  [TestCase("Math.fround", 2015, "$fround")]
  [TestCase("Math.trunc", 2015, "$trunc")]
  [TestCase("Math.sign", 2015, "$sign")]
  [TestCase("Math.sin", 5, null)]
  [TestCase("Math.cos", 5, null)]
  [TestCase("Math.tan", 5, null)]
  [TestCase("Math.asin", 5, null)]
  [TestCase("Math.acos", 5, null)]
  [TestCase("Math.atan", 5, null)]
  [TestCase("Math.atan2", 5, null)]
  [TestCase("Math.exp", 5, null)]
  [TestCase("Math.log", 5, null)]
  [TestCase("Math.pow", 5, null)]
  [TestCase("Math.log2", 2015, "the portable implementation")]
  [TestCase("Math.log10", 2015, "the portable implementation")]
  [TestCase("Math.sinh", 2015, "the portable implementation")]
  [TestCase("Math.cosh", 2015, "the portable implementation")]
  [TestCase("Math.tanh", 2015, "the portable implementation")]
  [TestCase("Math.asinh", 2015, "the portable implementation")]
  [TestCase("Math.acosh", 2015, "the portable implementation")]
  [TestCase("Math.atanh", 2015, "the portable implementation")]
  [TestCase("Math.expm1", 2015, "the portable implementation")]
  [TestCase("Math.log1p", 2015, "the portable implementation")]
  [TestCase("Math.cbrt", 2015, "the portable implementation")]
  [TestCase("Math.hypot", 2015, "the portable implementation")]
  [TestCase("Number", 5, null)]
  [TestCase("Number.isNaN", 2015, "!==")]
  [TestCase("globalThis", 2020, "this")]
  [TestCase("Int8Array", 2015, "Array")]
  [TestCase("Uint8Array", 2015, "Array")]
  [TestCase("Int16Array", 2015, "Array")]
  [TestCase("Uint16Array", 2015, "Array")]
  [TestCase("Int32Array", 2015, "Array")]
  [TestCase("Uint32Array", 2015, "Array")]
  [TestCase("Float32Array", 2015, "Array")]
  [TestCase("Float64Array", 2015, "Array")]
  [TestCase("ArrayBuffer", 2015, "$bits64")]
  [TestCase("DataView", 2015, "$bits64")]
  [TestCase(".set", 2015, "$copya")]
  [TestCase(".subarray", 2015, "$copya")]
  [TestCase(".setFloat64", 2015, "$bits64")]
  [TestCase(".getFloat64", 2015, "$float64")]
  [TestCase(".setFloat32", 2015, "$bits32")]
  [TestCase(".getFloat32", 2015, "$float32")]
  [TestCase(".setInt32", 2015, "$float32")]
  [TestCase(".getInt32", 2015, "$bits32")]
  [TestCase(".setUint32", 2015, "$float64")]
  [TestCase(".getUint32", 2015, "$bits64")]
  [TestCase(".fill", 2015, "$fill")]
  [TestCase("BigInt", 2020, "{ hi, lo }")]
  [TestCase("BigInt.asIntN", 2020, "{ hi, lo }")]
  [TestCase("BigInt.asUintN", 2020, "{ hi, lo }")]
  [TestCase("BigInt64Array", 2020, "Array")]
  [TestCase("BigUint64Array", 2020, "Array")]
  [TestCase(".getBigInt64", 2020, ".getUint32")]
  [TestCase(".setBigInt64", 2020, ".setUint32")]
  [TestCase("subarray as a view", 2015, "$vobj")]
  [TestCase(".getInt16", 2015, "$vat")]
  [TestCase(".getUint16", 2015, "$vat")]
  [TestCase(".setInt16", 2015, "$vat")]
  [TestCase(".setUint16", 2015, "$vat")]
  [TestCase(".getBigUint64", 2020, "$load64")]
  [TestCase(".setBigUint64", 2020, "$store64")]
  [TestCase("String", 5, null)]
  [TestCase("String.fromCharCode", 5, null)]
  [TestCase(".charCodeAt", 5, null)]
  [TestCase(".charAt", 5, null)]
  [TestCase(".indexOf", 5, null)]
  [TestCase(".lastIndexOf", 5, null)]
  [TestCase(".substring", 5, null)]
  [TestCase(".split", 5, null)]
  [TestCase(".join", 5, null)]
  [TestCase(".replace", 5, null)]
  [TestCase(".trim", 5, null)]
  [TestCase(".startsWith", 2015, "$startsWith")]
  [TestCase(".endsWith", 2015, "$endsWith")]
  [TestCase(".includes", 2015, ".indexOf")]
  [TestCase(".repeat", 2015, "$repeat")]
  [TestCase(".codePointAt", 2015, "$codePointAt")]
  [TestCase(".padStart", 2017, "$pad")]
  [TestCase(".padEnd", 2017, "$pad")]
  [TestCase(".trimStart", 2019, "$trimEcma")]
  [TestCase(".trimEnd", 2019, "$trimEcma")]
  [TestCase(".replaceAll", 2021, ".split")]
  public void Standard_ListsTheEntry(string name, int since, string? replacement) {
    var entry = _table[name];
    Assert.That((entry.Name, entry.Since, entry.Replacement), Is.EqualTo((name, since, replacement)));
  }

  /// <summary>The table holds nothing but what <see cref="Standard_ListsTheEntry"/> states, each name once.</summary>
  [Test]
  public void Standard_HasNoOtherEntries() {
    var stated = typeof(JavaScriptCapabilitiesTests).GetMethod(nameof(Standard_ListsTheEntry))!
      .GetCustomAttributes(typeof(TestCaseAttribute), false).Cast<TestCaseAttribute>().Select(test => (string)test.Arguments[0]!);
    Assert.That(_table.Entries.Select(entry => entry.Name), Is.EquivalentTo(stated));
    Assert.That(_table.Entries.Select(entry => entry.Name), Is.Unique);
  }

  /// <summary>What an engine of ECMAScript 5 may have without the standard saying so: the typed arrays and nothing else.</summary>
  [Test]
  public void Standard_MarksTheTypedArrays() {
    var typed = _table.Entries.Where(entry => entry.TypedArray).Select(entry => entry.Name);
    Assert.That(typed, Is.EqualTo(new[] {
      "Int8Array", "Uint8Array", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array", "Float32Array", "Float64Array",
      "ArrayBuffer", "DataView", ".set", ".subarray", ".setFloat64", ".getFloat64", ".setFloat32", ".getFloat32",
      ".setInt32", ".getInt32", ".setUint32", ".getUint32",
      "subarray as a view", ".getInt16", ".getUint16", ".setInt16", ".setUint16"
    }));
    Assert.That(_table.Entries.Where(entry => entry.TypedArray).Select(entry => entry.Since), Has.All.EqualTo(2015));
  }

  /// <summary>What the emitter never writes, at any level.</summary>
  [Test]
  public void Standard_NamesWhatIsNeverEmitted()
    => Assert.That(_table.Entries.Where(entry => !entry.Emitted).Select(entry => entry.Name),
      Is.EquivalentTo(new[] { "=>", "**", "numeric separators", "Number.isNaN", "globalThis", "Math.asin", "Math.acos", "Math.atan", "Math.asinh", "Math.acosh", "Math.atanh", "Math.expm1", "Math.log1p" }));

  /// <summary>Everything above ECMAScript 5 that the emitter writes has a replacement, so no module is refused today.</summary>
  [Test]
  public void Standard_ReplacesEverythingAboveEcmaScript5()
    => Assert.That(_table.Entries.Where(entry => entry.Since > 5 && entry.Replacement is null), Is.Empty);

  /// <summary>Test design 3, the boundary of every level: on it, just below it, just above it.</summary>
  [TestCase("var", "es5", true)]
  [TestCase("Math.floor", "es5", true)]
  [TestCase("let", "es5", false)]
  [TestCase("let", "es2015", true)]
  [TestCase("let", "es2017", true)]
  [TestCase("Math.imul", "es5", false)]
  [TestCase("Math.imul", "es2015", true)]
  [TestCase("**", "es2015", false)]
  [TestCase("**", "es2017", true)]
  [TestCase("BigInt", "es2017", false)]
  [TestCase("BigInt", "es2020", true)]
  [TestCase("BigInt", "es2022", true)]
  [TestCase("BigInt64Array", "es2017", false)]
  [TestCase("BigInt64Array", "es2020", true)]
  [TestCase("numeric separators", "es2020", false)]
  [TestCase("numeric separators", "es2022", true)]
  [TestCase(".fill", "es5", false)]
  [TestCase(".fill", "es2015", true)]
  public void Has_FollowsTheLevel(string name, string level, bool expected)
    => Assert.That(_table.Has(name, Profile(level)), Is.EqualTo(expected));

  /// <summary>An ECMAScript 5 profile has the typed arrays exactly when it says so, and nothing else with them.</summary>
  [TestCase("Uint8Array", false, false)]
  [TestCase("Uint8Array", true, true)]
  [TestCase("DataView", false, false)]
  [TestCase("DataView", true, true)]
  [TestCase(".subarray", true, true)]
  [TestCase(".fill", true, false)]
  [TestCase("Math.fround", true, false)]
  [TestCase("BigInt64Array", true, false)]
  [TestCase("let", true, false)]
  public void Has_FollowsTheTypedArraysOfEcmaScript5(string name, bool typedArrays, bool expected)
    => Assert.That(_table.Has(name, Profile("es5", typedArrays)), Is.EqualTo(expected));

  [Test]
  public void UnknownName_IsAnError() {
    Assert.That(() => _table["Math.random"], Throws.ArgumentException.With.Message.StartsWith("The capability table has no entry 'Math.random'."));
    Assert.That(() => _table.Has("Math.random", JavaScriptProfile.Default), Throws.ArgumentException);
  }

  [Test]
  public void NameListedTwice_IsAnError()
    => Assert.That(() => new JavaScriptCapabilities([new("let", 2015), new("const", 2015), new("let", 5)]),
      Throws.ArgumentException.With.Message.StartsWith("The capability 'let' is listed twice."));

  /// <summary>A later issue adds an operation as one more entry, or states an existing one differently.</summary>
  [Test]
  public void With_AddsOrReplacesOneEntry() {
    var added = _table.With(new("Math.f16round", 2025, "$f16round"));
    Assert.That(added.Entries, Has.Count.EqualTo(_table.Entries.Count + 1));
    Assert.That(added.Entries[^1], Is.EqualTo(new JavaScriptCapability("Math.f16round", 2025, "$f16round")));
    Assert.That(added.Has("Math.f16round", Profile("es2022")), Is.False);

    var replaced = _table.With(new("Math.imul", 5));
    Assert.That(replaced.Entries.Select(entry => entry.Name), Is.EqualTo(_table.Entries.Select(entry => entry.Name)));
    Assert.That(replaced.Has("Math.imul", Profile("es5")), Is.True);
    Assert.That(_table.Has("Math.imul", Profile("es5")), Is.False, "the table it was made from is unchanged");
  }

  /// <summary>The globals the generated code relies on, which no name of its own may hide.</summary>
  [Test]
  public void Globals_AreTheIdentifiersTheEntriesStartWith()
    => Assert.That(_table.Globals(), Is.EquivalentTo(new[] {
      "NaN", "Infinity", "RangeError", "Math", "Number", "globalThis", "Int8Array", "Uint8Array", "Int16Array", "Uint16Array", "Int32Array",
      "Uint32Array", "Float32Array", "Float64Array", "ArrayBuffer", "DataView", "BigInt", "BigInt64Array", "BigUint64Array", "String"
    }));

  [Test]
  public void Entry_IsPlainData() {
    var entry = new JavaScriptCapability("x", 2015);
    Assert.That((entry.Name, entry.Since, entry.Replacement, entry.TypedArray, entry.Emitted), Is.EqualTo(("x", 2015, (string?)null, false, true)));
    Assert.That(entry with { Replacement = "$x", TypedArray = true, Emitted = false }, Is.EqualTo(new JavaScriptCapability("x", 2015, "$x", true, false)));
  }

}
