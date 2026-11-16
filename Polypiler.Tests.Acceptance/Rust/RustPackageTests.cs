using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using RunnerChecks = Polypiler.Core.Verification.Runners.OverflowChecks;
using RustRunner = Polypiler.Core.Verification.Runners.RustRunner;
using RustTestOutcome = Polypiler.Core.Verification.Runners.RustTestOutcome;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// A Rust package of several modules, <c>tests/fixtures/prototype/rust/modgraph</c>, imported from its
/// root file with its file modules (#464): its exported functions computed as <c>rustc</c> computes
/// them in both overflow modes - the package built as a library, a driver calling it through its
/// public paths - and its <c>#[test]</c> functions passing and failing as <c>cargo test</c> runs the package.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
[RequiresToolchain("rust")]
public sealed class RustPackageTests {

  private static string Root => Path.Combine(RustFixture.Directory, "modgraph");

  private static string LibraryPath => Path.Combine(Root, "src", "lib.rs");

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-rust-package-").FullName;

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private static ImportResult Import(OverflowChecks mode)
    => RustFrontend.Import(LibraryPath, File.ReadAllText(LibraryPath), new RustImportOptions(mode) { ReadFile = path => File.Exists(path) ? File.ReadAllText(path) : null });

  /// <summary>The exported functions, by their IR names and their paths from the crate root, with the arguments they are called with.</summary>
  private static readonly (string Name, string Path, object[][] Arguments)[] _entries = [
    ("hash_fnv1a", "modgraph::hash::fnv1a", [[Encoding.ASCII.GetBytes("a")], [Array.Empty<byte>()], [Encoding.ASCII.GetBytes("foobar")]]),
    ("rotl", "modgraph::rotl", [[0x80000000u, 1u], [1u, 32u], [0x12345678u, 8u]]),
    ("checksum", "modgraph::checksum", [[Encoding.ASCII.GetBytes("polypiler")], [Array.Empty<byte>()]])
  ];

  [TestCase(OverflowChecks.Panic)]
  [TestCase(OverflowChecks.Wrap)]
  public void ExportedFunctions_ComputeWhatRustcComputes(OverflowChecks mode) {
    var result = Import(mode);
    Assert.That(result.Diagnostics, Is.Empty);
    var module = result.Module!;
    Assert.That(module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.EquivalentTo(_entries.Select(entry => entry.Name)));

    var flag = $"overflow-checks={(mode == OverflowChecks.Panic ? "on" : "off")}";
    var library = Path.Combine(this._directory, "libmodgraph.rlib");
    var (built, _, errors) = RustOracle.Run("rustc", this._directory, "--edition", "2024", "--crate-type", "lib", "--crate-name", "modgraph", "-C", flag, "-o", library, LibraryPath);
    Assert.That(built, Is.Zero, errors);

    var driver = new StringBuilder("fn main() {\n");
    foreach (var (_, path, arguments) in _entries)
      foreach (var call in arguments)
        driver.Append(CultureInfo.InvariantCulture, $"  println!(\"{{}}\", {path}({string.Join(", ", call.Select(Literal))}));\n");

    driver.Append("}\n");
    var main = Path.Combine(this._directory, "main.rs");
    File.WriteAllText(main, driver.ToString());
    var executable = Path.Combine(this._directory, OperatingSystem.IsWindows() ? "driver.exe" : "driver");
    var (linked, _, linkErrors) = RustOracle.Run("rustc", this._directory, "--edition", "2024", "-C", flag, "--extern", $"modgraph={library}", "-o", executable, main);
    Assert.That(linked, Is.Zero, linkErrors);
    var (exit, output, failure) = RustOracle.Run(executable, this._directory);
    Assert.That(exit, Is.Zero, failure);

    var expected = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
    var actual = _entries.SelectMany(entry => entry.Arguments.Select(call => {
      var value = IrInterpreter.Call(module, entry.Name, [.. call.Select(argument => argument is Array array ? array.Clone() : argument)]);
      return PrototypeFixture.Show(value);
    })).ToList();
    Assert.That(actual, Is.EqualTo(expected));
  }

  /// <summary>
  /// Scenario "the source's tests are imported and behave as under cargo test" and test design 19:
  /// the package itself, its <c>Cargo.toml</c> and its source files, run by <c>cargo test</c> of the
  /// pinned toolchain through <see cref="RustRunner"/>, reports <c>fnv1a_of_a</c> and
  /// <c>rotl_wraps</c> passed and <c>wrong</c> failed, and every imported test interpreted has the
  /// outcome cargo reports for it, by name.
  /// </summary>
  [Test]
  public async Task ImportedTests_PassAndFailAsCargoTestReports() {
    var result = Import(OverflowChecks.Panic);
    Assert.That(result.Diagnostics, Is.Empty);
    var report = await RustRun.Runner(RunnerChecks.Panic).RunTestsAsync(RustLayerRun.Crate(Path.GetRelativePath(PrototypeFixture.RepositoryRoot, Root)));
    Assert.That(report.Diagnostics, Is.Empty, report.Output);
    var reported = report.Tests.ToDictionary(test => test.Name, test => test.Outcome);
    Assert.That(reported, Is.EquivalentTo(new Dictionary<string, RustTestOutcome> {
      ["tests::fnv1a_of_a"] = RustTestOutcome.Passed,
      ["tests::rotl_wraps"] = RustTestOutcome.Passed,
      ["tests::wrong"] = RustTestOutcome.Failed
    }), report.Output);

    var interpreted = result.Tests.ToDictionary(test => test, test => RustFixture.Interpreted(result.Module!, new PrototypeCall(test, [])) == "void" ? RustTestOutcome.Passed : RustTestOutcome.Failed);
    Assert.That(interpreted, Is.EquivalentTo(reported.ToDictionary(test => test.Key.Replace("::", "_", StringComparison.Ordinal), test => test.Value)));
  }

  private static string Literal(object value) => value switch {
    byte[] bytes => $"&[{string.Join(", ", bytes.Select(item => item.ToString(CultureInfo.InvariantCulture) + "u8"))}]",
    uint number => number.ToString(CultureInfo.InvariantCulture) + "u32",
    _ => throw new ArgumentException($"no literal for {value}")
  };

}
