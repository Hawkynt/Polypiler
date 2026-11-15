using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Polypiler.Core.Backends.Python.Syntax;

namespace Polypiler.Core.Backends.Python.Printing;

/// <summary>
/// Turns a Python syntax tree into text: four spaces per level, double-quoted strings, LF line
/// endings and a final newline (PEP 8). Parentheses are put exactly where Python's precedence needs
/// them (The Python Language Reference, §6.17 "Operator precedence"): an operand of a comparison that
/// is a comparison itself, the operand of a unary operator that binds looser, the left operand of
/// <c>**</c>. A signature longer than 88 columns is written one parameter per line.
/// </summary>
public static class PythonPrinter {

  /// <summary>The width a signature has to stay within to be written on one line.</summary>
  public const int LineWidth = 88;

  private const string Indent = "    ";

  /// <summary>The binding strength of each kind of expression, loosest first.</summary>
  private enum Precedence {
    Conditional = 1,
    Or = 2,
    And = 3,
    Not = 4,
    Comparison = 5,
    BitOr = 6,
    BitXor = 7,
    BitAnd = 8,
    Shift = 9,
    Additive = 10,
    Multiplicative = 11,
    Unary = 12,
    Power = 13,
    Primary = 14,
    Atom = 15
  }

  /// <summary>Prints a module.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The text of the file.</returns>
  public static string Print(PyModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var text = new StringBuilder();
    foreach (var line in module.Header)
      text.Append(Comment(line)).Append('\n');

    Statements(module.Body, 0, text, module.Header.Count > 0);
    return text.ToString();
  }

  /// <summary>Prints one statement at the level of the module, without the lines in front of it.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>Its text, ending with a newline.</returns>
  public static string Print(PyStatement statement) {
    ArgumentNullException.ThrowIfNull(statement);
    var text = new StringBuilder();
    Statement(statement with { Leading = PyTrivia.None }, 0, text);
    return text.ToString();
  }

  /// <summary>Prints an expression as it stands on its own.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>Its text.</returns>
  public static string Print(PyExpression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return Expression(expression, Precedence.Conditional, 0);
  }

  private static string Comment(string line) => line.Length == 0 ? "#" : "# " + line;

  private static void Statements(IReadOnlyList<PyStatement> statements, int level, StringBuilder text, bool afterHeader = false) {
    if (statements.Count == 0) {
      text.Append(Pad(level)).Append("pass\n");
      return;
    }

    for (var index = 0; index < statements.Count; ++index) {
      var statement = statements[index];
      // The first statement of a file follows its header after the lines the statement asks for.
      if (index > 0 || afterHeader)
        text.Append('\n', statement.Leading.BlankLines);

      foreach (var comment in statement.Leading.Comments)
        text.Append(Pad(level)).Append(Comment(comment)).Append('\n');

      Statement(statement, level, text);
    }
  }

  private static string Pad(int level) => string.Concat(Enumerable.Repeat(Indent, level));

