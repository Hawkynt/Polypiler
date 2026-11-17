using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// What a conversion of a floating-point number to an integer does with a value the integer type
/// cannot hold (<c>docs/adr/0002-numeric-contracts.md</c>, "Range policies"). The value is what the
/// <c>i32</c> constant that carries the policy on <see cref="IrIntrinsicOp.FloatToInt"/> holds.
/// </summary>
public enum RangePolicy {

  /// <summary><c>trap &lt;label&gt;</c>: a NaN, an infinity or a rounded value outside the type traps.</summary>
  Trap = 0,

  /// <summary><c>sat</c>: the nearest bound of the type; a NaN gives 0 unless <c>nan=trap</c> says otherwise.</summary>
  Saturate = 1,

  /// <summary><c>wrap</c>: the rounded value modulo 2^N in the signedness of the type; a NaN and the infinities give 0 (ECMA-262 <c>ToInt32</c>, <c>ToUint32</c>).</summary>
  Wrap = 2,

  /// <summary><c>sentinel &lt;value&gt;</c>: one fixed value for a NaN, an infinity and every value outside the type (x86's "integer indefinite").</summary>
  Sentinel = 3

  // The policy `undefined` of C (#122) is to follow as Undefined = 4: its interpreter outcome is UndefinedBehaviourReached.

}

/// <summary>
/// The contract of a conversion of a floating-point number to an integer (#117): the rounding mode,
/// which rounds the exact value once, the range policy, which applies to the rounded integer, the
/// value of <see cref="RangePolicy.Sentinel"/>, the trap of <see cref="RangePolicy.Trap"/>, and the
/// trap a NaN raises instead of what the policy gives it (<c>nan=trap &lt;label&gt;</c>).
/// <para>
/// The text form is the one the issues write: <c>rtz, sat, nan=0</c>, <c>rtz, wrap</c>,
/// <c>rtz, trap Overflow</c>, <c>rtz, trap Overflow, nan=trap InvalidOperation</c>,
/// <c>rne, sentinel -2147483648</c>. <see cref="TryParse"/> takes the short forms as well - a missing
/// rounding mode is <c>rtz</c>, a contract of a rounding mode alone is <c>sentinel MIN</c>, bare
/// <c>trap</c> is <c>trap Overflow</c>, and <c>saturate</c>, <c>modular</c>, <c>round-even</c> and
/// <c>nan→0</c> stand for <c>sat</c>, <c>wrap</c>, <c>rne</c> and <c>nan=0</c> - and
/// <see cref="ToString"/> prints the canonical one.
/// </para>
/// </summary>
/// <param name="Rounding">The rounding to an integer.</param>
/// <param name="Policy">What a value outside the type gives.</param>
/// <param name="Sentinel">The value of <see cref="RangePolicy.Sentinel"/>, as an <see cref="IrConst"/> of the type carries it; 0 for the other policies.</param>
/// <param name="Trap">The trap of <see cref="RangePolicy.Trap"/>.</param>
/// <param name="NanTrap">The trap a NaN raises, or <see langword="null"/> where a NaN gives what the policy gives it.</param>
public sealed record FloatToIntContract(RoundingMode Rounding, RangePolicy Policy, long Sentinel = 0, IrTrap Trap = IrTrap.IntegerOverflow, IrTrap? NanTrap = null) {

  /// <summary>The traps a conversion may raise: those of arithmetic, each with its exception class.</summary>
  public static IReadOnlyList<IrTrap> Traps { get; } = [IrTrap.DivideByZero, IrTrap.IntegerOverflow, IrTrap.InvalidOperation];

  /// <summary>A trap as the text form names it: <c>Overflow</c> for <see cref="IrTrap.IntegerOverflow"/>, the name of the trap otherwise.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The label.</returns>
  public static string Label(IrTrap trap) => trap == IrTrap.IntegerOverflow ? "Overflow" : trap.ToString();

  /// <summary>The contract as its text form prints it, with every part spelled out.</summary>
  public override string ToString() {
    var parts = new List<string> { IeeeNames.Of(this.Rounding) };
    parts.Add(this.Policy switch {
      RangePolicy.Trap => "trap " + Label(this.Trap),
      RangePolicy.Saturate => "sat",
      RangePolicy.Wrap => "wrap",
      _ => "sentinel " + this.Sentinel.ToString(System.Globalization.CultureInfo.InvariantCulture)
    });
    if (this.NanTrap is { } nan)
      parts.Add("nan=trap " + Label(nan));
    else if (this.Policy == RangePolicy.Saturate)
      parts.Add("nan=0");

    return string.Join(", ", parts);
  }

