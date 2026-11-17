using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>
/// The name of an intrinsic: PascalCase and unique, the <c>name</c> of its contract and the name of the
/// <see cref="IrIntrinsicOp"/> member an <see cref="IrIntrinsic"/> node of it carries.
/// </summary>
/// <param name="Name">The name, for example <c>PopCount</c>.</param>
public readonly record struct IntrinsicId(string Name) {

  /// <summary>The name of the intrinsic an operation of the IR is.</summary>
  /// <param name="op">The operation.</param>
  /// <returns>Its name.</returns>
  public static IntrinsicId Of(IrIntrinsicOp op) => new(op.ToString());

  /// <inheritdoc />
  public override string ToString() => this.Name;

}

/// <summary>The kinds of type a type parameter of a contract admits.</summary>
public enum IntrinsicConstraint {

  /// <summary>Every integer type.</summary>
  Integer,

  /// <summary>The unsigned integer types.</summary>
  Unsigned,

  /// <summary>The signed integer types.</summary>
  Signed,

  /// <summary>The floating-point types.</summary>
  Float,

  /// <summary>Every scalar type: the integers, the floating-point types and <c>bool</c>.</summary>
  Any

}

/// <summary>A type parameter of an intrinsic.</summary>
/// <param name="Name">Its name, for example <c>T</c>.</param>
/// <param name="Constraint">The types it admits.</param>
public sealed record IntrinsicTypeParameter(string Name, IntrinsicConstraint Constraint) {

  /// <summary>Whether the type parameter may stand for a type.</summary>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> when the constraint admits the type.</returns>
  public bool Admits(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return this.Constraint switch {
      IntrinsicConstraint.Integer => type.IsInteger,
      IntrinsicConstraint.Unsigned => type.IsInteger && !type.IsSigned,
      IntrinsicConstraint.Signed => type.IsSigned,
      IntrinsicConstraint.Float => type.IsFloat,
      _ => type.IsNumeric || type == IrType.Bool
    };
  }

}

/// <summary>
/// A parameter or a result of an intrinsic: its name and its type, a type parameter or the ID of a
/// <c>TYPES.</c> document, an array of it when <see cref="IsArray"/>.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Type">A type parameter, such as <c>T</c>, or a type ID, such as <c>TYPES.i32</c>.</param>
/// <param name="IsArray">Whether the value is an array of the type.</param>
public sealed record IntrinsicValue(string Name, string Type, bool IsArray);

/// <summary>A compile-time immediate of an intrinsic: an operand after the parameters that is a constant within a range.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The ID of its type, such as <c>TYPES.i32</c>.</param>
/// <param name="Low">The least value admitted.</param>
/// <param name="High">The greatest value admitted.</param>
public sealed record IntrinsicAttribute(string Name, string Type, long Low, long High);

/// <summary>Whether an intrinsic has one result on every target, or one within a tolerance policy.</summary>
public enum IntrinsicExactness {

  /// <summary>One result, the same bits on every target.</summary>
  Exact,

  /// <summary>A result within the tolerance a <c>VERIFICATION.</c> document states.</summary>
  Tolerance

}

/// <summary>The algebraic laws an intrinsic obeys, which passes may use.</summary>
public enum IntrinsicAlgebra {

  /// <summary><c>f(a, b) = f(b, a)</c>.</summary>
  Commutative,

  /// <summary><c>f(f(a, b), c) = f(a, f(b, c))</c>.</summary>
  Associative,

  /// <summary><c>f(f(a)) = f(a)</c>, or <c>f(a, a) = a</c> for two operands.</summary>
  Idempotent,

  /// <summary><c>f(f(a)) = a</c>.</summary>
  Involution,

  /// <summary>An operand value leaves the other unchanged.</summary>
  IdentityElement,

  /// <summary>An operand value fixes the result whatever the other is.</summary>
  AbsorbingElement

}

/// <summary>Facts about the result an intrinsic gives on any operands; every member is optional.</summary>
/// <param name="ResultRange">The least and greatest result, inclusive.</param>
/// <param name="KnownZeroBits">The bits of the result that are always 0.</param>
/// <param name="KnownOneBits">The bits of the result that are always 1.</param>
public sealed record IntrinsicFacts((long Low, long High)? ResultRange, ulong? KnownZeroBits, ulong? KnownOneBits);

