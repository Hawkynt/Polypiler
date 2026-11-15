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
public sealed record ImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>
  /// The functions of the module that are the crate's <c>#[test]</c> functions, by their IR names: a
  /// test passes when the function returns and fails when it panics. The IR has no role for a test, so
  /// the import names them here.
  /// </summary>
  public IReadOnlyList<string> Tests { get; init; } = [];

  /// <summary>The crate paths of the modules in the order of the module graph, the order the functions of the module follow.</summary>
  public IReadOnlyList<string> ModuleOrder { get; init; } = [];

}

/// <summary>How a Rust source is imported: the build settings the source does not state, and where the other files of its crate are.</summary>
/// <param name="OverflowChecks">What an overflowing operator does: <c>rustc</c>'s <c>-C overflow-checks</c>.</param>
public sealed record RustImportOptions(OverflowChecks OverflowChecks) {

  /// <summary>
  /// Whether <c>debug_assert*!</c> asserts, <c>rustc</c>'s <c>-C debug-assertions</c>: on in a debug build,
  /// off in a release build. <see langword="null"/> when the profile does not say, which refuses a
  /// source that uses one (<c>PLY1536</c>).
  /// </summary>
  public bool? DebugAssertions { get; init; }

  /// <summary>
  /// Reads another file of the crate by its path, for <c>mod m;</c>; <see langword="null"/> when the file
  /// does not exist. Without a reader, a file module is not found.
  /// </summary>
  public Func<string, string?>? ReadFile { get; init; }

