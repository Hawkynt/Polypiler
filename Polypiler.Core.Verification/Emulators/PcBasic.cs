using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>What a program run by PC-BASIC produced.</summary>
/// <param name="Outcome">How the run ended; a run cut off by a limit is never <see cref="RunOutcome.Completed"/>.</param>
/// <param name="Output">
/// The console output, decoded as code page 437, CR LF turned into LF and one final line end removed;
/// <see langword="null"/> when the run failed, so a failure is never taken for an empty output.
/// </param>
public sealed record PcBasicRun(RunOutcome Outcome, string? Output) {

  /// <summary>Why the run failed: <c>PLY3960</c> followed by the runner's own diagnostics; empty when it did not fail.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

  /// <summary>Whether the run produced its output.</summary>
  public bool Succeeded => this.Output != null;

}

/// <summary>A conversion PC-BASIC could not do; carries the <c>PLY3960</c> that says why.</summary>
public sealed class PcBasicException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="diagnostic">The <c>PLY3960</c>.</param>
  public PcBasicException(Diagnostic diagnostic) : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message) => this.Diagnostic = diagnostic;

  /// <summary>The <c>PLY3960</c>.</summary>
  public Diagnostic Diagnostic { get; }

}

/// <summary>
/// PC-BASIC, the free reimplementation of GW-BASIC by Rob Hagemans, used only as an external process
/// through the cross-language runner: it lists a token file, tokenizes a listing and runs a program
/// headless. It shares no code with Polypiler, which makes it the independent oracle of the GW-BASIC
/// token format (PRD §23.1, §24 R3). The adapter knows the command line and the console's code page,
/// nothing of BASIC; the input it is given is never modified.
/// </summary>
/// <param name="runner">The runner every PC-BASIC process goes through.</param>
public sealed class PcBasic(ICrossLanguageRunner runner) {

  /// <summary>The key of PC-BASIC in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "pcbasic";

  /// <summary>The name a token file has in the run's directory.</summary>
  private const string TokenFile = "PROG.BAS";

  /// <summary>The name a listing has in the run's directory.</summary>
  private const string ListingFile = "PROG.TXT";

  /// <summary>The name of a conversion's result in the run's directory.</summary>
  private const string ResultFile = "OUT";

  /// <summary>The byte that ends a file under DOS; PC-BASIC writes it after a converted file.</summary>
  private const byte EndOfFile = 0x1A;

  /// <summary>The token file of a program without lines: the token-file mark, then the zero link that ends every program.</summary>
  private static readonly byte[] _emptyProgram = [0xFF, 0x00, 0x00];

  private readonly ICrossLanguageRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>The DOS code page the console and the files of GW-BASIC are written in.</summary>
  public static Encoding CodePage437 { get; } = CodePagesEncodingProvider.Instance.GetEncoding(437)!;

  /// <summary>The limits of a conversion; the runner's defaults unless given.</summary>
  public RunLimits ConversionLimits { get; init; } = RunLimits.Default;

  /// <summary>Where the programs come from; a program from outside the repository is untrusted and runs only where the sandbox isolates it.</summary>
  public TrustLevel Trust { get; init; } = TrustLevel.Untrusted;

  /// <summary>Lists a token file: <c>pcbasic PROG.BAS OUT --convert=A</c>.</summary>
  /// <param name="tokenized">The token file's bytes.</param>
  /// <returns>The listing, lines separated by LF, without a final line end.</returns>
  /// <exception cref="PcBasicException">PC-BASIC failed or wrote nothing, or listed as empty a file that is not the empty program (<c>PLY3960</c>).</exception>
  public string ToAscii(ReadOnlyMemory<byte> tokenized) => this.ToAsciiAsync(tokenized).GetAwaiter().GetResult();

  /// <summary>Tokenizes a listing: <c>pcbasic PROG.TXT OUT --convert=B</c>.</summary>
  /// <param name="listing">The listing; its lines are written with CR LF.</param>
  /// <returns>The token file's bytes, as PC-BASIC wrote them.</returns>
  /// <exception cref="PcBasicException">PC-BASIC failed, wrote nothing or no token file, or made the empty program of a listing that is not empty (<c>PLY3960</c>).</exception>
  public byte[] ToTokenized(string listing) => this.ToTokenizedAsync(listing).GetAwaiter().GetResult();

  /// <summary>Runs a token file headless: <c>pcbasic PROG.BAS --interface=none --quit</c>.</summary>
  /// <param name="program">The token file's bytes.</param>
  /// <param name="input">What the program reads on standard input, its line ends sent as CR as a keyboard sends them; <see langword="null"/> for nothing.</param>
  /// <param name="limit">How long the run may take.</param>
  /// <returns>How the run ended and what it printed, or why it failed.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
  public PcBasicRun Run(ReadOnlyMemory<byte> program, string? input, TimeSpan limit) => this.RunAsync(program, input, limit).GetAwaiter().GetResult();

