using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Scev;

/// <summary>
/// How an integer value changes from one pass of a loop to the next, read where a pass starts - before
/// the loop's test, with every variable as the previous pass left it (#350). The value is
/// <see cref="Invariant"/>, the same in every pass; an <see cref="AddRec"/> <c>{start, +, step}</c>,
/// <c>start + k·step</c> in pass <c>k</c> counted from 0, modulo 2^w of its type; or
/// <see cref="Unknown"/>. Terminology after Bachmann, Wang and Zima, "Chains of recurrences" (ISSAC 1994).
/// </summary>
public abstract record Evolution {

  private protected Evolution() { }

  /// <summary>Nothing is known of how the value changes.</summary>
  public static Evolution Unknown { get; } = new Opaque();

  /// <summary>
  /// The same value in every pass: a constant, or an expression whose variables the loop does not write,
  /// evaluated where the loop is entered.
  /// </summary>
  /// <param name="Value">The value; trap-free and without effect.</param>
  public sealed record Invariant(IrExpr Value) : Evolution {

    /// <summary>Whether the value is an integer constant, and which, as a number of its type.</summary>
    /// <param name="number">The number, when it is one.</param>
    /// <returns><see langword="true"/> for a constant.</returns>
    public bool IsConstant(out Int128 number) {
      number = this.Value is IrConst { Type.IsInteger: true } constant ? IrOperators.Number(constant.Type, constant.Value) : 0;
      return this.Value is IrConst { Type.IsInteger: true };
    }

    /// <inheritdoc/>
    public override string ToString() => this.IsConstant(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : AnalysisDiagnostics.Describe(this.Value);

  }

  /// <summary>
  /// The add recurrence <c>{Start, +, Step}</c> of a loop: <c>Start + k·Step</c> in pass <c>k</c>, modulo 2^w.
  /// The flags say that the mathematical value <c>Start + k·Step</c> stays within the unsigned, or signed,
  /// range of the type for every pass that starts - including the one whose test ends the loop - so that
  /// no wrap ever happens; they are set only where that is proven, never from the source language.
  /// </summary>
  /// <param name="Start">The value in the first pass: invariant in the loop, or a recurrence of an enclosing loop.</param>
  /// <param name="Step">What each pass adds: invariant in the loop.</param>
  /// <param name="Loop">The loop: an <see cref="IrWhile"/>, <see cref="IrFor"/> or <see cref="IrDoWhile"/>.</param>
  /// <param name="Type">The integer type the recurrence is computed in.</param>
  /// <param name="NoUnsignedWrap">Whether the bits read as unsigned never wrap.</param>
  /// <param name="NoSignedWrap">Whether the bits read as signed never wrap.</param>
  public sealed record AddRec(Evolution Start, Evolution Step, IrStmt Loop, IrType Type, bool NoUnsignedWrap, bool NoSignedWrap) : Evolution {

    /// <summary>Whether two recurrences describe the same values: start, step, type and the very same loop node.</summary>
    /// <param name="other">The other recurrence.</param>
    /// <returns><see langword="true"/> when they are the same.</returns>
    public bool Equals(AddRec? other) => other is not null && Equals(this.Start, other.Start) && Equals(this.Step, other.Step)
      && ReferenceEquals(this.Loop, other.Loop) && this.Type == other.Type && this.NoUnsignedWrap == other.NoUnsignedWrap && this.NoSignedWrap == other.NoSignedWrap;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Start, this.Step, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.Loop), this.Type);

    /// <inheritdoc/>
    public override string ToString() => $"AddRec(start {this.Start}, step {this.Step}){(this.NoUnsignedWrap ? " nuw" : "")}{(this.NoSignedWrap ? " nsw" : "")}";

  }

  /// <summary>The case that claims nothing.</summary>
  private sealed record Opaque : Evolution {

    /// <inheritdoc/>
    public override string ToString() => "Unknown";

  }

}
