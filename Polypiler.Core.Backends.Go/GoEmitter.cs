using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Go.Passes;
using Polypiler.Core.Backends.Go.Printing;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Go;

/// <summary>What a Go module is laid out as, beyond the profile.</summary>
public sealed record GoEmitOptions {

  /// <summary>
  /// The module path <c>go.mod</c> declares, whose last element (before a major-version suffix such
  /// as <c>/v2</c>) names the package; <see langword="null"/> for the name of the IR module in lower case.
  /// </summary>
  public string? ModulePath { get; init; }

}

/// <summary>
/// Emits one Go module per IR module, for the Go a <see cref="GoProfile"/> names: <c>go.mod</c>
/// with the profile's language version and no requirement, one package file with the module's
/// declarations, and <c>polyrt.go</c> with the helpers the package calls, if any. Every integer type
/// of the IR is the sized type of Go of the same width and signedness, whose arithmetic wraps as the
/// IR's does; <c>f32</c> and <c>f64</c> are <c>float32</c> and <c>float64</c>; truth values are
/// <c>bool</c>; arrays are slices; structs are struct types with exported fields; strings are Go
/// strings. What Go does differently is written out: a shift's count is masked to the IR's width, a
/// signed division traps where the quotient does not fit, a product in a floating-point sum is
/// converted to forbid fusion, a conversion of a floating-point number to an integer saturates or
/// traps through a helper, a constant expression is folded with the IR's semantics before Go folds it
/// exactly. Control flow is Go's: <c>if</c>/<c>else if</c>, the forms of <c>for</c>, <c>switch</c>
/// without <c>fallthrough</c>. The output is in gofmt's layout, declares nothing it does not use and
/// imports only the standard library. A trap is a panic; the text of one the IR's helpers raise is the
/// trap's (<c>docs/reference/exceptions.md</c>).
/// </summary>
public static partial class GoEmitter {

  /// <summary>The code for a type that has no Go representation in the chosen profile.</summary>
  public const string TypeUnsupported = "PLY2150";

  /// <summary>The code for an IR operation that cannot be written as Go.</summary>
  public const string Inexpressible = "PLY2151";

  /// <summary>The code for a control construct that has no Go form.</summary>
  public const string ControlUnsupported = "PLY2160";

  /// <summary>The code for a feature of a function Go has no form of.</summary>
  public const string FunctionUnsupported = "PLY2170";

  /// <summary>The code for functions whose Go names collide: Go has no overloading.</summary>
  public const string NameCollision = "PLY2172";

  /// <summary>The code for a setting of the layout that is missing or invalid.</summary>
  public const string SettingInvalid = "PLY2173";

  /// <summary>The registered diagnostic of <see cref="TypeUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypeUnsupportedDescriptor = new(TypeUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.backend.type");

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.backend.operation");

  /// <summary>The registered diagnostic of <see cref="ControlUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupportedDescriptor = new(ControlUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.backend.control");

  /// <summary>The registered diagnostic of <see cref="FunctionUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor FunctionUnsupportedDescriptor = new(FunctionUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.backend.function");

  /// <summary>The registered diagnostic of <see cref="NameCollision"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NameCollisionDescriptor = new(NameCollision, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.backend.names");

  /// <summary>The registered diagnostic of <see cref="SettingInvalid"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SettingInvalidDescriptor = new(SettingInvalid, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "profile setting '{0}' is missing or invalid: {1}", "go.backend.settings");

  /// <summary>The runtime the header names for the floating-point record, as <c>tooling/toolchains.lock</c> keys it.</summary>
  public const string Runtime = "go";

  /// <summary>The file the helpers of a package are written to.</summary>
  public const string RuntimeFile = "polyrt.go";

  /// <summary>The file of the module's layout.</summary>
  public const string ModuleFile = "go.mod";

  /// <summary>The name of the package file of a module: <c>&lt;module in snake_case&gt;.go</c>.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The file name.</returns>
  public static string FileName(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return GoNaming.File(module.Name);
  }

