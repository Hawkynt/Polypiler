using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Passes;
using Polypiler.Core.Backends.Kotlin.Printing;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// Emits one Kotlin file per IR module, for the Kotlin a <see cref="KotlinProfile"/> names. Every IR
/// type has a Kotlin type of its own width: <c>i8</c> to <c>i64</c> are <c>Byte</c>, <c>Short</c>,
/// <c>Int</c>, <c>Long</c>, <c>u8</c> to <c>u64</c> the unsigned <c>UByte</c> to <c>ULong</c>,
/// <c>f32</c> and <c>f64</c> <c>Float</c> and <c>Double</c>, arrays the primitive arrays of the signed
/// type of the width (an unsigned element is converted where it is read and written, as Kotlin's
/// unsigned arrays need an opt-in). Kotlin's <c>Int</c> and <c>Long</c> wrap as the IR does, its shifts
/// mask the count to five or six bits, its integer division truncates and throws on a zero divisor;
/// what differs is written out: arithmetic of 8 and 16 bits is computed in <c>Int</c> and converted
/// back, the one quotient that does not fit traps through a helper, and a checked operation is the
/// <c>Math.*Exact</c> method or a helper. Control flow is structured Kotlin with labels where a jump
/// leaves more than the innermost loop. A trap is the throw of the JVM exception of the trap table
/// (<c>docs/reference/exceptions.md</c>, "Kotlin"). The file imports nothing outside the standard
/// library and <c>java.lang</c>.
/// </summary>
public static partial class KotlinEmitter {

  /// <summary>The code for a type that has no Kotlin representation in the chosen profile.</summary>
  public const string TypeUnsupported = "PLY1650";

  /// <summary>The code for an operation of the IR that cannot be written as Kotlin in the chosen profile.</summary>
  public const string Inexpressible = "PLY1651";

  /// <summary>The code for a control construct that has no Kotlin form.</summary>
  public const string ControlUnsupported = "PLY1658";

  /// <summary>The code for a construct of memory - views, pointers, layouts - that has no Kotlin form.</summary>
  public const string MemoryUnsupported = "PLY1666";

  /// <summary>The code for public functions whose Kotlin names collide, or a name nothing is left of.</summary>
  public const string NameCollision = "PLY1674";

  /// <summary>The code for a declared type - a class, an interface, a struct - the backend does not write.</summary>
  public const string DeclarationUnsupported = "PLY1682";

  /// <summary>The registered diagnostic of <see cref="TypeUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypeUnsupportedDescriptor = new(TypeUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.type");

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.operation");

  /// <summary>The registered diagnostic of <see cref="ControlUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupportedDescriptor = new(ControlUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.control");

  /// <summary>The registered diagnostic of <see cref="MemoryUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MemoryUnsupportedDescriptor = new(MemoryUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.memory");

  /// <summary>The registered diagnostic of <see cref="NameCollision"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NameCollisionDescriptor = new(NameCollision, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.names");

  /// <summary>The registered diagnostic of <see cref="DeclarationUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DeclarationUnsupportedDescriptor = new(DeclarationUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.backend.types");

  /// <summary>The runtime the header names for the floating-point record, as <c>tooling/toolchains.lock</c> keys it.</summary>
  public const string Runtime = "jdk";

  /// <summary>Every helper of the backend a file can carry, by its name, with its licence and origin.</summary>
  public static IReadOnlyDictionary<string, (string Licence, string Origin)> HelperOrigins { get; }
    = KotlinHelpers.Names.ToDictionary(name => name, _ => (EmittedHelper.OwnLicence, EmittedHelper.OriginOf(KotlinHelpers.Folder)), StringComparer.Ordinal);

  /// <summary>The name of the file a module is written to: <c>&lt;Module&gt;.kt</c>, the module's name in UpperCamelCase.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The file name.</returns>
  public static string FileName(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return FileClass(module)[..^2] + ".kt";
  }

  /// <summary>The package a module is written to, its name in lower case (<see cref="KotlinNaming.Package"/>).</summary>
  /// <param name="module">The module.</param>
  /// <returns>The package.</returns>
  public static string PackageOf(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return KotlinNaming.Package(module.Name);
  }

  /// <summary>
  /// The class of the JVM that holds the top-level functions of the file, as the Kotlin compiler names
  /// it: the file name in UpperCamelCase with <c>Kt</c> appended, inside the package.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The simple name of the class.</returns>
  public static string FileClass(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var name = KotlinNaming.Type(module.Name);
    return (name.Length == 0 ? "Generated" : name.TrimEnd('_')) + "Kt";
  }

