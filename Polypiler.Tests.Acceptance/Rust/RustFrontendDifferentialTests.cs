using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The Rust fixtures against <c>rustc</c>: every call of every fixture, with overflow checks on and
/// off, has one outcome in <c>rustc</c>'s build of the original, in the recording the unit tests
/// compare the interpreter with, in the C# emitted from the imported IR and built by the C#
/// compiler, and in the JavaScript emitted from it and run by <c>node</c>. The expected outcome is
/// never computed here: it is what <c>rustc</c> does.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
[RequiresToolchain("rust")]
public sealed class RustFrontendDifferentialTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = System.IO.Directory.CreateTempSubdirectory("polypiler-rust-").FullName;

  [TearDown]
  public void DeleteDirectory() => System.IO.Directory.Delete(this._directory, true);

  private static IEnumerable<TestCaseData> Cases()
    => RustFixture.Names.SelectMany(fixture => RustFixture.Modes.Select(mode => new TestCaseData(fixture, mode).SetArgDisplayNames(fixture, RustFixture.NameOf(mode))));

  /// <summary>
  /// The recordings are what <c>rustc</c> does with the original. Run with <c>POLYPILER_RECORD=1</c> to
  /// write them after a fixture or its calls changed.
  /// </summary>
  [TestCaseSource(nameof(Cases))]
  public void Recording_IsWhatRustcDoes(string fixture, OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    var calls = RustCases.For(fixture, module);
    var outcomes = RustOracle.Outcomes(this._directory, RustFixture.PathOf(fixture), module, calls, mode);
    var lines = calls.Select((call, index) => RustFixture.Line(call, outcomes[index])).ToArray();
    if (Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1")
      RustFixture.Record(RustFixture.RecordingOf(fixture, mode), lines);

    Assert.That(RustFixture.RecordedLines(fixture, mode), Is.EqualTo(lines));
  }

  /// <summary>The C# and the JavaScript emitted from the imported IR do what <c>rustc</c> recorded, call by call.</summary>
  [TestCaseSource(nameof(Cases))]
  public void EmittedCSharpAndJavaScript_DoWhatRustcDoes(string fixture, OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    var calls = RustCases.For(fixture, module);
    var recorded = RustFixture.RecordedLines(fixture, mode);
    var source = Path.Combine(this._directory, fixture + ".rs");
    File.Copy(RustFixture.PathOf(fixture), source);
    string[] common = ["translate", fixture + ".rs", "--from", "rust", "--overflow-checks", RustFixture.NameOf(mode)];
    var (csharpExit, csharpText) = Toolchain.Polypiler(true, this._directory, [.. common, "--to", "csharp", "--output", "cs"]);
    Assert.That(csharpExit, Is.Zero, csharpText);
    var (javaScriptExit, javaScriptText) = Toolchain.Polypiler(true, this._directory, [.. common, "--to", "javascript", "--output", "js"]);
    Assert.That(javaScriptExit, Is.Zero, javaScriptText);

    var build = GeneratedCSharp.Compile(File.ReadAllText(Path.Combine(this._directory, "cs", fixture + ".cs")), false);
    var javaScript = Toolchain.NodeOutcomes(Path.Combine(this._directory, "js"), fixture + ".mjs", calls, true);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var expected = recorded[index][(recorded[index].IndexOf(" => ", StringComparison.Ordinal) + 4)..];
      var csharp = RustFixture.Normalized(build.Outcome(calls[index]));
      var node = RustFixture.Normalized(javaScript[index]);
      if (csharp != expected || node != expected)
        mismatches.Add($"{calls[index]}: rustc {expected}, C# {csharp}, JavaScript {node}");
    }

    TestContext.Out.WriteLine($"{fixture} ({RustFixture.NameOf(mode)}): {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(30), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

}