  /// <summary>
  /// Reads the text form of a contract for a conversion to an integer type, the short forms included.
  /// </summary>
  /// <param name="text">The parts, separated by commas.</param>
  /// <param name="type">The integer type converted to, whose minimum is the sentinel of a contract of a rounding mode alone.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="error">What is wrong with the text, as the diagnostic says it.</param>
  /// <returns><see langword="true"/> when the text is a contract.</returns>
  public static bool TryParse(string text, IrType type, out FloatToIntContract? contract, out string? error) {
    ArgumentNullException.ThrowIfNull(text);
    contract = null;
    error = null;
    if (!type.IsInteger) {
      error = $"'FloatToInt' converts to an integer type, not {type}";
      return false;
    }

    RoundingMode? rounding = null;
    RangePolicy? policy = null;
    var sentinel = 0L;
    var trap = IrTrap.IntegerOverflow;
    IrTrap? nanTrap = null;
    var nanZero = false;
    foreach (var part in text.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)) {
      var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (words.Length == 1 && (IeeeNames.TryParse(words[0], out RoundingMode mode) || TryAlias(words[0], out mode))) {
        if (rounding is not null)
          return Fail("two rounding modes", out error);

        rounding = mode;
        continue;
      }

      if (part is "nan=0" or "nan→0") {
        nanZero = true;
        continue;
      }

      if (words is ["nan=trap", var nanLabel]) {
        if (!TryTrap(nanLabel, out var raised))
          return Fail($"unknown trap '{nanLabel}'", out error);

        nanTrap = raised;
        continue;
      }

      if (policy is not null)
        return Fail("two range policies", out error);

      switch (words) {
        case ["trap"]:
          policy = RangePolicy.Trap;
          break;
        case ["trap", var label]:
          if (!TryTrap(label, out trap))
            return Fail($"unknown trap '{label}'", out error);

          policy = RangePolicy.Trap;
          break;
        case ["sat" or "saturate"]:
          policy = RangePolicy.Saturate;
          break;
        case ["wrap" or "modular"]:
          policy = RangePolicy.Wrap;
          break;
        case ["sentinel", var value]:
          if (!long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out sentinel)
              || !FitsIn(type, sentinel))
            return Fail($"the sentinel '{value}' is no value of {type}", out error);

          policy = RangePolicy.Sentinel;
          break;
        default:
          return Fail($"unknown part '{part}' of a contract", out error);
      }
    }

    if (rounding is null && policy is null)
      return Fail("'FloatToInt' requires a contract: a rounding mode, a range policy (trap, sat, wrap, sentinel) or both", out error);
    if (nanZero && (nanTrap is not null || policy != RangePolicy.Saturate))
      return Fail("nan=0 belongs to sat alone", out error);

    if (policy is null) {
      // A rounding mode alone: x87's integer indefinite, the smallest value of the type.
      policy = RangePolicy.Sentinel;
      sentinel = (long)IrOperators.Minimum(type);
    }

    contract = new(rounding ?? RoundingMode.TowardZero, policy.Value, sentinel, trap, nanTrap);
    return true;
  }

  /// <summary>Reads the text form of a contract.</summary>
  /// <param name="text">The text.</param>
  /// <param name="type">The integer type converted to.</param>
  /// <returns>The contract.</returns>
  /// <exception cref="FormatException">The text is no contract.</exception>
  public static FloatToIntContract Parse(string text, IrType type)
    => TryParse(text, type, out var contract, out var error) ? contract! : throw new FormatException(error);

  /// <summary>The node of a conversion of a number to an integer type under this contract.</summary>
  /// <param name="type">The integer type.</param>
  /// <param name="value">The number, of <c>f32</c> or <c>f64</c>.</param>
  /// <returns>The node.</returns>
  public IrIntrinsic Apply(IrType type, IrExpr value) {
    ArgumentNullException.ThrowIfNull(value);
    return new(IrIntrinsicOp.FloatToInt, type, [
      value, Int((int)this.Rounding), Int((int)this.Policy), new IrConst(type, this.Sentinel), Int((int)this.Trap), Int(this.NanTrap is { } nan ? (int)nan : -1)
    ]);
  }

  /// <summary>The contract a node of <see cref="IrIntrinsicOp.FloatToInt"/> carries in its constant arguments.</summary>
  /// <param name="intrinsic">A node whose arguments the verifier accepted.</param>
  /// <returns>The contract.</returns>
  public static FloatToIntContract Of(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    long Constant(int index) => ((IrConst)intrinsic.Arguments[index]).Value;
    var nan = Constant(5);
    return new((RoundingMode)Constant(1), (RangePolicy)Constant(2), Constant(3), (IrTrap)Constant(4), nan < 0 ? null : (IrTrap)nan);
  }

  private static bool TryAlias(string word, out RoundingMode mode) {
    mode = RoundingMode.NearestEven;
    return word == "round-even";
  }

  private static bool TryTrap(string label, out IrTrap trap) {
    trap = label == "Overflow" ? IrTrap.IntegerOverflow : Enum.TryParse<IrTrap>(label, out var named) ? named : (IrTrap)(-1);
    return Traps.Contains(trap) && (label == "Overflow" || label == trap.ToString());
  }

  private static bool FitsIn(IrType type, long value) => value >= IrOperators.Minimum(type) && value <= IrOperators.Maximum(type);

  private static bool Fail(string message, out string? error) {
    error = message;
    return false;
  }

  private static IrConst Int(int value) => new(IrType.I32, value);

}
