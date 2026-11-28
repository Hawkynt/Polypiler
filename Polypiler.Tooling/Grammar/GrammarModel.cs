using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Syntax;

namespace Polypiler.Tooling.Grammar;

/// <summary>
/// An item of an alternative, as pegen's <c>pegen/grammar.py</c> models it. <see cref="ToString"/> is
/// pegen's simple form (<c>SIMPLE_STR</c>): it names artificial rules, recognizes the alternatives of
/// <c>invalid_*</c> rules and spells a forced group in its message.
/// </summary>
public abstract class GrammarItem {

  /// <summary>The items directly inside this one, for the visitors of the checker.</summary>
  public abstract IEnumerable<GrammarItem> Children { get; }

}

/// <summary>A rule or a token by name (<c>expr</c>, <c>NAME</c>).</summary>
/// <param name="value">The name.</param>
/// <param name="line">The line of the grammar it is written on.</param>
public sealed class NameLeaf(string value, int line) : GrammarItem {

  /// <summary>The name.</summary>
  public string Value { get; } = value;

  /// <summary>The line of the grammar it is written on.</summary>
  public int Line { get; } = line;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [];

  /// <inheritdoc />
  public override string ToString() => this.Value == "ENDMARKER" ? "$" : this.Value;

}

/// <summary>What a string literal of the grammar matches.</summary>
public enum LiteralKind {

  /// <summary><c>'if'</c>: a keyword.</summary>
  Keyword,

  /// <summary><c>"match"</c>: a soft keyword, a name that is a keyword only where the grammar asks for it.</summary>
  SoftKeyword,

  /// <summary><c>'+'</c> or <c>"+"</c>: an operator or delimiter.</summary>
  Operator

}

/// <summary>A literal token (<c>'if'</c>, <c>"match"</c>, <c>'+'</c>).</summary>
/// <param name="value">The literal as the grammar spells it, quotes included.</param>
public sealed class StringLeaf(string value) : GrammarItem {

  /// <summary>The literal as the grammar spells it, quotes included.</summary>
  public string Value { get; } = value;

  /// <summary>The literal without its quotes and escapes.</summary>
  public string Text { get; } = GrammarText.Unquote(value);

  /// <summary>
  /// A keyword where the text is an identifier in single quotes, a soft keyword where it is one in
  /// double quotes, an operator otherwise (pegen's <c>KeywordCollectorVisitor</c>).
  /// </summary>
  public LiteralKind Kind => !GrammarText.IsIdentifier(this.Text) ? LiteralKind.Operator
    : this.Value.EndsWith('\'') ? LiteralKind.Keyword : LiteralKind.SoftKeyword;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [];

  /// <inheritdoc />
  public override string ToString() => this.Value;

}

/// <summary>A parenthesized choice (<c>( a | b )</c>).</summary>
/// <param name="rhs">The choice.</param>
public sealed class Group(Rhs rhs) : GrammarItem {

  /// <summary>The choice.</summary>
  public Rhs Rhs { get; } = rhs;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Rhs];

  /// <inheritdoc />
  public override string ToString() => $"({this.Rhs})";

}

/// <summary>An optional item (<c>e?</c>, <c>[ e ]</c>).</summary>
/// <param name="node">The item, an atom or the choice in brackets.</param>
public sealed class Opt(GrammarItem node) : GrammarItem {

  /// <summary>The item.</summary>
  public GrammarItem Node { get; } = node;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() {
    var s = this.Node.ToString()!;
    return s.Contains(' ', StringComparison.Ordinal) ? $"[{s}]" : $"{s}?";
  }

}

/// <summary>A repetition: <c>e*</c> (<see cref="Minimum"/> 0) or <c>e+</c> (1).</summary>
/// <param name="node">The item repeated.</param>
/// <param name="minimum">0 or 1.</param>
public sealed class Repeat(GrammarItem node, int minimum) : GrammarItem {

  /// <summary>The item repeated.</summary>
  public GrammarItem Node { get; } = node;

  /// <summary>0 for <c>*</c>, 1 for <c>+</c>.</summary>
  public int Minimum { get; } = minimum;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() {
    var s = this.Node.ToString()!;
    var mark = this.Minimum == 0 ? "*" : "+";
    return s.Contains(' ', StringComparison.Ordinal) ? $"({s}){mark}" : $"{s}{mark}";
  }

}