  /// <summary>Lists a token file; see <see cref="ToAscii"/>.</summary>
  internal async Task<string> ToAsciiAsync(ReadOnlyMemory<byte> tokenized, CancellationToken cancellationToken = default) {
    var bytes = await this.ConvertAsync("conversion to ASCII", TokenFile, tokenized, "A", cancellationToken).ConfigureAwait(false);
    var listing = Normalise(CodePage437.GetString(bytes.Span.TrimEnd(EndOfFile)));
    if (listing.Length == 0 && !IsEmptyProgram(tokenized.Span))
      throw Failure("conversion to ASCII", "the token file lists as nothing, but it is not the empty program");

    return listing;
  }

  /// <summary>Tokenizes a listing; see <see cref="ToTokenized"/>.</summary>
  internal async Task<byte[]> ToTokenizedAsync(string listing, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(listing);
    var text = CodePage437.GetBytes(Normalise(listing).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");
    var bytes = (await this.ConvertAsync("conversion to a token file", ListingFile, text, "B", cancellationToken).ConfigureAwait(false)).ToArray();
    if (bytes[0] != _emptyProgram[0])
      throw Failure("conversion to a token file", $"the result does not start with the token-file mark FF but with {bytes[0]:X2}");

    if (IsEmptyProgram(bytes) && !string.IsNullOrWhiteSpace(listing))
      throw Failure("conversion to a token file", "a listing that is not empty became the empty program");

    return bytes;
  }

  /// <summary>Runs a token file; see <see cref="Run"/>.</summary>
  internal async Task<PcBasicRun> RunAsync(ReadOnlyMemory<byte> program, string? input, TimeSpan limit, CancellationToken cancellationToken = default) {
    var limits = new RunLimits(limit, this.ConversionLimits.MemoryBytes, this.ConversionLimits.OutputBytes, this.ConversionLimits.Processes);
    var stdin = CodePage437.GetBytes((input ?? "").Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));
    var execute = new ProcessSpec(Toolchain, [TokenFile, "--interface=none", "--quit"], stdin);
    var plan = new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [TokenFile] = program }, [], execute, limits, this.Trust);
    var result = await this._runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != RunOutcome.Completed)
      return new(result.Outcome, null) { Diagnostics = [Failure("run", Causes(result)).Diagnostic, .. result.Diagnostics] };

    if (result.ExitCode != 0)
      return new(result.Outcome, null) { Diagnostics = [Failure("run", Exited(result)).Diagnostic] };

    return new(result.Outcome, Normalise(CodePage437.GetString(result.Stdout.Span)));
  }

  /// <summary>
  /// Turns CR LF into LF and removes one final line end: what GW-BASIC ends a line with is a property of
  /// DOS, not of the program, and an output that is only a line end is no output.
  /// </summary>
  /// <param name="text">The text as PC-BASIC wrote it.</param>
  public static string Normalise(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    return lines.EndsWith('\n') ? lines[..^1] : lines;
  }

  /// <summary>Whether a token file is the empty program, with or without the end-of-file byte after it.</summary>
  private static bool IsEmptyProgram(ReadOnlySpan<byte> tokenized) => tokenized.TrimEnd(EndOfFile).SequenceEqual(_emptyProgram);

  /// <summary>Runs one conversion and returns the file it wrote.</summary>
  private async Task<ReadOnlyMemory<byte>> ConvertAsync(string what, string name, ReadOnlyMemory<byte> content, string format, CancellationToken cancellationToken) {
    var execute = new ProcessSpec(Toolchain, name, ResultFile, $"--convert={format}");
    var plan = new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [name] = content }, [], execute, this.ConversionLimits, this.Trust) { Collect = [ResultFile] };
    var result = await this._runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != RunOutcome.Completed)
      throw Failure(what, Causes(result));

    if (result.ExitCode != 0)
      throw Failure(what, Exited(result));

    if (!result.Collected.TryGetValue(ResultFile, out var bytes) || bytes.IsEmpty)
      throw Failure(what, "it wrote no output");

    return bytes;
  }

  private static string Causes(RunResult result)
    => result.Diagnostics.Count == 0 ? $"the run ended {result.Outcome}" : string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

  private static string Exited(RunResult result) {
    var stderr = Encoding.UTF8.GetString(result.Stderr.Span).Trim();
    return stderr.Length == 0 ? $"it exited with {result.ExitCode}" : $"it exited with {result.ExitCode}: {(stderr.Length <= 200 ? stderr : stderr[..200] + "...")}";
  }

  private static PcBasicException Failure(string what, string cause) => new(Diagnostic.Create(PcBasicDiagnostics.Failed, null, what, cause));

}
