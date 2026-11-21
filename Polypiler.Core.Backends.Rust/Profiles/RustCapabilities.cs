using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Rust;

/// <summary>
/// What each Rust profile has: one row per feature the emitter may write, with the release of Rust
/// that stabilized it (from the "Stabilized" notes of the standard library's documentation and the
/// release notes). A profile has a row when its MSRV (<see cref="RustProfile.RustVersion"/>) is at
/// least that release; where it has not, the emitter writes the operation with the IR helper library
/// or another form, or refuses it. A row of <see langword="null"/> is a feature no profile has, which
/// a test's table can declare to see the refusal.
/// </summary>
public sealed class RustCapabilities {

  /// <summary><c>f32::round_ties_even</c>, <c>f64::round_ties_even</c>: the rounding to even (Rust 1.77).</summary>
  public const string RoundTiesEven = "round_ties_even";

  /// <summary><c>reverse_bits</c> of the integer types (Rust 1.37).</summary>
  public const string ReverseBits = "reverse_bits";

  /// <summary><c>leading_ones</c> and <c>trailing_ones</c> of the integer types (Rust 1.46).</summary>
  public const string CountOnes = "leading_ones";

  /// <summary><c>to_bits</c> and <c>from_bits</c> of the floating-point types (Rust 1.20).</summary>
  public const string FloatBits = "to_bits";

  /// <summary><c>copysign</c> of the floating-point types (Rust 1.35).</summary>
  public const string CopySign = "copysign";

  /// <summary><c>is_subnormal</c> of the floating-point types (Rust 1.53).</summary>
  public const string IsSubnormal = "is_subnormal";

  /// <summary><c>TryFrom</c> between the integer types, the checked conversion (Rust 1.34).</summary>
  public const string TryFrom = "TryFrom";

  /// <summary><c>checked_abs</c> and <c>wrapping_abs</c> of the signed integer types (Rust 1.13).</summary>
  public const string CheckedAbs = "checked_abs";

  /// <summary>A labelled block that <c>break 'label</c> leaves, <c>label_break_value</c> (Rust 1.65).</summary>
  public const string LabeledBlock = "label_break_value";

  /// <summary><c>Mutex::new</c> as a constant, so that a <c>static</c> holds a <c>Mutex</c> (Rust 1.63).</summary>
  public const string ConstMutex = "const_mutex_new";

  /// <summary><c>std::sync::LazyLock</c>, a static made on first use (Rust 1.80).</summary>
  public const string LazyLock = "LazyLock";

  /// <summary>A static of each thread whose value is a constant, <c>thread_local! { static X: T = const { v }; }</c> (Rust 1.59).</summary>
  public const string ConstThreadLocal = "const_thread_local";

  /// <summary>The key <c>rust-version</c> of <c>Cargo.toml</c>, which Cargo reads from Rust 1.56.</summary>
  public const string RustVersionKey = "rust-version";

  /// <summary>The table of the standard library and the language as released.</summary>
  public static RustCapabilities Default { get; } = new(new Dictionary<string, Version?>(StringComparer.Ordinal) {
    [CheckedAbs] = new(1, 13),
    [FloatBits] = new(1, 20),
    [TryFrom] = new(1, 34),
    [CopySign] = new(1, 35),
    [ReverseBits] = new(1, 37),
    [IsSubnormal] = new(1, 53),
    [RustVersionKey] = new(1, 56),
    [ConstThreadLocal] = new(1, 59),
    [ConstMutex] = new(1, 63),
    [LabeledBlock] = new(1, 65),
    [RoundTiesEven] = new(1, 77),
    [LazyLock] = new(1, 80),
    [CountOnes] = new(1, 46)
  });

  private readonly IReadOnlyDictionary<string, Version?> _rows;

  private RustCapabilities(IReadOnlyDictionary<string, Version?> rows) => this._rows = rows;

  /// <summary>Every feature of the table with the release that stabilized it, <see langword="null"/> for one no profile has.</summary>
  public IReadOnlyDictionary<string, Version?> Rows => this._rows;

  /// <summary>Whether a profile has a feature.</summary>
  /// <param name="name">The feature, one of the constants of this class.</param>
  /// <param name="profile">The profile.</param>
  /// <exception cref="ArgumentException">The table has no row of the name.</exception>
  public bool Has(string name, RustProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    if (!this._rows.TryGetValue(name, out var version))
      throw new ArgumentException($"The Rust capability table has no row '{name}'.", nameof(name));

    return version is not null && profile.Has(version);
  }

  /// <summary>The table with one row changed: a feature stabilized in another release, or in none.</summary>
  /// <param name="name">The feature.</param>
  /// <param name="version">The release, or <see langword="null"/> for none.</param>
  /// <returns>The changed table.</returns>
  public RustCapabilities With(string name, Version? version) {
    var rows = this._rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    rows[name] = version;
    return new(rows);
  }

}
