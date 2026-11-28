using System;
using System.Collections.Generic;
using System.Globalization;

namespace Polypiler.Core.Syntax;

/// <summary>The comparison of a guard (<c>@version &gt;= 3.10</c>).</summary>
public enum GuardOperator {

  /// <summary><c>==</c>, also a guard without an operator.</summary>
  Equal,

  /// <summary><c>!=</c>.</summary>
  NotEqual,

  /// <summary><c>&lt;</c>.</summary>
  Less,

  /// <summary><c>&lt;=</c>.</summary>
  LessOrEqual,

  /// <summary><c>&gt;</c>.</summary>
  Greater,

  /// <summary><c>&gt;=</c>.</summary>
  GreaterOrEqual

}

/// <summary>
/// The profile a parse runs for: a value per axis the grammar declares (<c>@axis version</c>,
/// <c>@axis dialect</c>). The axis <c>version</c> compares as dotted numbers, every other axis as text.
/// The guards of a grammar with <c>@guards runtime</c> are checked against it.
/// </summary>
public sealed class GrammarProfile {

  /// <summary>The axis whose values compare as dotted numbers.</summary>
  public const string VersionAxis = "version";

  private readonly Dictionary<string, string> _values;

  /// <summary>A profile with the values given.</summary>
  /// <param name="values">The value of each axis.</param>
  public GrammarProfile(IReadOnlyDictionary<string, string> values) {
    ArgumentNullException.ThrowIfNull(values);
    this._values = new(values, StringComparer.Ordinal);
  }

  /// <summary>The profile without values: a guard on any axis fails.</summary>
  public static GrammarProfile None { get; } = new(new Dictionary<string, string>());

  /// <summary>The axes the profile sets.</summary>
  public IReadOnlyDictionary<string, string> Values => this._values;

  /// <summary>Whether the profile satisfies a guard; a guard on an axis without a value fails.</summary>
  /// <param name="axis">The axis.</param>
  /// <param name="op">The comparison.</param>
  /// <param name="value">The value the guard compares with.</param>
  public bool Satisfies(string axis, GuardOperator op, string value) {
    ArgumentNullException.ThrowIfNull(axis);
    ArgumentNullException.ThrowIfNull(value);
    if (!this._values.TryGetValue(axis, out var actual))
      return false;

    var order = axis == VersionAxis ? CompareVersions(actual, value) : string.CompareOrdinal(actual, value);
    return op switch {
      GuardOperator.Equal => order == 0,
      GuardOperator.NotEqual => order != 0,
      GuardOperator.Less => order < 0,
      GuardOperator.LessOrEqual => order <= 0,
      GuardOperator.Greater => order > 0,
      _ => order >= 0
    };
  }

  /// <summary>Compares two dotted versions part by part as numbers; a missing part counts as 0.</summary>
  /// <param name="left">The first version, such as <c>3.9</c>.</param>
  /// <param name="right">The second version, such as <c>3.10</c>.</param>
  /// <exception cref="FormatException">A part is not a number.</exception>
  public static int CompareVersions(string left, string right) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    var a = left.Split('.');
    var b = right.Split('.');
    for (var i = 0; i < Math.Max(a.Length, b.Length); i++) {
      var x = i < a.Length ? int.Parse(a[i], NumberStyles.None, CultureInfo.InvariantCulture) : 0;
      var y = i < b.Length ? int.Parse(b[i], NumberStyles.None, CultureInfo.InvariantCulture) : 0;
      if (x != y)
        return x.CompareTo(y);
    }

    return 0;
  }

}
