using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Proofs.Obligations;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// An obligation as an SMT-LIB 2.6 query in the theory <c>QF_BV</c> (fixed-size bitvectors): each input
/// a constant, each instruction a definition, and one assertion that is satisfiable exactly where the
/// rewrite is wrong -
/// <c>preconditions ∧ valid(before) ∧ (¬valid(after) ∨ before ≠ after)</c>. A side is valid where no
/// strict shift reaches its width, no division has a zero divisor and no signed division that traps
/// on overflow divides the smallest value by -1; the definition of the operations is that of the
/// SMT-LIB theory <c>FixedSizeBitVectors</c>.
/// </summary>
public static class SmtEncoder {

  /// <summary>The query of an obligation at a width.</summary>
  /// <param name="obligation">The obligation.</param>
  /// <param name="width">What <c>&lt;w&gt;</c> stands for; an obligation without it is encoded as written.</param>
  /// <returns>The SMT-LIB script, ending in <c>(check-sat)</c>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="obligation"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not between 1 and 64.</exception>
  /// <exception cref="NotSupportedException">The obligation uses an operation, a type or a constant the encoding does not model at this width; the message names it.</exception>
  public static string ToSmtLib(ProofObligation obligation, int width) {
    ArgumentNullException.ThrowIfNull(obligation);
    try {
      return Encode(Parse(obligation, width));
    } catch (ObligationUnsupportedException exception) {
      throw new NotSupportedException(exception.Operation, exception);
    }
  }

