using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>What petcat produced.</summary>
/// <param name="Listing">The normalised listing of a detokenized PRG; empty for a tokenization or a failure.</param>
/// <param name="Prg">The PRG of a tokenized listing; empty for a detokenization or a failure.</param>
/// <param name="Diagnostics">Why petcat failed; empty when it did not.</param>
public sealed record PetcatResult(string Listing, ReadOnlyMemory<byte> Prg, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>The diagnostics.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; } = Diagnostics?.ToArray() ?? throw new ArgumentNullException(nameof(Diagnostics));

  /// <summary>Whether petcat produced its output.</summary>
  public bool Succeeded => this.Diagnostics.Count == 0;

}

/// <summary>
/// VICE's BASIC lister and tokenizer, run through the sandboxed cross-language runner: a tokenizer
/// Polypiler did not write, so a listing or a PRG it agrees with is checked by an independent codec.
/// Its listing is normalised to the text form of <see cref="PetsciiText"/>, which the token codec and
/// the screen output of <see cref="ViceRunner"/> share.
/// </summary>
/// <param name="runner">The runner every petcat run goes through.</param>
public sealed class Petcat(ICrossLanguageRunner runner) {

  /// <summary>The toolchain's key in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "petcat";

  /// <summary>The listing file of a tokenization.</summary>
  public const string ListingFile = "LIST.TXT";

  private readonly ICrossLanguageRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>The dialects petcat is run for, with its BASIC version option.</summary>
  public static IReadOnlyDictionary<string, string> Dialects { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
    ["c128-basic-7"] = "70",
    ["c64-basic-v2"] = "2",
    ["vic20-basic-v2"] = "2"
  }.ToFrozenDictionary(StringComparer.Ordinal);

  /// <summary>The named control-code escapes of petcat's listings and their bytes, from <c>Resources/petcat-escapes.tsv</c>.</summary>
  public static IReadOnlyDictionary<string, byte> Escapes { get; } = LoadEscapes();

  /// <summary>The limits of each petcat run; the runner's defaults unless given.</summary>
  public RunLimits Limits { get; init; } = RunLimits.Default;

  /// <summary>Where the input came from; untrusted unless given.</summary>
  public TrustLevel Trust { get; init; } = TrustLevel.Untrusted;

