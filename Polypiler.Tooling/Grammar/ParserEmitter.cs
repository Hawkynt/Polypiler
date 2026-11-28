using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Core.Syntax;

namespace Polypiler.Tooling.Grammar;

/// <summary>What a grammar is generated with.</summary>
/// <param name="GrammarName">The grammar's file name, for the header.</param>
/// <param name="Sha256">The SHA-256 of the grammar's text, for the header.</param>
/// <param name="ClassName">The class of the parser.</param>
public sealed record GeneratorOptions(string GrammarName, string Sha256, string ClassName) {

  /// <summary>The namespace; <c>@namespace</c> of the grammar where not given.</summary>
  public string? Namespace { get; init; }

  /// <summary>The profile the guards of a grammar with <c>@guards generate</c> are evaluated against.</summary>
  public GrammarProfile? Profile { get; init; }

}

/// <summary>
/// Turns a checked grammar into the generated half of a C# parser on <see cref="ParserBase{TToken, TKind}"/>,
/// shaped as pegen's C generator (<c>pegen/c_generator.py</c>, PSF-2.0) shapes its output: one method
/// per rule; artificial rules for groups (<c>_tmp_</c>), repetitions (<c>_loop0_</c>, <c>_loop1_</c>)
/// and gathers (<c>_gather_</c>), shared by structure; alternatives tried in order with the position
/// given back after each failure; the alternatives of an <c>invalid_*</c> rule only in the second pass;
/// memoization of the rules marked <c>(memo)</c>; seed growing for the leaders of left recursion; cuts,
/// lookaheads and forced items. Every rule call counts one level of nesting, as pegen's do.
/// </summary>
public sealed class ParserEmitter {

  private enum ArtificialKind { Tmp, Loop0, Loop1, Gather, Hook }

  private sealed record Method(string Name, string Type, Rhs Rhs, ArtificialKind? Kind, GrammarRule? Rule, string? HookName = null);

  private static readonly Regex _identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
  private static readonly Regex _construction = new(@"^new\s+([A-Za-z_][\w.]*(?:<[^()]*>)?)\s*[({]", RegexOptions.CultureInvariant);
  private static readonly Regex _cast = new(@"^\(\s*([A-Za-z_][\w.]*(?:<[^()]*>)?\??)\s*\)\s*[A-Za-z_@(""]", RegexOptions.CultureInvariant);

  private readonly ParserGrammar _grammar;
  private readonly GeneratorOptions _options;
  private readonly IReadOnlyDictionary<string, GrammarToken> _tokens;
  private readonly Dictionary<string, string> _literals = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Method> _artificial = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Method> _artificialByName = new(StringComparer.Ordinal);
  private readonly List<Method> _methods = [];
  private readonly Dictionary<string, int> _ruleIds = new(StringComparer.Ordinal);
  private readonly HashSet<string> _keywords = new(StringComparer.Ordinal);
  private readonly HashSet<string> _softKeywords = new(StringComparer.Ordinal);
  private readonly string _tokenType;
  private readonly string _kindType;
  private readonly (string Method, string Type)? _softKeyword;
  private readonly bool _generateGuards;
  private readonly (string Expression, string Type) _dummy = ("Dummy", "object");
  private int _counter;
  private int _variables;

  private ParserEmitter(ParserGrammar grammar, GeneratorOptions options) {
    this._grammar = grammar;
    this._options = options;
    this._tokens = GrammarTokens.Of(grammar);
    this._tokenType = grammar.Meta("token_type") ?? throw new GrammarException("the grammar names no token type: @token_type");
    this._kindType = grammar.Meta("token_kind") ?? throw new GrammarException("the grammar names no token kind: @token_kind");
    foreach (var line in (grammar.Meta("literals") ?? "").Split('\n')) {
      var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length == 0)
        continue;

      if (words.Length != 2)
        throw new GrammarException($"@literals: '{line.Trim()}' is not 'spelling Kind'");

      this._literals[words[0]] = words[1];
    }

    if (grammar.Meta("soft_keyword") is { } soft) {
      var words = soft.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length != 2)
        throw new GrammarException($"@soft_keyword: '{soft}' is not 'Method Type'");

