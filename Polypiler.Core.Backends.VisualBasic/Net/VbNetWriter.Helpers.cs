using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

internal sealed partial class VbNetWriter {

  /// <summary>The helpers the module names, by key.</summary>
  private readonly HashSet<string> _helperKeys = new(StringComparer.Ordinal);

  /// <summary>Names a helper, which the module then declares.</summary>
  private VnName Helper(string key) => new(this.HelperName(key));

  private string HelperName(string key) {
    this._helperKeys.Add(key);
    return VbNetHelpers.NameOf(key);
  }

  /// <summary>The declarations of every helper named, with the helpers they call, in the order of <see cref="VbNetHelpers.Order"/>.</summary>
  private IEnumerable<VnMethod> HelperMethods() {
    var pending = new Stack<string>(this._helperKeys);
    while (pending.TryPop(out var key))
      foreach (var needed in VbNetHelpers.Needs(key))
        if (this._helperKeys.Add(needed))
          pending.Push(needed);

    // The equality of a structure compares the structures it holds with their own.
    for (var changed = true; changed;) {
      changed = false;
      foreach (var key in this._helperKeys.Where(key => key.StartsWith("PlyEquals:", StringComparison.Ordinal)).ToList())
        foreach (var field in this.StructOf(key).Fields)
          if (field.Type is { IsStruct: true, Declaration: IrStructType inner } && this._helperKeys.Add(VbNetHelpers.EqualsOf(this._typeNames[inner])))
            changed = true;
    }

    int Rank(string key) => VbNetHelpers.Order.ToList().IndexOf(VbNetHelpers.NameOf(key));
    foreach (var key in this._helperKeys.OrderBy(Rank).ThenBy(key => key, StringComparer.Ordinal))
      foreach (var method in key.StartsWith("PlyEquals:", StringComparison.Ordinal) ? [this.EqualsHelper(key)] : VbNetHelpers.Declarations(key))
        yield return method;
  }

  private IrStructType StructOf(string key) {
    var name = key[(key.IndexOf(':') + 1)..];
    return (IrStructType)this._typeNames.First(pair => pair.Value == name).Key;
  }

  /// <summary>
  /// <c>PlyEquals(a, b)</c> of a structure: its fields in order, numbers and Booleans with <c>=</c>,
  /// floating-point numbers with <c>Equals</c> (a NaN equals a NaN, +0 equals -0), strings ordinally,
  /// structures with their own equality and arrays by identity.
  /// </summary>
  private VnMethod EqualsHelper(string key) {
    var type = this.StructOf(key);
    var name = this._typeNames[type];
    VnExpression? test = null;
    foreach (var field in type.Fields) {
      VnExpression left = new VnMember(new VnName("a"), this._fieldNames[field]);
      VnExpression right = new VnMember(new VnName("b"), this._fieldNames[field]);
      VnExpression same = field.Type switch {
        { IsStruct: true } => new VnInvoke(new VnName("PlyEquals"), [left, right]),
        { IsFloat: true } => new VnInvoke(new VnMember(left, "Equals"), [right]),
        { IsString: true } => new VnInvoke(new VnName("System.String.Equals"), [left, right]),
        var reference when reference.IsArray || reference.IsObject => new VnBinary(left, "Is", right),
        _ => new VnBinary(left, "=", right)
      };
      test = test is null ? same : new VnBinary(test, "AndAlso", same);
    }

    return new VnMethod("Private", "PlyEquals", [new("a", name), new("b", name)], "Boolean", [new VnReturn(test ?? new VnLiteral("True"))]);
  }

}