  private static void Statement(PyStatement statement, int level, StringBuilder text) {
    var pad = Pad(level);
    switch (statement) {
      case PyExpressionStatement expression:
        text.Append(pad).Append(Expression(expression.Expression, Precedence.Conditional, level)).Append('\n');
        break;
      case PyAssign assign:
        text.Append(pad).Append(Expression(assign.Target, Precedence.Primary, level));
        if (assign.Annotation is { } annotation)
          text.Append(": ").Append(Expression(annotation, Precedence.Conditional, level));

        text.Append(" = ").Append(Expression(assign.Value, Precedence.Conditional, level)).Append('\n');
        break;
      case PyAnnotation declaration:
        text.Append(pad).Append(declaration.Name).Append(": ").Append(Expression(declaration.Annotation, Precedence.Conditional, level)).Append('\n');
        break;
      case PyReturn { Value: null }:
        text.Append(pad).Append("return\n");
        break;
      case PyReturn @return:
        text.Append(pad).Append("return ").Append(Expression(@return.Value!, Precedence.Conditional, level)).Append('\n');
        break;
      case PyPass:
        text.Append(pad).Append("pass\n");
        break;
      case PyBreak:
        text.Append(pad).Append("break\n");
        break;
      case PyContinue:
        text.Append(pad).Append("continue\n");
        break;
      case PyRaise raise:
        text.Append(pad).Append("raise ").Append(Expression(raise.Exception, Precedence.Conditional, level)).Append('\n');
        break;
      case PyGlobal global:
        text.Append(pad).Append("global ").Append(string.Join(", ", global.Names)).Append('\n');
        break;
      case PyImport import:
        text.Append(pad).Append("import ").Append(import.Module).Append('\n');
        break;
      case PyImportFrom import:
        text.Append(pad).Append("from ").Append(import.Module).Append(" import ").Append(string.Join(", ", import.Names)).Append('\n');
        break;
      case PyIf conditional:
        If(conditional, level, text, "if");
        break;
      case PyWhile loop:
        text.Append(pad).Append("while ").Append(Expression(loop.Test, Precedence.Conditional, level)).Append(":\n");
        Statements(loop.Body, level + 1, text);
        break;
      case PyFor loop:
        text.Append(pad).Append("for ").Append(loop.Variable).Append(" in ").Append(Expression(loop.Iterable, Precedence.Conditional, level)).Append(":\n");
        Statements(loop.Body, level + 1, text);
        break;
      case PyMatchStatement match:
        text.Append(pad).Append("match ").Append(Expression(match.Subject, Precedence.Conditional, level)).Append(":\n");
        foreach (var @case in match.Cases) {
          text.Append(Pad(level + 1)).Append("case ");
          text.Append(@case.Patterns.Count == 0 ? "_" : string.Join(" | ", @case.Patterns.Select(pattern => Expression(pattern, Precedence.Conditional, level))));
          if (@case.Guard is { } guard)
            text.Append(" if ").Append(Expression(guard, Precedence.Conditional, level));

          text.Append(":\n");
          Statements(@case.Body, level + 2, text);
        }

        break;
      case PyFunctionDef function:
        Function(function, level, text);
        break;
      default:
        throw new ArgumentException($"The printer has no form for the statement {statement.GetType().Name}.", nameof(statement));
    }
  }

  private static void If(PyIf conditional, int level, StringBuilder text, string keyword) {
    text.Append(Pad(level)).Append(keyword).Append(' ').Append(Expression(conditional.Test, Precedence.Conditional, level)).Append(":\n");
    Statements(conditional.Body, level + 1, text);
    if (conditional.Else is [PyIf { Leading.BlankLines: 0, Leading.Comments.Count: 0 } chained]) {
      If(chained, level, text, "elif");
      return;
    }

    if (conditional.Else.Count == 0)
      return;

    text.Append(Pad(level)).Append("else:\n");
    Statements(conditional.Else, level + 1, text);
  }

  private static void Function(PyFunctionDef function, int level, StringBuilder text) {
    var pad = Pad(level);
    var parameters = function.Parameters.Select(parameter => parameter.Name + ": " + Expression(parameter.Annotation, Precedence.Conditional, level)).ToList();
    var returns = " -> " + Expression(function.Returns, Precedence.Conditional, level) + ":";
    var line = $"{pad}def {function.Name}({string.Join(", ", parameters)}){returns}";
    if (line.Length <= LineWidth || parameters.Count == 0) {
      text.Append(line).Append('\n');
    } else {
      text.Append(pad).Append("def ").Append(function.Name).Append("(\n");
      foreach (var parameter in parameters)
        text.Append(Pad(level + 1)).Append(parameter).Append(",\n");

      text.Append(pad).Append(')').Append(returns).Append('\n');
    }

    Statements(function.Body, level + 1, text);
  }

  private static Precedence Of(PyExpression expression) => expression switch {
    PyInt integer => integer.Value.Sign < 0 ? Precedence.Unary : Precedence.Atom,
    PyFloat number => double.IsNegative(number.Value) ? Precedence.Unary : Precedence.Atom,
    PyConditional => Precedence.Conditional,
    PyBoolOp { Operator: "or" } => Precedence.Or,
    PyBoolOp => Precedence.And,
    PyUnary { Operator: "not" } => Precedence.Not,
    PyUnary => Precedence.Unary,
    PyCompare => Precedence.Comparison,
    PyBinary binary => OfOperator(binary.Operator),
    PyAttribute or PySubscript or PyCall => Precedence.Primary,
    _ => Precedence.Atom
  };

