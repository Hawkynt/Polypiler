using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Core.Backends.JavaScript.Printing;

/// <summary>
/// Turns a JavaScript syntax tree into text, the only place where generated JavaScript becomes text.
/// The house style: two spaces per level, the opening brace at the end of the line that opens the
/// block, one statement per line, LF line endings, strings in double quotes. Parentheses stand where
/// the tree has them; where a rewritten tree lacks parentheses that the precedence of ECMA-262 needs
/// (§13 to §15), the printer adds exactly those, and it puts an expression statement that would start
/// with <c>{</c> or <c>function</c> in parentheses. A number is spelled by its value - the shortest
/// digits that read back as the same double - unless it carries a spelling that still denotes it.
/// </summary>
public static class JavaScriptPrinter {

  private const string Indent = "  ";

  /// <summary>How many elements of a tabulated array stand on one line.</summary>
  private const int RowLength = 8;

  /// <summary>Prints a file.</summary>
  /// <param name="program">The tree.</param>
  /// <param name="profile">The profile the file is written for.</param>
  /// <returns>The text.</returns>
  public static string Print(JsProgram program, JavaScriptProfile profile) {
    ArgumentNullException.ThrowIfNull(program);
    ArgumentNullException.ThrowIfNull(profile);
    var writer = new Writer();
    foreach (var line in program.Header)
      writer.Comment(line, 0);

    foreach (var statement in program.Body)
      writer.Statement(statement, 0);

    return writer.Text;
  }

  /// <summary>Prints a statement at the outermost level.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The text, ending with a line break.</returns>
  public static string Print(JsStatement statement) {
    var writer = new Writer();
    writer.Statement(statement, 0);
    return writer.Text;
  }

  /// <summary>Prints an expression on its own.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The text.</returns>
  public static string Print(JsExpression expression) {
    var writer = new Writer();
    writer.Expression(expression, 0);
    return writer.Text;
  }

  private const int Primary = 20, Postfix = 15, Prefix = 14, Exponent = 13, Conditional = 2, Sequence = 1;

  /// <summary>How tightly an expression binds, by the precedence of its operator; higher binds tighter.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The precedence: 20 for a primary expression down to 1 for a sequence.</returns>
  public static int Precedence(JsExpression expression) => expression switch {
    JsNumber number when IsNegative(number.Value) => Prefix,
    JsBigInt { Value.Sign: < 0 } => Prefix,
    JsUpdate { Prefix: false } => Postfix,
    JsUnary or JsUpdate => Prefix,
    JsBinary binary => Of(binary.Operator),
    JsConditional or JsAssignment => Conditional,
    JsSequence => Sequence,
    _ => Primary
  };

  /// <summary>Whether a number is written with a minus sign: a negative one, -0 and -Infinity, but no NaN, whatever its sign bit.</summary>
  /// <param name="value">The number.</param>
  /// <returns><see langword="true"/> when its spelling starts with a minus sign.</returns>
  public static bool IsNegative(double value) => !double.IsNaN(value) && double.IsNegative(value);

  /// <summary>The precedence of a binary operator.</summary>
  private static int Of(string op) => op switch {
    "**" => Exponent,
    "*" or "/" or "%" => 12,
    "+" or "-" => 11,
    "<<" or ">>" or ">>>" => 10,
    "<" or ">" or "<=" or ">=" or "in" or "instanceof" => 9,
    "==" or "!=" or "===" or "!==" => 8,
    "&" => 7,
    "^" => 6,
    "|" => 5,
    "&&" => 4,
    "||" or "??" => 3,
    _ => throw new ArgumentException($"'{op}' is no binary operator of JavaScript.", nameof(op))
  };

  /// <summary>Whether an expression, written at the start of a statement, would start with <c>{</c> or <c>function</c>, which a statement cannot.</summary>
  private static bool StartsAmbiguously(JsExpression expression) => expression switch {
    JsObject or JsFunctionExpression => true,
    JsMember member => StartsAmbiguously(member.Target),
    JsIndex index => StartsAmbiguously(index.Target),
    JsCall call => StartsAmbiguously(call.Callee),
    JsBinary binary => StartsAmbiguously(binary.Left),
    JsAssignment assignment => StartsAmbiguously(assignment.Left),
    JsConditional choice => StartsAmbiguously(choice.Test),
    JsSequence sequence => StartsAmbiguously(sequence.Expressions[0]),
    JsUpdate { Prefix: false } update => StartsAmbiguously(update.Operand),
    _ => false
  };

