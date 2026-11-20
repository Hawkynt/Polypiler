using System.Globalization;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Verification.Proofs.Obligations;

/// <summary>The kinds of value an obligation has.</summary>
internal enum ObligationTypeKind {

  /// <summary>An integer read as unsigned where an operation cares: <c>u1</c> to <c>u64</c>.</summary>
  Unsigned,

  /// <summary>An integer read as two's complement where an operation cares: <c>i1</c> to <c>i64</c>.</summary>
  Signed,

  /// <summary>A truth value: <c>bool</c>.</summary>
  Bool,

  /// <summary>A floating-point number, which no prover models: <c>f16</c>, <c>f32</c>, <c>f64</c>.</summary>
  Float

}

/// <summary>The type of a value of an obligation: its kind and its width in bits.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Width">The width; 1 for <c>bool</c>.</param>
internal readonly partial record struct ObligationType(ObligationTypeKind Kind, int Width) {

  /// <summary>The truth values.</summary>
  public static ObligationType Bool { get; } = new(ObligationTypeKind.Bool, 1);

  /// <summary>Whether the type is an integer of either signedness.</summary>
  public bool IsInteger => this.Kind is ObligationTypeKind.Unsigned or ObligationTypeKind.Signed;

  /// <summary>The bits a value of the type occupies, as a mask of the low <see cref="Width"/> bits.</summary>
  public ulong Mask => this.Width >= 64 ? ulong.MaxValue : (1UL << this.Width) - 1;

  /// <summary>Reads a type as an obligation writes it.</summary>
  /// <param name="text">The text, such as <c>u8</c>, <c>i64</c>, <c>bool</c> or <c>f32</c>.</param>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> when the text is a type.</returns>
  public static bool TryParse(string text, out ObligationType type) {
    type = default;
    if (text == "bool") {
      type = Bool;
      return true;
    }

    var match = Pattern().Match(text);
    if (!match.Success)
      return false;

    var width = int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture);
    var kind = match.Groups["kind"].Value switch {
      "u" => ObligationTypeKind.Unsigned,
      "i" => ObligationTypeKind.Signed,
      _ => ObligationTypeKind.Float
    };
    if (kind == ObligationTypeKind.Float ? width is not (16 or 32 or 64) : width is < 1 or > 64)
      return false;

    type = new(kind, width);
    return true;
  }

  /// <inheritdoc/>
  public override string ToString() => this.Kind switch {
    ObligationTypeKind.Bool => "bool",
    ObligationTypeKind.Unsigned => $"u{this.Width}",
    ObligationTypeKind.Signed => $"i{this.Width}",
    _ => $"f{this.Width}"
  };

  [GeneratedRegex("^(?<kind>[uif])(?<width>[0-9]{1,2})$")]
  private static partial Regex Pattern();

}
