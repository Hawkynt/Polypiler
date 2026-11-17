using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The integer operations that state their contract on the node (#118): what happens where a result leaves the type.</summary>
public enum IrIntegerOpcode {

  /// <summary>The sum of two values; takes an overflow contract.</summary>
  Add,

  /// <summary>The difference of two values; takes an overflow contract.</summary>
  Sub,

  /// <summary>The product of two values; takes an overflow contract.</summary>
  Mul,

  /// <summary>The negation of one value, <c>0 - x</c>; takes an overflow contract.</summary>
  Neg,

  /// <summary>The quotient of the bit patterns read as unsigned numbers; takes a zero-divisor contract.</summary>
  UDiv,

  /// <summary>The quotient of the bit patterns read as two's complement numbers, truncated towards zero; takes a zero-divisor and an <c>ovf</c> contract.</summary>
  SDiv,

  /// <summary>The remainder of <see cref="UDiv"/>; takes a zero-divisor contract.</summary>
  URem,

  /// <summary>The remainder of <see cref="SDiv"/>, with the sign of the dividend; takes a zero-divisor and an <c>ovf</c> contract.</summary>
  SRem,

  /// <summary>The value shifted left, zeros shifted in; takes a shift-amount contract.</summary>
  Shl,

  /// <summary>The bit pattern shifted right, zeros shifted in; takes a shift-amount contract.</summary>
  LShr,

  /// <summary>The bit pattern shifted right, the sign bit replicated whatever the signedness of the type; takes a shift-amount contract.</summary>
  AShr

}

/// <summary>What an arithmetic operation does with a mathematical result its type does not hold.</summary>
public enum IrOverflowKind {

  /// <summary>The result modulo 2^width.</summary>
  Wrap,

  /// <summary>The operation raises its trap label.</summary>
  Trap,

  /// <summary>The result is clamped to the smallest or largest value of the type.</summary>
  Saturate

}

/// <summary>
/// The overflow contract of <see cref="IrIntegerOpcode.Add"/>, <see cref="IrIntegerOpcode.Sub"/>,
/// <see cref="IrIntegerOpcode.Mul"/>, <see cref="IrIntegerOpcode.Neg"/>, and as <c>ovf=</c> the contract of
/// a signed division for <c>MIN / -1</c> and <c>MIN % -1</c>: <c>[wrap]</c>, <c>[trap &lt;label&gt;]</c> or <c>[sat]</c>.
/// </summary>
public sealed record IrOverflowContract {

  private IrOverflowContract(IrOverflowKind kind, IrTrap label) {
    this.Kind = kind;
    this.Label = label;
  }

  /// <summary>The kind of the contract.</summary>
  public IrOverflowKind Kind { get; }

  /// <summary>The trap a <see cref="IrOverflowKind.Trap"/> contract raises; <see cref="IrTrap.IntegerOverflow"/> for every other kind.</summary>
  public IrTrap Label { get; }

  /// <summary><c>[wrap]</c>: the result modulo 2^width.</summary>
  public static IrOverflowContract Wrap { get; } = new(IrOverflowKind.Wrap, IrTrap.IntegerOverflow);

  /// <summary><c>[sat]</c>: the nearest bound of the type.</summary>
  public static IrOverflowContract Saturate { get; } = new(IrOverflowKind.Saturate, IrTrap.IntegerOverflow);

  /// <summary><c>[trap &lt;label&gt;]</c>: the operation raises the trap.</summary>
  /// <param name="label">The trap raised; an integer overflow when none is given.</param>
  /// <returns>The contract.</returns>
  public static IrOverflowContract Trap(IrTrap label = IrTrap.IntegerOverflow) => new(IrOverflowKind.Trap, label);

  /// <summary>The contract as the text form writes it inside the brackets: <c>wrap</c>, <c>trap IntegerOverflow</c>, <c>sat</c>.</summary>
  public override string ToString() => this.Kind switch {
    IrOverflowKind.Wrap => "wrap",
    IrOverflowKind.Trap => $"trap {this.Label}",
    _ => "sat"
  };

}

/// <summary>What a division or a remainder does with a zero divisor.</summary>
public enum IrDivisorKind {

  /// <summary>The operation raises its trap label.</summary>
  Trap,

  /// <summary><c>divzero=zero</c>: the quotient and the remainder are 0.</summary>
  Zero

}

/// <summary>The zero-divisor contract of a division or a remainder: <c>[trap &lt;label&gt;]</c> or <c>[divzero=zero]</c>.</summary>
public sealed record IrDivisorContract {

  private IrDivisorContract(IrDivisorKind kind, IrTrap label) {
    this.Kind = kind;
    this.Label = label;
  }

  /// <summary>The kind of the contract.</summary>
  public IrDivisorKind Kind { get; }

  /// <summary>The trap a <see cref="IrDivisorKind.Trap"/> contract raises; <see cref="IrTrap.DivideByZero"/> for every other kind.</summary>
  public IrTrap Label { get; }

  /// <summary><c>[divzero=zero]</c>: a zero divisor gives 0.</summary>
  public static IrDivisorContract Zero { get; } = new(IrDivisorKind.Zero, IrTrap.DivideByZero);

  /// <summary><c>[trap &lt;label&gt;]</c>: a zero divisor raises the trap.</summary>
  /// <param name="label">The trap raised; a division by zero when none is given.</param>
  /// <returns>The contract.</returns>
  public static IrDivisorContract Trap(IrTrap label = IrTrap.DivideByZero) => new(IrDivisorKind.Trap, label);

  /// <summary>The contract as the text form writes it: <c>trap DivideByZero</c>, <c>divzero=zero</c>.</summary>
  public override string ToString() => this.Kind switch {
    IrDivisorKind.Trap => $"trap {this.Label}",
    _ => "divzero=zero"
  };

}

/// <summary>What a shift does with an amount that is not below the width of its type.</summary>
public enum IrShiftKind {

  /// <summary>The amount modulo the width, as C#, Java and JavaScript shift.</summary>
  Mask,

  /// <summary>Every bit is shifted out: 0 for a left and a logical right shift, the sign fill for an arithmetic one, as Go and Perl shift.</summary>
  Unbounded,

  /// <summary>The shift raises its trap label, as Rust's debug build does.</summary>
  Trap,

  /// <summary>
  /// The amount is promised below the width: a constant amount that is not is a verifier error
  /// (<c>PLY0126</c>), and the interpreter raises <see cref="IrTrap.InvalidOperation"/> for a variable one.
  /// </summary>
  Strict

}

/// <summary>The shift-amount contract of a shift: <c>[mask]</c>, <c>[unbounded]</c>, <c>[trap &lt;label&gt;]</c> or <c>[strict]</c>.</summary>
public sealed record IrShiftContract {

  private IrShiftContract(IrShiftKind kind, IrTrap label) {
    this.Kind = kind;
    this.Label = label;
  }

  /// <summary>The kind of the contract.</summary>
  public IrShiftKind Kind { get; }

  /// <summary>The trap a <see cref="IrShiftKind.Trap"/> contract raises; <see cref="IrTrap.InvalidOperation"/> for every other kind.</summary>
  public IrTrap Label { get; }

  /// <summary><c>[mask]</c>: the amount modulo the width.</summary>
  public static IrShiftContract Mask { get; } = new(IrShiftKind.Mask, IrTrap.InvalidOperation);

  /// <summary><c>[unbounded]</c>: every bit shifted out.</summary>
  public static IrShiftContract Unbounded { get; } = new(IrShiftKind.Unbounded, IrTrap.InvalidOperation);

  /// <summary><c>[strict]</c>: the amount is promised below the width.</summary>
  public static IrShiftContract Strict { get; } = new(IrShiftKind.Strict, IrTrap.InvalidOperation);

  /// <summary><c>[trap &lt;label&gt;]</c>: an amount not below the width raises the trap.</summary>
  /// <param name="label">The trap raised; an integer overflow, as Rust's panic says, when none is given.</param>
  /// <returns>The contract.</returns>
  public static IrShiftContract Trap(IrTrap label = IrTrap.IntegerOverflow) => new(IrShiftKind.Trap, label);

  /// <summary>The contract as the text form writes it: <c>mask</c>, <c>unbounded</c>, <c>trap IntegerOverflow</c>, <c>strict</c>.</summary>
  public override string ToString() => this.Kind switch {
    IrShiftKind.Mask => "mask",
    IrShiftKind.Unbounded => "unbounded",
    IrShiftKind.Trap => $"trap {this.Label}",
    _ => "strict"
  };

}

/// <summary>
/// The contracts an <see cref="IrIntegerOp"/> carries; which of them an opcode requires is fixed
/// (<see cref="IrIntegerContracts.Problems"/>), and a part the opcode does not take is an error.
/// </summary>
/// <param name="Overflow">The overflow contract of an arithmetic operation, or the <c>ovf</c> contract of a signed division.</param>
/// <param name="Divisor">The zero-divisor contract of a division or a remainder.</param>
/// <param name="Shift">The shift-amount contract of a shift.</param>
public sealed record IrIntegerContract(IrOverflowContract? Overflow = null, IrDivisorContract? Divisor = null, IrShiftContract? Shift = null) {

  /// <summary>An overflow contract alone, that of an arithmetic operation.</summary>
  /// <param name="overflow">The contract.</param>
  public static implicit operator IrIntegerContract(IrOverflowContract overflow) => new(overflow);

  /// <summary>A shift-amount contract alone, that of a shift.</summary>
  /// <param name="shift">The contract.</param>
  public static implicit operator IrIntegerContract(IrShiftContract shift) => new(Shift: shift);

  /// <summary>A zero-divisor contract alone, that of an unsigned division or remainder.</summary>
  /// <param name="divisor">The contract.</param>
  public static implicit operator IrIntegerContract(IrDivisorContract divisor) => new(Divisor: divisor);

  /// <summary>
  /// The contract as the text form writes it, every part spelled out: <c>[wrap]</c>,
  /// <c>[trap DivideByZero, ovf=wrap]</c>, <c>[divzero=zero, ovf=trap IntegerOverflow]</c>, <c>[mask]</c>.
  /// </summary>
  public override string ToString() {
    IEnumerable<string?> parts = this.Divisor is null
      ? [this.Overflow?.ToString(), this.Shift?.ToString()]
      : [this.Divisor.ToString(), this.Overflow is null ? null : $"ovf={this.Overflow}", this.Shift?.ToString()];
    return $"[{string.Join(", ", parts.OfType<string>())}]";
  }

}

/// <summary>
/// An integer operation with its contract on the node (#118): the operands and the result have the
/// integer type of the node - the amount of a shift too, read as an unsigned number - and the
/// contract says what happens where the mathematical result leaves the type, the divisor is zero or
/// the amount is not below the width. Legalization lowers it to the operators and helpers every
/// backend already writes (<c>IntegerContractLegalization</c>).
/// </summary>
/// <param name="Op">The operation.</param>
/// <param name="Type">The integer type of the operands and of the result.</param>
/// <param name="Operands">The operands in the order they are evaluated: one for <see cref="IrIntegerOpcode.Neg"/>, two for every other operation.</param>
/// <param name="Contract">The contracts the operation requires.</param>
public sealed record IrIntegerOp(IrIntegerOpcode Op, IrType Type, IReadOnlyList<IrExpr> Operands, IrIntegerContract Contract) : IrExpr(Type) {

  /// <summary>The operation with its type and contract as the text form writes it, without operands: <c>Add.u32[wrap]</c>.</summary>
  public string Text => $"{this.Op}.{this.Type}{this.Contract}";

}
