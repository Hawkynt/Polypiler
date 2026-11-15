using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>The line of the header that names the caveats of the import, or none for a module without one.</summary>
  private static IReadOnlyList<string> Caveats(IrModule module)
    => module.Caveats.Count == 0 ? [] : [$"Caveats of the import: {string.Join(", ", module.Caveats)}."];

  private sealed partial class Writer {

    /// <summary>
    /// A conversion between a number and text. The rules of ECMA-262 are JavaScript's own -
    /// <c>String</c>, <c>toFixed</c>, <c>Number</c>, <c>parseFloat</c>, <c>parseInt</c> - and a
    /// 64-bit integer below ECMAScript 2020 is written digit by digit from its halves. The rules of
    /// .NET are helpers: the shortest digits of <c>String</c> in the layout of .NET, those of a
    /// binary32 by <c>toPrecision</c>, the exact digits of <c>"F&lt;n&gt;"</c>, and the grammar of
    /// <c>Parse</c>, which throws the <c>RangeError</c> of the IR's traps.
    /// </summary>
    private (JsExpression Node, bool Atomic) NumberText(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      var operand = arguments[0].Type;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.FormatEcma or IrIntrinsicOp.FormatDotNet when operand.IsLong && this.Halves:
          return (this.Helper("$radix64", [this.Text(arguments[0]), Number("10"), Number("0"), new JsBoolean(false), new JsBoolean(operand.IsSigned)]), true);
        case IrIntrinsicOp.FormatDotNet when operand.IsFloat:
          return (this.Helper(operand == IrType.F32 ? "$fmtNet32" : "$fmtNet", arguments[0]), true);
        case IrIntrinsicOp.FormatEcma or IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatBoolEcma:
          this.Native("String");
          return (Invoke("String", this.Text(arguments[0])), true);
        case IrIntrinsicOp.FormatBoolDotNet:
          return (new JsConditional(this.Operand(arguments[0]), new JsString("True"), new JsString("False")), false);
        case IrIntrinsicOp.FormatRadix:
          return (this.Radix(intrinsic), true);
        case IrIntrinsicOp.FixedEcma:
          this.Native(".toFixed");
          return (new JsCall(new JsMember(this.Receiver(arguments[0]), "toFixed"), [this.Text(arguments[1])]), true);
        case IrIntrinsicOp.FixedDotNet:
          return (this.Helper("$fixedNet", arguments[0], arguments[1]), true);
        case IrIntrinsicOp.ParseNumberEcma:
          this.Native("Number");
          return (Invoke("Number", this.Text(arguments[0])), true);
        case IrIntrinsicOp.ParseFloatEcma:
          this.Native("parseFloat");
          return (Invoke("parseFloat", this.Text(arguments[0])), true);
        case IrIntrinsicOp.ParseIntEcma:
          this.Native("parseInt");
          return (new JsCall(Id("parseInt"), [.. arguments.Select(this.Text)]), true);
        default:
          return (this.Parsed(intrinsic), true);
      }
    }

    /// <summary>
    /// An operand a method is called on: in parentheses unless it is one already, and a number
    /// literal too, whose point the dot of the method would continue.
    /// </summary>
    private JsExpression Receiver(IrExpr value) {
      var written = this.Operand(value);
      return written is JsNumber ? Parenthesized(written) : written;
    }

    /// <summary>
    /// The digits of a radix: <c>toString(radix)</c> of the number, of its bits read as unsigned for
    /// two's complement (<c>&gt;&gt;&gt; 0</c>, a mask, <c>BigInt.asUintN</c>), and the helper where
    /// the digits are padded or upper case or the number is the halves of 64 bits.
    /// </summary>
    private JsExpression Radix(IrIntrinsic intrinsic) {
      var (radix, padding, upper, mode) = NumberTextOperations.RadixOf(intrinsic)!.Value;
      var value = intrinsic.Arguments[0];
      var type = value.Type;
      var plain = padding <= 1 && !upper;
      if (type.IsLong && this.Halves)
        return this.Helper("$radix64", [this.Text(value), Number(radix), Number(padding), new JsBoolean(upper), new JsBoolean(mode == RadixMode.SignMagnitude && type.IsSigned)]);

      JsExpression bits;
      bool atomic;
      if (mode == RadixMode.SignMagnitude) {
        (bits, atomic) = (this.Text(value), true);
        if (plain && this.Native(".toString"))
          return new JsCall(new JsMember(this.Receiver(value), "toString"), [Number(radix)]);
      } else if (type.IsLong) {
        this.Native("BigInt.asUintN");
        (bits, atomic) = (Invoke("BigInt.asUintN", Number("64"), this.Text(value)), true);
      } else if (type.Bits == 32) {
        (bits, atomic) = (Infix(this.Operand(value), ">>>", Number("0")), false);
      } else {
        (bits, atomic) = (Infix(this.Operand(value), "&", Number(type.Bits == 8 ? "0xFF" : "0xFFFF")), false);
      }

      if (!plain)
        return this.Helper("$radix", [bits, Number(radix), Number(padding), new JsBoolean(upper)]);

      this.Native(".toString");
      return new JsCall(new JsMember(atomic ? bits : Parenthesized(bits), "toString"), [Number(radix)]);
    }

    /// <summary>
    /// <c>Parse</c> of .NET: a double or a float by the grammar of <c>NumberStyles.Float |
    /// NumberStyles.AllowThousands</c>, an integer by that of <c>NumberStyles.Integer</c> with its bounds.
    /// </summary>
    private JsExpression Parsed(IrIntrinsic intrinsic) {
      var text = this.Text(intrinsic.Arguments[0]);
      var type = intrinsic.Type;
      if (type.IsFloat)
        return this.Helper("$parseNet", [text, new JsBoolean(type == IrType.F32)]);

      if (type.IsLong)
        return this.Helper("$parseNet64", [text, new JsBoolean(type.IsSigned)]);

      return this.Helper("$parseNetInt", [text, Number(IrOperators.Minimum(type)), Number(IrOperators.Maximum(type))]);
    }

  }

}
