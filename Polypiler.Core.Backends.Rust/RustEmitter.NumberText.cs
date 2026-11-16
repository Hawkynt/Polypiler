using System.Globalization;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// A conversion between a number and text of the IR: an integer's decimal digits as Rust's
    /// <c>to_string</c> writes them, which .NET and ECMA-262 agree with; a truth value as .NET or
    /// ECMA-262 spells it; a floating-point number by the shortest digits Rust's <c>{:e}</c> gives, laid
    /// out as .NET or ECMA-262 does (<c>ply_layout</c>); a fixed notation by <c>{:.n}</c>, which rounds
    /// the exact value with a tie to the even digit as .NET's <c>F</c> does; the digits of a radix by
    /// <c>{:x}</c>, <c>{:o}</c> and <c>{:b}</c>, which write a negative number's bits; a parse by the
    /// helpers of .NET's syntax. Every other one is refused by name.
    /// </summary>
    private RsExpression NumberText(IrIntrinsic operation) {
      var arguments = operation.Arguments;
      RsExpression Call(string helper, params System.Collections.Generic.IReadOnlyList<RsExpression> values) => new RsCall(new RsPath(this.StringHelper(helper)), values) { Type = operation.Type };
      RsExpression Text(RsExpression text) => Call("ply_utf16", new RsReference(false, text));
      RsExpression Format(string pattern, params System.Collections.Generic.IReadOnlyList<RsExpression> values) => new RsMacro("format", [new RsString(pattern), .. values]) { Type = IrType.String };
      var value = arguments.Count > 0 ? arguments[0] : null;
      switch (operation.Op) {
        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma when value!.Type.IsInteger:
          return Text(new RsMethodCall(this.Receiver(this.Expression(value)), "to_string", []));
        case IrIntrinsicOp.FormatDotNet when value!.Type == IrType.F32:
          return Call("ply_format_f32", this.Expression(value));
        // A single is formatted by .NET's rules above; the verifier gives FormatEcma doubles only.
        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma when value!.Type.IsFloat:
          return Call("ply_format_f64", this.Expression(value),
            new RsLiteral(operation.Op == IrIntrinsicOp.FormatEcma ? "true" : "false"));
        case IrIntrinsicOp.FormatBoolDotNet or IrIntrinsicOp.FormatBoolEcma: {
          var dotNet = operation.Op == IrIntrinsicOp.FormatBoolDotNet;
          return Call("ply_utf16", new RsIf(this.Expression(value!), new RsBlock([], new RsString(dotNet ? "True" : "true")),
            new RsBlockExpression(new RsBlock([], new RsString(dotNet ? "False" : "false")))));
        }
        case IrIntrinsicOp.FixedDotNet when NumberTextOperations.DigitsOf(operation) is { } digits:
          return Call("ply_fixed", this.Expression(value!), new RsLiteral(digits.ToString(CultureInfo.InvariantCulture)));
        case IrIntrinsicOp.FormatRadix when NumberTextOperations.RadixOf(operation) is { } radix: {
          var minimum = radix.MinDigits.ToString(CultureInfo.InvariantCulture);
          if (radix.Mode == RadixMode.SignMagnitude && radix.Radix == 10)
            return Call("ply_signed_digits", new RsCall(new RsPath("i128::from"), [this.Expression(value!)]), new RsLiteral(minimum));
          if (radix.Mode == RadixMode.TwoComplement) {
            var kind = radix.Radix switch { 2 => "b", 8 => "o", _ => radix.Upper ? "X" : "x" };
            return Text(Format(radix.MinDigits > 1 ? $"{{:0{minimum}{kind}}}" : $"{{:{kind}}}", this.Expression(value!)));
          }

          break;
        }
        case IrIntrinsicOp.ParseDotNet when operation.Type.IsInteger: {
          var parsed = Call("ply_parse_integer", this.StringArgument(value!), new RsLiteral(IrOperators.Minimum(operation.Type).ToString(CultureInfo.InvariantCulture)),
            new RsLiteral(IrOperators.Maximum(operation.Type).ToString(CultureInfo.InvariantCulture)));
          return this.Cast(parsed with { Type = IrType.I64 }, operation.Type);
        }
        case IrIntrinsicOp.ParseDotNet:
          return Call(operation.Type == IrType.F32 ? "ply_parse_f32" : "ply_parse_f64", this.StringArgument(value!));
      }

      return this.Refuse(operation.Op.ToString());
    }

  }

}
