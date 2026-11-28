using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Polypiler.Tooling.Grammar;

/// <summary>
/// Checks a grammar before it is generated, as pegen's <c>ParserGenerator</c> does, and marks its
/// left-recursive rules and their leaders (<c>compute_left_recursives</c>, converted from
/// <c>pegen/parser_generator.py</c> and <c>pegen/sccutils.py</c>, PSF-2.0; the visitors keep pegen's
/// exact behaviour, including that a lookahead counts as not nullable). It reports undefined rules,
/// unknown token kinds, left recursion without a leader, alternatives that can never be reached,
/// guards on undeclared axes and forced items that are neither a literal nor a group.
/// </summary>
public static class GrammarChecker {

  /// <summary>The values <c>@guards</c> may take.</summary>
  public static IReadOnlyList<string> GuardModes { get; } = ["runtime", "generate"];

  /// <summary>Checks a grammar and marks its left-recursive rules; the problems in the order found, empty where it is valid.</summary>
  /// <param name="grammar">The grammar.</param>
  /// <param name="tokens">The token names the grammar may use where it declares none with <c>@tokens</c>; <see langword="null"/> accepts every name in upper case.</param>
  public static IReadOnlyList<string> Check(ParserGrammar grammar, IReadOnlySet<string>? tokens = null) {
    ArgumentNullException.ThrowIfNull(grammar);
    var problems = new List<string>();
    var declared = GrammarTokens.Of(grammar);
    if (declared.Count > 0)
      tokens = declared.Keys.ToHashSet(StringComparer.Ordinal);

    foreach (var rule in grammar.Rules.Where(rule => rule.Name.StartsWith('_')))
      problems.Add($"rule names cannot start with underscore: '{rule.Name}' at line {rule.Line}");

    var starts = grammar.Words("start");
    foreach (var start in starts.Where(start => !grammar.Rules.Contains(start)))
      problems.Add($"start rule '{start}' is not defined");

    if (starts.Count == 0 && !grammar.Rules.Contains("start") && !grammar.HasMeta("trailer"))
      problems.Add("the grammar has no start rule: name one with @start or define 'start'");

    var axes = grammar.Words("axis").ToHashSet(StringComparer.Ordinal);
    var mode = grammar.Meta("guards");
    if (mode is not null && !GuardModes.Contains(mode))
      problems.Add($"@guards is '{mode}', not runtime or generate");

    foreach (var rule in grammar.Rules) {
      foreach (var leaf in Leaves(rule.Rhs).OfType<NameLeaf>()) {
        if (grammar.Rules.Contains(leaf.Value) || tokens is null && IsTokenName(leaf.Value) || tokens is not null && tokens.Contains(leaf.Value))
          continue;

        problems.Add(IsTokenName(leaf.Value) ? $"unknown token kind '{leaf.Value}' at line {leaf.Line}" : $"undefined rule '{leaf.Value}' at line {leaf.Line}");
      }

      foreach (var alt in Alternatives(rule.Rhs)) {
        foreach (var guard in alt.Guards.Where(guard => !axes.Contains(guard.Axis)))
          problems.Add($"guard on undeclared axis '{guard.Axis}' at line {alt.Line}: declare it with @axis {guard.Axis}");

        foreach (var forced in alt.Items.Select(item => item.Item).OfType<Forced>().Where(forced => forced.Node is not (StringLeaf or Group)))
          problems.Add($"forced item '{forced}' at line {alt.Line} is neither a literal nor a group");
      }

      foreach (var rhs in Choices(rule.Rhs))
        for (var i = 0; i < rhs.Alts.Count - 1; i++)
          if (AlwaysSucceeds(rhs.Alts[i])) {
            problems.Add($"alternative '{rhs.Alts[i + 1]}' of rule '{rule.Name}' at line {rhs.Alts[i + 1].Line} is never tried: '{rhs.Alts[i]}' before it always matches");
            break;
          }
    }

    if (problems.Count > 0)
      return problems;

    problems.AddRange(ComputeLeftRecursives(grammar));
    return problems;
  }

  /// <summary>Whether a name is one of a token kind: upper case letters, digits and underscores.</summary>
  /// <param name="name">The name.</param>
  public static bool IsTokenName(string name) => char.IsLetter(name[0]) && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

  /// <summary>
  /// Whether an alternative matches whatever follows: no action that could refuse, and only items that
  /// match empty input - several optional or repeated items, or one repetition.
  /// </summary>
  private static bool AlwaysSucceeds(Alt alt)
    => alt.Action is null && alt.Guards.Count == 0
      && (alt.Items.Count == 1 ? alt.Items[0].Item is Repeat { Minimum: 0 } : alt.Items.All(item => item.Item is Opt or Repeat { Minimum: 0 }));

