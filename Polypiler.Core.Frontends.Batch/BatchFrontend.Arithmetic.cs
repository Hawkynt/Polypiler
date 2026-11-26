using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private sealed class Arithmetic(string text, int line, Func<string, IrLocal> local, (char Name, IrLocal Local)? iterator = null) {

    private int _position;

    public int IteratorReferences { get; private set; }

    public IrExpr Read() {
      var value = this.Sum(0);
      this.SkipSpace();
      if (this._position != text.Length)
        throw new Refusal(line, $"arithmetic syntax near {text[this._position..]}");
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
        var inner = this.Sum(depth + 1);
        if (!this.Take(')'))
          throw new Refusal(line, "missing close parenthesis", malformed: true);
        return Negative(inner, negative);
      }

      var start = this._position;
      if (start == text.Length)
        throw new Refusal(line, "missing arithmetic operand", malformed: true);
      if (iterator is { } variable && start + 3 <= text.Length
          && text[start] == '%' && text[start + 1] == '%' && text[start + 2] == variable.Name
          && (start + 3 == text.Length || !NamePart(text[start + 3]))) {
        this._position += 3;
        ++this.IteratorReferences;
        return Negative(new IrLocalRef(variable.Local), negative);
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
      if (char.IsAsciiLetter(text[start]) || text[start] == '_') {
        ++this._position;
        while (this._position < text.Length && NamePart(text[this._position]))
          ++this._position;
        return Negative(new IrLocalRef(local(text[start..this._position])), negative);
      }
      throw new Refusal(line, $"arithmetic syntax near {text[start..]}");
    }

    private static bool NamePart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    private static IrExpr Negative(IrExpr expression, bool negative) => negative ? new IrUnary(IrUnaryOp.Negate, expression) : expression;

    private bool Take(char token) {
      this.SkipSpace();
      if (this._position == text.Length || text[this._position] != token)
        return false;
      ++this._position;
      return true;
    }

    private void SkipSpace() {
      while (this._position < text.Length && (text[this._position] is ' ' or '\t'))
        ++this._position;
    }

  }

}
