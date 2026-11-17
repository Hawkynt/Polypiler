using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Core.Verification.Capabilities;

/// <summary>A construct a source profile diagnoses instead of translating: a known limitation of every pair from that profile.</summary>
/// <param name="Construct">The construct as the profile names it, for example <c>decimal arithmetic</c>.</param>
/// <param name="Diagnostic">The code reported for it, for example <c>PLY1003</c>; empty when the profile names none.</param>
public sealed record CapabilityLimitation(string Construct, string Diagnostic);

/// <summary>A source profile: a <c>LANGUAGES.&lt;language&gt;.&lt;profile&gt;</c> document.</summary>
/// <param name="Language">The language, for example <c>csharp</c>.</param>
/// <param name="Profile">The profile, for example <c>net10.0</c>.</param>
/// <param name="Limitations">The profile's <c>diagnosed</c> constructs, in document order.</param>
public sealed record CapabilitySource(string Language, string Profile, IReadOnlyList<CapabilityLimitation> Limitations);

/// <summary>A target profile: a <c>TARGETS.&lt;language&gt;.&lt;profile&gt;</c> document.</summary>
/// <param name="Language">The language, for example <c>javascript</c>.</param>
/// <param name="Profile">The profile, for example <c>es2022</c>.</param>
public sealed record CapabilityTarget(string Language, string Profile);

/// <summary>One row of the capability report: a source profile translated to a target profile, at the state its gate record proves.</summary>
/// <param name="From">The source language.</param>
/// <param name="SourceProfile">The source profile.</param>
/// <param name="To">The target language.</param>
/// <param name="TargetProfile">The target profile.</param>
/// <param name="State">The lowest state of the record's fixtures; <see cref="GateState.Planned"/> without a valid record.</param>
/// <param name="Fixtures">The number of fixtures of the record; 0 for a planned pair.</param>
/// <param name="Evidence">The gate record, relative to the repository with <c>/</c> between the parts; <see langword="null"/> for a planned pair.</param>
/// <param name="Limitations">The source profile's <c>diagnosed</c> constructs, in document order.</param>
public sealed record CapabilityPair(string From, string SourceProfile, string To, string TargetProfile, GateState State, int Fixtures, string? Evidence, IReadOnlyList<CapabilityLimitation> Limitations) {

  /// <summary>The pair as the diagnostics name it: <c>&lt;from&gt;/&lt;source profile&gt; -&gt; &lt;to&gt;/&lt;target profile&gt;</c>.</summary>
  public string Name => $"{this.From}/{this.SourceProfile} -> {this.To}/{this.TargetProfile}";

  /// <summary>The four parts that identify the pair, in the order the report sorts by.</summary>
  public (string From, string SourceProfile, string To, string TargetProfile) Key => (this.From, this.SourceProfile, this.To, this.TargetProfile);

  /// <summary>Whether another pair says the same in every field, the limitations compared element by element.</summary>
  /// <param name="other">The other pair.</param>
  /// <returns>Whether it does.</returns>
  public bool SameAs(CapabilityPair other) {
    ArgumentNullException.ThrowIfNull(other);
    return this with { Limitations = other.Limitations } == other && this.Limitations.SequenceEqual(other.Limitations);
  }

}
