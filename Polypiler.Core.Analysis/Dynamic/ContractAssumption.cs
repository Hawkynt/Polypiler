using System.Collections.Generic;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>
/// A caller contract the user states (PRD §4.6): checked at run time at the boundary, or unchecked and
/// then reported as <c>PLY0512</c> and listed in the manifest with <c>checked: false</c> — never
/// trusted silently.
/// </summary>
/// <param name="Target">
/// What the contract is about: <c>function.parameter</c>, <c>function.return</c> or <c>global.name</c>.
/// </param>
/// <param name="Contract">What it states.</param>
/// <param name="Checked">Whether a violating call is refused at run time (<c>--assume</c>) or the contract is trusted (<c>--assume-unchecked</c>).</param>
/// <param name="Origin">Where it was stated: <c>--assume</c>, <c>--assume-unchecked</c> or <c>--assume-file &lt;path&gt;:&lt;line&gt;</c>.</param>
public sealed record ContractAssumption(string Target, Contract Contract, bool Checked, string Origin) {

  /// <summary>The canonical text, <c>&lt;target&gt;: &lt;contract&gt;</c>.</summary>
  public string Text => $"{this.Target}: {this.Contract.Text}";

  /// <summary>
  /// The language-native spellings the text used, each with the source language it belongs to, such
  /// as <c>("Uint8Array", "javascript")</c>; the contract is valid only for a source of that language.
  /// </summary>
  public IReadOnlyList<(string Spelling, string Language)> Aliases { get; init; } = [];

  /// <summary>Whether the assumption is about the result of a function.</summary>
  public bool IsReturn => this.Target.EndsWith(".return", System.StringComparison.Ordinal);

  /// <summary>Whether the assumption is about a global of the module.</summary>
  public bool IsGlobal => this.Target.StartsWith("global.", System.StringComparison.Ordinal);

  /// <summary>The function the target names, or the empty text for a global.</summary>
  public string Function => this.IsGlobal ? "" : this.Target[..this.Target.IndexOf('.')];

  /// <summary>The parameter or global the target names, <c>return</c> for the result.</summary>
  public string Name => this.Target[(this.Target.IndexOf('.') + 1)..];

  /// <summary>
  /// Whether two assumptions are the same: the same target, contract, mode and origin; the
  /// <see cref="Aliases"/> only say how the text was spelled.
  /// </summary>
  /// <param name="other">The other assumption.</param>
  /// <returns>Whether they are equal.</returns>
  public bool Equals(ContractAssumption? other)
    => other is not null && (this.Target, this.Contract, this.Checked, this.Origin) == (other.Target, other.Contract, other.Checked, other.Origin);

  /// <inheritdoc/>
  public override int GetHashCode() => (this.Target, this.Contract, this.Checked, this.Origin).GetHashCode();

}
