using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Rust;

/// <summary>A unit test the crate carries: one call of a function and what it must give.</summary>
/// <param name="Name">The name of the test function, converted to <c>snake_case</c>.</param>
/// <param name="Function">The IR name of the function called.</param>
/// <param name="Arguments">One constant per parameter.</param>
/// <param name="Expected">The result the call must return, or <see langword="null"/> where it must trap or returns nothing.</param>
/// <param name="Trap">The trap the call must raise, or <see langword="null"/>.</param>
public sealed record RustTestCase(string Name, string Function, IReadOnlyList<IrConst> Arguments, IrConst? Expected, IrTrap? Trap = null);

/// <summary>What the crate is besides its code: the package and the tests it carries.</summary>
public sealed record RustEmitOptions {

  /// <summary>The options when none are given.</summary>
  public static RustEmitOptions Default { get; } = new();

  /// <summary>The name of the package; the module's name in <c>snake_case</c> when <see langword="null"/>.</summary>
  public string? PackageName { get; init; }

  /// <summary>The version of the package.</summary>
  public string PackageVersion { get; init; } = "0.1.0";

  /// <summary>The floating-point mode the module was legalized for, which the header states.</summary>
  public FloatMode Mode { get; init; } = FloatMode.Accurate;

  /// <summary>The unit tests, emitted into a <c>#[cfg(test)]</c> module at the end of the module's file.</summary>
  public IReadOnlyList<RustTestCase> Tests { get; init; } = [];

  /// <summary>
  /// The module of the crate each function is placed in, by the function's IR name, as a path from
  /// the crate root: <c>util</c> writes it into <c>src/util.rs</c>, <c>util::bits</c> into
  /// <c>src/util/bits.rs</c>, and every other file names it by <c>use crate::util::f;</c>. A function
  /// whose IR name starts with the module's path joined by <c>_</c>, as the Rust frontend names them
  /// (<c>util_bits_mix</c>), is called by the rest of its name. A function placed nowhere, or in the
  /// IR module's own module, stays in that module's file with everything that is no function.
  /// Empty: every function there.
  /// </summary>
  public IReadOnlyDictionary<string, string> Modules { get; init; } = new Dictionary<string, string>();

  /// <summary>
  /// Whether the IR module is the crate root, as a Rust crate's <c>lib.rs</c> is: what stays in it is
  /// written in <c>src/lib.rs</c> itself. Also so where the module's name is <c>lib</c> or <c>main</c>,
  /// whose file would be the crate root's or a program's.
  /// </summary>
  public bool CrateRoot { get; init; }

  /// <summary>The functions that are tests, by IR name: <c>#[test]</c> functions of the <c>#[cfg(test)]</c> module, a test passing where the function returns.</summary>
  public IReadOnlyList<string> TestFunctions { get; init; } = [];

}

/// <summary>
/// A generated crate as syntax trees: <c>Cargo.toml</c> as text, <c>src/lib.rs</c>, the crate root,
/// and <c>src/&lt;module&gt;.rs</c>, the module that holds the translated functions.
/// </summary>
/// <param name="PackageName">The name of the package.</param>
/// <param name="Manifest">The text of <c>Cargo.toml</c>.</param>
/// <param name="Root">The crate root.</param>
/// <param name="ModuleName">The name of the module, which is also its file's name.</param>
/// <param name="Module">The module.</param>
public sealed record RustCrate(string PackageName, string Manifest, RsFile Root, string ModuleName, RsFile Module) {

  /// <summary><c>src/error.rs</c>, the crate's <c>Error</c> enum under the trap policy <c>result</c>, or <see langword="null"/> where the crate has none.</summary>
  public RsFile? Errors { get; init; }

  /// <summary>The modules the functions placed elsewhere are written into (<see cref="RustEmitOptions.Modules"/>), by name; empty where every function is in <see cref="Module"/>.</summary>
  public IReadOnlyList<RustModule> Modules { get; init; } = [];

  /// <summary>Whether <see cref="Module"/>'s items are written in the crate root, <c>src/lib.rs</c>, rather than a file of their own.</summary>
  public bool InRoot { get; init; }

}

