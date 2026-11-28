using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The helpers the actions of <c>python.pgram</c> call: those of CPython's
/// <c>Parser/action_helpers.c</c> and <c>Parser/pegen.c</c> over the frontend's tree, with the
/// intermediate results of the grammar's rules as records.
/// </summary>
internal sealed partial class PyParser {

  /// <summary>A parameter with its default (<c>NameDefaultPair</c>).</summary>
  internal sealed record NameDefault(PyNode Arg, PyNode? Value);

  /// <summary>The positional-only parameters with defaults (<c>SlashWithDefault</c>).</summary>
  internal sealed record SlashDefaults(List<PyNode> PlainNames, List<NameDefault> NamesWithDefaults);

  /// <summary>What follows <c>*</c> (<c>StarEtc</c>).</summary>
  internal sealed record StarParameters(PyNode? VarArg, List<NameDefault>? KwOnly, PyNode? KwArg);

  /// <summary>A key and value of a dict display; the key is <see langword="null"/> for <c>**</c> (<c>KeyValuePair</c>).</summary>
  internal sealed record KeyValue(PyNode? Key, PyNode Value);

  /// <summary>A keyword argument or a starred one (<c>KeywordOrStarred</c>).</summary>
  internal sealed record KeywordOrStarred(PyNode Element, bool IsKeyword);

  /// <summary>A comparison operator and its right operand (<c>CmpopExprPair</c>).</summary>
  internal sealed record CompareOp(string Op, PyNode Operand);

  /// <summary>A key or keyword of a pattern with its pattern (<c>KeyPatternPair</c>).</summary>
  internal sealed record KeyPattern(PyNode Key, PyNode Pattern);

  /// <summary>A result with the source text the tokenizer kept for it (<c>ResultTokenWithMetadata</c>).</summary>
  internal sealed record WithMetadata(PyNode Result, string? Metadata);

  // ---------------------------------------------------------------------------------------------
  // Sequences, names and operators

  /// <summary>A sequence for a field of a node; an absent one is empty.</summary>
  private static List<PyNode?> Seq(List<PyNode>? items) => items is null ? [] : [.. items];

  /// <summary><c>_PyPegen_seq_insert_in_front</c>, for a field of a node.</summary>
  private static List<PyNode?> Insert(PyNode first, List<PyNode>? rest) => [first, .. rest ?? []];

  /// <summary>The identifier of a <c>Name</c>.</summary>
  private static string Id(PyNode name) => name.Text("id")!;

  /// <summary>The node of a context, an operator or a comparison.</summary>
  private static PyNode Op(string kind) => PyAst.Singleton(kind);

  /// <summary>pegen's dummy name, what an alternative of several items without action gives.</summary>
  private static PyNode DummyName { get; } = PyAst.Make("Name", null, "", PyAst.Singleton("Load"));

  /// <summary><c>_PyPegen_seq_count_dots</c>: a <c>.</c> counts one, a <c>...</c> three.</summary>
  private static int CountDots(List<PyToken> dots) => dots.Sum(dot => dot.Text == "..." ? 3 : 1);

  /// <summary><c>_PyPegen_check_legacy_stmt</c>: a name that was a statement in Python 2.</summary>
  private static bool IsLegacyStatement(PyNode name) => name.Is("Name") && Id(name) is "print" or "exec";

  /// <summary><c>_PyPegen_tokens_are_adjacent</c>.</summary>
  private static bool TokensAreAdjacent(PyToken first, PyToken second) => first.EndLine == second.Line && first.EndColumn == second.Column;

  // ---------------------------------------------------------------------------------------------
  // Statements and parameters

  /// <summary><c>_PyPegen_checked_future_import</c>: <c>from __future__ import barry_as_FLUFL</c> turns the tokenizer's <c>&lt;&gt;</c> on.</summary>
  private PyNode CheckedFutureImport(string module, List<PyNode> names, int dots, PyRange range) {
    if (dots == 0 && module == "__future__" && names.Any(alias => alias.Text("name") == "barry_as_FLUFL")) {
      this._barry = true;
      this._lexer.BarryAsBdfl = true;
    }

    return PyAst.Make("ImportFrom", range, module, Seq(names), dots);
  }