  private sealed class Writer {

    private readonly StringBuilder _text = new();

    public string Text => this._text.ToString();

    private StringBuilder Line(int depth) {
      for (var level = 0; level < depth; ++level)
        this._text.Append(Indent);

      return this._text;
    }

    private void NewLine(int depth) {
      this._text.Append('\n');
      this.Line(depth);
    }

    public void Comment(string line, int depth) => this.Line(depth).Append(line.Length == 0 ? "//" : "// " + line).Append('\n');

    /// <summary>A statement on its own line: what stands in front of it, its indentation, the statement and the line break.</summary>
    public void Statement(JsStatement statement, int depth) {
      this._text.Append('\n', statement.Leading.BlankLines);

      foreach (var comment in statement.Leading.Comments)
        this.Comment(comment, depth);

      this.Line(depth);
      this.Bare(statement, depth);
      this._text.Append('\n');
    }

    /// <summary>A block whose opening brace stands where the text is; the closing brace ends without a line break.</summary>
    private void Braces(JsBlock block, int depth) {
      if (block.Inline) {
        this._text.Append('{');
        foreach (var statement in block.Statements) {
          this._text.Append(' ');
          this.Bare(statement, depth);
        }

        this._text.Append(block.Statements.Count == 0 ? "}" : " }");
        return;
      }

      this._text.Append("{\n");
      foreach (var statement in block.Statements)
        this.Statement(statement, depth + 1);

      this.Line(depth).Append('}');
    }

    /// <summary>A statement from where the text is, without a line break at its end; lines inside it are indented for <paramref name="depth"/>.</summary>
    private void Bare(JsStatement statement, int depth) {
      switch (statement) {
        case JsBlock block:
          this.Braces(block, depth);
          break;
        case JsVariableDeclaration declaration:
          this.Declaration(declaration, depth);
          this._text.Append(';');
          break;
        case JsExpressionStatement expression:
          this.Operand(expression.Expression, !StartsAmbiguously(expression.Expression), depth);
          this._text.Append(';');
          break;
        case JsIf conditional:
          this._text.Append("if (");
          this.Expression(conditional.Test, depth);
          this._text.Append(") ");
          this.Bare(conditional.Then, depth);
          if (conditional.Else is null)
            break;

          if (conditional.Then is JsBlock || conditional.ElseInline)
            this._text.Append(' ');
          else
            this.NewLine(depth);

          this._text.Append("else ");
          this.Bare(conditional.Else, depth);
          break;
        case JsWhile loop:
          this._text.Append("while (");
          this.Expression(loop.Test, depth);
          this._text.Append(") ");
          this.Bare(loop.Body, depth);
          break;
        case JsDoWhile loop:
          this._text.Append("do ");
          this.Bare(loop.Body, depth);
          if (loop.Body is JsBlock)
            this._text.Append(' ');
          else
            this.NewLine(depth);

          this._text.Append("while (");
          this.Expression(loop.Test, depth);
          this._text.Append(");");
          break;
        case JsFor loop:
          this._text.Append("for (");
          if (loop.Declaration is not null)
            this.Declaration(loop.Declaration, depth);

          this._text.Append("; ");
          if (loop.Test is not null)
            this.Expression(loop.Test, depth);

          this._text.Append("; ");
          if (loop.Update is not null)
            this.Expression(loop.Update, depth);

          this._text.Append(") ");
          this.Bare(loop.Body, depth);
          break;
        case JsSwitch choice:
          this.Switch(choice, depth);
          break;
        case JsBreak:
          this._text.Append("break;");
          break;
        case JsContinue:
          this._text.Append("continue;");
          break;
        case JsReturn result:
          this._text.Append("return");
          if (result.Value is not null) {
            this._text.Append(' ');
            this.Expression(result.Value, depth);
          }

          this._text.Append(';');
          break;
        case JsThrow exception:
          this._text.Append("throw ");
          this.Expression(exception.Value, depth);
          this._text.Append(';');
          break;
        case JsFunctionDeclaration function:
          this.Function(function.Name, function.Parameters, function.Body, depth);
          break;
        default:
          this._text.Append("export ");
          this.Bare(((JsExport)statement).Declaration, depth);
          break;
      }
    }

