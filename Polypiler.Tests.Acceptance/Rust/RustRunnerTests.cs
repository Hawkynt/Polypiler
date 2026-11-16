using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The Rust runner on the pinned toolchain (#468): a fixture file or a package built, tested,
/// format-checked and linted, and its functions called on input images through the generated driver,
/// in both overflow modes. The fixtures lie in <c>tests/fixtures/rust/runner</c>; the format and lint
/// cases reuse the crates of the Rust CI layer.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustRunnerTests {

  private const string Fixtures = "tests/fixtures/rust/runner";

  private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Fixture(string name)
    => RustRunner.Package(name, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, name)));

  private static CallSignature Signature(ImageType? result, params ImageType[] parameters)
    => new([.. parameters.Select((type, index) => new CallParameter("p" + index, type, ParameterMode.In))], result);

  private static byte[] Words(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

  private static IEnumerable<string> Shown(RustExecution execution)
    => execution.Outcomes!.Select(outcome => outcome.Image is null ? "!" + outcome.Trap : Convert.ToHexStringLower(outcome.Image));

  /// <summary>Scenario 1 and 2 of run-rs.feature, test design 1 and 6: the overflow mode changes the result, not the source.</summary>
  [TestCase(OverflowChecks.Panic, "05000000", "!overflow", "00000000")]
  [TestCase(OverflowChecks.Wrap, "05000000", "00000000", "00000000")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Export_IsCalledOnEveryImage(OverflowChecks checks, string first, string second, string third) {
    var add = new RustExport("fixture::add", Signature(ImageType.U32, ImageType.U32, ImageType.U32));
    var execution = await RustRun.Runner(checks).ExecuteAsync(Fixture("add.rs"), "fixture_package", [add], [(0, Words(2, 3)), (0, Words(-1, 1)), (0, Words(0, 0))]);
    Assert.That(execution.Diagnostics, Is.Empty);
    Assert.That(Shown(execution), Is.EqualTo(new[] { first, second, third }));
  }

  /// <summary>The trap outline of run-rs.feature and test design 7 and 9.</summary>
  [TestCase(OverflowChecks.Wrap, true)]
  [TestCase(OverflowChecks.Panic, false)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task EachKindOfPanic_IsItsTrapClass(OverflowChecks checks, bool debugAssertions) {
    RustExport Export(string name, ImageType? result, params ImageType[] parameters) => new("fixture::" + name, Signature(result, parameters));
    var exports = new[] {
      Export("div", ImageType.I32, ImageType.I32, ImageType.I32), Export("first", ImageType.U32, ImageType.U32), Export("boom", ImageType.U32),
      Export("never", ImageType.U32), Export("generated", ImageType.U32), Export("checked", ImageType.U32, ImageType.U32)
    };
    var execution = await RustRun.Runner(checks, debugAssertions).ExecuteAsync(Fixture("traps.rs"), "fixture_package", exports,
      [(0, Words(1, 0)), (0, Words(int.MinValue, -1)), (0, Words(7, 2)), (1, Words(1)), (1, Words(3)), (2, []), (3, []), (4, []), (5, Words(0))]);
    Assert.That(execution.Diagnostics, Is.Empty);
    Assert.That(Shown(execution), Is.EqualTo(new[] {
      "!divide-by-zero", "!overflow", "03000000", "02000000", "!null", "!other:panic", "!unreachable", "!other:InvalidCast",
      debugAssertions ? "!other:panic" : "ffffffff"
    }));
  }

  /// <summary>Scenario 6 and test design 19: a fixture that does not compile is reported, not executed.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task FixtureThatDoesNotCompile_IsReported() {
    var execution = await RustRun.Runner(OverflowChecks.Panic).ExecuteAsync(Fixture("broken.rs"), "fixture_package", [new RustExport("fixture::f", Signature(ImageType.U32))], [(0, [])]);
    Assert.That(execution.Outcomes, Is.Null);
    Assert.That(execution.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1591" }));
    Assert.That(execution.Diagnostics[1].Message, Does.Contain("E0308"));
    var build = await RustRun.Runner(OverflowChecks.Panic).CompileAsync(Fixture("broken.rs"));
    Assert.That((build.Succeeded, build.Messages.Any(message => message.Code == "E0308")), Is.EqualTo((false, true)));
  }

  /// <summary>Scenario 7 and test design 20: a signature that does not fit the declaration is refused.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task SignatureThatDoesNotFit_IsRefused() {
    var execution = await RustRun.Runner(OverflowChecks.Panic).ExecuteAsync(Fixture("add.rs"), "fixture_package",
      [new RustExport("fixture::add", Signature(ImageType.U32, ImageType.I64, ImageType.U32))], [(0, new byte[12])]);
    Assert.That(execution.Diagnostics.Single().Code, Is.EqualTo("PLY1595"));
    Assert.That(execution.Outcomes, Is.Null);
  }

  /// <summary>Test design 21: the process dies inside a call.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ProcessThatDies_GivesNoOutcome() {
    var execution = await RustRun.Runner(OverflowChecks.Panic).ExecuteAsync(Fixture("abort.rs"), "fixture_package",
      [new RustExport("fixture::f", Signature(ImageType.U32, ImageType.U32))], [(0, Words(1)), (0, Words(0)), (0, Words(2))]);
    Assert.That(execution.Outcomes, Is.Null);
    Assert.That(execution.Diagnostics.Single().Code, Is.EqualTo("PLY1592"));
  }

  /// <summary>Scenario 5 and test design 3, 24 and 25: build, tests, format and lints of a package.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Package_IsBuiltTestedFormatCheckedAndLinted() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var ok = RustLayerRun.Crate(Fixtures + "/ok");
    var build = await runner.CompileAsync(ok);
    Assert.That((build.Succeeded, build.Messages.Count(message => message.Level == "error")), Is.EqualTo((true, 0)));
    var report = await runner.RunTestsAsync(ok);
    Assert.That((report.Tests.Count, report.Passed, report.Failed), Is.EqualTo((2, 2, 0)));
    Assert.That(await runner.FormatCheckAsync(ok), Is.Empty);
    Assert.That(await runner.ClippyAsync(ok), Is.Empty);

    var failing = await runner.RunTestsAsync(RustLayerRun.Crate(Fixtures + "/failing"));
    Assert.That((failing.Passed, failing.Failed), Is.EqualTo((1, 1)));
    Assert.That(failing.Tests.Single(test => test.Outcome == RustTestOutcome.Failed).Name, Is.EqualTo("tests::doubles"));
    Assert.That(failing.Output, Does.Contain("left: 42").And.Contain("right: 43"));

    Assert.That(await runner.FormatCheckAsync(RustLayerRun.Crate(RustLayerRun.Fixtures + "/unformatted")), Is.Not.Empty);
    Assert.That(await runner.ClippyAsync(RustLayerRun.Crate(RustLayerRun.Fixtures + "/clippy-warn")), Is.Not.Empty);
  }

  /// <summary>Test design 27: a package with a registry dependency fails offline without reaching the network.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task RegistryDependency_FailsOffline() {
    var build = await RustRun.Runner(OverflowChecks.Panic).CompileAsync(RustLayerRun.Crate(RustLayerRun.Fixtures + "/registry-dep"));
    Assert.That(build.Succeeded, Is.False);
    Assert.That(build.Diagnostics.First().Code, Is.EqualTo("PLY0030"));
  }

}
