using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Core.Verification.Gates;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Gates;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Verification.Capabilities;

/// <summary>
/// A temporary repository for the capability report: a copy of one spec tree of
/// <c>tests/fixtures/verify/capabilities/</c> as <c>spec/</c>, and <c>artifacts/</c> with a copy of the
/// gate fixtures' evidence and an empty <c>gates/</c>, where <see cref="Record"/> writes records with
/// the real <see cref="GateRecorder"/>. Deleted on disposal.
/// </summary>
internal sealed class CapabilityWorkspace : IDisposable {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/verify/capabilities";

  /// <summary>The commit the tests name.</summary>
  public const string Commit = "0123456789abcdef0123456789abcdef01234567";

  /// <summary>C# for .NET 10 to JavaScript on node at ES2022, the pair the scenarios record.</summary>
  public static readonly PairKey CSharpToJavaScript = new("csharp", "javascript", "net10", "node-es2022");

  /// <summary>JavaScript on node at ES2022 to C# for .NET 10.</summary>
  public static readonly PairKey JavaScriptToCSharp = new("javascript", "csharp", "node-es2022", "net10");

  /// <summary>A workspace with the spec tree of a fixture.</summary>
  /// <param name="tree"><c>empty</c>, <c>one-verified</c> or <c>mixed</c>.</param>
  public CapabilityWorkspace(string tree) {
    this.Root = Path.Combine(Path.GetTempPath(), "polypiler-capabilities-" + Guid.NewGuid().ToString("N"));
    Copy(Path.Combine(PrototypeFixture.RepositoryRoot, Folder, tree, "spec"), Path.Combine(this.Root, "spec"));
    Copy(GateFixtures.PathOf("evidence"), Path.Combine(this.Root, "artifacts", "evidence"));
    Directory.CreateDirectory(this.Gates);
  }

  /// <summary>The workspace, the repository root of the command.</summary>
  public string Root { get; }

  /// <summary>The gate directory, <c>artifacts/gates</c>.</summary>
  public string Gates => Path.Combine(this.Root, "artifacts", "gates");

  /// <summary>The full path of a file below <see cref="Root"/>.</summary>
  public string PathOf(string relative) => Path.Combine(this.Root, relative.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>
  /// Records one fixture of a pair up to a state: every mandatory state up to it, the optional ones
  /// only where the state is one of them or <paramref name="optional"/> asks for them.
  /// </summary>
  public void Record(PairKey pair, string fixture, GateState last, bool optional = false) {
    var recorder = new GateRecorder(this.Gates, "fixture");
    foreach (var state in GateStates.Evidenced.Where(state => state <= last && (!GateStates.IsOptional(state) || optional || GateStates.IsOptional(last))))
      recorder.Record(pair, fixture, recorder.Evidence(state, Path.Combine(recorder.Root, GateFixtures.EvidenceOf(state))));
  }

  /// <summary>Loads the spec tree and the gate records and builds the report; the diagnostics as <c>code message</c> lines.</summary>
  public (CapabilityReport Report, string[] Diagnostics) Build() {
    var bag = new DiagnosticBag();
    var store = GateStore.Load(this.Gates, bag);
    var (sources, targets) = ReportCapabilitiesCommand.Profiles(SpecTree.Load(this.PathOf("spec"), this.Root));
    var report = CapabilityReport.Build(sources, targets, store, "artifacts/gates", Commit, bag);
    return (report, [.. bag.Items.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")]);
  }

  /// <summary>Runs <c>report capabilities</c> in the workspace with the test commit and any further arguments.</summary>
  public (int ExitCode, string Output, string Error) Run(params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = ReportCapabilitiesCommand.Run(args, output, error, this.Root, Commit);
    return (exitCode, output.ToString(), error.ToString());
  }

  /// <summary>The lines of standard error.</summary>
  public static IReadOnlyList<string> Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

  public void Dispose() {
    if (Directory.Exists(this.Root))
      Directory.Delete(this.Root, true);
  }

  private static void Copy(string source, string target) {
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source))
      File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var directory in Directory.GetDirectories(source))
      Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
  }

}