  /// <summary><c>_PyPegen_check_barry_as_flufl</c>: whether a <c>!=</c> is not the inequality the future import allows.</summary>
  private bool CheckBarryAsFlufl(PyToken token) {
    if (this._barry && token.Source != "<>")
      throw this.Error("with Barry as BDFL, use '<>' instead of '!='");

    return !this._barry && token.Source != "!=";
  }

  /// <summary><c>_PyPegen_empty_arguments</c>.</summary>
  private static PyNode EmptyArguments()
    => PyAst.Make("arguments", null, new List<PyNode?>(), new List<PyNode?>(), null, new List<PyNode?>(), new List<PyNode?>(), null, new List<PyNode?>());

  /// <summary><c>_PyPegen_make_arguments</c>.</summary>
  private static PyNode MakeArguments(List<PyNode>? slashWithout, SlashDefaults? slashWith, List<PyNode>? plain, List<NameDefault>? withDefault, StarParameters? star) {
    var positionalOnly = slashWithout ?? (slashWith is null ? [] : [.. slashWith.PlainNames, .. slashWith.NamesWithDefaults.Select(pair => pair.Arg)]);
    List<PyNode> positional = [.. plain ?? [], .. (withDefault ?? []).Select(pair => pair.Arg)];
    List<PyNode?> defaults = [.. (slashWith?.NamesWithDefaults ?? []).Select(pair => pair.Value), .. (withDefault ?? []).Select(pair => pair.Value)];
    var keywordOnly = star?.KwOnly ?? [];
    return PyAst.Make("arguments", null, Seq(positionalOnly), Seq(positional), star?.VarArg, keywordOnly.Select(pair => (PyNode?)pair.Arg).ToList(),
      keywordOnly.Select(pair => pair.Value).ToList(), star?.KwArg, defaults);
  }

  /// <summary><c>_PyPegen_collect_call_seqs</c>: the positional arguments, the starred keywords among them, and the keywords.</summary>
  private PyNode CollectCall(PyRange range, List<PyNode> arguments, List<KeywordOrStarred>? keywords) {
    var args = Seq(arguments);
    args.AddRange(keywords?.Where(item => !item.IsKeyword).Select(item => (PyNode?)item.Element) ?? []);
    var keywordNodes = keywords?.Where(item => item.IsKeyword).Select(item => (PyNode?)item.Element).ToList() ?? [];
    return PyAst.Make("Call", range, DummyName, args, keywordNodes);
  }

  // ---------------------------------------------------------------------------------------------
  // Patterns

  /// <summary><c>_PyPegen_ensure_real</c>.</summary>
  private PyNode EnsureReal(PyNode number) => number.Value is PyComplex ? throw this.ErrorAt(number, "real number required in complex literal") : number;

  /// <summary><c>_PyPegen_ensure_imaginary</c>.</summary>
  private PyNode EnsureImaginary(PyNode number) => number.Value is PyComplex ? number : throw this.ErrorAt(number, "imaginary number required in complex literal");

  /// <summary><c>_PyPegen_get_pattern_keys</c>.</summary>
  private static List<PyNode?> PatternKeys(List<KeyPattern> items) => [.. items.Select(item => item.Key)];

  /// <summary><c>_PyPegen_get_patterns</c>.</summary>
  private static List<PyNode?> Patterns(List<KeyPattern> items) => [.. items.Select(item => item.Pattern)];

  /// <summary>A <c>MatchClass</c> of its positional and keyword patterns.</summary>
  private static PyNode ClassPattern(PyRange range, PyNode cls, List<PyNode>? positional, List<KeyPattern>? keywords)
    => PyAst.Make("MatchClass", range, cls, Seq(positional), (keywords ?? []).Select(keyword => Id(keyword.Key)).ToList(), Patterns(keywords ?? []));

