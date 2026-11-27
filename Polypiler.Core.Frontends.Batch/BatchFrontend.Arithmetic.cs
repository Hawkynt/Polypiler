using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private sealed class Arithmetic(string text, int line, Func<string, IrLocal> local,
    (char Letter, IrLocal Local)? forVariable = null, Func<string, IrLocal>? expand = null) {

    private int _position;

    public IrExpr Read() {
      var value = this.BitAnd(0);
      this.SkipSpace();
      if (this._position != text.Length)
        throw new Refusal(line, $"arithmetic syntax near {text[this._position..]}");
      return value;
    }

    private IrExpr BitAnd(int depth) {
      var value = this.Shift(depth);
      while (this.TakeSingle('&'))
        value = new IrBinary(IrBinaryOp.And, value, this.Shift(depth));
      return value;
    }

    private IrExpr Shift(int depth) {
      var value = this.Sum(depth);
      while (this.Take(">>"))
        value = new IrBinary(IrBinaryOp.ShiftRight, value, this.Sum(depth));
      return value;
    }

    private IrExpr Sum(int depth) {
      var value = this.Product(depth);
      while (true) {
        if (this.Take('+'))
          value = new IrBinary(IrBinaryOp.Add, value, this.Product(depth));
        else if (this.Take('-'))
          value = new IrBinary(IrBinaryOp.Subtract, value, this.Product(depth));
        else
          return value;
      }
    }

    private IrExpr Product(int depth) {
      var value = this.Atom(depth);
      while (this.Take('*'))
        value = new IrBinary(IrBinaryOp.Multiply, value, this.Atom(depth));
      return value;
    }

    private IrExpr Atom(int depth) {
      if (depth >= 128)
        throw new Refusal(line, "arithmetic nesting at or beyond 128 levels");

      var negative = this.Take('-');
      if (this.Take('(')) {
        var inner = this.BitAnd(depth + 1);
        if (!this.Take(')'))
          throw new Refusal(line, "missing close parenthesis", malformed: true);
        return Negative(inner, negative);
      }

      var start = this._position;
      if (start == text.Length)
        throw new Refusal(line, "missing arithmetic operand", malformed: true);
      if (text.AsSpan(start).StartsWith("%%", StringComparison.Ordinal)) {
        if (start + 2 < text.Length && char.IsAsciiLetter(text[start + 2])
            && (start + 3 == text.Length || (!char.IsAsciiLetterOrDigit(text[start + 3]) && text[start + 3] != '_'))) {
          var letter = text[start + 2];
          if (forVariable is { } bound && bound.Letter == letter) {
            this._position += 3;
            return Negative(new IrLocalRef(bound.Local), negative);
          }
          throw new Refusal(line, $"unbound FOR variable %%{letter}");
        }
        throw new Refusal(line, $"arithmetic syntax near {text[start..]}");
      }
      if (expand is not null && text[start] is '%' or '!') {
        var end = this.NameEnd(start + 1);
        if (end == start + 1 || end == text.Length || text[end] != text[start])
          throw new Refusal(line, $"invalid numeric expansion near {text[start..]}");
        this._position = end + 1;
        return Negative(new IrLocalRef(expand(text[start..this._position])), negative);
      }
      if (char.IsAsciiDigit(text[start])) {
        while (this._position < text.Length && char.IsAsciiDigit(text[this._position]))
          ++this._position;
        var digits = text[start..this._position];
        var literal = Constant(negative ? "-" + digits : digits, line);
        if (literal.Value == int.MinValue)
          throw new Refusal(line, "SET /A decimal magnitude exceeds 2147483647", malformed: true);
        return literal;
      }
      var nameEnd = this.NameEnd(start);
      if (nameEnd > start) {
        this._position = nameEnd;
        return Negative(new IrLocalRef(local(text[start..this._position])), negative);
      }
      throw new Refusal(line, $"arithmetic syntax near {text[start..]}");
    }

    private int NameEnd(int start) {
      if (start == text.Length || (!char.IsAsciiLetter(text[start]) && text[start] != '_'))
        return start;
      var end = start + 1;
      while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
        ++end;
      return end;
    }

    private static IrExpr Negative(IrExpr expression, bool negative) => negative ? new IrUnary(IrUnaryOp.Negate, expression) : expression;

    private bool Take(char token) {
      this.SkipSpace();
      if (this._position == text.Length || text[this._position] != token)
        return false;
      ++this._position;
      return true;
    }

    private bool TakeSingle(char token) {
      this.SkipSpace();
      if (this._position == text.Length || text[this._position] != token)
        return false;
      if (this._position + 1 < text.Length && text[this._position + 1] == token)
        return false;
      ++this._position;
      return true;
    }

    private bool Take(string token) {
      this.SkipSpace();
      if (!text.AsSpan(this._position).StartsWith(token, StringComparison.Ordinal))
        return false;
      this._position += token.Length;
      return true;
    }

    private void SkipSpace() {
      while (this._position < text.Length && (text[this._position] is ' ' or '\t'))
        ++this._position;
    }

  }

}