  /// <summary>Whether the source is one of the frontend's own helpers, which may name the IR's string operations.</summary>
  internal bool Internal { get; init; }

}

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

  /// <summary>The code for an element type or a form of array, slice or <c>Vec</c> outside the subset: a nested array, a slice of structs, a slice pattern.</summary>
  public const string UnsupportedElement = "PLY1520";

  /// <summary>The registered diagnostic of <see cref="UnsupportedElement"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedElementDescriptor = new(UnsupportedElement, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported by the Rust frontend", "rust.memory");

  /// <summary>The code for <c>str</c>, <c>String</c> and <c>char</c>.</summary>
  public const string TextType = "PLY1521";

  /// <summary>The registered diagnostic of <see cref="TextType"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TextTypeDescriptor = new(TextType, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "text type '{0}' is not supported; use byte slices", "rust.text");

  /// <summary>The code for <c>unsafe</c> blocks, raw pointers, <c>transmute</c>, <c>union</c>, <c>static mut</c> and <c>MaybeUninit</c>.</summary>
  public const string UnsafeConstruct = "PLY1522";

  /// <summary>The registered diagnostic of <see cref="UnsafeConstruct"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsafeConstructDescriptor = new(UnsafeConstruct, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "unsafe construct '{0}' is not supported", "rust.unsafe");

  /// <summary>The code for <c>Box</c>, <c>Rc</c>, <c>Arc</c>, <c>Cell</c>, <c>RefCell</c> and <c>Cow</c>.</summary>
  public const string SmartPointer = "PLY1523";

  /// <summary>The registered diagnostic of <see cref="SmartPointer"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SmartPointerDescriptor = new(SmartPointer, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "smart pointer or interior-mutability type '{0}' is not supported", "rust.ownership");

  /// <summary>The code for a method of slices, arrays or <c>Vec</c> outside the subset.</summary>
  public const string UnsupportedSliceMethod = "PLY1524";

  /// <summary>The registered diagnostic of <see cref="UnsupportedSliceMethod"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedSliceMethodDescriptor = new(UnsupportedSliceMethod, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "method '{0}' is not supported on slices, arrays or Vec", "rust.slice-methods");

  /// <summary>The code for the collections other than <c>Vec</c>.</summary>
  public const string UnsupportedCollection = "PLY1525";

  /// <summary>The registered diagnostic of <see cref="UnsupportedCollection"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedCollectionDescriptor = new(UnsupportedCollection, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "collection type '{0}' is not supported", "rust.collections");

  /// <summary>The code for a closure that escapes the function that defines it, or moves a value that is not <c>Copy</c>.</summary>
  public const string EscapingClosure = "PLY1532";

  /// <summary>The registered diagnostic of <see cref="EscapingClosure"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor EscapingClosureDescriptor = new(EscapingClosure, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "closure escapes its defining function: {0}", "rust.closures");

  /// <summary>The code for a path into another crate or to a standard-library item outside the subset.</summary>
  public const string ForeignPath = "PLY1534";

  /// <summary>The registered diagnostic of <see cref="ForeignPath"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ForeignPathDescriptor = new(ForeignPath, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' refers to an item outside the supported standard-library subset or to another crate", "rust.paths");

  /// <summary>The code for an iterator method outside the subset.</summary>
  public const string UnsupportedIteratorMethod = "PLY1535";

  /// <summary>The registered diagnostic of <see cref="UnsupportedIteratorMethod"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIteratorMethodDescriptor = new(UnsupportedIteratorMethod, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "iterator method '{0}' is not supported", "rust.iterators");

  /// <summary>The code for a <c>debug_assert*!</c> imported without the <c>debugAssertions</c> setting.</summary>
  public const string MissingDebugAssertions = "PLY1536";

  /// <summary>The registered diagnostic of <see cref="MissingDebugAssertions"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingDebugAssertionsDescriptor = new(MissingDebugAssertions, DiagnosticSeverity.Error, ExitCode.UsageError, "the source profile does not set 'debugAssertions': {0}", "rust.debug-assertions");

  /// <summary>The code for traits, trait implementations and trait objects.</summary>
  public const string UnsupportedTrait = "PLY1540";

  /// <summary>The registered diagnostic of <see cref="UnsupportedTrait"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedTraitDescriptor = new(UnsupportedTrait, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "traits are not supported by the Rust frontend: '{0}' (the object-model contract of #473 and #474)", "rust.traits");

  /// <summary>The code for generic parameters of structs, enums and impls.</summary>
  public const string UnsupportedGeneric = "PLY1541";

  /// <summary>The registered diagnostic of <see cref="UnsupportedGeneric"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedGenericDescriptor = new(UnsupportedGeneric, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "generic parameters are not supported: '{0}'", "rust.generics");

  /// <summary>The code for named lifetime parameters and fields of reference type.</summary>
  public const string UnsupportedLifetime = "PLY1542";

  /// <summary>The registered diagnostic of <see cref="UnsupportedLifetime"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedLifetimeDescriptor = new(UnsupportedLifetime, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "lifetime parameters and reference fields are not supported: '{0}'", "rust.lifetimes");

  /// <summary>The code for an enum whose variants carry data.</summary>
  public const string DataEnum = "PLY1543";

  /// <summary>The registered diagnostic of <see cref="DataEnum"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DataEnumDescriptor = new(DataEnum, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "enum '{0}' has variants with data", "rust.enums");

  /// <summary>The code for a derive or an attribute of a type outside the subset.</summary>
  public const string UnsupportedDerive = "PLY1544";

  /// <summary>The registered diagnostic of <see cref="UnsupportedDerive"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDeriveDescriptor = new(UnsupportedDerive, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "attribute or derive '{0}' is not supported", "rust.derives");

  /// <summary>The code for a method of <c>Option</c> or <c>Result</c> outside the subset, and a <c>Result</c> of an unsupported error type.</summary>
  public const string UnsupportedOptionMethod = "PLY1545";

  /// <summary>The registered diagnostic of <see cref="UnsupportedOptionMethod"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedOptionMethodDescriptor = new(UnsupportedOptionMethod, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported on Option or Result", "rust.option-result");

  /// <summary>The code for <c>impl Drop</c>.</summary>
  public const string Destructor = "PLY1546";

  /// <summary>The registered diagnostic of <see cref="Destructor"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DestructorDescriptor = new(Destructor, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "destructors are not supported: 'impl Drop for {0}'", "rust.drop");

  private static readonly Dictionary<string, DiagnosticDescriptor> _descriptors = new[] {
    InvalidSourceDescriptor, UnsupportedTypeDescriptor, UnsupportedDescriptor, UnresolvedTypeDescriptor,
    ConditionalBindingDescriptor, UnsupportedPatternDescriptor, UnsupportedIterableDescriptor, CoroutineDescriptor,
    UnsupportedFunctionDescriptor, UnsupportedMacroDescriptor, UnsupportedAttributeDescriptor,
    UnsupportedElementDescriptor, TextTypeDescriptor, UnsafeConstructDescriptor, SmartPointerDescriptor, UnsupportedSliceMethodDescriptor,
    UnsupportedCollectionDescriptor, EscapingClosureDescriptor, ForeignPathDescriptor, UnsupportedIteratorMethodDescriptor,
    MissingDebugAssertionsDescriptor, UnsupportedTraitDescriptor, UnsupportedGenericDescriptor, UnsupportedLifetimeDescriptor,
    DataEnumDescriptor, UnsupportedDeriveDescriptor, UnsupportedOptionMethodDescriptor, DestructorDescriptor
  }.ToDictionary(descriptor => descriptor.Code);

  /// <summary>Imports one Rust source file.</summary>
  /// <param name="path">The name of the file, used in diagnostics; the module is named after it.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="overflowChecks">What an overflowing operator does: <c>rustc</c>'s <c>-C overflow-checks</c>.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, OverflowChecks overflowChecks) => Import(path, source, new RustImportOptions(overflowChecks));

  /// <summary>Imports a crate from its root file: the file and every module it declares.</summary>
  /// <param name="path">The path of the root file, from which file modules are found; the module is named after it.</param>
  /// <param name="source">The text of the root file.</param>
  /// <param name="options">The build settings and the reader of the crate's other files.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, RustImportOptions options) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(options);

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

    var lowered = ModuleLowering.Run(file, path, options, Report);
    if (diagnostics.Count > 0 || lowered is null)
      return new(null, diagnostics);

    var module = new IrModule(Path.GetFileNameWithoutExtension(path), lowered.Functions, lowered.Constants) { Types = lowered.Types };
    return new(module, []) { Tests = lowered.Tests, ModuleOrder = lowered.ModuleOrder };
  }

}
