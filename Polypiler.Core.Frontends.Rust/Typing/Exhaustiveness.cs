using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Whether patterns cover every value of a type, as <c>rustc</c> requires of a <c>match</c>
/// (<c>E0004</c>) and of the pattern of a <c>let</c>, a parameter or a <c>for</c> (<c>E0005</c>):
/// the matrix of patterns is split column by column - a tuple, a struct and a reference into their
/// parts, <c>Option</c> and <c>Result</c> by their variants, an integer, a <c>bool</c> and an enum at
/// every bound a pattern names - and every piece must be covered by some row.
/// </summary>
internal static class Exhaustiveness {

  /// <summary>Whether the patterns, in order, cover every value of the type.</summary>
  public static bool Covers(IReadOnlyList<Pattern> patterns, RType type, Typed typed)
    => Exhaustive([.. patterns.Select(pattern => new List<Pattern?> { pattern })], [type], typed);

  /// <summary>Whether one pattern matches every value of the type.</summary>
  public static bool Irrefutable(Pattern pattern, RType type, Typed typed) => Covers([pattern], type, typed);

  private static bool Exhaustive(List<List<Pattern?>> rows, List<RType> types, Typed typed) {
    if (types.Count == 0)
      return rows.Count > 0;

    // An or-pattern is one row per alternative; a binding or `_` is no pattern at all.
    var expanded = new List<List<Pattern?>>();
    foreach (var row in rows)
      foreach (var head in Alternatives(row[0], typed))
        expanded.Add([head, .. row.Skip(1)]);

    rows = expanded;
    var type = types[0].Resolved;
    var rest = types.Skip(1).ToList();
    if (type.Kind == RKind.Reference) {
      var inner = rows.Select(row => (List<Pattern?>)[row[0] is ReferencePattern reference ? Wild(reference.Inner, typed) : row[0], .. row.Skip(1)]).ToList();
      return Exhaustive(inner, [type.Element!, .. rest], typed);
    }

    switch (type.Kind) {
      case RKind.Tuple or RKind.Struct or RKind.Unit:
        return Exhaustive(Specialize(rows, null, Fields(type), typed), [.. Fields(type), .. rest], typed);
      case RKind.Option:
        return Exhaustive(Specialize(rows, "None", [], typed), rest, typed)
          && Exhaustive(Specialize(rows, "Some", [type.Element!], typed), [type.Element!, .. rest], typed);
      case RKind.Result:
        return Exhaustive(Specialize(rows, "Ok", [type.Element!], typed), [type.Element!, .. rest], typed)
          && Exhaustive(Specialize(rows, "Err", [type.Error], typed), [type.Error, .. rest], typed);
      case RKind.Integer or RKind.Bool or RKind.Enum or RKind.Char:
        return Values(rows, type, rest, typed);
      default:
        // Floats, arrays, slices, vectors: only a binding or `_` covers them.
        return Exhaustive([.. rows.Where(row => row[0] is null).Select(row => row.Skip(1).ToList())], rest, typed);
    }
  }

  /// <summary>The alternatives of a pattern, each as a pattern that tests something or <see langword="null"/> for one that matches everything.</summary>
  private static IEnumerable<Pattern?> Alternatives(Pattern? pattern, Typed typed) {
    if (pattern is OrPattern or)
      return or.Alternatives.SelectMany(alternative => Alternatives(alternative, typed));

    return [Wild(pattern, typed)];
  }

  /// <summary>
  /// A pattern, or <see langword="null"/> when it matches every value: <c>_</c>, a binding, and a
  /// refused one, whose refusal is the diagnostic that counts.
  /// </summary>
  private static Pattern? Wild(Pattern? pattern, Typed typed) => pattern switch {
    null or WildcardPattern or RefusedPattern => null,
    IdentifierPattern identifier when !typed.Resolutions.ContainsKey(identifier) => null,
    _ => pattern
  };

  private static List<RType> Fields(RType type) => type.Kind switch {
    RKind.Tuple => [.. type.Elements],
    RKind.Struct => [.. type.StructFields.Select(field => field.Type)],
    _ => []
  };