  /// <summary>The module path of a module: the one the options name, or its name in lower case.</summary>
  /// <param name="module">The module.</param>
  /// <param name="options">The options, or <see langword="null"/>.</param>
  /// <returns>The path.</returns>
  public static string ModulePath(IrModule module, GoEmitOptions? options) {
    ArgumentNullException.ThrowIfNull(module);
    return options?.ModulePath ?? GoNaming.Package(module.Name);
  }

  /// <summary>The name of the package of a module: the last element of its module path.</summary>
  /// <param name="module">The module.</param>
  /// <param name="options">The options, or <see langword="null"/>.</param>
  /// <returns>The name.</returns>
  public static string PackageName(IrModule module, GoEmitOptions? options = null) => GoNaming.Package(ModulePath(module, options));

  /// <summary>Emits a module for the default profile, printed as built.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <returns>The generated files.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Go.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, GoProfile.Default);

  /// <summary>Emits a module for a profile, printed as built, without the target passes.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Go to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated files.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Go.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, GoProfile profile, GoCapabilities? capabilities = null)
    => Emit(module, profile, FloatMode.Accurate, capabilities, TargetPassOptions.None, null).Files;

  /// <summary>
  /// Emits a module: builds its syntax tree, runs the target passes the options let run
  /// (<see cref="GoTargetPasses.Pipeline"/>), splits the helpers into <see cref="RuntimeFile"/> and
  /// prints both files and <c>go.mod</c>.
  /// </summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Go to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <param name="options">The layout, or <see langword="null"/> for the default one.</param>
  /// <returns>The generated files and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Go.</exception>
  public static TargetEmission Emit(IrModule module, GoProfile profile, FloatMode mode, GoCapabilities? capabilities, TargetPassOptions passes, GoEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var (result, helpers) = Built(module, profile, mode, capabilities, passes, options);
    var (source, runtime) = Split(result.Tree);
    var files = new List<GeneratedFile> { new(ModuleFile, $"module {ModulePath(module, options)}\n\ngo {profile.Version}\n") };
    var text = GoPrinter.Print(source);
    files.Add(new GeneratedFile(FileName(module), text));
    if (runtime is not null) {
      var helperText = GoPrinter.Print(runtime);
      files.Add(new GeneratedFile(RuntimeFile, helperText) { Helpers = Measured(runtime, helpers, helperText, text) });
    }

    return new(files, result.Applications);
  }

  /// <summary>The syntax tree of a module after the target passes the options let run, helpers and declarations in one file: what <see cref="Emit(IrModule, GoProfile, FloatMode, GoCapabilities?, TargetPassOptions, GoEmitOptions?)"/> splits and prints.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Go to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <param name="options">The layout, or <see langword="null"/> for the default one.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Go.</exception>
  public static TargetPipelineResult<GoFile> Build(IrModule module, GoProfile profile, FloatMode mode, GoCapabilities? capabilities, TargetPassOptions passes, GoEmitOptions? options = null)
    => Built(module, profile, mode, capabilities, passes, options).Result;

  /// <summary>The package file and the file of the helpers of a tree; the latter <see langword="null"/> without helpers.</summary>
  /// <param name="tree">The tree, helpers and declarations in one file.</param>
  /// <returns>Both files, each importing what it uses.</returns>
  public static (GoFile Source, GoFile? Runtime) Split(GoFile tree) {
    ArgumentNullException.ThrowIfNull(tree);
    var own = tree.Declarations.Where(declaration => declaration is not GoFuncDecl { IsHelper: true }).ToList();
    var helpers = tree.Declarations.Except(own).ToList();
    static bool Licence(string line) => line.StartsWith("Helpers added by", StringComparison.Ordinal);
    var source = tree with { Header = [.. tree.Header.Where(line => !Licence(line))], Declarations = own };
    source = source with { Imports = Writer.Imports(source) };
    if (helpers.Count == 0)
      return (source, null);

    var runtime = new GoFile(["Code generated by polypiler. DO NOT EDIT.", "", "The helpers of the package: what Go has no expression for.", .. tree.Header.Where(Licence)], tree.Package, [], helpers);
    return (source, runtime with { Imports = Writer.Imports(runtime) });
  }

