using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Whether the arms of a <c>match</c> cover every value of its scrutinee, as <c>rustc</c> requires
/// (error <c>E0004</c>): each column of the scrutinee - one for a scalar, one per element of a tuple -
/// is split at every bound a pattern names, and every piece must be covered by the arms without a
/// guard that remain once the earlier columns are fixed.
/// </summary>
internal static class Exhaustiveness {

  /// <summary>A set of values of a column: ranges, or <see langword="null"/> for every value.</summary>
  private sealed record Space(IReadOnlyList<(BigInteger Low, BigInteger High)>? Ranges) {

    public static Space All { get; } = new((IReadOnlyList<(BigInteger, BigInteger)>?)null);

    public bool Covers(BigInteger low, BigInteger high) => this.Ranges is null || this.Ranges.Any(range => range.Low <= low && high <= range.High);

  }

  public static bool Covers(MatchExpr match, IReadOnlyList<RType> columns, Typed typed) {
    var domains = columns.Select(column => column.Resolved.Kind == RKind.Bool ? (BigInteger.Zero, BigInteger.One) : (column.Resolved.Minimum, column.Resolved.Maximum)).ToList();
    var rows = new List<Space[]>();
    foreach (var arm in match.Arms.Where(arm => arm.Guard is null)) {
      if (columns.Count == 1) {
        rows.Add([SpaceOf(arm.Pattern, domains[0], typed)]);
        continue;
      }

      foreach (var alternative in arm.Pattern is OrPattern or ? or.Alternatives : [arm.Pattern])
        rows.Add(alternative is TuplePattern tuple
          ? [.. tuple.Elements.Select((element, index) => SpaceOf(element, domains[index], typed))]
          : [.. domains.Select(_ => alternative is WildcardPattern ? Space.All : new Space([]))]);
    }

    return Exhaustive(rows, domains, 0);
  }

  private static bool Exhaustive(List<Space[]> rows, List<(BigInteger Low, BigInteger High)> domains, int column) {
    if (column == domains.Count)
      return rows.Count > 0;

    var (low, high) = domains[column];
    var bounds = new SortedSet<BigInteger> { low, high + 1 };
    foreach (var row in rows)
      foreach (var (from, to) in row[column].Ranges ?? [])
        foreach (var bound in new[] { from, to + 1 })
          if (bound > low && bound <= high)
            bounds.Add(bound);

    var cuts = bounds.ToList();
    for (var index = 0; index + 1 < cuts.Count; ++index) {
      var (from, to) = (cuts[index], cuts[index + 1] - 1);
      if (!Exhaustive(rows.Where(row => row[column].Covers(from, to)).ToList(), domains, column + 1))
        return false;
    }

    return true;
  }

  private static Space SpaceOf(Pattern pattern, (BigInteger Low, BigInteger High) domain, Typed typed) {
    switch (pattern) {
      case WildcardPattern:
        return Space.All;
      case IdentifierPattern identifier:
        if (typed.Bindings.TryGetValue(identifier, out var binding) && binding.Constant is { } constant)
          return Point(Constants.Evaluate(constant.Value, typed));

        return Space.All;
      case LiteralPattern literal:
        return Point(Constants.Evaluate(literal.Value, typed));
      case RangePattern range: {
        var low = range.Low is null ? domain.Low : Constants.Evaluate(range.Low, typed);
        var high = range.High is null ? domain.High : Constants.Evaluate(range.High, typed) - (range.Inclusive ? 0 : 1);
        return low is null || high is null ? new Space([]) : new Space([(low.Value, high.Value)]);
      }
      case OrPattern alternatives:
        return new Space([.. alternatives.Alternatives.SelectMany(alternative => SpaceOf(alternative, domain, typed) is { Ranges: { } ranges } ? ranges : [domain])]);
      default:
        return new Space([]);
    }
  }

  private static Space Point(BigInteger? value) => value is { } known ? new Space([(known, known)]) : new Space([]);

}

/// <summary>The value of a constant expression of a pattern or a <c>const</c>, as far as the subset needs it.</summary>
internal static class Constants {

  /// <summary>The value, or <see langword="null"/> for an expression this does not evaluate.</summary>
  public static BigInteger? Evaluate(Expr expression, Typed typed) {
    switch (expression) {
      case IntegerLiteral literal:
        return literal.Value;
      case BoolLiteral truth:
        return truth.Value ? 1 : 0;
      case UnaryExpr { Op: "-" } negation:
        return -Evaluate(negation.Operand, typed);
      case PathExpr { Segments: [var owner, var name] } when RType.Named(owner) is { Kind: RKind.Integer } type:
        return name switch { "MIN" => type.Minimum, "MAX" => type.Maximum, _ => type.Bits };
      case PathExpr path when typed.Bindings.TryGetValue(path, out var binding) && binding.Constant is { } constant:
        return Evaluate(constant.Value, typed);
      case BinaryExpr binary when Evaluate(binary.Left, typed) is { } left && Evaluate(binary.Right, typed) is { } right:
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
      case CastExpr cast when Evaluate(cast.Operand, typed) is { } value && RType.Named(cast.Type.Name) is { Kind: RKind.Integer } target: {
        var modulus = BigInteger.One << target.Bits;
        var wrapped = ((value % modulus) + modulus) % modulus;
        return wrapped > target.Maximum ? wrapped - modulus : wrapped;
      }
      default:
        return null;
    }
  }

}
