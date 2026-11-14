using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Records;

/// <summary>Modules of value records built by hand, for what no frontend writes or what every backend has to.</summary>
internal static class RecordModules {

  public static readonly IrStructType Inner = new("Inner", [new IrField("A", IrType.I64, false), new IrField("B", IrType.Bool, false)]);

  public static readonly IrStructType Outer = new("Outer", [
    new IrField("In", IrType.Struct(Inner), false), new IrField("F", IrType.F32, false), new IrField("D", IrType.F64, false), new IrField("U", IrType.U64, false)
  ]);

  public static IrType InnerType => IrType.Struct(Inner);

  public static IrType OuterType => IrType.Struct(Outer);

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IrConst I(int value) => new(IrType.I32, value);

  /// <summary>
  /// A module that uses every node of the value records: new values and defaults, field loads and
  /// stores through every kind of place, equality, arrays of structs created, listed, read and
  /// written, and a struct chosen by a condition. <c>Run(n)</c> returns 1 when everything holds.
  /// </summary>
  public static IrModule Everything() {
    var o = new IrLocal("o", OuterType);
    var copy = new IrLocal("copy", OuterType);
    var items = new IrLocal("items", IrType.Array(OuterType));
    var listed = new IrLocal("listed", IrType.Array(InnerType));
    var n = new IrLocal("n", IrType.I32);
    var inField = Outer.Fields[0];
    IrExpr NewInner(long a, bool b) => new IrStructNew(InnerType, [new IrConst(IrType.I64, a), new IrConst(IrType.Bool, b ? 1 : 0)]);
    var body = new IrBlock([
      new IrDeclare(o, new IrStructNew(OuterType, [NewInner(5, true), new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 1.5)), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.NaN)), new IrConst(IrType.U64, -1)])),
      new IrDeclare(copy, Ref(o)),
      new IrFieldStore(new IrFieldLoad(Ref(copy), inField), Inner.Fields[0], new IrConst(IrType.I64, 6)),
      new IrDeclare(items, new IrNewArray(IrType.Array(OuterType), Ref(n))),
      new IrStore(Ref(items), I(0), Ref(copy)),
      new IrFieldStore(new IrArrayLoad(Ref(items), I(0)), Outer.Fields[1], new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 2.5))),
      new IrDeclare(listed, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(InnerType), [NewInner(5, true), new IrStructDefault(InnerType)])),
      new IrFieldStore(Ref(o), Outer.Fields[2], new IrConditional(new IrBinary(IrBinaryOp.Equal, Ref(n), I(1)), new IrFieldLoad(Ref(copy), Outer.Fields[2]), new IrConst(IrType.F64, 0))),
      new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd,
        new IrBinary(IrBinaryOp.LogicalAnd,
          new IrStructEquals(new IrFieldLoad(Ref(o), inField), new IrArrayLoad(Ref(listed), I(0))),
          new IrUnary(IrUnaryOp.LogicalNot, new IrStructEquals(Ref(o), Ref(copy)))),
        new IrBinary(IrBinaryOp.LogicalAnd,
          new IrStructEquals(new IrConditional(new IrBinary(IrBinaryOp.Greater, Ref(n), I(0)), new IrArrayLoad(Ref(listed), I(1)), new IrStructDefault(InnerType)), new IrStructDefault(InnerType)),
          new IrBinary(IrBinaryOp.Equal, new IrFieldLoad(new IrFieldLoad(new IrArrayLoad(Ref(items), I(0)), inField), Inner.Fields[0]), new IrConst(IrType.I64, 6)))))
    ]);
    var run = new IrFunction("Run", true, IrType.Bool, [n], [o, copy, items, listed], body);
    return new IrModule("Everything", [run]) { Types = [Inner, Outer] };
  }

  /// <summary>A struct of a given number of f64 fields, with a function that builds one, compares it with itself and returns the sum of its fields.</summary>
  public static IrModule Wide(int count) {
    var wide = new IrStructType("Wide", [.. Enumerable.Range(0, count).Select(index => new IrField("F" + index, IrType.F64, true))]);
    var type = IrType.Struct(wide);
    var w = new IrLocal("w", type);
    IrExpr sum = new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 0));
    foreach (var field in wide.Fields)
      sum = new IrBinary(IrBinaryOp.Add, sum, new IrFieldLoad(Ref(w), field));

    var body = new IrBlock([
      new IrDeclare(w, new IrStructNew(type, [.. Enumerable.Range(0, count).Select(index => new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, index)))])),
      new IrReturn(new IrConditional(new IrStructEquals(Ref(w), Ref(w)), sum, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, -1))))
    ]);
    return new IrModule("Many", [new IrFunction("Sum", true, IrType.F64, [], [w], body)]) { Types = [wide] };
  }

}
