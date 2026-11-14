using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Toolchains;
using Polypiler.Tests.Verification.Fuzzing;
using Polypiler.Tooling;
using Polypiler.Tooling.Fuzz;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Fuzz;

/// <summary>
/// <c>polypiler-tooling fuzz</c>: the command line, the diagnostics and the exit codes. The targets run
/// in process here; the worker process is tested in <see cref="WorkerProcessExecutorTests"/> and by
/// the acceptance scenarios.
/// </summary>
[TestFixture]
public sealed class FuzzCommandTests {

  private static readonly string _assembly = typeof(DemoMarkerTarget).Assembly.Location;
  private static readonly byte[] _seed = [0x78, 0x78, 0x50, 0x4C, 0x59, 0x78, 0x78];

  private FuzzWorkspace _workspace = null!;
  private List<string> _instrumented = null!;

  [SetUp]
  public void CreateWorkspace() {
    this._workspace = new FuzzWorkspace();
    this._instrumented = [];
  }

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private FuzzCommand Command(IProcessRunner? runner = null, string platform = "linux", string? path = null)
    => new(this._workspace.Root, (target, _) => new InProcessExecutor(target), runner ?? new ScriptedRunner(_ => throw new Win32Exception("not scripted")),
      name => name == "PATH" ? path ?? "" : null, platform, this._instrumented.Add);

  private (int ExitCode, string Output, string Error) Invoke(FuzzCommand command, params string[] args) {
    using var output = new StringWriter { NewLine = "\n" };
    using var error = new StringWriter { NewLine = "\n" };
    var exitCode = command.Run(args, output, error, Stream.Null, Stream.Null);
    return (exitCode, output.ToString(), error.ToString());
  }

  private (int ExitCode, string Output, string Error) Invoke(params string[] args) => this.Invoke(this.Command(), args);

  private static string[] Fuzz(string verb, string target, params string[] more) => [verb, "--target", target, "--assembly", _assembly, .. more];