  private static IEnumerable<GrammarItem> Leaves(GrammarItem item) {
    if (item is NameLeaf or StringLeaf)
      yield return item;

    var children = item is Gather gather ? [gather.Separator, gather.Node] : item.Children;
    foreach (var child in children)
      foreach (var leaf in Leaves(child))
        yield return leaf;
  }

  private static IEnumerable<Alt> Alternatives(GrammarItem item) {
    if (item is Alt alt)
      yield return alt;

    var children = item is Gather gather ? [gather.Separator, gather.Node] : item.Children;
    foreach (var child in children)
      foreach (var nested in Alternatives(child))
        yield return nested;
  }

  private static IEnumerable<Rhs> Choices(GrammarItem item) {
    if (item is Rhs rhs)
      yield return rhs;

    var children = item is Gather gather ? [gather.Separator, gather.Node] : item.Children;
    foreach (var child in children)
      foreach (var nested in Choices(child))
        yield return nested;
  }

  // ---------------------------------------------------------------------------------------------
  // Left recursion, as pegen computes it.

  /// <summary>Marks the left-recursive rules and their leaders; the problems of cycles without a leader.</summary>
  /// <param name="grammar">The grammar.</param>
  public static IReadOnlyList<string> ComputeLeftRecursives(ParserGrammar grammar) {
    ArgumentNullException.ThrowIfNull(grammar);
    var problems = new List<string>();
    var graph = FirstGraph(grammar);
    foreach (var scc in StronglyConnectedComponents(graph)) {
      if (scc.Count > 1) {
        foreach (var name in scc)
          grammar.Rules[name].LeftRecursive = true;

        var leaders = new HashSet<string>(scc, StringComparer.Ordinal);
        foreach (var start in scc) {
          foreach (var cycle in CyclesInScc(graph, scc, start)) {
            leaders.ExceptWith(scc.Except(cycle));
            if (leaders.Count == 0)
              break;
          }

          if (leaders.Count == 0)
            break;
        }

        if (leaders.Count == 0) {
          var members = scc.Order(StringComparer.Ordinal).ToList();
          problems.Add($"unmarked left recursion in '{members[0]}': no rule of the cycle {string.Join(", ", members)} is in all of its cycles");
          continue;
        }

        grammar.Rules[leaders.Min(StringComparer.Ordinal)!].Leader = true;
      } else {
        var name = scc.First();
        if (graph[name].Contains(name)) {
          grammar.Rules[name].LeftRecursive = true;
          grammar.Rules[name].Leader = true;
        }
      }
    }

    return problems;
  }

  /// <summary>An edge from A to B where A may invoke B at its first position (<c>make_first_graph</c>).</summary>
  private static Dictionary<string, HashSet<string>> FirstGraph(ParserGrammar grammar) {
    var nullables = Nullables(grammar);
    var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var vertices = new List<string>();
    foreach (var rule in grammar.Rules) {
      var names = InitialNames(rule.Rhs, nullables);
      graph[rule.Name] = names;
      vertices.AddRange(names);
    }

    foreach (var vertex in vertices)
      graph.TryAdd(vertex, []);

    return graph;
  }

  /// <summary>pegen's <c>NullableVisitor</c>: the rules and named items that may match nothing.</summary>
  private static HashSet<object> Nullables(ParserGrammar grammar) {
    var visited = new HashSet<GrammarRule>();
    var nullables = new HashSet<object>(ReferenceEqualityComparer.Instance);

    bool VisitRule(GrammarRule rule) {
      if (!visited.Add(rule))
        return false;

      if (Visit(rule.Rhs))
        nullables.Add(rule);

      return nullables.Contains(rule);
    }

    bool Visit(GrammarItem node) {
      switch (node) {
        case Rhs rhs:
          return rhs.Alts.Any(Visit);
        case Alt alt:
          return alt.Items.All(Visit);
        case Forced or Opt or Repeat { Minimum: 0 }:
          return true;
        case Repeat or Gather or Cut or StringLeaf or Predicate:
          return false;
        case Group group:
          return Visit(group.Rhs);
        case Hook hook:
          return Visit(hook.Node);
        case NamedItem item:
          if (Visit(item.Item))
            nullables.Add(item);

          return nullables.Contains(item);
        case NameLeaf leaf:
          return grammar.Rules.TryGet(leaf.Value, out var rule) && VisitRule(rule);
        default:
          // A lookahead: pegen's generic visit goes through its item and counts as not nullable.
          foreach (var child in node.Children)
            Visit(child);

          return false;
      }
    }

    foreach (var rule in grammar.Rules)
      VisitRule(rule);

    return nullables;
  }

