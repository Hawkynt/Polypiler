using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Verification.Gates;

/// <summary>
/// A language or CPU pair with the profiles of both sides, which names its gate record
/// <c>&lt;from&gt;--&lt;to&gt;--&lt;source-profile&gt;--&lt;target-profile&gt;.json</c>. Each part is
/// lower-case letters, digits, <c>.</c>, <c>+</c>, <c>_</c> and single hyphens between them, so that
/// <c>--</c> separates the parts unambiguously.
/// </summary>
/// <param name="From">The source language, for example <c>csharp</c>.</param>
/// <param name="To">The target language, for example <c>javascript</c>.</param>
/// <param name="SourceProfile">The profile of the source, for example <c>net10</c>.</param>
/// <param name="TargetProfile">The profile of the target, for example <c>node-es2022</c>.</param>
/// <exception cref="ArgumentException">A part is empty, contains <c>--</c>, starts or ends with a hyphen or holds another character.</exception>
public sealed partial record PairKey(string From, string To, string SourceProfile, string TargetProfile) {

  /// <summary>What separates the parts of a file name.</summary>
  public const string Separator = "--";

  /// <summary>The extension of a gate record.</summary>
  public const string Extension = ".json";

  /// <summary>The source language.</summary>
  public string From { get; } = Checked(From, nameof(From));

  /// <summary>The target language.</summary>
  public string To { get; } = Checked(To, nameof(To));

  /// <summary>The profile of the source.</summary>
  public string SourceProfile { get; } = Checked(SourceProfile, nameof(SourceProfile));

  /// <summary>The profile of the target.</summary>
  public string TargetProfile { get; } = Checked(TargetProfile, nameof(TargetProfile));

  /// <summary>The name of the pair's gate record.</summary>
  public string FileName => string.Join(Separator, this.From, this.To, this.SourceProfile, this.TargetProfile) + Extension;

  /// <summary>The pair a file name stands for.</summary>
  /// <param name="fileName">A file name without a directory.</param>
  /// <param name="pair">The pair, or <see langword="null"/>.</param>
  /// <returns>Whether the name is that of a gate record.</returns>
  public static bool TryParseFileName(string? fileName, out PairKey? pair) {
    pair = null;
    if (fileName is null || !fileName.EndsWith(Extension, StringComparison.Ordinal))
      return false;

    var parts = fileName[..^Extension.Length].Split(Separator);
    if (parts.Length != 4 || !parts.All(IsPart))
      return false;

    pair = new(parts[0], parts[1], parts[2], parts[3]);
    return true;
  }

  /// <inheritdoc/>
  public override string ToString() => $"{this.From} ({this.SourceProfile}) to {this.To} ({this.TargetProfile})";

  /// <summary>Whether a text can be a part of a pair.</summary>
  /// <param name="text">The text.</param>
  /// <returns>Whether it is one.</returns>
  public static bool IsPart(string? text) => text is not null && Part().IsMatch(text);

  private static string Checked(string text, string name)
    => IsPart(text) ? text : throw new ArgumentException($"'{text}' is no part of a pair: lower-case letters, digits, '.', '+', '_' and single hyphens between them", name);

  [GeneratedRegex(@"^[a-z0-9.+_]+(?:-[a-z0-9.+_]+)*\z", RegexOptions.CultureInvariant)]
  private static partial Regex Part();

}

/// <summary>One file that supports one state of a fixture.</summary>
/// <param name="State">The state it supports.</param>
/// <param name="Kind">What the file is, as <see cref="GateStates.KindOf"/> names it.</param>
/// <param name="Path">The file, relative to the directory above the gate directory, with <c>/</c> as the separator.</param>
/// <param name="Sha256">The SHA-256 of the file, 64 lower-case hexadecimal digits.</param>
public sealed record GateEvidence(GateState State, string Kind, string Path, string Sha256);

/// <summary>The evidence of one fixture of a pair.</summary>
/// <param name="FixtureId">The fixture.</param>
/// <param name="Evidence">Its evidence, at most one per state.</param>
public sealed record FixtureGate(string FixtureId, IReadOnlyList<GateEvidence> Evidence) {

  /// <summary>The state the evidence supports, by <see cref="GateStates.Reached"/>.</summary>
  public GateState State => GateStates.Reached(this.Evidence.Select(evidence => evidence.State));

}

/// <summary>A pair whose state is lower than in a baseline, or which disappeared from the store.</summary>
/// <param name="Pair">The pair.</param>
/// <param name="Before">Its state in the baseline.</param>
/// <param name="After">Its state now; <see cref="GateState.Planned"/> when it disappeared.</param>
public sealed record GateRegression(PairKey Pair, GateState Before, GateState After);
