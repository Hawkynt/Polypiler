using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// Compiles a PowerBASIC program with PB-Compiler and runs the DOS executable in DOSBox-X, both through
/// the sandboxed runner: <c>pbc PROG.BAS -O PROG.EXE</c> builds a DOS MZ executable, and DOSBox-X,
/// started without a window and with the run directory mounted as <c>C:</c>, runs
/// <c>PROG.EXE &gt; OUT.TXT</c> and exits. The captured output is <c>OUT.TXT</c> with CR LF turned
/// into LF and nothing else changed.
/// </summary>
public static class PbCommands {

  /// <summary>The toolchain key of PB-Compiler in <c>tooling/toolchains.lock</c>.</summary>
  public const string Compiler = "pb-compiler";

  /// <summary>The toolchain key of DOSBox-X in <c>tooling/toolchains.lock</c>.</summary>
  public const string Emulator = "dosbox-x";

  /// <summary>The program's source in the run directory.</summary>
  public const string Source = "PROG.BAS";

  /// <summary>The DOS executable <c>pbc</c> writes.</summary>
  public const string Executable = "PROG.EXE";

  /// <summary>The file the program's standard output is redirected to inside DOS.</summary>
  public const string Output = "OUT.TXT";

  /// <summary>The emulator's configuration in the run directory.</summary>
  public const string Configuration = "dosbox.conf";

  /// <summary>
  /// The configuration: no start-up delay, the fastest emulated CPU, and an autoexec that mounts the
  /// run directory (the emulator's working directory) as <c>C:</c>, runs the program with its output
  /// redirected and leaves the emulator. DOS needs CR LF in its batch lines.
  /// </summary>
  public static string ConfigurationText { get; } = string.Join("\r\n",
    "[sdl]",
    "autolock=false",
    "[dosbox]",
    "quit warning=false",
    "fastbioslogo=true",
    "startbanner=false",
    "[cpu]",
    "cycles=max",
    "[autoexec]",
    "mount c .",
    "c:",
    $"{Executable} > {Output}",
    "exit",
    "");

  /// <summary>
  /// The variables that keep the emulator off any display and sound device: SDL's dummy video and
  /// audio drivers. The runner's environment has no <c>DISPLAY</c>, so no window can open either way.
  /// </summary>
  public static IReadOnlyDictionary<string, string> Headless { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
    ["SDL_VIDEODRIVER"] = "dummy",
    ["SDL_AUDIODRIVER"] = "dummy"
  };

  /// <summary>The build step: <c>pbc PROG.BAS -O PROG.EXE</c>.</summary>
  public static ProcessSpec Compile { get; } = new(Compiler, Source, "-O", Executable);

  /// <summary>The execute step: DOSBox-X with <see cref="ConfigurationText"/>, headless.</summary>
  public static ProcessSpec Execute { get; } = new(Emulator, "-fastlaunch", "-nomenu", "-conf", Configuration) { Environment = Headless };

  /// <summary>A plan that compiles a program and runs it in the emulator.</summary>
  /// <param name="source">The program's source.</param>
  /// <param name="limits">The limits of each step.</param>
  public static RunPlan Plan(ReadOnlyMemory<byte> source, RunLimits limits)
    => new(Files((Source, source)), [Compile], Execute, limits, TrustLevel.Trusted) { Collect = [Output] };

  /// <summary>A plan that only compiles a program; the executable comes back in <see cref="RunResult.Collected"/>.</summary>
  /// <param name="source">The program's source.</param>
  /// <param name="limits">The limits of the compiler.</param>
  public static RunPlan CompilePlan(ReadOnlyMemory<byte> source, RunLimits limits)
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [Source] = source }, [], Compile, limits, TrustLevel.Trusted) { Collect = [Executable] };

  /// <summary>A plan that runs an executable already compiled, so that its limits hold the emulator alone.</summary>
  /// <param name="executable">The DOS executable.</param>
  /// <param name="limits">The limits of the emulator.</param>
  public static RunPlan ExecutePlan(ReadOnlyMemory<byte> executable, RunLimits limits)
    => new(Files((Executable, executable)), [], Execute, limits, TrustLevel.Trusted) { Collect = [Output] };

  /// <summary>Runs a plan with the runner of this host and the toolchains of the repository lock.</summary>
  /// <param name="plan">The plan.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  public static Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default)
    => new CrossLanguageRunner(RunnerTesting.Host, RunnerTesting.Toolchains).RunAsync(plan, cancellationToken);

  /// <summary>What the program wrote to <see cref="Output"/>, CR LF turned into LF; <see langword="null"/> when there is no such file.</summary>
  /// <param name="result">The run.</param>
  public static string? Captured(RunResult result)
    => result.Collected.TryGetValue(Output, out var bytes) ? Encoding.Latin1.GetString(bytes.Span).Replace("\r\n", "\n", StringComparison.Ordinal) : null;

  /// <summary>
  /// Writes what a run produced to <c>artifacts/basic/&lt;name&gt;.txt</c> of the repository, the
  /// evidence the job uploads: outcome, exit code, diagnostics, standard output and error, and the
  /// captured output.
  /// </summary>
  /// <param name="name">The name of the run.</param>
  /// <param name="result">The run.</param>
  public static void Record(string name, RunResult result) {
    var folder = Path.Combine(PrototypeFixture.RepositoryRoot, "artifacts", "basic");
    Directory.CreateDirectory(folder);
    var text = new StringBuilder()
      .Append(CultureInfo.InvariantCulture, $"outcome: {result.Outcome}\nexit code: {result.ExitCode}\nwall time: {result.WallTime.TotalMilliseconds:0} ms\nisolation: {result.Isolation}\n")
      .Append(CultureInfo.InvariantCulture, $"diagnostics: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message} {diagnostic.Cause}".TrimEnd()))}\n")
      .Append("--- stdout\n").Append(Encoding.UTF8.GetString(result.Stdout.Span)).Append('\n')
      .Append("--- stderr\n").Append(Encoding.UTF8.GetString(result.Stderr.Span)).Append('\n')
      .Append(CultureInfo.InvariantCulture, $"--- {Output}\n").Append(Captured(result) ?? "(none)").Append('\n');
    File.WriteAllText(Path.Combine(folder, name + ".txt"), text.ToString());
  }

  private static Dictionary<string, ReadOnlyMemory<byte>> Files((string Name, ReadOnlyMemory<byte> Bytes) file)
    => new(StringComparer.Ordinal) { [file.Name] = file.Bytes, [Configuration] = Encoding.ASCII.GetBytes(ConfigurationText) };

}
