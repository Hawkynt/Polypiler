using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Text: <c>String</c>, <c>&amp;str</c> and <c>char</c>, their methods and associated functions,
/// the operators Rust defines on them, and <c>format!</c>. Byte lengths and byte offsets are UTF-8's,
/// as Rust's are; the lowering computes them from the IR's UTF-16 strings.
/// </summary>
internal sealed partial class TypeChecker {

  /// <summary>
  /// The operations of the IR's strings the frontend's own helpers are written with: their source
  /// uses them as functions, which no other source can name.
  /// </summary>
  private static readonly Dictionary<string, (RType[] Parameters, RType Result)> _intrinsics = new() {
    ["__units"] = ([RType.StrRef], RType.Usize),
    ["__unit"] = ([RType.StrRef, RType.Usize], RType.U32),
    ["__substring"] = ([RType.StrRef, RType.Usize, RType.Usize], RType.String),
    ["__from_unit"] = ([RType.U32], RType.String),
    ["__index_of"] = ([RType.StrRef, RType.StrRef, RType.Usize], RType.I64)
  };

  /// <summary>The ASCII predicates and conversions of <c>char</c>.</summary>
  private static readonly HashSet<string> _asciiPredicates = [
    "is_ascii", "is_ascii_digit", "is_ascii_alphabetic", "is_ascii_alphanumeric", "is_ascii_uppercase", "is_ascii_lowercase",
    "is_ascii_whitespace", "is_ascii_punctuation", "is_ascii_hexdigit", "is_ascii_graphic", "is_ascii_control", "is_whitespace"
  ];

  /// <summary>Whether the source is one of the frontend's helpers, which may name <see cref="_intrinsics"/>.</summary>
  public bool Internal { get; init; }

  private RType? Intrinsic(CallExpr call, string name) {
    if (!this.Internal || !_intrinsics.TryGetValue(name, out var intrinsic))
      return null;

    this._typed.Resolutions[call] = name;
    this.Arguments(call, intrinsic.Parameters, "function");
    return intrinsic.Result;
  }

  // ---------------------------------------------------------------- operators

  /// <summary>
  /// <c>String + &amp;str</c>, and the comparisons of text: <c>==</c> between any two of <c>String</c>,
  /// <c>str</c> and their references, the order between two of the same type. <see langword="null"/>
  /// for operands that are no text.
  /// </summary>
  private RType? TextOperator(BinaryExpr binary, RType left, RType right) {
    if (!left.IsText && !right.IsText)
      return null;

    if (binary.Op == "+") {
      if (left.Resolved.Kind != RKind.String)
        throw this.Invalid($"E0369: cannot add `{right}` to `{left}`", binary.At);

      this.Coerce(RType.StrRef, right, binary.Right.At);
      return RType.String;
    }

    if (!_comparisons.Contains(binary.Op) || !left.IsText || !right.IsText)
      throw this.Invalid($"E0369: binary operation `{binary.Op}` cannot be applied to type `{left}`", binary.At);

    if (binary.Op is not ("==" or "!=") && !SameText(left, right))
      throw this.Invalid($"E0308: mismatched types: expected `{left}`, found `{right}`", binary.Right.At);

    return RType.Bool;
  }

  /// <summary>Whether two text types are the same type, which the order needs.</summary>
  private static bool SameText(RType left, RType right) {
    var (a, b) = (left.Resolved, right.Resolved);
    while (a.Kind == RKind.Reference && b.Kind == RKind.Reference) {
      a = a.Element!.Resolved;
      b = b.Element!.Resolved;
    }

    return a.Kind == b.Kind;
  }

  // ---------------------------------------------------------------- associated functions

  /// <summary><c>String::new</c>, <c>String::from</c>, <c>String::with_capacity</c>, <c>char::from_u32</c>, <c>char::from_digit</c>, <c>char::from</c>.</summary>
  private RType? TextCall(CallExpr call, PathExpr path) {
    var owner = path.Segments[^2];
    var name = path.Segments[^1];
    var prefix = path.Segments.Take(path.Segments.Count - 2).ToList();
    if (owner == "String" && (prefix.Count == 0 || prefix is ["std" or "alloc", "string"]) && name is "new" or "from" or "with_capacity") {
      this._typed.Resolutions[call] = "String::" + name;
      this.ArgumentCount(call, name == "new" ? 0 : 1);
      if (name == "with_capacity")
        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);

      if (name == "from") {
        var value = this.Value(call.Arguments[0]);
        this._deferred.Add(() => {
          if (!value.IsText && value.Resolved.Kind != RKind.Char && !this.IsError(value))
            throw this.Invalid($"E0277: the trait bound `String: From<{value}>` is not satisfied", call.At);
        });
      }

      return RType.String;
    }

