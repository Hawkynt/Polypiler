using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// Runs a Commodore BASIC program on VICE, the genuine ROMs of the emulated machine, through the
/// sandboxed cross-language runner. The emulator starts without sound, in warp mode, with the PRG
/// injected into RAM and run by the autostart, and a monitor command file: a tracepoint on the screen
/// editor's output routine logs the accumulator at every character that reaches the screen, and a
/// tracepoint on BASIC's <c>READY.</c> routine marks the start-up and the program's end. At the
/// program's end the requested memory is saved and a write to the debug cartridge ends the emulator;
/// a program that does not end runs into the cycle limit. On Linux the emulator runs under a virtual
/// X server, because its user interface needs a display even with no window shown.
/// </summary>
/// <param name="runner">The runner every emulator run goes through.</param>
/// <param name="virtualDisplay">Whether the emulator is started under <c>xvfb-run</c>.</param>
public sealed partial class ViceRunner(ICrossLanguageRunner runner, bool virtualDisplay) {

  /// <summary>The name of the program in the run's directory.</summary>
  public const string ProgramFile = "PROG.PRG";

  /// <summary>The monitor command file.</summary>
  public const string MonitorFile = "trace.mon";

  /// <summary>The monitor log the tracepoints write.</summary>
  public const string LogFile = "monitor.log";

  /// <summary>The toolchain the emulator is started under on Linux.</summary>
  public const string DisplayToolchain = "xvfb-run";

  /// <summary>The library list of the Mesa EGL vendor: the X server then renders without a GPU driver, which the sandbox does not reach.</summary>
  public const string MesaEglVendor = "/usr/share/glvnd/egl_vendor.d/50_mesa.json";

  /// <summary>The program's bytes as BASIC's <c>RUN</c> echoes them: the start of the program's output follows.</summary>
  private static readonly byte[] _run = "RUN\r"u8.ToArray();

  private readonly ICrossLanguageRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>A runner that uses a virtual display on Linux only.</summary>
  /// <param name="runner">The runner every emulator run goes through.</param>
  public ViceRunner(ICrossLanguageRunner runner) : this(runner, OperatingSystem.IsLinux()) { }

  /// <summary>Whether the emulator is started under <c>xvfb-run</c>.</summary>
  public bool VirtualDisplay { get; } = virtualDisplay;

  /// <summary>How VICE emulates one machine and where its ROMs print and become ready.</summary>
  /// <param name="Toolchain">The key of the emulator in <c>tooling/toolchains.lock</c>.</param>
  /// <param name="Binary">The emulator's executable name, as <c>xvfb-run</c> starts it.</param>
  /// <param name="Options">The options that select the machine's configuration.</param>
  /// <param name="Output">The screen editor's character output routine.</param>
  /// <param name="Ready">The first instruction of BASIC's <c>READY.</c> routine.</param>
  /// <param name="DebugExit">The debug cartridge's exit register.</param>
  /// <param name="Preamble">Monitor commands before the tracepoints: the C128's I/O bank, so the exit write reaches the cartridge.</param>
  internal sealed record Profile(string Toolchain, string Binary, string[] Options, ushort Output, ushort Ready, ushort DebugExit, string[] Preamble);

  /// <summary>The profile of a machine.</summary>
  /// <param name="machine">The machine.</param>
  internal static Profile ProfileOf(CbmMachine machine) => machine switch {
    CbmMachine.C64 => new("vice", "x64sc", [], 0xE716, 0xA474, 0xD7FF, []),
    CbmMachine.Vic20Unexpanded => Vic20("none"),
    CbmMachine.Vic20Plus3K => Vic20("3k"),
    CbmMachine.Vic20Plus8K => Vic20("8k"),
    _ => new("vice-x128", "x128", ["-40col"], 0xC72D, 0x4D37, 0xD7FF, ["bank io"])
  };

  private static Profile Vic20(string memory) => new("vice-xvic", "xvic", ["-memory", memory], 0xE742, 0xC474, 0x910F, []);

  /// <summary>The file a range of <see cref="ViceRequest.ReadAfterRun"/> is saved to.</summary>
  /// <param name="index">The range's index.</param>
  public static string MemoryFile(int index) => string.Create(CultureInfo.InvariantCulture, $"MEM{index}.BIN");