    private void Function(string? name, IReadOnlyList<string> parameters, JsBlock body, int depth) {
      this._text.Append("function ");
      if (name is not null)
        this._text.Append(name);

      this._text.Append('(').AppendJoin(", ", parameters).Append(") ");
      this.Braces(body, depth);
    }

    private void Declaration(JsVariableDeclaration declaration, int depth) {
      this._text.Append(declaration.Kind).Append(' ');
      for (var index = 0; index < declaration.Declarators.Count; ++index) {
        if (index > 0)
          this._text.Append(", ");

        var declarator = declaration.Declarators[index];
        this._text.Append(declarator.Name);
        if (declarator.Initializer is not null) {
          this._text.Append(" = ");
          this.Operand(declarator.Initializer, Precedence(declarator.Initializer) > Sequence, depth);
        }
      }
    }

    /// <summary>A switch whose cases each stand on a line; a case whose statements are one block opens it on that line.</summary>
    private void Switch(JsSwitch choice, int depth) {
      this._text.Append("switch (");
      this.Expression(choice.Discriminant, depth);
      this._text.Append(") {\n");
      foreach (var @case in choice.Cases) {
        this.Line(depth + 1);
        if (@case.Test is null) {
          this._text.Append("default:");
        } else {
          this._text.Append("case ");
          this.Expression(@case.Test, depth + 1);
          this._text.Append(':');
        }

        if (@case.Consequent is [JsBlock block]) {
          this._text.Append(' ');
          this.Braces(block, depth + 1);
          this._text.Append('\n');
          continue;
        }

        this._text.Append('\n');
        foreach (var statement in @case.Consequent)
          this.Statement(statement, depth + 2);
      }

      this.Line(depth).Append('}');
    }

    /// <summary>An operand, in parentheses when <paramref name="tight"/> says it binds too loosely to stand there.</summary>
    private void Operand(JsExpression operand, bool tight, int depth) {
      if (tight) {
        this.Expression(operand, depth);
        return;
      }

      this._text.Append('(');
      this.Expression(operand, depth);
      this._text.Append(')');
    }

    /// <summary>Whether an expression may stand where a member is read from it: a primary expression, but no number whose dot would be its fraction.</summary>
    private static bool IsTarget(JsExpression target) => Precedence(target) == Primary && target is not JsNumber;

