using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The sandboxes without processes: the command lines they build, their isolation and the environment of a step.</summary>
[TestFixture]
public sealed class SandboxTests {

  private static readonly SandboxCommand _command = new("/usr/bin/dotnet", ["out/hello.dll", "a b"], "/tmp/polypiler-run-1");

  /// <summary>The bwrap argument vector of bwrap(1): every namespace, the read-only root, a private /tmp, the hidden home, the run's directory.</summary>
  [Test]
  public void Bubblewrap_WrapsTheCommand() {
    var sandbox = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user/");
    Assert.That(sandbox.Isolation, Is.EqualTo(IsolationLevel.Isolated));
    Assert.That(sandbox.CommandLine(_command), Is.EqualTo(new[] {
      "/usr/bin/bwrap", "--unshare-all", "--die-with-parent", "--new-session",
      "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp",
      "--tmpfs", "/home/user",
      "--bind", "/tmp/polypiler-run-1", "/tmp/polypiler-run-1",
      "--remount-ro", "/home/user",
      "--chdir", "/tmp/polypiler-run-1", "--", "/usr/bin/dotnet", "out/hello.dll", "a b"
    }));
  }

  [TestCase(null)]
  [TestCase("")]
  [TestCase("/")]
  public void Bubblewrap_WithoutAHomeToHide_HidesNone(string? home) {
    var line = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command);
    Assert.That(line, Has.No.Member("--remount-ro"));
    Assert.That(line, Has.Exactly(1).EqualTo("--tmpfs"));
  }

  [Test]
  public void Bubblewrap_BindsAToolchainInTheHiddenHomeBackIn() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    var log = new List<string>();
    var line = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user") { Log = log.Add }.CommandLine(_command with { Executable = "/home/user/.dotnet/dotnet" });
    Assert.That(string.Join(' ', line), Does.Contain("--tmpfs /home/user --ro-bind /home/user/.dotnet /home/user/.dotnet --bind "));
    Assert.That(log, Has.One.Contains("only '/home/user/.dotnet' is shown").And.Contains("no root marker"), "the fallback says why");
  }

  /// <summary>A tool right in the home folder shows only itself: the whole home is never bound back in.</summary>
  [Test]
  public void Bubblewrap_ShowsAToolchainRightInTheHomeFolderAsAFileOnly() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    var sandbox = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user") { Log = _ => { } };
    Assert.That(string.Join(' ', sandbox.CommandLine(_command with { Executable = "/home/user/dotnet" })),
      Does.Contain("--tmpfs /home/user --ro-bind /home/user/dotnet /home/user/dotnet --bind "));
    Assert.That(sandbox.Roots(_command with { Executable = "/home/user/bin/tool" }), Is.EqualTo(new[] { "/home/user/bin" }), "~/bin/tool would show the whole home");
    Assert.That(sandbox.Roots(_command with { Executable = "/home/user/.local/bin/tool" }), Is.EqualTo(new[] { "/home/user/.local/bin" }), "~/.local is every user install's prefix");
    Assert.That(sandbox.Roots(_command with { Executable = "/tmp/bin/tool" }), Is.EqualTo(new[] { "/tmp/bin" }), "/tmp/bin/tool would show the whole of /tmp");
    Assert.That(sandbox.Roots(_command with { Executable = "/home/user/tools/node/bin/node" }), Is.EqualTo(new[] { "/home/user/tools/node" }));
  }

  /// <summary>Test design 2: a toolchain the sandbox does not hide needs nothing bound back in, wherever it is.</summary>
  [TestCase("/usr/bin/node")]
  [TestCase("/usr/lib/jvm/java-21/bin/java")]
  [TestCase("/opt/kotlinc/bin/kotlinc")]
  [TestCase("/opt/rustup/toolchains/stable/bin/cargo")]
  [TestCase("/home/username/node/bin/node")]
  public void Bubblewrap_BindsNothingForAToolchainItDoesNotHide(string executable) {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    Assert.That(new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").Roots(_command with { Executable = executable }), Is.Empty);
  }

  /// <summary>The extra roots of a lock entry are bound read-only where the sandbox hides them, and refused where they would show too much.</summary>
  [Test]
  public void Bubblewrap_BindsTheExtraRootsItHides_AndRefusesTheWideOnes() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    var log = new List<string>();
    var sandbox = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user") { Log = log.Add };
    var command = _command with {
      Executable = "/home/user/.cargo/bin/cargo",
      ReadOnlyRoots = ["/home/user/miri/", "/opt/jdk", "relative", "/home/user", "/home", "/", "/tmp", "/home/user/.local", "/home/user/.cargo/bin/sub", "/tmp/sysroot"]
    };
    var line = string.Join(' ', sandbox.CommandLine(command));
    Assert.That(line, Does.Contain("--tmpfs /home/user --ro-bind /home/user/.cargo /home/user/.cargo --ro-bind /home/user/miri /home/user/miri --ro-bind /tmp/sysroot /tmp/sysroot --bind "));
    Assert.That(line, Does.Not.Contain("--bind /home/user/miri"), "nothing extra is writable");
    Assert.That(log.Where(entry => entry.Contains("is not shown")).Select(entry => entry.Split('\'')[1]), Is.EqualTo(new[] { "relative", "/home/user", "/home", "/", "/tmp", "/home/user/.local" }));
    sandbox.CommandLine(command);
    Assert.That(log, Has.Count.EqualTo(6), "each reason is said once");
  }

  [Test]
  public void AStep_NeedsAToolchainThatWasFound() {
    var missing = ToolchainResolution.Failed(Polypiler.Core.Diagnostic.Create(RunnerDiagnostics.IsolationUnavailable, null));
    Assert.That(async () => await StepLauncher.RunAsync(new PollingSandbox(new ProcFileSystemTable()), missing, new ProcessSpec("x", []), Path.GetTempPath(), RunLimits.Default, default),
      Throws.ArgumentException.With.Message.Contains("not found"));
  }

  [Test]
  public void Bubblewrap_RootsNeedACommand()
    => Assert.That(() => new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").Roots(null!), Throws.ArgumentNullException);

  [Test]
  public void Bubblewrap_BindsAToolchainUnderTmpBackIn() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    Assert.That(string.Join(' ', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), null).CommandLine(_command with { Executable = "/tmp/tools/node" })),
      Does.Contain("--tmpfs /tmp --ro-bind /tmp/tools /tmp/tools --bind "));
  }

  [Test]
  public void Bubblewrap_DoesNotBindAToolchainBesideTheHome()
    => Assert.That(new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").CommandLine(_command with { Executable = "/home/username/node" }),
      Has.No.Member("/home/username"));

  [Test]
  [Category("OsIntegration")]
  public void Bubblewrap_BindsTheTargetOfALinkedToolchainToo() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only");
      return;
    }

    var home = Directory.CreateTempSubdirectory("polypiler-home-").FullName;
    try {
      Directory.CreateDirectory(Path.Combine(home, "real"));
      Directory.CreateDirectory(Path.Combine(home, "bin"));
      File.WriteAllText(Path.Combine(home, "real", "tool"), "");
      File.CreateSymbolicLink(Path.Combine(home, "bin", "tool"), Path.Combine(home, "real", "tool"));
      var line = string.Join('|', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command with { Executable = Path.Combine(home, "bin", "tool") }));
      Assert.That(line, Does.Contain($"--ro-bind|{home}/bin|{home}/bin|--ro-bind|{home}/real|{home}/real|"));
      var plain = string.Join('|', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command with { Executable = Path.Combine(home, "real", "tool") }));
      Assert.That(plain, Does.Contain($"--tmpfs|{home}|--ro-bind|{home}/real|{home}/real|--bind|"));
    } finally {
      Directory.Delete(home, true);
    }
  }

  /// <summary>A tool installed with pip into a virtual environment starts only with the environment's libraries beside its folder.</summary>
  [Test]
  public void Bubblewrap_BindsTheVirtualEnvironmentOfAToolchainBackIn() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("bubblewrap is Linux only, and Windows reads these paths with its own separator");
      return;
    }

    var home = Directory.CreateTempSubdirectory("polypiler-home-").FullName;
    try {
      var environment = Path.Combine(home, "venv");
      Directory.CreateDirectory(Path.Combine(environment, "bin"));
      Directory.CreateDirectory(Path.Combine(home, "python", "bin"));
      File.WriteAllText(Path.Combine(environment, "pyvenv.cfg"), $"home = {home}/python/bin\n");
      File.WriteAllText(Path.Combine(environment, "bin", "tool"), "");
      var line = string.Join('|', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command with { Executable = Path.Combine(environment, "bin", "tool") }));
      Assert.That(line, Does.Contain($"--tmpfs|{home}|--ro-bind|{environment}|{environment}|--ro-bind|{home}/python|{home}/python|--bind|"), "the environment and its base interpreter's prefix");
    } finally {
      Directory.Delete(home, true);
    }
  }

  [Test]
  public void Bubblewrap_WithAToolchainInTheRoot_BindsNothingMore()
    => Assert.That(new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").CommandLine(_command with { Executable = "/tool" }),
      Has.Exactly(1).EqualTo("--ro-bind"));

  [Test]
  public void Polling_RunsTheCommandAsItIs() {
    var sandbox = new PollingSandbox(new ProcFileSystemTable());
    Assert.That(sandbox.Isolation, Is.EqualTo(IsolationLevel.Limited));
    Assert.That(sandbox.CommandLine(_command), Is.EqualTo(new[] { "/usr/bin/dotnet", "out/hello.dll", "a b" }));
  }

  [Test]
  public void Polling_WithSetsid_StartsTheCommandInASessionOfItsOwn() {
    Assert.That(new PollingSandbox(new ProcFileSystemTable(), "/usr/bin/setsid").CommandLine(_command),
      Is.EqualTo(new[] { "/usr/bin/setsid", "--wait", "/usr/bin/dotnet", "out/hello.dll", "a b" }));
    Assert.That(PollingSandbox.SessionLaunchers, Is.EqualTo(new[] { "/usr/bin/setsid", "/bin/setsid" }));
  }

  [Test]
  public void JobObject_RunsTheCommandAsItIs_AndIsLimited() {
    if (!OperatingSystem.IsWindows()) {
      Assert.Pass("Job Objects are Windows only; the Windows leg runs the runner tests on them");
      return;
    }

    var sandbox = new JobObjectSandbox();
    Assert.That(sandbox.Isolation, Is.EqualTo(IsolationLevel.Limited));
    Assert.That(sandbox.CommandLine(_command), Is.EqualTo(new[] { "/usr/bin/dotnet", "out/hello.dll", "a b" }));
  }

  /// <summary>Behaviour 3: only the listed variables, nothing inherited.</summary>
  [Test]
  public void TheEnvironment_IsScrubbed() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-run-x");
    var executable = Path.Combine(Path.GetTempPath(), "tools", "dotnet");
    var environment = StepLauncher.Environment(executable, directory);
    var expected = new Dictionary<string, string> {
      ["HOME"] = directory,
      ["LANG"] = "C.UTF-8",
      ["TZ"] = "UTC",
      ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
      ["DOTNET_NOLOGO"] = "1"
    };
    if (OperatingSystem.IsWindows()) {
      var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
      expected["PATH"] = $@"{Path.GetDirectoryName(executable)};{system}\System32;{system}";
      expected["SystemRoot"] = system;
      expected["USERPROFILE"] = directory;
      expected["APPDATA"] = directory;
      expected["LOCALAPPDATA"] = directory;
      expected["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      expected["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
      expected["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
      expected["TEMP"] = directory;
      expected["TMP"] = directory;
    } else {
      expected["PATH"] = $"{Path.GetDirectoryName(executable)}:/usr/local/bin:/usr/bin:/bin";
      expected["TMPDIR"] = directory;
    }

    Assert.That(environment, Is.EquivalentTo(expected));
  }

}
