using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The five command lines of the Rust CI layer (PRD §7.3), as steps of the cross-language runner:
/// <c>cargo fmt --check</c>, <c>cargo clippy --all-targets -- -D warnings</c>, <c>cargo test</c>,
/// <c>cargo run --quiet</c> on the stable toolchain <c>rust</c>, and <c>cargo +&lt;rust-miri&gt; miri test</c>
/// on the dated nightly <c>rust-miri</c>, which only ever runs Miri. Every step runs with
/// <c>CARGO_NET_OFFLINE=true</c>: a crate that needs the registry fails instead of reaching the network.
/// Because the runner gives each step the run's directory as its home, the steps are told where rustup
/// keeps its toolchains and where the Miri sysroot was prepared.
/// </summary>
public sealed partial class RustCommands {

  /// <summary>The key of the stable toolchain in <c>tooling/toolchains.lock</c>; its executable is <c>cargo</c>.</summary>
  public const string Toolchain = "rust";

  /// <summary>The key of the nightly that runs Miri; its version is the rustup channel, for example <c>nightly-2026-11-01</c>.</summary>
  public const string MiriToolchain = "rust-miri";

  /// <summary>What the layer records for a crate it does not run Miri on.</summary>
  public const string MiriNotApplicable = "miri: not applicable (no unsafe code)";

  /// <summary>The variable that keeps cargo off the network.</summary>
  public const string OfflineVariable = "CARGO_NET_OFFLINE";

  /// <summary>The variable that names the sysroot <c>cargo miri setup</c> prepared.</summary>
  public const string MiriSysrootVariable = "MIRI_SYSROOT";

  /// <summary>The variable that names rustup's folder of toolchains.</summary>
  public const string RustupHomeVariable = "RUSTUP_HOME";

  /// <summary>Creates the commands for given toolchain versions and host folders.</summary>
  /// <param name="stable">The version of the stable toolchain, which rustup selects for every step but Miri.</param>
  /// <param name="miriChannel">The rustup channel of the nightly that runs Miri.</param>
  /// <param name="rustupHome">Rustup's folder of toolchains; <see langword="null"/> where cargo is not rustup's.</param>
  /// <param name="miriSysroot">The sysroot Miri runs against; <see langword="null"/> when none was prepared.</param>
  /// <exception cref="ArgumentException">A version is empty.</exception>
  public RustCommands(string stable, string miriChannel, string? rustupHome, string? miriSysroot) {
    ArgumentException.ThrowIfNullOrEmpty(stable);
    ArgumentException.ThrowIfNullOrEmpty(miriChannel);
    this.Stable = stable;
    this.MiriChannel = miriChannel;
    this.RustupHome = string.IsNullOrEmpty(rustupHome) ? null : rustupHome;
    this.MiriSysroot = string.IsNullOrEmpty(miriSysroot) ? null : miriSysroot;
  }

  /// <summary>The version of the stable toolchain.</summary>
  public string Stable { get; }

  /// <summary>The rustup channel of the nightly that runs Miri.</summary>
  public string MiriChannel { get; }

  /// <summary>Rustup's folder of toolchains, or <see langword="null"/>.</summary>
  public string? RustupHome { get; }

  /// <summary>The sysroot Miri runs against, or <see langword="null"/>.</summary>
  public string? MiriSysroot { get; }

  /// <summary>The host folders a step reads besides the toolchain's own: rustup's toolchains and the Miri sysroot.</summary>
  public IReadOnlyList<string> HostFolders => new[] { this.RustupHome, this.MiriSysroot }.OfType<string>().ToArray();

  /// <summary><c>cargo fmt --check</c>: exit code 1 and a diff on standard output when a file is not formatted.</summary>
  public ProcessSpec Format => this.Cargo("fmt", "--check");

  /// <summary><c>cargo clippy --all-targets -- -D warnings</c>: every lint is an error.</summary>
  public ProcessSpec Clippy => this.Cargo("clippy", "--all-targets", "--", "-D", "warnings");

  /// <summary><c>cargo run --quiet</c>: the crate's binary, without cargo's own progress lines.</summary>
  public ProcessSpec Run => this.Cargo("run", "--quiet");

  /// <summary>The four stable commands of the smoke crate, in the order the layer runs them.</summary>
  public IReadOnlyList<ProcessSpec> Smoke => [this.Format, this.Clippy, this.Test(), this.Run];

