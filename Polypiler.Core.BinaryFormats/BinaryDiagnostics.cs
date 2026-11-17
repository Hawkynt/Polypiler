using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// The diagnostics of the binary input manifest and the image model, <c>PLY4055</c>–<c>PLY4059</c> of
/// the machine block of <c>spec/diagnostics.yaml</c>.
/// </summary>
public static class BinaryDiagnostics {

  /// <summary><c>PLY4055</c>: a manifest that breaks a rule. Arguments: the manifest file, the rule.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvalidManifest = new("PLY4055", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "binary manifest '{0}' is not valid: {1}", "machine.binary.manifest");

  /// <summary><c>PLY4056</c>: an image whose hash is not the manifest's. Arguments: the image, its hash, the expected hash.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor HashMismatch = new("PLY4056", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "'{0}' has SHA-256 {1}, the manifest expects {2}", "machine.binary.hash");

  /// <summary>
  /// <c>PLY4057</c>: a fact the manifest states and another source contradicts. Arguments: the field,
  /// the manifest's value, the other source, its value. A contradiction by the command line carries
  /// <see cref="ExitCode.UsageError"/> instead (<see cref="BinaryManifest.CheckAgainst"/>).
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Contradicted = new("PLY4057", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "manifest states {0} '{1}' but {2} states '{3}'", "machine.binary.conflict");

  /// <summary>
  /// <c>PLY4058</c>: an entry that is not inside executable code (arguments: the entry, its address), or,
  /// through <see cref="RangeOutside"/>, a range outside the image.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutsideCode = new("PLY4058", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "entry '{0}' at {1} is not inside executable code", "machine.binary.range");

  /// <summary><c>PLY4059</c>: a file no registered codec reads. Arguments: the file.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownContainer = new("PLY4059", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "'{0}' is not a recognized container; give the manifest field 'container' or use a raw profile", "machine.binary.container");

  /// <summary>The second form of <c>PLY4058</c>: a range of addresses that no section of the image holds.</summary>
  /// <param name="start">The first address.</param>
  /// <param name="end">The address after the last.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic RangeOutside(ulong start, ulong end) => OutsideCode.At($"range {Hex(start)}..{Hex(end)} lies outside the image");

  /// <summary>
  /// The diagnostic of a file that does not exist or cannot be read: <c>PLY0004</c>, the one the command
  /// line reports for its inputs.
  /// </summary>
  /// <param name="path">The file.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic Unreadable(string path)
    => _unreadable.At($"input '{path}' does not exist or is not readable", path);

  /// <summary><c>PLY0004</c> as the command line registers it; declared there, so not marked here.</summary>
  private static readonly DiagnosticDescriptor _unreadable = new("PLY0004", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "io.read");

  /// <summary>An address as the manifest writes it: <c>0x</c> and at least eight upper-case hexadecimal digits.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The text.</returns>
  public static string Hex(ulong address) => "0x" + address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

}

/// <summary>A value, or the diagnostics that say why there is none.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class BinaryResult<T> where T : class {

  private readonly T? _value;

  private BinaryResult(T? value, ImmutableArray<Diagnostic> problems) {
    this._value = value;
    this.Problems = problems;
  }

  /// <summary>The diagnostics; empty on success.</summary>
  public ImmutableArray<Diagnostic> Problems { get; }

  /// <summary>Whether there is a value.</summary>
  [MemberNotNullWhen(true, nameof(Value))]
  public bool Succeeded => this._value is not null;

  /// <summary>The value, or <see langword="null"/> after a failure.</summary>
  public T? Value => this._value;

  /// <summary>A success.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The result.</returns>
  public static BinaryResult<T> Success(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), []);

  /// <summary>A failure.</summary>
  /// <param name="problems">The diagnostics, at least one.</param>
  /// <returns>The result.</returns>
  public static BinaryResult<T> Failure(ImmutableArray<Diagnostic> problems) {
    if (problems.IsDefaultOrEmpty)
      throw new ArgumentException("a failure needs a diagnostic", nameof(problems));
    return new(null, problems);
  }

  /// <summary>A failure with one diagnostic.</summary>
  /// <param name="problem">The diagnostic.</param>
  /// <returns>The result.</returns>
  public static BinaryResult<T> Failure(Diagnostic problem) => Failure([problem ?? throw new ArgumentNullException(nameof(problem))]);

}