    public void Expression(JsExpression expression, int depth) {
      switch (expression) {
        case JsNumber number:
          this._text.Append(Spelled(number));
          break;
        case JsBigInt big:
          this._text.Append(Spelled(big));
          break;
        case JsString text:
          this._text.Append('"').Append(JavaScriptLiterals.Escape(text.Value)).Append('"');
          break;
        case JsBoolean truth:
          this._text.Append(truth.Value ? "true" : "false");
          break;
        case JsIdentifier identifier:
          this._text.Append(identifier.Name);
          break;
        case JsMember member:
          this.Operand(member.Target, IsTarget(member.Target), depth);
          this._text.Append('.').Append(member.Property);
          break;
        case JsIndex index:
          this.Operand(index.Target, Precedence(index.Target) == Primary, depth);
          this._text.Append('[');
          this.Expression(index.Index, depth);
          this._text.Append(']');
          break;
        case JsCall call:
          this.Operand(call.Callee, Precedence(call.Callee) == Primary, depth);
          this.Arguments(call.Arguments, depth);
          break;
        case JsNew creation:
          this._text.Append("new ");
          this.Operand(creation.Callee, creation.Callee is JsIdentifier or JsMember, depth);
          this.Arguments(creation.Arguments, depth);
          break;
        case JsUnary unary:
          this._text.Append(unary.Operator);
          if (char.IsAsciiLetter(unary.Operator[^1]))
            this._text.Append(' ');

          this.Operand(unary.Operand, Precedence(unary.Operand) >= Prefix && !Collides(unary.Operator, unary.Operand), depth);
          break;
        case JsUpdate { Prefix: true } update:
          this._text.Append(update.Operator);
          this.Operand(update.Operand, Precedence(update.Operand) == Primary, depth);
          break;
        case JsUpdate update:
          this.Operand(update.Operand, Precedence(update.Operand) == Primary, depth);
          this._text.Append(update.Operator);
          break;
        case JsBinary binary:
          var precedence = Of(binary.Operator);
          var rightAssociative = binary.Operator == "**";
          this.Operand(binary.Left, rightAssociative ? Precedence(binary.Left) > precedence : Precedence(binary.Left) >= precedence, depth);
          this._text.Append(' ').Append(binary.Operator).Append(' ');
          this.Operand(binary.Right, rightAssociative ? Precedence(binary.Right) >= precedence : Precedence(binary.Right) > precedence, depth);
          break;
        case JsAssignment assignment:
          this.Operand(assignment.Left, Precedence(assignment.Left) == Primary, depth);
          this._text.Append(' ').Append(assignment.Operator).Append(' ');
          this.Operand(assignment.Right, Precedence(assignment.Right) >= Conditional, depth);
          break;
        case JsConditional choice:
          this.Operand(choice.Test, Precedence(choice.Test) > Conditional, depth);
          if (choice.BreakBeforeQuestion)
            this.NewLine(depth + 1);
          else
            this._text.Append(' ');

          this._text.Append("? ");
          this.Operand(choice.WhenTrue, Precedence(choice.WhenTrue) >= Conditional, depth);
          if (choice.BreakBeforeColon)
            this.NewLine(depth + 1);
          else
            this._text.Append(' ');

          this._text.Append(": ");
          this.Operand(choice.WhenFalse, Precedence(choice.WhenFalse) >= Conditional, depth);
          break;
        case JsSequence sequence:
          for (var index = 0; index < sequence.Expressions.Count; ++index) {
            if (index > 0)
              this._text.Append(", ");

            this.Operand(sequence.Expressions[index], Precedence(sequence.Expressions[index]) > Sequence, depth);
          }

          break;
        case JsParenthesized parenthesized:
          this._text.Append('(');
          this.Expression(parenthesized.Inner, depth);
          this._text.Append(')');
          break;
        case JsArray array:
          this.Array(array, depth);
          break;
        case JsObject literal:
          this.Object(literal, depth);
          break;
        default:
          var function = (JsFunctionExpression)expression;
          this.Function(function.Name, function.Parameters, function.Body, depth);
          break;
      }
    }

    /// <summary>Whether a prefix operator and its operand would run together into another token: <c>- -x</c> written <c>--x</c>.</summary>
    private static bool Collides(string op, JsExpression operand) => op is "-" or "+" && operand switch {
      JsUnary inner => inner.Operator == op,
      JsUpdate { Prefix: true } inner => inner.Operator[0] == op[0],
      JsNumber number => op == "-" && IsNegative(number.Value),
      JsBigInt big => op == "-" && big.Value.Sign < 0,
      _ => false
    };

    private void Arguments(IReadOnlyList<JsExpression> arguments, int depth) {
      this._text.Append('(');
      for (var index = 0; index < arguments.Count; ++index) {
        if (index > 0)
          this._text.Append(", ");

        this.Operand(arguments[index], Precedence(arguments[index]) > Sequence, depth);
      }

      this._text.Append(')');
    }

    private void Array(JsArray array, int depth) {
      var elements = array.Elements;
      if (!array.Tabulated || elements.Count <= RowLength) {
        this._text.Append('[');
        for (var index = 0; index < elements.Count; ++index) {
          if (index > 0)
            this._text.Append(", ");

          this.Operand(elements[index], Precedence(elements[index]) > Sequence, depth);
        }

        this._text.Append(']');
        return;
      }

      this._text.Append("[\n");
      for (var index = 0; index < elements.Count; ++index) {
        if (index % RowLength == 0)
          this.Line(depth + 1);

        this.Operand(elements[index], Precedence(elements[index]) > Sequence, depth + 1);
        if (index + 1 < elements.Count)
          this._text.Append(index % RowLength == RowLength - 1 ? ",\n" : ", ");
      }

      this._text.Append('\n');
      this.Line(depth).Append(']');
    }

