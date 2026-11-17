using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Proofs.Obligations;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>An input of an obligation.</summary>
/// <param name="Name">The name, as the fragments read it after a <c>%</c>.</param>
/// <param name="Type">The type, such as <c>u8</c>, <c>i&lt;w&gt;</c> or <c>bool</c>.</param>
public sealed record ProofInput(string Name, string Type);

/// <summary>
/// What a rule must prove before it may fire: two IR fragments over the same inputs that define the
/// value named by <see cref="Result"/>, and the preconditions under which they must agree. The IR is
/// written in the notation of <c>backlog/README.md</c> - <c>%r = Op.type[contracts] operand, operand</c>,
/// one instruction per line or per <c>;</c> - and a type may use the width parameter <c>&lt;w&gt;</c>.
/// </summary>
/// <param name="RuleId">The rule the proof is for.</param>
/// <param name="Before">The fragment the rule replaces.</param>
/// <param name="After">The fragment it puts in its place.</param>
/// <param name="Inputs">The inputs both fragments read.</param>
/// <param name="Preconditions">Fragments each defining, last, a <c>bool</c> that must hold.</param>
/// <param name="Result">The name of the value both fragments define, without the <c>%</c>.</param>
/// <exception cref="ArgumentException">
/// The fragments read a value that is neither an input nor defined before, define the result with
/// different types, or do not define it at all; an input is named twice; a precondition is no
/// <c>bool</c>. An operation that no prover models is no error here: a prover answers it with
/// <see cref="ProofResult.Unsupported"/>.
/// </exception>
public sealed record ProofObligation(string RuleId, string Before, string After, IReadOnlyList<ProofInput> Inputs, IReadOnlyList<string> Preconditions, string Result) {

  /// <summary>The width a width-generic obligation is checked at when it is made.</summary>
  private const int CheckWidth = 8;

  private readonly bool _checked = Check(RuleId, Before, After, Inputs, Preconditions, Result);

  /// <summary>An obligation without preconditions whose result is named <c>r</c>.</summary>
  /// <param name="ruleId">The rule the proof is for.</param>
  /// <param name="before">The fragment the rule replaces.</param>
  /// <param name="after">The fragment it puts in its place.</param>
  /// <param name="inputs">The inputs both fragments read.</param>
  public ProofObligation(string ruleId, string before, string after, IReadOnlyList<ProofInput> inputs) : this(ruleId, before, after, inputs, [], "r") { }

  /// <summary>Whether the obligation uses the width parameter <c>&lt;w&gt;</c>.</summary>
  public bool IsWidthGeneric => this._checked && ObligationProgram.IsWidthGeneric(this);

  private static bool Check(string ruleId, string before, string after, IReadOnlyList<ProofInput> inputs, IReadOnlyList<string> preconditions, string result) {
    ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
    ArgumentNullException.ThrowIfNull(before);
    ArgumentNullException.ThrowIfNull(after);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(preconditions);
    ArgumentException.ThrowIfNullOrWhiteSpace(result);
    var twice = inputs.GroupBy(input => input.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
    if (twice is not null)
      throw new ArgumentException($"The input '{twice.Key}' is named twice.", nameof(inputs));

    try {
      ObligationProgram.Parse(before, after, inputs, preconditions, result, CheckWidth);
    } catch (ObligationUnsupportedException) {
      // Outside every prover's model: the prover says so when it is asked.
    }

    return true;
  }

}