/// <summary>
/// A reference vector of a contract: the type each type parameter stands for, one literal per parameter
/// and one per result, as the document writes them - a hexadecimal string is a bit pattern, a number is
/// a value, a list is an array.
/// </summary>
/// <param name="Types">The ID of the type each type parameter stands for.</param>
/// <param name="Inputs">One literal per parameter.</param>
/// <param name="Outputs">One literal per result.</param>
public sealed record IntrinsicVector(IReadOnlyDictionary<string, string> Types, IReadOnlyList<JsonNode?> Inputs, IReadOnlyList<JsonNode?> Outputs);

/// <summary>
/// The contract of one intrinsic, the in-memory form of a document <c>spec/intrinsics/&lt;group&gt;/&lt;name&gt;.yaml</c>
/// (<see cref="ContractLoader"/>): its signature, attributes, effects, exactness, algebra, the operations
/// its decomposition may use, facts, and the reference algorithm with its vectors.
/// </summary>
public sealed record IntrinsicContract {

  /// <summary>The name.</summary>
  public required IntrinsicId Id { get; init; }

  /// <summary>The IR spelling when it differs from the name, dotted PascalCase such as <c>Memory.Copy</c>; otherwise the name.</summary>
  public required string Spelling { get; init; }

  /// <summary>The group, such as <c>BitCount</c>.</summary>
  public required string Group { get; init; }

  /// <summary>The type parameters.</summary>
  public IReadOnlyList<IntrinsicTypeParameter> TypeParameters { get; init; } = [];

  /// <summary>The parameters, in operand order.</summary>
  public IReadOnlyList<IntrinsicValue> Parameters { get; init; } = [];

  /// <summary>The results, one to four; several make one tuple value whose projections are <see cref="ResultNames"/>.</summary>
  public required IReadOnlyList<IntrinsicValue> Returns { get; init; }

  /// <summary>The compile-time immediates, operands after the parameters.</summary>
  public IReadOnlyList<IntrinsicAttribute> Attributes { get; init; } = [];

  /// <summary>The IDs of the conditions under which the intrinsic has its result.</summary>
  public IReadOnlyList<string> Legality { get; init; } = [];

  /// <summary><c>pure</c>, or the <c>EFFECTS.</c> IDs of what it does.</summary>
  public required IReadOnlyList<string> Effects { get; init; }

  /// <summary>Whether the result is exact.</summary>
  public required IntrinsicExactness Exactness { get; init; }

  /// <summary>The tolerance document of an inexact intrinsic; otherwise <see langword="null"/>.</summary>
  public string? TolerancePolicy { get; init; }

  /// <summary>The algebraic laws.</summary>
  public IReadOnlyList<IntrinsicAlgebra> Algebra { get; init; } = [];

  /// <summary>The <c>INTRINSICS.</c> and <c>IR.</c> documents a decomposition may use, in lowering order.</summary>
  public IReadOnlyList<string> LowersTo { get; init; } = [];

  /// <summary>Facts about the result; <see langword="null"/> when the document states none.</summary>
  public IntrinsicFacts? Facts { get; init; }

  /// <summary>Whether the operation applies lane by lane to vectors.</summary>
  public bool LaneLiftable { get; init; }

  /// <summary>Whether no decomposition exists and a target has to provide the operation from its runtime.</summary>
  public bool RequiresRuntime { get; init; }

  /// <summary>The defining algorithm, in words or pseudo-code.</summary>
  public required string Algorithm { get; init; }

  /// <summary>The citation of the algorithm.</summary>
  public required string Source { get; init; }

  /// <summary>The reference vectors.</summary>
  public IReadOnlyList<IntrinsicVector> Vectors { get; init; } = [];

  /// <summary>
  /// The names of the projections of the tuple value an intrinsic with several results gives, in the
  /// order of <see cref="Returns"/>; the one name of the result otherwise.
  /// </summary>
  public IReadOnlyList<string> ResultNames => [.. this.Returns.Select(value => char.ToUpperInvariant(value.Name[0]) + value.Name[1..])];