  private static Precedence OfOperator(string op) => op switch {
    "|" => Precedence.BitOr,
    "^" => Precedence.BitXor,
    "&" => Precedence.BitAnd,
    "<<" or ">>" => Precedence.Shift,
    "+" or "-" => Precedence.Additive,
    "*" or "/" or "//" or "%" => Precedence.Multiplicative,
    "**" => Precedence.Power,
    _ => throw new ArgumentException($"'{op}' is no binary operator of Python.", nameof(op))
  };

  /// <summary>An expression as an operand that has to bind at least as tightly as <paramref name="least"/>; in parentheses otherwise.</summary>
  private static string Expression(PyExpression expression, Precedence least, int level) {
    var written = Bare(expression, level);
    return Of(expression) < least ? "(" + written + ")" : written;
  }

  private static string Bare(PyExpression expression, int level) {
    switch (expression) {
      case PyInt integer:
        return Integer(integer);
      case PyFloat number:
        return Float(number.Value);
      case PyBool truth:
        return truth.Value ? "True" : "False";
      case PyNone:
        return "None";
      case PyString text:
        return Quote(text.Value);
      case PyName name:
        return name.Name;
      case PyAttribute attribute:
        // A number before a dot would be read as part of the number.
        var target = attribute.Target is PyInt or PyFloat ? "(" + Bare(attribute.Target, level) + ")" : Expression(attribute.Target, Precedence.Primary, level);
        return target + "." + attribute.Name;
      case PySubscript subscript:
        return Expression(subscript.Target, Precedence.Primary, level) + "[" + Expression(subscript.Index, Precedence.Conditional, level) + "]";
      case PyCall call:
        return Expression(call.Callee, Precedence.Primary, level) + "(" + string.Join(", ", call.Arguments.Select(argument => Expression(argument, Precedence.Conditional, level))) + ")";
      case PyUnary { Operator: "not" } negation:
        return "not " + Expression(negation.Operand, Precedence.Not, level);
      case PyUnary unary:
        return unary.Operator + Expression(unary.Operand, Precedence.Power, level);
      case PyBinary { Operator: "**" } power:
        return Expression(power.Left, Precedence.Primary, level) + " ** " + Expression(power.Right, Precedence.Unary, level);
      case PyBinary binary:
        var strength = OfOperator(binary.Operator);
        return Operand(binary.Left, binary.Operator, strength, level) + " " + binary.Operator + " " + Operand(binary.Right, binary.Operator, strength + 1, level);
      case PyCompare compare:
        if (compare.Operands.Count != compare.Operators.Count + 1 || compare.Operators.Count == 0)
          throw new ArgumentException("A comparison has one operator fewer than operands, and at least one.", nameof(expression));

        var parts = new StringBuilder(Expression(compare.Operands[0], Precedence.BitOr, level));
        for (var index = 0; index < compare.Operators.Count; ++index)
          parts.Append(' ').Append(compare.Operators[index]).Append(' ').Append(Expression(compare.Operands[index + 1], Precedence.BitOr, level));

        return parts.ToString();
      case PyBoolOp boolOp:
        var own = boolOp.Operator == "or" ? Precedence.Or : Precedence.And;
        return Expression(boolOp.Left, own, level) + " " + boolOp.Operator + " " + Expression(boolOp.Right, own + 1, level);
      case PyConditional conditional:
        return Expression(conditional.WhenTrue, Precedence.Or, level) + " if " + Expression(conditional.Test, Precedence.Or, level) + " else " + Expression(conditional.WhenFalse, Precedence.Conditional, level);
      case PyNamedExpression named:
        return "(" + named.Name + " := " + Expression(named.Value, Precedence.Conditional, level) + ")";
      case PyTuple tuple:
        return tuple.Elements.Count == 1 ? "(" + Expression(tuple.Elements[0], Precedence.Conditional, level) + ",)" : Display("(", ")", tuple.Elements, tuple.Tabulated, level);
      case PyList list:
        return Display("[", "]", list.Elements, list.Tabulated, level);
      default:
        throw new ArgumentException($"The printer has no form for the expression {expression.GetType().Name}.", nameof(expression));
    }
  }

