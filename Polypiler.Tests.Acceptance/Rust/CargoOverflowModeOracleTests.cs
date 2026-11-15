using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.Frontends.Rust.Cargo;
using RustRunner = Polypiler.Core.Verification.Runners.RustRunner;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The overflow mode <c>translate</c> reads from a Cargo profile against Cargo itself: for each
/// manifest and profile, <c>cargo run --profile</c> builds a program that adds 1 to
/// <c>i32::MAX</c> and prints whether it panicked, and <see cref="CargoOverflowMode"/> must say the
/// same for a source in the same package.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class CargoOverflowModeOracleTests {

  private const string Package = "[package]\nname = \"p\"\nversion = \"0.1.0\"\nedition = \"2024\"\n";

  private const string Program = """
    fn main() {
        std::panic::set_hook(Box::new(|_| {}));
        let r = std::panic::catch_unwind(|| std::hint::black_box(i32::MAX) + std::hint::black_box(1));
        print!("{}", if r.is_ok() { "wrap" } else { "panic" });
    }
    """;

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-cargo-oracle-").FullName;

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private static IEnumerable<TestCaseData> Cases() {
    foreach (var profile in new[] { "dev", "release", "test", "bench" })
      yield return new TestCaseData("", "", profile).SetArgDisplayNames("defaults", profile);
    yield return new TestCaseData("[profile.dev]\noverflow-checks = false\n", "", "test").SetArgDisplayNames("dev off", "test");
    yield return new TestCaseData("[profile.release]\noverflow-checks = true\n", "", "bench").SetArgDisplayNames("release on", "bench");
    yield return new TestCaseData("[profile.fast]\ninherits = \"release\"\n", "", "fast").SetArgDisplayNames("custom", "fast");
    yield return new TestCaseData("[profile.fast]\ninherits = \"dev\"\n\n[profile.dev]\noverflow-checks = false\n", "", "fast").SetArgDisplayNames("custom of dev off", "fast");
    yield return new TestCaseData("[profile]\nrelease = { overflow-checks = true }\n", "", "release").SetArgDisplayNames("inline table", "release");
    yield return new TestCaseData("[profile.dev]\noverflow-checks = true\n", "[workspace]\nmembers = [\"p\"]\nresolver = \"3\"\n\n[profile.dev]\noverflow-checks = false\n", "dev").SetArgDisplayNames("workspace root wins", "dev");
  }

  /// <param name="profiles">The profile tables of the package's manifest.</param>
  /// <param name="workspace">The manifest of a workspace root above the package, or empty for none.</param>
  /// <param name="profile">The profile Cargo builds with.</param>
  [TestCaseSource(nameof(Cases))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public void Mode_IsWhatCargoBuildsWith(string profiles, string workspace, string profile) {
    var package = workspace.Length == 0 ? this._directory : Path.Combine(this._directory, "p");
    Directory.CreateDirectory(Path.Combine(package, "src"));
    if (workspace.Length > 0)
      File.WriteAllText(Path.Combine(this._directory, "Cargo.toml"), workspace);
    File.WriteAllText(Path.Combine(package, "Cargo.toml"), Package + "\n" + profiles);
    var source = Path.Combine(package, "src", "main.rs");
    File.WriteAllText(source, Program);

    var (exitCode, output, error) = RustOracle.Run("cargo", package, "run", "--offline", "--quiet", "--profile", profile);
    Assert.That(exitCode, Is.Zero, error);
    var expected = output == "panic" ? OverflowChecks.Panic : OverflowChecks.Wrap;
    Assert.That(output, Is.AnyOf("panic", "wrap"));
    Assert.That(CargoOverflowMode.ForSource(source, profile).Mode, Is.EqualTo(expected));
  }

}