  /// <summary>The monitor commands of a run.</summary>
  /// <param name="request">The run.</param>
  /// <returns>The command file's text, one command per line.</returns>
  public static string MonitorCommands(ViceRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    var profile = ProfileOf(request.Machine);
    var lines = new List<string>(profile.Preamble) { $"trace exec ${Hex4(profile.Output)}", $"trace exec ${Hex4(profile.Ready)}" };
    var checkpoint = 2;
    void AtReturn(string command) {
      ++checkpoint;
      lines.AddRange([$"trace exec ${Hex4(profile.Ready)}", $"ignore {checkpoint} 1", $"command {checkpoint} \"{command}\""]);
    }

    for (var index = 0; index < request.ReadAfterRun.Length; ++index) {
      var range = request.ReadAfterRun[index];
      AtReturn($"bsave \\\"{MemoryFile(index)}\\\" 0 {Hex4(range.Start)} {Hex4(range.End)}");
    }

    AtReturn($"> {Hex4(profile.DebugExit)} 00");
    return string.Join('\n', lines) + "\n";
  }

  /// <summary>The emulator's options for a run, after the executable.</summary>
  /// <param name="request">The run.</param>
  public static IReadOnlyList<string> EmulatorArguments(ViceRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    List<string> arguments = ["-default", "-warp", "-sounddev", "dummy", "-debugcart", .. ProfileOf(request.Machine).Options,
      "-autostartprgmode", "1", "-autostart", ProgramFile, "-moncommands", MonitorFile, "-monlog", "-monlogname", LogFile,
      "-limitcycles", request.CycleLimit.ToString(CultureInfo.InvariantCulture)];
    if (request.KeyboardInput is { Length: > 0 } input)
      arguments.AddRange(["-keybuf", KeyboardBuffer(input)]);

    return arguments;
  }

  /// <summary>
  /// Keyboard input in the notation of VICE's <c>-keybuf</c>, which types a lower-case letter as the
  /// unshifted key and turns <c>\n</c> into RETURN.
  /// </summary>
  /// <param name="input">The input: an upper-case letter for the unshifted key, as the screen shows it.</param>
  public static string KeyboardBuffer(string input) {
    ArgumentNullException.ThrowIfNull(input);
    var text = new StringBuilder(input.Length);
    foreach (var character in input)
      text.Append(character switch {
        '\n' => "\\n",
        >= 'A' and <= 'Z' => char.ToLowerInvariant(character).ToString(),
        >= 'a' and <= 'z' => char.ToUpperInvariant(character).ToString(),
        _ => character.ToString()
      });

    return text.ToString();
  }

  /// <summary>The step that starts the emulator.</summary>
  /// <param name="request">The run.</param>
  public ProcessSpec Command(ViceRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    var profile = ProfileOf(request.Machine);
    if (!this.VirtualDisplay)
      return new(profile.Toolchain, EmulatorArguments(request));

    // A server number of its own, so runs outside a sandbox's private /tmp do not wait on each other's lock.
    var display = Random.Shared.Next(200, 900).ToString(CultureInfo.InvariantCulture);
    var environment = new Dictionary<string, string>(StringComparer.Ordinal);
    if (File.Exists(MesaEglVendor))
      environment["__EGL_VENDOR_LIBRARY_FILENAMES"] = MesaEglVendor;

    return new(DisplayToolchain, ["-a", "-n", display, profile.Binary, .. EmulatorArguments(request)]) { Environment = environment };
  }

  /// <summary>Runs a program and reads what it printed.</summary>
  /// <param name="request">The run.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  /// <returns>How the run ended, the screen text and memory, or the diagnostics that say why there are none.</returns>
  public async Task<ViceResult> RunAsync(ViceRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    if (BasicProgram.Problem(request.Prg.Span) is { } malformed)
      return Failed(request, [], malformed);

    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      [ProgramFile] = request.Prg.ToArray(),
      [MonitorFile] = Encoding.ASCII.GetBytes(MonitorCommands(request))
    };
    var plan = new RunPlan(files, [], this.Command(request), request.Limits, request.Trust) {
      Collect = [LogFile, .. Enumerable.Range(0, request.ReadAfterRun.Length).Select(MemoryFile)]
    };
    var run = await this._runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
    if (run.Outcome != RunOutcome.Completed)
      return Failed(request, run.Diagnostics, EmulatorDiagnostics.Describe(run.Diagnostics));

