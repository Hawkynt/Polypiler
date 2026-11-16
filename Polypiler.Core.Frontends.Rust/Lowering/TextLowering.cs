using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Text onto the IR's strings: a <c>String</c> and a <c>&amp;str</c> are the string, a <c>char</c> its
/// code point as a <c>u32</c>. Byte lengths, byte offsets, bytes and chars are Rust's UTF-8 ones,
/// computed by the helpers of <see cref="TextHelpers"/>; a byte range off a char boundary panics as
/// Rust's slicing does.
/// </summary>
internal sealed partial class FunctionLowering {

  private IrExpr Helper(string name, IrType type, params IrExpr[] arguments) => new IrCall(this._module.Text(name), type, arguments);

  private static IrExpr Text(string value) => new IrStrConst(value);

  private static IrExpr Units(IrExpr text) => new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [text]);

  private static IrExpr Concat(params IrExpr[] parts) => parts.Length == 1 ? parts[0] : new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, parts);

  /// <summary>A char as the string of its code point.</summary>
  private IrExpr CharText(IrExpr code) => code is IrConst constant
    ? Text(char.ConvertFromUtf32((int)constant.Value))
    : this.Helper("char_str", IrType.String, code);

  /// <summary>A pattern of a text search as a string: a <c>char</c> as its text.</summary>
  private IrExpr Needle(Expr pattern) {
    var value = this.Value(pattern);
    return this._typed.TypeOf(pattern).Resolved.Kind == RKind.Char ? this.CharText(value) : value;
  }

  // ---------------------------------------------------------------- Display

  /// <summary>A value as Display writes it: an integer in decimal, a <c>bool</c> as <c>true</c> or <c>false</c>, a char as itself, text as it is.</summary>
  private IrExpr Displayed(IrExpr value, RType type) => type.Dereferenced.Kind switch {
    RKind.Integer => new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [value]),
    RKind.Bool => new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [value]),
    RKind.Char => this.CharText(value),
    _ => value
  };

  // ---------------------------------------------------------------- operators

  /// <summary><c>String + &amp;str</c> and the comparisons of text; the order is the order of code points, as UTF-8's bytes give it.</summary>
  private IrExpr TextOperator(string op, IrExpr left, IrExpr right) {
    switch (op) {
      case "+":
        return Concat(left, right);
      case "==":
        return new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
      case "!=":
        return Not(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]));
    }

    var order = this.Helper("str_cmp", IrType.I32, left, right);
    var comparison = op switch {
      "<" => IrBinaryOp.Less,
      ">" => IrBinaryOp.Greater,
      "<=" => IrBinaryOp.LessOrEqual,
      _ => IrBinaryOp.GreaterOrEqual
    };
    return Binary(comparison, order, new IrConst(IrType.I32, 0));
  }

  // ---------------------------------------------------------------- calls

  /// <summary><c>String::new</c>, <c>String::from</c>, <c>String::with_capacity</c>, the functions of <c>char</c> and the IR's string operations of the helpers.</summary>
  private IrExpr TextCall(CallExpr call, string resolution) {
    var values = this.Operands([.. call.Arguments]);
    switch (resolution) {
      case "String::new" or "String::with_capacity":
        return Text("");
      case "String::from":
        return this._typed.TypeOf(call.Arguments[0]).Resolved.Kind == RKind.Char ? this.CharText(values[0]) : values[0];
      case "char::from":
        return Cast(values[0], IrType.U32);
      case "char::from_u32": {
        var code = this.Stable(values[0], "code");
        var valid = And(Compare(IrBinaryOp.LessOrEqual, code, new IrConst(IrType.U32, 0x10FFFF)),
          Or(Compare(IrBinaryOp.Less, code, new IrConst(IrType.U32, 0xD800)), Compare(IrBinaryOp.Greater, code, new IrConst(IrType.U32, 0xDFFF))));
        return new IrConditional(valid, new IrSome(code), new IrNone(IrType.Optional(IrType.U32)));
      }
      case "char::from_digit":
        return this.Helper("char_from_digit", IrType.Optional(IrType.U32), values[0], values[1]);
      case "__units":
        return Cast(Units(values[0]), IrType.U64);
      case "__unit":
        return Cast(new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [values[0], Cast(values[1], IrType.I32)]), IrType.U32);
      case "__substring": {
        var from = this.Stable(Cast(values[1], IrType.I32), "from");
        return new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [values[0], from, Binary(IrBinaryOp.Subtract, Cast(values[2], IrType.I32), from)]);
      }
      case "__from_unit":
        return new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [Cast(values[0], IrType.U16)]);
      default:
        // __index_of.
        return Cast(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [values[0], values[1], Cast(values[2], IrType.I32)]), IrType.I64);
    }
  }

  // ---------------------------------------------------------------- methods

  private IrExpr? TextMethod(MethodCallExpr call, string name) {
    switch (name) {
      case "push_str" or "push" or "clear": {
        var place = this.PlaceOf(call.Receiver);
        var appended = name == "clear" ? null : name == "push" ? this.CharText(this.Value(call.Arguments[0])) : this.Value(call.Arguments[0]);
        if (place is null || this._diverged)
          return null;

        this.Write(place, appended is null ? Text("") : Concat(this.Read(place), appended));
        return null;
      }
      case "contains" or "starts_with" or "ends_with" or "find": {
        var text = this.Stable(this.Value(call.Receiver), "text");
        var needle = this.Needle(call.Arguments[0]);
        return name switch {
          "contains" => new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [text, needle]),
          "starts_with" => new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [text, needle]),
          "ends_with" => new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [text, needle]),
          _ => this.Helper("str_find", IrType.Optional(IrType.U64), text, needle)
        };
      }
      case "repeat": {
        var values = this.Operands(call.Receiver, call.Arguments[0]);
        return new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [values[0], Cast(values[1], IrType.I32)]);
      }
      case "parse":
        return this.Parse(call);
    }

    var value = this.Value(call.Receiver);
    return name switch {
      "len" => this.Helper("str_len", IrType.U64, value),
      "is_empty" => Equal(Units(value), new IrConst(IrType.I32, 0)),
      "as_bytes" => new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.ReadOnlySpan(IrType.U8),
        [new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.U8), [this.Helper("str_utf8", IrType.List(IrType.U8), value)])]),
      "trim" => this.Helper("str_trim", IrType.String, value, Truth(true), Truth(true)),
      "trim_start" => this.Helper("str_trim", IrType.String, value, Truth(true), Truth(false)),
      "trim_end" => this.Helper("str_trim", IrType.String, value, Truth(false), Truth(true)),
      "to_ascii_uppercase" => this.Helper("str_ascii_case", IrType.String, value, Truth(true)),
      "to_ascii_lowercase" => this.Helper("str_ascii_case", IrType.String, value, Truth(false)),
      // as_str, to_string, to_owned, clone: the string is the value.
      _ => value
    };
  }

  /// <summary><c>parse::&lt;T&gt;()</c> of an integer type: the digits as <c>from_str_radix</c> reads them in base 10, then the range of the type.</summary>
  private IrExpr Parse(MethodCallExpr call) {
    var result = this._typed.TypeOf(call);
    var type = result.Resolved.Element!.Resolved;
    var text = this.Stable(this.Value(call.Receiver), "text");
    var digits = this.Stable(this.Helper("parse_digits", IrType.Optional(IrType.U64), text, Truth(type.IsSigned)), "digits");
    var magnitude = this.Stable(new IrCoalesce(digits, new IrConst(IrType.U64, 0)), "magnitude");
    IrExpr ok;
    IrExpr value;
    if (type.IsSigned) {
      var negative = this.Stable(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [text, Text("-")]), "negative");
      var limit = new IrConst(IrType.U64, (long)(ulong)type.Maximum);
      ok = And(Not(new IrIsNone(digits)), new IrConditional(negative, Compare(IrBinaryOp.LessOrEqual, magnitude, new IrConst(IrType.U64, (long)((ulong)type.Maximum + 1))), Compare(IrBinaryOp.LessOrEqual, magnitude, limit)));
      value = new IrConditional(negative, Cast(Binary(IrBinaryOp.Subtract, new IrConst(IrType.U64, 0), magnitude), type.Ir), Cast(magnitude, type.Ir));
    } else {
      ok = And(Not(new IrIsNone(digits)), Compare(IrBinaryOp.LessOrEqual, magnitude, new IrConst(IrType.U64, (long)(ulong)type.Maximum)));
      value = Cast(magnitude, type.Ir);
    }

    var flag = this.Stable(ok, "parsed");
    return new IrStructNew(this.Ir(result), [flag, new IrConditional(flag, value, this.Zero(type.Ir))]);
  }

  private IrExpr CharMethod(MethodCallExpr call, string name) {
    var values = this.Operands([call.Receiver, .. call.Arguments]);
    var c = this.Stable(values[0], "c");
    IrExpr Between(int low, int high) => And(Compare(IrBinaryOp.GreaterOrEqual, c, new IrConst(IrType.U32, low)), Compare(IrBinaryOp.LessOrEqual, c, new IrConst(IrType.U32, high)));
    IrExpr Is(int code) => Equal(c, new IrConst(IrType.U32, code));
    switch (name) {
      case "is_ascii":
        return Compare(IrBinaryOp.Less, c, new IrConst(IrType.U32, 0x80));
      case "is_ascii_digit":
        return Between('0', '9');
      case "is_ascii_alphabetic":
        return Or(Between('a', 'z'), Between('A', 'Z'));
      case "is_ascii_alphanumeric":
        return Or(Or(Between('a', 'z'), Between('A', 'Z')), Between('0', '9'));
      case "is_ascii_uppercase":
        return Between('A', 'Z');
      case "is_ascii_lowercase":
        return Between('a', 'z');
      case "is_ascii_hexdigit":
        return Or(Or(Between('a', 'f'), Between('A', 'F')), Between('0', '9'));
      case "is_ascii_whitespace":
        return Or(Or(Or(Is(' '), Is('\t')), Or(Is('\n'), Is('\f'))), Is('\r'));
      case "is_ascii_punctuation":
        return Or(Or(Between('!', '/'), Between(':', '@')), Or(Between('[', '`'), Between('{', '~')));
      case "is_ascii_graphic":
        return Between('!', '~');
      case "is_ascii_control":
        return Or(Between(0, 0x1F), Is(0x7F));
      case "is_whitespace":
        return this.Helper("is_whitespace", IrType.Bool, c);
      case "to_ascii_uppercase":
        return new IrConditional(Between('a', 'z'), Binary(IrBinaryOp.Subtract, c, new IrConst(IrType.U32, 32)), c);
      case "to_ascii_lowercase":
        return new IrConditional(Between('A', 'Z'), Binary(IrBinaryOp.Add, c, new IrConst(IrType.U32, 32)), c);
      case "len_utf8":
        return new IrConditional(Compare(IrBinaryOp.Less, c, new IrConst(IrType.U32, 0x80)), new IrConst(IrType.U64, 1),
          new IrConditional(Compare(IrBinaryOp.Less, c, new IrConst(IrType.U32, 0x800)), new IrConst(IrType.U64, 2),
            new IrConditional(Compare(IrBinaryOp.Less, c, new IrConst(IrType.U32, 0x10000)), new IrConst(IrType.U64, 3), new IrConst(IrType.U64, 4))));
      case "to_string":
        return this.CharText(c);
      case "to_digit":
        return this.Helper("char_digit", IrType.Optional(IrType.U32), c, values[1]);
      case "is_digit":
        return Not(new IrIsNone(this.Helper("char_digit", IrType.Optional(IrType.U32), c, values[1])));
      default:
        // clone.
        return c;
    }
  }

  // ---------------------------------------------------------------- slicing

  /// <summary><c>s[a..b]</c> and its relatives: the code units between the char boundaries at the byte offsets.</summary>
  private IrExpr TextSlice(IndexExpr index) {
    var range = (RangeExpr)index.Index;
    var text = this.Stable(this.Value(index.Target), "text");
    var from = range.Start is null ? new IrConst(IrType.U64, 0) : this.Stable(this.Value(range.Start), "from");
    IrExpr to;
    if (range.End is null) {
      to = this.Helper("str_len", IrType.U64, text);
    } else {
      to = this.Value(range.End);
      if (range.Inclusive) {
        var end = this.Stable(to, "end");
        this.Emit(new IrIf(Equal(end, new IrConst(IrType.U64, -1)), new IrBlock([RustTraps.OutOfBounds("attempted to index str up to maximum usize")]), null));
        to = Binary(IrBinaryOp.Add, end, new IrConst(IrType.U64, 1));
      }
    }

    return this.Helper("str_slice", IrType.String, text, from, to);
  }

  // ---------------------------------------------------------------- iteration

  /// <summary>The list a <see cref="SourceKind.Text"/> source iterates: the chars, the bytes or the parts.</summary>
  private IrExpr TextSource(ElementSource source) {
    var text = this.Value(source.Container);
    return source.TextMethod switch {
      "chars" => this.Helper("str_chars", IrType.List(IrType.U32), text),
      "bytes" => this.Helper("str_utf8", IrType.List(IrType.U8), text),
      _ => this.Helper("str_split", IrType.List(IrType.String), this.Stable(text, "text"), this.Needle(source.Separator!))
    };
  }

  // ---------------------------------------------------------------- format!

  /// <summary><c>format!</c>: the pieces one after the other, each argument evaluated once, in the order of the arguments.</summary>
  private IrExpr Format(MacroExpr macro) {
    var pieces = this._typed.Formats[macro];
    var values = new Dictionary<Expr, IrExpr>(ReferenceEqualityComparer.Instance);
    foreach (var argument in pieces.Where(piece => piece.Argument is not null).Select(piece => piece.Argument!).Distinct(ReferenceEqualityComparer.Instance).Cast<Expr>())
      values[argument] = this.Stable(this.Value(argument), "argument");

    var parts = pieces.Select(piece => piece.Text is { } text ? Text(text) : this.Formatted(piece, values[piece.Argument!])).ToArray();
    return parts.Length == 0 ? Text("") : Concat(parts);
  }

  /// <summary>An argument as its spec writes it: the digits, the sign and the prefix, then the fill to the width.</summary>
  private IrExpr Formatted(FormatPiece piece, IrExpr value) {
    var type = this._typed.TypeOf(piece.Argument!).Dereferenced;
    var number = type.Kind == RKind.Integer;
    IrExpr digits;
    IrExpr? sign = null;
    if (number && piece.Kind is 'x' or 'X' or 'o' or 'b') {
      var radix = piece.Kind switch { 'o' => 8, 'b' => 2, _ => 16 };
      digits = new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String, [value, new IrConst(IrType.I32, radix), new IrConst(IrType.I32, 1), Truth(piece.Kind == 'X'), new IrConst(IrType.I32, (int)RadixMode.TwoComplement)]);
      if (piece.Plus)
        sign = Text("+");
    } else if (number) {
      var text = this.Stable(this.Displayed(value, type), "digits");
      if (type.IsSigned) {
        var negative = this.Stable(Compare(IrBinaryOp.Less, value, this.Zero(type.Ir)), "negative");
        sign = new IrConditional(negative, Text("-"), Text(piece.Plus ? "+" : ""));
        digits = new IrConditional(negative, new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [text, new IrConst(IrType.I32, 1)]), text);
      } else {
        digits = text;
        if (piece.Plus)
          sign = Text("+");
      }
    } else {
      digits = this.Displayed(value, type);
    }

    if (piece.Alternate && number && piece.Kind is 'x' or 'X' or 'o' or 'b')
      sign = Concat(sign ?? Text(""), Text(piece.Kind switch { 'o' => "0o", 'b' => "0b", _ => "0x" }));

    var body = sign is null ? digits : Concat(sign, digits);
    if (piece.Width == 0)
      return body;

    var width = new IrConst(IrType.I32, piece.Width);
    if (piece.Zero && number) {
      // The zeros stand between the sign and the digits; fill and alignment do not apply.
      var head = this.Stable(sign ?? Text(""), "sign");
      var tail = this.Stable(digits, "digits");
      var count = this.Stable(Binary(IrBinaryOp.Subtract, width, Binary(IrBinaryOp.Add, Units(head), Units(tail))), "zeros");
      return Concat(head, this.Fill("0", count), tail);
    }

    var formatted = this.Stable(body, "formatted");
    var length = number ? Units(formatted) : Cast(this.Helper("str_char_count", IrType.U64, formatted), IrType.I32);
    var missing = this.Stable(Binary(IrBinaryOp.Subtract, width, length), "missing");
    var fill = char.ConvertFromUtf32(piece.Fill);
    var align = piece.Align != '\0' ? piece.Align : number ? '>' : '<';
    return align switch {
      '<' => Concat(formatted, this.Fill(fill, missing)),
      '>' => Concat(this.Fill(fill, missing), formatted),
      _ => Concat(this.Fill(fill, Binary(IrBinaryOp.Divide, missing, new IrConst(IrType.I32, 2))), formatted,
        this.Fill(fill, Binary(IrBinaryOp.Subtract, missing, Binary(IrBinaryOp.Divide, missing, new IrConst(IrType.I32, 2)))))
    };
  }

  /// <summary>A fill repeated a number of times, none for a count below one.</summary>
  private IrExpr Fill(string fill, IrExpr count) {
    var times = this.Stable(count, "count");
    return new IrConditional(Compare(IrBinaryOp.Greater, times, new IrConst(IrType.I32, 0)), new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [Text(fill), times]), Text(""));
  }

}
