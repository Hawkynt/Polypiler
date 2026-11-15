using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// How far an output has been checked, from the evidence its manifest records (PRD §10.2). Each state
/// includes the ones before it; <see cref="ManifestStateRule"/> derives it.
/// </summary>
public enum ManifestState {

  /// <summary>The output was written; nothing more is known.</summary>
  Generated,

  /// <summary>The output was built with exit code 0.</summary>
  Built,

  /// <summary>The built output ran with exit code 0.</summary>
  Executed,

  /// <summary>An oracle other than Polypiler compared at least one vector, and every one passed.</summary>
  Verified

}

/// <summary>
/// The record <c>polypiler.manifest.json</c> holds in the root of an output directory
/// (<c>spec/schemas/manifest.schema.json</c>): what produced the output, from what, with which helpers,
/// and how far it has been checked. The state is not stored but derived from the evidence, so a
/// manifest cannot claim more than it records. <see cref="ManifestWriter"/> serializes it canonically.
/// </summary>
public sealed record Manifest {

  /// <summary>The version of the schema this model writes and reads.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The name of the file in the root of the output directory.</summary>
  public const string FileName = "polypiler.manifest.json";

  /// <summary>The compiler that produced the output.</summary>
  public required ManifestCompiler Compiler { get; init; }

  /// <summary>The argument vector without the executable path; paths in it are relative to the output directory.</summary>
  public required IReadOnlyList<string> Command { get; init; }

  /// <summary>The inputs; written sorted by path.</summary>
  public required IReadOnlyList<ManifestInput> Inputs { get; init; }

  /// <summary>The source language and its profile.</summary>
  public required ManifestFrontend Frontend { get; init; }

  /// <summary>The target language, profile, runtime and platform.</summary>
  public required ManifestTarget Target { get; init; }

  /// <summary>The files written; sorted by path.</summary>
  public required IReadOnlyList<ManifestOutput> Outputs { get; init; }

  /// <summary>The exported symbols, in the order of the output.</summary>
  public required IReadOnlyList<ManifestApiSymbol> Api { get; init; }

  /// <summary>The IDs of the intrinsics the output uses; sorted.</summary>
  public required IReadOnlyList<string> Intrinsics { get; init; }

  /// <summary>The floating-point record of ADR 0005 the generated files carry in their header; <see langword="null"/> when the output has no floating-point numbers.</summary>
  public ManifestFloatingPoint? FloatingPoint { get; init; }

  /// <summary>The helpers of the helper closure; sorted by symbol.</summary>
  public required IReadOnlyList<ManifestHelper> Helpers { get; init; }

  /// <summary>The native libraries the output needs.</summary>
  public required IReadOnlyList<ManifestNativeDependency> NativeDependencies { get; init; }

  /// <summary>The licence of the output and what it obliges to.</summary>
  public required ManifestLicensing Licensing { get; init; }

  /// <summary>What the output is known to do differently from the source.</summary>
  public required IReadOnlyList<ManifestMessage> Caveats { get; init; }

  /// <summary>The optimization level and the passes applied.</summary>
  public required ManifestOptimization Optimization { get; init; }

  /// <summary>The warnings of the run.</summary>
  public required IReadOnlyList<ManifestMessage> Warnings { get; init; }

  /// <summary>The toolchains actually invoked; sorted by key.</summary>
  public required IReadOnlyList<ManifestToolchain> Toolchains { get; init; }

  /// <summary>The build of the output, or <see langword="null"/> when none ran.</summary>
  public ManifestBuild? Build { get; init; }

  /// <summary>The execution of the built output, or <see langword="null"/> when none ran.</summary>
  public ManifestExecution? Execution { get; init; }

  /// <summary>The comparison with an oracle, or <see langword="null"/> when none ran.</summary>
  public ManifestVerification? Verification { get; init; }

  /// <summary>The benchmark results, shape owned by the benchmark harness; <see langword="null"/> when none were measured.</summary>
  public JsonArray? Benchmarks { get; init; }

