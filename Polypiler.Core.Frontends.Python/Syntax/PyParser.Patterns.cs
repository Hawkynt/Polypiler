using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>The <c>match</c> statement and its patterns.</summary>
internal sealed partial class PyParser {

  private sealed record KeyPattern(PyNode Key, PyNode Pattern);

  private PyNode? MatchStatement() => this.Rule(() => {
    var start = this.Peek();
    var match = this.Try(() => {
      if (this.SoftKeyword("match") is null || this.SubjectExpression() is not { } subject || this.Expect(":") is null || this.Expect(PyTokenKind.Newline) is null
        || this.Expect(PyTokenKind.Indent) is null || this.Repeat(this.CaseBlock, 1) is not { } cases || this.Expect(PyTokenKind.Dedent) is null)
        return null;

      return this.Version(10, "Pattern matching is", PyAst.Make("Match", this.Extra(start), subject, Nullable(cases)));
    });
    if (match is not null)
      return match;

    return this._invalid ? this.InvalidMatchStatement() : null;
  });

  private PyNode? SubjectExpression() => this.Rule(() => {
    var start = this.Peek();
    var tuple = this.Try(() => {
      if (this.StarNamedExpression() is not { } first || this.Expect(",") is null)
        return null;

      var rest = this.Try(this.StarNamedExpressions);
      return PyAst.Make("Tuple", this.Extra(start), Nullable(Prepend(first, rest)), PyAst.Singleton("Load"));
    });
    return tuple ?? this.NamedExpression();
  });