/// <summary>
/// Emits an IR module as a Cargo package of safe Rust for a <see cref="RustProfile"/>: <c>Cargo.toml</c>
/// without dependencies, <c>src/lib.rs</c> with the header, <c>#![forbid(unsafe_code)]</c>, the lints
/// the code needs allowed and the module declaration, and <c>src/&lt;module&gt;.rs</c> with the
/// functions. Every operation is written in the one form that has the IR's meaning in every build
/// mode: the wrapping arithmetic as <c>wrapping_add</c>, the trapping one as
/// <c>checked_add(b).expect("IntegerOverflow")</c>, the saturating one as <c>saturating_add</c>, a
/// shift as <c>wrapping_shl</c>, never a bare <c>+</c> on integers, whose meaning depends on
/// <c>overflow-checks</c>. A trap is a panic whose message is the IR trap's name, which
/// <c>RustTraps</c> of the verification reads back. Control flow is structured as the IR is; a
/// counted loop becomes <c>for i in a..b</c>, a switch a <c>match</c>.
/// </summary>
public static partial class RustEmitter {

  /// <summary>The code for an IR construct the Rust profile cannot write.</summary>
  public const string Inexpressible = "PLY1550";

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.profile");

  /// <summary>The code for an exception region, which a crate that panics on a trap cannot catch.</summary>
  public const string ExceptionRegion = "PLY1560";

  /// <summary>The registered diagnostic of <see cref="ExceptionRegion"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExceptionRegionDescriptor = new(ExceptionRegion, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.exceptions");

  /// <summary>The code for two exported functions whose Rust names are the same.</summary>
  public const string ExportCollision = "PLY1574";

  /// <summary>The registered diagnostic of <see cref="ExportCollision"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExportCollisionDescriptor = new(ExportCollision, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.names");

  /// <summary>The folder of the backend, which the origin of its spelling functions names.</summary>
  public const string Folder = "Polypiler.Core.Backends.Rust";

  /// <summary>Emits a module for a profile, without the target passes.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Rust to write.</param>
  /// <returns><c>Cargo.toml</c>, <c>src/lib.rs</c> and the module's file.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, RustProfile profile)
    => Emit(module, profile, null, TargetPassOptions.None, null).Files;

  /// <summary>
  /// Emits a module: builds its crate, runs the target passes the options let run
  /// (<see cref="RustTargetPasses.Pipeline"/>) and prints it.
  /// </summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Rust to write.</param>
  /// <param name="capabilities">The table the forms are chosen from; the default one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <param name="options">The package and its tests; the defaults when <see langword="null"/>.</param>
  /// <returns>The files and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted.</exception>
  public static TargetEmission Emit(IrModule module, RustProfile profile, RustCapabilities? capabilities, TargetPassOptions passes, RustEmitOptions? options) {
    var (result, helpers) = Built(module, profile, capabilities, passes, options);
    return new(Files(result.Tree, helpers), result.Applications);
  }

  /// <summary>The crate of a module after the target passes: what <see cref="Emit(IrModule, RustProfile, RustCapabilities?, TargetPassOptions, RustEmitOptions?)"/> prints.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Rust to write.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <param name="options">The package and its tests.</param>
  /// <returns>The crate and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted.</exception>
  public static TargetPipelineResult<RustCrate> Build(IrModule module, RustProfile profile, RustCapabilities? capabilities, TargetPassOptions passes, RustEmitOptions? options)
    => Built(module, profile, capabilities, passes, options).Result;