    return Interpret(request, run);
  }

  /// <summary>What a completed emulator run means.</summary>
  private static ViceResult Interpret(ViceRequest request, RunResult run) {
    var profile = ProfileOf(request.Machine);
    var trace = Trace.Read(run.Collected.TryGetValue(LogFile, out var log) ? Encoding.Latin1.GetString(log.Span) : "", profile);
    var banner = PetsciiText.Trim(PetsciiText.Decode(trace.Banner));
    var screen = PetsciiText.Trim(PetsciiText.Decode(trace.Program));
    if (run.ExitCode == 0 && trace.Returned is { } cycles) {
      var memory = new List<MemoryBlock>();
      for (var index = 0; index < request.ReadAfterRun.Length; ++index) {
        var range = request.ReadAfterRun[index];
        if (!run.Collected.TryGetValue(MemoryFile(index), out var bytes) || bytes.Length != range.Length)
          return Failed(request, [], $"the memory at ${Hex4(range.Start)} was not saved") with { Banner = banner };

        memory.Add(new(range.Start, bytes.ToArray()));
      }

      return new(ViceStatus.ReturnedToBasic, screen, [.. memory], []) { Banner = banner, Cycles = cycles };
    }

    if (run.ExitCode == 1 && trace.Returned is null && Text(run).Contains("cycle limit reached", StringComparison.Ordinal)) {
      IReadOnlyList<Diagnostic> problems = request.RequireReturn ? [Problem(request, $"BASIC printed no READY. before the cycle limit of {request.CycleLimit.ToString(CultureInfo.InvariantCulture)}")] : [];
      return new(ViceStatus.CycleLimit, screen, [], problems) { Banner = banner, Cycles = request.CycleLimit };
    }

    return Failed(request, [], $"the emulator exited with {run.ExitCode.ToString(CultureInfo.InvariantCulture)}: {Describe(run)}") with { Banner = banner };
  }

  private static ViceResult Failed(ViceRequest request, IReadOnlyList<Diagnostic> causes, string cause)
    => new(ViceStatus.EmulatorFailed, "", [], [.. causes, Problem(request, cause)]);

  private static Diagnostic Problem(ViceRequest request, string cause) => Diagnostic.Create(EmulatorDiagnostics.ViceRunFailed, null, request.Name, cause);

  private static string Text(RunResult run) => Encoding.UTF8.GetString(run.Stdout.Span) + Encoding.UTF8.GetString(run.Stderr.Span);

  /// <summary>The last line the emulator printed that is not a sound warning, or a note that it printed none.</summary>
  private static string Describe(RunResult run)
    => Text(run).Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0 && !line.StartsWith("Sound:", StringComparison.Ordinal)) ?? "it printed nothing";

  private static string Hex4(ushort value) => value.ToString("x4", CultureInfo.InvariantCulture);

  /// <summary>What the monitor log says: the bytes printed before the first <c>READY.</c> and after the autostart's <c>RUN</c>, and when BASIC was reached again.</summary>
  /// <param name="Banner">The bytes printed before BASIC's first <c>READY.</c>.</param>
  /// <param name="Program">The bytes printed after the autostart's <c>RUN</c> and before the next <c>READY.</c> or the end of the log; empty when no <c>RUN</c> was echoed.</param>
  /// <param name="Returned">The cycle at which BASIC's <c>READY.</c> routine was entered after the program's <c>RUN</c>, or <see langword="null"/>.</param>
  internal sealed record Trace(byte[] Banner, byte[] Program, long? Returned) {

    /// <summary>Reads a monitor log.</summary>
    /// <param name="log">The log.</param>
    /// <param name="profile">The machine's addresses.</param>
    public static Trace Read(string log, Profile profile) {
      var output = Hex4(profile.Output);
      var ready = Hex4(profile.Ready);
      var printed = new List<byte>();
      var readyAt = new List<(long Cycle, int Printed)>();
      foreach (Match match in TraceLine().Matches(log)) {
        var address = match.Groups["address"].Value;
        var cycle = long.Parse(match.Groups["cycle"].Value, CultureInfo.InvariantCulture);
        if (address == output)
          printed.Add(byte.Parse(match.Groups["a"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        else if (address == ready && (readyAt.Count == 0 || readyAt[^1].Cycle != cycle))
          readyAt.Add((cycle, printed.Count));
      }

      if (readyAt.Count == 0)
        return new([.. printed], [], null);

      var banner = printed.Take(readyAt[0].Printed).ToArray();
      var session = printed.Skip(readyAt[0].Printed).Take((readyAt.Count > 1 ? readyAt[1].Printed : printed.Count) - readyAt[0].Printed).ToArray();
      var run = session.AsSpan().IndexOf(_run);
      if (run < 0)
        return new(banner, [], null);

      return new(banner, session[(run + _run.Length)..], readyAt.Count > 1 ? readyAt[1].Cycle : null);
    }

  }

  /// <summary>A trace line of the monitor log: <c>.C:e716  48  PHA  - A:48 X:… 3181885</c>.</summary>
  [GeneratedRegex(@"^\.C:(?<address>[0-9a-f]{4}) .*? - A:(?<a>[0-9A-F]{2}) .*?(?<cycle>\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex TraceLine();

}
