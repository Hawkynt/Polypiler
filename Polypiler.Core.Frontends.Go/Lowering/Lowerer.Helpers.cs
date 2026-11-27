using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go.Lowering;

internal sealed partial class Lowerer {

  /// <summary>The name of an integer type for the name of a helper: <c>Int32</c>, <c>Uint8</c>.</summary>
  private static string Title(IrType type) => type.Kind switch {
    IrTypeKind.I8 => "Int8",
    IrTypeKind.U8 => "Uint8",
    IrTypeKind.I16 => "Int16",
    IrTypeKind.U16 => "Uint16",
    IrTypeKind.I32 => "Int32",
    IrTypeKind.U32 => "Uint32",
    IrTypeKind.I64 => "Int64",
    _ => "Uint64"
  };

  /// <summary>Adds a helper function to the module once, under a name no function of the source has.</summary>
  private string HelperOf(string wanted, System.Func<string, IrFunction> build) {
    if (this._helperNames.TryGetValue(wanted, out var known))
      return known;

    var name = wanted;
    for (var suffix = 2; this._signatures.ContainsKey(name); ++suffix)
      name = wanted + suffix.ToString(CultureInfo.InvariantCulture);

    this._helperNames[wanted] = name;
    this._helpers.Add(build(name));
    return name;
  }

  /// <summary>
  /// The signed division or remainder of Go: the smallest value divided by -1 is the smallest value
  /// again, its remainder 0, where the IR's operator traps; a zero divisor traps in both.
  /// </summary>
  private string DivisionHelper(bool quotient, IrType type) => this.HelperOf((quotient ? "goDiv" : "goRem") + Title(type), name => {
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    IrExpr byMinusOne = quotient ? new IrUnary(IrUnaryOp.Negate, new IrLocalRef(a)) : new IrConst(type, 0);
    var body = new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(b), new IrConst(type, -1)), new IrBlock([new IrReturn(byMinusOne)]), null),
      new IrReturn(new IrBinary(quotient ? IrBinaryOp.Divide : IrBinaryOp.Remainder, new IrLocalRef(a), new IrLocalRef(b)))
    ]);
    return new IrFunction(name, false, type, [a, b], [], body);
  });

  /// <summary>
  /// A shift of Go by a count that is no constant below the width: a negative count of a signed type
  /// traps, as Go panics with "negative shift amount"; a count of the width or more gives 0, or the
  /// sign of a signed value shifted right; any other is the IR's shift.
  /// </summary>
  private string ShiftHelper(bool left, IrType type, IrType countType) => this.HelperOf((left ? "goShl" : "goShr") + Title(type) + "By" + Title(countType), name => {
    var (x, n) = (new IrLocal("x", type), new IrLocal("n", countType));
    var count = Comparable(new IrLocalRef(n));
    var statements = new List<IrStmt>();
    if (countType.IsSigned)
      statements.Add(new IrIf(new IrBinary(IrBinaryOp.Less, count, new IrConst(count.Type, 0)),
        new IrBlock([new IrThrow(Exception(IrExceptionClass.ArithmeticException, new IrStrConst("negative shift amount")))]), null));

    var value = Comparable(new IrLocalRef(x));
    IrExpr beyond = left || !type.IsSigned
      ? new IrConst(type, 0)
      : Wrapped(new IrBinary(IrBinaryOp.ShiftRight, value, new IrConst(IrType.I32, type.Bits - 1)), type);
    statements.Add(new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, count, new IrConst(count.Type, type.Bits)), new IrBlock([new IrReturn(beyond)]), null));
    var shifted = new IrBinary(left ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight, value, count.Type == IrType.I32 ? count : new IrConvert(IrType.I32, count));
    statements.Add(new IrReturn(Wrapped(shifted, type)));
    return new IrFunction(name, false, type, [x, n], [], new IrBlock(statements));
  });

}
