using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The enums of a file, at the top of the file or nested in the static class, over any integer type,
/// with <c>[Flags]</c> or without, and what <see cref="CSharpEnumApi"/> lists of <c>System.Enum</c>. A
/// value of an enum is the value of its underlying type with the enum's name: arithmetic on it is the
/// underlying type's, done as C# does it - promoted, then converted back to the enum - and only
/// equality, the bitwise operators of 32 and 64 bits and the complement stay operators on the enum.
/// </summary>
internal sealed partial class Lowering {

  private readonly Dictionary<INamedTypeSymbol, IrEnumType> _enums = new(SymbolEqualityComparer.Default);

  /// <summary>Whether a member declares an enum.</summary>
  private static bool IsEnumDeclaration(MemberDeclarationSyntax member) => member is EnumDeclarationSyntax;

  /// <summary>The enums of every file, in file order; each is a type of the module before any struct, whose field may have its type.</summary>
  private void DeclareEnums(IReadOnlyList<(string Path, SemanticModel Model, EnumDeclarationSyntax Type)> enums) {
    foreach (var (file, semantics, declaration) in enums) {
      path = file;
      model = semantics;
      var symbol = semantics.GetDeclaredSymbol(declaration)!;
      try {
        foreach (var modifier in declaration.Modifiers)
          if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword))
            throw new UnsupportedException(declaration, $"a {modifier.Text} enum");

        var flags = false;
        foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
          if (semantics.GetSymbolInfo(attribute).Symbol?.ContainingType.ToDisplayString() == "System.FlagsAttribute")
            flags = true;
          else
            throw new UnsupportedException(attribute, $"the attribute [{attribute.Name}] on an enum");

        if (this._types.Exists(type => type.Name == symbol.Name))
          throw new UnsupportedException(declaration, $"a second type named {symbol.Name}");

        var underlying = this.Map(symbol.EnumUnderlyingType, declaration);
        var members = symbol.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue)
          .Select(field => new IrEnumMember(field.Name, Carried(field.ConstantValue!))).ToList();
        var declared = new IrEnumType(symbol.Name, underlying, members, flags) { IsPublic = IsVisible(symbol) };
        this._enums[symbol] = declared;
        this._types.Add(declared);
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }
    }
  }

  /// <summary>Whether code outside the file can name a type: it and every type around it is public.</summary>
  private static bool IsVisible(INamedTypeSymbol type)
    => type.DeclaredAccessibility == Accessibility.Public && (type.ContainingType is not { } outer || IsVisible(outer));

  /// <summary>The value of an enum as one of its underlying type: a constant stays one.</summary>
  private static IrExpr Plain(IrExpr value)
    => !value.Type.IsEnum ? value
      : value is IrConst constant ? new IrConst(value.Type.Underlying, constant.Value)
      : value is IrConvert { Operand.Type: var inner } conversion && inner == value.Type.Underlying ? conversion.Operand
      : new IrConvert(value.Type.Underlying, value);

  /// <summary>A value of the underlying type of an enum as one of the enum: a constant stays one.</summary>
  private static IrExpr Named(IrExpr value, IrType type)
    => value is IrConst constant ? new IrConst(type, constant.Value)
      : value is IrConvert { Operand.Type: var inner } conversion && inner == type ? conversion.Operand
      : new IrConvert(type, value);

  /// <summary>The type C# computes an operator on an enum in: its underlying type, an <c>int</c> for one narrower than that.</summary>
  private static IrType Promoted(IrType type) => type.Underlying.IsWideInteger ? type.Underlying : IrType.I32;

  /// <summary>
  /// Converts where one side is an enum: through the underlying type, which C# converts from and to
  /// as from and to any other integer type, and on to the enum without a change.
  /// </summary>
  private static IrExpr ConvertEnum(IrExpr value, IrType type, bool trap) {
    var plain = Convert(Plain(value), type.Underlying, trap);
    return type.IsEnum ? Named(plain, type) : plain;
  }

  /// <summary>
  /// A binary operator with an enum among its operands. Two values of one enum are compared for
  /// equality, and of an enum of 32 or 64 bits combined bit by bit, as they are; any other operator -
  /// <c>e + n</c>, <c>e - e</c>, <c>&lt;</c>, the bit operators of a narrow enum - is computed on the
  /// underlying values promoted as C# promotes them and converted to the type of the result.
  /// </summary>
  private static IrExpr EnumBinary(IrBinaryOp op, IrExpr left, IrExpr right, IrType result, SyntaxNode node, bool trap) {
    if (left.Type == right.Type && IrOperators.Accepts(op, left.Type, right.Type))
      return new IrBinary(op, left, right);

    var work = Promoted(left.Type.IsEnum ? left.Type : right.Type);
    var computed = Binary(op, Convert(Plain(left), work, trap), Convert(Plain(right), work, trap), node, trap);
    return computed.Type == IrType.Bool ? computed : Convert(computed, result, trap);
  }

  /// <summary>The complement of an enum: of 32 or 64 bits on the enum itself, of a narrower one on its promoted value.</summary>
  private static IrExpr EnumComplement(IrExpr operand)
    => operand.Type.Underlying.IsWideInteger
      ? new IrUnary(IrUnaryOp.BitNot, operand)
      : Named(Convert(new IrUnary(IrUnaryOp.BitNot, Convert(Plain(operand), IrType.I32)), operand.Type.Underlying), operand.Type);

  /// <summary>The enum of the file a type is, or <see langword="null"/>.</summary>
  private IrEnumType? EnumOf(ITypeSymbol? type) => type is INamedTypeSymbol named && this._enums.TryGetValue(named, out var declared) ? declared : null;

  /// <summary>The enum a <c>typeof(E)</c> names, which has to be one of the file.</summary>
  private IrEnumType TypeArgument(ExpressionSyntax argument, string member)
    => argument is TypeOfExpressionSyntax typeOf && this.EnumOf(model.GetTypeInfo(typeOf.Type).Type) is { } declared
      ? declared
      : throw new UnsupportedException(argument, $"{member} of another type than an enum of the file named by typeof");

  /// <summary>The text of an enum value, which .NET does not define for an enum whose members repeat a value.</summary>
  private static IrExpr EnumText(IrExpr value, SyntaxNode node, string member)
    => value.Type.EnumType!.HasRepeatedValues
      ? throw new UnsupportedException(node, $"{member} of the enum {value.Type}, whose members repeat a value, for which .NET does not say which name it gives")
      : new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [value]);

  /// <summary>The names or the values of an enum, whose order .NET does not define for one whose members repeat a value.</summary>
  private static IrExpr Listing(IrIntrinsicOp op, IrEnumType type, SyntaxNode node, string member)
    => type.HasRepeatedValues
      ? throw new UnsupportedException(node, $"{member} of the enum {type.Name}, whose members repeat a value, which .NET lists in no defined order")
      : EnumOperations.Listing(op, type);

  /// <summary>
  /// A cast of the object <c>Enum.Parse(typeof(E), …)</c> gives to the enum, or of the array
  /// <c>Enum.GetValues(typeof(E))</c> gives to <c>E[]</c>; <see langword="null"/> for any other cast.
  /// </summary>
  private IrExpr? EnumCast(CastExpressionSyntax cast, IrType type) {
    var inner = cast.Expression;
    while (inner is ParenthesizedExpressionSyntax parenthesized)
      inner = parenthesized.Expression;

    if (inner is not InvocationExpressionSyntax invocation || model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { ContainingType.SpecialType: SpecialType.System_Enum, IsGenericMethod: false } method)
      return null;

    var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.Expression).ToList();
    var member = Member(method);
    switch (method.Name) {
      case "Parse" when arguments.Count is 2 or 3 && method.Parameters[1].Type.SpecialType == SpecialType.System_String:
        var parsed = this.TypeArgument(arguments[0], member);
        if (type.EnumType != parsed)
          throw new UnsupportedException(cast, $"{member} cast to {type}, which is not the enum it parses");

        return this.Parse(parsed, arguments[1], arguments.Count == 3 ? arguments[2] : null);
      case "GetValues" when arguments.Count == 1:
        var listed = this.TypeArgument(arguments[0], member);
        return type == IrType.Array(listed.Type)
          ? Listing(IrIntrinsicOp.EnumValues, listed, invocation, member)
          : throw new UnsupportedException(cast, $"{member} cast to {type}, which is not the array of the enum it lists");
      default:
        return null;
    }
  }

  private IrExpr Parse(IrEnumType type, ExpressionSyntax text, ExpressionSyntax? ignoreCase)
    => new IrIntrinsic(IrIntrinsicOp.EnumParse, type.Type, [this.Value(text), ignoreCase is null ? new IrConst(IrType.Bool, 0) : this.Value(ignoreCase)]);

  /// <summary>
  /// A member of <c>System.Enum</c> that <see cref="CSharpEnumApi"/> lists, called on an enum of the
  /// file; <see langword="null"/> for a method of another type. Any other member of <c>System.Enum</c> is refused by name.
  /// </summary>
  private IrExpr? EnumCall(InvocationExpressionSyntax invocation, IMethodSymbol method, List<ExpressionSyntax> operands) {
    if (method.ContainingType.SpecialType != SpecialType.System_Enum)
      return null;

    var member = Member(method);
    var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
    var generic = method.IsGenericMethod ? this.EnumOf(method.TypeArguments[0]) : null;
    switch (method.Name) {
      case "ToString" when !method.IsStatic && operands.Count == 0 && receiver is not null:
        return EnumText(this.Enumerated(receiver, member), invocation, member);
      case "HasFlag" when !method.IsStatic && receiver is not null:
        var value = this.Enumerated(receiver, member);
        var flag = this.Own(operands[0]);
        return flag.Type == value.Type
          ? new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [value, flag])
          : throw new UnsupportedException(operands[0], $"{member} with a flag of the type {flag.Type}, which is not the enum {value.Type}, for which .NET throws");
      case "IsDefined" when generic is not null:
        return new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [Convert(this.Value(operands[0]), generic.Type)]);
      case "IsDefined" when operands.Count == 2:
        var defined = this.TypeArgument(operands[0], member);
        var candidate = this.Own(operands[1]);
        return candidate.Type == defined.Type || candidate.Type == defined.Underlying
          ? new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [Convert(candidate, defined.Type)])
          : throw new UnsupportedException(operands[1], $"{member} with a value of the type {candidate.Type}, which is neither {defined.Name} nor its underlying type {defined.Underlying}, for which .NET throws");
      case "Parse" when generic is not null && method.Parameters[0].Type.SpecialType == SpecialType.System_String:
        return this.Parse(generic, operands[0], operands.Count == 2 ? operands[1] : null);
      case "GetNames" when generic is not null:
        return Listing(IrIntrinsicOp.EnumNames, generic, invocation, member);
      case "GetNames" when operands.Count == 1:
        return Listing(IrIntrinsicOp.EnumNames, this.TypeArgument(operands[0], member), invocation, member);
      case "GetValues" when generic is not null:
        return Listing(IrIntrinsicOp.EnumValues, generic, invocation, member);
      default:
        throw new UnsupportedException(invocation, method.Name switch {
          "ToString" => $"{member} with a format",
          "TryParse" => $"{member}, whose result is an out parameter (#216)",
          _ => member
        });
    }
  }

  /// <summary>The receiver of a method of <c>System.Enum</c> as a value of an enum of the file.</summary>
  private IrExpr Enumerated(ExpressionSyntax receiver, string member) {
    var value = this.Own(receiver);
    return value.Type.IsEnum ? value : throw new UnsupportedException(receiver, $"{member} of the type {value.Type}");
  }

}