  // ---------------------------------------------------------------------------------------------
  // Errors of the invalid rules

  private PyError? TargetError(PyAst.Targets targets, PyNode expression)
    => PyAst.InvalidTarget(expression, targets) is { } invalid
      ? this.ErrorAt(invalid, $"{(targets == PyAst.Targets.Delete ? "cannot delete" : "cannot assign to")} {PyAst.Name(invalid)}")
      : null;

  /// <summary><c>RAISE_SYNTAX_ERROR_INVALID_TARGET</c>: the error of the part that cannot be a target, or nothing where every part can.</summary>
  private T? RaiseInvalidTarget<T>(PyAst.Targets targets, PyNode expression) where T : class
    => this.TargetError(targets, expression) is { } error ? throw error : null;

  /// <summary><c>_PyPegen_get_last_comprehension_item</c>.</summary>
  private static PyNode LastComprehensionItem(PyNode comprehension)
    => comprehension.Nodes("ifs") is { Count: > 0 } ifs ? ifs[^1] : comprehension.Node("iter")!;

  /// <summary><c>_PyPegen_nonparen_genexp_in_call</c>: nothing where the call has at most one argument.</summary>
  private PyNode? NonParenthesizedGenexpInCall(PyNode call, List<PyNode> clauses) {
    var arguments = call.Nodes("args");
    if (arguments.Count <= 1)
      return null;

    throw this.ErrorAt(arguments[^1], LastComprehensionItem(clauses[^1]), "Generator expression must be parenthesized");
  }

  /// <summary><c>_PyPegen_arguments_parsing_error</c>.</summary>
  private PyError ArgumentsParsingError(PyNode call)
    => this.Error(call.Nodes("keywords").Any(keyword => keyword.Text("arg") is null)
      ? "positional argument follows keyword argument unpacking"
      : "positional argument follows keyword argument");

  // ---------------------------------------------------------------------------------------------
  // Strings, f-strings and t-strings

  /// <summary>A str constant of code points; a surrogate among them is kept as a code point of its own.</summary>
  private static PyNode Text(PyRange? range, IReadOnlyList<int> points, string? kind) {
    var text = PyUnicode.Text(points);
    if (!points.Any(point => point is >= 0xD800 and <= 0xDFFF))
      return PyAst.Make("Constant", range, text, kind);

    var pair = false;
    for (var i = 0; i + 1 < points.Count; i++)
      pair |= points[i] is >= 0xD800 and <= 0xDBFF && points[i + 1] is >= 0xDC00 and <= 0xDFFF;

    return new PyNode("Constant", range, [("value", text), ("kind", kind), ("#codePoints", points.ToList()), ("#surrogatePair", pair)]);
  }

  /// <summary>The code points of a str constant.</summary>
  private static List<int> PointsOf(PyNode constant)
    => constant.Get("#codePoints") as List<int> ?? PyUnicode.CodePoints((string)constant.Value!);

  private static bool IsEmptyText(PyNode node) => node.Is("Constant") && node.Value is string { Length: 0 };

  /// <summary><c>_PyPegen_constant_from_string</c>: a string or bytes literal decoded.</summary>
  private PyNode ConstantFromString(PyToken token) {
    object value;
    try {
      value = PyLiterals.String(token.Text, out var nonAscii);
      if (nonAscii)
        throw this.ErrorAt(token, token, "bytes can only contain ASCII literal characters");
    } catch (PyLiterals.LiteralException exception) {
      throw this.Error(exception.Message);
    }

    var kind = token.Text[0] == 'u' ? "u" : null;
    return value is byte[] bytes ? PyAst.Make("Constant", this.Span(token, token), bytes, kind) : Text(this.Span(token, token), (List<int>)value, kind);
  }

  /// <summary><c>_PyPegen_constant_from_token</c>: the literal part of an f-string or t-string as written, decoded later.</summary>
  private PyNode ConstantFromToken(PyToken token) => PyAst.Make("Constant", this.Span(token, token), token.Text, null);

