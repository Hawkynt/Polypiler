using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Lowering;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust;

/// <summary>
/// What <c>+</c>, <c>-</c>, <c>*</c>, unary <c>-</c>, <c>abs</c>, <c>pow</c> and the shifts do when the
/// result does not fit: <c>rustc</c>'s <c>-C overflow-checks</c>, on in a debug build and off in a
/// release build. The source alone does not say: the caller passes the mode, which the command line
/// takes from <c>--overflow-checks</c> or from the Cargo profile that applies
/// (<see cref="Cargo.CargoOverflowMode"/>).
/// </summary>
public enum OverflowChecks {

  /// <summary>Overflow checks on (a debug build): the operation panics.</summary>
  Panic,

  /// <summary>Overflow checks off (a release build): the result wraps around, a shift amount is masked.</summary>
  Wrap

}

/// <summary>What importing a Rust source file produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
public sealed record ImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports the scalar subset of Rust (2024 edition): free functions over the integer types of 8 to
/// 64 bits, <c>isize</c> and <c>usize</c> (64 bits wide), <c>f32</c>, <c>f64</c> and <c>bool</c>, with
/// their literals, operators, <c>as</c> casts and the overflow methods (<c>wrapping_*</c>,
/// <c>checked_*</c>, <c>overflowing_*</c>, <c>saturating_*</c>); <c>let</c>, constants, blocks,
/// <c>if</c>, <c>match</c>, <c>while</c>, <c>loop</c>, <c>for</c> over integer ranges with
/// <c>rev()</c> and <c>step_by()</c>, labelled <c>break</c> and <c>continue</c>, and calls between the
/// functions of the file; a <c>pub</c> function is public. The source is parsed by a recursive-descent
/// parser of the frontend and typed by its own checker, which infers the types of unsuffixed
/// literals and untyped locals the way <c>rustc</c> does; everything outside the subset is refused
/// by name. Rust's own semantics are kept exactly: <see cref="OverflowChecks"/> decides what an
/// overflowing operator does, division and remainder panic in both modes, the explicit methods
/// ignore the mode, a float cast to an integer saturates, and operands are evaluated left to right.
/// </summary>
public static class RustFrontend {

  /// <summary>The code for source the Rust compiler rejects: a syntax error, a type error or a denied lint.</summary>
  public const string InvalidSource = "PLY1500";

  /// <summary>The registered diagnostic of <see cref="InvalidSource"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvalidSourceDescriptor = new(InvalidSource, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "rust.compiler");

  /// <summary>The code for a type the frontend does not import.</summary>
  public const string UnsupportedType = "PLY1501";

  /// <summary>The registered diagnostic of <see cref="UnsupportedType"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedTypeDescriptor = new(UnsupportedType, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "type '{0}' is not supported by the Rust frontend", "rust.types");

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string Unsupported = "PLY1502";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported by the Rust frontend", "rust.subset");

  /// <summary>The code for an overflow mode that the Cargo profile that applies does not give: a profile that is not defined, a manifest that is no TOML.</summary>
  public const string UnreadableOverflowMode = "PLY1503";

  /// <summary>The registered diagnostic of <see cref="UnreadableOverflowMode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnreadableOverflowModeDescriptor = new(UnreadableOverflowMode, DiagnosticSeverity.Error, ExitCode.UsageError, "the overflow mode cannot be read from Cargo: {0}", "rust.overflow-checks");

  /// <summary>The code for a value whose type nothing decides.</summary>
  public const string UnresolvedType = "PLY1504";

  /// <summary>The registered diagnostic of <see cref="UnresolvedType"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnresolvedTypeDescriptor = new(UnresolvedType, DiagnosticSeverity.Error, ExitCode.InvalidSource, "the type of '{0}' could not be resolved", "rust.inference");

  /// <summary>The code for <c>if let</c>, <c>while let</c> and <c>let ... else</c>.</summary>
  public const string ConditionalBinding = "PLY1510";

