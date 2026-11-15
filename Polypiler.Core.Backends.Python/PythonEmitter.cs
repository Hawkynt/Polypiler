using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Passes;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Python;

/// <summary>
/// Emits one Python module per IR module, for the Python a <see cref="PythonProfile"/> names. Every
/// function is typed (PEP 484): integers of every width are <c>int</c>, a signed one holding its
/// signed value and an unsigned one its non-negative value; <c>f32</c> and <c>f64</c> are
/// <c>float</c>, an <c>f32</c> always exactly a binary32 number; truth values are <c>bool</c>;
/// arrays are <c>list</c>. Python's <c>int</c> never wraps, so fixed-width arithmetic is written
/// with a mask (unsigned) or a mask and a sign-wrap (signed); its <c>//</c> and <c>%</c> floor, so
/// the IR's truncating division is a helper; its float division raises, so IEEE 754 division is a
/// helper. Control flow is structured Python: <c>if</c>/<c>elif</c>, <c>while</c>, counted loops as
/// <c>for … in range(…)</c>, post-test loops as <c>while True:</c> with the test at the end, and
/// switches as <c>match</c> from Python 3.10 or as an <c>if</c>/<c>elif</c> chain before. Every
/// public function is listed in <c>__all__</c>; everything else is private behind a leading
/// underscore. The module imports nothing outside the standard library.
/// </summary>
public static partial class PythonEmitter {

  /// <summary>The code for a type that has no Python representation in the chosen profile.</summary>
  public const string TypeUnsupported = "PLY1850";

  /// <summary>The code for an IR construct that cannot be written as Python in the chosen profile.</summary>
  public const string Inexpressible = "PLY1851";

  /// <summary>The code for a control construct that has no Python form.</summary>
  public const string ControlUnsupported = "PLY1858";

  /// <summary>The code for public functions whose Python names collide, or a name nothing is left of.</summary>
  public const string NameCollision = "PLY1874";

  /// <summary>The registered diagnostic of <see cref="TypeUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypeUnsupportedDescriptor = new(TypeUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.backend.type");

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.backend.profile");

  /// <summary>The registered diagnostic of <see cref="ControlUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupportedDescriptor = new(ControlUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.backend.control");

  /// <summary>The registered diagnostic of <see cref="NameCollision"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NameCollisionDescriptor = new(NameCollision, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.backend.names");

  /// <summary>The runtime the header names for the floating-point record, as <c>tooling/toolchains.lock</c> keys it.</summary>
  public const string Runtime = "python";

  /// <summary>Every helper of the backend a module can carry, by its name, with its licence and origin.</summary>
  public static IReadOnlyDictionary<string, (string Licence, string Origin)> HelperOrigins { get; }
    = PythonHelpers.Order.ToDictionary(name => name, _ => (EmittedHelper.OwnLicence, EmittedHelper.OriginOf(PythonHelpers.Folder)), StringComparer.Ordinal);

  /// <summary>The name of the file a module is written to: <c>&lt;module in snake_case&gt;.py</c>.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The file name.</returns>
  public static string FileName(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return PythonNaming.Module(module.Name) + ".py";
  }

  /// <summary>Emits a module for the default profile, printed as built.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Python.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, PythonProfile.Default);

  /// <summary>Emits a module for a profile, printed as built, without the target passes.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Python to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Python.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, PythonProfile profile, PythonCapabilities? capabilities = null)
    => Emit(module, profile, FloatMode.Accurate, capabilities, TargetPassOptions.None).Files;

  /// <summary>
  /// Emits a module: builds its syntax tree, runs the target passes the options let run
  /// (<see cref="PythonTargetPasses.Pipeline"/>) and prints the tree.
  /// </summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Python to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The generated file and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Python.</exception>
  public static TargetEmission Emit(IrModule module, PythonProfile profile, FloatMode mode, PythonCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(module);
    var (result, helpers) = Built(module, profile, mode, capabilities, passes);
    var text = PythonPrinter.Print(result.Tree);
    return new([new GeneratedFile(FileName(module), text) { Helpers = Measured(result.Tree, helpers, text) }], result.Applications);
  }