  /// <summary><c>_PyPegen_decoded_constant_from_token</c>: a literal part of a format spec, decoded.</summary>
  private PyNode DecodedConstantFromToken(PyToken token) {
    try {
      return Text(this.Span(token, token), PyLiterals.Text(token.Text, this._lexer.InsideRawString), null);
    } catch (PyLiterals.LiteralException exception) {
      // A format spec is decoded by the action itself, which leaves the codec's own exception standing
      // where the other parts of a string raise a SyntaxError: "(unicode error) " and then its message.
      throw this.Error(exception.Message["(unicode error) ".Length..], type: "UnicodeDecodeError");
    }
  }

  /// <summary><c>_PyPegen_check_fstring_conversion</c>.</summary>
  private WithMetadata CheckFStringConversion(PyToken bang, PyNode conversion) {
    var prefix = this._lexer.StringPrefix;
    if (bang.Line != conversion.Range!.Line || bang.EndColumn != conversion.Range.Offset)
      throw this.ErrorAt(bang, conversion, $"{prefix}-string: conversion type must come right after the exclamation mark");

    var id = Id(conversion);
    if (id.Length > 1 || id is not ("s" or "r" or "a"))
      throw this.ErrorAt(conversion, $"{prefix}-string: invalid conversion character {Repr(id)}: expected 's', 'r', or 'a'");

    return new WithMetadata(conversion, bang.Metadata);
  }

  private static string Repr(string text) => "'" + text.Replace("\\", "\\\\", System.StringComparison.Ordinal).Replace("'", "\\'", System.StringComparison.Ordinal) + "'";

  /// <summary><c>_PyPegen_setup_full_format_spec</c>.</summary>
  private WithMetadata SetupFullFormatSpec(PyToken colon, List<PyNode> spec, PyRange range) {
    var parts = spec.Where(part => !IsEmptyText(part)).ToList();
    var result = parts.Count == 0 || (parts.Count == 1 && parts[0].Is("Constant"))
      ? PyAst.Make("JoinedStr", range, Seq(parts))
      : this.ConcatenateStrings(parts, range);
    return new WithMetadata(result, colon.Metadata);
  }

  /// <summary><c>_PyPegen_formatted_value</c> and <c>_PyPegen_interpolation</c>.</summary>
  private PyNode FormattedValue(PyNode expression, PyToken? debug, WithMetadata? conversion, WithMetadata? format, PyToken close, PyRange range, bool interpolation) {
    var conversionValue = conversion is not null ? (int)Id(conversion.Result)[0] : debug is not null && format is null ? 'r' : -1;
    int debugEndLine, debugEndOffset;
    string? metadata;
    if (conversion is not null) {
      debugEndLine = conversion.Result.Range!.Line;
      debugEndOffset = conversion.Result.Range.Offset;
      metadata = conversion.Metadata;
    } else if (format is not null) {
      debugEndLine = format.Result.Range!.Line;
      debugEndOffset = format.Result.Range.Offset + 1;
      metadata = format.Metadata;
    } else {
      debugEndLine = range.EndLine;
      debugEndOffset = range.EndOffset;
      metadata = close.Metadata;
    }

    var value = interpolation
      ? PyAst.Make("Interpolation", range, expression, debug is not null ? StripDebug(metadata ?? "") : metadata ?? "", conversionValue, format?.Result)
      : PyAst.Make("FormattedValue", range, expression, conversionValue, format?.Result);
    if (debug is null)
      return value;

    var text = Text(this.Range(range.Line, range.Offset + 1, debugEndLine, debugEndOffset - 1), PyUnicode.CodePoints(metadata ?? ""), null);
    return PyAst.Make("JoinedStr", this.Range(range.Line, range.Offset, debugEndLine, debugEndOffset), new List<PyNode?> { text, value });
  }