  /// <summary>
  /// Checks a node against the contract: one operand per parameter and per attribute, each of a type
  /// the parameter admits - a type parameter standing for the same type wherever it appears -, an
  /// attribute a constant within its range, and a node of the type of the one result.
  /// </summary>
  /// <param name="node">The node.</param>
  /// <returns><c>PLY0252</c> or <c>PLY0255</c>, or <see langword="null"/> when the node keeps the contract.</returns>
  public Diagnostic? Check(IrIntrinsic node) {
    ArgumentNullException.ThrowIfNull(node);
    var bound = new Dictionary<string, IrType>(StringComparer.Ordinal);
    var arity = this.Parameters.Count + this.Attributes.Count;
    for (var index = 0; index < Math.Max(arity, node.Arguments.Count); ++index) {
      if (index >= node.Arguments.Count)
        return IntrinsicDiagnostics.NotAdmitted.At(IntrinsicDiagnostics.NotAdmittedText(this.Id, "no value", $"operand {index}"));
      var argument = node.Arguments[index];
      if (index >= arity || (index < this.Parameters.Count && !this.Binds(this.Parameters[index], argument.Type, bound)))
        return IntrinsicDiagnostics.NotAdmitted.At(IntrinsicDiagnostics.NotAdmittedText(this.Id, argument.Type.ToString(), $"operand {index}"));
      if (index >= this.Parameters.Count && this.Attribute(this.Attributes[index - this.Parameters.Count], argument) is { } outside)
        return outside;
    }

    // A node of no value that writes an array in place: the result its contract names is that array.
    if (node.Type == IrType.Void && this.Returns is [{ IsArray: true } written] && this.Parameters.Any(parameter => parameter == written with { Name = parameter.Name }))
      return null;

    return this.Returns.Count == 1 && !this.Binds(this.Returns[0], node.Type, bound)
      ? IntrinsicDiagnostics.NotAdmitted.At(IntrinsicDiagnostics.NotAdmittedText(this.Id, node.Type.ToString(), "result"))
      : null;
  }

  /// <summary>Whether a value of the contract may have a type, binding its type parameter on first use.</summary>
  private bool Binds(IntrinsicValue value, IrType type, Dictionary<string, IrType> bound) {
    if (value.IsArray) {
      if (!type.IsArray)
        return false;
      type = type.Element!;
    }

    if (ContractTypes.Scalar(value.Type) is { } fixedType)
      return type == fixedType;
    if (bound.TryGetValue(value.Type, out var earlier))
      return type == earlier;

    var parameter = this.TypeParameters.First(candidate => candidate.Name == value.Type);
    if (!parameter.Admits(type))
      return false;

    bound[value.Type] = type;
    return true;
  }

  /// <summary><c>PLY0252</c> for an attribute operand of another type or not constant, <c>PLY0255</c> for one outside its range.</summary>
  private Diagnostic? Attribute(IntrinsicAttribute attribute, IrExpr argument) {
    if (argument is not IrConst constant || constant.Type != ContractTypes.Scalar(attribute.Type))
      return IntrinsicDiagnostics.NotAdmitted.At(IntrinsicDiagnostics.NotAdmittedText(this.Id, argument is IrConst ? argument.Type.ToString() : $"a non-constant {argument.Type}", $"attribute '{attribute.Name}'"));

    return constant.Value < attribute.Low || constant.Value > attribute.High
      ? IntrinsicDiagnostics.OutsideRange.At($"intrinsic '{this.Id}': attribute '{attribute.Name}' = {constant.Value} is outside [{attribute.Low}, {attribute.High}]")
      : null;
  }

}

/// <summary>The scalar types a contract names by the ID of their <c>TYPES.</c> document.</summary>
public static class ContractTypes {

  private static readonly Dictionary<string, IrType> _byId = new[] {
    IrType.Bool, IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64, IrType.String
  }.ToDictionary(type => "TYPES." + type, StringComparer.Ordinal);

  /// <summary>The type a <c>TYPES.</c> ID names, or <see langword="null"/> for any other text.</summary>
  /// <param name="id">The ID, such as <c>TYPES.u32</c>.</param>
  /// <returns>The type, or <see langword="null"/>.</returns>
  public static IrType? Scalar(string id) => _byId.GetValueOrDefault(id);

}
