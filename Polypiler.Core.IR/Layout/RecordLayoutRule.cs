using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// A record a function asks the layout of, or takes the address of a field of, has a layout (#110): under every
/// profile the rule checks, <see cref="RecordLayout.Problems"/> finds nothing - no field without bytes, no
/// overlapping explicit fields, no stated size short of the fields, no packing that is no power of two, no
/// record that holds itself. A record no function lays out is not checked: a struct of strings is a value
/// like any other until its bytes are asked for.
/// </summary>
public sealed class RecordLayoutRule : IVerifierRule {

  private readonly IReadOnlyList<LayoutProfile> _profiles;

  /// <summary>A rule that checks the layouts under every standard profile, <see cref="LayoutProfile.Standard"/>.</summary>
  public RecordLayoutRule() : this(LayoutProfile.Standard) { }

  /// <summary>A rule that checks the layouts under some profiles: those of the targets a module is built for.</summary>
  /// <param name="profiles">The profiles, at least one.</param>
  /// <exception cref="ArgumentException">No profile is given.</exception>
  public RecordLayoutRule(IReadOnlyList<LayoutProfile> profiles) {
    ArgumentNullException.ThrowIfNull(profiles);
    this._profiles = profiles.Count > 0 ? [.. profiles] : throw new ArgumentException("the rule needs a profile to check layouts under", nameof(profiles));
  }

  /// <inheritdoc/>
  public string Id => "ir.verifier.records.layout";

  /// <inheritdoc/>
  public string Family => "records";

  /// <inheritdoc/>
  public string Description => "a record whose layout a function asks for, or whose field it addresses, has a layout under every profile";

  /// <inheritdoc/>
  public void Check(VerifierRuleContext context) {
    ArgumentNullException.ThrowIfNull(context);
    var seen = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
    foreach (var node in IrVerifier.AllNodes(context.Function.Body)) {
      var record = node switch {
        IrLayoutOf query => query.Of.StructType,
        IrFieldAddress address => address.Record,
        _ => null
      };
      if (record is null || !record.IsDefined || !seen.Add(record))
        continue;

      foreach (var problem in this._profiles.SelectMany(profile => RecordLayout.Problems(record, profile)).Distinct())
        context.Report(node, $"layout of '{record.Name}': {problem}");
    }
  }

}