  /// <summary>The commands a user types to rebuild and run the output.</summary>
  public required ManifestCommands Commands { get; init; }

  /// <summary>Caller contracts; <see langword="null"/> when their owner did not run.</summary>
  public JsonArray? Assumptions { get; init; }

  /// <summary>The resource report of a constrained target; <see langword="null"/> when its owner did not run.</summary>
  public JsonObject? Resources { get; init; }

  /// <summary>The object-model features used and their rules; <see langword="null"/> when their owner did not run.</summary>
  public JsonObject? ObjectModel { get; init; }

  /// <summary>The language-profile section; <see langword="null"/> when its owner did not run.</summary>
  public JsonObject? Profile { get; init; }

  /// <summary>Measured sizes against profile ceilings; <see langword="null"/> when their owner did not run.</summary>
  public JsonObject? Budget { get; init; }

  /// <summary>The highest state the recorded evidence supports, by <see cref="ManifestStateRule"/>.</summary>
  public ManifestState State => ManifestStateRule.Derive(this.Build, this.Execution, this.Verification).State;

}

/// <summary>The compiler that produced an output.</summary>
/// <param name="Name">The product name.</param>
/// <param name="Version">The product version.</param>
/// <param name="IrVersion">The version of the IR the compiler works on.</param>
public sealed record ManifestCompiler(string Name, string Version, int IrVersion);

/// <summary>Where an input was taken from upstream.</summary>
/// <param name="Repository">The repository, <c>owner/name</c>.</param>
/// <param name="Commit">The full commit SHA, 40 lower-case hexadecimal digits.</param>
/// <param name="Path">The path of the file in the repository.</param>
public sealed record ManifestProvenance(string Repository, string Commit, string Path);

/// <summary>An input of the run.</summary>
/// <param name="Path">The path relative to the output directory, with <c>/</c> separators.</param>
/// <param name="Sha256">The SHA-256 of its bytes, 64 lower-case hexadecimal digits.</param>
/// <param name="Provenance">Where it came from upstream, or <see langword="null"/> for a local file.</param>
public sealed record ManifestInput(string Path, string Sha256, ManifestProvenance? Provenance);

/// <summary>The source language of a run.</summary>
/// <param name="Language">The language as <c>--from</c> names it.</param>
/// <param name="Profile">The source profile, or <see langword="null"/> when the frontend has none.</param>
public sealed record ManifestFrontend(string Language, string? Profile);

/// <summary>The target of a run.</summary>
/// <param name="Language">The language as <c>--to</c> names it.</param>
/// <param name="Profile">The profile as the header of a generated file states it.</param>
/// <param name="Runtime">The runtime, by its toolchain key.</param>
/// <param name="Platform">The platform; <c>any</c> for portable source.</param>
public sealed record ManifestTarget(string Language, string Profile, string Runtime, string Platform);

/// <summary>What a written file is for.</summary>
public enum ManifestOutputRole {

  /// <summary>Generated source.</summary>
  Source,

  /// <summary>A generated test.</summary>
  Test,

  /// <summary>A helper the source needs.</summary>
  Helper,

  /// <summary>A build file.</summary>
  Build,

  /// <summary>A notice: licences, SBOM.</summary>
  Notice

}

/// <summary>A file written to the output directory.</summary>
/// <param name="Path">The path relative to the output directory, with <c>/</c> separators.</param>
/// <param name="Sha256">The SHA-256 of the bytes written.</param>
/// <param name="Role">What the file is for.</param>
public sealed record ManifestOutput(string Path, string Sha256, ManifestOutputRole Role);

/// <summary>An exported symbol of the output.</summary>
/// <param name="Name">The name as the target sees it.</param>
/// <param name="Signature">The signature in IR types.</param>
public sealed record ManifestApiSymbol(string Name, string Signature) {

  /// <summary>The API shape, written by the API-shape profile; <see langword="null"/> when absent.</summary>
  public JsonNode? Shape { get; init; }

