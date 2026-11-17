using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>How an integer is read as a truth value (#112): <c>[nonzero]</c> or <c>[eq k]</c>.</summary>
public enum IrTruthRule {

  /// <summary>True for every non-zero bit pattern, as ISO C §6.3.1.2 converts to <c>_Bool</c>.</summary>
  NonZero,

  /// <summary>True only for exactly one value.</summary>
  Equal

}

/// <summary>The test an <c>IntToBool</c> applies: <c>[nonzero]</c>, or <c>[eq k]</c> for one value <c>k</c>.</summary>
public sealed record IrTruthTest {

  private IrTruthTest(IrTruthRule rule, long value) {
    this.Rule = rule;
    this.Value = value;
  }

  /// <summary>The rule.</summary>
  public IrTruthRule Rule { get; }

  /// <summary>The value <see cref="IrTruthRule.Equal"/> is true for; 0 for <see cref="IrTruthRule.NonZero"/>.</summary>
  public long Value { get; }

  /// <summary><c>[nonzero]</c>.</summary>
  public static IrTruthTest NonZero { get; } = new(IrTruthRule.NonZero, 0);

  /// <summary><c>[eq k]</c>: true only for exactly <paramref name="value"/>.</summary>
  /// <param name="value">The value, as the type of the integer carries it.</param>
  /// <returns>The test.</returns>
  public static IrTruthTest EqualTo(long value) => new(IrTruthRule.Equal, value);

  /// <summary>The test as the text form writes it inside the brackets: <c>nonzero</c>, <c>eq -1</c>.</summary>
  public override string ToString() => this.Rule == IrTruthRule.NonZero ? "nonzero" : $"eq {this.Value}";

}

/// <summary>
/// How a boolean is stored (#112): <c>bool</c> has no memory layout of its own, so a stored one is an
/// integer of <paramref name="Storage"/>, written with <c>BoolToInt[true=k]</c> and read with
/// <c>IntToBool</c> under <paramref name="Test"/>.
/// </summary>
/// <param name="Storage">The integer type the boolean is stored as.</param>
/// <param name="TrueValue">What <see langword="true"/> is stored as; <see langword="false"/> is always 0.</param>
/// <param name="Test">How a stored value is read back.</param>
public sealed record IrBoolEncoding(IrType Storage, long TrueValue, IrTruthTest Test) {

  /// <summary>C's <c>int</c> flag: 1 for true, any non-zero value read as true.</summary>
  public static IrBoolEncoding C { get; } = new(IrType.I32, 1, IrTruthTest.NonZero);

  /// <summary>BASIC's <c>TRUE = -1</c> in a 16-bit integer, any non-zero value read as true.</summary>
  public static IrBoolEncoding Basic { get; } = new(IrType.I16, -1, IrTruthTest.NonZero);

  /// <summary>One byte, 1 for true, as .NET and C's <c>_Bool</c> store a boolean.</summary>
  public static IrBoolEncoding Byte { get; } = new(IrType.U8, 1, IrTruthTest.NonZero);

  /// <summary>The integer a boolean is stored as.</summary>
  /// <param name="value">The boolean.</param>
  /// <returns><see cref="TrueValue"/> or 0, of <see cref="Storage"/>.</returns>
  /// <exception cref="ArgumentException">The encoding or the value is not one <see cref="IrBooleans.ToInt"/> takes.</exception>
  public IrExpr Store(IrExpr value) => IrBooleans.ToInt(value, this.Storage, this.TrueValue);

  /// <summary>The boolean a stored integer stands for.</summary>
  /// <param name="stored">The integer, of <see cref="Storage"/>.</param>
  /// <returns>The truth value under <see cref="Test"/>.</returns>
  /// <exception cref="ArgumentException">The integer is not of <see cref="Storage"/>, or the test is not one <see cref="IrBooleans.FromInt"/> takes.</exception>
  public IrExpr Load(IrExpr stored) {
    ArgumentNullException.ThrowIfNull(stored);
    return stored.Type == this.Storage ? IrBooleans.FromInt(stored, this.Test) : throw new ArgumentException($"A boolean stored as {this.Storage} is read from a value of {stored.Type}.", nameof(stored));
  }

}

/// <summary>
/// The stored-boolean encodings of a profile (#112), per field: each field a profile names has its
/// own encoding, every other the default.
/// </summary>
/// <param name="Default">The encoding of a field the profile does not name.</param>
/// <param name="Fields">The encoding of each field the profile names, by the field's name.</param>
public sealed record IrBoolStorage(IrBoolEncoding Default, IReadOnlyDictionary<string, IrBoolEncoding> Fields) {

  /// <summary>The encoding a field is stored with.</summary>
  /// <param name="field">The name of the field.</param>
  /// <returns>Its own encoding, or the default.</returns>
  public IrBoolEncoding Of(string field) {
    ArgumentNullException.ThrowIfNull(field);
    return this.Fields.TryGetValue(field, out var encoding) ? encoding : this.Default;
  }

}

/// <summary>
/// The conversions between the truth values of the IR and integers (#112): <c>BoolToInt[true=k]</c> and
/// <c>IntToBool[nonzero]</c> or <c>[eq k]</c>, built from the conditional and the comparisons every backend
/// writes. <c>bool</c> stays distinct from every integer: a branch on an integer is <c>PLY0100</c>, and an
/// arithmetic operation, a shift or an ordered comparison on <c>bool</c> is <c>PLY0130</c>.
/// </summary>
public static class IrBooleans {

  /// <summary><c>PLY0130</c>: an arithmetic operation, a shift or an ordered comparison on <c>bool</c>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BoolOperand = new("PLY0130", DiagnosticSeverity.Error, ExitCode.InternalError,
    "'{0}' does not accept bool operands; convert with BoolToInt", "ir.bool.operands");

  /// <summary><c>PLY0131</c>: a true encoding that is zero or that the integer type does not hold.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TrueEncoding = new("PLY0131", DiagnosticSeverity.Error, ExitCode.InternalError,
    "true encoding {0} is zero or does not fit {1}", "ir.bool.encoding");

  /// <summary>What is wrong with a true encoding, or <see langword="null"/> for a non-zero value of an integer type.</summary>
  /// <param name="type">The integer type.</param>
  /// <param name="trueValue">The encoding, as the type carries its values.</param>
  /// <returns>The <c>PLY0131</c> diagnostic, or <see langword="null"/>.</returns>
  public static Diagnostic? Problem(IrType type, long trueValue) {
    ArgumentNullException.ThrowIfNull(type);
    return trueValue != 0 && Fits(type, trueValue) ? null : TrueEncoding.At($"true encoding {trueValue} is zero or does not fit {type}");
  }

  /// <summary><c>BoolToInt.&lt;type&gt;[true=k]</c>: exactly <paramref name="trueValue"/> for true and 0 for false, never another value.</summary>
  /// <param name="value">A <c>bool</c>.</param>
  /// <param name="type">The integer type of the result.</param>
  /// <param name="trueValue">What true becomes; 1 when none is given.</param>
  /// <returns>The integer.</returns>
  /// <exception cref="ArgumentException">The value is no <c>bool</c>, or the encoding is zero or does not fit the type (the message of <c>PLY0131</c>).</exception>
  public static IrExpr ToInt(IrExpr value, IrType type, long trueValue = 1) {
    ArgumentNullException.ThrowIfNull(value);
    if (value.Type != IrType.Bool)
      throw new ArgumentException($"BoolToInt converts a bool, not a value of {value.Type}.", nameof(value));

    if (Problem(type, trueValue) is { } problem)
      throw new ArgumentException(problem.Message, nameof(trueValue));

    return new IrConditional(value, new IrConst(type, trueValue), new IrConst(type, 0));
  }

  /// <summary><c>IntToBool.bool[nonzero]</c> or <c>[eq k]</c>: whether an integer is a true value under a test.</summary>
  /// <param name="value">An integer.</param>
  /// <param name="test">The test.</param>
  /// <returns>The <c>bool</c>.</returns>
  /// <exception cref="ArgumentException">The value is no integer, or the value of <c>[eq k]</c> is not one of its type.</exception>
  public static IrExpr FromInt(IrExpr value, IrTruthTest test) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(test);
    var type = value.Type;
    if (!IrIntegerContracts.IsInteger(type))
      throw new ArgumentException($"IntToBool reads an integer, not a value of {type}.", nameof(value));

    if (test.Rule == IrTruthRule.Equal && !Fits(type, test.Value))
      throw new ArgumentException($"{test.Value} is no value of {type}.", nameof(test));

    // The comparisons are defined on integers of 32 and 64 bits; a narrow one widens without a change of value.
    var compared = type.IsWideInteger ? value : new IrConvert(IrType.I32, value);
    var operand = new IrConst(compared.Type, test.Value);
    return new IrBinary(test.Rule == IrTruthRule.NonZero ? IrBinaryOp.NotEqual : IrBinaryOp.Equal, compared, operand);
  }

  /// <summary>Whether a binary operator is one <c>bool</c> does not take: arithmetic, a shift or an ordered comparison.</summary>
  /// <param name="op">The operator.</param>
  /// <returns><see langword="false"/> for the logical operators, the bitwise ones and equality, which <c>bool</c> takes.</returns>
  public static bool RejectsBool(IrBinaryOp op)
    => op is not (IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr or IrBinaryOp.Equal or IrBinaryOp.NotEqual or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor);

  /// <summary>Whether a value is one an integer type carries: within its range, a <c>u64</c> as any 64 bits.</summary>
  private static bool Fits(IrType type, long value) => IrIntegerContracts.IsInteger(type) && IrOperators.Wrap(type, value) == value;

}
