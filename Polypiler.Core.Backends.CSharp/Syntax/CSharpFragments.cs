using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Backends.CSharp.Syntax;

/// <summary>
/// The pieces of C# source the capability table and the helpers are written in, read into the syntax
/// tree once and then reused: a form's method and its further arguments, a template with its places
/// filled, a helper's declaration. Each source is read by <see cref="CSharpSyntaxReader"/> the first
/// time it is asked for; the trees are immutable, so every class shares them.
/// </summary>
public static class CSharpFragments {

  private const string Place = "__polypiler_";

  /// <summary>A place of a template: an argument by its position, or the helper.</summary>
  private static readonly Regex _placeholder = new(@"\{([0-9]|h)\}", RegexOptions.CultureInvariant);

  private static readonly ConcurrentDictionary<string, CsExpression> _expressions = new(StringComparer.Ordinal);
  private static readonly ConcurrentDictionary<string, IReadOnlyList<CsExpression>> _arguments = new(StringComparer.Ordinal);
  private static readonly ConcurrentDictionary<string, CsMember> _members = new(StringComparer.Ordinal);

  /// <summary>An expression of C# source, such as <c>global::System.Math.Round</c>.</summary>
  /// <param name="source">The source.</param>
  /// <returns>The tree, shared between callers.</returns>
  public static CsExpression Expression(string source) => _expressions.GetOrAdd(source, CSharpSyntaxReader.Expression);

  /// <summary>A list of arguments, such as <c>-2147483648, 2147483647</c>.</summary>
  /// <param name="source">The source, without its parentheses.</param>
  /// <returns>The arguments.</returns>
  public static IReadOnlyList<CsExpression> Arguments(string source)
    => _arguments.GetOrAdd(source, text => ((CsInvocation)CSharpSyntaxReader.Expression(Place + "(" + text + ")")).Arguments);

  /// <summary>A member of C# source, such as a helper's method.</summary>
  /// <param name="source">The source.</param>
  /// <returns>The tree, shared between callers.</returns>
  public static CsMember Member(string source) => _members.GetOrAdd(source, CSharpSyntaxReader.Member);

  /// <summary>
  /// A template with its places filled: <c>{0}</c>, <c>{1}</c> and so on with the arguments in their
  /// order, <c>{h}</c> with the name of the helper. The arguments stand as they are given; a template
  /// puts every place where a primary expression may stand, so an argument that is none comes in
  /// parentheses already.
  /// </summary>
  /// <param name="template">The template.</param>
  /// <param name="arguments">The arguments.</param>
  /// <param name="helper">Gives the name of the helper; asked only when the template has the place <c>{h}</c>.</param>
  /// <returns>The expression.</returns>
  public static CsExpression Fill(string template, IReadOnlyList<CsExpression> arguments, Func<string> helper) {
    var parsed = Expression(_placeholder.Replace(template, match => Place + match.Groups[1].Value));
    string? name = null;
    return CSharpSyntaxRewriter.Rewrite(parsed, node => node is CsName { Identifier: var identifier } && identifier.StartsWith(Place, StringComparison.Ordinal)
      ? identifier[^1] == 'h' ? new CsName(name ??= helper()) : arguments[identifier[^1] - '0']
      : node);
  }

}
