using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The instantiations of a module (<see cref="IrGenericOrigin"/>) and the copies a frontend made of
/// one function for several types, in JavaScript, which has no types to tell them apart: the members
/// of one set whose declarations the emitter writes as the same text once the name of each is the
/// set's own are written once, under the name of the source (erasure); the others each under the
/// source name with its type arguments after a <c>$</c>, <c>Ring$i64</c> and <c>Ring$f64</c>. What a
/// declaration calls is part of its text, so the sets are decided together: all are taken to merge,
/// and a set whose texts differ is split until no decision changes. The equality and the order of a
/// generic definition are written as JavaScript reproduces .NET's: <c>===</c> where it is that, and
/// SameValueZero, <c>a === b || (a !== a &amp;&amp; b !== b)</c>, for floating point.
/// </summary>
public static partial class JavaScriptEmitter {

  /// <summary>The sets of instantiations and copies of a module: each source name with the names in the IR of its members, in module order.</summary>
  private static List<(string Source, List<string> Members)> GenericSets(IrModule module) {
    var functions = module.Functions
      .Where(function => function.Origin is not null || IrGenerics.IsInstance(function.Name))
      .GroupBy(function => "f" + (function.Origin?.Name ?? IrGenerics.SourceName(function.Name)))
      .Select(group => (Source: group.Key[1..], Members: group.Select(function => function.Name).ToList()));
    var classes = module.Types.OfType<IrClassType>().Where(type => type.Origin is not null)
      .GroupBy(type => type.Origin!.Name)
      .Select(group => (Source: group.Key, Members: group.Select(type => type.Name).ToList()));
    return [.. functions, .. classes];
  }

  /// <summary>The name of a member of a set written on its own: the source name and the type arguments of the IR name, <c>Ring$i64</c>, <c>middle$i32Array</c>.</summary>
  private static string Suffixed(string name) {
    var arguments = name[(name.IndexOf('<', StringComparison.Ordinal) + 1)..^1].Replace("[]", "Array", StringComparison.Ordinal);
    var suffix = new string([.. arguments.Select(character => character is '<' or '>' or ',' ? '$' : character)]).TrimEnd('$');
    return $"{IrGenerics.SourceName(name)}${new string([.. suffix.Where(character => char.IsLetterOrDigit(character) || character is '_' or '$')])}";
  }

  /// <summary>
  /// The names the members of the sets of a module are written with: the declarations of the sets
  /// that merge are written once under the source name, every other member under its own name.
  /// </summary>
  private static Naming Named(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities capabilities, FloatMode mode) {
    var sets = GenericSets(module);
    var merged = sets.Select(set => set.Source).ToHashSet();
    for (var changed = sets.Count > 0; changed;) {
      changed = false;
      var naming = new Naming(sets, merged, false);
      var writer = new Writer(profile, capabilities, mode) { Naming = naming };
      writer.Module(module);
      foreach (var (source, members) in sets.Where(set => merged.Contains(set.Source))) {
        var texts = members.Select(member => Regex.Replace(
          string.Join('\n', writer.Declarations[member].Select(statement => JavaScriptPrinter.Print(statement with { Leading = JsTrivia.None }))),
          $@"(?<![\w$]){Regex.Escape(naming.Declared[member])}(?![\w$])", source, RegexOptions.CultureInvariant)).Distinct().Count();
        if (texts > 1)
          changed |= merged.Remove(source);
      }
    }

    return new Naming(sets, merged, true);
  }

  /// <summary>How the members of the sets of a module are named, by their names in the IR.</summary>
  private sealed class Naming {

    /// <param name="sets">The sets.</param>
    /// <param name="merged">The source names of the sets whose members are one declaration.</param>
    /// <param name="final">Whether the declarations of a merged set are written once; while the sets are decided each member is written under a name of its own.</param>
    public Naming(IReadOnlyList<(string Source, List<string> Members)> sets, IReadOnlySet<string> merged, bool final) {
      foreach (var (source, members) in sets) {
        var together = merged.Contains(source);
        foreach (var member in members) {
          this.References[member] = together ? source : Suffixed(member);
          this.Declared[member] = together && final ? source : Suffixed(member);
        }

        if (together) {
          this.Merged[members[0]] = members;
          if (final)
            this.Skipped.UnionWith(members.Skip(1));
        }
      }
    }

    /// <summary>The name every reference of a member is written with.</summary>
    public Dictionary<string, string> References { get; } = [];

    /// <summary>The name the declaration of a member is written with.</summary>
    public Dictionary<string, string> Declared { get; } = [];

    /// <summary>The members of every merged set, by its first member.</summary>
    public Dictionary<string, IReadOnlyList<string>> Merged { get; } = [];

    /// <summary>The members whose declaration the first member of their set stands for.</summary>
    public HashSet<string> Skipped { get; } = [];

  }

  private sealed partial class Writer {

