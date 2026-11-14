using System;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>An IEEE 754 binary interchange format whose values an output holds.</summary>
public enum FloatFormat {

  /// <summary><c>binary32</c>, four bytes per element.</summary>
  F32,

  /// <summary><c>binary64</c>, eight bytes per element.</summary>
  F64

}

/// <summary>How NaNs and zeros are compared before the mode measures anything (ADR 0005, "Special values").</summary>
public enum SpecialValues {

  /// <summary>Sign, quiet bit and payload of a NaN are compared, and <c>+0</c> differs from <c>-0</c>.</summary>
  Bitwise,

  /// <summary>Any NaN equals any NaN, and <c>+0</c> differs from <c>-0</c>; the default.</summary>
  SignedZero,

  /// <summary>Any NaN equals any NaN, and <c>+0</c> equals <c>-0</c>.</summary>
  Class

}

/// <summary>Where the bound of a tolerance comes from.</summary>
public enum BoundKind {

  /// <summary>There is no bound: the policy is <see cref="ExactnessPolicy.Exact"/>.</summary>
  None,

  /// <summary>Derived from a tolerance document, <c>VERIFICATION.tolerance.&lt;name&gt;</c>.</summary>
  Derived,

  /// <summary>Declared by the fixture itself, with a written reason.</summary>
  Declared

}

/// <summary>
/// How a candidate's output is compared with the reference: <see cref="Exact"/>, or one of the three
/// tolerance modes of <c>docs/adr/0005-floating-point-tolerance.md</c> over a declared floating-point
/// format. Every policy carries the special-value rule, applied first in every mode.
/// </summary>
public abstract record ExactnessPolicy {

  private protected ExactnessPolicy() { }

  /// <summary>How NaNs and zeros are compared.</summary>
  public SpecialValues SpecialValues { get; init; } = SpecialValues.SignedZero;

  /// <summary>The mode as the vector file spells it: <c>exact</c>, <c>ulp</c>, <c>relative</c> or <c>absolute</c>.</summary>
  public abstract string Mode { get; }

  /// <summary>The format the outputs are read in; <see langword="null"/> when they are compared as bytes.</summary>
  public abstract FloatFormat? ElementFormat { get; }

  /// <summary>Where the bound comes from.</summary>
  public virtual BoundKind BoundKind => BoundKind.None;

  /// <summary>The number of bytes of one element of a format.</summary>
  /// <param name="format">The format.</param>
  public static int SizeOf(FloatFormat format) => format == FloatFormat.F32 ? 4 : 8;

  /// <summary>The special-value rule as the vector file spells it.</summary>
  /// <param name="rule">The rule.</param>
  public static string Spelling(SpecialValues rule) => rule switch {
    SpecialValues.Bitwise => "bitwise",
    SpecialValues.Class => "class",
    _ => "signed-zero"
  };

  /// <summary>
  /// Bit for bit, subject to the special-value rule for the elements of a declared format. Without a
  /// format the outputs are bytes and are compared as such; the format stands for the image type
  /// <c>f32</c> or <c>f64</c> a function signature declares.
  /// </summary>
  /// <param name="Format">The format of every element of the outputs, or <see langword="null"/> for bytes.</param>
  public sealed record Exact(FloatFormat? Format = null) : ExactnessPolicy {

    /// <inheritdoc />
    public override string Mode => "exact";

    /// <inheritdoc />
    public override FloatFormat? ElementFormat => this.Format;

  }

  /// <summary>
  /// A comparison with slack, over outputs that are little-endian sequences of one format. The bound
  /// is either derived from a tolerance document (<see cref="Policy"/>) or declared with a written
  /// reason (<see cref="Reason"/>), never neither and never both.
  /// </summary>
  public abstract record Tolerance : ExactnessPolicy {

    private protected Tolerance(FloatFormat format, string? policy, string? reason) {
      if (!Enum.IsDefined(format))
        throw new ArgumentOutOfRangeException(nameof(format), format, "unknown floating-point format");
      if ((policy is null) == (reason is null))
        throw new ArgumentException("a tolerance carries either the ID of its tolerance document or the reason of a declared bound, exactly one of them", policy is null ? nameof(reason) : nameof(policy));
      if (string.IsNullOrWhiteSpace(policy ?? reason))
        throw new ArgumentException("the policy ID or reason of a tolerance is empty", policy is null ? nameof(reason) : nameof(policy));

      this.Format = format;
      this.Policy = policy;
      this.Reason = reason;
    }

