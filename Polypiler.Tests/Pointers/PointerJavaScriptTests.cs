using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Pointers;

/// <summary>
/// The bounded pointers in the JavaScript backend (#1331): every operation on every element type has a form in every
/// profile - no PLY1152 - and the file is at its level; a pointer is two variables in its function and
/// <c>[region, offset]</c> where it is passed; a reinterpreting read goes through a <c>DataView</c>. <c>node</c> runs
/// the JavaScript in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class PointerJavaScriptTests {

  private static readonly IrType[] _elements = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64];

  private static readonly JavaScriptProfile[] _profiles = [
    JavaScriptProfile.Parse("es5"), JavaScriptProfile.Parse("es5", typedArrays: true), JavaScriptProfile.Parse("es2015"),
    JavaScriptProfile.Parse("es2017"), JavaScriptProfile.Parse("es2020"), JavaScriptProfile.Parse("es2022")
  ];

  private static IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrConst Word(int value) => new(IrType.I32, value);

  /// <summary>
  /// One function per element type that uses every operation on pointers: pins of an array, of an element, of a view
  /// and of an element of one, the address of a local and of a parameter, stackalloc, every comparison, the
  /// difference, a load, a store and a step of the local, casts to bytes and back, a pointer passed to a function and
  /// moved in the step of a loop.
  /// </summary>
  internal static IrModule EveryOperation() {
    var functions = new List<IrFunction>();
    foreach (var element in _elements) {
      var pointer = IrType.Pointer(element);
      var bytes = IrType.Pointer(IrType.U8);
      var array = new IrLocal("a", IrType.Array(element));
      var parameter = new IrLocal("v", element);
      var p = new IrLocal("p", pointer);
      var q = new IrLocal("q", pointer);
      var r = new IrLocal("r", pointer);
      var s = new IrLocal("s", pointer);
      var w = new IrLocal("w", pointer);
      var b = new IrLocal("b", bytes);
      var t = new IrLocal("t", pointer);
      var x = new IrLocal("x", element);
      var n = new IrLocal("n", IrType.I64);
      var flag = new IrLocal("f", IrType.Bool);
      var view = Op(IrIntrinsicOp.ViewOf, IrType.Span(element), new IrLocalRef(array));
      var helper = $"G{element}";
      IrExpr Ref(IrLocal local) => new IrLocalRef(local);
      IrExpr Compare(IrIntrinsicOp op) => Op(op, IrType.Bool, Ref(p), Ref(q));
      List<IrStmt> body = [
        new IrDeclare(p, Op(IrIntrinsicOp.Pin, pointer, Ref(array))),
        new IrDeclare(q, Op(IrIntrinsicOp.Pin, pointer, Ref(array), Word(0))),
        new IrDeclare(r, Op(IrIntrinsicOp.Pin, pointer, view)),
        new IrDeclare(w, Op(IrIntrinsicOp.Pin, pointer, view, Word(0))),
        new IrDeclare(s, Op(IrIntrinsicOp.StackAlloc, pointer, Word(2))),
        new IrDeclare(x, null),
        new IrAssign(x, new IrLocalRef(parameter)),
        new IrExprStmt(new IrStep(Ref(x), true, true, false)),
        new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, Op(IrIntrinsicOp.PointerAdd, pointer, Ref(s), Word(1)), Ref(x))),
        new IrAssign(x, Op(IrIntrinsicOp.PointerLoad, element, Op(IrIntrinsicOp.AddressOf, pointer, Ref(parameter)))),
        new IrDeclare(b, Op(IrIntrinsicOp.PointerCast, bytes, Op(IrIntrinsicOp.AddressOf, pointer, Ref(x)))),
        new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, Ref(b), new IrConst(IrType.U8, 1))),
        new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, Op(IrIntrinsicOp.PointerCast, pointer, Ref(b)), Op(IrIntrinsicOp.PointerLoad, element, Ref(w)))),
        new IrDeclare(n, Op(IrIntrinsicOp.PointerDifference, IrType.I64, Ref(q), Ref(p))),
        new IrDeclare(flag, Compare(IrIntrinsicOp.PointerEqual)),
        new IrAssign(flag, Compare(IrIntrinsicOp.PointerNotEqual)),
        new IrAssign(flag, Compare(IrIntrinsicOp.PointerLess)),
        new IrAssign(flag, Compare(IrIntrinsicOp.PointerLessOrEqual)),
        new IrAssign(flag, Compare(IrIntrinsicOp.PointerGreater)),
        new IrAssign(flag, Op(IrIntrinsicOp.PointerGreaterOrEqual, IrType.Bool, Ref(r), new IrNull(pointer))),
        new IrDeclare(t, Ref(q)),
        new IrFor(Op(IrIntrinsicOp.PointerLess, IrType.Bool, Ref(t), Op(IrIntrinsicOp.PointerAdd, pointer, Ref(p), Word(1))), new IrBlock([new IrContinue()]),
          new IrBlock([new IrAssign(t, Op(IrIntrinsicOp.PointerAdd, pointer, Ref(t), Word(1)))])),
        new IrExprStmt(new IrCall(helper, element, [Ref(s)])),
        new IrReturn(Op(IrIntrinsicOp.PointerLoad, element, Ref(r)))
      ];
      functions.Add(new IrFunction("F" + element, true, element, [array, parameter], [p, q, r, s, w, b, t, x, n, flag], new IrBlock(body)));
      var passed = new IrLocal("p", pointer);
      functions.Add(new IrFunction(helper, false, element, [passed], [], new IrBlock([
        new IrAssign(passed, Op(IrIntrinsicOp.PointerAdd, pointer, Ref(passed), Word(-1))),
        new IrReturn(Op(IrIntrinsicOp.PointerLoad, element, Op(IrIntrinsicOp.PointerAdd, pointer, Ref(passed), Word(1))))
      ])));
    }

    return new IrModule("Every", functions);
  }

  /// <summary>Test design 14: every operation is written in every level - no PLY1152 - and the file is at its level; C# compiles it in every profile.</summary>
  [Test, Category("Slow")]
  public void EveryOperation_IsWrittenInEveryProfile() {
    var module = EveryOperation();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var profile in _profiles) {
      Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, profile.ToString());
      var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, profile + "\n" + text);
    }

    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    }
  }

  /// <summary>The scenario: a reinterpreting read is a DataView with little-endian get at es2022, and no typed array of the wider type is made.</summary>
  [Test]
  public void Reinterpretation_IsADataView() {
    var text = JavaScriptEmitter.Emit(PointerFixtures.Imported("Pointers"), JavaScriptProfile.Parse("es2022")).Single().Content;
    var word = text[text.IndexOf("function Word(", System.StringComparison.Ordinal)..];
    word = word[..word.IndexOf("\n}", System.StringComparison.Ordinal)];
    Assert.That(word, Does.Contain("getUint32(").And.Contain("true"));
    Assert.That(text, Does.Not.Contain("Uint32Array("));
    Assert.That(text, Does.Contain("A pointer is a region"));
    // Below ECMAScript 2020 a region may be a plain array of halves: the helper decides at run time.
    var es2015 = JavaScriptEmitter.Emit(PointerFixtures.Imported("Pointers"), JavaScriptProfile.Parse("es2015")).Single().Content;
    var older = es2015[es2015.IndexOf("function Word(", System.StringComparison.Ordinal)..];
    Assert.That(older[..older.IndexOf("\n}", System.StringComparison.Ordinal)], Does.Contain("$pget(p, p$o + at, 4, \"Uint32\")"));
  }

  /// <summary>Test design 10: a pointer in its function is two variables, one passed is [region, offset], which the function called takes apart.</summary>
  [Test]
  public void PassedPointer_IsAPair() {
    var text = JavaScriptEmitter.Emit(PointerFixtures.Imported("Pointers"), JavaScriptProfile.Parse("es2022")).Single().Content;
    Assert.That(text, Does.Contain("let x = p, x$o = p$o + i * 4;"));
    Assert.That(text, Does.Contain("return SumOf([p, p$o], a.length);"));
    Assert.That(text, Does.Contain("let p$o = p[1];").And.Contain("p = p[0];"));
    Assert.That(text, Does.Contain("// SumOf(p: [region, offset], n: number): number"));
  }

  /// <summary>Test design 9: an address-taken local lives in an array of one element: a typed one where the profile has it, a plain one otherwise.</summary>
  [Test]
  public void AddressTakenLocal_LivesInAnArray() {
    var module = PointerFixtures.Imported("Pointers");
    Assert.That(JavaScriptEmitter.Emit(module, JavaScriptProfile.Parse("es2022")).Single().Content, Does.Contain("let d = new Float64Array([0]);"));
    Assert.That(JavaScriptEmitter.Emit(module, JavaScriptProfile.Parse("es5")).Single().Content, Does.Contain("var d = [0];"));
  }

  /// <summary>No level has pointers: the capability is replaced by a view and an index everywhere.</summary>
  [Test]
  public void PointerCapability_IsAViewAndAnIndex() {
    var row = JavaScriptCapabilities.Standard["*p, a pointer"];
    Assert.That(row.Replacement, Is.EqualTo("view and index"));
    Assert.That(_profiles.Where(profile => JavaScriptCapabilities.Standard.Has("*p, a pointer", profile)), Is.Empty);
  }

}