  private static (TargetPipelineResult<RustCrate> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, RustProfile profile, RustCapabilities? capabilities, TargetPassOptions passes, RustEmitOptions? options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(passes);
    var problems = IrVerifier.Verify(module);
    var (writer, crate) = problems.Count > 0 ? (null, null) : Written(module, profile, capabilities ?? RustCapabilities.Default, options ?? RustEmitOptions.Default);
    if (problems.Concat(writer?.Problems ?? []).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    var result = RustTargetPasses.Pipeline.Run(crate!, profile, passes);
    // The modules are made after the passes, which see one module; Check refused what cannot be placed.
    var placed = Modules.Split(Lints.Allowed(result.Tree), writer!.Placement, writer.InRoot);
    return (result with { Tree = placed }, writer.Helpers);
  }

  /// <summary>Says why a module cannot be emitted for a profile; empty when it can.</summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The Rust to write.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <param name="options">The package and its tests.</param>
  /// <returns>The findings of <see cref="IrVerifier"/>, or one diagnostic per construct the profile cannot write.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, RustProfile profile, RustCapabilities? capabilities = null, RustEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    return Written(module, profile, capabilities ?? RustCapabilities.Default, options ?? RustEmitOptions.Default).Writer.Problems;
  }

  /// <summary>The files of a crate: <c>Cargo.toml</c>, <c>src/lib.rs</c>, <c>src/&lt;module&gt;.rs</c>.</summary>
  /// <param name="crate">The crate.</param>
  /// <param name="helpers">The helpers the module carried before the passes, their texts still empty.</param>
  /// <returns>The files.</returns>
  public static IReadOnlyList<GeneratedFile> Files(RustCrate crate, IReadOnlyList<HelperText>? helpers = null) {
    ArgumentNullException.ThrowIfNull(crate);
    var module = RustPrinter.Print(crate.Module);
    List<GeneratedFile> files = [
      new GeneratedFile("Cargo.toml", crate.Manifest),
      new GeneratedFile("src/lib.rs", RustPrinter.Print(crate.Root))
    ];
    // Where every function went into a module of its own, the IR module's file has nothing left.
    if (!crate.InRoot && (crate.Modules.Count == 0 || crate.Module.Items.Count > 0))
      files.Add(new GeneratedFile($"src/{crate.ModuleName}.rs", module) { Helpers = Measured(crate.Module, helpers ?? []) });

    files.AddRange(crate.Modules.Select(placed => new GeneratedFile(placed.FilePath, RustPrinter.Print(placed.File))));
    if (crate.Errors is { } errors)
      files.Add(new GeneratedFile("src/error.rs", RustPrinter.Print(errors)));

    return files;
  }