/// <summary>One or more items between separators, the separators dropped (<c>','.e+</c>).</summary>
/// <param name="separator">The separator.</param>
/// <param name="node">The item.</param>
public sealed class Gather(GrammarItem separator, GrammarItem node) : GrammarItem {

  /// <summary>The separator.</summary>
  public GrammarItem Separator { get; } = separator;

  /// <summary>The item.</summary>
  public GrammarItem Node { get; } = node;

  /// <summary>pegen's <c>Gather</c> yields only its item to the visitors.</summary>
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() => $"{this.Separator}.{this.Node}+";

}

/// <summary>A lookahead that consumes nothing: <c>&amp;e</c> or <c>!e</c>.</summary>
/// <param name="node">The item looked for.</param>
/// <param name="positive">Whether it must match (<c>&amp;</c>) or must not (<c>!</c>).</param>
public sealed class Lookahead(GrammarItem node, bool positive) : GrammarItem {

  /// <summary>The item looked for.</summary>
  public GrammarItem Node { get; } = node;

  /// <summary>Whether it must match.</summary>
  public bool Positive { get; } = positive;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() => $"{(this.Positive ? "&" : "!")}{this.Node}";

}

/// <summary>An item that must follow (<c>&amp;&amp;e</c>): its absence is an error at once.</summary>
/// <param name="node">A literal or a group.</param>
public sealed class Forced(GrammarItem node) : GrammarItem {

  /// <summary>The item.</summary>
  public GrammarItem Node { get; } = node;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() => $"&&{this.Node}";

}

/// <summary>The cut (<c>~</c>): once passed, the rule's later alternatives are not tried.</summary>
public sealed class Cut : GrammarItem {

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [];

  /// <inheritdoc />
  public override string ToString() => "~";

}

/// <summary>An item parsed inside a hook (<c>nolit(e)</c>): <c>EnterNolit()</c> before, <c>LeaveNolit()</c> after.</summary>
/// <param name="name">The hook, as <c>@hooks</c> declares it.</param>
/// <param name="node">The item.</param>
public sealed class Hook(string name, GrammarItem node) : GrammarItem {

  /// <summary>The hook.</summary>
  public string Name { get; } = name;

  /// <summary>The item.</summary>
  public GrammarItem Node { get; } = node;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Node];

  /// <inheritdoc />
  public override string ToString() => $"{this.Name}({this.Node})";

}

/// <summary>A predicate of the hand-written half (<c>?litok</c>): <c>bool litok()</c>, consuming nothing.</summary>
/// <param name="name">The method.</param>
public sealed class Predicate(string name) : GrammarItem {

  /// <summary>The method.</summary>
  public string Name { get; } = name;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [];

  /// <inheritdoc />
  public override string ToString() => $"?{this.Name}";

}

/// <summary>An item of an alternative with its optional name and type (<c>a[expr_ty]=e</c>).</summary>
/// <param name="name">The name the action reads it by.</param>
/// <param name="item">The item.</param>
/// <param name="type">The type it is cast to.</param>
public sealed class NamedItem(string? name, GrammarItem item, string? type = null) : GrammarItem {

  /// <summary>The name the action reads it by.</summary>
  public string? Name { get; } = name;

  /// <summary>The item.</summary>
  public GrammarItem Item { get; } = item;

  /// <summary>The type it is cast to.</summary>
  public string? Type { get; } = type;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => [this.Item];

  /// <inheritdoc />
  public override string ToString() => this.Item.ToString()!;

}

/// <summary>A guard of an alternative on an axis of the profile (<c>@version &gt;= 3.10</c>).</summary>
/// <param name="Axis">The axis.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">The value compared with.</param>
/// <param name="Message">
/// The error where the guard fails after the alternative matched (<c>@message "…"</c>, pegen's
/// <c>CHECK_VERSION</c>); without one, the alternative is not tried where the guard fails.
/// </param>
public sealed record GrammarGuard(string Axis, GuardOperator Operator, string Value, string? Message) {

  /// <summary>The operator as the grammar spells it.</summary>
  public string Spelling => this.Operator switch {
    GuardOperator.Equal => "==",
    GuardOperator.NotEqual => "!=",
    GuardOperator.Less => "<",
    GuardOperator.LessOrEqual => "<=",
    GuardOperator.Greater => ">",
    _ => ">="
  };