    /// <summary>How the instantiations and copies of the module are named; none are for a module without them.</summary>
    public Naming Naming { get; init; } = new([], new HashSet<string>(), true);

    /// <summary>The statements every function and class of the module was declared with, by its name in the IR.</summary>
    public Dictionary<string, IReadOnlyList<JsStatement>> Declarations { get; } = [];

    /// <summary>How many equalities of floating-point numbers of a generic definition the module has, which share a helper from two on.</summary>
    private int _sameValueZero;

    /// <summary>The name a function or a class is declared with.</summary>
    private string DeclaredName(string name) => this.Naming.Declared.TryGetValue(name, out var declared) ? declared : this.Name(name);

    /// <summary>The parameter types of a merged set as a function's comment states them: each type its members take there, joined by <c>|</c>.</summary>
    private string Signature(IrFunction function, IrModule module) {
      if (!this.Naming.Merged.TryGetValue(function.Name, out var members))
        return string.Join(", ", function.Parameters.Select(parameter => $"{this.Name(parameter.Name)}: {this.Describe(parameter.Type)}"));

      var all = members.Select(member => module.Functions.First(candidate => candidate.Name == member)).ToList();
      return string.Join(", ", function.Parameters.Select((parameter, index) =>
        $"{this.Name(parameter.Name)}: {string.Join(" | ", all.Select(member => this.Describe(member.Parameters[index].Type)).Distinct())}"));
    }

    /// <summary>
    /// The equality of a generic definition: <c>===</c> of integers, truth values and references, a
    /// record's own equality, and SameValueZero of floating-point numbers - inline over variables and
    /// constants, else the helper <c>$sameValueZero</c>, which the module also calls wherever it has two or more.
    /// </summary>
    private (JsExpression Node, bool Atomic) GenericEquals(IrGenericEquals equality) {
      var (left, right) = (equality.Left, equality.Right);
      if (left.Type.IsObject)
        return this.Write(left.Type.ClassType is { IsRecord: true } ? new IrStructEquals(left, right) : new IrReferenceEquals(left, right));

      if (!left.Type.IsFloat)
        return this.Write(left.Type.IsArithmetic || left.Type == IrType.Bool ? new IrBinary(IrBinaryOp.Equal, left, right)
          : new IrBinary(IrBinaryOp.Equal, new IrConvert(IrType.I32, left), new IrConvert(IrType.I32, right)));

      if (this._sameValueZero >= 2 || !Simple(left) || !Simple(right))
        return (this.Helper("$sameValueZero", left, right), true);

      // a === b || (a !== a && b !== b): the operators bind as written, the parentheses keep the conjunction readable.
      JsExpression A() => this.Operand(left);
      JsExpression B() => this.Operand(right);
      return (Infix(Infix(A(), "===", B()), "||", Parenthesized(Infix(Infix(A(), "!==", A()), "&&", Infix(B(), "!==", B())))), false);
    }

    /// <summary>
    /// The order of a generic definition: the difference of two integers narrower than 32 bits, and
    /// for the other numbers and truth values the helper <c>$compare</c>, which puts a NaN below every
    /// number. Of two pairs of halves it is the library's comparison, which legalization put in (O0075).
    /// </summary>
    private (JsExpression Node, bool Atomic) GenericCompare(IrGenericCompare order) {
      var (left, right) = (order.Left, order.Right);
      if (left.Type.IsInteger && left.Type.Bits < 32)
        return this.Write(new IrBinary(IrBinaryOp.Subtract, new IrConvert(IrType.I32, left), new IrConvert(IrType.I32, right)));

      if (left.Type.IsLong && this.Halves)
        return this.Unlegalized("order");

      return (this.Helper("$compare", left, right), true);
    }

    /// <summary>Whether an operand may be read more than once: a variable or a constant.</summary>
    private static bool Simple(IrExpr expression) => expression is IrLocalRef or IrConst or IrConstRef;

    /// <summary>Counts the equalities of floating-point numbers of a generic definition in the module.</summary>
    private void CountGenerics(IrModule module)
      => this._sameValueZero = module.Functions.Sum(function => IrTree.Nodes(function.Body).Count(node => node is IrGenericEquals { Left.Type.IsFloat: true }));

  }

}

internal static partial class JavaScriptHelpers {

  /// <summary>The helpers of the generic definitions, in the order a module lists them.</summary>
  private static IEnumerable<string> GenericOrder => ["$sameValueZero", "$compare"];

  /// <summary>The text of a helper of the generic definitions, or <see langword="null"/> for another name.</summary>
  private static string? GenericSource(string name) => name switch {
    "$sameValueZero" => Lines("function $sameValueZero(a, b) {", "  return a === b || (a !== a && b !== b);", "}"),
    "$compare" => Lines("function $compare(a, b) {", "  return a < b ? -1 : a > b ? 1 : a === b ? 0 : a !== a ? (b !== b ? 0 : -1) : 1;", "}"),
    _ => null
  };

}
