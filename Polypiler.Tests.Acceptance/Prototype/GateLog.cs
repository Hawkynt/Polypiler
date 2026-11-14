using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Gates;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// What one run of a fixture through a pair showed, each part only when the run saw it: the text of a
/// translation without an error, the IR the frontend produced, the backend's verdict on that IR for
/// the target profile, the manifest the translation wrote, and the build, execution and comparison the
/// test itself carried out.
/// </summary>
/// <param name="TranslationText">Everything the translation wrote to its two streams, or <see langword="null"/> before it ran.</param>
/// <param name="TranslationExitCode">The exit code of the translation.</param>
/// <param name="Lifted">The IR the frontend made of the fixture, or <see langword="null"/>.</param>
/// <param name="TargetProblems">What the backend's check of the target profile says of that IR; empty when it is legal there, <see langword="null"/> when it was not asked.</param>
/// <param name="Output">The output directory of the translation, which holds its manifest, or <see langword="null"/>.</param>
/// <param name="Build">The build of the generated file, or <see langword="null"/> when there was none.</param>
/// <param name="Execution">The execution of the built file, or <see langword="null"/> when it did not run.</param>
/// <param name="Verification">The comparison with the oracle, or <see langword="null"/> when there was none.</param>
internal sealed record GateRun(
  string? TranslationText = null,
  int TranslationExitCode = -1,
  IrModule? Lifted = null,
  IReadOnlyList<Diagnostic>? TargetProblems = null,
  string? Output = null,
  ManifestBuild? Build = null,
  ManifestExecution? Execution = null,
  ManifestVerification? Verification = null);

/// <summary>
/// Records the gate state a fixture reached in an acceptance test, with the evidence written beside the
/// gate directory: by default <c>artifacts/gates</c> of the repository, or the directory the variable
/// <see cref="DirectoryVariable"/> names. A fixture's earlier entries are forgotten first, so a record
/// never holds more than the last run showed. Recognition and proof are not recorded: the translation
/// reports its rewrites as unproven, and the chain skips both.
/// </summary>
internal static partial class GateLog {

  /// <summary>The environment variable that moves the gate directory.</summary>
  public const string DirectoryVariable = "POLYPILER_GATES";

  /// <summary>The source profile of the C# frontend: C# 14 on .NET 10.</summary>
  public const string CSharpSource = "net10";

  /// <summary>The source profile of the JavaScript frontend: ES2022 scripts and modules.</summary>
  public const string JavaScriptSource = "es2022";

  private static readonly UTF8Encoding _utf8 = new(false);

  /// <summary>The gate directory the acceptance tests record into.</summary>
  public static string Directory { get; } = Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } directory
    ? Path.GetFullPath(directory)
    : Path.Combine(PrototypeFixture.RepositoryRoot, "artifacts", "gates");

  /// <summary>The target profile of a JavaScript profile as a pair names it: <c>node-</c>, the level, the module format, and <c>-typed</c> for ES5 with typed arrays.</summary>
  public static string NodeProfile(ProfileCase profile)
    => $"node-{profile.Level}-{profile.Module}{(profile.TypedArrays ? "-typed" : "")}";

  /// <summary>The exactness of an exact comparison of every outcome, -0 told from 0.</summary>
  public static ManifestExactness Exact(string output, long identical) {
    var manifest = ManifestReader.Read(Path.Combine(output, Manifest.FileName), new DiagnosticBag());
    return new(manifest?.FloatingPoint?.Mode ?? "accurate", "exact", "signed-zero", [], [], new(identical, 0, 0, null));
  }

  /// <summary>Records what a run showed for a fixture of a pair.</summary>
  /// <param name="pair">The pair.</param>
  /// <param name="fixture">The fixture.</param>
  /// <param name="run">What the run showed.</param>
  public static void Reached(PairKey pair, string fixture, GateRun run) {
    var recorder = new GateRecorder(Directory);
    var evidence = Path.Combine(recorder.Root, "evidence", Path.GetFileNameWithoutExtension(pair.FileName), Unsafe().Replace(fixture, "_"));
    if (System.IO.Directory.Exists(evidence))
      System.IO.Directory.Delete(evidence, true);

    System.IO.Directory.CreateDirectory(evidence);
    recorder.Forget(pair, fixture);
    string Write(string name, byte[] bytes) {
      var path = Path.Combine(evidence, name);
      System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, bytes);
      return path;
    }

    void Record(GateState state, string path) => recorder.Record(pair, fixture, recorder.Evidence(state, path));

    if (run.TranslationText is { } text && run.TranslationExitCode == 0 && !Regex.IsMatch(text, @"\berror PLY\d{4}"))
      Record(GateState.Parsed, Write("frontend.log", _utf8.GetBytes(text)));
    if (run.Lifted is { } lifted && IrVerifier.Verify(lifted).Count == 0) {
      Record(GateState.Lifted, Write("lifted.ir", IrModuleFormat.Write(lifted)));
      if (run.TargetProblems is { Count: 0 })
        Record(GateState.Legalized, Write("legal.ir", IrModuleFormat.Write(lifted)));
    }

    var written = run.Output is null ? "" : Path.Combine(run.Output, Manifest.FileName);
    if (!File.Exists(written) || ManifestReader.Read(written, new DiagnosticBag()) is not { } generated)
      return;

    Record(GateState.Emitted, Write(Path.Combine("emitted", Manifest.FileName), File.ReadAllBytes(written)));
    var ran = generated with { Build = run.Build, Execution = run.Execution, Verification = run.Verification };
    var path = Write(Path.Combine("run", Manifest.FileName), _utf8.GetBytes(ManifestWriter.Serialize(ran)));
    var diagnostics = new DiagnosticBag();
    Assert.That(ManifestReader.Read(path, diagnostics), Is.Not.Null, string.Join("\n", diagnostics.Items));
    foreach (var state in new[] { GateState.Compiled, GateState.Executed, GateState.Verified })
      if (ran.State >= GateStates.ManifestStateOf(state))
        Record(state, path);
  }

  [GeneratedRegex("[^A-Za-z0-9._-]")]
  private static partial Regex Unsafe();

}
