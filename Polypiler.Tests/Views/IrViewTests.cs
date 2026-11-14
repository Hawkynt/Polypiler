using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Views;

/// <summary>
/// The views of the IR (#1325): their types, the typing rules of every operation, the verifier's rules -
/// a view lives on the stack and a read-only one is not written - and the interpreter, whose outcome for
/// every call of <c>Spans.cs</c> is the one of .NET's build of the fixture.
/// </summary>
[TestFixture]
public sealed class IrViewTests {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);
  private static readonly IrType _span = IrType.Span(IrType.U8);
  private static readonly IrType _readOnly = IrType.ReadOnlySpan(IrType.U8);
  private static readonly IrType _words = IrType.Span(IrType.U32);
  private static readonly IrConst _zero = new(IrType.I32, 0);

  // ---------------------------------------------------------------- types

  /// <summary>A view is a sequence of its element type, storable as a local of a number type only, and spelled as such.</summary>
  [Test]
  public void ViewTypes_AreSequencesOfTheirElements() {
    Assert.That((_span.IsView, _span.IsSequence, _span.IsArray, _span.IsWritableSequence, _span.IsStorable), Is.EqualTo((true, true, false, true, true)));
    Assert.That((_readOnly.IsView, _readOnly.IsWritableSequence, _bytes.IsWritableSequence, IrType.U8.IsSequence), Is.EqualTo((true, false, true, false)));
    Assert.That(IrType.Span(IrType.Struct(new IrStructType("S"))).IsStorable, Is.False);
    Assert.That((_span.ToString(), _readOnly.ToString()), Is.EqualTo(("span<u8>", "readonly span<u8>")));
    Assert.That(_span, Is.EqualTo(IrType.Span(IrType.U8)).And.Not.EqualTo(_readOnly));
    Assert.That(IrViews.Operations, Has.Count.EqualTo(10).And.All.Matches<IrIntrinsicOp>(IrViews.IsViewOperation));
    Assert.That(IrViews.IsViewOperation(IrIntrinsicOp.ArrayCopy), Is.False);
    Assert.That(IrTrapException.Describe(IrTrap.ArgumentOutOfRange), Is.EqualTo("argument out of range"));
  }

  // ---------------------------------------------------------------- typing

  /// <summary>Test design 9 to 11: every operation on its types, one case per class of argument, and the types it refuses.</summary>
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8[]", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "ro<u8>", "u8[]", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "ro<u8>", "span<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "ro<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8[] i32", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8[] i32 i32", true)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8[] i32 i32 i32", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8[] i64", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u32>", "u8[]", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "u8[]", "u8[]", false)]
  [TestCase(IrIntrinsicOp.ViewOf, "span<u8>", "u8", false)]
  [TestCase(IrIntrinsicOp.Slice, "span<u8>", "span<u8> i32", true)]
  [TestCase(IrIntrinsicOp.Slice, "ro<u8>", "ro<u8> i32 i32", true)]
  [TestCase(IrIntrinsicOp.Slice, "ro<u8>", "span<u8> i32 i32", false)]
  [TestCase(IrIntrinsicOp.Slice, "span<u8>", "span<u8>", false)]
  [TestCase(IrIntrinsicOp.Slice, "u8[]", "u8[] i32", false)]
  [TestCase(IrIntrinsicOp.ViewLength, "i32", "ro<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewLength, "i32", "u8[]", false)]
  [TestCase(IrIntrinsicOp.ViewLength, "u32", "span<u8>", false)]
  [TestCase(IrIntrinsicOp.StackAlloc, "span<f64>", "i32", true)]
  [TestCase(IrIntrinsicOp.StackAlloc, "ro<f64>", "i32", false)]
  [TestCase(IrIntrinsicOp.StackAlloc, "span<f64>", "i64", false)]
  [TestCase(IrIntrinsicOp.ViewCopy, "void", "ro<u8> span<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewCopy, "void", "span<u8> ro<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewCopy, "void", "span<u8> span<u32>", false)]
  [TestCase(IrIntrinsicOp.ViewCopy, "void", "u8[] span<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewCopy, "bool", "span<u8> span<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewTryCopy, "bool", "span<u8> span<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewTryCopy, "void", "span<u8> span<u8>", false)]
  [TestCase(IrIntrinsicOp.ViewFill, "void", "span<u8> u8", true)]
  [TestCase(IrIntrinsicOp.ViewFill, "void", "ro<u8> u8", false)]
  [TestCase(IrIntrinsicOp.ViewFill, "void", "span<u8> i32", false)]
  [TestCase(IrIntrinsicOp.ViewEqual, "bool", "span<f64> ro<f64>", true)]
  [TestCase(IrIntrinsicOp.ViewEqual, "bool", "span<f64> f64[]", false)]
  [TestCase(IrIntrinsicOp.ViewEqual, "bool", "span<f64> span<f32>", false)]
  [TestCase(IrIntrinsicOp.ViewToArray, "u8[]", "ro<u8>", true)]
  [TestCase(IrIntrinsicOp.ViewToArray, "u32[]", "ro<u8>", false)]
  [TestCase(IrIntrinsicOp.Reinterpret, "ro<u32>", "ro<u8>", true)]
  [TestCase(IrIntrinsicOp.Reinterpret, "span<u8>", "span<f64>", true)]
  [TestCase(IrIntrinsicOp.Reinterpret, "span<u32>", "ro<u8>", false)]
  [TestCase(IrIntrinsicOp.Reinterpret, "ro<u32>", "u8[]", false)]
  [TestCase(IrIntrinsicOp.LoadLittle, "u32", "ro<u8> i32", true)]
  [TestCase(IrIntrinsicOp.LoadBig, "f64", "span<u8> i32", true)]
  [TestCase(IrIntrinsicOp.LoadLittle, "u32", "ro<u16> i32", false)]
  [TestCase(IrIntrinsicOp.StoreLittle, "void", "span<u8> i32 u16", true)]
  [TestCase(IrIntrinsicOp.StoreBig, "void", "ro<u8> i32 u16", false)]
  public void Operation_TakesItsTypes(IrIntrinsicOp op, string result, string arguments, bool accepted)
    => Assert.That(IrOperators.Accepts(op, Type(result), [.. arguments.Split(' ').Select(Type)]), Is.EqualTo(accepted));

  /// <summary>A type as the test cases spell it: as <see cref="IrType.ToString"/> writes it, a read-only view as <c>ro&lt;…&gt;</c>.</summary>
  private static IrType Type(string spelled) {
    if (spelled.StartsWith("ro<", StringComparison.Ordinal))
      return IrType.ReadOnlySpan(Type(spelled[3..^1]));

    if (spelled.StartsWith("span<", StringComparison.Ordinal))
      return IrType.Span(Type(spelled[5..^1]));

    if (spelled.EndsWith("[]", StringComparison.Ordinal))
      return IrType.Array(Type(spelled[..^2]));

    return spelled switch {
      "void" => IrType.Void,
      "bool" => IrType.Bool,
      "u8" => IrType.U8,
      "u16" => IrType.U16,
      "i32" => IrType.I32,
      "u32" => IrType.U32,
      "i64" => IrType.I64,
      "f32" => IrType.F32,
      _ => IrType.F64
    };
  }

  // ---------------------------------------------------------------- the verifier

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static IrModule Module(IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new("M", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);

  /// <summary>Test design 11: a write through a read-only view - a store, a step, a fill, a copy into it - is refused.</summary>
  [Test]
  public void ReadOnlyView_IsNotWritten() {
    var view = new IrLocal("v", _readOnly);
    var read = new IrLocalRef(view);
    var stored = Verified(Module(IrType.Void, [view], [], new IrStore(read, _zero, new IrConst(IrType.U8, 1))));
    Assert.That(stored, Has.Some.Contains("an element of a read-only span is written"));
    var stepped = Verified(Module(IrType.U8, [view], [], new IrReturn(new IrStep(new IrArrayLoad(read, _zero), true, false, false))));
    Assert.That(stepped, Has.Some.Contains("an element of a read-only span is written"));
    var filled = Verified(Module(IrType.Void, [view], [], new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [read, new IrConst(IrType.U8, 1)]))));
    Assert.That(filled, Has.Some.Contains("ViewFill"));
    var writable = new IrLocal("w", _span);
    Assert.That(Verified(Module(IrType.U8, [writable], [], new IrStore(new IrLocalRef(writable), _zero, new IrConst(IrType.U8, 1)), new IrReturn(new IrArrayLoad(new IrLocalRef(writable), _zero)))), Is.Empty);
  }

  /// <summary>Test design 15: a view stored in a field, in an array or as a constant is refused, as is one that is not a view of numbers.</summary>
  [Test]
  public void View_IsStoredOnTheStackOnly() {
    var holder = new IrStructType("Holder");
    holder.Define([new IrField("Window", _span, false)]);
    var field = new IrModule("M", []) { Types = [holder] };
    Assert.That(Verified(field), Has.Some.Contains("a span is stored outside the stack"));

    var array = new IrLocal("a", IrType.Array(_span));
    Assert.That(Verified(Module(IrType.Void, [array], [])), Has.Some.Contains("a span is stored outside the stack"));
    Assert.That(Verified(Module(IrType.Void, [], [array])), Has.Some.Contains("a span is stored outside the stack"));

    var constant = new IrModule("M", [], [new IrModuleConst("K", _span, [0])]);
    Assert.That(Verified(constant), Has.Some.Contains("a span is stored outside the stack").Or.Some.Contains("a constant of the type span<u8>"));
    Assert.That(Verified(new IrModule("M", [], [new IrModuleConst("K", IrType.Array(_span), [0])])), Has.Some.Contains("a span is stored outside the stack"));

    var structs = new IrLocal("s", IrType.Span(IrType.Struct(holder)));
    Assert.That(Verified(Module(IrType.Void, [structs], []) with { Types = [holder] }), Has.Some.Contains("has the type span<Holder>"));
  }

  /// <summary>A view of a table of the module is read-only: the table may stand in it, and nowhere writable.</summary>
  [Test]
  public void TableOfTheModule_IsViewedReadOnly() {
    var table = new IrModuleConst("T", _bytes, [1, 2, 3]);
    IrModule Viewing(IrType view) => new("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.ViewOf, view, [new IrConstRef(table)])]))
    ]))], [table]);
    Assert.That(Verified(Viewing(_readOnly)), Is.Empty);
    Assert.That(IrInterpreter.Call(Viewing(_readOnly), "F", []), Is.EqualTo(3));
    Assert.That(Verified(Viewing(_span)), Has.Some.Contains("the constant table 'T' is used as a value"));
  }

  /// <summary>A view crosses the module format with its kind and element type.</summary>
  [Test]
  public void ModuleFormat_KeepsViews() {
    var view = new IrLocal("v", _readOnly);
    var words = new IrLocal("w", IrType.Span(IrType.U32));
    var module = Module(IrType.I32, [view], [words],
      new IrDeclare(words, new IrIntrinsic(IrIntrinsicOp.StackAlloc, words.Type, [new IrConst(IrType.I32, 2)])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrLocalRef(view)])));
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(read.Functions[0].Parameters[0].Type, Is.EqualTo(_readOnly));
    Assert.That(read.Functions[0].Locals[0].Type, Is.EqualTo(IrType.Span(IrType.U32)));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
  }

  // ---------------------------------------------------------------- the interpreter

  private static IrModule? _spans;

  private static IrModule Spans => _spans ??= SpanFixtures.Imported("Spans");

  private static readonly IReadOnlyList<PrototypeCall> _calls = [.. SpanFixtures.Calls()];

  private static IEnumerable<TestCaseData> Calls() => _calls.Select((call, index) => new TestCaseData(index).SetArgDisplayNames(call.ToString()));

  /// <summary>Test design 2 to 10 and 12: every call of the fixture does in the interpreter what it does in .NET.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_IsDotNet(int call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(Spans, _calls[call]), Is.EqualTo(SpanFixtures.DotNet("Spans", _calls[call])));

  /// <summary>Test design 1: Adler-32 over a slice of a larger buffer is the published check value 0x11E60398.</summary>
  [Test]
  public void Adler32OverASlice_IsThePublishedValue() {
    var call = new PrototypeCall("Compute", [SpanFixtures.Wikipedia, 5, 9]);
    Assert.That(PrototypeFixture.InterpretedOutcome(SpanFixtures.Imported("Adler32Span"), call), Is.EqualTo("300286872 | " + PrototypeFixture.Show(SpanFixtures.Wikipedia)));
    Assert.That(SpanFixtures.DotNet("Adler32Span", call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(SpanFixtures.Imported("Adler32Span"), call)));
  }

  /// <summary>Test design 5: a negative <c>stackalloc</c> length is the overflow trap, as an array's is; .NET is not asked, it ends the process.</summary>
  [Test]
  public void NegativeStackAlloc_IsTheOverflowTrap()
    => Assert.That(PrototypeFixture.InterpretedOutcome(Spans, new PrototypeCall("Zeroed", [-1])), Is.EqualTo(PrototypeFixture.Trap(IrTrap.IntegerOverflow)));

  /// <summary>
  /// A reinterpretation is a view of the same memory: a byte written after it is made is read through
  /// it, little-endian, and a write through it reaches the bytes; floating-point elements keep their bits.
  /// </summary>
  [Test]
  public void Reinterpretation_SharesTheMemory() {
    var bytes = new IrLocal("b", _bytes);
    var words = new IrLocal("w", _words);
    var module = Module(IrType.U32, [bytes], [words],
      new IrDeclare(words, new IrIntrinsic(IrIntrinsicOp.Reinterpret, _words, [new IrIntrinsic(IrIntrinsicOp.ViewOf, _span, [new IrLocalRef(bytes), new IrConst(IrType.I32, 1)])])),
      new IrStore(new IrLocalRef(bytes), new IrConst(IrType.I32, 1), new IrConst(IrType.U8, 0xAA)),
      new IrStore(new IrLocalRef(words), new IrConst(IrType.I32, 1), new IrConst(IrType.U32, 0x04030201)),
      new IrReturn(new IrArrayLoad(new IrLocalRef(words), _zero)));
    Assert.That(Verified(module), Is.Empty);
    var data = new byte[10];
    Assert.That(IrInterpreter.Call(module, "F", [data]), Is.EqualTo(0x000000AAu));
    Assert.That(data, Is.EqualTo(new byte[] { 0, 0xAA, 0, 0, 0, 1, 2, 3, 4, 0 }));

    var doubles = new IrLocal("d", IrType.Array(IrType.F64));
    var low = new IrLocal("l", IrType.ReadOnlySpan(IrType.U32));
    var floats = Module(IrType.U32, [doubles], [low],
      new IrDeclare(low, new IrIntrinsic(IrIntrinsicOp.Reinterpret, low.Type, [new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.ReadOnlySpan(IrType.F64), [new IrLocalRef(doubles)])])),
      new IrReturn(new IrArrayLoad(new IrLocalRef(low), new IrConst(IrType.I32, 1))));
    Assert.That(IrInterpreter.Call(floats, "F", [new[] { -2.0 }]), Is.EqualTo(0xC0000000u));
  }

  /// <summary>A view crosses into the interpreter as the whole of an array of its element type, and out of it as a copy.</summary>
  [Test]
  public void View_CrossesTheBoundaryAsAnArray() {
    var view = new IrLocal("v", _span);
    var module = Module(_span, [view], [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Slice, _span, [new IrLocalRef(view), new IrConst(IrType.I32, 1)])));
    Assert.That(IrInterpreter.Call(module, "F", [new byte[] { 1, 2, 3 }]), Is.EqualTo(new byte[] { 2, 3 }));
    Assert.That(() => IrInterpreter.Call(module, "F", [new sbyte[] { 1 }]), Throws.ArgumentException);
    Assert.That(() => IrInterpreter.Call(module, "F", [7]), Throws.ArgumentException);
  }

}
