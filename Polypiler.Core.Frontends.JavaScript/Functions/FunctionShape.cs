using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The functions a function value may be: the nested functions and the functions of the file that
/// reach one place. Two places that one value reaches share a shape, so the shapes of a join are
/// merged - a value called through either is a call of every member - and the members are inferred
/// jointly: one signature, the parameters of each the join of what every call passes, the result
/// the join of what each returns (#1326).
/// </summary>
internal sealed class FunctionShape {

  private FunctionShape? _merged;
  private readonly List<Candidate> _members = [];

  /// <summary>A shape of one function.</summary>
  /// <param name="member">The function.</param>
  public FunctionShape(Candidate member) => this._members.Add(member);

  /// <summary>The shape this one was merged into, or itself.</summary>
  public FunctionShape Root => this._merged is null ? this : this._merged = this._merged.Root;

  /// <summary>The functions of the shape.</summary>
  public IReadOnlyList<Candidate> Members => this.Root._members;

  /// <summary>The function type of the values of the shape, once the types are final.</summary>
  public IrType? Signature { get => this.Root._signature; set => this.Root._signature = value; }

  private IrType? _signature;

  /// <summary>Merges two shapes into one; says which one is the root.</summary>
  public static FunctionShape Merge(FunctionShape first, FunctionShape second) {
    var (a, b) = (first.Root, second.Root);
    if (ReferenceEquals(a, b))
      return a;

    b._merged = a;
    foreach (var member in b._members)
      if (!a._members.Contains(member))
        a._members.Add(member);

    b._members.Clear();
    return a;
  }

  /// <summary>Two shapes are one once merged.</summary>
  public override bool Equals(object? obj) => obj is FunctionShape other && ReferenceEquals(this.Root, other.Root);

  /// <summary>One hash for every shape: merging changes which shapes are equal.</summary>
  public override int GetHashCode() => 0;

}
