using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Pointers;

/// <summary>
/// Arrays in memory (#111): <see cref="IrArrayShape"/> - rank 1 to 8, lower bounds, row and column order and the
/// index formula - and <see cref="IrElementAddress"/>, the address of an element, checked per dimension, in the
/// verifier, the interpreter, the lowering and the form in bytes. Row-major offsets are those of C
/// (<c>a[i][j]</c> at <c>i * 4 + j</c> in <c>int a[3][4]</c>); column-major those of Fortran.
/// </summary>
[TestFixture]
public sealed class ElementAddressTests {

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrArrayShape Shape(IrStorageOrder order, params long[] extents) => new([.. extents.Select(extent => new IrDimension(0, extent))], order);

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  /// <summary><c>F(elements, i…)</c>: pins the <c>i32</c> elements and loads the one the indices name; one parameter per index type.</summary>
  private static IrModule Loading(IrArrayShape shape, bool isChecked, params IrType[] indexTypes) {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var pointer = new IrLocal("p", IrType.Pointer(IrType.I32));
    var indices = indexTypes.Select((type, position) => new IrLocal($"i{position}", type)).ToList();
    var address = new IrElementAddress(new IrLocalRef(pointer), shape, [.. indices.Select(index => (IrExpr)new IrLocalRef(index))], isChecked);
    return new IrModule("M", [new IrFunction("F", true, IrType.I32, [array, .. indices], [pointer], new IrBlock([
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.Pin, pointer.Type, [new IrLocalRef(array)])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [address]))]))]);
  }

  /// <summary>Elements that hold their linear positions.</summary>
  private static int[] Positions(long count) => [.. Enumerable.Range(0, (int)count)];

  /// <summary>The value the module returns, or the trap, interpreted and - for an unchecked address - again after lowering.</summary>
  private static object Outcome(IrModule module, params object[] arguments) {
    Assert.That(Verified(module), Is.Empty);
    object Run(IrModule run) {
      try {
        return IrInterpreter.Call(run, "F", arguments)!;
      } catch (IrTrapException trap) {
        return trap.Trap;
      }
    }

    var direct = Run(module);
    var lowered = IrLayoutLowering.Lower(module, LayoutProfile.Lp64SysV);
    Assert.That(Verified(lowered), Is.Empty);
    Assert.That(Run(lowered), Is.EqualTo(direct), "the lowered module agrees");
    return direct;
  }

  /// <summary>Scenario "Indexing honours the lower bound", test design 1, 3 and 5.</summary>
  [TestCase(0, 0, 0)]
  [TestCase(0, 9, 9)]
  [TestCase(0, 10, -1)]
  [TestCase(1, 1, 0)]
  [TestCase(1, 10, 9)]
  [TestCase(1, 0, -1)]
  [TestCase(0, -1, -1)]
  [TestCase(-5, -5, 0)]
  [TestCase(-5, 4, 9)]
  [TestCase(-5, 5, -1)]
  public void Indexing_HonoursTheLowerBound(long lower, int index, int expected) {
    var module = Loading(new IrArrayShape([new IrDimension(lower, 10)]), true, IrType.I32);
    Assert.That(Outcome(module, Positions(10), index), Is.EqualTo(expected < 0 ? IrTrap.IndexOutOfRange : expected));
  }

  /// <summary>Scenario "Storage order decides the element position", test design 4.</summary>
  [TestCase(IrStorageOrder.Row, new long[] { 3, 4 }, new long[] { 1, 2 }, 6)]
  [TestCase(IrStorageOrder.Column, new long[] { 3, 4 }, new long[] { 1, 2 }, 7)]
  [TestCase(IrStorageOrder.Row, new long[] { 2, 3, 4 }, new long[] { 1, 0, 2 }, 14)]
  [TestCase(IrStorageOrder.Column, new long[] { 2, 3, 4 }, new long[] { 1, 0, 2 }, 13)]
  public void StorageOrder_DecidesThePosition(IrStorageOrder order, long[] extents, long[] indices, long position) {
    var shape = Shape(order, extents);
    Assert.That((shape.Linear(indices), shape.Unchecked(indices)), Is.EqualTo((position, position)));
    var module = Loading(shape, true, [.. indices.Select(_ => IrType.I32)]);
    Assert.That(Outcome(module, [Positions(shape.Count), .. indices.Select(index => (object)(int)index)]), Is.EqualTo((int)position));
  }

  /// <summary>Scenario "Each dimension is checked on its own": (0, 4) of a 3 × 4 array traps, though position 4 exists; unchecked it is element 4.</summary>
  [Test]
  public void EachDimension_IsCheckedOnItsOwn() {
    var shape = Shape(IrStorageOrder.Row, 3, 4);
    Assert.That(() => shape.Linear([0, 4]), Throws.TypeOf<IrTrapException>());
    Assert.That((shape.Outside([0, 4]), shape.Outside([2, 3]), shape.Unchecked([0, 4])), Is.EqualTo((1, -1, 4L)));
    Assert.That(Outcome(Loading(shape, true, IrType.I32, IrType.I32), Positions(12), 0, 4), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Outcome(Loading(shape, false, IrType.I32, IrType.I32), Positions(12), 0, 4), Is.EqualTo(4));
  }

  /// <summary>Test design 6: an extent of 0 traps on every index, an extent of 1 takes its one index.</summary>
  [Test]
  public void Extents_ZeroAndOne() {
    var empty = new IrArrayShape([new IrDimension(0, 0)]);
    Assert.That((empty.Count, empty.Dimensions[0].Upper, empty.Dimensions[0].Contains(0)), Is.EqualTo((0L, -1L, false)));
    Assert.That(Outcome(Loading(empty, true, IrType.I32), Positions(1), 0), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Outcome(Loading(IrArrayShape.Fixed(1), true, IrType.I32), Positions(1), 0), Is.EqualTo(0));
  }

  /// <summary>Test design 7: rank 1 and 8 are shapes, 0 and 9 are not, nor is a negative extent.</summary>
  [Test]
  public void Rank_IsOneToEight() {
    Assert.That(Shape(IrStorageOrder.Row, 2, 2, 2, 2, 2, 2, 2, 2).Count, Is.EqualTo(256));
    Assert.That(Shape(IrStorageOrder.Column, 2, 2, 2, 2, 2, 2, 2, 2).Strides, Is.EqualTo(new long[] { 1, 2, 4, 8, 16, 32, 64, 128 }));
    Assert.That(() => Shape(IrStorageOrder.Row, 2, 2, 2, 2, 2, 2, 2, 2, 2), Throws.ArgumentException.With.Message.Contains("rank 9"));
    Assert.That(() => new IrArrayShape([]), Throws.ArgumentException);
    Assert.That(() => new IrArrayShape([new IrDimension(0, -1)]), Throws.ArgumentException.With.Message.Contains("extent -1"));
    Assert.That(() => new IrArrayShape(null!), Throws.ArgumentNullException);
    Assert.That(() => IrArrayShape.Fixed(2).Linear([0, 0]), Throws.ArgumentException.With.Message.Contains("2 indices for an array of rank 1"));
    Assert.That(() => IrArrayShape.Fixed(2).Unchecked(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 8: a <c>u8</c> of 255 is zero-extended, an <c>i64</c> of 2^40 is never truncated, a <c>u64</c> above 2^63 is outside.</summary>
  [Test]
  public void Indices_AreExtendedByTheirType() {
    var wide = new IrArrayShape([new IrDimension(0, 256)]);
    Assert.That(Outcome(Loading(wide, true, IrType.U8), Positions(256), (byte)255), Is.EqualTo(255));
    var shifted = new IrArrayShape([new IrDimension((1L << 40) - 3, 4)]);
    Assert.That(Outcome(Loading(shifted, true, IrType.I64), Positions(4), (1L << 40) - 1), Is.EqualTo(2));
    Assert.That(Outcome(Loading(shifted, true, IrType.I64), Positions(4), 1L << 41), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Outcome(Loading(shifted, true, IrType.I64), Positions(4), 1L), Is.EqualTo(IrTrap.IndexOutOfRange));
    var negative = new IrArrayShape([new IrDimension(-5, 10)]);
    Assert.That(Outcome(Loading(negative, true, IrType.U64), Positions(10), ulong.MaxValue), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Outcome(Loading(negative, true, IrType.U64), Positions(10), 4UL), Is.EqualTo(9));
    Assert.That(IrArrays.Spelled(IrType.U64, -1), Is.EqualTo("18446744073709551615"));
    Assert.That(new IrDimension(-5, 10).Contains(long.MaxValue), Is.False);
  }

  /// <summary>Test design 2: a fixed array and a shape of the same extent address alike.</summary>
  [Test]
  public void FixedArray_AddressesAsAShape() {
    Assert.That(IrArrayShape.Fixed(16), Is.EqualTo(new IrArrayShape([new IrDimension(0, 16)], IrStorageOrder.Row)));
    Assert.That(IrArrayShape.Fixed(16), Is.Not.EqualTo(new IrArrayShape([new IrDimension(0, 16)], IrStorageOrder.Column)));
    Assert.That(IrArrayShape.Fixed(16).GetHashCode(), Is.EqualTo(new IrArrayShape([new IrDimension(0, 16)]).GetHashCode()));
    Assert.That((IrArrayShape.Fixed(16).ToString(), new IrArrayShape([new IrDimension(1, 3), new IrDimension(0, 2)], IrStorageOrder.Column).ToString()),
      Is.EqualTo(("[0..15] row", "[1..3, 0..1] column")));
    Assert.That(Outcome(Loading(IrArrayShape.Fixed(16), false, IrType.I32), Positions(16), 3), Is.EqualTo(3));
  }

  /// <summary>Scenario "A constant index out of range is rejected", scenario "The wrong number of indices", test design 9 and 10.</summary>
  [Test]
  public void Verifier_RefusesBadIndices() {
    var pointer = new IrNull(IrType.Pointer(IrType.U8));
    IReadOnlyList<string> Problems(IrExpr address) => Verified(new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new IrExprStmt(address)]))]));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(16), [Int(16)], false)), Has.Some.Contains("constant index 16 is outside 0..15 in dimension 0"));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(16), [new IrConst(IrType.U64, -1)], true)), Has.Some.Contains("constant index 18446744073709551615 is outside"));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(16), [Int(15)], false)), Is.Empty);
    Assert.That(Problems(new IrElementAddress(pointer, Shape(IrStorageOrder.Row, 3, 4), [Int(1)], true)), Has.Some.Contains("'ElemAddr' on a rank-2 array needs 2 indices, got 1"));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(4), [Int(1), Int(2)], true)), Has.Some.Contains("needs 1 indices, got 2"));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(4), [new IrConst(IrType.F64, 0)], true)), Has.Some.Contains("index 0 has type f64, expected an integer"));
    Assert.That(Problems(new IrElementAddress(new IrNull(IrType.Pointer(IrType.Void)), IrArrayShape.Fixed(4), [Int(1)], true)), Has.Some.Contains("not a pointer to a number"));
    Assert.That(Problems(new IrElementAddress(pointer, IrArrayShape.Fixed(4), [Int(1)], true) with { Type = IrType.Pointer(IrType.U16) }), Has.Some.Contains("expected u8*"));
  }

  /// <summary>Lowering writes an unchecked address as pointer arithmetic, folds constant indices, and leaves a checked one.</summary>
  [Test]
  public void Lowering_WritesUncheckedAddresses() {
    var shape = new IrArrayShape([new IrDimension(1, 3), new IrDimension(-2, 4)], IrStorageOrder.Column);
    var module = Loading(shape, false, IrType.I16, IrType.U8);
    var lowered = IrLayoutLowering.Lower(module, LayoutProfile.Dos16);
    Assert.That(lowered.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrElementAddress>(), Is.Empty);
    // (3 - 1) + (1 - -2) * 3 = 11
    Assert.That(Outcome(module, Positions(12), (short)3, (byte)1), Is.EqualTo(11));
    var checkedModule = Loading(shape, true, IrType.I16, IrType.U8);
    Assert.That(IrLayoutLowering.Lower(checkedModule, LayoutProfile.Dos16), Is.SameAs(checkedModule));

    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var pointer = new IrLocal("p", IrType.Pointer(IrType.I32));
    IrModule Constant(IrArrayShape constantShape, params long[] indices) => new("M", [new IrFunction("F", true, IrType.I32, [array], [pointer], new IrBlock([
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.Pin, pointer.Type, [new IrLocalRef(array)])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [new IrElementAddress(new IrLocalRef(pointer), constantShape, [.. indices.Select(index => new IrConst(IrType.I64, index))], false)]))]))]);
    var folded = IrLayoutLowering.Lower(Constant(Shape(IrStorageOrder.Row, 3, 4), 2, 1), LayoutProfile.Lp64SysV);
    Assert.That(folded.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrConst>().Select(constant => constant.Value), Does.Contain(9L));
    Assert.That(IrInterpreter.Call(folded, "F", [Positions(12)]), Is.EqualTo(9));
    var far = IrLayoutLowering.Lower(Constant(new IrArrayShape([new IrDimension(0, 1L << 40)]), (1L << 40) - 1), LayoutProfile.Lp64SysV);
    Assert.That(Verified(far), Is.Empty);
    Assert.That(Assert.Throws<IrTrapException>(() => IrInterpreter.Call(far, "F", [Positions(1)]))!.Trap, Is.EqualTo(IrTrap.IntegerOverflow));
  }

  /// <summary>An element address comes back from the form in bytes, shape, check and all.</summary>
  [Test]
  public void Format_RoundTripsElementAddresses() {
    var module = Loading(new IrArrayShape([new IrDimension(1, 3), new IrDimension(-2, 4)], IrStorageOrder.Column), true, IrType.I32, IrType.I32);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    var address = read.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrElementAddress>().Single();
    Assert.That((address.Shape, address.Checked, address.Indices.Count), Is.EqualTo((new IrArrayShape([new IrDimension(1, 3), new IrDimension(-2, 4)], IrStorageOrder.Column), true, 2)));
    Assert.That(IrInterpreter.Call(read, "F", [Positions(12), 3, 1]), Is.EqualTo(11));
  }

}