  /// <summary>The helpers of a module's file, measured in it, each printed on its own; one inlined everywhere and removed is listed with no text.</summary>
  private static IReadOnlyList<EmittedHelper> Measured(RsFile module, IReadOnlyList<HelperText> helpers) {
    var texts = module.Items.OfType<RsFunction>().ToDictionary(function => function.Name, RustPrinter.Print, StringComparer.Ordinal);
    var file = string.Concat(module.Items.Select(RustPrinter.Print));
    return EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = texts.GetValueOrDefault(helper.Name, "") })], file);
  }

  /// <summary>
  /// Whether the profile writes an intrinsic itself, as a method of the standard library; every other
  /// one legalization replaces by a helper of the IR library or a portable implementation.
  /// </summary>
  /// <param name="intrinsic">The intrinsic.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when the emitter writes it.</returns>
  public static bool Writes(IrIntrinsic intrinsic, RustProfile profile, RustCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    var table = capabilities ?? RustCapabilities.Default;
    if (intrinsic.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig)
      return true;
    if (intrinsic.Arguments.Count == 0)
      return false;

    var operand = intrinsic.Arguments[0].Type;
    if (operand.IsInteger && !operand.IsEnum)
      return intrinsic.Op switch {
        IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount
          or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.ByteSwap or IrIntrinsicOp.Min or IrIntrinsicOp.Max
          or IrIntrinsicOp.AddSaturate or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate
          or IrIntrinsicOp.AddOverflows or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows => true,
        IrIntrinsicOp.BitReverse or IrIntrinsicOp.ReverseBitsInBytes => table.Has(RustCapabilities.ReverseBits, profile),
        IrIntrinsicOp.LeadingOneCount or IrIntrinsicOp.TrailingOneCount => table.Has(RustCapabilities.CountOnes, profile),
        IrIntrinsicOp.IsPow2 => !operand.IsSigned,
        IrIntrinsicOp.Abs or IrIntrinsicOp.AbsWrap => operand.IsSigned && table.Has(RustCapabilities.CheckedAbs, profile),
        IrIntrinsicOp.BitsFloat => table.Has(RustCapabilities.FloatBits, profile),
        _ => false
      };

    if (!operand.IsFloat)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Sqrt or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate or IrIntrinsicOp.RoundAway or IrIntrinsicOp.Abs
        or IrIntrinsicOp.FusedMultiplyAdd or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity or IrIntrinsicOp.IsFinite
        or IrIntrinsicOp.IsNegative or IrIntrinsicOp.IsNormal or IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32 => true,
      IrIntrinsicOp.RoundEven => table.Has(RustCapabilities.RoundTiesEven, profile),
      IrIntrinsicOp.CopySign => table.Has(RustCapabilities.CopySign, profile),
      IrIntrinsicOp.IsSubnormal => table.Has(RustCapabilities.IsSubnormal, profile),
      IrIntrinsicOp.FloatBits => table.Has(RustCapabilities.FloatBits, profile),
      _ => false
    };
  }

  /// <summary>
  /// The module with every operation the profile does not write itself (<see cref="Writes"/>) replaced:
  /// a floating-point operation by its portable implementation, an integer or bit operation by its
  /// helper of the IR library (<see cref="IntegerLibrary"/>), the floating-point operations Rust has no
  /// method of the IR's meaning for - the IEEE minimum and maximum, the sign that traps on a NaN, the
  /// rounding to even below Rust 1.77, ECMAScript's rounding, the clamp and the
  /// rounding to digits that trap - by theirs. Each helper is copied into the module.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, RustProfile profile, RustCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(module);
    var table = capabilities ?? RustCapabilities.Default;
    var floats = FloatLegalization.Rewrite(intrinsic => Writes(intrinsic, profile, table));
    LibraryRewrite helpers = (expression, calls) => expression is IrIntrinsic intrinsic && !Writes(intrinsic, profile, table) ? Library(intrinsic, calls) : null;
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => floats(expression, calls) ?? helpers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>The helper of the library for an intrinsic the profile does not write, or <see langword="null"/> where there is none.</summary>
  private static IrExpr? Library(IrIntrinsic intrinsic, LibraryCalls calls) {
    if (intrinsic.Arguments.Count == 0)
      return null;

    var operand = intrinsic.Arguments[0].Type;
    if (!operand.IsFloat)
      return IntegerLibrary.Operand(intrinsic).IsInteger ? IntegerLibrary.Call(intrinsic, calls) : null;

    var single = operand == IrType.F32;
    var name = intrinsic.Op switch {
      IrIntrinsicOp.Min => single ? "MinimumSingle" : "MinimumDouble",
      IrIntrinsicOp.Max => single ? "MaximumSingle" : "MaximumDouble",
      IrIntrinsicOp.Sign => "SignOf",
      IrIntrinsicOp.RoundEven => "RoundEven",
      IrIntrinsicOp.RoundHalfUp => "RoundHalfUp",
      IrIntrinsicOp.Clamp => single ? "ClampF" : "Clamp",
      IrIntrinsicOp.RoundDigits => single ? "RoundDigitsF" : "RoundDigits",
      _ => null
    };
    return name is null ? null : calls.Call(name, intrinsic.Type, intrinsic.Arguments);
  }

  /// <summary>The Rust type of an IR scalar, or <see langword="null"/> for any other type.</summary>
  /// <param name="type">The IR type.</param>
  public static string? TypeName(IrType type) => type.IsEnum ? null : type.Kind switch {
    IrTypeKind.Bool => "bool",
    IrTypeKind.I8 => "i8",
    IrTypeKind.U8 => "u8",
    IrTypeKind.I16 => "i16",
    IrTypeKind.U16 => "u16",
    IrTypeKind.I32 => "i32",
    IrTypeKind.U32 => "u32",
    IrTypeKind.I64 => "i64",
    IrTypeKind.U64 => "u64",
    IrTypeKind.F32 => "f32",
    IrTypeKind.F64 => "f64",
    _ => null
  };

  /// <summary>
  /// A constant as a Rust literal: an integer in decimal, the smallest value of a signed type as
  /// <c>i32::MIN</c>; a floating-point number as the shortest literal that reads back to its bits,
  /// the infinities and the canonical NaN as <c>f64::INFINITY</c>, <c>f64::NEG_INFINITY</c> and
  /// <c>f64::NAN</c>, any other NaN as <c>f64::from_bits(…)</c>.
  /// </summary>
  /// <param name="constant">The constant, of a scalar type.</param>
  /// <returns>The literal, with the IR type.</returns>
  public static RsExpression Literal(IrConst constant) {
    ArgumentNullException.ThrowIfNull(constant);
    var type = constant.Type;
    var name = TypeName(type)!;
    if (type == IrType.Bool)
      return new RsLiteral(constant.Value != 0 ? "true" : "false") { Type = type };

    if (type.IsInteger) {
      var value = IrOperators.Number(type, constant.Value);
      if (type.IsSigned && value == IrOperators.Minimum(type))
        return new RsPath(name + "::MIN") { Type = type };

      return new RsLiteral(value.ToString(CultureInfo.InvariantCulture)) { Type = type };
    }

    var number = IrOperators.Float(type, constant.Value);
    if (double.IsNaN(number)) {
      var canonical = type == IrType.F32 ? 0x7FC00000L : 0x7FF8000000000000L;
      return (constant.Value & (type == IrType.F32 ? 0xFFFFFFFFL : -1L)) == canonical
        ? new RsPath(name + "::NAN") { Type = type }
        : new RsCall(new RsPath(name + "::from_bits"), [new RsLiteral("0x" + constant.Value.ToString(type == IrType.F32 ? "x8" : "x16", CultureInfo.InvariantCulture)[(type == IrType.F32 ? ^8 : ^16)..])]) { Type = type };
    }

    if (double.IsInfinity(number))
      return new RsPath(name + (number > 0 ? "::INFINITY" : "::NEG_INFINITY")) { Type = type };

    var text = type == IrType.F32 ? ((float)number).ToString("R", CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
    text = text.Replace("E+", "e", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
    if (!text.Contains('.') && !text.Contains('e'))
      text += ".0";
    else if (!text.Contains('.') && text.Contains('e'))
      text = text.Insert(text.IndexOf('e'), ".0");

    // "R" keeps the sign of a negative zero: -0.0 is "-0", written "-0.0".
    return new RsLiteral(text) { Type = type };
  }

  /// <summary>The text of <c>Cargo.toml</c>: the package, its edition and, where Cargo reads it, its MSRV; no dependencies.</summary>
  /// <param name="name">The package name.</param>
  /// <param name="version">The package version.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The table.</param>
  public static string Manifest(string name, string version, RustProfile profile, RustCapabilities capabilities) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(capabilities);
    var text = new StringBuilder();
    text.Append("[package]\n");
    text.Append("name = \"").Append(name).Append("\"\n");
    text.Append("version = \"").Append(version).Append("\"\n");
    text.Append("edition = \"").Append(profile.Edition.ToString(CultureInfo.InvariantCulture)).Append("\"\n");
    if (capabilities.Has(RustCapabilities.RustVersionKey, profile))
      text.Append("rust-version = \"").Append(profile.RustVersionName).Append("\"\n");

    return text.ToString();
  }

  /// <summary>
  /// The public API of a module's crate: every exported IR function with its path below the crate root,
  /// <c>&lt;module&gt;::&lt;function&gt;</c>, in the order of the module. An export whose name collides
  /// with an earlier one is left out; emission refuses it.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="profile">The profile, whose keywords are escaped.</param>
  /// <returns>The path of each exported function by its IR name.</returns>
  public static IReadOnlyDictionary<string, string> Exports(IrModule module, RustProfile profile) => Exports(module, profile, null);

  /// <summary>The path of each exported function in the crate of a module whose functions are placed in modules as the options say.</summary>
  /// <param name="module">The module.</param>
  /// <param name="profile">The profile, whose keywords are escaped.</param>
  /// <param name="options">The placement of the functions (<see cref="RustEmitOptions.Modules"/>); none when <see langword="null"/>.</param>
  /// <returns>The path of each exported function by its IR name.</returns>
  public static IReadOnlyDictionary<string, string> Exports(IrModule module, RustProfile profile, RustEmitOptions? options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    var moduleName = ModuleName(module.Name, profile);
    var placement = options?.Modules ?? RustEmitOptions.Default.Modules;
    var exports = new Dictionary<string, string>(StringComparer.Ordinal);
    var taken = new HashSet<string>(StringComparer.Ordinal);
    var generalized = GeneralizedExports(module, profile.Edition);
    var root = options?.CrateRoot == true || moduleName is "lib" or "main";
    var names = module.Functions.Select(function => RustNames.Snake(function.Name, profile.Edition)).ToHashSet(StringComparer.Ordinal);
    foreach (var function in module.Functions.Where(function => function.IsPublic)) {
      if (generalized.TryGetValue(function.Name, out var generic)) {
        exports[function.Name] = root ? generic : $"{moduleName}::{generic}";
        continue;
      }

      var name = RustNames.Snake(function.Name, profile.Edition);
      if (!taken.Add(name))
        continue;

      if (placement.TryGetValue(function.Name, out var placed)) {
        var path = PlacedPath(placed, profile);
        exports[function.Name] = $"{path}::{(Unprefixed(name, path) is { } own && !names.Contains(own) ? own : name)}";
      } else {
        exports[function.Name] = root ? name : $"{moduleName}::{name}";
      }
    }

    return exports;
  }

  /// <summary>
  /// Whether a parameter of a function takes null, which makes it an <c>Option</c>: an array the module
  /// tests for null or gives null (<c>Option&lt;&amp;[T]&gt;</c>), an optional value (<c>Option&lt;T&gt;</c>).
  /// </summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="function">The IR name of the function.</param>
  /// <param name="parameter">The position of the parameter.</param>
  /// <returns><see langword="true"/> when the parameter is an <c>Option</c>.</returns>
  public static bool TakesNull(IrModule module, string function, int parameter) {
    ArgumentNullException.ThrowIfNull(module);
    var lowered = Polypiler.Core.Backends.Rust.Emit.ObjectLowering.Run(Polypiler.Core.Backends.Rust.Emit.BindHoisting.Run(module));
    var local = lowered.Functions.First(candidate => candidate.Name == function).Parameters[parameter];
    return local.Type.IsOptional || local.Type.IsArray && Polypiler.Core.Backends.Rust.Emit.ArrayNullness.Of(lowered).MayBeNull(function, local);
  }

  /// <summary>A module's path as Rust names it, each segment a module's name: <c>Util::Bits</c> as <c>util::bits</c>.</summary>
  /// <param name="path">The path from the crate root, segments separated by <c>::</c>.</param>
  /// <param name="profile">The profile, whose keywords are avoided.</param>
  public static string PlacedPath(string path, RustProfile profile) {
    ArgumentNullException.ThrowIfNull(path);
    return string.Join("::", path.Split("::").Select(segment => ModuleName(segment, profile)));
  }

  /// <summary>A function's Rust name without the path of its module in front, <c>util_bits_mix</c> in <c>util::bits</c> as <c>mix</c>; <see langword="null"/> where it has no such prefix.</summary>
  private static string? Unprefixed(string name, string path) {
    var prefix = path.Replace("::", "_", StringComparison.Ordinal) + "_";
    return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : null;
  }

  /// <summary>The name of a package or module of an IR module's name, as Cargo and the guidelines want it.</summary>
  /// <param name="name">The IR name.</param>
  /// <param name="profile">The profile, whose keywords are avoided.</param>
  public static string ModuleName(string name, RustProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    var snake = RustNames.Snake(name, profile.Edition);
    return snake.StartsWith("r#", StringComparison.Ordinal) ? snake[2..] + "_" : snake;
  }

}
