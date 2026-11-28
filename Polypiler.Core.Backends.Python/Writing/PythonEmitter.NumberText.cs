using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// A conversion between a number and text. An integer is <c>str</c> of it in both rules, a truth
    /// value the spelling of its rule; every other conversion is a helper of the rule's layout or grammar.
    /// <see langword="null"/> for any other operation.
    /// </summary>
    private PyExpression? NumberText(IrIntrinsic intrinsic) {
      if (!NumberTextOperations.IsNumberText(intrinsic.Op))
        return null;

      var arguments = intrinsic.Arguments;
      PyExpression Arg(int index) => this.Expression(arguments[index]);
      var operand = arguments[0].Type;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.FormatEcma or IrIntrinsicOp.FormatDotNet when operand.IsInteger:
          return new PyCall(Name("str"), [Arg(0)]);
        case IrIntrinsicOp.FormatEcma:
          return this.Helper(PythonHelpers.FormatEcma, Arg(0));
        case IrIntrinsicOp.FormatDotNet:
          return this.Helper(PythonHelpers.FormatDotNet, Arg(0), new PyBool(operand == IrType.F32));
        case IrIntrinsicOp.FormatBoolEcma or IrIntrinsicOp.FormatBoolDotNet:
          var (yes, no) = intrinsic.Op == IrIntrinsicOp.FormatBoolEcma ? ("true", "false") : ("True", "False");
          return new PyConditional(Arg(0), new PyString(yes), new PyString(no));
        case IrIntrinsicOp.FormatRadix:
          var (radix, padding, upper, mode) = NumberTextOperations.RadixOf(intrinsic)!.Value;
          if (radix == 16 && mode == RadixMode.TwoComplement && padding <= 1 && !upper && !operand.IsSigned)
            return new PyCall(Name("format"), [Arg(0), new PyString("x")]);

          return this.Helper(PythonHelpers.FormatRadix, Arg(0), Integer(operand.Bits), Integer(radix), Integer(padding), new PyBool(upper), new PyBool(mode == RadixMode.TwoComplement));
        case IrIntrinsicOp.FixedEcma:
          return this.Helper(PythonHelpers.FixedEcma, Arg(0), Arg(1));
        case IrIntrinsicOp.FixedDotNet:
          return this.Helper(PythonHelpers.FixedDotNet, Arg(0), Arg(1));
        case IrIntrinsicOp.ParseNumberEcma:
          return this.Helper(PythonHelpers.ParseNumberEcma, Arg(0));
        case IrIntrinsicOp.ParseFloatEcma:
          return this.Helper(PythonHelpers.ParseFloatEcma, Arg(0));
        case IrIntrinsicOp.ParseIntEcma:
          return this.Helper(PythonHelpers.ParseIntEcma, Arg(0), arguments.Count > 1 ? Arg(1) : Integer(0));
        case IrIntrinsicOp.FormatVisualBasic:
          return this.Helper(PythonHelpers.FormatVisualBasic, Arg(0), Integer(operand == IrType.F32 ? 7 : 15), new PyString(NumberTextOperations.SeparatorOf(intrinsic)!.Value.ToString()));
        default:
          var type = intrinsic.Type;
          if (type.IsFloat)
            return this.Helper(PythonHelpers.ParseFloatDotNet, Arg(0), new PyBool(type == IrType.F32));

          return this.Helper(PythonHelpers.ParseIntDotNet, Arg(0), Smallest(type), Largest(type));
      }
    }

    /// <summary>
    /// The operations on pointers: a pointer is a <c>_PlyPointer</c> of a region and a byte offset, the
    /// null pointer <c>None</c>; a local whose address is taken is a cell, which is the region.
    /// <see langword="null"/> for any other operation.
    /// </summary>
    private PyExpression? PointerOperation(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      PyExpression Arg(int index) => this.Expression(arguments[index]);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.Pin when arguments.Count == 1:
          return this.Helper(PythonHelpers.Pin, Arg(0), new PyString(Format(arguments[0].Type.Element!)));
        case IrIntrinsicOp.Pin:
          return this.Helper(PythonHelpers.PinAt, Arg(0), Arg(1), new PyString(Format(arguments[0].Type.Element!)));
        case IrIntrinsicOp.AddressOf when arguments[0] is IrLocalRef { Local: var local }:
          return new PyCall(Name(this.PointerClass()), [Name(this.LocalName(local)), Integer(0), Integer(1), new PyString(Format(local.Type)), Integer(0)]);
        case IrIntrinsicOp.StackAlloc when intrinsic.Type.IsPointer:
          var element = intrinsic.Type.Element!;
          var length = this.NonNegative(arguments[0]) ? Arg(0) : this.Helper(PythonHelpers.Length, Arg(0));
          var held = this.Temporary();
          var zeros = Binary(new PyList([this.FieldDefault(element)]), "*", new PyNamedExpression(held, length));
          return new PyCall(Name(this.PointerClass()), [zeros, Integer(0), Name(held), new PyString(Format(element)), Integer(0)]);
        case IrIntrinsicOp.PointerAdd:
          return this.Helper(PythonHelpers.PointerAdd, Arg(0), Arg(1), Integer(Size(intrinsic.Type.Element!)));
        case IrIntrinsicOp.PointerCast:
          return Arg(0);
        case IrIntrinsicOp.PointerLoad:
          return this.Helper(PythonHelpers.PointerLoad, Arg(0), new PyString(Format(intrinsic.Type)));
        case IrIntrinsicOp.PointerStore:
          return this.Helper(PythonHelpers.PointerStore, Arg(0), new PyString(Format(arguments[1].Type)), Arg(1));
        case IrIntrinsicOp.PointerDifference:
          return this.Helper(PythonHelpers.PointerDifference, Arg(0), Arg(1), Integer(Size(arguments[0].Type.Element!)));
        case IrIntrinsicOp.PointerEqual:
          return this.Helper(PythonHelpers.PointerEqual, Arg(0), Arg(1));
        case IrIntrinsicOp.PointerNotEqual:
          return Not(this.Helper(PythonHelpers.PointerEqual, Arg(0), Arg(1)));
        case IrIntrinsicOp.PointerLess or IrIntrinsicOp.PointerLessOrEqual or IrIntrinsicOp.PointerGreater or IrIntrinsicOp.PointerGreaterOrEqual:
          var op = intrinsic.Op switch {
            IrIntrinsicOp.PointerLess => "<",
            IrIntrinsicOp.PointerLessOrEqual => "<=",
            IrIntrinsicOp.PointerGreater => ">",
            _ => ">="
          };
          return Compare(this.Helper(PythonHelpers.PointerDistance, Arg(0), Arg(1)), op, Integer(0));
        default:
          return null;
      }
    }

    /// <summary>The size in bytes of an element a pointer steps by; one for <c>void</c>.</summary>
    private static int Size(IrType element) => element.IsNumeric ? element.Size : 1;

  }

}