    /// <summary>The format of every element.</summary>
    public FloatFormat Format { get; }

    /// <summary>The ID of the tolerance document the bound is derived from, or <see langword="null"/>.</summary>
    public string? Policy { get; }

    /// <summary>The written reason of a bound the fixture declares, or <see langword="null"/>.</summary>
    public string? Reason { get; }

    /// <inheritdoc />
    public override FloatFormat? ElementFormat => this.Format;

    /// <inheritdoc />
    public override BoundKind BoundKind => this.Policy is null ? BoundKind.Declared : BoundKind.Derived;

    /// <summary>The bound as a message shows it.</summary>
    public abstract string BoundText { get; }

  }

  /// <summary>Passes when the comparison distance, the number of representable values between the two, is at most <see cref="Bound"/>.</summary>
  public sealed record Ulp : Tolerance {

    /// <summary>Creates the policy.</summary>
    /// <param name="bound">The largest distance that passes; positive.</param>
    /// <param name="format">The format of every element.</param>
    /// <param name="policy">The ID of the tolerance document the bound is derived from.</param>
    /// <param name="reason">The reason of a declared bound.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bound is not positive, or the format is unknown.</exception>
    /// <exception cref="ArgumentException">Not exactly one of <paramref name="policy"/> and <paramref name="reason"/> is given.</exception>
    public Ulp(int bound, FloatFormat format, string? policy = null, string? reason = null) : base(format, policy, reason)
      => this.Bound = bound > 0 ? bound : throw new ArgumentOutOfRangeException(nameof(bound), bound, "an ulp bound is a positive integer");

    /// <summary>The largest distance that passes.</summary>
    public int Bound { get; }

    /// <inheritdoc />
    public override string Mode => "ulp";

    /// <inheritdoc />
    public override string BoundText => this.Bound.ToString(System.Globalization.CultureInfo.InvariantCulture);

  }

  /// <summary>Passes when <c>|actual - reference| &lt;= Bound * |reference|</c>, evaluated exactly.</summary>
  public sealed record Relative : Tolerance {

    /// <summary>Creates the policy.</summary>
    /// <param name="bound">The bound; a positive finite number.</param>
    /// <param name="format">The format of every element.</param>
    /// <param name="policy">The ID of the tolerance document the bound is derived from.</param>
    /// <param name="reason">The reason of a declared bound.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bound is not a positive finite number, or the format is unknown.</exception>
    /// <exception cref="ArgumentException">Not exactly one of <paramref name="policy"/> and <paramref name="reason"/> is given.</exception>
    public Relative(double bound, FloatFormat format, string? policy = null, string? reason = null) : base(format, policy, reason)
      => this.Bound = CheckReal(bound);

    /// <summary>The bound.</summary>
    public double Bound { get; }

    /// <inheritdoc />
    public override string Mode => "relative";

    /// <inheritdoc />
    public override string BoundText => this.Bound.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

  }

  /// <summary>Passes when <c>|actual - reference| &lt;= Bound</c>, evaluated exactly.</summary>
  public sealed record Absolute : Tolerance {

    /// <summary>Creates the policy.</summary>
    /// <param name="bound">The bound; a positive finite number.</param>
    /// <param name="format">The format of every element.</param>
    /// <param name="policy">The ID of the tolerance document the bound is derived from.</param>
    /// <param name="reason">The reason of a declared bound.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bound is not a positive finite number, or the format is unknown.</exception>
    /// <exception cref="ArgumentException">Not exactly one of <paramref name="policy"/> and <paramref name="reason"/> is given.</exception>
    public Absolute(double bound, FloatFormat format, string? policy = null, string? reason = null) : base(format, policy, reason)
      => this.Bound = CheckReal(bound);

    /// <summary>The bound.</summary>
    public double Bound { get; }

    /// <inheritdoc />
    public override string Mode => "absolute";

    /// <inheritdoc />
    public override string BoundText => this.Bound.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

  }

  private static double CheckReal(double bound)
    => double.IsFinite(bound) && bound > 0 ? bound : throw new ArgumentOutOfRangeException(nameof(bound), bound, "a relative or absolute bound is a positive finite number");

}