  /// <summary>The symbol this one adapts, written by the API-shape profile; <see langword="null"/> when absent.</summary>
  public JsonNode? AdapterOf { get; init; }

  /// <summary>The representation chosen, written by the API-shape profile; <see langword="null"/> when absent.</summary>
  public JsonNode? Representation { get; init; }

}

/// <summary>The floating-point record of ADR 0005, as the header of every generated file carries it.</summary>
/// <param name="Mode"><c>strict</c>, <c>accurate</c> or <c>fast</c>.</param>
/// <param name="Natives">Every native function with an <c>ulp(n)</c> contract the code calls; none in <c>strict</c>.</param>
public sealed record ManifestFloatingPoint(string Mode, IReadOnlyList<ManifestNativeFunction> Natives);

/// <summary>A native function the code calls under an <c>ulp(n)</c> contract.</summary>
/// <param name="Operation">The operation ID, for example <c>INTRINSICS.Sin</c>.</param>
/// <param name="Format"><c>f32</c> or <c>f64</c>.</param>
/// <param name="Accuracy">The contract, for example <c>ulp(1)</c>.</param>
/// <param name="Runtime">The runtime whose slack applies.</param>
/// <param name="Slack">The slack <c>N</c> of the runtime's function.</param>
/// <param name="Bound">The comparison bound <c>n + N + max(n, N)</c>.</param>
public sealed record ManifestNativeFunction(string Operation, string Format, string Accuracy, string Runtime, int Slack, int Bound);

/// <summary>A helper of the helper closure.</summary>
/// <param name="Symbol">The symbol.</param>
/// <param name="License">Its licence.</param>
/// <param name="Origin">Where it comes from.</param>
/// <param name="CallSites">How many call sites use it.</param>
/// <param name="Size">Its size.</param>
/// <param name="DependsOn">The helpers it needs.</param>
public sealed record ManifestHelper(string Symbol, string License, string Origin, int CallSites, int Size, IReadOnlyList<string> DependsOn);

/// <summary>A native library the output needs.</summary>
/// <param name="Name">The library.</param>
/// <param name="Version">Its version.</param>
/// <param name="Licence">Its licence.</param>
public sealed record ManifestNativeDependency(string Name, string Version, string Licence);

/// <summary>The licence of an output.</summary>
/// <param name="OutputLicence">The licence, as an SPDX expression or <c>NOASSERTION</c>.</param>
/// <param name="Obligations">What redistributing the output obliges to.</param>
/// <param name="ModelRestrictions">Restrictions of models and weights the output uses.</param>
public sealed record ManifestLicensing(string OutputLicence, IReadOnlyList<string> Obligations, IReadOnlyList<string> ModelRestrictions);

/// <summary>A caveat or a warning.</summary>
/// <param name="Code">The caveat ID or diagnostic code.</param>
/// <param name="Message">The text.</param>
public sealed record ManifestMessage(string Code, string Message) {

  /// <summary>The warnings among the diagnostics of a run, in the order they were reported.</summary>
  /// <param name="diagnostics">The diagnostics.</param>
  /// <returns>One message per warning: its code and its text.</returns>
  public static IReadOnlyList<ManifestMessage> WarningsOf(IEnumerable<Diagnostic> diagnostics)
    => [.. diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning).Select(diagnostic => new ManifestMessage(diagnostic.Code, diagnostic.Message))];

}

/// <summary>The optimization of a run.</summary>
/// <param name="Level">The level, as <c>--opt</c> names it.</param>
/// <param name="Disabled">The optimizations <c>--disable-opt</c> turned off.</param>
/// <param name="Passes">The rules applied, once each, in the order they first applied.</param>
public sealed record ManifestOptimization(string Level, IReadOnlyList<string> Disabled, IReadOnlyList<ManifestPass> Passes);

/// <summary>A rule the optimizer applied.</summary>
/// <param name="Id">The optimization number.</param>
/// <param name="ProofClass">How it is justified; <c>unproven</c> when no proof is recorded.</param>
public sealed record ManifestPass(string Id, string ProofClass);