    private void Object(JsObject literal, int depth) {
      if (literal.Properties.Count == 0) {
        this._text.Append("{}");
        return;
      }

      this._text.Append(literal.Multiline ? "{" : "{ ");
      for (var index = 0; index < literal.Properties.Count; ++index) {
        if (index > 0)
          this._text.Append(literal.Multiline ? "," : ", ");

        if (literal.Multiline)
          this.NewLine(depth + 1);

        var property = literal.Properties[index];
        this._text.Append(property.Key);
        if (property.Shorthand)
          continue;

        this._text.Append(": ");
        this.Operand(property.Value, Precedence(property.Value) > Sequence, depth + (literal.Multiline ? 1 : 0));
      }

      if (literal.Multiline)
        this.NewLine(depth);
      else
        this._text.Append(' ');

      this._text.Append('}');
    }

    /// <summary>A number: its spelling while that still denotes it, else the shortest digits that read back as the value.</summary>
    private static string Spelled(JsNumber number) {
      if (number.Raw is { } raw && JavaScriptLiterals.ParseNumber(raw) is { } parsed && BitConverter.DoubleToInt64Bits(parsed) == BitConverter.DoubleToInt64Bits(number.Value))
        return raw;

      var value = number.Value;
      if (double.IsNaN(value))
        return "NaN";

      return double.IsInfinity(value) ? (value < 0 ? "-Infinity" : "Infinity") : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Spelled(JsBigInt big)
      => big.Raw is { } raw && JavaScriptLiterals.ParseBigInt(raw) == big.Value ? raw : big.Value.ToString(CultureInfo.InvariantCulture) + "n";

  }

}

/// <summary>Reads the spelling of a JavaScript numeric literal (ECMA-262 §12.9.3), so that a spelling can be checked against a value.</summary>
public static class JavaScriptLiterals {

  /// <summary>
  /// The text of a string literal without its quotes: a backslash and a quote escaped, a line feed as
  /// <c>\n</c>, and every other code unit outside printable ASCII as <c>\uXXXX</c>, so that a file
  /// holds every string - an unpaired surrogate too - in ASCII.
  /// </summary>
  /// <param name="value">The code units.</param>
  /// <returns>The escaped text.</returns>
  public static string Escape(string value) {
    var text = new System.Text.StringBuilder(value.Length);
    foreach (var unit in value)
      if (unit is '\\' or '"')
        text.Append('\\').Append(unit);
      else if (unit == '\n')
        text.Append("\\n");
      else if (unit is < ' ' or > '~')
        text.Append("\\u").Append(((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
      else
        text.Append(unit);

    return text.ToString();
  }

  /// <summary>The value of a numeric literal, with an optional minus sign in front; <c>NaN</c> and <c>Infinity</c> count as literals.</summary>
  /// <param name="text">The spelling.</param>
  /// <returns>The value, or <see langword="null"/> when the text is no numeric literal.</returns>
  public static double? ParseNumber(string text) {
    var negative = text.StartsWith('-');
    var body = (negative ? text[1..] : text).Replace("_", "", StringComparison.Ordinal);
    double value;
    if (body == "NaN") {
      value = double.NaN;
    } else if (body == "Infinity") {
      value = double.PositiveInfinity;
    } else if (body.Length > 2 && body[0] == '0' && (body[1] | 0x20) is 'x' or 'o' or 'b') {
      var radix = (body[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
      BigInteger whole = 0;
      foreach (var digit in body[2..]) {
        var number = char.IsAsciiDigit(digit) ? digit - '0' : char.IsAsciiLetter(digit) ? (digit | 0x20) - 'a' + 10 : 99;
        if (number >= radix)
          return null;

        whole = whole * radix + number;
      }

      value = (double)whole;
    } else if (body.Length == 0 || !char.IsAsciiDigit(body[0]) && body[0] != '.' || !double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
      return null;
    }

    return negative ? -value : value;
  }

  /// <summary>The value of a <c>BigInt</c> literal, with an optional minus sign in front.</summary>
  /// <param name="text">The spelling, ending in <c>n</c>.</param>
  /// <returns>The value, or <see langword="null"/> when the text is no <c>BigInt</c> literal.</returns>
  public static BigInteger? ParseBigInt(string text) {
    if (!text.EndsWith('n'))
      return null;

    var number = ParseInteger(text[..^1].TrimStart('-'));
    return number is null ? null : text.StartsWith('-') ? -number : number;
  }

  private static BigInteger? ParseInteger(string body) {
    if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      return BigInteger.TryParse("0" + body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : null;

    return body.Length > 0 && body.All(char.IsAsciiDigit) ? BigInteger.Parse(body, CultureInfo.InvariantCulture) : null;
  }

}
