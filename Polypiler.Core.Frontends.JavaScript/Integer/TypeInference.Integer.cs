using System.Linq;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// The type of a call the integer operations decide: <c>Math.abs</c>, <c>Math.min</c>,
  /// <c>Math.max</c> and <c>Math.sign</c> of proven integers, and a getter of a <c>DataView</c>.
  /// <see langword="null"/> for any other call, whose type is decided as before.
  /// </summary>
  private JsType? IntegerCall(CallExpression call, string? builtin) {
    var arguments = call.Arguments;
    switch (builtin) {
      case "Math.abs" when arguments is [var value] && this.Of(value) is { IsInteger: true, Ir: { } type }:
        // The magnitude of the smallest value of a signed type is one more than its largest.
        return JsType.Of(type.IsSigned ? Unsigned(type) : type);
      case "Math.sign" when arguments is [var value] && this.Of(value).IsInteger:
        return JsType.I32;
      case "Math.min" or "Math.max" when arguments.Count > 0 && arguments.All(argument => this.Of(argument).IsInteger):
        return arguments.Select(argument => this.Of(argument)).Aggregate(JsType.Join);
      case null when call.Callee is MemberExpression { Computed: false, Object: Identifier, Property: Identifier { Name: var method } }
        && method.StartsWith("get", System.StringComparison.Ordinal) && JavaScriptIntegerApi.ViewType(method) is { } read:
        return JsType.Of(read);
      default:
        return null;
    }
  }

  /// <summary>The unsigned type of the width of a signed type of up to 32 bits.</summary>
  internal static IrType Unsigned(IrType type) => type.Bits switch {
    8 => IrType.U8,
    16 => IrType.U16,
    _ => IrType.U32
  };

}