  /// <summary>Emits a module for the default profile, printed as built.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Kotlin.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, KotlinProfile.Default, FloatMode.Accurate, null, TargetPassOptions.None).Files;

  /// <summary>
  /// Emits a module: builds its syntax tree, runs the target passes the options let run
  /// (<see cref="KotlinTargetPasses.Pipeline"/>) and prints the tree.
  /// </summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Kotlin to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The generated file and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Kotlin.</exception>
  public static TargetEmission Emit(IrModule module, KotlinProfile profile, FloatMode mode, KotlinCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(module);
    var (result, helpers) = Built(module, profile, mode, capabilities, passes);
    var text = KotlinPrinter.Print(result.Tree);
    return new([new GeneratedFile(FileName(module), text) { Helpers = Measured(result.Tree, helpers, text) }], result.Applications);
  }

  /// <summary>The syntax tree of a module after the target passes the options let run: what <see cref="Emit(IrModule, KotlinProfile, FloatMode, KotlinCapabilities?, TargetPassOptions)"/> prints.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Kotlin to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Kotlin.</exception>
  public static TargetPipelineResult<KtFile> Build(IrModule module, KotlinProfile profile, FloatMode mode, KotlinCapabilities? capabilities, TargetPassOptions passes)
    => Built(module, profile, mode, capabilities, passes).Result;

  private static (TargetPipelineResult<KtFile> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, KotlinProfile profile, FloatMode mode, KotlinCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(passes);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    var writer = new Writer(profile, capabilities ?? KotlinCapabilities.Standard, mode);
    var tree = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return (KotlinTargetPasses.Pipeline.Run(tree!, profile, passes), writer.Helpers);
  }

  /// <summary>The helpers of a file, measured in the text of their declarations; a helper the passes removed is listed with no text.</summary>
  private static IReadOnlyList<EmittedHelper> Measured(KtFile tree, IReadOnlyList<HelperText> helpers, string file) {
    var texts = tree.Declarations.OfType<KtFunction>().GroupBy(function => function.Name, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => KotlinPrinter.Print(group.First()), StringComparer.Ordinal);
    return EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = texts.GetValueOrDefault(helper.Name, "") })], file);
  }

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The Kotlin to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>
  /// The findings of <see cref="IrVerifier"/> for an ill-typed module; otherwise one diagnostic per
  /// type, operation, control construct or name Kotlin cannot carry. Empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, KotlinProfile profile, KotlinCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile, capabilities ?? KotlinCapabilities.Standard, FloatMode.Accurate);
    writer.Module(module);
    return writer.Problems;
  }

  /// <summary>
  /// Whether the backend writes a floating-point operation of the table itself: those without a
  /// portable implementation - the square root, which <c>Math.sqrt</c> rounds correctly, the roundings,
  /// the sign, minimum and maximum and the bit casts - and, where the profile has <c>Math.fma</c>, the
  /// fused multiply-add, which the Java specification rounds once as IEEE 754 does. Every other
  /// operation is its portable implementation, in both modes, so that its bits are the interpreter's.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The capability table.</param>
  /// <returns>Whether the backend writes it.</returns>
  public static bool Writes(IrIntrinsic intrinsic, KotlinProfile profile, KotlinCapabilities capabilities) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    ArgumentNullException.ThrowIfNull(capabilities);
    return !FloatLegalization.IsReplaceable(intrinsic)
      || intrinsic.Op == IrIntrinsicOp.FusedMultiplyAdd && capabilities.Has(KotlinCapabilities.Fma, profile);
  }

  /// <summary>
  /// The module with every floating-point operation the backend does not write replaced by its
  /// portable implementation and every integer operation Kotlin has no form of replaced by the call of
  /// its helper of the library (<see cref="KotlinLibrary"/>), each copied into the module.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode; Kotlin writes the same in both.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, KotlinProfile profile, FloatMode mode, KotlinCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(profile);
    var table = capabilities ?? KotlinCapabilities.Standard;
    var floats = FloatLegalization.Rewrite(intrinsic => Writes(intrinsic, profile, table));
    var helpers = KotlinLibrary.Rewrite(profile, table);
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => floats(expression, calls) ?? helpers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>
  /// The Kotlin name of every function of a module, by its name in the IR: a public function's is what
  /// a caller imports from the module's package, a private one's is private to the file.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyDictionary<string, string> FunctionNames(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new Writer(KotlinProfile.Default, KotlinCapabilities.Standard, FloatMode.Accurate).Names(module);
  }

}