  /// <summary>The registered diagnostic of <see cref="ConditionalBinding"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ConditionalBindingDescriptor = new(ConditionalBinding, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported by the Rust frontend", "rust.conditional-binding");

  /// <summary>The code for a pattern outside the supported subset.</summary>
  public const string UnsupportedPattern = "PLY1511";

  /// <summary>The registered diagnostic of <see cref="UnsupportedPattern"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedPatternDescriptor = new(UnsupportedPattern, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "pattern '{0}' is not supported", "rust.patterns");

  /// <summary>The code for a <c>for</c> over anything but an integer range.</summary>
  public const string UnsupportedIterable = "PLY1512";

  /// <summary>The registered diagnostic of <see cref="UnsupportedIterable"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIterableDescriptor = new(UnsupportedIterable, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "iterating over '{0}' is not supported; only integer ranges with rev() and step_by() are", "rust.iterables");

  /// <summary>The code for <c>async</c>, <c>.await</c>, <c>try</c> and <c>gen</c> blocks and <c>yield</c>.</summary>
  public const string Coroutine = "PLY1513";

  /// <summary>The registered diagnostic of <see cref="Coroutine"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CoroutineDescriptor = new(Coroutine, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' requires coroutine lowering, which the Rust frontend does not provide", "rust.coroutines");

  /// <summary>The code for a form of function outside the subset: generic, <c>unsafe</c>, <c>extern</c>, nested, a parameter pattern.</summary>
  public const string UnsupportedFunction = "PLY1530";

  /// <summary>The registered diagnostic of <see cref="UnsupportedFunction"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedFunctionDescriptor = new(UnsupportedFunction, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "function form '{0}' is not supported", "rust.functions");

  /// <summary>The code for a macro invocation.</summary>
  public const string UnsupportedMacro = "PLY1531";

  /// <summary>The registered diagnostic of <see cref="UnsupportedMacro"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedMacroDescriptor = new(UnsupportedMacro, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "macro '{0}' is not supported", "rust.macros");

  /// <summary>The code for an attribute other than the lint levels, <c>inline</c>, <c>must_use</c>, <c>cold</c>, <c>track_caller</c> and documentation.</summary>
  public const string UnsupportedAttribute = "PLY1533";

  /// <summary>The registered diagnostic of <see cref="UnsupportedAttribute"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedAttributeDescriptor = new(UnsupportedAttribute, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "attribute '{0}' is not supported", "rust.attributes");

  private static readonly Dictionary<string, DiagnosticDescriptor> _descriptors = new[] {
    InvalidSourceDescriptor, UnsupportedTypeDescriptor, UnsupportedDescriptor, UnresolvedTypeDescriptor,
    ConditionalBindingDescriptor, UnsupportedPatternDescriptor, UnsupportedIterableDescriptor, CoroutineDescriptor,
    UnsupportedFunctionDescriptor, UnsupportedMacroDescriptor, UnsupportedAttributeDescriptor
  }.ToDictionary(descriptor => descriptor.Code);

  /// <summary>Imports one Rust source file.</summary>
  /// <param name="path">The name of the file, used in diagnostics; the module is named after it.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="overflowChecks">What an overflowing operator does: <c>rustc</c>'s <c>-C overflow-checks</c>.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, OverflowChecks overflowChecks) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(source);

    SourceFile file;
    try {
      file = Parser.Parse(source);
    } catch (RustSyntaxException error) {
      return new(null, [InvalidSourceDescriptor.At(error.Message, path, error.At.Line, error.At.Column)]);
    }

    var diagnostics = new List<Diagnostic>();
    void Report(string code, string text, Span at) {
      var descriptor = _descriptors[code];
      var message = descriptor.MessageFormat == "{0}" ? text : string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.MessageFormat, text);
      diagnostics.Add(descriptor.At(message, path, at.Line, at.Column));
    }

    var lowered = ModuleLowering.Run(file, overflowChecks, Report);
    if (diagnostics.Count > 0 || lowered is null)
      return new(null, diagnostics);

    return new(new IrModule(Path.GetFileNameWithoutExtension(path), lowered), []);
  }

}