  /// <summary>The syntax tree of a module after the target passes the options let run: what <see cref="Emit(IrModule, PythonProfile, FloatMode, PythonCapabilities?, TargetPassOptions)"/> prints.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Python to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Python.</exception>
  public static TargetPipelineResult<PyModule> Build(IrModule module, PythonProfile profile, FloatMode mode, PythonCapabilities? capabilities, TargetPassOptions passes)
    => Built(module, profile, mode, capabilities, passes).Result;

  private static (TargetPipelineResult<PyModule> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, PythonProfile profile, FloatMode mode, PythonCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(passes);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    var writer = new Writer(profile, capabilities ?? PythonCapabilities.Standard, mode);
    var tree = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return (PythonTargetPasses.Pipeline.Run(tree!, profile, passes), writer.Helpers);
  }

  /// <summary>The helpers of a file, measured in the text of their declarations; a helper the passes removed is listed with no text.</summary>
  private static IReadOnlyList<EmittedHelper> Measured(PyModule tree, IReadOnlyList<HelperText> helpers, string file) {
    var texts = tree.Body.Where(statement => statement is PyFunctionDef or PyClassDef)
      .ToDictionary(statement => statement is PyFunctionDef function ? function.Name : ((PyClassDef)statement).Name, PythonPrinter.Print, StringComparer.Ordinal);
    return EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = texts.GetValueOrDefault(helper.Name, "") })], file);
  }

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The Python to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>
  /// The findings of <see cref="IrVerifier"/> for an ill-typed module; otherwise one diagnostic per
  /// type, operation, control construct or name Python cannot carry. Empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, PythonProfile profile, PythonCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile, capabilities ?? PythonCapabilities.Standard, FloatMode.Accurate);
    writer.Module(module);
    return writer.Problems;
  }

  /// <summary>
  /// Whether the backend writes a floating-point operation of the table itself: only those that have
  /// no portable implementation - the square root, which <c>math.sqrt</c> rounds correctly, the
  /// roundings, the sign, minimum and maximum, and the bit casts. Every other operation of the table
  /// is its portable implementation, in both modes: no function of Python's <c>math</c> module has a
  /// measured error that ADR 0005 admits, and <c>math.fma</c> (3.13) raises on an overflow or an
  /// invalid operation where IEEE 754 gives an infinity or a NaN.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <returns>Whether the backend writes it.</returns>
  public static bool Writes(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    return !FloatLegalization.IsReplaceable(intrinsic);
  }

  /// <summary>
  /// The module with every floating-point operation the backend does not write replaced by its
  /// portable implementation and every integer operation Python has no form of replaced by the call
  /// of its helper of the library (<see cref="PythonLibrary"/>), each copied into the module.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode; Python writes the same in both.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, PythonProfile profile, FloatMode mode, PythonCapabilities? capabilities = null) {
    var table = capabilities ?? PythonCapabilities.Standard;
    var floats = FloatLegalization.Rewrite(Writes);
    var helpers = PythonLibrary.Rewrite(profile, table);
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => floats(expression, calls) ?? helpers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>
  /// The module with every integer operation Python has no form of replaced by the call of its helper
  /// of the library; the floating-point operations stay, for <see cref="Legalize"/> to replace.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule LegalizeHelpers(IrModule module, Func<IrModule> implementations, PythonProfile profile, PythonCapabilities? capabilities = null)
    => LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => PythonLibrary.Rewrite(profile, capabilities ?? PythonCapabilities.Standard)(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));

  /// <summary>
  /// The Python name of every function of a module, by its name in the IR: a public function's is
  /// what a caller imports and what <c>__all__</c> lists, a private one's starts with an underscore.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyDictionary<string, string> FunctionNames(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new Writer(PythonProfile.Default, PythonCapabilities.Standard, FloatMode.Accurate).Names(module);
  }

}
