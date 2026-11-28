using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>String literals, f-strings and t-strings, and their concatenation.</summary>
internal sealed partial class PyParser {

  private sealed record WithMetadata(PyNode Result, string? Metadata);

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

  private PyNode? Strings() => this.Memo(MemoStrings, () => {
    if (this._invalid)
      this.Try(this.InvalidStringTStringConcat);

    var start = this.Peek();
    var plain = this.Try(() => this.Repeat(() => this.Try(this.FString) ?? this.StringToken(), 1) is { } parts ? this.ConcatenateStrings(parts, this.Extra(start)) : null);
    if (plain is not null)
      return plain;

    return this.Try(() => this.Repeat(this.TString, 1) is { } parts ? PyAst.Make("TemplateStr", this.Extra(start), Nullable(this.ConcatenatedValues(parts))) : null);
  });

  private PyNode? StringToken() => this.Rule(() => {
    if (this.Expect(PyTokenKind.String) is not { } token)
      return null;

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
  });

  private PyNode? FString() => this.Rule(() => {
    if (this.Expect(PyTokenKind.FStringStart) is not { } start)
      return null;

    var parts = this.Repeat(this.FStringMiddle, 0)!;
    if (this.Expect(PyTokenKind.FStringEnd) is not { } end)
      return null;

    return PyAst.Make("JoinedStr", this.Span(start, end), Nullable(this.ResizedExpressions(start, parts)));
  });

  private PyNode? TString() => this.Memo(MemoTString, () => {
    if (this.Expect(PyTokenKind.TStringStart) is not { } start)
      return null;

    var parts = this.Repeat(this.TStringMiddle, 0)!;
    if (this.Expect(PyTokenKind.TStringEnd) is not { } end)
      return null;

    return this.Version(14, "t-strings are", PyAst.Make("TemplateStr", this.Span(start, end), Nullable(this.ResizedExpressions(start, parts))));
  });

  private PyNode? FStringMiddle() => this.Rule(() => {
    if (this.Try(() => this.ReplacementField(false, false)) is { } field)
      return field;

    return this.Expect(PyTokenKind.FStringMiddle) is { } token ? PyAst.Make("Constant", this.Span(token, token), token.Text, null) : null;
  });

  private PyNode? TStringMiddle() => this.Rule(() => {
    if (this.Try(() => this.ReplacementField(true, false)) is { } field)
      return field;

    return this.Expect(PyTokenKind.TStringMiddle) is { } token ? PyAst.Make("Constant", this.Span(token, token), token.Text, null) : null;
  });

  /// <summary>
  /// <c>'{' annotated_rhs '='? [conversion] [format spec] '}'</c>: an <c>Interpolation</c> of a t-string,
  /// a <c>FormattedValue</c> of an f-string or of the format spec of a t-string; with <c>=</c>, after its text.
  /// </summary>
  private PyNode? ReplacementField(bool template, bool inTemplateSpec) {
    var start = this.Peek();
    var field = this.Try(() => {
      if (this.Expect("{") is null || this.AnnotatedRhs() is not { } expression)
        return null;

      var debug = this.Expect("=");
      var conversion = this.Try(this.FStringConversion);
      var format = this.Try(() => this.FullFormatSpec(template || inTemplateSpec));
      if (this.Expect("}") is not { } close)
        return null;

      return this.FormattedValue(expression, debug, conversion, format, close, this.Extra(start), template && !inTemplateSpec);
    });
    if (field is not null)
      return field;

    return this._invalid ? this.InvalidReplacementField(template || inTemplateSpec) : null;
  }

  private WithMetadata? FStringConversion() => this.Rule(() => {
    if (this.Expect("!") is not { } bang || this.NameToken() is not { } conversion)
      return null;

    var prefix = this._lexer.StringPrefix;
    if (bang.Line != conversion.Range!.Line || bang.EndColumn != conversion.Range.Offset)
      throw this.ErrorAt(bang, conversion, $"{prefix}-string: conversion type must come right after the exclamation mark");

    var id = conversion.Text("id")!;
    if (id.Length > 1 || id is not ("s" or "r" or "a"))
      throw this.ErrorAt(conversion, $"{prefix}-string: invalid conversion character {Repr(id)}: expected 's', 'r', or 'a'");

    return new WithMetadata(conversion, bang.Metadata);
  });

  private static string Repr(string text) => "'" + text.Replace("\\", "\\\\", System.StringComparison.Ordinal).Replace("'", "\\'", System.StringComparison.Ordinal) + "'";

  private WithMetadata? FullFormatSpec(bool template) => this.Rule(() => {
    var start = this.Peek();
    if (this.Expect(":") is not { } colon)
      return null;

    var spec = this.Repeat(() => this.Try(() => this.FormatSpecPart(template)), 0)!;
    var parts = spec.Where(part => !IsEmptyText(part)).ToList();
    var range = this.Extra(start);
    var result = parts.Count == 0 || (parts.Count == 1 && parts[0].Is("Constant"))
      ? PyAst.Make("JoinedStr", range, Nullable(parts))
      : this.ConcatenateStrings(parts, range);
    return new WithMetadata(result, colon.Metadata);
  });

  private PyNode? FormatSpecPart(bool template) {
    if (this.Expect(template ? PyTokenKind.TStringMiddle : PyTokenKind.FStringMiddle) is { } token) {
      try {
        return Text(this.Span(token, token), PyLiterals.Text(token.Text, this._lexer.InsideRawString), null);
      } catch (PyLiterals.LiteralException exception) {
        // A format spec is decoded by the action itself, which leaves the codec's own exception standing
        // where the other parts of a string raise a SyntaxError: "(unicode error) " and then its message.
        throw this.Error(exception.Message["(unicode error) ".Length..], type: "UnicodeDecodeError");
      }
    }

    return this.ReplacementField(false, template);
  }

  /// <summary><c>_PyPegen_formatted_value</c> and <c>_PyPegen_interpolation</c>.</summary>
  private PyNode FormattedValue(PyNode expression, PyToken? debug, WithMetadata? conversion, WithMetadata? format, PyToken close, PyRange range, bool interpolation) {
    var conversionValue = conversion is not null ? (int)conversion.Result.Text("id")![0] : debug is not null && format is null ? 'r' : -1;
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

    return PyAst.Make("JoinedStr", range, Nullable(this.ConcatenatedValues(strings)));
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
