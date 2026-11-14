using System;
using System.Collections.Generic;
using System.IO;
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

    var line = new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").CommandLine(_command with { Executable = "/home/user/.dotnet/dotnet" });
    Assert.That(string.Join(' ', line), Does.Contain("--tmpfs /home/user --ro-bind /home/user/.dotnet /home/user/.dotnet --bind "));
    Assert.That(string.Join(' ', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), "/home/user").CommandLine(_command with { Executable = "/home/user/dotnet" })),
      Does.Contain("--tmpfs /home/user --ro-bind /home/user /home/user --bind "));
  }

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
      File.WriteAllText(Path.Combine(environment, "pyvenv.cfg"), "");
      File.WriteAllText(Path.Combine(environment, "bin", "tool"), "");
      var line = string.Join('|', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command with { Executable = Path.Combine(environment, "bin", "tool") }));
      Assert.That(line, Does.Contain($"--tmpfs|{home}|--ro-bind|{environment}|{environment}|--bind|"));
      File.Delete(Path.Combine(environment, "pyvenv.cfg"));
      var plain = string.Join('|', new BubblewrapSandbox("/usr/bin/bwrap", new ProcFileSystemTable(), home).CommandLine(_command with { Executable = Path.Combine(environment, "bin", "tool") }));
      Assert.That(plain, Does.Contain($"--tmpfs|{home}|--ro-bind|{environment}/bin|{environment}/bin|--bind|"));
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