  private PyNode? CaseBlock() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidCaseBlock);

    if (this.SoftKeyword("case") is null || this.Patterns() is not { } pattern)
      return null;

    var guard = this.Try(this.Guard);
    if (this.Expect(":") is null || this.Block() is not { } body)
      return null;

    return PyAst.Make("match_case", null, pattern, guard, Nullable(body));
  });

  private PyNode? Guard() => this.Rule(() => this.Expect("if") is not null ? this.NamedExpression() : null);

  private PyNode? Patterns() => this.Rule(() => {
    var start = this.Peek();
    var sequence = this.Try(() => this.OpenSequencePattern() is { } patterns ? PyAst.Make("MatchSequence", this.Extra(start), Nullable(patterns)) : null);
    return sequence ?? this.Pattern();
  });

  private PyNode? Pattern() => this.Rule(() => this.Try(this.AsPattern) ?? this.OrPattern());

  private PyNode? AsPattern() => this.Rule(() => {
    var start = this.Peek();
    var named = this.Try(() => this.OrPattern() is { } pattern && this.Expect("as") is not null && this.PatternCaptureTarget() is { } target
      ? PyAst.Make("MatchAs", this.Extra(start), pattern, target.Text("id")) : null);
    if (named is not null)
      return named;

    return this._invalid ? this.InvalidAsPattern() : null;
  });

  private PyNode? OrPattern() => this.Rule(() => {
    var start = this.Peek();
    if (this.Gather("|", this.ClosedPattern) is not { } patterns)
      return null;

    return patterns.Count == 1 ? patterns[0] : PyAst.Make("MatchOr", this.Extra(start), Nullable(patterns));
  });

  private PyNode? ClosedPattern() => this.Memo(MemoClosedPattern, () =>
    this.Try(this.LiteralPattern) ?? this.Try(this.CapturePattern) ?? this.Try(this.WildcardPattern) ?? this.Try(this.ValuePattern) ?? this.Try(this.GroupPattern)
    ?? this.Try(this.SequencePattern) ?? this.Try(this.MappingPattern) ?? this.ClassPattern());

  private PyNode? LiteralPattern() => this.Rule(() => {
    var start = this.Peek();
    var number = this.Try(() => this.SignedNumber() is { } value && !this.CheckAny("+", "-") ? PyAst.Make("MatchValue", this.Extra(start), value) : null);
    if (number is not null)
      return number;

    var complex = this.Try(() => this.ComplexNumber() is { } value ? PyAst.Make("MatchValue", this.Extra(start), value) : null);
    if (complex is not null)
      return complex;

    var text = this.Try(() => this.Peek().Kind is PyTokenKind.String or PyTokenKind.FStringStart or PyTokenKind.TStringStart && this.Strings() is { } value
      ? PyAst.Make("MatchValue", this.Extra(start), value) : null);
    if (text is not null)
      return text;

    var token = this.Peek();
    if (token.Kind != PyTokenKind.Keyword || token.Text is not ("None" or "True" or "False"))
      return null;

    this._mark++;
    return PyAst.Make("MatchSingleton", this.Extra(start), token.Text switch { "True" => true, "False" => false, _ => null });
  });

  private PyNode? LiteralExpression() => this.Rule(() => {
    var number = this.Try(() => this.SignedNumber() is { } value && !this.CheckAny("+", "-") ? value : null);
    if (number is not null)
      return number;

    if (this.Try(this.ComplexNumber) is { } complex)
      return complex;

    if (this.Peek().Kind is PyTokenKind.String or PyTokenKind.FStringStart or PyTokenKind.TStringStart && this.Try(this.Strings) is { } text)
      return text;

    var token = this.Peek();
    if (token.Kind != PyTokenKind.Keyword || token.Text is not ("None" or "True" or "False"))
      return null;

    this._mark++;
    return PyAst.Make("Constant", this.Extra(token), token.Text switch { "True" => true, "False" => false, _ => null }, null);
  });

  private PyNode? ComplexNumber() => this.Rule(() => {
    var start = this.Peek();
    foreach (var (token, op) in new[] { ("+", "Add"), ("-", "Sub") }) {
      var complex = this.Try(() => this.SignedRealNumber() is { } real && this.Expect(token) is not null && this.ImaginaryNumber() is { } imaginary
        ? PyAst.Make("BinOp", this.Extra(start), real, PyAst.Singleton(op), imaginary) : null);
      if (complex is not null)
        return complex;
    }

    return null;
  });

  private PyNode? SignedNumber() => this.Rule(() => {
    if (this.Try(this.NumberToken) is { } number)
      return number;

    var start = this.Peek();
    return this.Expect("-") is not null && this.NumberToken() is { } negated ? PyAst.Make("UnaryOp", this.Extra(start), PyAst.Singleton("USub"), negated) : null;
  });

  private PyNode? SignedRealNumber() => this.Rule(() => {
    if (this.Try(this.RealNumber) is { } number)
      return number;

    var start = this.Peek();
    return this.Expect("-") is not null && this.RealNumber() is { } negated ? PyAst.Make("UnaryOp", this.Extra(start), PyAst.Singleton("USub"), negated) : null;
  });

  private PyNode? RealNumber() => this.Rule(() => {
    if (this.NumberToken() is not { } number)
      return null;

    return number.Value is PyComplex ? throw this.ErrorAt(number, "real number required in complex literal") : number;
  });

  private PyNode? ImaginaryNumber() => this.Rule(() => {
    if (this.NumberToken() is not { } number)
      return null;

    return number.Value is PyComplex ? number : throw this.ErrorAt(number, "imaginary number required in complex literal");
  });

  private PyNode? CapturePattern() => this.Rule(() => {
    var start = this.Peek();
    return this.PatternCaptureTarget() is { } target ? PyAst.Make("MatchAs", this.Extra(start), null, target.Text("id")) : null;
  });

  private PyNode? PatternCaptureTarget() => this.Rule(() => {
    if (this.CheckSoftKeyword("_") || this.NameToken() is not { } name || this.CheckAny(".", "(", "="))
      return null;

    return PyAst.SetContext(name, "Store");
  });

  private PyNode? WildcardPattern() => this.Rule(() => {
    var start = this.Peek();
    return this.SoftKeyword("_") is not null ? PyAst.Make("MatchAs", this.Extra(start), null, null) : null;
  });

  private PyNode? ValuePattern() => this.Rule(() => {
    var start = this.Peek();
    return this.Attr() is { } attr && !this.CheckAny(".", "(", "=") ? PyAst.Make("MatchValue", this.Extra(start), attr) : null;
  });

  /// <summary><c>attr: name_or_attr '.' NAME</c>: at least one dot.</summary>
  private PyNode? Attr() => this.Rule(() => this.NameOrAttr() is { Kind: "Attribute" } attribute ? attribute : null);

  /// <summary><c>name_or_attr: attr | NAME</c>, grown as a loop.</summary>
  private PyNode? NameOrAttr() => this.Rule(() => {
    var start = this.Peek();
    if (this.NameToken() is not { } value)
      return null;

    while (true) {
      var current = value;
      var grown = this.Try(() => this.Expect(".") is not null && this.NameToken() is { } name
        ? PyAst.Make("Attribute", this.Extra(start), current, name.Text("id"), PyAst.Singleton("Load")) : null);
      if (grown is null)
        return value;

      value = grown;
    }
  });

  private PyNode? GroupPattern() => this.Rule(() => this.Expect("(") is not null && this.Pattern() is { } pattern && this.Expect(")") is not null ? pattern : null);

  private PyNode? SequencePattern() => this.Rule(() => {
    var start = this.Peek();
    var list = this.Try(() => {
      if (this.Expect("[") is null)
        return null;

      var patterns = this.Try(this.MaybeSequencePattern);
      return this.Expect("]") is null ? null : PyAst.Make("MatchSequence", this.Extra(start), Nullable(patterns ?? []));
    });
    if (list is not null)
      return list;

    return this.Try(() => {
      if (this.Expect("(") is null)
        return null;

      var patterns = this.Try(this.OpenSequencePattern);
      return this.Expect(")") is null ? null : PyAst.Make("MatchSequence", this.Extra(start), Nullable(patterns ?? []));
    });
  });

  private List<PyNode>? OpenSequencePattern() => this.Rule(() => {
    if (this.MaybeStarPattern() is not { } first || this.Expect(",") is null)
      return null;

    return Prepend(first, this.Try(this.MaybeSequencePattern));
  });

  private List<PyNode>? MaybeSequencePattern() => this.Rule(() => {
    var patterns = this.Gather(",", this.MaybeStarPattern);
    if (patterns is null)
      return null;

    this.Expect(",");
    return patterns;
  });

  private PyNode? MaybeStarPattern() => this.Rule(() => this.Try(this.StarPattern) ?? this.Pattern());

  private PyNode? StarPattern() => this.Memo(MemoStarPattern, () => {
    var start = this.Peek();
    var named = this.Try(() => this.Expect("*") is not null && this.PatternCaptureTarget() is { } target ? PyAst.Make("MatchStar", this.Extra(start), target.Text("id")) : null);
    if (named is not null)
      return named;

    return this.Try(() => this.Expect("*") is not null && this.WildcardPattern() is not null ? PyAst.Make("MatchStar", this.Extra(start), null) : null);
  });

  private PyNode? MappingPattern() => this.Rule(() => {
    var start = this.Peek();
    PyNode Mapping(List<KeyPattern>? items, PyNode? rest)
      => PyAst.Make("MatchMapping", this.Extra(start), (items ?? []).Select(item => (PyNode?)item.Key).ToList(), (items ?? []).Select(item => (PyNode?)item.Pattern).ToList(),
        rest?.Text("id"));

    var empty = this.Try(() => this.Expect("{") is not null && this.Expect("}") is not null ? Mapping(null, null) : null);
    if (empty is not null)
      return empty;

    var restOnly = this.Try(() => {
      if (this.Expect("{") is null || this.DoubleStarPattern() is not { } rest)
        return null;

      this.Expect(",");
      return this.Expect("}") is null ? null : Mapping(null, rest);
    });
    if (restOnly is not null)
      return restOnly;

    var itemsAndRest = this.Try(() => {
      if (this.Expect("{") is null || this.ItemsPattern() is not { } items || this.Expect(",") is null || this.DoubleStarPattern() is not { } rest)
        return null;

      this.Expect(",");
      return this.Expect("}") is null ? null : Mapping(items, rest);
    });
    if (itemsAndRest is not null)
      return itemsAndRest;

    return this.Try(() => {
      if (this.Expect("{") is null || this.ItemsPattern() is not { } items)
        return null;

      this.Expect(",");
      return this.Expect("}") is null ? null : Mapping(items, null);
    });
  });

  private List<KeyPattern>? ItemsPattern() => this.Rule(() => this.Gather(",", this.KeyValuePattern));

  private KeyPattern? KeyValuePattern() => this.Rule(() => {
    var key = this.Try(this.LiteralExpression) ?? this.Attr();
    return key is not null && this.Expect(":") is not null && this.Pattern() is { } pattern ? new KeyPattern(key, pattern) : null;
  });

  private PyNode? DoubleStarPattern() => this.Rule(() => this.Expect("**") is not null ? this.PatternCaptureTarget() : null);

  private PyNode? ClassPattern() => this.Rule(() => {
    var start = this.Peek();
    PyNode Class(PyNode cls, List<PyNode>? positional, List<KeyPattern>? keywords)
      => PyAst.Make("MatchClass", this.Extra(start), cls, Nullable(positional ?? []), (keywords ?? []).Select(keyword => keyword.Key.Text("id")!).ToList(),
        (keywords ?? []).Select(keyword => (PyNode?)keyword.Pattern).ToList());

    var empty = this.Try(() => this.NameOrAttr() is { } cls && this.Expect("(") is not null && this.Expect(")") is not null ? Class(cls, null, null) : null);
    if (empty is not null)
      return empty;

    var positional = this.Try(() => {
      if (this.NameOrAttr() is not { } cls || this.Expect("(") is null || this.PositionalPatterns() is not { } patterns)
        return null;

      this.Expect(",");
      return this.Expect(")") is null ? null : Class(cls, patterns, null);
    });
    if (positional is not null)
      return positional;

    var keyword = this.Try(() => {
      if (this.NameOrAttr() is not { } cls || this.Expect("(") is null || this.KeywordPatterns() is not { } keywords)
        return null;

      this.Expect(",");
      return this.Expect(")") is null ? null : Class(cls, null, keywords);
    });
    if (keyword is not null)
      return keyword;

    var both = this.Try(() => {
      if (this.NameOrAttr() is not { } cls || this.Expect("(") is null || this.PositionalPatterns() is not { } patterns || this.Expect(",") is null
        || this.KeywordPatterns() is not { } keywords)
        return null;

      this.Expect(",");
      return this.Expect(")") is null ? null : Class(cls, patterns, keywords);
    });
    if (both is not null)
      return both;

    return this._invalid ? this.InvalidClassPattern() : null;
  });

  private List<PyNode>? PositionalPatterns() => this.Rule(() => this.Gather(",", this.Pattern));

  private List<KeyPattern>? KeywordPatterns() => this.Rule(() => this.Gather(",", this.KeywordPattern));

  private KeyPattern? KeywordPattern() => this.Rule(() =>
    this.NameToken() is { } name && this.Expect("=") is not null && this.Pattern() is { } pattern ? new KeyPattern(name, pattern) : null);

}
