using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Polypiler.Core.Syntax;

namespace Polypiler.Tooling.Grammar;

/// <summary>A directive of an overlay grammar: what it does to the rule of its name.</summary>
public enum OverlayAction {

  /// <summary>A rule of the grammar itself.</summary>
  Define,

  /// <summary><c>@replace</c>: the rule replaces the base's rule of the name.</summary>
  Replace,

  /// <summary><c>@add</c>: the alternatives are appended to the base's rule, or the rule is added.</summary>
  Add,

  /// <summary><c>@remove</c>: the rule, or the alternatives listed, are removed from the base.</summary>
  Remove

}

/// <summary>A rule as one file of a grammar writes it, with what it does to the rules of the base.</summary>
/// <param name="Action">What it does.</param>
/// <param name="Rule">The rule; for a plain <c>@remove name</c>, a rule without alternatives.</param>
public sealed record RuleDefinition(OverlayAction Action, GrammarRule Rule);

/// <summary>One grammar file as written: its metas and its rule definitions.</summary>
/// <param name="Metas">The metas in order.</param>
/// <param name="Rules">The rule definitions in order.</param>
public sealed record GrammarFile(IReadOnlyList<KeyValuePair<string, string?>> Metas, IReadOnlyList<RuleDefinition> Rules);

/// <summary>
/// Reads a grammar in pegen's format (<c>Tools/peg_generator/pegen/metagrammar.gram</c>, PEP 617) -
/// metas, rules with types and <c>(memo)</c>, alternatives over several lines, named and typed items,
/// groups, <c>[ ]</c>, <c>? * +</c>, gathers, lookaheads, cuts, forced items, <c>$</c> and actions in
/// braces taken as written - with the additions of ADR 1000: hooks <c>name(e)</c> of the names
/// <c>@hooks</c> declares, predicates <c>?name</c>, guards <c>@axis op value [@message "…"]</c> before
/// an alternative and the overlay directives <c>@replace</c>, <c>@add</c> and <c>@remove</c>.
/// </summary>
public sealed class GrammarReader {

  private enum Kind { Name, String, Number, Op, Newline, End }

  private readonly record struct Token(Kind Kind, string Text, int Line, int Column, bool LineStart, int Offset, int EndOffset);

  private readonly string _text;
  private readonly string _file;
  private readonly HashSet<string> _hooks = new(StringComparer.Ordinal);
  private int _pos;
  private int _line = 1;
  private int _lineStart;
  private int _depth;
  private bool _lineHasToken;
  private Token _token;

  private GrammarReader(string text, string file) {
    this._text = text.ReplaceLineEndings("\n");
    this._file = file;
    this._token = this.Scan();
  }

  /// <summary>Reads one grammar file.</summary>
  /// <param name="text">The text of the file.</param>
  /// <param name="file">Its name, for the messages.</param>
  /// <exception cref="GrammarException">The text is no grammar.</exception>
  public static GrammarFile Read(string text, string file = "<grammar>") {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(file);
    return new GrammarReader(text, file).File();
  }

  // ---------------------------------------------------------------------------------------------
  // Scanner: Python's tokenizer as far as a grammar needs it.

  private GrammarException Error(string message, int line) => new($"{this._file}:{line}: {message}");

  private Token Scan() {
    while (true) {
      if (this._pos >= this._text.Length) {
        if (this._lineHasToken) {
          this._lineHasToken = false;
          return new(Kind.Newline, "", this._line, 0, false, this._pos, this._pos);
        }

        return new(Kind.End, "", this._line, 0, false, this._pos, this._pos);
      }

      var c = this._text[this._pos];
      if (c == '\n') {
        this._pos++;
        var line = this._line++;
        this._lineStart = this._pos;
        if (this._depth == 0 && this._lineHasToken) {
          this._lineHasToken = false;
          return new(Kind.Newline, "", line, 0, false, this._pos - 1, this._pos);
        }

        continue;
      }

      if (c is ' ' or '\t' or '\r' or '\f') {
        this._pos++;
        continue;
      }

      if (c == '#') {
        while (this._pos < this._text.Length && this._text[this._pos] != '\n')
          this._pos++;

        continue;
      }

      var lineStart = !this._lineHasToken;
      this._lineHasToken = true;
      var start = this._pos;
      var column = start - this._lineStart;
      var startLine = this._line;
      Kind kind;
      if (char.IsLetter(c) || c == '_') {
        while (this._pos < this._text.Length && (char.IsLetterOrDigit(this._text[this._pos]) || this._text[this._pos] == '_'))
          this._pos++;

        kind = Kind.Name;
      } else if (char.IsDigit(c)) {
        while (this._pos < this._text.Length && (char.IsDigit(this._text[this._pos]) || this._text[this._pos] == '.'))
          this._pos++;

        kind = Kind.Number;
      } else if (c is '\'' or '"') {
        this.SkipString();
        kind = Kind.String;
      } else {
        this._pos++;
        if (this._pos < this._text.Length && this._text[this._pos] == '=' && c is '<' or '>' or '=' or '!')
          this._pos++;

        if (c is '(' or '[')
          this._depth++;
        else if (c is ')' or ']' && this._depth > 0)
          this._depth--;

        kind = Kind.Op;
      }

      return new(kind, this._text[start..this._pos], startLine, column, lineStart, start, this._pos);
    }
  }