  [Test]
  public void ACrash_IsReportedWithPLY0070AndRecorded() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    var (exitCode, output, error) = this.Invoke(Fuzz("run", "demo-marker", "--iterations", "1"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output, Does.StartWith("fuzz target 'demo-marker': 1 iterations, 1 findings, 1 worker starts, "));
    Assert.That(output, Does.EndWith(" executions per second (engine inprocess, seed 1)\n"));
    Assert.That(error, Is.EqualTo($"polypiler-tooling: error PLY0070: fuzz target 'demo-marker': Crash 'crash:System.InvalidOperationException@{typeof(DemoMarkerTarget).FullName}.Execute' "
      + "at iteration 0 (seed 1); minimized to 3 bytes: artifacts/fuzz/demo-marker/b8d17c5a6e84d640.bin\n"));
    Assert.That(File.Exists(Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker", "b8d17c5a6e84d640.json")), Is.True);
  }

  [Test]
  public void ACleanRun_SucceedsWithTheDefaults() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [0x41]));
    var (exitCode, output, error) = this.Invoke(Fuzz("run", "demo-clean"));
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.StartWith("fuzz target 'demo-clean': 10000 iterations, 0 findings, 1 worker starts, "));
  }

  [Test]
  public void TheSeed_IsTakenFromTheCommandLine() {
    this._workspace.Corpus("demo-zero", ("seed.bin", [0x41]));
    var (exitCode, output, error) = this.Invoke(Fuzz("run", "demo-zero", "--seed", "2", "--iterations", "2000"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output, Does.EndWith("(engine inprocess, seed 2)\n"));
    Assert.That(error, Does.Contain("(seed 2); minimized to 1 bytes: artifacts/fuzz/demo-zero/6e340b9cffb37a98.bin"));
  }

  /// <summary>The corpus is found above the current directory, as from a subdirectory of a checkout.</summary>
  [Test]
  public void TheCorpus_IsFoundAboveTheCurrentDirectory() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [0x41]));
    var below = Directory.CreateDirectory(Path.Combine(this._workspace.Root, "src", "deep")).FullName;
    var command = new FuzzCommand(below, (target, _) => new InProcessExecutor(target), new ScriptedRunner(""), _ => null, "linux", _ => { });
    Assert.That(this.Invoke(command, Fuzz("run", "demo-clean", "--iterations", "5")).ExitCode, Is.Zero);
  }

  /// <summary>Test design 17: a record that cannot be written is PLY0005 after the finding's PLY0070.</summary>
  [Test]
  public void ARecordThatCannotBeWritten_IsPLY0005() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    File.WriteAllText(this._workspace.Artifacts, "a file where the directory should be");
    var (exitCode, _, error) = this.Invoke(Fuzz("run", "demo-marker", "--iterations", "1"));
    var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(lines, Has.Length.EqualTo(2));
    Assert.That(lines[0], Does.StartWith("polypiler-tooling: error PLY0070: fuzz target 'demo-marker': Crash"));
    Assert.That(lines[1], Does.StartWith($"polypiler-tooling: error PLY0005: the output directory '{Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker")}' cannot be written: "));
  }

  /// <summary>Test design 14 and 8: what makes a run impossible is a usage error.</summary>
  [TestCase(new[] { "run", "--target", "no-such-target" }, "unknown fuzz target 'no-such-target'", TestName = "An unknown target")]
  [TestCase(new[] { "run", "--target", "demo-zero", "--assembly", "@" }, "corpus directory '*' does not exist", TestName = "A missing corpus directory")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@" }, "corpus is empty: *", TestName = "An empty corpus")]
  [TestCase(new string[0], "fuzz needs 'run', 'minimize' or 'replay'", TestName = "No fuzz command")]
  [TestCase(new[] { "shake" }, "unknown fuzz command 'shake'", TestName = "An unknown fuzz command")]
  [TestCase(new[] { "afl-worker" }, "unknown fuzz command 'afl-worker'", TestName = "The AFL++ worker outside a process")]
  [TestCase(new[] { "run" }, "option '--target' is required", TestName = "No target")]
  [TestCase(new[] { "run", "--input", "x" }, "unknown option '--input'", TestName = "An option of another command")]
  [TestCase(new[] { "run", "--target" }, "option '--target' requires a value", TestName = "An option without its value")]
  [TestCase(new[] { "run", "--target", "a", "--target", "b" }, "option '--target' is given more than once", TestName = "A repeated option")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@", "--seed", "-1" }, "option '--seed' does not allow '-1'", TestName = "A negative seed")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@", "--seed", "x" }, "option '--seed' does not allow 'x'", TestName = "A seed that is not a number")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@", "--iterations", "-1" }, "option '--iterations' must not be negative", TestName = "Negative iterations")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@", "--duration", "0" }, "option '--duration' must be at least 1", TestName = "A duration of zero")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "@", "--engine", "libfuzzer" }, "option '--engine' does not allow 'libfuzzer'", TestName = "An unknown engine")]
  [TestCase(new[] { "run", "--target", "demo-clean", "--assembly", "no-such.dll" }, "assembly 'no-such.dll' cannot be loaded: *", TestName = "An assembly that is not there")]
  [TestCase(new[] { "minimize", "--target", "demo-zero", "--assembly", "@" }, "fuzz minimize needs '--input'", TestName = "Minimize without an input")]
  [TestCase(new[] { "minimize", "--target", "demo-zero", "--assembly", "@", "--input", "missing.bin" }, "input 'missing.bin' cannot be read: *", TestName = "Minimize an input that is not there")]
  [TestCase(new[] { "replay", "--target", "no-such-target" }, "unknown fuzz target 'no-such-target'", TestName = "Replay an unknown target")]
  [TestCase(new[] { "worker", "--target", "no-such-target" }, "unknown fuzz target 'no-such-target'", TestName = "A worker for an unknown target")]
  public void AnUnusableCommandLine_IsPLY0003(string[] args, string message) {
    Directory.CreateDirectory(Path.Combine(this._workspace.TargetDirectory("demo-clean"), "corpus"));
    var (exitCode, output, error) = this.Invoke([.. args.Select(arg => arg == "@" ? _assembly : arg)]);
    Assert.That((exitCode, output), Is.EqualTo((2, "")));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0003: "));
    var expected = message.Split('*');
    Assert.That(error.TrimEnd('\n')["polypiler-tooling: error PLY0003: ".Length..], Does.StartWith(expected[0]).And.EndWith(expected[^1]));
  }

  [Test]
  public void AnAssemblyThatIsNotOne_IsPLY0003() {
    var fake = Path.Combine(this._workspace.Root, "fake.dll");
    File.WriteAllText(fake, "not an assembly");
    var (exitCode, _, error) = this.Invoke("replay", "--assembly", fake);
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(error, Does.StartWith($"polypiler-tooling: error PLY0003: assembly '{fake}' cannot be loaded: "));
  }

  [Test]
  public void Minimize_RecordsTheMinimizedInput() {
    File.WriteAllBytes(Path.Combine(this._workspace.Root, "crash.bin"), _seed);
    var (exitCode, output, error) = this.Invoke(Fuzz("minimize", "demo-marker", "--input", "crash.bin"));
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.EndWith("at iteration 0 (seed 0); minimized to 3 bytes: artifacts/fuzz/demo-marker/b8d17c5a6e84d640.bin\n"));
  }

  [Test]
  public void Minimize_AnInputWithoutAFinding_Succeeds() {
    File.WriteAllBytes(Path.Combine(this._workspace.Root, "clean.bin"), [1, 2]);
    var (exitCode, output, error) = this.Invoke(Fuzz("minimize", "demo-marker", "--input", "clean.bin"));
    Assert.That((exitCode, output, error), Is.EqualTo((0, "fuzz target 'demo-marker': input 'clean.bin' shows no finding\n", "")));
  }

  [Test]
  public void Replay_PassesWhenNoRegressionFailsAgain() {
    this._workspace.Regressions("demo-zero", ("fixed.bin", [0x41]));
    this._workspace.Regressions("demo-marker", ("fixed.bin", [0x50]));
    var (exitCode, output, error) = this.Invoke("replay", "--assembly", _assembly);
    Assert.That((exitCode, output, error), Is.EqualTo((0, "0 regression inputs fail\n", "")));
  }

  /// <summary>Test design 15 through the tooling: a regression input that fails again is named.</summary>
  [Test]
  public void Replay_NamesARegressionThatFailsAgain() {
    this._workspace.Regressions("demo-zero", ("back.bin", [0x00]));
    this._workspace.Regressions("demo-marker", ("other.bin", "PLY"u8.ToArray()));
    var (exitCode, output, error) = this.Invoke("replay", "--target", "demo-zero", "--assembly", _assembly);
    Assert.That((exitCode, output), Is.EqualTo((1, "1 regression inputs fail\n")));
    Assert.That(error, Is.EqualTo($"polypiler-tooling: error PLY0070: regression {Path.Combine(this._workspace.Regressions("demo-zero"), "regressions", "back.bin")}: "
      + $"Crash 'crash:System.ArgumentException@{typeof(DemoZeroTarget).FullName}.Execute'\n"));
  }

  [Test]
  public void TheWorker_ServesTheStreamsItIsGiven() {
    var request = new byte[4 + 3];
    BinaryPrimitives.WriteInt32LittleEndian(request, 3);
    "PLY"u8.CopyTo(request.AsSpan(4));
    using var requests = new MemoryStream(request);
    using var responses = new MemoryStream();
    using var output = new StringWriter { NewLine = "\n" };
    using var error = new StringWriter { NewLine = "\n" };
    var exitCode = this.Command().Run(["worker", "--target", "demo-marker", "--assembly", _assembly], output, error, requests, responses);
    Assert.That((exitCode, error.ToString()), Is.EqualTo((0, "")));
    Assert.That(responses.ToArray()[0], Is.EqualTo(FuzzWorker.Ready));
    Assert.That(responses.ToArray()[1], Is.EqualTo(1), "the crash");
  }

  [Test]
  public void TheWorkerCommand_IsTheExecutableOrDotnetWithTheAssembly() {
    Assert.That(FuzzCommand.WorkerCommand(this._workspace.Root, false), Is.EqualTo(new[] { "dotnet", Path.Combine(this._workspace.Root, "polypiler-tooling.dll") }));
    File.WriteAllText(Path.Combine(this._workspace.Root, "polypiler-tooling.exe"), "");
    Assert.That(FuzzCommand.WorkerCommand(this._workspace.Root, true), Is.EqualTo(new[] { Path.Combine(this._workspace.Root, "polypiler-tooling.exe") }));
    Assert.That(() => FuzzCommand.WorkerExecutor(null!, []), Throws.ArgumentNullException);
    Assert.That(() => FuzzCommand.WorkerExecutor(new DemoCleanTarget(), null!), Throws.ArgumentNullException);
    using var executor = FuzzCommand.WorkerExecutor(new DemoCleanTarget(), []);
    Assert.That(executor.Starts, Is.Zero);
  }

  [Test]
  public void TheShell_ListsTheFuzzVerbsAndRoutesToThem() {
    using var output = new StringWriter { NewLine = "\n" };
    Program.Run([], output, TextWriter.Null);
    Assert.That(output.ToString(), Does.Contain("polypiler-tooling fuzz run --target <id>").And.Contain("fuzz minimize").And.Contain("fuzz replay"));
    using var error = new StringWriter { NewLine = "\n" };
    Assert.That(Program.Run(["fuzz"], TextWriter.Null, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Is.EqualTo("polypiler-tooling: error PLY0003: fuzz needs 'run', 'minimize' or 'replay'\n"));
    Assert.That(() => this.Command().Run(null!, TextWriter.Null, TextWriter.Null), Throws.ArgumentNullException);
    Assert.That(() => this.Command().Run(null!, TextWriter.Null, TextWriter.Null, Stream.Null, Stream.Null), Throws.ArgumentNullException);
  }

  /// <summary>Test design 16: without AFL++ the run stops with PLY0021 before anything is instrumented.</summary>
  [Test]
  public void AflWithoutAflPlusPlus_IsPLY0021AndInstrumentsNothing() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    this.CopyLock();
    var (exitCode, output, error) = this.Invoke(Fuzz("run", "demo-marker", "--engine", "afl"));
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0021: toolchain 'aflplusplus' is not installed (probe: afl-fuzz -h)"));
    Assert.That(this._instrumented, Is.Empty);
    Assert.That(Directory.Exists(this._workspace.Artifacts), Is.False);
  }

  [Test]
  public void AflOnWindows_IsPLY0021() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    this.CopyLock();
    var (exitCode, _, error) = this.Invoke(this.Command(platform: "windows"), Fuzz("run", "demo-marker", "--engine", "afl"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Is.EqualTo("polypiler-tooling: error PLY0021: toolchain 'aflplusplus' is not installed: not used on windows\n"));
  }

  [Test]
  public void AflAtAnotherVersion_IsPLY0022() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    this.CopyLock();
    var (exitCode, _, error) = this.Invoke(this.Command(new ScriptedRunner("afl-fuzz++4.00c based on afl"), path: this.FakeAfl()), Fuzz("run", "demo-marker", "--engine", "afl"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0022: toolchain 'aflplusplus' is 4.00c, the lock pins 5.03c"));
  }

  /// <summary>What AFL++ keeps in its crashes and hangs folders is confirmed, minimized and recorded as engine <c>afl</c>.</summary>
  [Test]
  public void AflFindings_AreConfirmedMinimizedAndRecorded() {
    this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => {
      if (argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal))
        return new(1, "\u001b[1;96mafl-fuzz++5.03c\u001b[0m based on afl by Michal Zalewski\n", "", false);
      var output = argv[Array.IndexOf([.. argv], "-o") + 1];
      Directory.CreateDirectory(Path.Combine(output, "default", "crashes"));
      // AFL++'s own name; ':' is not allowed in a Windows file name, where AFL++ never runs.
      var name = OperatingSystem.IsWindows() ? "id_000000,sig_06" : "id:000000,sig:06";
      File.WriteAllBytes(Path.Combine(output, "default", "crashes", name), "aPLYb"u8.ToArray());
      File.WriteAllBytes(Path.Combine(output, "default", "crashes", "README.txt"), "not an input"u8.ToArray());
      return new(0, "", "", false);
    });
    var (exitCode, output, error) = this.Invoke(this.Command(runner, path: this.FakeAfl()), Fuzz("run", "demo-marker", "--engine", "afl", "--duration", "5", "--seed", "3"));

    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output, Does.StartWith("fuzz target 'demo-marker': 1 iterations, 1 findings").And.EndWith("(engine afl, seed 3)\n"));
    Assert.That(error, Does.Contain("at iteration 0 (seed 3); minimized to 3 bytes: artifacts/fuzz/demo-marker/b8d17c5a6e84d640.bin"));
    Assert.That(File.ReadAllText(Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker", "b8d17c5a6e84d640.json")), Does.Contain("\"engine\": \"afl\""));

    var work = Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker", "afl");
    var instrumented = Path.Combine(work, "instrumented", Path.GetFileName(_assembly));
    Assert.That(this._instrumented, Is.EqualTo(new[] { instrumented }));
    Assert.That(File.ReadAllBytes(Path.Combine(work, "input", "0000.bin")), Is.EqualTo(_seed));
    var afl = runner.Calls.Last();
    Assert.That(afl.Take(3), Is.EqualTo(new[] { "env", "AFL_SKIP_BIN_CHECK=1", "AFL_NO_UI=1" }));
    Assert.That(afl.SkipWhile(arg => arg != "-V").Take(6), Is.EqualTo(new[] { "-V", "5", "-t", "1000", "-s", "3" }));
    Assert.That(afl.SkipWhile(arg => arg != "--").Skip(1).SkipWhile(arg => arg != "fuzz"),
      Is.EqualTo(new[] { "fuzz", "afl-worker", "--target", "demo-marker", "--assembly", instrumented, "--probe", Path.GetDirectoryName(_assembly) }));
    Assert.That(runner.LastTimeout, Is.EqualTo(TimeSpan.FromSeconds(5) + AflFuzzer.Grace));
  }

  [Test]
  public void AflThatTimesOut_StillHasItsFindingsConfirmed() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal)
      ? new(0, "afl-fuzz++5.03c based on afl\n", "", false)
      : new(-1, "", "", true));
    var (exitCode, output, _) = this.Invoke(this.Command(runner, path: this.FakeAfl()), Fuzz("run", "demo-clean", "--engine", "afl", "--duration", "1"));
    Assert.That(exitCode, Is.Zero);
    Assert.That(output, Does.StartWith("fuzz target 'demo-clean': 0 iterations, 0 findings"));
  }

  [Test]
  public void AflThatFails_IsPLY0077() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal)
      ? new(0, "afl-fuzz++5.03c based on afl\n", "", false)
      : new(1, "\n[-] PROGRAM ABORT : Pipe at the beginning of 'core_pattern'\n", "", false));
    var (exitCode, _, error) = this.Invoke(this.Command(runner, path: this.FakeAfl()), Fuzz("run", "demo-clean", "--engine", "afl"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Is.EqualTo("polypiler-tooling: error PLY0077: fuzz target 'demo-clean': afl-fuzz ended with exit code 1: [-] PROGRAM ABORT : Pipe at the beginning of 'core_pattern'\n"));
  }

  [Test]
  public void AflWithoutOutput_SaysSo() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal)
      ? new(0, "afl-fuzz++5.03c based on afl\n", "", false)
      : new(2, "", "", false));
    var (_, _, error) = this.Invoke(this.Command(runner, path: this.FakeAfl()), Fuzz("run", "demo-clean", "--engine", "afl"));
    Assert.That(error, Does.EndWith("afl-fuzz ended with exit code 2: no output\n"));
  }

  [Test]
  public void AflThatCannotBeStarted_IsPLY0021() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal)
      ? new(0, "afl-fuzz++5.03c based on afl\n", "", false)
      : throw new Win32Exception("env: not found"));
    var (exitCode, _, error) = this.Invoke(this.Command(runner, path: this.FakeAfl()), Fuzz("run", "demo-clean", "--engine", "afl"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Is.EqualTo("polypiler-tooling: error PLY0021: toolchain 'aflplusplus' cannot be started: env: not found\n"));
  }

  [Test]
  public void AflWithoutALock_IsTheLocksProblem() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    var (exitCode, _, error) = this.Invoke(Fuzz("run", "demo-clean", "--engine", "afl"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("error PLY0004: "));
  }

  [Test]
  public void ASecondAflRun_StartsFromAnEmptyWorkDirectory() {
    this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    this.CopyLock();
    var runner = new ScriptedRunner(argv => argv[0].EndsWith("afl-fuzz", StringComparison.Ordinal)
      ? new(0, "afl-fuzz++5.03c based on afl\n", "", false)
      : new(0, "", "", false));
    var command = this.Command(runner, path: this.FakeAfl());
    var work = Path.Combine(this._workspace.Artifacts, "fuzz", "demo-clean", "afl");
    Assert.That(this.Invoke(command, Fuzz("run", "demo-clean", "--engine", "afl")).ExitCode, Is.Zero);
    File.WriteAllText(Path.Combine(work, "left-over"), "");
    Assert.That(this.Invoke(command, Fuzz("run", "demo-clean", "--engine", "afl")).ExitCode, Is.Zero);
    Assert.That(File.Exists(Path.Combine(work, "left-over")), Is.False);
  }

  /// <summary>The <c>afl-worker</c> verb runs the target on what the fork server hands it, and lets its exceptions escape to AFL++.</summary>
  [Test]
  public void TheAflWorker_RunsTheTargetOnEachInput() {
    var inputs = new Queue<byte[]>([[1, 2], "PLY"u8.ToArray()]);
    var command = new FuzzCommand(this._workspace.Root, (target, _) => new InProcessExecutor(target), new ScriptedRunner(""), _ => null, "linux", _ => { }) {
      AflServe = action => {
        while (inputs.Count > 0)
          action(new MemoryStream(inputs.Dequeue()));
      }
    };
    Assert.That(() => command.Run(["afl-worker", "--target", "demo-marker", "--assembly", _assembly, "--probe", this._workspace.Root], TextWriter.Null, TextWriter.Null),
      Throws.InvalidOperationException.With.Message.EqualTo("the marker PLY"));
    Assert.That(inputs, Is.Empty);

    using var error = new StringWriter { NewLine = "\n" };
    Assert.That(command.Run(["afl-worker", "--target", "no-such-target"], TextWriter.Null, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Is.EqualTo("polypiler-tooling: error PLY0003: unknown fuzz target 'no-such-target'\n"));
    Assert.That(command.Run(["afl-worker", "--target", "demo-clean", "--assembly", _assembly], TextWriter.Null, TextWriter.Null), Is.Zero);
  }

  [Test]
  public void Probe_LoadsFromTheFirstDirectoryThatHasTheAssembly() {
    var directory = Path.GetDirectoryName(_assembly)!;
    Assert.That(FuzzCommand.Probe([this._workspace.Root, directory], "Polypiler.Core, Version=0.1.0.0"), Is.SameAs(typeof(Polypiler.Core.Diagnostic).Assembly));
    Assert.That(FuzzCommand.Probe([this._workspace.Root], "Polypiler.Core"), Is.Null);
  }

  /// <summary>SharpFuzz rewrites the copy it is given; the original stays as it was.</summary>
  [Test, NUnit.Framework.Category("Slow")]
  public void SharpFuzz_InstrumentsACopyInPlace() {
    var original = typeof(Polypiler.Core.ExitCode).Assembly.Location;
    var copy = Path.Combine(this._workspace.Root, Path.GetFileName(original));
    File.Copy(original, copy);
    AflFuzzer.InstrumentWithSharpFuzz(copy);
    Assert.That(File.ReadAllBytes(copy), Is.Not.EqualTo(File.ReadAllBytes(original)));
  }

  private void CopyLock() {
    var target = Path.Combine(this._workspace.Root, ToolchainLock.RepositoryPath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath), target);
  }

  /// <summary>A directory holding a file named <c>afl-fuzz</c>, to be put on <c>PATH</c>.</summary>
  private string FakeAfl() {
    var directory = Directory.CreateDirectory(Path.Combine(this._workspace.Root, "bin")).FullName;
    File.WriteAllText(Path.Combine(directory, "afl-fuzz"), "");
    return directory;
  }

}
