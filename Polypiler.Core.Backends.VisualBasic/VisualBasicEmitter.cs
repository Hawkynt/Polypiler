using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Net;
using Polypiler.Core.Backends.VisualBasic.Passes;
using Polypiler.Core.Backends.VisualBasic.Printing;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// Emits one classic Visual Basic standard module (<c>.bas</c>) per IR module, for the dialect a
/// <see cref="VisualBasicProfile"/> names. Every procedure is typed under <c>Option Explicit</c>:
/// <c>bool</c> is <c>Boolean</c>, <c>u8</c> <c>Byte</c>, <c>i8</c> and <c>i16</c> <c>Integer</c>,
/// <c>u16</c> and <c>i32</c> <c>Long</c>, <c>u32</c> the bits of a <c>Long</c>, <c>i64</c> and
/// <c>u64</c> a <c>LongLong</c> where the profile has it, <c>f32</c> <c>Single</c>, <c>f64</c>
/// <c>Double</c>, a string <c>String</c>, an array a dynamic array indexed from 0 and a struct a
/// user-defined type. Visual Basic's arithmetic traps on every overflow (run-time error 6) and its
/// <c>And</c> and <c>Or</c> evaluate both operands, so the IR's wrapping arithmetic is a helper
/// (<see cref="VisualBasicHelpers"/>) and its short-circuit operators and conditional expressions
/// become statements; every result of a narrow type is written through its conversion function
/// (<c>CInt</c>, <c>CLng</c>, <c>CByte</c>, <c>CSng</c>), which also makes an interpreter that widens
/// intermediates trap where Visual Basic does. Control flow is structured: every loop is a <c>Do</c>
/// loop, a <c>continue</c> a <c>GoTo</c> to a label before the loop's end, a switch a
/// <c>Select Case</c>. A trap of the IR is the run-time error Visual Basic raises for it:
/// 6 overflow, 11 division by zero, 9 subscript out of range, 5 invalid argument.
/// </summary>
public static partial class VisualBasicEmitter {

  /// <summary>The code for a type that has no Visual Basic representation in the chosen profile.</summary>
  public const string TypeUnsupported = "PLY2656";

  /// <summary>The code for an IR construct that cannot be written as Visual Basic in the chosen profile.</summary>
  public const string Inexpressible = "PLY2657";

  /// <summary>The code for a control construct that has no Visual Basic form.</summary>
  public const string ControlUnsupported = "PLY2655";

  /// <summary>The code for public procedures whose Visual Basic names collide, or a name nothing is left of.</summary>
  public const string NameCollision = "PLY2658";

