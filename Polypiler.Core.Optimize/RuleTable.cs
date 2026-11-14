using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Recognize;
using Polypiler.Core.Recognize.Rules;

namespace Polypiler.Core.Optimize;

/// <summary>
/// The rules the optimizer runs, each registered under its optimization number. Rules run in the
/// order of the table, which is the order of their numbers, so that the output is the same on every
/// run. Adding a rule is one file and one row.
/// </summary>
public sealed class RuleTable {

  /// <summary>The rules of the prototype pipeline.</summary>
  public static RuleTable Default { get; } = new([
    new O0100Rotate(),
    new O0101PopCountLoop(),
    new O0102PopCountSwar(),
    new O0103ZeroCountLoop(),
    new O0105BitReverse(),
    new O0106ByteSwap(),
    new O0108SingleBit(),
    new O0109SignExtend(),
    new O0110Parity(),
    new O0120MinMax(),
    new O0121AbsWrap(),
    new O0122Sign(),
    new O0123Clamp(),
    new O0125ShiftAddMultiply(),
    new O0127QuotientRemainder(),
    new O0140EndianLoad(),
    new O0141EndianStore()
  ]);

  private readonly Dictionary<string, IrRule> _byId = new(StringComparer.Ordinal);

  /// <summary>Makes a table of rules.</summary>
  /// <param name="rules">The rules, in the order they run.</param>
  /// <exception cref="ArgumentException">Two rules have the same number.</exception>
  public RuleTable(IEnumerable<IrRule> rules) {
    this.Rules = [.. rules];
    foreach (var rule in this.Rules)
      if (!this._byId.TryAdd(rule.Id, rule))
        throw new ArgumentException($"The optimization number {rule.Id} is registered twice.", nameof(rules));
  }

  /// <summary>The rules, in the order they run.</summary>
  public IReadOnlyList<IrRule> Rules { get; }

  /// <summary>The numbers of the rules, in order.</summary>
  public IEnumerable<string> Ids => this.Rules.Select(rule => rule.Id);

  /// <summary>The rule with a number.</summary>
  /// <param name="id">The optimization number.</param>
  /// <returns>The rule, or <see langword="null"/> when the table has none.</returns>
  public IrRule? Find(string id) => this._byId.GetValueOrDefault(id);

}