  private static (TargetPipelineResult<GoFile> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, GoProfile profile, FloatMode mode, GoCapabilities? capabilities, TargetPassOptions passes, GoEmitOptions? options) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(passes);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module).Concat(Settings(module, options)).ToList();
    var writer = new Writer(profile, capabilities ?? GoCapabilities.Standard, mode, PackageName(module, options));
    var tree = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return (GoTargetPasses.Pipeline.Run(tree!, profile, passes), writer.HelperTexts);
  }

  /// <summary>What is wrong with the settings of the layout: a module path the <c>go</c> command would refuse.</summary>
  private static IEnumerable<Diagnostic> Settings(IrModule module, GoEmitOptions? options) {
    if (options?.ModulePath is { } path && !GoNaming.IsModulePath(path))
      yield return SettingInvalidDescriptor.At($"profile setting 'modulePath' is missing or invalid: '{path}' is no module path the go command accepts");
  }

  /// <summary>The helpers of the file of helpers, measured in its text; a helper the passes removed is left out.</summary>
  private static IReadOnlyList<EmittedHelper> Measured(GoFile runtime, IReadOnlyList<HelperText> helpers, string helperText, string source) {
    var texts = runtime.Declarations.OfType<GoFuncDecl>().ToDictionary(function => function.Name, GoPrinter.Print, StringComparer.Ordinal);
    var kept = helpers.Where(helper => texts.ContainsKey(helper.Name)).Select(helper => helper with { Text = texts[helper.Name].TrimEnd('\n') }).ToList();
    return EmittedHelper.Measure(kept, helperText + source);
  }

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The Go to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="options">The layout, or <see langword="null"/> for the default one.</param>
  /// <returns>
  /// The findings of <see cref="IrVerifier"/> for an ill-typed module; otherwise one diagnostic per
  /// type, operation, control construct, function feature, name or setting Go cannot carry. Empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, GoProfile profile, GoCapabilities? capabilities = null, GoEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var settings = Settings(module, options).ToList();
    if (settings.Count > 0)
      return settings;

    var writer = new Writer(profile, capabilities ?? GoCapabilities.Standard, FloatMode.Accurate, PackageName(module, options));
    writer.Module(module);
    return writer.Problems;
  }

  /// <summary>
  /// Whether the backend writes a floating-point operation of the table itself: only those that have
  /// no portable implementation - the square root, which <c>math.Sqrt</c> rounds correctly, the
  /// roundings, the sign, minimum and maximum, and the bit casts. Every other operation of the table is
  /// its portable implementation, in both modes, so that the bits are the same on every <c>GOARCH</c>.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <returns>Whether the backend writes it.</returns>
  public static bool Writes(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    return !FloatLegalization.IsReplaceable(intrinsic);
  }

  /// <summary>
  /// The module with every floating-point operation the backend does not write replaced by its
  /// portable implementation and every integer operation Go has no form of replaced by the call of its
  /// helper of the library (<see cref="GoLibrary"/>), each copied into the module.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode; Go writes the same in both.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, GoProfile profile, FloatMode mode, GoCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(profile);
    var floats = FloatLegalization.Rewrite(Writes);
    var helpers = GoLibrary.Rewrite();
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => floats(expression, calls) ?? helpers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>
  /// The Go name of every function of a module, by its name in the IR: a public function's is
  /// exported, what a caller of the package calls; any other's is not.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyDictionary<string, string> FunctionNames(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new Writer(GoProfile.Default, GoCapabilities.Standard, FloatMode.Accurate, PackageName(module)).Names(module);
  }

}
