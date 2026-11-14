using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/jit-shift-xor.feature</c> with the real <c>polypiler</c>
/// executable: the generated C# is compiled in Release and run on this runtime, in its first tier and
/// fully optimized, and compared with the interpreter (#1310).
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class ShiftXorRotationTests {

  private static readonly string[] _scenarios = [
    "A shift-xor with a variable count computes C#'s result at count 0",
    "The unaffected form is not changed"
  ];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/jit-shift-xor.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Translates a method with the real executable to C# and returns the file written.</summary>
  private string Translate(string method, params string[] further) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, printed) = Toolchain.RunPolypiler(this._directory, ["translate", "Snippet.cs", "--from", "csharp", "--to", "csharp", "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, printed);
    return File.ReadAllText(Directory.GetFiles(output).Single());
  }

  /// <summary>Scenario: a shift-xor with a variable count computes C#'s result at count 0.</summary>
  [TestCase(false), TestCase(true)]
  public void ShiftXor_ComputesTheResultOfCSharpAtCountZero(bool fullyOptimized) {
    const string method = "public static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));";
    var text = this.Translate(method, "--opt", "none");
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    foreach (var checking in new[] { false, true }) {
      var build = GeneratedCSharp.Compile(text, checking, fullyOptimized);
      Assert.That(build.Outcome(new PrototypeCall("F", [0x12345678u, 0])), Is.EqualTo("0"));
      var mismatches = Enumerable.Range(0, 34).Select(n => new PrototypeCall("F", [0x12345678u, n]))
        .Where(call => build.Outcome(call) != PrototypeFixture.InterpretedOutcome(module, call)).Select(call => call.ToString());
      Assert.That(mismatches, Is.Empty, $"CheckOverflow {checking}");
    }
  }

  /// <summary>Scenario: the unaffected form is not changed - neither as the source wrote it nor as the table without the row of #1310 writes it.</summary>
  [Test]
  public void UnaffectedForm_IsNotChanged() {
    const string method = "public static uint F(uint x) => (x << 8) | (x >> 24);";
    var text = this.Translate(method, "--opt", "none");
    Assert.That(text, Does.Contain("(x << 8) | (x >> 24)"));
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    var unguarded = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Defect is null));
    Assert.That(CSharpEmitter.Emit(module, CSharpProfile.Default, null, unguarded).Single().Content, Is.EqualTo(CSharpEmitter.Emit(module).Single().Content));
  }

}