  /// <inheritdoc />
  public override string ToString() => $"@{this.Axis} {this.Spelling} {this.Value}";

}

/// <summary>One alternative: its items, its action and its guards.</summary>
/// <param name="items">The items.</param>
/// <param name="action">The action, a C# expression; <see langword="null"/> for the default.</param>
/// <param name="line">The line of the grammar it starts on.</param>
public sealed class Alt(IReadOnlyList<NamedItem> items, string? action, int line) : GrammarItem {

  /// <summary>The items.</summary>
  public IReadOnlyList<NamedItem> Items { get; } = items;

  /// <summary>The action, a C# expression; <see langword="null"/> for the default.</summary>
  public string? Action { get; } = action;

  /// <summary>The line of the grammar it starts on.</summary>
  public int Line { get; } = line;

  /// <summary>The guards on the profile.</summary>
  public IReadOnlyList<GrammarGuard> Guards { get; init; } = [];

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => this.Items;

  /// <summary>Whether it is an alternative of an invalid rule alone, tried only in the second pass (pegen's <c>visit_Alt</c>).</summary>
  public bool IsInvalid => this.Items.Count == 1 && this.Items[0].ToString().StartsWith("invalid_", StringComparison.Ordinal);

  /// <inheritdoc />
  public override string ToString() => string.Join(" ", this.Items);

}

/// <summary>A choice of alternatives.</summary>
/// <param name="alts">The alternatives, in the order they are tried.</param>
public sealed class Rhs(IReadOnlyList<Alt> alts) : GrammarItem {

  /// <summary>The alternatives.</summary>
  public IReadOnlyList<Alt> Alts { get; } = alts;

  /// <inheritdoc />
  public override IEnumerable<GrammarItem> Children => this.Alts;

  /// <summary>Whether a group of it is its one item, with no artificial rule (pegen's <c>can_be_inlined</c>).</summary>
  public bool CanBeInlined => this.Alts.Count == 1 && this.Alts[0].Items.Count == 1 && this.Alts[0].Action is null && this.Alts[0].Guards.Count == 0;

  /// <inheritdoc />
  public override string ToString() => string.Join(" | ", this.Alts);

}

/// <summary>A rule.</summary>
/// <param name="name">The name.</param>
/// <param name="type">The C# type of its result; <see langword="null"/> for <c>object</c>.</param>
/// <param name="rhs">The alternatives.</param>
/// <param name="memo">Whether it is marked <c>(memo)</c>.</param>
/// <param name="line">The line it is defined on.</param>
public sealed class GrammarRule(string name, string? type, Rhs rhs, bool memo, int line) {

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>The C# type of its result.</summary>
  public string? Type { get; } = type;

  /// <summary>The alternatives.</summary>
  public Rhs Rhs { get; } = rhs;

  /// <summary>Whether it is marked <c>(memo)</c>.</summary>
  public bool Memo { get; } = memo;

  /// <summary>The line it is defined on.</summary>
  public int Line { get; } = line;

  /// <summary>Whether it is part of a left-recursive cycle.</summary>
  public bool LeftRecursive { get; set; }

  /// <summary>Whether it is the rule of its cycle that grows the seed.</summary>
  public bool Leader { get; set; }

  /// <inheritdoc />
  public override string ToString() => $"{this.Name}: {this.Rhs}";

}

/// <summary>A grammar: its metas in the order written and its rules by name.</summary>
public sealed class ParserGrammar {

  /// <summary>A grammar of the rules and metas given.</summary>
  /// <param name="rules">The rules, in the order written.</param>
  /// <param name="metas">The metas, in the order written; a name may repeat.</param>
  public ParserGrammar(IEnumerable<GrammarRule> rules, IEnumerable<KeyValuePair<string, string?>> metas) {
    ArgumentNullException.ThrowIfNull(rules);
    ArgumentNullException.ThrowIfNull(metas);
    foreach (var rule in rules)
      this.Rules.Add(rule.Name, rule);

    this.Metas = [.. metas];
  }

  /// <summary>The rules by name, in the order written.</summary>
  public OrderedRules Rules { get; } = new();

  /// <summary>The metas, in the order written.</summary>
  public IReadOnlyList<KeyValuePair<string, string?>> Metas { get; }