  /// <summary>pegen's <c>InitialNamesVisitor</c>: the names an item may start with.</summary>
  private static HashSet<string> InitialNames(GrammarItem node, HashSet<object> nullables) {
    var names = new HashSet<string>(StringComparer.Ordinal);
    switch (node) {
      case Alt alt:
        foreach (var item in alt.Items) {
          names.UnionWith(InitialNames(item, nullables));
          if (!nullables.Contains(item))
            break;
        }

        break;
      case Forced or Cut or StringLeaf or Predicate:
        break;
      case NameLeaf leaf:
        names.Add(leaf.Value);
        break;
      default:
        foreach (var child in node.Children)
          names.UnionWith(InitialNames(child, nullables));

        break;
    }

    return names;
  }

  /// <summary>The strongly connected components of a graph (<c>sccutils.strongly_connected_components</c>, from mypy).</summary>
  private static List<HashSet<string>> StronglyConnectedComponents(Dictionary<string, HashSet<string>> edges) {
    var identified = new HashSet<string>(StringComparer.Ordinal);
    var stack = new List<string>();
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    var boundaries = new List<int>();
    var result = new List<HashSet<string>>();

    void Dfs(string v) {
      index[v] = stack.Count;
      stack.Add(v);
      boundaries.Add(index[v]);
      foreach (var w in edges[v]) {
        if (!index.ContainsKey(w)) {
          Dfs(w);
        } else if (!identified.Contains(w)) {
          while (index[w] < boundaries[^1])
            boundaries.RemoveAt(boundaries.Count - 1);
        }
      }

      if (boundaries[^1] == index[v]) {
        boundaries.RemoveAt(boundaries.Count - 1);
        var scc = new HashSet<string>(stack.Skip(index[v]), StringComparer.Ordinal);
        stack.RemoveRange(index[v], stack.Count - index[v]);
        identified.UnionWith(scc);
        result.Add(scc);
      }
    }

    foreach (var v in edges.Keys)
      if (!index.ContainsKey(v))
        Dfs(v);

    return result;
  }

  /// <summary>The cycles of a component from a start (<c>sccutils.find_cycles_in_scc</c>).</summary>
  private static IEnumerable<List<string>> CyclesInScc(Dictionary<string, HashSet<string>> graph, HashSet<string> scc, string start) {
    var reduced = scc.ToDictionary(source => source, source => graph[source].Where(scc.Contains).ToList(), StringComparer.Ordinal);
    return Dfs(start, []);

    IEnumerable<List<string>> Dfs(string node, List<string> path) {
      if (path.Contains(node)) {
        yield return [.. path, node];
        yield break;
      }

      List<string> next = [.. path, node];
      foreach (var child in reduced[node])
        foreach (var cycle in Dfs(child, next))
          yield return cycle;
    }
  }

}

/// <summary>One token name of a grammar's <c>@tokens</c> table: the kind it is, or the method that reads it.</summary>
/// <param name="Name">The name the grammar uses (<c>NAME</c>).</param>
/// <param name="Kind">The member of the token-kind enum, or <see langword="null"/> where a method reads it.</param>
/// <param name="Method">The method of the hand-written half that reads it, or <see langword="null"/>.</param>
/// <param name="Type">The type the method returns, where it is not the token type.</param>
public sealed record GrammarToken(string Name, string? Kind, string? Method, string? Type);

/// <summary>Reads the <c>@tokens</c> table: one token per line, <c>NAME Kind</c> or <c>NAME - Method Type</c> or <c>NAME Kind Method Type</c>.</summary>
public static class GrammarTokens {

  private static readonly ConditionalWeakTable<ParserGrammar, IReadOnlyDictionary<string, GrammarToken>> _cache = [];

  /// <summary>The token table of a grammar; empty where it declares none.</summary>
  /// <param name="grammar">The grammar.</param>
  /// <exception cref="GrammarException">A line of the table has fewer than two or more than four words.</exception>
  public static IReadOnlyDictionary<string, GrammarToken> Of(ParserGrammar grammar) {
    ArgumentNullException.ThrowIfNull(grammar);
    return _cache.GetValue(grammar, Parse);
  }

  private static IReadOnlyDictionary<string, GrammarToken> Parse(ParserGrammar grammar) {
    var table = new Dictionary<string, GrammarToken>(StringComparer.Ordinal);
    foreach (var line in (grammar.Meta("tokens") ?? "").Split('\n')) {
      var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length == 0)
        continue;

      if (words.Length is < 2 or > 4)
        throw new GrammarException($"@tokens: '{line.Trim()}' is not 'NAME Kind', 'NAME - Method Type' or 'NAME Kind Method Type'");

      var kind = words[1] == "-" ? null : words[1];
      table[words[0]] = new(words[0], kind, words.Length > 2 ? words[2] : null, words.Length > 3 ? words[3] : null);
    }

    return table;
  }

}