    // char::from_u32 is an associated function of char and a function of the module std::char.
    if (owner == "char" && (prefix.Count == 0 || prefix is ["std" or "core"] && name != "from") && name is "from_u32" or "from_digit" or "from") {
      this._typed.Resolutions[call] = "char::" + name;
      this.ArgumentCount(call, name == "from_digit" ? 2 : 1);
      this.Unify(name == "from" ? RType.U8 : RType.U32, this.Value(call.Arguments[0], name == "from" ? RType.U8 : RType.U32), call.Arguments[0].At);
      if (name == "from_digit")
        this.Unify(RType.U32, this.Value(call.Arguments[1], RType.U32), call.Arguments[1].At);

      return name == "from" ? RType.Char : RType.Option(RType.Char);
    }

    return null;
  }

  private void ArgumentCount(CallExpr call, int count) {
    if (call.Arguments.Count != count)
      throw this.Invalid($"E0061: this function takes {count} argument{(count == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);
  }

  // ---------------------------------------------------------------- methods

  private RType TextMethod(MethodCallExpr call, RType receiver, RType target, RType? expected) {
    var name = call.Name;
    this._typed.Methods[call] = "str::" + name;
    switch (name) {
      case "len":
        this.Count(call, 0);
        return RType.Usize;
      case "is_empty":
        this.Count(call, 0);
        return RType.Bool;
      case "as_str" when target.Kind == RKind.String:
        this.Count(call, 0);
        return RType.StrRef;
      case "to_string" or "to_owned" or "to_ascii_uppercase" or "to_ascii_lowercase":
        this.Count(call, 0);
        return RType.String;
      case "clone":
        this.Count(call, 0);
        return target.Kind == RKind.String ? RType.String : RType.StrRef;
      case "as_bytes":
        this.Count(call, 0);
        return RType.Reference(RType.Slice(RType.U8), false);
      case "trim" or "trim_start" or "trim_end":
        this.Count(call, 0);
        return RType.StrRef;
      case "repeat":
        this.Count(call, 1);
        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);
        return RType.String;
      case "contains" or "starts_with" or "ends_with" or "find":
        this.Count(call, 1);
        this.TextPattern(call.Arguments[0], false);
        return name == "find" ? RType.Option(RType.Usize) : RType.Bool;
      case "push_str" or "push" or "clear" when target.Kind == RKind.String:
        this.Count(call, name == "clear" ? 0 : 1);
        if (name == "push_str")
          this.Coerce(RType.StrRef, this.Value(call.Arguments[0], RType.StrRef), call.Arguments[0].At);
        else if (name == "push")
          this.Unify(RType.Char, this.Value(call.Arguments[0], RType.Char), call.Arguments[0].At);

        this.RequireMutable(call, receiver);
        return RType.Unit;
      case "chars" or "bytes" or "split": {
        this.Count(call, name == "split" ? 1 : 0);
        if (name == "split")
          this.TextPattern(call.Arguments[0], true);

        var item = name switch { "chars" => RType.Char, "bytes" => RType.U8, _ => RType.StrRef };
        var source = new ElementSource(call.Receiver, item, ElementAccess.Value) { TextMethod = name, Separator = call.Arguments.FirstOrDefault() };
        return RType.Iterator(new IteratorInfo(SourceKind.Text, null, source, [], item));
      }
      case "parse": {
        this.Count(call, 0);
        RType value;
        if (call.Generics.Count == 1)
          value = this.Resolve(call.Generics[0], false) ?? this.ErrorType();
        else
          value = expected?.Resolved is { Kind: RKind.Result } wanted ? wanted.Element! : this.Fresh(VariableKind.Any);

        this._deferred.Add(() => {
          if (value.Resolved.Kind != RKind.Integer && !this.IsError(value))
            this.Refuse(new Refusal(RustFrontend.Unsupported, $"parse into '{value}'", call.At));
        });
        // The error is a ParseIntError, which the subset knows no more of than that it is one.
        return RType.Result(value, RType.Unit);
      }
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    this._typed.Methods.Remove(call);
    return this.Refused(RustFrontend.Unsupported, $"str::{name}", call.At);
  }

  /// <summary>The pattern of a text search: a <c>&amp;str</c> or a <c>char</c>; <c>split</c> refuses an empty one, which splits between every char.</summary>
  private void TextPattern(Expr pattern, bool nonEmpty) {
    var type = this.Value(pattern);
    this._deferred.Add(() => {
      if (this.IsError(type))
        return;

      if (type.Resolved.Kind != RKind.Char && !type.IsText)
        this.Refuse(new Refusal(RustFrontend.Unsupported, $"text pattern of type '{type}'", pattern.At));
      else if (nonEmpty && pattern is StringLiteral { Value.Length: 0 })
        this.Refuse(new Refusal(RustFrontend.Unsupported, "split at an empty pattern", pattern.At));
    });
  }

  private RType CharMethod(MethodCallExpr call) {
    var name = call.Name;
    this._typed.Methods[call] = "char::" + name;
    if (_asciiPredicates.Contains(name)) {
      this.Count(call, 0);
      return RType.Bool;
    }

    switch (name) {
      case "to_ascii_uppercase" or "to_ascii_lowercase" or "clone":
        this.Count(call, 0);
        return RType.Char;
      case "len_utf8":
        this.Count(call, 0);
        return RType.Usize;
      case "to_string":
        this.Count(call, 0);
        return RType.String;
      case "to_digit" or "is_digit":
        this.Count(call, 1);
        this.Unify(RType.U32, this.Value(call.Arguments[0], RType.U32), call.Arguments[0].At);
        return name == "to_digit" ? RType.Option(RType.U32) : RType.Bool;
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    this._typed.Methods.Remove(call);
    return this.Refused(RustFrontend.Unsupported, $"char::{name}", call.At);
  }

  // ---------------------------------------------------------------- format!

  /// <summary>
  /// Checks a format string and its arguments from <paramref name="start"/> on, and records its
  /// pieces: Display of integers, <c>bool</c>, <c>char</c> and text, Debug of integers and
  /// <c>bool</c>, the radix traits of integers, with fill, alignment, sign, <c>#</c>, <c>0</c> and width.
  /// </summary>
  private void Format(MacroExpr macro, int start) {
    if (macro.Arguments.Count <= start || macro.Arguments[start] is not StringLiteral format)
      throw this.Invalid("format argument must be a string literal", macro.At);

    var positional = new List<Expr>();
    var named = new Dictionary<string, Expr>(System.StringComparer.Ordinal);
    foreach (var argument in macro.Arguments.Skip(start + 1)) {
      if (argument is AssignExpr { Op: "=", Target: PathExpr { Segments: [var name] } } assignment)
        named[name] = assignment.Value;
      else if (named.Count > 0)
        throw this.Invalid("positional arguments cannot follow named arguments", argument.At);
      else
        positional.Add(argument);
    }

    var pieces = new List<FormatPiece>();
    var used = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
    var text = new StringBuilder();
    var next = 0;
    var source = format.Value;
    for (var index = 0; index < source.Length; ++index) {
      var c = source[index];
      if (c == '}') {
        if (index + 1 >= source.Length || source[index + 1] != '}')
          throw this.Invalid("invalid format string: unmatched `}` found", format.At);

        text.Append('}');
        ++index;
        continue;
      }

      if (c != '{') {
        text.Append(c);
        continue;
      }

      if (index + 1 < source.Length && source[index + 1] == '{') {
        text.Append('{');
        ++index;
        continue;
      }

      var close = source.IndexOf('}', index);
      if (close < 0)
        throw this.Invalid("invalid format string: expected `}` but string was terminated", format.At);

      if (text.Length > 0) {
        pieces.Add(new FormatPiece(text.ToString(), null));
        text.Clear();
      }

      var spec = source[(index + 1)..close];
      var colon = spec.IndexOf(':');
      var reference = colon < 0 ? spec : spec[..colon];
      Expr argument;
      if (reference.Length == 0) {
        if (next >= positional.Count)
          throw this.Invalid($"invalid reference to positional argument {next}", format.At);

        argument = positional[next++];
      } else if (char.IsAsciiDigit(reference[0])) {
        var position = int.Parse(reference, CultureInfo.InvariantCulture);
        if (position >= positional.Count)
          throw this.Invalid($"invalid reference to positional argument {position}", format.At);

        argument = positional[position];
      } else if (named.TryGetValue(reference, out var value)) {
        argument = value;
      } else {
        argument = new PathExpr(format.At, [reference]);
        named[reference] = argument;
      }

      used.Add(argument);
      pieces.Add(this.Spec(colon < 0 ? "" : spec[(colon + 1)..], argument, format.At));
      index = close;
    }

    if (text.Length > 0)
      pieces.Add(new FormatPiece(text.ToString(), null));

    foreach (var argument in positional.Concat(named.Values).Where(argument => !used.Contains(argument)))
      throw this.Invalid("argument never used", argument.At);

    foreach (var argument in positional.Concat(named.Values).Distinct(ReferenceEqualityComparer.Instance).Cast<Expr>()) {
      var type = this.Value(argument);
      var kinds = pieces.Where(piece => ReferenceEquals(piece.Argument, argument)).Select(piece => piece.Kind).ToList();
      this._deferred.Add(() => this.Formattable(argument, type, kinds));
    }

    this._typed.Formats[macro] = pieces;
  }

  /// <summary>A format spec: <c>[[fill]align][sign]['#']['0'][width][type]</c>; a precision, a width argument and the exponent traits are refused.</summary>
  private FormatPiece Spec(string spec, Expr argument, Span at) {
    var piece = new FormatPiece(null, argument);
    var index = 0;
    var runes = spec.EnumerateRunes().Select(rune => rune.Value).ToList();
    if (runes.Count >= 2 && runes[1] is '<' or '^' or '>') {
      piece = piece with { Fill = runes[0], Align = (char)runes[1] };
      index = 2;
    } else if (runes.Count >= 1 && runes[0] is '<' or '^' or '>') {
      piece = piece with { Align = (char)runes[0] };
      index = 1;
    }

    if (index < runes.Count && runes[index] is '+' or '-') {
      piece = piece with { Plus = runes[index] == '+' };
      ++index;
    }

    if (index < runes.Count && runes[index] == '#') {
      piece = piece with { Alternate = true };
      ++index;
    }

    if (index < runes.Count && runes[index] == '0') {
      piece = piece with { Zero = true };
      ++index;
    }

    var width = 0;
    while (index < runes.Count && runes[index] is >= '0' and <= '9') {
      width = checked(width * 10 + runes[index] - '0');
      ++index;
    }

    var rest = string.Concat(runes.Skip(index).Select(char.ConvertFromUtf32));
    if (rest is not ("" or "?" or "x" or "X" or "o" or "b")) {
      this.Refuse(new Refusal(RustFrontend.UnsupportedMacro, $"format spec ':{spec}'", at));
      return piece with { Width = width };
    }

    return piece with { Width = width, Kind = rest.Length == 0 ? '\0' : rest[0] };
  }

  /// <summary>Whether an argument has the formatting traits its pieces use.</summary>
  private void Formattable(Expr argument, RType type, IReadOnlyList<char> kinds) {
    if (this.IsError(type))
      return;

    var value = type.Dereferenced;
    foreach (var kind in kinds) {
      var ok = kind switch {
        '\0' => value.Kind is RKind.Integer or RKind.Bool or RKind.Char || value.IsText,
        '?' => value.Kind is RKind.Integer or RKind.Bool,
        _ => value.Kind == RKind.Integer
      };
      if (ok)
        continue;

      if (value.Kind == RKind.Float || (kind == '?' && (value.IsText || value.Kind == RKind.Char)))
        this.Refuse(new Refusal(RustFrontend.UnsupportedMacro, $"format of a value of type '{type}'{(kind == '?' ? " with Debug" : "")}", argument.At));
      else
        throw this.Invalid($"E0277: `{type}` doesn't implement `{(kind == '\0' ? "std::fmt::Display" : kind == '?' ? "Debug" : "std::fmt::" + kind switch { 'x' => "LowerHex", 'X' => "UpperHex", 'o' => "Octal", _ => "Binary" })}`", argument.At);
    }
  }

}