  /// <summary>Reads an obligation at a width for encoding.</summary>
  internal static ObligationProgram Parse(ProofObligation obligation, int width) {
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 64);
    return ObligationProgram.Parse(obligation, width);
  }

  /// <summary>The query of a program.</summary>
  internal static string Encode(ObligationProgram program) {
    var text = new StringBuilder();
    text.Append("(set-logic QF_BV)\n");
    foreach (var input in program.Inputs)
      text.Append(CultureInfo.InvariantCulture, $"(declare-const {Symbol(input.Name)} {Sort(input.Type)})\n");

    var conditions = new List<string>();
    for (var index = 0; index < program.Preconditions.Count; ++index) {
      var (value, valid) = Fragment(text, program.Preconditions[index], $"pre{index}", program.Inputs);
      conditions.Add(valid);
      conditions.Add(value);
    }

    var (before, beforeValid) = Fragment(text, program.Before, "before", program.Inputs);
    var (after, afterValid) = Fragment(text, program.After, "after", program.Inputs);
    conditions.Add(beforeValid);
    conditions.Add($"(or (not {afterValid}) (not (= {before} {after})))");
    text.Append(CultureInfo.InvariantCulture, $"(assert (and {string.Join(' ', conditions)}))\n");
    text.Append("(check-sat)\n");
    return text.ToString();
  }

  /// <summary>A name as an SMT-LIB symbol: quoted, so that no name of an obligation is a keyword.</summary>
  internal static string Symbol(string name) => $"|{name}|";

  private static string Sort(ObligationType type)
    => type == ObligationType.Bool ? "Bool" : $"(_ BitVec {type.Width.ToString(CultureInfo.InvariantCulture)})";

  private static string Bits(ulong value, int width) => $"(_ bv{value.ToString(CultureInfo.InvariantCulture)} {width.ToString(CultureInfo.InvariantCulture)})";

  /// <summary>Defines the instructions of a fragment; returns its value and the condition that it is valid.</summary>
  private static (string Value, string Valid) Fragment(StringBuilder text, ObligationFragment fragment, string prefix, IReadOnlyList<ObligationVariable> inputs) {
    var names = inputs.ToDictionary(input => input.Name, input => Symbol(input.Name), StringComparer.Ordinal);
    var valid = new List<string>();
    foreach (var instruction in fragment.Instructions) {
      var symbol = Symbol($"{prefix}.{instruction.Target}");
      var term = Term(instruction, instruction.Operands.Select(operand => Operand(operand, names)).ToArray(), valid);
      text.Append(CultureInfo.InvariantCulture, $"(define-fun {symbol} () {Sort(instruction.Result)} {term})\n");
      names[instruction.Target] = symbol;
    }

    return (names[fragment.Result], valid.Count == 0 ? "true" : valid.Count == 1 ? valid[0] : $"(and {string.Join(' ', valid)})");
  }

  private static string Operand(ObligationOperand operand, Dictionary<string, string> names) => operand switch {
    ObligationReference reference => names[reference.Name],
    _ when operand.Type == ObligationType.Bool => ((ObligationConstant)operand).Bits != 0 ? "true" : "false",
    _ => Bits(((ObligationConstant)operand).Bits, operand.Type.Width)
  };

  /// <summary>The term of an instruction; adds the conditions under which it is valid.</summary>
  private static string Term(ObligationInstruction instruction, string[] operands, List<string> valid) {
    var type = instruction.Type;
    var truth = type == ObligationType.Bool;
    var width = type.Width;
    string Apply(string op) => $"({op} {string.Join(' ', operands)})";
    string Extend(string extension) => $"((_ {extension} {(width - instruction.Operands[0].Type.Width).ToString(CultureInfo.InvariantCulture)}) {operands[0]})";
    return instruction.Op switch {
      ObligationOp.Add => Apply("bvadd"),
      ObligationOp.Sub => Apply("bvsub"),
      ObligationOp.Mul => Apply("bvmul"),
      ObligationOp.Neg => Apply("bvneg"),
      ObligationOp.And => Apply(truth ? "and" : "bvand"),
      ObligationOp.Or => Apply(truth ? "or" : "bvor"),
      ObligationOp.Xor => Apply(truth ? "xor" : "bvxor"),
      ObligationOp.Not => Apply(truth ? "not" : "bvnot"),
      ObligationOp.Shl or ObligationOp.LShr or ObligationOp.AShr => Shift(instruction, operands, valid),
      ObligationOp.RotateLeft or ObligationOp.RotateRight => Rotate(instruction, operands),
      ObligationOp.UDiv or ObligationOp.URem or ObligationOp.SDiv or ObligationOp.SRem => Division(instruction, operands, valid),
      ObligationOp.Eq => Apply("="),
      ObligationOp.Ne => $"(not {Apply("=")})",
      ObligationOp.ULt => Apply("bvult"),
      ObligationOp.ULe => Apply("bvule"),
      ObligationOp.UGt => Apply("bvugt"),
      ObligationOp.UGe => Apply("bvuge"),
      ObligationOp.SLt => Apply("bvslt"),
      ObligationOp.SLe => Apply("bvsle"),
      ObligationOp.SGt => Apply("bvsgt"),
      ObligationOp.SGe => Apply("bvsge"),
      ObligationOp.Select => Apply("ite"),
      ObligationOp.ZeroExtend => Extend("zero_extend"),
      ObligationOp.SignExtend => Extend("sign_extend"),
      _ => $"((_ extract {(width - 1).ToString(CultureInfo.InvariantCulture)} 0) {operands[0]})"
    };
  }

  /// <summary>
  /// A shift amount of any width as a bitvector of the shifted value's width, and the conditions that
  /// it is below the width and that it is not: compared in the amount's own width, where the width
  /// may not be representable - then every amount is below it.
  /// </summary>
  private static (string Amount, string Below, string AtLeast) Amount(ObligationInstruction instruction, string amount) {
    var width = instruction.Type.Width;
    var from = instruction.Operands[1].Type.Width;
    var representable = (ulong)width <= instruction.Operands[1].Type.Mask;
    var below = representable ? $"(bvult {amount} {Bits((ulong)width, from)})" : "true";
    var atLeast = representable ? $"(bvuge {amount} {Bits((ulong)width, from)})" : "false";
    var fitted = from == width ? amount
      : from < width ? $"((_ zero_extend {(width - from).ToString(CultureInfo.InvariantCulture)}) {amount})"
      : $"((_ extract {(width - 1).ToString(CultureInfo.InvariantCulture)} 0) {amount})";
    return (fitted, below, atLeast);
  }

  /// <summary>An amount of any width taken modulo the width of the shifted value, as a bitvector of that width.</summary>
  private static string Reduced(ObligationInstruction instruction, string amount) {
    var width = instruction.Type.Width;
    var from = instruction.Operands[1].Type.Width;
    var representable = (ulong)width <= instruction.Operands[1].Type.Mask;
    var reduced = representable ? $"(bvurem {amount} {Bits((ulong)width, from)})" : amount;
    return Amount(instruction, reduced).Amount;
  }

  /// <summary>
  /// A shift by its amount contract. <c>bvshl</c>, <c>bvlshr</c> and <c>bvashr</c> already shift every
  /// bit out at an amount of the width or more; the contracts differ in what they do there: strict
  /// makes it invalid, mask reduces the amount modulo the width, unbounded keeps the shift.
  /// </summary>
  private static string Shift(ObligationInstruction instruction, string[] operands, List<string> valid) {
    var op = instruction.Op switch {
      ObligationOp.Shl => "bvshl",
      ObligationOp.LShr => "bvlshr",
      _ => "bvashr"
    };
    if (instruction.Shift == ShiftContract.Mask)
      return $"({op} {operands[0]} {Reduced(instruction, operands[1])})";

    var (amount, below, atLeast) = Amount(instruction, operands[1]);
    if (instruction.Shift == ShiftContract.Strict) {
      valid.Add(below);
      return $"({op} {operands[0]} {amount})";
    }

    // An amount wider than the value is cut to the value's width, so whether it reaches the width is decided first.
    var outside = instruction.Op == ObligationOp.AShr
      ? $"(bvashr {operands[0]} {Bits((ulong)instruction.Type.Width - 1, instruction.Type.Width)})"
      : Bits(0, instruction.Type.Width);
    return $"(ite {atLeast} {outside} ({op} {operands[0]} {amount}))";
  }

  /// <summary>A rotation: the shifts in both directions by the amount modulo the width, or-ed.</summary>
  private static string Rotate(ObligationInstruction instruction, string[] operands) {
    var width = instruction.Type.Width;
    var amount = Reduced(instruction, operands[1]);
    var (towards, away) = instruction.Op == ObligationOp.RotateLeft ? ("bvshl", "bvlshr") : ("bvlshr", "bvshl");
    return $"(bvor ({towards} {operands[0]} {amount}) ({away} {operands[0]} (bvsub {Bits((ulong)width, width)} {amount})))";
  }

  /// <summary>A division: valid for a non-zero divisor, and for a signed one that traps on overflow not for the smallest value divided by -1.</summary>
  private static string Division(ObligationInstruction instruction, string[] operands, List<string> valid) {
    var width = instruction.Type.Width;
    valid.Add($"(not (= {operands[1]} {Bits(0, width)}))");
    if (instruction.OverflowTraps)
      valid.Add($"(not (and (= {operands[0]} {Bits(1UL << (width - 1), width)}) (= {operands[1]} {Bits(instruction.Type.Mask, width)})))");

    var op = instruction.Op switch {
      ObligationOp.UDiv => "bvudiv",
      ObligationOp.URem => "bvurem",
      ObligationOp.SDiv => "bvsdiv",
      _ => "bvsrem"
    };
    return $"({op} {operands[0]} {operands[1]})";
  }

}