      this._softKeyword = (words[0], words[1]);
    }

    this._generateGuards = grammar.Meta("guards") == "generate";
    if (grammar.Meta("dummy") is { } dummy) {
      var words = dummy.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length != 2)
        throw new GrammarException($"@dummy: '{dummy}' is not 'Expression Type'");

      this._dummy = (words[0], words[1]);
    }

    if (this._generateGuards && options.Profile is null)
      throw new GrammarException("the grammar's guards are evaluated at generation (@guards generate): give a profile");

    var id = 0;
    foreach (var rule in grammar.Rules)
      this._ruleIds[rule.Name] = id++;
  }

  /// <summary>Generates the C# of a grammar that <see cref="GrammarChecker.Check"/> found valid.</summary>
  /// <param name="grammar">The checked grammar.</param>
  /// <param name="options">The names and the profile.</param>
  /// <exception cref="GrammarException">The grammar lacks a meta the C# needs, or uses an item no C# can be made of.</exception>
  public static string Emit(ParserGrammar grammar, GeneratorOptions options) {
    ArgumentNullException.ThrowIfNull(grammar);
    ArgumentNullException.ThrowIfNull(options);
    return new ParserEmitter(grammar, options).Emit();
  }

  private string Emit() {
    var ns = this._options.Namespace ?? this._grammar.Meta("namespace") ?? throw new GrammarException("the grammar names no namespace: @namespace or --namespace");
    var name = this._options.ClassName;
    var starts = this._grammar.Words("start") is { Count: > 0 } named ? named : ["start"];
    foreach (var rule in this._grammar.Rules)
      this._methods.Add(new(RuleMethod(rule.Name), RuleType(rule), this.Guarded(rule.Rhs), null, rule));

    var bodies = new StringBuilder();
    for (var i = 0; i < this._methods.Count; i++)
      this.EmitMethod(bodies, this._methods[i]);

    var hasInvalid = this._grammar.Rules.Any(rule => rule.Name.StartsWith("invalid_", StringComparison.Ordinal));
    var text = new StringBuilder();
    text.Append("// <auto-generated>\n");
    text.Append(CultureInfo.InvariantCulture, $"// Generated by polypiler-tooling grammar generate from {this._options.GrammarName}; edit the grammar, not this file.\n");
    text.Append(CultureInfo.InvariantCulture, $"// grammar sha256: {this._options.Sha256}\n");
    if (this._options.Profile is { } profile && this._generateGuards)
      text.Append(CultureInfo.InvariantCulture, $"// profile: {string.Join(" ", profile.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}\n");

    text.Append("// </auto-generated>\n");
    text.Append("#nullable enable\n");
    var usings = new SortedSet<string>(StringComparer.Ordinal) { "System.Collections.Generic", "Polypiler.Core.Syntax" };
    usings.UnionWith(this._grammar.Words("using"));
    foreach (var item in usings)
      text.Append(CultureInfo.InvariantCulture, $"using {item};\n");

    text.Append(CultureInfo.InvariantCulture, $"\nnamespace {ns};\n\n");
    text.Append(CultureInfo.InvariantCulture, $"partial class {name} : ParserBase<{this._tokenType}, {this._kindType}> {{\n\n");
    text.Append("  /// <summary>The keywords of the grammar: its literals in single quotes that are identifiers.</summary>\n");
    text.Append(CultureInfo.InvariantCulture, $"  public static IReadOnlyList<string> Keywords {{ get; }} = [{string.Join(", ", this._keywords.Order(StringComparer.Ordinal).Select(Quote))}];\n\n");
    text.Append("  /// <summary>The soft keywords of the grammar: its literals in double quotes that are identifiers.</summary>\n");
    text.Append(CultureInfo.InvariantCulture, $"  public static IReadOnlyList<string> SoftKeywords {{ get; }} = [{string.Join(", ", this._softKeywords.Order(StringComparer.Ordinal).Select(Quote))}];\n");
    foreach (var start in starts) {
      var rule = this._grammar.Rules[start];
      text.Append(CultureInfo.InvariantCulture, $"\n  /// <summary>Parses with the start rule <c>{start}</c>; throws the syntax error where the source does not parse.</summary>\n");
      text.Append(CultureInfo.InvariantCulture, $"  public {RuleType(rule)} Parse{GrammarText.Pascal(start)}() => this.Run(this.{RuleMethod(start)}, {(hasInvalid ? "true" : "false")});\n");
    }

    if (this._grammar.Meta("unexplained") is { } unexplained) {
      text.Append("\n  /// <inheritdoc />\n");
      text.Append(CultureInfo.InvariantCulture, $"  protected override string UnexplainedMessage({this._tokenType} token) => {Quote(unexplained)}.Replace(\"{{token}}\", token.Text, System.StringComparison.Ordinal);\n");
    }

    text.Append(bodies);
    text.Append("}\n");
    return text.ToString();
  }

  // ---------------------------------------------------------------------------------------------
  // Guards evaluated at generation

  private Rhs Guarded(Rhs rhs) {
    if (!this._generateGuards)
      return rhs;

    var alts = new List<Alt>();
    foreach (var alt in rhs.Alts) {
      var failed = alt.Guards.Where(guard => !this._options.Profile!.Satisfies(guard.Axis, guard.Operator, guard.Value)).ToList();
      if (failed.Any(guard => guard.Message is null))
        continue;

      var items = alt.Items.Select(item => new NamedItem(item.Name, this.GuardedItem(item.Item), item.Type)).ToList();
      var action = failed.Count > 0 ? $"throw this.GuardFailed({Quote(failed[0].Message!)})" : alt.Action;
      alts.Add(new(items, action, alt.Line));
    }

    return new(alts);
  }

  private GrammarItem GuardedItem(GrammarItem item) => item switch {
    Group group => new Group(this.Guarded(group.Rhs)),
    Rhs rhs => this.Guarded(rhs),
    Opt opt => new Opt(this.GuardedItem(opt.Node)),
    Repeat repeat => new Repeat(this.GuardedItem(repeat.Node), repeat.Minimum),
    Gather gather => new Gather(this.GuardedItem(gather.Separator), this.GuardedItem(gather.Node)),
    Lookahead lookahead => new Lookahead(this.GuardedItem(lookahead.Node), lookahead.Positive),
    Forced forced => new Forced(this.GuardedItem(forced.Node)),
    Hook hook => new Hook(hook.Name, this.GuardedItem(hook.Node)),
    _ => item
  };

  // ---------------------------------------------------------------------------------------------
  // Types

  private static string RuleMethod(string name) => name + "_rule";

  private static string RuleType(GrammarRule rule) => rule.Type ?? "object";

  private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

  /// <summary>The C# type of an item's value; <c>object</c> for what carries none (cuts, and lookaheads and predicates, which no value is asked of).</summary>
  private string TypeOf(GrammarItem item) => item switch {
    NamedItem named => named.Type ?? this.TypeOf(named.Item),
    NameLeaf leaf when this._grammar.Rules.TryGet(leaf.Value, out var rule) => RuleType(rule),
    NameLeaf leaf => this._tokens.TryGetValue(leaf.Value, out var token) && token.Method is not null ? token.Type ?? this._tokenType : this._tokenType,
    StringLeaf { Kind: LiteralKind.SoftKeyword } when this._softKeyword is { } soft => soft.Type,
    StringLeaf => this._tokenType,
    Group group => this.ChoiceType(group.Rhs),
    Rhs rhs => this.ChoiceType(rhs),
    Opt opt => this.TypeOf(opt.Node),
    Repeat repeat => $"List<{this.TypeOf(repeat.Node)}>",
    Gather gather => $"List<{this.TypeOf(gather.Node)}>",
    Forced forced => this.TypeOf(forced.Node),
    Hook hook => this.TypeOf(hook.Node),
    _ => "object"
  };

  private string ChoiceType(Rhs rhs) {
    if (rhs.CanBeInlined)
      return this.TypeOf(rhs.Alts[0].Items[0].Item);

    var types = rhs.Alts.Select(this.AltType).Distinct(StringComparer.Ordinal).ToList();
    return types.Count == 1 ? types[0] : "object";
  }

  private string AltType(Alt alt) {
    if (alt.Action is { } action) {
      if (_identifier.IsMatch(action) && alt.Items.FirstOrDefault(item => item.Name == action) is { } named)
        return this.TypeOf(named);

      var construction = _construction.Match(action);
      if (construction.Success)
        return construction.Groups[1].Value;

      var cast = _cast.Match(action);
      return cast.Success ? cast.Groups[1].Value.TrimEnd('?') : "object";
    }

    var values = ValueItems(alt).ToList();
    return values.Count == 1 ? this.TypeOf(values[0]) : "object";
  }

  /// <summary>The items that carry a value into the default action, as pegen's local variables do: all but lookaheads and predicates.</summary>
  private static IEnumerable<NamedItem> ValueItems(Alt alt) => alt.Items.Where(item => item.Item is not (Lookahead or Predicate));

  // ---------------------------------------------------------------------------------------------
  // Artificial rules

  private string Artificial(ArtificialKind kind, GrammarItem node, Func<(Rhs Rhs, string Type)> make, string? hook = null) {
    var key = $"{kind}:{hook}:{Describe(node)}";
    if (this._artificial.TryGetValue(key, out var existing))
      return existing.Name;

    this._counter++;
    var prefix = kind switch {
      ArtificialKind.Tmp => "_tmp_",
      ArtificialKind.Loop0 => "_loop0_",
      ArtificialKind.Loop1 => "_loop1_",
      ArtificialKind.Gather => "_gather_",
      _ => "_hook_"
    };
    var (rhs, type) = make();
    var method = new Method(prefix + this._counter.ToString(CultureInfo.InvariantCulture), type, rhs, kind, null, hook);
    this._artificial[key] = method;
    this._artificialByName[method.Name] = method;
    this._methods.Add(method);
    return method.Name;
  }

  /// <summary>An item with its actions and guards, so that two groups that differ only in their actions are two rules.</summary>
  private static string Describe(GrammarItem item) => item switch {
    Rhs rhs => string.Join(" | ", rhs.Alts.Select(Describe)),
    Alt alt => string.Join(" ", alt.Guards) + " " + string.Join(" ", alt.Items.Select(Describe)) + (alt.Action is null ? "" : " { " + alt.Action + " }"),
    NamedItem named => (named.Name is null ? "" : named.Name + (named.Type is null ? "" : "[" + named.Type + "]") + "=") + Describe(named.Item),
    Group group => "(" + Describe(group.Rhs) + ")",
    Opt opt => "[" + Describe(opt.Node) + "]",
    Repeat repeat => "(" + Describe(repeat.Node) + ")" + (repeat.Minimum == 0 ? "*" : "+"),
    Gather gather => Describe(gather.Separator) + "." + Describe(gather.Node) + "+",
    Lookahead lookahead => (lookahead.Positive ? "&" : "!") + Describe(lookahead.Node),
    Forced forced => "&&" + Describe(forced.Node),
    Hook hook => hook.Name + "(" + Describe(hook.Node) + ")",
    _ => item.ToString()!
  };

  private string ChoiceCall(Rhs rhs) {
    if (rhs.CanBeInlined)
      return this.Call(rhs.Alts[0].Items[0].Item);

    var name = this.Artificial(ArtificialKind.Tmp, rhs, () => (rhs, this.ChoiceType(rhs)));
    return $"this.{name}()";
  }

  /// <summary>An expression for the value of an item: its result, <see langword="null"/> where it does not match.</summary>
  private string Call(GrammarItem item) {
    switch (item) {
      case NameLeaf leaf when this._grammar.Rules.Contains(leaf.Value):
        return $"this.{RuleMethod(leaf.Value)}()";
      case NameLeaf leaf when this._artificialByName.ContainsKey(leaf.Value):
        return $"this.{leaf.Value}()";
      case NameLeaf leaf:
        if (this._tokens.TryGetValue(leaf.Value, out var token)) {
          if (token.Method is not null)
            return $"this.{token.Method}()";

          return $"this.Expect({this._kindType}.{token.Kind})";
        }

        throw new GrammarException($"token '{leaf.Value}' at line {leaf.Line} is not in the @tokens table");
      case StringLeaf literal:
        if (this._literals.TryGetValue(literal.Text, out var kind))
          return $"this.Expect({this._kindType}.{kind})";

        switch (literal.Kind) {
          case LiteralKind.Keyword:
            this._keywords.Add(literal.Text);
            return $"this.ExpectKeyword({Quote(literal.Text)})";
          case LiteralKind.SoftKeyword:
            this._softKeywords.Add(literal.Text);
            return this._softKeyword is { } soft ? $"this.{soft.Method}({Quote(literal.Text)})" : $"this.ExpectSoftKeyword({Quote(literal.Text)})";
          default:
            return $"this.ExpectOperator({Quote(literal.Text)})";
        }
      case Group group:
        return this.ChoiceCall(group.Rhs);
      case Rhs rhs:
        return this.ChoiceCall(rhs);
      case Opt opt:
        return this.Call(opt.Node);
      case Repeat repeat: {
        var type = this.TypeOf(repeat.Node);
        var kindOf = repeat.Minimum == 0 ? ArtificialKind.Loop0 : ArtificialKind.Loop1;
        var name = this.Artificial(kindOf, repeat, () => (new Rhs([new Alt([new NamedItem(null, repeat.Node)], null, 0)]), $"List<{type}>"));
        return $"this.{name}()";
      }
      case Gather gather: {
        var type = this.TypeOf(gather.Node);
        var name = this.Artificial(ArtificialKind.Gather, gather, () => {
          var rest = this.Artificial(ArtificialKind.Loop0, new Group(new Rhs([new Alt([new NamedItem(null, gather.Separator), new NamedItem("elem", gather.Node)], "elem", 0)])),
            () => (new Rhs([new Alt([new NamedItem(null, gather.Separator), new NamedItem("elem", gather.Node)], "elem", 0)]), $"List<{type}>"));
          return (new Rhs([new Alt([new NamedItem("elem", gather.Node), new NamedItem("seq", new NameLeaf(rest, 0))], null, 0)]), $"List<{type}>");
        });
        return $"this.{name}()";
      }
      case Forced { Node: StringLeaf literal }:
        return $"this.Forced({this.Call(literal)}, {Quote(literal.Text)})";
      case Forced forced:
        return $"this.ForcedResult({this.Call(forced.Node)}, {Quote(((Group)forced.Node).Rhs.ToString())})";
      case Hook hook: {
        var name = this.Artificial(ArtificialKind.Hook, hook.Node, () => (new Rhs([new Alt([new NamedItem(null, hook.Node)], null, 0)]), this.TypeOf(hook.Node)), hook.Name);
        return $"this.{name}()";
      }
      default:
        throw new GrammarException($"'{item}' has no value");
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Methods

  private void EmitMethod(StringBuilder text, Method method) {
    var rule = method.Rule;
    text.Append(CultureInfo.InvariantCulture, $"\n  // {(rule is null ? method.Name + ": " + method.Rhs : rule.ToString())}\n");
    if (method.Kind == ArtificialKind.Hook) {
      var hook = GrammarText.Pascal(method.HookName!);
      text.Append(CultureInfo.InvariantCulture, $"  private {method.Type}? {method.Name}() {{\n");
      text.Append(CultureInfo.InvariantCulture, $"    this.Enter{hook}();\n");
      text.Append(CultureInfo.InvariantCulture, $"    var _res = {this.Call(method.Rhs.Alts[0].Items[0].Item)};\n");
      text.Append(CultureInfo.InvariantCulture, $"    this.Leave{hook}();\n");
      text.Append("    return _res;\n  }\n");
      return;
    }

    if (rule is { LeftRecursive: true, Leader: true }) {
      var id = this._ruleIds[rule.Name];
      text.Append(CultureInfo.InvariantCulture, $"  private {method.Type}? {method.Name}() {{\n");
      text.Append("    this.EnterRule();\n");
      text.Append(CultureInfo.InvariantCulture, $"    if (this.IsMemoized({id}, out {method.Type}? _res)) {{\n      this.LeaveRule();\n      return _res;\n    }}\n\n");
      text.Append("    var _mark = this.Mark;\n    var _resmark = this.Mark;\n    while (true) {\n");
      text.Append(CultureInfo.InvariantCulture, $"      this.InsertMemo({id}, _mark, _res);\n      this.Mark = _mark;\n      var _raw = this.{rule.Name}_raw();\n");
      text.Append("      if (_raw is null || this.Mark <= _resmark)\n        break;\n\n      _resmark = this.Mark;\n      _res = _raw;\n    }\n\n");
      text.Append("    this.Mark = _resmark;\n    this.LeaveRule();\n    return _res;\n  }\n\n");
      this.EmitBody(text, method with { Name = rule.Name + "_raw" }, false);
      return;
    }

    if (method.Kind is ArtificialKind.Loop0 or ArtificialKind.Loop1)
      this.EmitLoop(text, method);
    else
      this.EmitBody(text, method, rule is { Memo: true, LeftRecursive: false });
  }

  private void EmitBody(StringBuilder text, Method method, bool memo) {
    var withoutInvalid = method.Rule?.Name.EndsWith("without_invalid", StringComparison.Ordinal) == true;
    var cleanup = withoutInvalid ? "this.CallInvalidRules = _prevInvalid;\n" : "";
    text.Append(CultureInfo.InvariantCulture, $"  private {method.Type}? {method.Name}() {{\n");
    text.Append("    this.EnterRule();\n");
    if (withoutInvalid)
      text.Append("    var _prevInvalid = this.CallInvalidRules;\n    this.CallInvalidRules = false;\n");

    var id = method.Rule is null ? -1 : this._ruleIds[method.Rule.Name];
    if (memo)
      text.Append(CultureInfo.InvariantCulture, $"    if (this.IsMemoized({id}, out {method.Type}? _memo)) {{\n{Indent(cleanup, 6)}      this.LeaveRule();\n      return _memo;\n    }}\n\n");

    text.Append(CultureInfo.InvariantCulture, $"    {method.Type}? _res;\n");
    text.Append("    var _mark = this.Mark;\n");
    var jumps = false;
    foreach (var alt in method.Rhs.Alts)
      jumps |= this.EmitAlt(text, alt, method, cleanup);

    text.Append(jumps ? "    _res = null;\n  done:\n" : "    _res = null;\n");
    if (memo)
      text.Append(CultureInfo.InvariantCulture, $"    this.InsertMemo({id}, _mark, _res);\n");

    text.Append(Indent(cleanup, 4));
    text.Append("    this.LeaveRule();\n    return _res;\n  }\n");
  }

  private static string Indent(string lines, int spaces) => lines.Length == 0 ? "" : string.Concat(lines.Split('\n').Where(line => line.Length > 0).Select(line => new string(' ', spaces) + line + "\n"));

  /// <summary>Whether a rule can only fail or throw: every alternative's action is a <c>throw</c>.</summary>
  private static bool NeverSucceeds(GrammarRule rule) => rule.Rhs.Alts.All(alt => alt.Action?.StartsWith("throw ", StringComparison.Ordinal) == true);

  /// <summary>Emits one alternative; whether it jumps to the end of the rule, which an alternative that only throws does not.</summary>
  private bool EmitAlt(StringBuilder text, Alt alt, Method method, string cleanup) {
    var invalid = alt.IsInvalid;
    if (invalid && alt.Items[0].Item is NameLeaf { Value: var name } && this._grammar.Rules.TryGet(name, out var called) && NeverSucceeds(called)) {
      // A rule whose every alternative throws gives nothing but its error: it is called for that alone.
      text.Append(CultureInfo.InvariantCulture, $"    if (this.CallInvalidRules) // {alt}\n      this.{RuleMethod(name)}();\n\n");
      return false;
    }

    const string pad = "      ";
    text.Append(CultureInfo.InvariantCulture, $"    {(invalid ? "if (this.CallInvalidRules) " : "")}{{ // {alt.ToString().Replace('\n', ' ')}\n");
    var hasCut = alt.Items.Any(item => item.Item is Cut);
    if (hasCut)
      text.Append(CultureInfo.InvariantCulture, $"{pad}var _cut = false;\n");

    var (condition, value, post) = this.Alternative(alt, method);
    text.Append(CultureInfo.InvariantCulture, $"{pad}if ({condition}) {{\n");
    foreach (var line in post)
      text.Append(CultureInfo.InvariantCulture, $"{pad}  {line}\n");

    var throws = value.StartsWith("throw ", StringComparison.Ordinal);
    if (throws) {
      text.Append(CultureInfo.InvariantCulture, $"{pad}  {value};\n");
    } else {
      text.Append(CultureInfo.InvariantCulture, $"{pad}  _res = {value};\n");
      text.Append(CultureInfo.InvariantCulture, $"{pad}  goto done;\n");
    }

    text.Append(CultureInfo.InvariantCulture, $"{pad}}}\n\n{pad}this.Mark = _mark;\n");
    if (hasCut)
      text.Append(CultureInfo.InvariantCulture, $"{pad}if (_cut) {{\n{Indent(cleanup, pad.Length + 2)}{pad}  this.LeaveRule();\n{pad}  return null;\n{pad}}}\n");

    text.Append("    }\n\n");
    return !throws;
  }

  private void EmitLoop(StringBuilder text, Method method) {
    var alt = method.Rhs.Alts[0];
    var element = method.Type[5..^1];
    text.Append(CultureInfo.InvariantCulture, $"  private {method.Type}? {method.Name}() {{\n");
    text.Append("    this.EnterRule();\n    var _mark = this.Mark;\n");
    text.Append(CultureInfo.InvariantCulture, $"    var _children = new List<{element}>();\n");
    var (condition, value, _) = this.Alternative(alt, method);
    text.Append(CultureInfo.InvariantCulture, $"    while ({condition}) {{\n");

    text.Append(CultureInfo.InvariantCulture, $"      _children.Add({value}!);\n");
    text.Append("      if (this.Mark == _mark)\n        break;\n\n      _mark = this.Mark;\n    }\n\n    this.Mark = _mark;\n");
    if (method.Kind == ArtificialKind.Loop1)
      text.Append("    if (_children.Count == 0) {\n      this.LeaveRule();\n      return null;\n    }\n\n");

    text.Append("    this.LeaveRule();\n    return _children;\n  }\n");
  }

  /// <summary>The condition of an alternative, the value of its action and the statements of its guards with messages.</summary>
  private (string Condition, string Value, List<string> Post) Alternative(Alt alt, Method method) {
    var parts = new List<string>();
    var post = new List<string>();
    foreach (var guard in alt.Guards) {
      var check = $"this.Guard({Quote(guard.Axis)}, GuardOperator.{guard.Operator}, {Quote(guard.Value)})";
      if (guard.Message is null) {
        parts.Add(check);
        continue;
      }

      post.Add($"if (!{check})");
      post.Add($"  throw this.GuardFailed({Quote(guard.Message)});");
    }

    var values = ValueItems(alt).ToList();
    var needsDefault = alt.Action is null;
    var names = new List<string>();
    var used = new HashSet<string>(StringComparer.Ordinal);
    foreach (var item in alt.Items) {
      string? bind = item.Name;
      // pegen's dedupe: a name an alternative gives twice reads as name_1 the second time; the action reads the first.
      for (var n = 1; bind is not null && !used.Add(bind); n++)
        bind = $"{item.Name}_{n}";

      if (bind is null && needsDefault && values.Count == 1 && values.Contains(item) && item.Item is not Cut)
        bind = $"_v{++this._variables}";

      if (bind is not null && item.Item is not Cut)
        names.Add(bind);

      parts.Add(this.Condition(item, bind));
    }

    string value;
    if (alt.Action is { } action) {
      value = action;
    } else if (method.Kind == ArtificialKind.Gather) {
      value = "Gathered(elem, seq)";
    } else if (values.Count == 1 && values[0].Item is not Cut) {
      value = names[^1];
      var type = this.TypeOf(values[0]);
      var target = method.Kind is ArtificialKind.Loop0 or ArtificialKind.Loop1 ? method.Type[5..^1] : method.Type;
      if (type == "object" && target != "object")
        value = $"({target}?){value}";
    } else {
      value = this._dummy.Expression;
      var target = method.Kind is ArtificialKind.Loop0 or ArtificialKind.Loop1 ? method.Type[5..^1] : method.Type;
      if (target != "object" && target != this._dummy.Type)
        value = $"({target}?)(object){value}";
    }

    return (string.Join("\n        && ", parts), value, post);
  }

  private string Condition(NamedItem named, string? bind) {
    var item = named.Item;
    switch (item) {
      case Lookahead lookahead:
        return $"{(lookahead.Positive ? "" : "!")}this.EndLookahead(this.BeginLookahead(), {this.Call(lookahead.Node)} is not null)";
      case Predicate predicate:
        return $"this.{predicate.Name}()";
      case Cut:
        return "(_cut = true)";
      case Opt opt:
        if (bind is null)
          return $"Optional({this.Call(opt.Node)})";

        return named.Type is null ? $"{this.Call(opt.Node)} is var {bind}" : $"({named.Type}?)({this.Call(opt.Node)}) is var {bind}";
      default:
        var call = this.Call(item);
        if (bind is null)
          return $"{call} is not null";

        return named.Type is null ? $"{call} is {{ }} {bind}" : $"{call} is {named.Type} {bind}";
    }
  }

}
