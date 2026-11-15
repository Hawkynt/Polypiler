using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Pointers;

/// <summary>
/// The bounded pointers of the IR (#1331): the type, the typing rules of every operation, the verifier's
/// rules - a pointer lives on the stack, never outlives the block of its region and is never counted through
/// as <c>void*</c> - and the interpreter, which checks every load and store against the region.
/// </summary>
[TestFixture]
public sealed class IrPointerTests {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);
  private static readonly IrType _bytePointer = IrType.Pointer(IrType.U8);
  private static readonly IrType _intPointer = IrType.Pointer(IrType.I32);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrIntrinsic Load(IrExpr pointer) => Op(IrIntrinsicOp.PointerLoad, pointer.Type.Element!, pointer);

  private static IrIntrinsic Add(IrExpr pointer, IrExpr count) => Op(IrIntrinsicOp.PointerAdd, pointer.Type, pointer, count);

  private static IrModule Module(IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new("M", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrModule module, params object[] arguments) {
    Assert.That(Verified(module), Is.Empty);
    return IrInterpreter.Call(module, "F", arguments);
  }

  private static IrTrap Trapped(IrModule module, params object[] arguments) {
    Assert.That(Verified(module), Is.Empty);
    return Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", arguments))!.Trap;
  }

  // ---------------------------------------------------------------- the type

  /// <summary>A pointer is storable over a number or void only, and spelled with a star.</summary>
  [Test]
  public void PointerType_IsOverANumberOrVoid() {
    Assert.That((_intPointer.IsPointer, _intPointer.IsStorable, _intPointer.ToString()), Is.EqualTo((true, true, "i32*")));
    Assert.That((IrType.Pointer(IrType.Void).IsStorable, IrType.Pointer(IrType.Void).ToString()), Is.EqualTo((true, "void*")));
    Assert.That(IrType.Pointer(IrType.Bool).IsStorable, Is.False);
    Assert.That(IrType.Pointer(IrType.String).IsStorable, Is.False);
    Assert.That(IrType.Array(_intPointer).IsStorable, Is.False);
    Assert.That(_intPointer, Is.EqualTo(IrType.Pointer(IrType.I32)).And.Not.EqualTo(_bytePointer));
    Assert.That(IrPointers.Operations, Has.Count.EqualTo(13).And.All.Matches<IrIntrinsicOp>(IrPointers.IsPointerOperation));
    Assert.That(IrPointers.IsPointerOperation(IrIntrinsicOp.StackAlloc), Is.False);
    Assert.That(IrPointers.IsPointerNode(Op(IrIntrinsicOp.StackAlloc, _intPointer, Int(1))), Is.True);
    Assert.That(IrPointers.IsPointerNode(Op(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I32), Int(1))), Is.False);
  }

  // ---------------------------------------------------------------- typing

  /// <summary>Test design 8 and 13: every operation on its types, and void* refused for arithmetic, loads and stores.</summary>
  [TestCase(IrIntrinsicOp.Pin, "u8*", "u8[]", true)]
  [TestCase(IrIntrinsicOp.Pin, "u8*", "u8[] i32", true)]
  [TestCase(IrIntrinsicOp.Pin, "u8*", "span<u8>", true)]
  [TestCase(IrIntrinsicOp.Pin, "u8*", "ro<u8> i32", true)]
  [TestCase(IrIntrinsicOp.Pin, "u32*", "u8[]", false)]
  [TestCase(IrIntrinsicOp.Pin, "u8*", "u8[] i64", false)]
  [TestCase(IrIntrinsicOp.Pin, "u8*", "u8", false)]
  [TestCase(IrIntrinsicOp.AddressOf, "f64*", "f64", true)]
  [TestCase(IrIntrinsicOp.AddressOf, "f64*", "f32", false)]
  [TestCase(IrIntrinsicOp.AddressOf, "bool*", "bool", false)]
  [TestCase(IrIntrinsicOp.PointerAdd, "i32*", "i32* i32", true)]
  [TestCase(IrIntrinsicOp.PointerAdd, "i32*", "i32* i64", false)]
  [TestCase(IrIntrinsicOp.PointerAdd, "void*", "void* i32", false)]
  [TestCase(IrIntrinsicOp.PointerAdd, "u8*", "i32* i32", false)]
  [TestCase(IrIntrinsicOp.PointerDifference, "i64", "i32* i32*", true)]
  [TestCase(IrIntrinsicOp.PointerDifference, "i64", "void* void*", false)]
  [TestCase(IrIntrinsicOp.PointerDifference, "i64", "i32* u8*", false)]
  [TestCase(IrIntrinsicOp.PointerDifference, "i32", "i32* i32*", false)]
  [TestCase(IrIntrinsicOp.PointerEqual, "bool", "void* void*", true)]
  [TestCase(IrIntrinsicOp.PointerLess, "bool", "u8* u8*", true)]
  [TestCase(IrIntrinsicOp.PointerGreaterOrEqual, "bool", "u8* i32*", false)]
  [TestCase(IrIntrinsicOp.PointerNotEqual, "i32", "u8* u8*", false)]
  [TestCase(IrIntrinsicOp.PointerLoad, "u64", "u64*", true)]
  [TestCase(IrIntrinsicOp.PointerLoad, "u8", "void*", false)]
  [TestCase(IrIntrinsicOp.PointerLoad, "u32", "u64*", false)]
  [TestCase(IrIntrinsicOp.PointerStore, "void", "f32* f32", true)]
  [TestCase(IrIntrinsicOp.PointerStore, "void", "f32* f64", false)]
  [TestCase(IrIntrinsicOp.PointerStore, "void", "void* u8", false)]
  [TestCase(IrIntrinsicOp.PointerCast, "u32*", "u8*", true)]
  [TestCase(IrIntrinsicOp.PointerCast, "void*", "f64*", true)]
  [TestCase(IrIntrinsicOp.PointerCast, "u32*", "u8[]", false)]
  [TestCase(IrIntrinsicOp.PointerCast, "u32", "u8*", false)]
  [TestCase(IrIntrinsicOp.StackAlloc, "i16*", "i32", true)]
  [TestCase(IrIntrinsicOp.StackAlloc, "void*", "i32", false)]
  [TestCase(IrIntrinsicOp.StackAlloc, "i16*", "u8", false)]
  public void Operation_TakesItsTypes(IrIntrinsicOp op, string result, string arguments, bool accepted)
    => Assert.That(IrOperators.Accepts(op, Type(result), [.. arguments.Split(' ').Select(Type)]), Is.EqualTo(accepted));

  /// <summary>A type as the test cases spell it, a read-only view as <c>ro&lt;…&gt;</c>.</summary>
  private static IrType Type(string spelled) {
    if (spelled.EndsWith('*'))
      return IrType.Pointer(Type(spelled[..^1]));
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
      "i16" => IrType.I16,
      "i32" => IrType.I32,
      "u32" => IrType.U32,
      "i64" => IrType.I64,
      "u64" => IrType.U64,
      "f32" => IrType.F32,
      _ => IrType.F64
    };
  }

  // ---------------------------------------------------------------- the verifier

  /// <summary>A function that pins its array into <c>p</c> and runs the statements after that.</summary>
  private static IrModule Pinning(IrType returns, IrLocal array, IReadOnlyList<IrLocal> locals, IrLocal pointer, params IrStmt[] after)
    => Module(returns, [array], [pointer, .. locals], [new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, pointer.Type, new IrLocalRef(array))), .. after]);

  /// <summary>Test design 13: a pointer kept after the block of its fixed is refused, a pointer kept inside it is not.</summary>
  [Test]
  public void Pointer_DoesNotOutliveItsFixed() {
    var array = new IrLocal("a", _bytes);
    var outer = new IrLocal("q", _bytePointer);
    var inner = new IrLocal("p", _bytePointer);
    var escaping = Module(IrType.Void, [array], [outer, inner],
      new IrDeclare(outer, new IrNull(_bytePointer)),
      new IrBlock([new IrDeclare(inner, Op(IrIntrinsicOp.Pin, _bytePointer, new IrLocalRef(array))), new IrAssign(outer, Add(new IrLocalRef(inner), Int(1)))]));
    Assert.That(Verified(escaping), Has.Some.Contains("a pointer outlives the block of its region in 'q'"));

    var kept = Module(IrType.U8, [array], [outer, inner],
      new IrBlock([new IrDeclare(inner, Op(IrIntrinsicOp.Pin, _bytePointer, new IrLocalRef(array))), new IrDeclare(outer, new IrLocalRef(inner)), new IrReturn(Load(new IrLocalRef(outer)))]),
      new IrReturn(new IrConst(IrType.U8, 0)));
    Assert.That(Verified(kept), Is.Empty);
  }

  /// <summary>
  /// An iterator function holds no pointer - no parameter, no local, no value - since its locals live across a yield,
  /// where the region of a pin or of a local is no longer held; the same function without the pointer is verified.
  /// </summary>
  [Test]
  public void Pointer_StaysOutOfAnIterator() {
    var sequence = IrType.Sequence(IrType.U8, true);
    var array = new IrLocal("a", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var pinning = Pinning(sequence, array, [], pointer, new IrYield(Load(new IrLocalRef(pointer))));
    Assert.That(Verified(pinning), Has.Some.Contains(IrPointers.InIterator));

    var parameter = Module(sequence, [pointer], [], new IrYield(new IrConst(IrType.U8, 1)));
    Assert.That(Verified(parameter), Has.Some.Contains(IrPointers.InIterator));

    var x = new IrLocal("x", IrType.U8);
    var addressed = Module(sequence, [], [x], new IrDeclare(x, new IrConst(IrType.U8, 2)), new IrYield(Load(Op(IrIntrinsicOp.AddressOf, _bytePointer, new IrLocalRef(x)))));
    Assert.That(Verified(addressed), Has.Some.Contains(IrPointers.InIterator));

    var plain = Module(sequence, [array], [x], new IrDeclare(x, new IrConst(IrType.U8, 2)), new IrYield(new IrLocalRef(x)));
    Assert.That(Verified(plain), Is.Empty);
    Assert.That(Verified(Pinning(IrType.U8, array, [], pointer, new IrReturn(Load(new IrLocalRef(pointer))))), Is.Empty);
  }

  /// <summary>The address of a local declared in an inner block does not reach a pointer of the outer one.</summary>
  [Test]
  public void AddressOfInnerLocal_DoesNotEscape() {
    var outer = new IrLocal("q", _intPointer);
    var x = new IrLocal("x", IrType.I32);
    var module = Module(IrType.Void, [], [outer, x],
      new IrDeclare(outer, new IrNull(_intPointer)),
      new IrBlock([new IrDeclare(x, Int(1)), new IrAssign(outer, Op(IrIntrinsicOp.AddressOf, _intPointer, new IrLocalRef(x)))]));
    Assert.That(Verified(module), Has.Some.Contains("a pointer outlives the block of its region in 'q'"));
  }

  /// <summary>Test design 13: void* is not counted through, nor read, nor written.</summary>
  [Test]
  public void VoidPointer_HasNoArithmetic() {
    var pointer = new IrLocal("p", IrType.Pointer(IrType.Void));
    var module = Module(IrType.Void, [], [pointer], new IrDeclare(pointer, new IrNull(pointer.Type)),
      new IrAssign(pointer, Op(IrIntrinsicOp.PointerAdd, pointer.Type, new IrLocalRef(pointer), Int(1))));
    Assert.That(Verified(module), Has.Some.Contains("PointerAdd with the type void*"));
  }

  /// <summary>Test design 13: the address of a local a lambda captures is refused, as is a lambda that holds a pointer.</summary>
  [Test]
  public void AddressOfCapturedLocal_IsRefused() {
    var x = new IrLocal("x", IrType.I32);
    var pointer = new IrLocal("p", _intPointer);
    var signature = IrType.Function(IrType.I32);
    var lambda = new IrLambda(signature, [x], [], [], new IrBlock([new IrReturn(new IrLocalRef(x))]));
    var function = new IrLocal("f", signature);
    var module = Module(IrType.Void, [], [x, pointer, function],
      new IrDeclare(x, Int(1)),
      new IrDeclare(function, lambda),
      new IrDeclare(pointer, Op(IrIntrinsicOp.AddressOf, _intPointer, new IrLocalRef(x))));
    Assert.That(Verified(module), Has.Some.Contains("the address is taken of the captured local 'x'"));

    var holder = new IrLambda(signature, [pointer], [], [], new IrBlock([new IrReturn(Load(new IrLocalRef(pointer)))]));
    var holding = Module(IrType.Void, [], [x, pointer, function],
      new IrDeclare(x, Int(1)),
      new IrDeclare(pointer, Op(IrIntrinsicOp.AddressOf, _intPointer, new IrLocalRef(x))),
      new IrDeclare(function, holder));
    Assert.That(Verified(holding), Has.Some.Contains("a lambda holds a pointer"));
  }

  /// <summary>The address is taken of a local only, a pin and a stackalloc into a pointer only initialize, and the pinned local stays.</summary>
  [Test]
  public void PinAndStackAlloc_InitializeADeclaration() {
    var array = new IrLocal("a", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var other = new IrLocal("q", _bytePointer);
    var assignedPin = Module(IrType.Void, [array], [pointer], new IrDeclare(pointer, new IrNull(_bytePointer)),
      new IrAssign(pointer, Op(IrIntrinsicOp.Pin, _bytePointer, new IrLocalRef(array))));
    Assert.That(Verified(assignedPin), Has.Some.Contains("a pin that initializes no declaration"));

    var stack = Module(IrType.U8, [], [], new IrReturn(Load(Op(IrIntrinsicOp.StackAlloc, _bytePointer, Int(1)))));
    Assert.That(Verified(stack), Has.Some.Contains("a stackalloc into a pointer that initializes no declaration"));

    var moved = Pinning(IrType.Void, array, [], pointer, new IrAssign(pointer, Add(new IrLocalRef(pointer), Int(1))));
    Assert.That(Verified(moved), Has.Some.Contains("the pinned pointer 'p' is assigned"));

    var element = Module(IrType.Void, [array], [other], new IrDeclare(other, Op(IrIntrinsicOp.AddressOf, _bytePointer, new IrArrayLoad(new IrLocalRef(array), Int(0)))));
    Assert.That(Verified(element), Has.Some.Contains("the address is taken of something that is no local"));

    var chosen = Pinning(IrType.Void, array, [other], pointer, new IrDeclare(other, new IrConditional(new IrConst(IrType.Bool, 1), new IrLocalRef(pointer), new IrNull(_bytePointer))));
    Assert.That(Verified(chosen), Has.Some.Contains("a conditional expression of pointers"));
  }

  /// <summary>Test design 12's counterpart in the IR: a pointer is no result, field, global, constant or element.</summary>
  [Test]
  public void Pointer_IsStoredOnTheStackOnly() {
    Assert.That(Verified(Module(_intPointer, [], [], new IrReturn(new IrNull(_intPointer)))), Has.Some.Contains("a pointer is stored outside the stack"));
    var holder = new IrStructType("Holder");
    holder.Define([new IrField("P", _intPointer, false)]);
    Assert.That(Verified(new IrModule("M", []) { Types = [holder] }), Has.Some.Contains("'Holder.P': a pointer is stored outside the stack"));
    var global = new IrGlobal("G", _intPointer, null);
    Assert.That(Verified(new IrModule("M", []) { Globals = [global] }), Has.Some.Contains("'G': a pointer is stored outside the stack"));
    Assert.That(Verified(new IrModule("M", [], [new IrModuleConst("K", _intPointer, [0])])), Has.Some.Contains("'K': a pointer is stored outside the stack"));
    var array = new IrLocal("a", IrType.Array(_intPointer));
    Assert.That(Verified(Module(IrType.Void, [array], [])), Has.Some.Contains("has the type i32*[]"));
  }

  // ---------------------------------------------------------------- the interpreter

  /// <summary>
  /// <c>fixed (T* p = a) return p[index];</c> as IR, read through a pointer of another type when
  /// <paramref name="read"/> is given: <c>*(read*)(p + index)</c>.
  /// </summary>
  private static IrModule ReadAt(IrType element, IrType? read = null) {
    var array = new IrLocal("a", IrType.Array(element));
    var index = new IrLocal("i", IrType.I32);
    var pointer = new IrLocal("p", IrType.Pointer(element));
    IrExpr at = Add(new IrLocalRef(pointer), new IrLocalRef(index));
    if (read is not null)
      at = Op(IrIntrinsicOp.PointerCast, IrType.Pointer(read), at);

    return new("M", [new IrFunction("F", true, read ?? element, [array, index], [pointer], new IrBlock([
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, pointer.Type, new IrLocalRef(array))),
      new IrReturn(Load(at))
    ]))]);
  }

  private static byte[] D => [.. Enumerable.Range(0, 16).Select(value => (byte)value)];

  /// <summary>Test design 4: the last element is read, one past the end and one before the start trap.</summary>
  [Test]
  public void Dereference_IsCheckedAgainstTheRegion() {
    var module = ReadAt(IrType.I32);
    Assert.That(Run(module, new[] { 1, 2, 3, 4 }, 3), Is.EqualTo(4));
    Assert.That(Trapped(module, new[] { 1, 2, 3, 4 }, 4), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Trapped(module, new[] { 1, 2, 3, 4 }, -1), Is.EqualTo(IrTrap.IndexOutOfRange));
  }

  /// <summary>Test design 5: a reinterpreted read is little-endian, unaligned, and traps when it straddles the end.</summary>
  [Test]
  public void ReinterpretedRead_IsLittleEndianAndBounded() {
    var module = ReadAt(IrType.U8, IrType.U32);
    Assert.That(Run(module, D, 1), Is.EqualTo(67305985u));
    Assert.That(Run(module, D, 12), Is.EqualTo(252579084u));
    Assert.That(Trapped(module, D, 13), Is.EqualTo(IrTrap.IndexOutOfRange));
  }

  /// <summary>Test design 6: fixed over an empty array and over null gives the null pointer, whose dereference traps.</summary>
  [Test]
  public void FixedOverEmptyOrNull_IsTheNullPointer() {
    var module = ReadAt(IrType.I32);
    Assert.That(Trapped(module, Array.Empty<int>(), 0), Is.EqualTo(IrTrap.NullReference));
    Assert.That(Trapped(module, null!, 0), Is.EqualTo(IrTrap.NullReference));

    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var pointer = new IrLocal("p", _intPointer);
    var isNull = Pinning(IrType.Bool, array, [], pointer, new IrReturn(Op(IrIntrinsicOp.PointerEqual, IrType.Bool, new IrLocalRef(pointer), new IrNull(_intPointer))));
    Assert.That(Run(isNull, Array.Empty<int>()), Is.EqualTo(true));
    Assert.That(Run(isNull, new[] { 1 }), Is.EqualTo(false));
  }

  /// <summary>fixed of an element pins the whole array: the element must exist, and the elements before it are in reach.</summary>
  [Test]
  public void FixedOfAnElement_ReachesTheWholeArray() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var index = new IrLocal("i", IrType.I32);
    var pointer = new IrLocal("p", _intPointer);
    var module = Module(IrType.I32, [array, index], [pointer],
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, _intPointer, new IrLocalRef(array), new IrLocalRef(index))),
      new IrReturn(Load(Add(new IrLocalRef(pointer), Int(-1)))));
    Assert.That(Run(module, new[] { 5, 6, 7 }, 2), Is.EqualTo(6));
    Assert.That(Trapped(module, new[] { 5, 6, 7 }, 3), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Trapped(module, null!, 0), Is.EqualTo(IrTrap.NullReference));
  }

  /// <summary>A pinned view is a region of its own: the elements of the array outside it are out of reach.</summary>
  [Test]
  public void FixedOfAView_IsBoundedByTheView() {
    var array = new IrLocal("a", _bytes);
    var index = new IrLocal("i", IrType.I32);
    var pointer = new IrLocal("p", _bytePointer);
    var view = Op(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U8), new IrLocalRef(array), Int(4), Int(4));
    var module = Module(IrType.U8, [array, index], [pointer],
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, _bytePointer, view)),
      new IrReturn(Load(Add(new IrLocalRef(pointer), new IrLocalRef(index)))));
    Assert.That(Run(module, D, 3), Is.EqualTo((byte)7));
    Assert.That(Trapped(module, D, 4), Is.EqualTo(IrTrap.IndexOutOfRange));
  }

  /// <summary>Semantics: the difference counts elements, both ways; across regions equality is false and an ordering traps.</summary>
  [Test]
  public void DifferenceAndComparison_StayInOneRegion() {
    var first = new IrLocal("a", IrType.Array(IrType.I32));
    var second = new IrLocal("b", IrType.Array(IrType.I32));
    var p = new IrLocal("p", _intPointer);
    var q = new IrLocal("q", _intPointer);
    IrModule Of(IrType result, IrExpr value) => Module(result, [first, second], [p, q],
      new IrDeclare(p, Op(IrIntrinsicOp.Pin, _intPointer, new IrLocalRef(first))),
      new IrDeclare(q, Op(IrIntrinsicOp.Pin, _intPointer, new IrLocalRef(second))),
      new IrReturn(value));
    var ten = new int[10];
    var seven = Add(new IrLocalRef(p), Int(7));
    var two = Add(new IrLocalRef(p), Int(2));
    Assert.That(Run(Of(IrType.I64, Op(IrIntrinsicOp.PointerDifference, IrType.I64, seven, two)), ten, ten), Is.EqualTo(5L));
    Assert.That(Run(Of(IrType.I64, Op(IrIntrinsicOp.PointerDifference, IrType.I64, two, seven)), ten, ten), Is.EqualTo(-5L));
    foreach (var (op, expected) in new[] { (IrIntrinsicOp.PointerLess, true), (IrIntrinsicOp.PointerLessOrEqual, true), (IrIntrinsicOp.PointerGreater, false), (IrIntrinsicOp.PointerGreaterOrEqual, false), (IrIntrinsicOp.PointerEqual, false), (IrIntrinsicOp.PointerNotEqual, true) })
      Assert.That(Run(Of(IrType.Bool, Op(op, IrType.Bool, two, seven)), ten, ten), Is.EqualTo(expected), op.ToString());

    // Pins of one array are one region.
    Assert.That(Run(Of(IrType.Bool, Op(IrIntrinsicOp.PointerEqual, IrType.Bool, new IrLocalRef(p), new IrLocalRef(q))), ten, ten), Is.EqualTo(true));
    Assert.That(Run(Of(IrType.Bool, Op(IrIntrinsicOp.PointerEqual, IrType.Bool, new IrLocalRef(p), new IrLocalRef(q))), ten, new int[10]), Is.EqualTo(false));
    Assert.That(Run(Of(IrType.Bool, Op(IrIntrinsicOp.PointerNotEqual, IrType.Bool, new IrLocalRef(p), new IrLocalRef(q))), ten, new int[10]), Is.EqualTo(true));
    Assert.That(Trapped(Of(IrType.Bool, Op(IrIntrinsicOp.PointerLess, IrType.Bool, new IrLocalRef(p), new IrLocalRef(q))), ten, new int[10]), Is.EqualTo(IrTrap.InvalidOperation));
    Assert.That(Trapped(Of(IrType.I64, Op(IrIntrinsicOp.PointerDifference, IrType.I64, new IrLocalRef(p), new IrLocalRef(q))), ten, new int[10]), Is.EqualTo(IrTrap.InvalidOperation));
  }

  /// <summary>Test design 7: stackalloc into a pointer gives zeros, a region even of no elements, and traps for a negative length.</summary>
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(256, true)]
  public void StackAlloc_IsZeroed(int length, bool readable) {
    var n = new IrLocal("n", IrType.I32);
    var pointer = new IrLocal("p", IrType.Pointer(IrType.I64));
    var module = Module(IrType.I64, [n], [pointer],
      new IrDeclare(pointer, Op(IrIntrinsicOp.StackAlloc, pointer.Type, new IrLocalRef(n))),
      new IrReturn(Load(Add(new IrLocalRef(pointer), new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), Int(1))))));
    if (readable)
      Assert.That(Run(module, length), Is.EqualTo(0L));
    else
      Assert.That(Trapped(module, length), Is.EqualTo(IrTrap.IndexOutOfRange));

    Assert.That(Trapped(module, -1), Is.EqualTo(IrTrap.IntegerOverflow));
  }

  /// <summary>The address of a local reads and writes the local itself: <c>*(long*)&amp;d = 0x3FF8000000000000</c> makes d 1.5.</summary>
  [Test]
  public void AddressOfLocal_WritesTheLocal() {
    var d = new IrLocal("d", IrType.F64);
    var module = Module(IrType.F64, [], [d],
      new IrDeclare(d, new IrConst(IrType.F64, 0)),
      new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void,
        Op(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.I64), Op(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.F64), new IrLocalRef(d))),
        new IrConst(IrType.I64, 0x3FF8000000000000))),
      new IrReturn(new IrLocalRef(d)));
    Assert.That(Run(module), Is.EqualTo(1.5));

    var x = new IrLocal("x", IrType.I32);
    var p = new IrLocal("p", _intPointer);
    var counted = Module(IrType.I32, [], [x, p],
      new IrDeclare(x, null),
      new IrDeclare(p, Op(IrIntrinsicOp.AddressOf, _intPointer, new IrLocalRef(x))),
      new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, new IrLocalRef(p), Int(6))),
      new IrAssign(x, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), Int(1))),
      new IrReturn(Load(new IrLocalRef(p))));
    Assert.That(Run(counted), Is.EqualTo(7));
  }

  /// <summary>A store evaluates its value first and then checks, a load through the null pointer traps, and the null pointer moved stays null.</summary>
  [Test]
  public void NullPointer_TrapsOnAccess() {
    var p = new IrLocal("p", _intPointer);
    var load = Module(IrType.I32, [], [p], new IrDeclare(p, new IrNull(_intPointer)), new IrReturn(Load(Add(new IrLocalRef(p), Int(2)))));
    Assert.That(Trapped(load), Is.EqualTo(IrTrap.NullReference));
    var store = Module(IrType.Void, [], [p], new IrDeclare(p, new IrNull(_intPointer)),
      new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, new IrLocalRef(p), new IrBinary(IrBinaryOp.Divide, Int(1), Int(0)))));
    Assert.That(Trapped(store), Is.EqualTo(IrTrap.DivideByZero));
    var distance = Module(IrType.I64, [], [p], new IrDeclare(p, new IrNull(_intPointer)),
      new IrReturn(Op(IrIntrinsicOp.PointerDifference, IrType.I64, Add(new IrLocalRef(p), Int(3)), new IrLocalRef(p))));
    Assert.That(Run(distance), Is.EqualTo(3L));
  }

  /// <summary>Test design 8: a store and a load of every element type through a pointer of bytes, as .NET reads the same bytes.</summary>
  [TestCase("u8", 255UL)]
  [TestCase("i16", 0xFFF9UL)]
  [TestCase("u16", 0xABCDUL)]
  [TestCase("i32", 0x80000000UL)]
  [TestCase("u32", 0xFFFFFFFFUL)]
  [TestCase("i64", 0x8000000000000000UL)]
  [TestCase("u64", 0xFFFFFFFFFFFFFFFFUL)]
  [TestCase("f32", 0x3FC00000UL)]
  [TestCase("f64", 0x7FF8000000000000UL)]
  public void EveryElementType_RoundTripsThroughBytes(string type, ulong bits) {
    var element = Type(type);
    var bytes = new IrLocal("b", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var typed = Op(IrIntrinsicOp.PointerCast, IrType.Pointer(element), Add(new IrLocalRef(pointer), Int(1)));
    var value = new IrConst(element, element.IsFloat ? (long)bits : IrOperators.Wrap(element, (long)bits));
    var module = Pinning(element, bytes, [], pointer,
      new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, typed, value)),
      new IrReturn(Load(typed)));
    var data = new byte[12];
    var result = Run(module, data);
    var expected = new byte[12];
    BitConverter.TryWriteBytes(expected.AsSpan(1), bits);
    Assert.That(data[1..(1 + element.Size)], Is.EqualTo(expected[1..(1 + element.Size)]));
    Assert.That(data[(1 + element.Size)..], Is.All.EqualTo((byte)0));
    Assert.That(result, Is.Not.Null);
  }

  /// <summary>Pointers cross the module format with their element types, void included.</summary>
  [Test]
  public void ModuleFormat_KeepsPointers() {
    var module = ReadAt(IrType.U8, IrType.U32);
    var voided = new IrLocal("v", IrType.Pointer(IrType.Void));
    module = module with { Functions = [module.Functions[0] with { Locals = [.. module.Functions[0].Locals, voided] }] };
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(read.Functions[0].Locals.Select(local => local.Type), Is.EqualTo(new[] { _bytePointer, IrType.Pointer(IrType.Void) }));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
    Assert.That(IrInterpreter.Call(read, "F", [D, 1]), Is.EqualTo(67305985u));
  }

}
