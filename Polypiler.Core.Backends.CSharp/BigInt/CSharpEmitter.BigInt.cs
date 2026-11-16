using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>The C# type of the arbitrary-precision integer, which .NET Framework 4.0 brought.</summary>
  private const string BigIntegerType = "global::System.Numerics.BigInteger";

  /// <summary>
  /// An arbitrary-precision integer in a profile whose framework has no <c>BigInteger</c>, .NET
  /// Framework 2.0 and 3.5 as the table says: one <see cref="Inexpressible"/> diagnostic that names it.
  /// </summary>
  private static IEnumerable<Diagnostic> BigIntProblems(IrModule module, CSharpProfile profile, CSharpCapabilities table) {
    if (table.HasApi(CSharpCapabilities.BigIntegerApi, profile) || !UsesBigInt(module))
      return [];

    return [InexpressibleDescriptor.At($"'System.Numerics.BigInteger' (the arbitrary-precision integer, a Python int no fixed width holds) cannot be expressed in C# profile '{profile.Name}'")];
  }

  /// <summary>Whether a module has a value of the arbitrary-precision integer anywhere: a variable, a result or an expression.</summary>
  private static bool UsesBigInt(IrModule module)
    => module.Functions.Any(function => function.ReturnType.IsBigInt || function.Parameters.Concat(function.Locals).Any(local => local.Type.IsBigInt)
      || Expressions(function.Body).Any(expression => expression.Type.IsBigInt || expression is IrConvert { Operand.Type.IsBigInt: true }));

  private sealed partial class Writer {

    /// <summary>
    /// A constant of the arbitrary-precision integer: a cast of a <c>long</c> literal where one holds
    /// it, otherwise the digits parsed in the invariant culture.
    /// </summary>
    private static (CsExpression Node, int Binding) BigLiteral(IrBigConst constant) {
      var value = constant.Value;
      if (value >= long.MinValue && value <= long.MaxValue) {
        var literal = new CsIntegerLiteral(value, CsIntegerSuffix.L) { Type = IrType.I64 };
        return (new CsCast(new CsType(BigIntegerType), value.Sign < 0 ? new CsParenthesized(literal) : literal) { Type = IrType.BigInt }, Cast);
      }

      var parse = new CsInvocation(CSharpFragments.Expression(BigIntegerType + ".Parse"), [
        new CsStringLiteral(value.ToString(CultureInfo.InvariantCulture)), CSharpFragments.Expression("global::System.Globalization.CultureInfo.InvariantCulture")
      ]) { Type = IrType.BigInt };
      return (parse, Primary);
    }

    /// <summary>
    /// A conversion from or to the arbitrary-precision integer. To a fixed width that wraps it keeps
    /// the low bits through the mask of the unsigned type of the width, which the explicit conversion
    /// then takes without overflow, and an unchecked cast gives the signed type; one that traps is the
    /// explicit conversion, which throws <c>OverflowException</c> outside the type in every context.
    /// To <c>double</c> it is the helper that rounds; from a number it is the explicit conversion, which
    /// truncates a floating-point number and throws for a NaN and an infinity.
    /// </summary>
    private (CsExpression Node, int Binding) BigConvert(IrConvert conversion) {
      var to = conversion.Type;
      if (to.IsBigInt)
        return (new CsCast(new CsType(BigIntegerType), this.Operand(conversion.Operand, Cast)) { Type = to }, Cast);

      if (to == IrType.F64)
        return (new CsInvocation(CSharpFragments.Name(this.Helper(CSharpBigIntegerHelpers.BigToDouble)), [this.Text(conversion.Operand)]) { Type = to }, Primary);

      var keyword = TypeNameOf(to);
      if (conversion.Contract == IrConversion.Trap)
        return (new CsCast(new CsType(keyword), this.Operand(conversion.Operand, Cast)) { Type = to }, Cast);

      var unsigned = to.Kind switch {
        IrTypeKind.I8 or IrTypeKind.U8 => "byte",
        IrTypeKind.I16 or IrTypeKind.U16 => "ushort",
        IrTypeKind.I32 or IrTypeKind.U32 => "uint",
        _ => "ulong"
      };
      var masked = new CsParenthesized(new CsBinary(this.Operand(conversion.Operand, Unary), "&", new CsMemberAccess(new CsName(unsigned), "MaxValue")));
      CsExpression low = new CsCast(new CsType(unsigned), masked);
      return (to.IsSigned ? new CsCast(new CsType(keyword), low) { Type = to } : low with { Type = to }, Cast);
    }

  }

  /// <summary>The keyword of a fixed-width integer type.</summary>
  private static string TypeNameOf(IrType type) => _types[type.Kind];

}
