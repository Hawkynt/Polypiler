using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// A Rust package of several modules, <c>tests/fixtures/prototype/rust/modgraph</c>, imported from its
/// root file with its file modules (#464): its exported functions computed as <c>rustc</c> computes
/// them in both overflow modes - the package built as a library, a driver calling it through its
/// public paths - and its <c>#[test]</c> functions passing and failing as <c>rustc --test</c> runs them.
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

  [Test]
  public void ImportedTests_PassAndFailAsRustcTestReports() {
    var result = Import(OverflowChecks.Panic);
    Assert.That(result.Diagnostics, Is.Empty);
    var tests = Path.Combine(this._directory, OperatingSystem.IsWindows() ? "tests.exe" : "tests");
    var (built, _, errors) = RustOracle.Run("rustc", this._directory, "--edition", "2024", "--test", "-o", tests, LibraryPath);
    Assert.That(built, Is.Zero, errors);
    var (_, output, _) = RustOracle.Run(tests, this._directory, "--test-threads", "1");
    var reported = Regex.Matches(output, @"^test (\S+) \.\.\. (ok|FAILED)", RegexOptions.Multiline)
      .ToDictionary(match => match.Groups[1].Value.Replace("::", "_", StringComparison.Ordinal), match => match.Groups[2].Value == "ok");
    Assert.That(reported, Has.Count.EqualTo(3), output);

    var interpreted = result.Tests.ToDictionary(test => test, test => RustFixture.Interpreted(result.Module!, new PrototypeCall(test, [])) == "void");
    Assert.That(interpreted, Is.EquivalentTo(reported));
  }

  /// <summary>
  /// #459, the round trip of the package: imported in each overflow mode and written back by the Rust
  /// backend with the crate's modules (src/hash.rs, src/util.rs, src/util/bits.rs, the root in
  /// src/lib.rs) and its tests as #[test] functions, the crate builds with overflow checks on and off,
  /// passes clippy -D warnings and cargo fmt --check, its exports give what rustc's build of the
  /// original gives, and its tests pass and fail as the original's.
  /// </summary>
  [TestCase(OverflowChecks.Panic)]
  [TestCase(OverflowChecks.Wrap)]
  public async System.Threading.Tasks.Task RoundTrip_KeepsTheModulesTheValuesAndTheTests(OverflowChecks mode) {
    var result = Import(mode);
    Assert.That(result.Diagnostics, Is.Empty);
    var module = result.Module!;
    var options = new Polypiler.Core.Backends.Rust.RustEmitOptions { PackageName = "modgraph", Modules = result.Modules, CrateRoot = true, TestFunctions = result.Tests };
    var files = Polypiler.Backends.Testing.RustCrates.Emit(module, options: options);
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/hash.rs", "src/util.rs", "src/util/bits.rs" }));
    Assert.That(Polypiler.Backends.Testing.RustCrates.Text(files, "src/hash.rs"), Does.Contain("use crate::util::bits::mix;"));

    var calls = _entries.SelectMany(entry => entry.Arguments.Select(call => new PrototypeCall(entry.Name, call))).ToList();
    var expected = calls.Select(call => RustFixture.Interpreted(module, call)).ToList();
    foreach (var build in new[] { Polypiler.Core.Verification.Runners.OverflowChecks.Panic, Polypiler.Core.Verification.Runners.OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, build, options: options), Is.EqualTo(expected), $"crate built with overflow checks {build}");

    var runner = RustRun.Runner(Polypiler.Core.Verification.Runners.OverflowChecks.Panic);
    var package = Polypiler.Backends.Testing.RustCrates.Package(files);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    var report = await runner.RunTestsAsync(package);
    Assert.That(report.Tests.ToDictionary(test => test.Name, test => test.Outcome == Polypiler.Core.Verification.Runners.RustTestOutcome.Passed),
      Is.EquivalentTo(new Dictionary<string, bool> { ["tests::fnv1a_of_a"] = true, ["tests::rotl_wraps"] = true, ["tests::wrong"] = false }), report.Output);
  }

  private static string Literal(object value) => value switch {
    byte[] bytes => $"&[{string.Join(", ", bytes.Select(item => item.ToString(CultureInfo.InvariantCulture) + "u8"))}]",
    uint number => number.ToString(CultureInfo.InvariantCulture) + "u32",
    _ => throw new ArgumentException($"no literal for {value}")
  };

}
