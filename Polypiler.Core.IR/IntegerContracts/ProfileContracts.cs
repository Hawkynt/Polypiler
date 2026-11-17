using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// What a source profile's operator means as a contract, or the recipe it stands for (#122): exactly
/// one of the two is set. A recipe is ordinary IR built from existing operations; the table only names it.
/// </summary>
/// <param name="Contract">The contract of the operation, or <see langword="null"/> where a recipe stands instead.</param>
/// <param name="Recipe">The name of the recipe, or <see langword="null"/> where a contract is enough.</param>
public sealed record IrProfileContract(IrIntegerContract? Contract, string? Recipe) {

  /// <summary>The entry as <c>docs/reference/integer-operations.md</c> writes it: the contract in brackets, or the recipe's name.</summary>
  public override string ToString() => this.Contract?.ToString() ?? this.Recipe!;

}

/// <summary>
/// The one table that maps each source profile's integer operators to contracts (#122), so that no two
/// frontends encode the same language rule differently. A profile is named <c>&lt;language&gt;/&lt;version&gt;</c>
/// (<c>c/23</c>, <c>csharp/net10.0</c>, <c>rust/2024</c>); only the language decides. The rows are those
/// of <c>docs/adr/0002-numeric-contracts.md</c> for <c>c</c>, <c>csharp</c>, <c>rust</c>, <c>java</c> and
/// <c>perl</c>; the frontends fill in the others.
/// </summary>
public static class ProfileContracts {

  /// <summary>
  /// The recipe of Perl's integer <c>+</c>, <c>-</c> and <c>*</c>: a result outside <c>i64</c> continues
  /// exactly as <c>u64</c> up to <c>UV_MAX</c> and as <c>f64</c> only beyond it (perlop, "Integer Arithmetic").
  /// </summary>
  public const string PromoteToFloat = "promote-to-float";

  /// <summary>The context of C#'s <c>checked</c> arithmetic.</summary>
  public const string Checked = "checked";

  /// <summary>The context of C#'s <c>unchecked</c> arithmetic, the default.</summary>
  public const string Unchecked = "unchecked";

  /// <summary>The context of a Rust debug build, the default: overflow panics.</summary>
  public const string Debug = "debug";

  /// <summary>The context of a Rust release build: overflow wraps.</summary>
  public const string Release = "release";

  private static readonly Dictionary<string, string[]> _contexts = new(StringComparer.Ordinal) {
    ["c"] = [],
    ["csharp"] = [Unchecked, Checked],
    ["rust"] = [Debug, Release],
    ["java"] = [],
    ["perl"] = []
  };

  /// <summary>The languages the table has rows of.</summary>
  public static IReadOnlyCollection<string> Languages => _contexts.Keys;

  /// <summary>Whether a source profile permits <c>[undefined]</c> contracts: only C's does, for signed overflow, shifts and division.</summary>
  /// <param name="profile">The profile, <c>&lt;language&gt;/&lt;version&gt;</c>.</param>
  /// <returns><see langword="true"/> when the profile declares <c>undefinedOverflow</c>.</returns>
  public static bool DeclaresUndefinedOverflow(string profile) => Language(profile) == "c";

  /// <summary>The contract or recipe a source profile's operator stands for.</summary>
  /// <param name="profile">The profile, <c>&lt;language&gt;/&lt;version&gt;</c>.</param>
  /// <param name="op">The operation the operator is.</param>
  /// <param name="type">The integer type it computes on; its signedness decides C's rows.</param>
  /// <param name="context">The context the operator stands in: <see cref="Checked"/> or <see cref="Unchecked"/> for C#, <see cref="Debug"/> or <see cref="Release"/> for Rust; <see langword="null"/> for the default.</param>
  /// <returns>The entry, or <see langword="null"/> where the table has no row: a language it does not know, a Perl operator other than <c>+ - *</c>.</returns>
  /// <exception cref="ArgumentException">The context is not one of the language's.</exception>
  public static IrProfileContract? For(string profile, IrIntegerOpcode op, IrType type, string? context = null) {
    ArgumentNullException.ThrowIfNull(type);
    var language = Language(profile);
    if (!_contexts.TryGetValue(language, out var contexts))
      return null;

    if (context is not null && !contexts.Contains(context))
      throw new ArgumentException($"'{context}' is no context of the profile '{profile}'; it has {(contexts.Length == 0 ? "none" : string.Join(", ", contexts))}.", nameof(context));

    var trapping = (language, context) is ("csharp", Checked) or ("rust", null or Debug);
    var family = op switch {
      IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul or IrIntegerOpcode.Neg => 0,
      IrIntegerOpcode.UDiv or IrIntegerOpcode.URem or IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem => 1,
      _ => 2
    };
    var signedDivision = op is IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem;
    IrIntegerContract? contract = (language, family) switch {
      // Perl's rows are recipes, below.
      ("perl", _) => null,
      ("c", 0) => type.IsSigned ? IrOverflowContract.Undefined : IrOverflowContract.Wrap,
      ("c", 1) => new(signedDivision ? IrOverflowContract.Undefined : null, IrDivisorContract.Undefined),
      ("c", _) => IrShiftContract.Undefined,
      (_, 0) => trapping ? IrOverflowContract.Trap() : IrOverflowContract.Wrap,
      // Java's MIN / -1 is MIN and MIN % -1 is 0 (JLS §15.17.2, §15.17.3); C# and Rust trap.
      (_, 1) => new(signedDivision ? language == "java" ? IrOverflowContract.Wrap : IrOverflowContract.Trap() : null, IrDivisorContract.Trap()),
      _ => language == "rust" && trapping ? IrShiftContract.Trap() : IrShiftContract.Mask
    };
    if (contract is not null)
      return new(contract, null);

    return language == "perl" && op is IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul ? new(null, PromoteToFloat) : null;
  }

  /// <summary>
  /// The <c>[undefined]</c> contracts of a module whose source profile does not permit them, each as
  /// <c>PLY0127</c>: "'Add.i32[undefined]' is not permitted: source profile 'java/21' does not declare undefinedOverflow".
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="profile">Its source profile.</param>
  /// <returns>One diagnostic per node; empty where the profile permits them or the module has none.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, string profile) {
    ArgumentNullException.ThrowIfNull(module);
    if (DeclaresUndefinedOverflow(profile))
      return [];

    return [.. module.Functions
      .SelectMany(function => IrTree.Nodes(function.Body).OfType<IrIntegerOp>().Where(IsUndefined).Select(node => (function, node)))
      .Select(entry => IrIntegerContracts.MissingContract.At(
        $"'{entry.node.Text}' is not permitted: source profile '{profile}' does not declare undefinedOverflow in '{entry.function.Name}'"))];
  }

  private static bool IsUndefined(IrIntegerOp node) => node.Contract is var contract
    && (contract.Overflow?.Kind == IrOverflowKind.Undefined || contract.Divisor?.Kind == IrDivisorKind.Undefined || contract.Shift?.Kind == IrShiftKind.Undefined);

  private static string Language(string profile) {
    ArgumentException.ThrowIfNullOrEmpty(profile);
    var slash = profile.IndexOf('/', StringComparison.Ordinal);
    return slash < 0 ? profile : profile[..slash];
  }

}
