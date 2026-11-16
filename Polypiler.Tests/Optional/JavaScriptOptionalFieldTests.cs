using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// An optional value as a field of a class, as the Cipher framework's <c>Algorithm</c> has
/// <c>this.year = null</c> before an algorithm gives it its year: <c>T?</c> in the IR, <c>int?</c> in
/// C#, <c>null</c> in JavaScript, and none in a new object before its constructor runs. A struct keeps
/// none: a record compares its fields, which C# and JavaScript would do differently for an absent one.
/// The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptOptionalFieldTests {

  private const string Source = """
    class Algo { constructor() { this.year = null; this.ratio = null; } }
    /** @param {int32} set */
    export function years(set) {
      const algo = new Algo();
      const before = algo.year === null ? 1 : 0;
      if (set === 1) { algo.year = 1995; algo.ratio = 0.5; }
      return before + (algo.year ?? 0) + (algo.ratio === null ? 10000 : 20000);
    }
    """;

  /// <summary>Happy path and both values: none, and a value stored after.</summary>
  [TestCase(0, 10001)]
  [TestCase(1, 21996)]
  public void OptionalField_HoldsNoneAndAValue(int set, double expected) {
    var result = JavaScriptFrontend.Import("m.mjs", Source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    var module = result.Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Select(field => field.Type), Is.EqualTo(new[] { IrType.Optional(IrType.I32), IrType.Optional(IrType.F64) }));
    Assert.That(IrInterpreter.Call(module, "years", [set]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("years").Invoke(null, [set]), Is.EqualTo(expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Boundary: a field no constructor assigns holds none in a new object, in the interpreter as in both targets.</summary>
  [Test]
  public void NewObject_HoldsNone() {
    var type = new IrClassType("Box");
    var field = new IrField("count", IrType.Optional(IrType.I32), false);
    type.Define([field]);
    type.DefineMembers([new IrConstructor("Box.ctor")], [], [], []);
    var self = new IrLocal("this", IrType.Object(type));
    var module = new IrModule("M", [
      new IrFunction("Box.ctor", false, IrType.Void, [self], [], new IrBlock([])),
      new IrFunction("f", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrIsNone(new IrFieldLoad(new IrNew(IrType.Object(type), type.Constructors[0], []), field)))]))
    ]) { Types = [type] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(true));
  }

  /// <summary>Error path: an optional field of a struct stays refused by the verifier.</summary>
  [Test]
  public void OptionalFieldOfAStruct_IsRefused() {
    var record = new IrStructType("P", [new IrField("x", IrType.Optional(IrType.I32), false)]);
    var module = new IrModule("M", [new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))]))]) { Types = [record] };
    Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Has.Some.Contains("the field 'x' has the type i32?"));
  }

}