  /// <summary>
  /// An operand of a binary operator. An operand of a bitwise or shift operator that is a binary
  /// operation of another operator is put in parentheses even where precedence does not need them,
  /// so that <c>((x + y) &amp; 0xFF) ^ 0x80</c> reads as it computes (PEP 8's advice for mixed
  /// operators); the parentheses change nothing.
  /// </summary>
  private static string Operand(PyExpression operand, string op, Precedence least, int level)
    => op is "&" or "|" or "^" or "<<" or ">>" && operand is PyBinary inner && inner.Operator != op
      ? "(" + Bare(operand, level) + ")"
      : Expression(operand, least, level);

  /// <summary>The elements of a tuple or list between their brackets: on one line, or eight to a line when it is a table of more than eight.</summary>
  private static string Display(string open, string close, IReadOnlyList<PyExpression> elements, bool tabulated, int level) {
    var written = elements.Select(element => Expression(element, Precedence.Conditional, level)).ToList();
    if (!tabulated || written.Count <= 8)
      return open + string.Join(", ", written) + close;

    var text = new StringBuilder(open).Append('\n');
    for (var start = 0; start < written.Count; start += 8)
      text.Append(Pad(level + 1)).Append(string.Join(", ", written.Skip(start).Take(8))).Append(",\n");

    return text.Append(Pad(level)).Append(close).ToString();
  }

  /// <summary>An integer: its spelling while that still denotes the value, its decimal digits otherwise.</summary>
  private static string Integer(PyInt integer) {
    if (integer.Raw is { } raw && PythonLiterals.ParseInteger(raw) == integer.Value)
      return raw;

    return integer.Value.ToString(CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// A finite double as the shortest decimal digits that read back as it, always with a point or an
  /// exponent so that Python reads a float.
  /// </summary>
  /// <exception cref="ArgumentException">The value is an infinity or a NaN, which Python has no literal for.</exception>
  public static string Float(double value) {
    if (!double.IsFinite(value))
      throw new ArgumentException("Python has no literal for an infinity or a NaN.", nameof(value));

    var digits = value.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
    if (digits.StartsWith("-", StringComparison.Ordinal) || (value == 0 && double.IsNegative(value)))
      digits = "-" + digits.TrimStart('-');

    return digits.Contains('.') || digits.Contains('e') ? digits : digits + ".0";
  }

  /// <summary>A string as a double-quoted literal: printable ASCII as it is, everything else escaped.</summary>
  public static string Quote(string value) {
    var text = new StringBuilder("\"");
    foreach (var unit in value) {
      text.Append(unit switch {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        < ' ' or (> '~' and <= 'ÿ') => $"\\x{(int)unit:x2}",
        > 'ÿ' => $"\\u{(int)unit:x4}",
        _ => unit.ToString()
      });
    }

    return text.Append('"').ToString();
  }

}

/// <summary>The reading of Python's number literals that the printer and the reader share.</summary>
public static class PythonLiterals {

  /// <summary>
  /// The value of an integer literal: decimal, or hexadecimal, octal or binary with its prefix, with
  /// <c>_</c> between digits; <see langword="null"/> for anything else.
  /// </summary>
  /// <param name="text">The literal, without a sign.</param>
  /// <returns>The value.</returns>
  public static BigInteger? ParseInteger(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var digits = text.Replace("_", "", StringComparison.Ordinal);
    if (text.StartsWith('_') || text.EndsWith('_') || text.Contains("__", StringComparison.Ordinal) || digits.Length == 0)
      return null;

    // A prefix without digits, "0x", falls through to the decimal reading, which refuses the letter.
    var (radix, body) = digits.Length > 2 && digits[0] == '0' ? char.ToLowerInvariant(digits[1]) switch {
      'x' => (16, digits[2..]),
      'o' => (8, digits[2..]),
      'b' => (2, digits[2..]),
      _ => (10, digits)
    } : (10, digits);

    // Python reads no decimal literal with a leading zero but zero itself.
    if (radix == 10 && body.Length > 1 && body[0] == '0' && body.Any(digit => digit != '0'))
      return null;

    var value = BigInteger.Zero;
    foreach (var digit in body) {
      var place = char.IsAsciiDigit(digit) ? digit - '0' : char.IsAsciiLetter(digit) ? char.ToLowerInvariant(digit) - 'a' + 10 : 99;
      if (place >= radix)
        return null;

      value = value * radix + place;
    }

    return value;
  }

}