/// <summary>A toolchain a run invoked.</summary>
/// <param name="Key">Its key in <c>tooling/toolchains.lock</c>.</param>
/// <param name="Version">The version that ran.</param>
public sealed record ManifestToolchain(string Key, string Version);

/// <summary>The build of an output.</summary>
/// <param name="Toolchain">The toolchain key.</param>
/// <param name="Command">The command line.</param>
/// <param name="ExitCode">Its exit code.</param>
public sealed record ManifestBuild(string Toolchain, string Command, int ExitCode);

/// <summary>The execution of a built output.</summary>
/// <param name="Command">The command line.</param>
/// <param name="ExitCode">Its exit code.</param>
public sealed record ManifestExecution(string Command, int ExitCode);

/// <summary>The comparison of an output with an oracle.</summary>
/// <param name="Oracle">The oracle.</param>
/// <param name="Producer">What produced the expected values; <c>polypiler</c> never verifies itself.</param>
/// <param name="Exactness">How the values were compared (ADR 0005).</param>
/// <param name="Total">The vectors compared.</param>
/// <param name="Passed">The vectors that passed.</param>
/// <param name="Failed">The vectors that failed.</param>
public sealed record ManifestVerification(string Oracle, string Producer, ManifestExactness Exactness, long Total, long Passed, long Failed);

/// <summary><c>verification.exactness</c> of ADR 0005.</summary>
/// <param name="Profile"><c>strict</c>, <c>accurate</c> or <c>fast</c>.</param>
/// <param name="Mode"><c>exact</c> when every output was compared exactly, otherwise <c>tolerance</c>.</param>
/// <param name="SpecialValues">The rule applied: <c>bitwise</c>, <c>signed-zero</c> or <c>class</c>.</param>
/// <param name="Operations">Every tolerance operation whose result reaches an output directly; empty under <c>exact</c>.</param>
/// <param name="Declared">Every bound a fixture declared itself.</param>
/// <param name="Observed">What the comparison found.</param>
public sealed record ManifestExactness(
  string Profile,
  string Mode,
  string SpecialValues,
  IReadOnlyList<ManifestToleranceOperation> Operations,
  IReadOnlyList<ManifestDeclaredBound> Declared,
  ManifestObserved Observed);

/// <summary>A tolerance operation whose result reaches an output directly.</summary>
/// <param name="Operation">The operation ID.</param>
/// <param name="Accuracy">Its contract.</param>
/// <param name="Policy">The ID of its tolerance document.</param>
/// <param name="Mode"><c>ulp</c>, <c>relative</c> or <c>absolute</c>.</param>
/// <param name="Bound">The bound applied.</param>
public sealed record ManifestToleranceOperation(string Operation, string Accuracy, string Policy, string Mode, double Bound);

/// <summary>A bound a fixture declared itself.</summary>
/// <param name="Output">The output it applies to.</param>
/// <param name="Mode"><c>ulp</c>, <c>relative</c> or <c>absolute</c>.</param>
/// <param name="Bound">The bound.</param>
/// <param name="Reason">Why no bound can be derived.</param>
public sealed record ManifestDeclaredBound(string Output, string Mode, double Bound, string Reason);

/// <summary>What a comparison found.</summary>
/// <param name="Identical">Compared elements whose bit patterns were identical.</param>
/// <param name="WithinTolerance">Elements that passed without being identical.</param>
/// <param name="MaxDistance">The largest comparison distance of a passing element.</param>
/// <param name="WorstVector">The vector that produced it; <see langword="null"/> when <paramref name="MaxDistance"/> is 0.</param>
public sealed record ManifestObserved(long Identical, long WithinTolerance, double MaxDistance, string? WorstVector);

/// <summary>The commands a user types to rebuild and run an output.</summary>
/// <param name="Build">The command that rebuilds it.</param>
/// <param name="Run">The command that runs it, or <see langword="null"/> for a library without an entry point.</param>
public sealed record ManifestCommands(string Build, string? Run);
