using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// An operator on the arbitrary-precision integer, which is Python's own <c>int</c>: addition,
    /// subtraction and multiplication as they are, the truncating division and remainder and a shift
    /// by a count that may be negative through helpers - a division of a dividend that is not negative
    /// by a positive constant, and a shift by a constant that is not, as Python's operators.
    /// </summary>
    private PyExpression BigBinary(IrBinary binary, PyExpression left, PyExpression right) {
      var plain = this.NonNegative(binary.Left) && binary.Right is IrBigConst { Value.Sign: > 0 };
      var counted = binary.Right is IrConst { Value: >= 0 };
      return binary.Op switch {
        IrBinaryOp.Add => Binary(left, "+", right),
        IrBinaryOp.Subtract => Binary(left, "-", right),
        IrBinaryOp.Multiply => Binary(left, "*", right),
        IrBinaryOp.Divide when plain => Binary(left, "//", right),
        IrBinaryOp.Divide => this.Helper(PythonHelpers.BigDivide, left, right),
        IrBinaryOp.Remainder when plain => Binary(left, "%", right),
        IrBinaryOp.Remainder => this.Helper(PythonHelpers.BigRemainder, left, right),
        IrBinaryOp.ShiftLeft when counted => Binary(left, "<<", right),
        IrBinaryOp.ShiftLeft => this.Helper(PythonHelpers.BigShiftLeft, left, right),
        IrBinaryOp.ShiftRight when counted => Binary(left, ">>", right),
        _ => this.Helper(PythonHelpers.BigShiftRight, left, right)
      };
    }

    /// <summary>
    /// A conversion from or to the arbitrary-precision integer: from an integer nothing, from a float
    /// the truncation that traps on a NaN and an infinity; to a fixed width the mask and the sign-wrap
    /// or the check of the range; to a float the nearest one, an infinity where <c>float</c> raises.
    /// </summary>
    private PyExpression BigConversion(IrConvert conversion, PyExpression operand) {
      var (from, to) = (conversion.Operand.Type, conversion.Type);
      if (to.IsBigInt)
        return from.IsFloat ? this.Helper(PythonHelpers.FloatToBig, operand) : operand;

      if (to.IsFloat)
        return this.Helper(PythonHelpers.BigToFloat, operand);

      return conversion.Contract == IrConversion.Wrap ? Wrapped(operand, to) : this.Checked(operand, to);
    }

  }

}
