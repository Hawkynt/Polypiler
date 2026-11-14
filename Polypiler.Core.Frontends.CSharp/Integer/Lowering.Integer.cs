using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

internal sealed partial class Lowering {

  /// <summary>
  /// A call of a member <see cref="CSharpIntegerApi"/> lists, as the operation of the IR it means;
  /// <see langword="null"/> for a member it does not list, an overload whose types the operation is
  /// not defined on, and a form that only a statement can have, so that the call is refused by name.
  /// </summary>
  private IrExpr? Integer(IMethodSymbol method, List<ExpressionSyntax> operands, IrType type) {
    if (CSharpIntegerApi.Find(method.ContainingType.ToDisplayString(), method.Name) is not { } member)
      return null;

    switch (member.Ir) {
      case "LoadLittle" or "LoadBig":
        var (array, index) = this.Bytes(method, operands[0], operands.Count == 2 ? operands[1] : null, type);
        return new IrIntrinsic(Enum.Parse<IrIntrinsicOp>(member.Ir), type, [array, index]);
      case "StoreLittle" or "StoreBig":
        var value = this.Value(operands[1]);
        var (target, at) = this.Bytes(method, operands[0], null, value.Type);
        if (!Pure(value))
          throw new UnsupportedException(operands[1], $"{method.ContainingType.Name}.{method.Name} with a value that can trap or has an effect, which .NET evaluates after it checks the span");

        return new IrIntrinsic(Enum.Parse<IrIntrinsicOp>(member.Ir), IrType.Void, [target, at, value]);
      case "Convert" or "Clamp, Convert | Convert":
        return Created(method.Name, this.Value(operands[0]), type);
      case "Divide, Remainder":
        return null;
    }

    // The operation is the first construct the table names; a second form is one only a statement has.
    var op = Enum.Parse<IrIntrinsicOp>(member.Ir.Split(' ')[0]);
    var arguments = operands.Select(this.Value).ToList();
    // The Log2 of a signed type throws for a negative value; through the unsigned type of its width, that value traps.
    if (op == IrIntrinsicOp.Log2 && arguments[0].Type.IsSigned)
      arguments[0] = new IrConvert(Unsigned(arguments[0].Type), arguments[0], IrConversion.Trap);

    var types = arguments.Select(argument => argument.Type).ToList();
    var signature = IntegerOperations.Signatures(op).Where(candidate => candidate.Arguments.SequenceEqual(types)).Select(candidate => candidate.Result).FirstOrDefault();
    return signature is null ? null : Convert(new IrIntrinsic(op, signature, arguments), type);
  }

  /// <summary>The unsigned type of the width of an integer type.</summary>
  private static IrType Unsigned(IrType type) => type.Bits switch {
    8 => IrType.U8,
    16 => IrType.U16,
    32 => IrType.U32,
    _ => IrType.U64
  };

  /// <summary>
  /// <c>CreateSaturating</c>, <c>CreateTruncating</c> and <c>CreateChecked</c> of an integer type. From
  /// an integer the saturating one clamps the value to the part of the target's range the source
  /// type holds and converts it then; from a floating-point number it is the saturating conversion,
  /// the checked one the trapping conversion. The truncating one takes integers only.
  /// </summary>
  private static IrExpr? Created(string name, IrExpr value, IrType type) {
    var from = value.Type;
    if (from.IsFloat)
      return name switch {
        "CreateSaturating" => new IrConvert(type, value, IrConversion.Saturate),
        "CreateChecked" => new IrConvert(type, value, IrConversion.Trap),
        _ => null
      };

    if (name != "CreateSaturating" || IrOperators.Contains(type, from))
      return Convert(value, type, name == "CreateChecked");

    var low = Int128.Max(IrOperators.Minimum(type), IrOperators.Minimum(from));
    var high = Int128.Min(IrOperators.Maximum(type), IrOperators.Maximum(from));
    return Convert(new IrIntrinsic(IrIntrinsicOp.Clamp, from, [value, new IrConst(from, (long)low), new IrConst(from, (long)high)]), type);
  }

  /// <summary>
  /// The array and the index of the bytes an endian access reads or writes: a <c>byte[]</c> from index
  /// 0, <c>array.AsSpan()</c>, <c>array.AsSpan(index)</c>, or <c>array.AsSpan(index, length)</c> with
  /// the size of the value as its constant length - the forms in which the span has the bounds the
  /// IR checks - or, for <c>BitConverter</c>, the array and its start index.
  /// </summary>
  private (IrExpr Array, IrExpr Index) Bytes(IMethodSymbol method, ExpressionSyntax bytes, ExpressionSyntax? start, IrType value) {
    var name = $"{method.ContainingType.Name}.{method.Name}";
    if (start is not null)
      return (this.ByteArray(bytes, name), this.Index(start));

    if (model.GetTypeInfo(bytes).Type is IArrayTypeSymbol)
      return (this.ByteArray(bytes, name), new IrConst(IrType.I32, 0));

    // AsSpan written as an extension method has the array in front of it, written as a static method as its first argument.
    if (bytes is InvocationExpressionSyntax slice && model.GetSymbolInfo(slice).Symbol is IMethodSymbol { Name: "AsSpan" } asSpan
        && asSpan.ContainingType.ToDisplayString() == "System.MemoryExtensions") {
      var arguments = slice.ArgumentList.Arguments.Select(argument => argument.Expression).ToList();
      if (asSpan.ReducedFrom is not null)
        arguments.Insert(0, ((MemberAccessExpressionSyntax)slice.Expression).Expression);

      if (model.GetTypeInfo(arguments[0]).Type is IArrayTypeSymbol && arguments.Count <= 3
          && (arguments.Count < 3 || model.GetConstantValue(arguments[2]) is { HasValue: true, Value: int length } && length == value.Bits / 8))
        return (this.ByteArray(arguments[0], name), arguments.Count == 1 ? new IrConst(IrType.I32, 0) : this.Index(arguments[1]));
    }

    throw new UnsupportedException(bytes, $"the bytes of {name} as anything but an array or its AsSpan with an index and the size of the value");
  }

  /// <summary>An array of bytes an endian access works on, which a table of the module is not.</summary>
  private IrExpr ByteArray(ExpressionSyntax expression, string name) {
    var array = this.Read(expression);
    return array is IrConstRef or { Type.Element: not { Kind: IrTypeKind.U8 } }
      ? throw new UnsupportedException(expression, $"{name} on {(array is IrConstRef table ? $"the static readonly array '{table.Constant.Name}'" : array.Type.ToString())}")
      : array;
  }

  /// <summary>Whether an expression can neither trap nor have an effect, so that when it is evaluated does not matter.</summary>
  private static bool Pure(IrExpr expression) => expression switch {
    IrConst or IrLocalRef or IrConstRef => true,
    IrUnary unary => unary.Op != IrUnaryOp.NegateChecked && Pure(unary.Operand),
    IrBinary binary => !IrOperators.Traps(binary.Op) && !(binary.Op is IrBinaryOp.Divide or IrBinaryOp.Remainder && binary.Type.IsInteger) && Pure(binary.Left) && Pure(binary.Right),
    IrConvert conversion => conversion.Contract != IrConversion.Trap && Pure(conversion.Operand),
    IrConditional choice => Pure(choice.Condition) && Pure(choice.WhenTrue) && Pure(choice.WhenFalse),
    _ => false
  };

}