  /// <summary>Skips a string in single, double or triple quotes with backslash escapes; lines inside triple quotes count.</summary>
  private void SkipString() {
    var line = this._line;
    var quote = this._text[this._pos];
    var triple = this._pos + 2 < this._text.Length && this._text[this._pos + 1] == quote && this._text[this._pos + 2] == quote;
    this._pos += triple ? 3 : 1;
    while (true) {
      if (this._pos >= this._text.Length || !triple && this._text[this._pos] == '\n')
        throw this.Error("unterminated string", line);

      var c = this._text[this._pos];
      if (c == '\\') {
        if (this._pos + 1 < this._text.Length && this._text[this._pos + 1] == '\n')
          this.NewLineInside(this._pos + 1);

        this._pos += 2;
        continue;
      }

      if (c == '\n')
        this.NewLineInside(this._pos);

      if (c == quote && (!triple || this._pos + 2 < this._text.Length && this._text[this._pos + 1] == quote && this._text[this._pos + 2] == quote)) {
        this._pos += triple ? 3 : 1;
        return;
      }

      this._pos++;
    }
  }

  private void NewLineInside(int position) {
    this._line++;
    this._lineStart = position + 1;
  }

  /// <summary>
  /// The text between an opening bracket just read and its partner, taken as written: an action in
  /// braces or a type in brackets. Brackets in strings and character literals do not count.
  /// </summary>
  private string Raw(char open, char close) {
    var line = this._token.Line;
    this._pos = this._token.EndOffset;
    if (open == '[')
      this._depth--;

    var start = this._pos;
    var depth = 1;
    while (true) {
      if (this._pos >= this._text.Length)
        throw this.Error($"'{open}' is never closed", line);

      var c = this._text[this._pos];
      if (c is '"' or '\'') {
        this.SkipLiteral(c, line);
        continue;
      }

      if (c == '\n')
        this.NewLineInside(this._pos);

      if (c == open)
        depth++;
      else if (c == close && --depth == 0)
        break;

      this._pos++;
    }

    var text = this._text[start..this._pos].Trim();
    this._pos++;
    this._token = this.Scan();
    return text;
  }

  /// <summary>Skips a string or character literal of an action, C# or C: backslash escapes, no lines.</summary>
  private void SkipLiteral(char quote, int line) {
    this._pos++;
    while (true) {
      if (this._pos >= this._text.Length || this._text[this._pos] == '\n')
        throw this.Error("unterminated literal in an action", line);

      var c = this._text[this._pos++];
      if (c == '\\')
        this._pos++;
      else if (c == quote)
        return;
    }
  }

  private Token Take() {
    var token = this._token;
    this._token = this.Scan();
    return token;
  }

  private bool IsOp(string text) => this._token.Kind == Kind.Op && this._token.Text == text;

  private Token ExpectOp(string text) {
    if (!this.IsOp(text))
      throw this.Error($"expected '{text}', found {Describe(this._token)}", this._token.Line);

    return this.Take();
  }

  private Token ExpectKind(Kind kind, string what) {
    if (this._token.Kind != kind)
      throw this.Error($"expected {what}, found {Describe(this._token)}", this._token.Line);

    return this.Take();
  }

  /// <summary>A token for a message: the end of a line, whose text is empty, or the text in quotes.</summary>
  private static string Describe(Token token) => token.Text.Length == 0 ? "the end of the line" : $"'{token.Text}'";

  // ---------------------------------------------------------------------------------------------
  // Parser