  /// <summary>The registered diagnostic of <see cref="TypeUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypeUnsupportedDescriptor = new(TypeUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "visualbasic.backend.type");

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "visualbasic.backend.profile");

  /// <summary>The registered diagnostic of <see cref="ControlUnsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupportedDescriptor = new(ControlUnsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "visualbasic.backend.control");

  /// <summary>The registered diagnostic of <see cref="NameCollision"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NameCollisionDescriptor = new(NameCollision, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "visualbasic.backend.names");

  /// <summary>The runtime the header names for the floating-point record, as <c>tooling/toolchains.lock</c> keys the oracle that runs the output.</summary>
  public const string Runtime = "libreoffice";

  /// <summary>The runtime of a Visual Basic .NET profile, as <c>tooling/toolchains.lock</c> keys the SDK whose compiler and runtime run the output.</summary>
  public const string NetRuntime = "dotnet-sdk";

  /// <summary>The runtime of a profile: LibreOffice Basic for a classic dialect, the .NET SDK for Visual Basic .NET.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The toolchain key.</returns>
  public static string RuntimeOf(VisualBasicProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    return profile.IsNet ? NetRuntime : Runtime;
  }

  /// <summary>The run-time error number Visual Basic raises for each trap of the IR; any other trap raises 5.</summary>
  public static IReadOnlyDictionary<IrTrap, int> ErrorNumbers { get; } = new Dictionary<IrTrap, int> {
    [IrTrap.IntegerOverflow] = 6,
    [IrTrap.DivideByZero] = 11,
    [IrTrap.IndexOutOfRange] = 9,
    [IrTrap.InvalidCast] = 13,
    [IrTrap.NullReference] = 91,
    [IrTrap.ArgumentOutOfRange] = 5,
    [IrTrap.InvalidOperation] = 5
  };

  /// <summary>Every fixed helper of the backend a module can carry, by its name, with its licence and origin.</summary>
  public static IReadOnlyDictionary<string, (string Licence, string Origin)> HelperOrigins { get; }
    = VisualBasicHelpers.Order.Concat(VbNetHelpers.Order).Distinct(StringComparer.Ordinal)
      .ToDictionary(name => name, _ => (EmittedHelper.OwnLicence, EmittedHelper.OriginOf(VisualBasicHelpers.Folder)), StringComparer.Ordinal);

  /// <summary>The name of the file a module is written to: <c>&lt;module&gt;.bas</c>.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The file name.</returns>
  public static string FileName(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return VisualBasicNaming.Module(module.Name) + ".bas";
  }

  /// <summary>The name of the source file of a module in Visual Basic .NET: <c>&lt;module&gt;.vb</c>.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The file name.</returns>
  public static string NetFileName(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return VbNetNaming.Valid(module.Name, "Module") + ".vb";
  }

  /// <summary>
  /// The project file of a module in Visual Basic .NET, <c>&lt;module&gt;.vbproj</c>: a library for the
  /// profile's framework and language version, every option of the file stated, warnings errors, and
  /// <c>RemoveIntegerChecks</c>, which the generated code needs for its wrapping arithmetic.
  /// </summary>
  /// <param name="profile">A Visual Basic .NET profile.</param>
  /// <returns>The text of the project file.</returns>
  /// <exception cref="ArgumentException">The profile is a classic one.</exception>
  public static string ProjectFile(VisualBasicProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    if (!profile.IsNet)
      throw new ArgumentException($"The profile '{profile.Name}' is no Visual Basic .NET profile.", nameof(profile));

    return "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n"
      + $"    <TargetFramework>{profile.Framework!.Moniker}</TargetFramework>\n    <LangVersion>{profile.LanguageVersion}</LangVersion>\n"
      + "    <RootNamespace></RootNamespace>\n    <OptionExplicit>On</OptionExplicit>\n    <OptionStrict>On</OptionStrict>\n    <OptionInfer>On</OptionInfer>\n"
      + "    <OptionCompare>Binary</OptionCompare>\n    <RemoveIntegerChecks>true</RemoveIntegerChecks>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n"
      + "    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n\n</Project>\n";
  }

  /// <summary>Emits a module for the default profile, printed as built.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, VisualBasicProfile.Default);

  /// <summary>Emits a module for a profile, printed as built, without the target passes.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The dialect to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, VisualBasicProfile profile, VisualBasicCapabilities? capabilities = null)
    => Emit(module, profile, FloatMode.Accurate, capabilities, TargetPassOptions.None).Files;

  /// <summary>Emits a module: builds its syntax tree, runs the target passes the options let run and prints the tree.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The dialect to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The generated file and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic.</exception>
  public static TargetEmission Emit(IrModule module, VisualBasicProfile profile, FloatMode mode, VisualBasicCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    if (profile.IsNet)
      return new(EmitNet(module, profile, mode, null), []);

    var (result, helpers) = Built(module, profile, mode, capabilities, passes);
    var text = VbPrinter.Print(result.Tree);
    return new([new GeneratedFile(FileName(module), text) { Helpers = Measured(result.Tree, helpers, text) }], result.Applications);
  }

  /// <summary>
  /// Emits a module as Visual Basic .NET: the source file <c>&lt;module&gt;.vb</c> of one <c>Module</c>
  /// and the project file <c>&lt;module&gt;.vbproj</c> that compiles it.
  /// </summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <param name="profile">A Visual Basic .NET profile.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The two files.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic .NET.</exception>
  public static IReadOnlyList<GeneratedFile> EmitNet(IrModule module, VisualBasicProfile profile, FloatMode mode, VbNetCapabilities? capabilities) {
    var table = capabilities ?? VbNetCapabilities.Standard;
    var (tree, helpers) = BuiltNet(module, profile, mode, table);
    var text = VnPrinter.Print(tree, table.Has(VbNetCapabilities.ImplicitLineContinuation, profile));
    var texts = tree.Members.OfType<VnMethod>().GroupBy(method => method.Name, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => VnPrinter.Print(group.First(), table.Has(VbNetCapabilities.ImplicitLineContinuation, profile)), StringComparer.Ordinal);
    var measured = EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = texts.GetValueOrDefault(helper.Name, "") })], text);
    var file = NetFileName(module);
    return [new GeneratedFile(file, text) { Helpers = measured }, new GeneratedFile(Path.ChangeExtension(file, ".vbproj"), ProjectFile(profile))];
  }

  /// <summary>The syntax tree of a module in Visual Basic .NET: what <see cref="EmitNet"/> prints.</summary>
  /// <param name="module">A legalized module that <see cref="Check"/> accepts.</param>
  /// <param name="profile">A Visual Basic .NET profile.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic .NET.</exception>
  public static VnModule BuildNet(IrModule module, VisualBasicProfile profile, FloatMode mode, VbNetCapabilities? capabilities)
    => BuiltNet(module, profile, mode, capabilities ?? VbNetCapabilities.Standard).Tree;

  private static (VnModule Tree, IReadOnlyList<HelperText> Helpers) BuiltNet(IrModule module, VisualBasicProfile profile, FloatMode mode, VbNetCapabilities capabilities) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    var writer = new VbNetWriter(profile, capabilities, mode);
    var tree = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return (tree!, writer.Helpers);
  }

  /// <summary>The syntax tree of a module after the target passes the options let run: what <see cref="Emit(IrModule, VisualBasicProfile, FloatMode, VisualBasicCapabilities?, TargetPassOptions)"/> prints.</summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The dialect to write.</param>
  /// <param name="mode">The floating-point mode the header records.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be written as Visual Basic.</exception>
  public static TargetPipelineResult<VbModule> Build(IrModule module, VisualBasicProfile profile, FloatMode mode, VisualBasicCapabilities? capabilities, TargetPassOptions passes)
    => Built(module, profile, mode, capabilities, passes).Result;

  private static (TargetPipelineResult<VbModule> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, VisualBasicProfile profile, FloatMode mode, VisualBasicCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(passes);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    var writer = new Writer(profile, capabilities ?? VisualBasicCapabilities.Standard, mode);
    var tree = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return (VisualBasicTargetPasses.Pipeline.Run(tree!, profile, passes), writer.Helpers);
  }

  /// <summary>The helpers of a file, measured in the text of their declarations; a helper the passes removed is listed with no text.</summary>
  private static IReadOnlyList<EmittedHelper> Measured(VbModule tree, IReadOnlyList<HelperText> helpers, string file) {
    var texts = tree.Declarations.OfType<VbProcedure>().GroupBy(procedure => procedure.Name, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => VbPrinter.Print(group.First()), StringComparer.Ordinal);
    return EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = texts.GetValueOrDefault(helper.Name, "") })], file);
  }

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The dialect to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>
  /// The findings of <see cref="IrVerifier"/> for an ill-typed module; otherwise one diagnostic per
  /// type, operation, control construct or name Visual Basic cannot carry. Empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, VisualBasicProfile profile, VisualBasicCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    module = SequenceFusion.Run(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    if (profile.IsNet) {
      var net = new VbNetWriter(profile, VbNetCapabilities.Standard, FloatMode.Accurate);
      net.Module(module);
      return net.Problems;
    }

    var writer = new Writer(profile, capabilities ?? VisualBasicCapabilities.Standard, FloatMode.Accurate);
    writer.Module(module);
    return writer.Problems;
  }

  /// <summary>
  /// Whether the backend writes a floating-point operation of the table itself: only those that have
  /// no portable implementation - the square root (<c>Sqr</c>), the roundings (<c>Int</c>, <c>Fix</c>,
  /// <c>Round</c>), the sign, the minimum and maximum, and the bit casts, which it refuses.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <returns>Whether the backend writes it.</returns>
  public static bool Writes(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    return !FloatLegalization.IsReplaceable(intrinsic);
  }

  /// <summary>
  /// The module with every floating-point operation the backend does not write replaced by its
  /// portable implementation and every integer operation Visual Basic has no form of replaced by the
  /// call of its helper of the library (<see cref="IntegerLibrary"/>), each copied into the module.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode; Visual Basic writes the same in both.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, VisualBasicProfile profile, FloatMode mode) {
    ArgumentNullException.ThrowIfNull(profile);
    var floats = FloatLegalization.Rewrite(Writes);
    LibraryRewrite integers = (expression, calls) => expression is IrIntrinsic intrinsic && intrinsic.Arguments.Count > 0 && IntegerLibrary.Operand(intrinsic).IsInteger
      ? IntegerLibrary.Call(intrinsic, calls) : null;
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations, (expression, calls) => floats(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>The Visual Basic name of every function of a module, by its name in the IR.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyDictionary<string, string> FunctionNames(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new Writer(VisualBasicProfile.Default, VisualBasicCapabilities.Standard, FloatMode.Accurate).Names(module);
  }

}