  /// <summary>The text of an interpolation without the <c>=</c> of a debug expression and what follows it (<c>_strip_interpolation_debug_expr</c>).</summary>
  private static string StripDebug(string text) {
    var length = text.Length;
    while (length > 0) {
      var newline = false;
      while (length > 0 && char.IsWhiteSpace(text[length - 1])) {
        newline |= text[length - 1] is '\r' or '\n';
        length--;
      }

      if (!newline || length == 0 || text[length - 1] != '\\')
        break;

      length--;
    }

    return length == 0 || text[length - 1] != '=' ? text : text[..(length - 1)];
  }

  /// <summary>The values of an f-string or t-string: the literal parts decoded, the empty ones dropped, debug texts spliced in (<c>_get_resized_exprs</c>).</summary>
  private List<PyNode> ResizedExpressions(PyToken start, List<PyNode> parts) {
    var raw = start.Text.Contains('r', System.StringComparison.Ordinal) || start.Text.Contains('R', System.StringComparison.Ordinal);
    var values = new List<PyNode>();
    foreach (var item in parts) {
      if (item.Is("JoinedStr")) {
        values.AddRange(item.Nodes("values"));
        continue;
      }

      if (item.Is("Constant")) {
        var text = (string)item.Value!;
        if (text is "{{" or "}}")
          text = text[..1];

        List<int> decoded;
        try {
          decoded = PyLiterals.Text(text, raw);
        } catch (PyLiterals.LiteralException exception) {
          throw this.Error(exception.Message);
        }

        if (decoded.Count == 0)
          continue;

        values.Add(Text(item.Range, decoded, null));
        continue;
      }

      values.Add(item);
    }

    return values;
  }

  /// <summary><c>_PyPegen_concatenate_strings</c>: bytes with bytes, strs with strs, f-strings flattened into one <c>JoinedStr</c>.</summary>
  private PyNode ConcatenateStrings(List<PyNode> strings, PyRange range) {
    var fString = false;
    var text = false;
    var bytes = false;
    foreach (var element in strings) {
      if (element.Is("Constant")) {
        if (element.Value is byte[])
          bytes = true;
        else
          text = true;
      } else {
        fString = true;
      }
    }

    if ((text || fString) && bytes)
      throw this.Error("cannot mix bytes and nonbytes literals");

    if (!fString) {
      if (strings.Count == 1)
        return strings[0];

      if (bytes)
        return PyAst.Make("Constant", range, strings.SelectMany(element => (byte[])element.Value!).ToArray(), strings[0].Get("kind"));

      return Text(range, strings.SelectMany(PointsOf).ToList(), (string?)strings[0].Get("kind"));
    }

    return PyAst.Make("JoinedStr", range, Seq(this.ConcatenatedValues(strings)));
  }

  /// <summary>The values of concatenated f-strings or t-strings: flattened, adjacent constants folded, empty ones dropped (<c>_build_concatenated_str</c>).</summary>
  private List<PyNode> ConcatenatedValues(List<PyNode> strings) {
    var flattened = new List<PyNode>();
    foreach (var element in strings) {
      if (element.Kind is "JoinedStr" or "TemplateStr")
        flattened.AddRange(element.Nodes("values"));
      else
        flattened.Add(element);
    }

    var values = new List<PyNode>();
    for (var i = 0; i < flattened.Count; i++) {
      var element = flattened[i];
      if (element.Is("Constant")) {
        if (i + 1 < flattened.Count && flattened[i + 1].Is("Constant")) {
          var first = element;
          var last = element;
          var points = new List<int>();
          var j = i;
          for (; j < flattened.Count && flattened[j].Is("Constant"); j++) {
            points.AddRange(PointsOf(flattened[j]));
            last = flattened[j];
          }

          i = j - 1;
          element = Text(this.Range(first.Range!.Line, first.Range.Offset, last.Range!.EndLine, last.Range.EndOffset), points, (string?)first.Get("kind"));
        }

        if (IsEmptyText(element))
          continue;
      }

      values.Add(element);
    }

    return values;
  }

}
