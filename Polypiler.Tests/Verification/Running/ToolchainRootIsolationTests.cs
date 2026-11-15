using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Running;

/// <summary>
/// Toolchains installed under a temporary home folder, each laid out as its installer lays it out,
/// run untrusted in real bubblewrap: each reads its own files, does not see the rest of the home, and
/// cannot write into its own root. The executable is a shell script standing in for the toolchain.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public sealed class ToolchainRootIsolationTests {

  /// <summary>Prints the file it is to read, whether the secret is readable, and whether the root takes a new file.</summary>
  private const string Script = """
    #!/bin/sh
    cat "$1" || exit 2
    if cat "$2" >/dev/null 2>&1; then echo leaked; else echo hidden; fi
    if touch "$3" 2>/dev/null; then echo writable; elif touch "$3" 2>&1 | grep -q 'Read-only file system'; then echo read-only; else echo refused; fi
    """;

  /// <summary>A kind of toolchain: where its executable lies, a file of its root it reads, where it tries to write, and the extra roots of its lock entry.</summary>
  public sealed record Layout(string Name, string Executable, string Reads, string Writes, string[] Markers, string[] ExtraRoots) {
    public override string ToString() => this.Name;
  }

  private static readonly Layout[] _layouts = [
    new("JDK", "jdk/bin/java", "jdk/lib/modules", "jdk/new", ["jdk/release"], []),
    new("kotlinc", "kotlinc/bin/kotlinc", "kotlinc/lib/kotlin-preloader.jar", "kotlinc/lib/new", ["kotlinc/build.txt"], []),
    new("rustup toolchain", "rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/bin/cargo", "rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/lib/rustlib/components", "rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/new", ["rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/lib/rustlib/"], []),
    new("rustup proxy", ".cargo/bin/cargo", ".rustup/toolchains/stable/lib/rustlib/components", ".rustup/new", [".cargo/bin/rustup", ".cargo/registry/index"], []),
    new("Miri sysroot", ".cargo/bin/cargo", "miri/lib/rustlib/x86_64-unknown-linux-gnu/lib/libcore.rlib", "miri/new", [".cargo/bin/rustup"], ["miri"]),
    new("Python venv", "venv/bin/pcbasic", "venv/lib/python3.13/site-packages/pcbasic/__init__.py", "venv/new", ["venv/pyvenv.cfg"], []),
    new("Python base", "venv/bin/pcbasic", "python/lib/python3.13/os.py", "python/new", ["venv/pyvenv.cfg"], []),
    new("node", "node/bin/node", "node/lib/node_modules/npm/package.json", "node/new", [], []),
    new("generic", "tool/bin/tool", "tool/share/data", "tool/new", [], []),
    new("no marker", "tools/tool", "tools/data", "tools/new", [], [])
  ];

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-toolchain-home-").FullName;

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Test design 1 and 4, under a home folder in /tmp (both hidden) and under /tmp beside the home (hidden by /tmp only).</summary>
  [Test]
  public async Task AToolchainUnderAHiddenFolder_RunsIsolated_ReadsItsRoot_AndNothingElse([ValueSource(nameof(_layouts))] Layout layout, [Values] bool insideTheHome) {
    var bwrap = BubblewrapExecutable();
    var home = insideTheHome ? this._directory : Path.Combine(this._directory, "home");
    var install = insideTheHome ? this._directory : Path.Combine(this._directory, "opt");
    Directory.CreateDirectory(home);
    var executable = Install(install, layout);
    var secret = Path.Combine(home, "secret.txt");
    File.WriteAllText(secret, "secret");
    var credentials = Path.Combine(install, ".cargo", "credentials.toml");
    if (layout.Markers.Contains(".cargo/bin/rustup"))
      File.WriteAllText(credentials, "token");

    var log = new List<string>();
    var sandbox = new BubblewrapSandbox(bwrap, new ProcFileSystemTable(), home) { Log = log.Add };
    var toolchain = ToolchainResolution.Found(executable, [.. Array.ConvertAll(layout.ExtraRoots, root => Path.Combine(install, root))]);
    var plan = new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(), [], new("tool", [Path.Combine(install, layout.Reads), secret, Path.Combine(install, layout.Writes)]), RunLimits.Default, TrustLevel.Untrusted);
    var result = await new CrossLanguageRunner(sandbox, new Single(toolchain)).RunAsync(plan);

    Assert.That((result.Outcome, result.Isolation), Is.EqualTo((RunOutcome.Completed, IsolationLevel.Isolated)), Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("own\nhidden\nread-only\n"), Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That(File.Exists(Path.Combine(install, layout.Writes)), Is.False);
    if (layout.Markers.Contains(".cargo/bin/rustup")) {
      var leak = await new CrossLanguageRunner(sandbox, new Single(toolchain)).RunAsync(new RunPlan(plan.Files, [], new("tool", [credentials, secret, Path.Combine(install, layout.Writes)]), RunLimits.Default, TrustLevel.Untrusted));
      Assert.That(leak.ExitCode, Is.EqualTo(2), "cargo's credentials stay hidden");
    }

    if (layout.Name == "no marker")
      Assert.That(log, Has.One.Contains("no root marker"), "the fallback says why");
  }

  /// <summary>Test design 2: the toolchain of the lock, wherever this host installed it, runs untrusted in bubblewrap under the real home.</summary>
  [Test]
  public async Task TheNodeOfThisHost_RunsIsolatedWhereverItIsInstalled() {
    _ = BubblewrapExecutable();
    if (Toolchains.Resolve("node").Executable == null)
      Assert.Inconclusive("node is not installed on this host");

    var plan = new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(), [], new("node", ["-p", "require('fs').readdirSync(require('path').join(process.execPath, '..', '..')).includes('bin')"]), RunLimits.Default, TrustLevel.Untrusted);
    var result = await new CrossLanguageRunner(Bubblewrap!, Toolchains).RunAsync(plan);
    Assert.That((result.Outcome, result.ExitCode, result.Isolation), Is.EqualTo((RunOutcome.Completed, 0, IsolationLevel.Isolated)), Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("true\n"), "node sees its own install root");
  }

  /// <summary>The path of a bwrap that can isolate on this host; the test is inconclusive without one.</summary>
  private static string BubblewrapExecutable() {
    if (Bubblewrap == null || Toolchains.Resolve(SandboxFactory.BubblewrapToolchain).Executable is not { } bwrap) {
      Assert.Inconclusive("bubblewrap is not installed or cannot unshare namespaces on this host");
      return "";
    }

    return bwrap;
  }

  /// <summary>Lays a toolchain out under a folder: the script as its executable, its markers, the file it reads, and the environment's configuration.</summary>
  private static string Install(string folder, Layout layout) {
    foreach (var marker in layout.Markers) {
      if (marker.EndsWith('/'))
        Directory.CreateDirectory(Path.Combine(folder, marker));
      else
        Write(Path.Combine(folder, marker), "");
    }

    if (layout.Name.StartsWith("Python", StringComparison.Ordinal))
      File.WriteAllText(Path.Combine(folder, "venv", "pyvenv.cfg"), $"home = {Path.Combine(folder, "python", "bin")}\nversion = 3.13.1\n");
    Write(Path.Combine(folder, layout.Reads), "own\n");
    var executable = Path.Combine(folder, layout.Executable);
    Write(executable, Script.Replace("\r", "") + "\n");
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    return executable;
  }

  private static void Write(string path, string text) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
  }

  /// <summary>Resolves every key to one toolchain.</summary>
  private sealed class Single(ToolchainResolution toolchain) : IToolchainResolver {

    public ToolchainResolution Resolve(string key) => toolchain;

  }

}