  /// <summary>The value of the last meta of a name, as pegen's dictionary keeps it.</summary>
  /// <param name="name">The meta.</param>
  public string? Meta(string name) => this.Metas.LastOrDefault(meta => meta.Key == name).Value;

  /// <summary>Whether a meta of the name is given.</summary>
  /// <param name="name">The meta.</param>
  public bool HasMeta(string name) => this.Metas.Any(meta => meta.Key == name);

  /// <summary>Every value of a meta that may repeat, or whose value lists names separated by spaces.</summary>
  /// <param name="name">The meta.</param>
  public IReadOnlyList<string> Words(string name) => [.. this.Metas.Where(meta => meta.Key == name)
    .SelectMany(meta => (meta.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))];

}

/// <summary>Rules by name in the order they were added.</summary>
public sealed class OrderedRules : IEnumerable<GrammarRule> {

  private readonly List<GrammarRule> _order = [];
  private readonly Dictionary<string, GrammarRule> _byName = new(StringComparer.Ordinal);

  /// <summary>The number of rules.</summary>
  public int Count => this._order.Count;

  /// <summary>The rule of a name.</summary>
  /// <param name="name">The name.</param>
  public GrammarRule this[string name] => this._byName[name];

  /// <summary>The names in order.</summary>
  public IEnumerable<string> Names => this._order.Select(rule => rule.Name);

  /// <summary>Adds a rule.</summary>
  /// <param name="name">Its name.</param>
  /// <param name="rule">The rule.</param>
  /// <exception cref="GrammarException">A rule of the name exists (pegen's <c>Repeated rule</c>).</exception>
  public void Add(string name, GrammarRule rule) {
    if (!this._byName.TryAdd(name, rule))
      throw new GrammarException($"repeated rule '{name}' at line {rule.Line}");

    this._order.Add(rule);
  }

  /// <summary>Whether a rule of the name exists.</summary>
  /// <param name="name">The name.</param>
  public bool Contains(string name) => this._byName.ContainsKey(name);

  /// <summary>The rule of a name, if there is one.</summary>
  /// <param name="name">The name.</param>
  /// <param name="rule">The rule.</param>
  public bool TryGet(string name, out GrammarRule rule) => this._byName.TryGetValue(name, out rule!);

  /// <inheritdoc />
  public IEnumerator<GrammarRule> GetEnumerator() => this._order.GetEnumerator();

  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

}

/// <summary>A grammar that cannot be read, checked or generated: the message names the place.</summary>
/// <param name="message">The message.</param>
public sealed class GrammarException(string message) : Exception(message);

/// <summary>Text helpers of the grammar format.</summary>
public static class GrammarText {

  /// <summary>Whether a text is an identifier: a letter or <c>_</c>, then letters, digits or <c>_</c>.</summary>
  /// <param name="text">The text.</param>
  public static bool IsIdentifier(string text) {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
      return false;

    foreach (var c in text)
      if (!(char.IsLetterOrDigit(c) || c == '_'))
        return false;

    return true;
  }

  /// <summary>A literal of the grammar without quotes, its backslash escapes resolved.</summary>
  /// <param name="literal">The literal, in single, double or triple quotes.</param>
  public static string Unquote(string literal) {
    ArgumentNullException.ThrowIfNull(literal);
    var quote = literal.Length >= 6 && (literal.StartsWith("'''", StringComparison.Ordinal) || literal.StartsWith("\"\"\"", StringComparison.Ordinal)) ? 3 : 1;
    var body = literal[quote..^quote];
    var text = new System.Text.StringBuilder(body.Length);
    for (var i = 0; i < body.Length; i++) {
      if (body[i] != '\\' || i + 1 == body.Length) {
        text.Append(body[i]);
        continue;
      }

      var next = body[++i];
      text.Append(next switch {
        'n' => '\n',
        't' => '\t',
        _ => next
      });
    }

    return text.ToString();
  }

  /// <summary>A rule name in PascalCase, for the public parse method of a start rule (<c>file</c> to <c>File</c>).</summary>
  /// <param name="name">The rule name.</param>
  public static string Pascal(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var text = new System.Text.StringBuilder(name.Length);
    var upper = true;
    foreach (var c in name) {
      if (c == '_') {
        upper = true;
        continue;
      }

      text.Append(upper ? char.ToUpperInvariant(c) : c);
      upper = false;
    }

    return text.ToString();
  }

}