  private GrammarFile File() {
    var metas = new List<KeyValuePair<string, string?>>();
    var rules = new List<RuleDefinition>();
    while (this._token.Kind != Kind.End) {
      if (this._token.Column != 0)
        throw this.Error($"expected a rule or a meta at the start of a line, found {Describe(this._token)}", this._token.Line);

      if (this.IsOp("@")) {
        var at = this.Take();
        var name = this.ExpectKind(Kind.Name, "the name of a meta").Text;
        if (name is "replace" or "add" or "remove") {
          var action = name == "replace" ? OverlayAction.Replace : name == "add" ? OverlayAction.Add : OverlayAction.Remove;
          rules.Add(new(action, this.Rule(action == OverlayAction.Remove, at.Line)));
          continue;
        }

        string? value = null;
        if (this._token.Kind is Kind.Name or Kind.Number)
          value = this.Take().Text;
        else if (this._token.Kind == Kind.String)
          value = GrammarText.Unquote(this.Take().Text);

        this.ExpectKind(Kind.Newline, "the end of the meta");
        metas.Add(new(name, value));
        if (name == "hooks")
          foreach (var hook in (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            this._hooks.Add(hook);

        continue;
      }

      rules.Add(new(OverlayAction.Define, this.Rule(false, this._token.Line)));
    }

    return new(metas, rules);
  }

  private GrammarRule Rule(bool removal, int line) {
    var name = this.ExpectKind(Kind.Name, "a rule name").Text;
    string? type = null;
    if (this.IsOp("["))
      type = this.Raw('[', ']');

    var memo = false;
    if (this.IsOp("(")) {
      this.Take();
      if (this._token.Kind != Kind.Name || this._token.Text != "memo")
        throw this.Error($"expected 'memo', found {Describe(this._token)}", this._token.Line);

      this.Take();
      this.ExpectOp(")");
      memo = true;
    }

    var alts = new List<Alt>();
    if (removal && this._token.Kind == Kind.Newline) {
      this.Take();
      return new(name, type, new Rhs(alts), memo, line);
    }

    this.ExpectOp(":");
    if (this.IsOp("|"))
      this.Take();

    if (this._token.Kind != Kind.Newline)
      alts.AddRange(this.Alts());

    this.ExpectKind(Kind.Newline, "the end of the rule");
    while (this._token.LineStart && this._token.Column > 0) {
      this.ExpectOp("|");
      alts.AddRange(this.Alts());
      this.ExpectKind(Kind.Newline, "the end of the alternative");
    }

    if (alts.Count == 0)
      throw this.Error($"rule '{name}' has no alternatives", line);

    return new(name, type, new Rhs(alts), memo, line);
  }

  private List<Alt> Alts() {
    var alts = new List<Alt> { this.Alt() };
    while (this.IsOp("|")) {
      this.Take();
      alts.Add(this.Alt());
    }

    return alts;
  }

  private Alt Alt() {
    var line = this._token.Line;
    var guards = new List<GrammarGuard>();
    while (this.IsOp("@"))
      guards.Add(this.Guard());

    var items = new List<NamedItem>();
    while (this.StartsItem())
      items.Add(this.NamedItem());

    if (items.Count == 0)
      throw this.Error($"expected an item, found {Describe(this._token)}", this._token.Line);

    if (this.IsOp("$")) {
      this.Take();
      items.Add(new(null, new NameLeaf("ENDMARKER", line)));
    }

    string? action = null;
    if (this.IsOp("{"))
      action = this.Raw('{', '}');

    return new(items, action, line) { Guards = guards };
  }

  private GrammarGuard Guard() {
    var line = this.Take().Line;
    var axis = this.ExpectKind(Kind.Name, "the axis of a guard").Text;
    var op = GuardOperator.Equal;
    if (this._token.Kind == Kind.Op && this._token.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
      op = this.Take().Text switch {
        "==" => GuardOperator.Equal,
        "!=" => GuardOperator.NotEqual,
        "<" => GuardOperator.Less,
        "<=" => GuardOperator.LessOrEqual,
        ">" => GuardOperator.Greater,
        _ => GuardOperator.GreaterOrEqual
      };

    if (this._token.Kind is not (Kind.Name or Kind.Number or Kind.String))
      throw this.Error($"expected the value of the guard on '{axis}', found {Describe(this._token)}", line);

    var token = this.Take();
    var value = token.Kind == Kind.String ? GrammarText.Unquote(token.Text) : token.Text;
    if (axis == GrammarProfile.VersionAxis && !IsVersion(value))
      throw this.Error($"'{value}' is no version", line);

    string? message = null;
    if (this.IsOp("@")) {
      var mark = (this._pos, this._line, this._lineStart, this._depth, this._lineHasToken, this._token);
      this.Take();
      if (this._token.Kind == Kind.Name && this._token.Text == "message") {
        this.Take();
        message = GrammarText.Unquote(this.ExpectKind(Kind.String, "the message of the guard").Text);
      } else {
        (this._pos, this._line, this._lineStart, this._depth, this._lineHasToken, this._token) = mark;
      }
    }

    return new(axis, op, value, message);
  }

  private static bool IsVersion(string value) {
    foreach (var part in value.Split('.'))
      if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        return false;

    return true;
  }

  /// <summary>Whether a <c>?</c> starts a predicate (<c>a ?b</c>) rather than closing an optional item (<c>a? b</c>): space before it, a name right after.</summary>
  private bool StartsPredicate() {
    var at = this._token.Offset;
    return char.IsWhiteSpace(this._text[at - 1]) && at + 1 < this._text.Length && (char.IsLetter(this._text[at + 1]) || this._text[at + 1] == '_');
  }

  private bool StartsItem() => this._token.Kind switch {
    Kind.Name or Kind.String => true,
    Kind.Op => this._token.Text is "(" or "[" or "&" or "!" or "~" or "?",
    _ => false
  };

  private NamedItem NamedItem() {
    if (this._token.Kind == Kind.Name && !this._hooks.Contains(this._token.Text)) {
      var mark = (this._pos, this._line, this._lineStart, this._depth, this._lineHasToken, this._token);
      var name = this.Take();
      string? type = null;
      if (this.IsOp("["))
        type = this.Raw('[', ']');

      if (this.IsOp("=")) {
        this.Take();
        if (name.Text.StartsWith('_'))
          throw this.Error($"variable names cannot start with underscore: '{name.Text}'", name.Line);

        return new(name.Text, this.Item(), type);
      }

      (this._pos, this._line, this._lineStart, this._depth, this._lineHasToken, this._token) = mark;
    }

    if (this.IsOp("&")) {
      var first = this.Take();
      if (this.IsOp("&") && this._token.Offset == first.EndOffset) {
        this.Take();
        return new(null, new Forced(this.Atom()));
      }

      return new(null, new Lookahead(this.Atom(), true));
    }

    if (this.IsOp("!")) {
      this.Take();
      return new(null, new Lookahead(this.Atom(), false));
    }

    if (this.IsOp("~")) {
      this.Take();
      return new(null, new Cut());
    }

    return new(null, this.Item());
  }

  private GrammarItem Item() {
    if (this.IsOp("[")) {
      this.Take();
      var alts = this.Alts();
      this.ExpectOp("]");
      return new Opt(new Rhs(alts));
    }

    var atom = this.Atom();
    if (this.IsOp("?") && !this.StartsPredicate()) {
      this.Take();
      return new Opt(atom);
    }

    if (this.IsOp("*")) {
      this.Take();
      return new Repeat(atom, 0);
    }

    if (this.IsOp("+")) {
      this.Take();
      return new Repeat(atom, 1);
    }

    if (this.IsOp(".")) {
      this.Take();
      var node = this.Atom();
      this.ExpectOp("+");
      return new Gather(atom, node);
    }

    return atom;
  }

  private GrammarItem Atom() {
    var token = this._token;
    if (this.IsOp("(")) {
      this.Take();
      var alts = this.Alts();
      this.ExpectOp(")");
      return new Group(new Rhs(alts));
    }

    if (this.IsOp("?")) {
      this.Take();
      return new Predicate(this.ExpectKind(Kind.Name, "the name of a predicate").Text);
    }

    if (token.Kind == Kind.Name) {
      this.Take();
      if (this._hooks.Contains(token.Text)) {
        this.ExpectOp("(");
        var alts = this.Alts();
        this.ExpectOp(")");
        var rhs = new Rhs(alts);
        return new Hook(token.Text, rhs.CanBeInlined ? rhs.Alts[0].Items[0].Item : new Group(rhs));
      }

      return new NameLeaf(token.Text, token.Line);
    }

    if (token.Kind == Kind.String) {
      this.Take();
      return new StringLeaf(token.Text);
    }

    throw this.Error($"expected an item, found {Describe(token)}", token.Line);
  }

}

/// <summary>
/// Loads a grammar with its overlays: <c>@extends "base.pgram"</c> reads the base first, relative to
/// the overlay, and applies the overlay's definitions to it.
/// </summary>
public static class GrammarLoader {

  /// <summary>The metas that belong to one file and are not inherited by an overlay.</summary>
  private static readonly HashSet<string> _ownMetas = new(StringComparer.Ordinal) { "extends", "class", "output", "profile_output" };

  /// <summary>A grammar loaded from a file and every base it extends.</summary>
  /// <param name="Grammar">The merged grammar.</param>
  /// <param name="Texts">The text of every file read, the base first, with LF line endings.</param>
  public sealed record Loaded(ParserGrammar Grammar, IReadOnlyList<string> Texts) {

    /// <summary>The SHA-256 of the texts read, in hex: what the generated file records.</summary>
    public string Sha256 => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", this.Texts))));

  }

  /// <summary>Loads a grammar file with its bases.</summary>
  /// <param name="path">The grammar file.</param>
  /// <param name="read">Reads a file; <see cref="System.IO.File.ReadAllText(string)"/> by default.</param>
  /// <exception cref="GrammarException">A file is no grammar, an overlay applies to nothing, or the bases form a cycle.</exception>
  public static Loaded Load(string path, Func<string, string>? read = null) {
    ArgumentNullException.ThrowIfNull(path);
    read ??= System.IO.File.ReadAllText;
    var texts = new List<string>();
    var grammar = Load(System.IO.Path.GetFullPath(path), read, [], texts);
    return new(grammar, texts);
  }

  private static ParserGrammar Load(string path, Func<string, string> read, List<string> chain, List<string> texts) {
    var name = System.IO.Path.GetFileName(path);
    if (chain.Contains(path)) {
      var names = new List<string>();
      foreach (var member in chain[chain.IndexOf(path)..])
        names.Add(System.IO.Path.GetFileName(member));

      names.Add(name);
      throw new GrammarException($"@extends cycle: {string.Join(" -> ", names)}");
    }

    string text;
    try {
      text = read(path).ReplaceLineEndings("\n");
    } catch (System.IO.IOException exception) {
      throw new GrammarException($"{name}: cannot be read: {exception.Message}");
    }

    var file = GrammarReader.Read(text, name);
    var extends = System.Linq.Enumerable.LastOrDefault(file.Metas, meta => meta.Key == "extends").Value;
    if (extends is null) {
      texts.Add(text);
      var rules = new List<GrammarRule>();
      foreach (var definition in file.Rules) {
        if (definition.Action != OverlayAction.Define)
          throw new GrammarException($"{name}:{definition.Rule.Line}: @{definition.Action.ToString().ToLowerInvariant()} needs @extends");

        rules.Add(definition.Rule);
      }

      return new(rules, file.Metas);
    }

    chain.Add(path);
    var basis = Load(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, extends)), read, chain, texts);
    chain.RemoveAt(chain.Count - 1);
    texts.Add(text);
    return Apply(basis, file, name);
  }

  private static ParserGrammar Apply(ParserGrammar basis, GrammarFile overlay, string name) {
    var rules = new List<GrammarRule>(basis.Rules);
    int Find(string rule) => rules.FindIndex(existing => existing.Name == rule);
    foreach (var (action, rule) in overlay.Rules) {
      var index = Find(rule.Name);
      switch (action) {
        case OverlayAction.Define when index >= 0:
          throw new GrammarException($"{name}:{rule.Line}: rule '{rule.Name}' is defined by the base; use @replace");
        case OverlayAction.Define:
        case OverlayAction.Add when index < 0:
          rules.Add(rule);
          break;
        case OverlayAction.Replace when index < 0:
          throw new GrammarException($"{name}:{rule.Line}: @replace of undefined rule '{rule.Name}'");
        case OverlayAction.Replace:
          rules[index] = rule;
          break;
        case OverlayAction.Add:
          var old = rules[index];
          rules[index] = new(old.Name, rule.Type ?? old.Type, new Rhs([.. old.Rhs.Alts, .. rule.Rhs.Alts]), old.Memo || rule.Memo, old.Line);
          break;
        case OverlayAction.Remove when index < 0:
          throw new GrammarException($"{name}:{rule.Line}: @remove of undefined rule '{rule.Name}'");
        case OverlayAction.Remove when rule.Rhs.Alts.Count == 0:
          rules.RemoveAt(index);
          break;
        default:
          var kept = new List<Alt>(rules[index].Rhs.Alts);
          foreach (var alt in rule.Rhs.Alts) {
            var at = kept.FindIndex(existing => existing.ToString() == alt.ToString());
            if (at < 0)
              throw new GrammarException($"{name}:{alt.Line}: rule '{rule.Name}' has no alternative '{alt}' to remove");

            kept.RemoveAt(at);
          }

          var current = rules[index];
          rules[index] = new(current.Name, current.Type, new Rhs(kept), current.Memo, current.Line);
          break;
      }
    }

    var metas = new List<KeyValuePair<string, string?>>();
    foreach (var meta in basis.Metas)
      if (!_ownMetas.Contains(meta.Key))
        metas.Add(meta);

    metas.AddRange(overlay.Metas);
    return new(rules, metas);
  }

}