  /// <summary>Lists a PRG.</summary>
  /// <param name="prg">The PRG.</param>
  /// <param name="dialect">The dialect's name, a key of <see cref="Dialects"/>.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  /// <returns>The normalised listing, or <c>PLY3936</c>.</returns>
  public async Task<PetcatResult> DetokenizeAsync(ReadOnlyMemory<byte> prg, string dialect, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(dialect);
    const string operation = "detokenize";
    if (!Dialects.TryGetValue(dialect, out var version))
      return Failed(operation, dialect, UnknownDialect);

    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [ViceRunner.ProgramFile] = prg.ToArray() };
    var run = await this._runner.RunAsync(new(files, [], new(Toolchain, "-" + version, "-nh", "--", ViceRunner.ProgramFile), this.Limits, this.Trust), cancellationToken).ConfigureAwait(false);
    return Problem(run) is { } problem ? Failed(operation, dialect, problem) : new(Normalise(Encoding.Latin1.GetString(run.Stdout.Span)), ReadOnlyMemory<byte>.Empty, []);
  }

  /// <summary>Tokenizes a listing.</summary>
  /// <param name="listing">The listing in the text form of <see cref="PetsciiText"/>, lines separated by <c>\n</c>.</param>
  /// <param name="dialect">The dialect's name, a key of <see cref="Dialects"/>.</param>
  /// <param name="loadAddress">The address the PRG loads to.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  /// <returns>The PRG, or <c>PLY3936</c>.</returns>
  public async Task<PetcatResult> TokenizeAsync(string listing, string dialect, ushort loadAddress, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(listing);
    ArgumentNullException.ThrowIfNull(dialect);
    const string operation = "tokenize";
    if (!Dialects.TryGetValue(dialect, out var version))
      return Failed(operation, dialect, UnknownDialect);

    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [ListingFile] = Encoding.Latin1.GetBytes(ToPetcatText(listing) + "\n") };
    var execute = new ProcessSpec(Toolchain, "-w" + version, "-l", loadAddress.ToString("x4", CultureInfo.InvariantCulture), "-o", ViceRunner.ProgramFile, "--", ListingFile);
    var run = await this._runner.RunAsync(new(files, [], execute, this.Limits, this.Trust) { Collect = [ViceRunner.ProgramFile] }, cancellationToken).ConfigureAwait(false);
    if (Problem(run) is { } problem)
      return Failed(operation, dialect, problem);

    return run.Collected.TryGetValue(ViceRunner.ProgramFile, out var prg) ? new("", prg, []) : Failed(operation, dialect, "petcat wrote no PRG");
  }

  /// <summary>
  /// A petcat listing in the text form of <see cref="PetsciiText"/>: each line without its indentation
  /// and trailing blanks, empty lines dropped; outside <c>{…}</c> a lower-case letter, which petcat
  /// prints for an unshifted one, upper-cased, an upper-case letter, a shifted one, written as
  /// <c>{$C1}</c>-<c>{$DA}</c>, and <c>~</c> (π) as <c>{$FF}</c>; a named escape replaced by its byte
  /// from <see cref="Escapes"/>, and <c>{$xx}</c> written with upper-case digits.
  /// </summary>
  /// <param name="text">What petcat printed.</param>
  public static string Normalise(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = new List<string>();
    foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(line => line.Trim(' ')).Where(line => line.Length > 0)) {
      var normal = new StringBuilder(line.Length);
      foreach (var element in PetsciiText.Elements(line))
        normal.Append(element switch {
          ['{', '$', _, _, '}'] when byte.TryParse(element.AsSpan(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) => PetsciiText.Escape(value),
          ['{', .., '}'] when Escapes.TryGetValue(element[1..^1], out var value) => PetsciiText.Escape(value),
          [>= 'a' and <= 'z'] => element.ToUpperInvariant(),
          [>= 'A' and <= 'Z' and var letter] => PetsciiText.Escape((byte)(letter + 0x80)),
          "~" => PetsciiText.Escape(0xFF),
          _ => element
        });

      lines.Add(normal.ToString());
    }

    return string.Join('\n', lines);
  }

  /// <summary>
  /// A listing in the text form of <see cref="PetsciiText"/> as petcat reads it: outside <c>{…}</c> the
  /// case of letters swapped, so an upper-case letter becomes the unshifted key petcat reads as lower
  /// case; <c>{$XX}</c> written with lower-case digits, the only ones petcat accepts.
  /// </summary>
  /// <param name="listing">The listing.</param>
  public static string ToPetcatText(string listing) {
    ArgumentNullException.ThrowIfNull(listing);
    var text = new StringBuilder(listing.Length);
    foreach (var element in PetsciiText.Elements(listing))
      text.Append(element switch {
        ['{', '$', ..] => element.ToLowerInvariant(),
        [>= 'A' and <= 'Z'] => element.ToLowerInvariant(),
        [>= 'a' and <= 'z'] => element.ToUpperInvariant(),
        _ => element
      });

    return text.ToString();
  }

  private static string UnknownDialect => $"petcat is run only for {string.Join(", ", Dialects.Keys.Order(StringComparer.Ordinal))}";

  /// <summary>Why a petcat run failed: it did not complete, exited non-zero, or reported an error it still exited 0 after.</summary>
  private static string? Problem(RunResult run) {
    if (run.Outcome != RunOutcome.Completed)
      return EmulatorDiagnostics.Describe(run.Diagnostics);

    var errors = Encoding.UTF8.GetString(run.Stderr.Span).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    if (run.ExitCode != 0)
      return $"petcat exited with {run.ExitCode.ToString(CultureInfo.InvariantCulture)}: {string.Join(" ", errors)}";

    return errors.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase));
  }

  private static PetcatResult Failed(string operation, string dialect, string cause)
    => new("", ReadOnlyMemory<byte>.Empty, [Diagnostic.Create(EmulatorDiagnostics.PetcatFailed, null, operation, dialect, cause)]);

  private static FrozenDictionary<string, byte> LoadEscapes() {
    using var stream = typeof(Petcat).Assembly.GetManifestResourceStream("Polypiler.Core.Verification.Emulators.petcat-escapes.tsv")!;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var escapes = new Dictionary<string, byte>(StringComparer.Ordinal);
    while (reader.ReadLine() is { } line)
      if (!line.StartsWith('#')) {
        var columns = line.Split('\t');
        escapes.Add(columns[0], byte.Parse(columns[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
      }

    return escapes.ToFrozenDictionary(StringComparer.Ordinal);
  }

}