  /// <summary>Reads the versions from a lock and the host folders from the environment.</summary>
  /// <param name="toolchains">The lock.</param>
  /// <param name="environment">Reads an environment variable: <c>RUSTUP_HOME</c>, <c>MIRI_SYSROOT</c>, <c>XDG_CACHE_HOME</c> and <c>HOME</c>.</param>
  /// <returns>
  /// The commands. Rustup's folder is <c>RUSTUP_HOME</c>, else <c>~/.rustup</c> where it exists; the
  /// Miri sysroot is <c>MIRI_SYSROOT</c>, else the cache folder <c>cargo miri setup</c> uses by default
  /// on Linux, where it exists.
  /// </returns>
  public static RustCommands FromLock(ToolchainLock toolchains, Func<string, string?> environment) {
    ArgumentNullException.ThrowIfNull(toolchains);
    ArgumentNullException.ThrowIfNull(environment);
    var home = environment("HOME");
    var rustupHome = environment(RustupHomeVariable) ?? Existing(home, ".rustup");
    var cache = environment("XDG_CACHE_HOME") ?? (home is null ? null : Path.Combine(home, ".cache"));
    var miriSysroot = environment(MiriSysrootVariable) ?? Existing(cache, "miri");
    return new(toolchains[Toolchain].Version, toolchains[MiriToolchain].Version, rustupHome, miriSysroot);
  }

  /// <summary><c>cargo test</c>, with further arguments such as <c>--features</c> or a test name filter.</summary>
  /// <param name="arguments">The arguments after <c>test</c>.</param>
  public ProcessSpec Test(params string[] arguments) => this.Cargo(["test", .. arguments]);

  /// <summary><c>cargo build</c>, with further arguments such as <c>--features</c>.</summary>
  /// <param name="arguments">The arguments after <c>build</c>.</param>
  public ProcessSpec Build(params string[] arguments) => this.Cargo(["build", .. arguments]);

  /// <summary><c>cargo +&lt;rust-miri&gt; miri test</c>, with further arguments such as a test name filter.</summary>
  /// <param name="arguments">The arguments after <c>test</c>.</param>
  public ProcessSpec Miri(params string[] arguments) {
    var environment = this.Environment();
    if (this.MiriSysroot is { } sysroot)
      environment[MiriSysrootVariable] = sysroot;
    return new(Toolchain, ["+" + this.MiriChannel, "miri", "test", .. arguments]) { Environment = environment };
  }

  /// <summary>Whether Miri applies to a crate: whether one of its Rust sources contains the token <c>unsafe</c>.</summary>
  /// <param name="files">The crate's files by name relative to it, as a plan carries them.</param>
  public static bool NeedsMiri(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) {
    ArgumentNullException.ThrowIfNull(files);
    return files.Any(file => file.Key.EndsWith(".rs", StringComparison.Ordinal) && UnsafeToken().IsMatch(Encoding.UTF8.GetString(file.Value.Span)));
  }

  /// <summary>How many tests the <c>test result:</c> lines of a <c>cargo test</c> output report as passed, summed over every test binary.</summary>
  /// <param name="output">The standard output of <c>cargo test</c>.</param>
  public static int Passed(string output) => Count(output, "passed");

  /// <summary>How many tests the <c>test result:</c> lines of a <c>cargo test</c> output report as failed.</summary>
  /// <param name="output">The standard output of <c>cargo test</c>.</param>
  public static int Failed(string output) => Count(output, "failed");

  /// <summary>Why a smoke crate's <c>cargo test</c> proves nothing: it ran no test. A green run of zero tests is no evidence.</summary>
  /// <param name="output">The standard output of <c>cargo test</c>.</param>
  /// <returns>The reason, or <see langword="null"/> when at least one test passed.</returns>
  public static string? SmokeProblem(string output)
    => Passed(output) > 0 ? null : "a smoke crate must run at least one test, and cargo test passed none";

  private static int Count(string output, string outcome) {
    ArgumentNullException.ThrowIfNull(output);
    return TestResult().Matches(output).Sum(match => int.Parse(match.Groups[outcome].Value, CultureInfo.InvariantCulture));
  }

  private ProcessSpec Cargo(params string[] arguments) => new(Toolchain, arguments) { Environment = this.Environment() };

  /// <summary>
  /// The variables every step adds: offline, colourless, the stable toolchain for rustup, which must
  /// neither install a toolchain on its own nor look for its folder in the run's directory.
  /// </summary>
  private Dictionary<string, string> Environment() {
    var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
      [OfflineVariable] = "true",
      ["CARGO_TERM_COLOR"] = "never",
      ["RUSTUP_TOOLCHAIN"] = this.Stable,
      ["RUSTUP_AUTO_INSTALL"] = "0"
    };
    if (this.RustupHome is { } rustupHome)
      environment[RustupHomeVariable] = rustupHome;
    return environment;
  }

  private static string? Existing(string? parent, string name)
    => parent is null || !Directory.Exists(Path.Combine(parent, name)) ? null : Path.Combine(parent, name);

  [GeneratedRegex(@"\bunsafe\b", RegexOptions.CultureInvariant)]
  private static partial Regex UnsafeToken();

  [GeneratedRegex(@"^test result: \w+\. (?<passed>\d+) passed; (?<failed>\d+) failed;", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex TestResult();

}