  /// <summary>
  /// The rows that match a constructor, with its fields in place of their first pattern: a tuple, a
  /// struct (<paramref name="constructor"/> <see langword="null"/>) or a variant of <c>Option</c> or
  /// <c>Result</c>; a row whose first pattern is no pattern contributes wildcards.
  /// </summary>
  private static List<List<Pattern?>> Specialize(List<List<Pattern?>> rows, string? constructor, List<RType> fields, Typed typed) {
    var result = new List<List<Pattern?>>();
    foreach (var row in rows) {
      var head = row[0];
      List<Pattern?>? parts = head switch {
        null => [.. fields.Select(_ => (Pattern?)null)],
        TuplePattern tuple => Spread(tuple.Elements, fields.Count, typed),
        StructPattern structure => [.. structure.Fields.Count == 0 && fields.Count == 0 ? [] : ((StructInfo)typed.Resolutions[structure]).Fields.Select(field => {
          var match = structure.Fields.FirstOrDefault(candidate => candidate.Name == field.Name);
          return match.Pattern is null ? null : Wild(match.Pattern, typed);
        })],
        TupleStructPattern tupleStruct when typed.Resolutions.TryGetValue(tupleStruct, out var name) && name is string variant
          => variant == constructor ? Spread(tupleStruct.Elements, 1, typed) : null,
        TupleStructPattern tupleStruct => Spread(tupleStruct.Elements, fields.Count, typed),
        _ when typed.Resolutions.TryGetValue(head, out var target) && target is "None" => constructor == "None" ? [] : null,
        _ when typed.Resolutions.TryGetValue(head, out var target) && target is StructInfo => [],
        // A constant of a composite type compares the whole value, which covers nothing for sure.
        _ => null
      };
      if (parts is not null)
        result.Add([.. parts, .. row.Skip(1)]);
    }

    return result;
  }

  /// <summary>The patterns of a list with an optional <c>..</c>, spread over the given number of fields.</summary>
  private static List<Pattern?> Spread(IReadOnlyList<Pattern> patterns, int count, Typed typed) {
    var rest = patterns.ToList().FindIndex(pattern => pattern is RestPattern);
    if (rest < 0)
      return [.. patterns.Select(pattern => Wild(pattern, typed))];

    var after = patterns.Count - rest - 1;
    return [
      .. patterns.Take(rest).Select(pattern => Wild(pattern, typed)),
      .. Enumerable.Repeat<Pattern?>(null, count - rest - after),
      .. patterns.Skip(rest + 1).Select(pattern => Wild(pattern, typed))
    ];
  }

  /// <summary>An integer, <c>bool</c> or enum column: split at every bound a pattern names, each piece covered by the rows that cover it.</summary>
  private static bool Values(List<List<Pattern?>> rows, RType type, List<RType> rest, Typed typed) {
    var ranges = rows.Select(row => row[0] is null ? null : RangeOf(row[0]!, type, typed)).ToList();
    IEnumerable<(BigInteger Low, BigInteger High)> pieces;
    if (type.Kind == RKind.Enum) {
      pieces = type.Enum!.Variants.Select(variant => (variant.Value, variant.Value));
    } else {
      var (low, high) = type.Kind == RKind.Bool ? (BigInteger.Zero, BigInteger.One) : (type.Minimum, type.Maximum);
      var bounds = new SortedSet<BigInteger> { low, high + 1 };
      foreach (var range in ranges.Where(range => range is not null))
        foreach (var bound in new[] { range!.Value.Low, range.Value.High + 1 })
          if (bound > low && bound <= high)
            bounds.Add(bound);

      var cuts = bounds.ToList();
      pieces = Enumerable.Range(0, cuts.Count - 1).Select(index => (cuts[index], cuts[index + 1] - 1));
    }

    foreach (var (from, to) in pieces) {
      var covering = rows.Where((row, index) => ranges[index] is null && row[0] is null || ranges[index] is { } range && range.Low <= from && to <= range.High)
        .Select(row => row.Skip(1).ToList()).ToList();
      if (!Exhaustive(covering, rest, typed))
        return false;
    }

    return true;
  }

