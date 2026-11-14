using System;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Gates;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Gates;

/// <summary>The gate fixtures of <c>tests/fixtures/verify/gates/</c>.</summary>
internal static class GateFixtures {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/verify/gates";

  /// <summary>The pair of the scenarios: C# for .NET 10 to JavaScript on node at ES2022.</summary>
  public static readonly PairKey CSharpToJavaScript = new("csharp", "javascript", "net10", "node-es2022");

  /// <summary>The second valid pair: JavaScript at ES2022 to C# for .NET 8.</summary>
  public static readonly PairKey JavaScriptToCSharp = new("javascript", "csharp", "es2022", "net8.0");

  /// <summary>The full path of a fixture file or folder.</summary>
  public static string PathOf(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, Folder, name);

  /// <summary>The evidence file of each state as the fixture records give it, relative to the fixture directory.</summary>
  public static string EvidenceOf(GateState state) => state switch {
    GateState.Parsed => "evidence/frontend.log",
    GateState.Lifted => "evidence/lifted.ir",
    GateState.Recognized => "evidence/opt-report.txt",
    GateState.Proved => "evidence/proof-report.txt",
    GateState.Legalized => "evidence/legal.ir",
    GateState.Emitted => "evidence/manifests/generated.json",
    GateState.Compiled => "evidence/manifests/built.json",
    GateState.Executed => "evidence/manifests/executed.json",
    GateState.Verified => "evidence/manifests/verified.json",
    _ => "evidence/bench.json"
  };

  /// <summary>Loads a gate directory; the diagnostics as <c>code message</c> lines.</summary>
  public static (GateStore Store, string[] Diagnostics) Load(string directory) {
    var bag = new DiagnosticBag();
    var store = GateStore.Load(directory, bag);
    return (store, [.. bag.Items.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")]);
  }

}

/// <summary>
/// A temporary artifacts directory: a copy of the fixtures' <c>evidence/</c> and an empty
/// <c>gates/</c> beside it, deleted on disposal.
/// </summary>
internal sealed class TemporaryArtifacts : IDisposable {

  public TemporaryArtifacts() {
    this.Root = Path.Combine(Path.GetTempPath(), "polypiler-gates-" + Guid.NewGuid().ToString("N"));
    Copy(GateFixtures.PathOf("evidence"), Path.Combine(this.Root, "evidence"));
    Directory.CreateDirectory(this.Gates);
  }

  /// <summary>The artifacts directory.</summary>
  public string Root { get; }

  /// <summary>The gate directory below it.</summary>
  public string Gates => Path.Combine(this.Root, "gates");

  /// <summary>A recorder of <see cref="Gates"/> with a fixed commit.</summary>
  public GateRecorder Recorder() => new(this.Gates, "fixture");

  /// <summary>Puts a fixture record into <see cref="Gates"/> under the name of a pair.</summary>
  public string Place(string fixture, PairKey pair) {
    var target = Path.Combine(this.Gates, pair.FileName);
    File.Copy(GateFixtures.PathOf(fixture), target);
    return target;
  }

  /// <summary>Writes a file into <see cref="Gates"/>.</summary>
  public string Write(string name, string text) {
    var target = Path.Combine(this.Gates, name);
    File.WriteAllText(target, text);
    return target;
  }

  /// <summary>The full path of a file below <see cref="Root"/>.</summary>
  public string PathOf(string relative) => Path.Combine(this.Root, relative);

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