  /// <summary>The values a pattern of an integer, <c>bool</c> or enum column matches, as one range; an empty range for one it cannot tell.</summary>
  private static (BigInteger Low, BigInteger High)? RangeOf(Pattern pattern, RType type, Typed typed) {
    switch (pattern) {
      case IdentifierPattern identifier when typed.Resolutions.TryGetValue(identifier, out var target):
        return Point(ValueOf(target, typed));
      case LiteralPattern literal when typed.Resolutions.TryGetValue(literal, out var target):
        return Point(ValueOf(target, typed));
      case LiteralPattern literal:
        return Point(Constants.Evaluate(literal.Value, typed));
      case RangePattern range: {
        var low = range.Low is null ? type.Minimum : Constants.Evaluate(range.Low, typed);
        var high = range.High is null ? type.Maximum : Constants.Evaluate(range.High, typed) - (range.Inclusive ? 0 : 1);
        return low is null || high is null ? (1, 0) : (low.Value, high.Value);
      }
      default:
        return (1, 0);
    }
  }

  private static (BigInteger, BigInteger) Point(BigInteger? value) => value is { } known ? (known, known) : (1, 0);

  private static BigInteger? ValueOf(object target, Typed typed) => target switch {
    Binding { Constant: { } constant } => Constants.Evaluate(constant.Value, typed),
    (EnumInfo enumeration, string variant) => enumeration.Variants.First(candidate => candidate.Name == variant).Value,
    _ => null
  };

}

/// <summary>The value of a constant expression of a pattern, a length, a discriminant or a <c>const</c>, as far as the subset needs it.</summary>
internal static class Constants {

  /// <summary>The value, or <see langword="null"/> for an expression this does not evaluate.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="typed">What the checker knows of the names in it.</param>
  /// <param name="named">The constant a path names, for a path the checker has not resolved yet.</param>
  public static BigInteger? Evaluate(Expr expression, Typed typed, Func<PathExpr, ConstItem?>? named = null) {
    switch (expression) {
      case IntegerLiteral literal:
        return literal.Value;
      case CharLiteral literal:
        return literal.Value;
      case BoolLiteral truth:
        return truth.Value ? 1 : 0;
      case UnaryExpr { Op: "-" } negation:
        return -Evaluate(negation.Operand, typed, named);
      case PathExpr { Segments: [var owner, var name] } when RType.Named(owner) is { Kind: RKind.Integer } type:
        return name switch { "MIN" => type.Minimum, "MAX" => type.Maximum, _ => type.Bits };
      case PathExpr path when typed.Bindings.TryGetValue(path, out var binding) && binding.Constant is { } constant:
        return Evaluate(constant.Value, typed, named);
      case PathExpr path when named?.Invoke(path) is { } constant:
        return Evaluate(constant.Value, typed, named);
      case BinaryExpr binary when Evaluate(binary.Left, typed, named) is { } left && Evaluate(binary.Right, typed, named) is { } right:
        return binary.Op switch {
          "+" => left + right,
          "-" => left - right,
          "*" => left * right,
          "/" when !right.IsZero => BigInteger.Divide(left, right),
          "%" when !right.IsZero => BigInteger.Remainder(left, right),
          "&" => left & right,
          "|" => left | right,
          "^" => left ^ right,
          "<<" => left << (int)right,
          ">>" => left >> (int)right,
          _ => null
        };
      case CastExpr cast when Evaluate(cast.Operand, typed, named) is { } value && RType.Named(cast.Type.Name) is { Kind: RKind.Integer } target: {
        var modulus = BigInteger.One << target.Bits;
        var wrapped = ((value % modulus) + modulus) % modulus;
        return wrapped > target.Maximum ? wrapped - modulus : wrapped;
      }
      default:
        return null;
    }
  }

}
