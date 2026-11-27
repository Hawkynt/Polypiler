using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.VisualBasic.Syntax;

namespace Polypiler.Core.Backends.VisualBasic.Printing;

/// <summary>
/// Turns a Visual Basic syntax tree into text: four spaces per level of indentation, CRLF-free LF
/// lines, keywords in the case the Visual Basic editor writes them, and parentheses exactly where the
/// operator precedence of [MS-VBAL] section 5.6.9 needs them. The only place that writes Visual Basic.
/// </summary>
public static class VbPrinter {

  private const string Indent = "    ";

  /// <summary>The binding strength of each binary operator, higher binds tighter ([MS-VBAL] 5.6.9).</summary>
  private static readonly Dictionary<string, int> _binary = new(StringComparer.Ordinal) {
    ["^"] = 14,
    ["*"] = 12, ["/"] = 12,
    ["\\"] = 11,
    ["Mod"] = 10,
    ["+"] = 9, ["-"] = 9,
    ["&"] = 8,
    ["="] = 7, ["<>"] = 7, ["<"] = 7, ["<="] = 7, [">"] = 7, [">="] = 7,
    ["And"] = 5,
    ["Or"] = 4,
    ["Xor"] = 3,
    ["Eqv"] = 2,
    ["Imp"] = 1
  };

  /// <summary>The binding strength of unary minus: below <c>^</c>, above <c>*</c>.</summary>
  private const int Negation = 13;

  /// <summary>The binding strength of <c>Not</c>: below the comparisons, above <c>And</c>.</summary>
  private const int Not = 6;

  /// <summary>The binding strength of an operand that is no operator: a name, a literal, a call.</summary>
  private const int Primary = 15;

  /// <summary>Whether a text is a binary operator of the language.</summary>
  /// <param name="text">The text.</param>
  /// <returns>Whether it is one.</returns>
  public static bool IsBinaryOperator(string text) => _binary.ContainsKey(text);

  /// <summary>The binding strength of a binary operator.</summary>
  /// <param name="text">The operator.</param>
  /// <returns>Its strength, higher binds tighter.</returns>
  /// <exception cref="ArgumentException">The text is no binary operator.</exception>
  public static int Precedence(string text) => _binary.TryGetValue(text, out var value) ? value : throw new ArgumentException($"'{text}' is no binary operator.", nameof(text));

  /// <summary>Prints a module.</summary>
  /// <param name="module">The module.</param>
  /// <returns>Its text, ending with a line break.</returns>
  public static string Print(VbModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var text = new StringBuilder();
    text.Append("Attribute VB_Name = \"").Append(module.Name).Append("\"\n");
    foreach (var line in module.Header)
      text.Append('\'').Append(line.Length == 0 ? "" : " " + line).Append('\n');

    foreach (var declaration in module.Declarations)
      Declaration(text, declaration);

    return text.ToString();
  }

  /// <summary>Prints one declaration, with what stands in front of it.</summary>
  /// <param name="declaration">The declaration.</param>
  /// <returns>Its text, ending with a line break.</returns>
  public static string Print(VbDeclaration declaration) {
    ArgumentNullException.ThrowIfNull(declaration);
    var text = new StringBuilder();
    Declaration(text, declaration);
    return text.ToString();
  }

  /// <summary>Prints statements at the outermost level of indentation.</summary>
  /// <param name="statements">The statements.</param>
  /// <returns>Their text, each line ending with a line break.</returns>
  public static string Print(IReadOnlyList<VbStatement> statements) {
    ArgumentNullException.ThrowIfNull(statements);
    var text = new StringBuilder();
    Statements(text, statements, 0);
    return text.ToString();
  }

  /// <summary>Prints an expression.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>Its text.</returns>
  public static string Print(VbExpression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return Expression(expression);
  }

  private static void Trivia(StringBuilder text, VbTrivia trivia, int depth) {
    for (var line = 0; line < trivia.BlankLines; ++line)
      text.Append('\n');
    foreach (var comment in trivia.Comments)
      text.Append(Pad(depth)).Append('\'').Append(comment.Length == 0 ? "" : " " + comment).Append('\n');
  }

  private static string Pad(int depth) => string.Concat(Enumerable.Repeat(Indent, depth));

  private static string Access(bool isPublic) => isPublic ? "Public" : "Private";

  private static void Declaration(StringBuilder text, VbDeclaration declaration) {
    Trivia(text, declaration.Leading, 0);
    switch (declaration) {
      case VbOption option:
        text.Append("Option ").Append(option.Text).Append('\n');
        break;
      case VbTypeDeclaration type:
        text.Append(Access(type.IsPublic)).Append(" Type ").Append(type.Name).Append('\n');
        foreach (var field in type.Fields)
          text.Append(Indent).Append(Variable(field)).Append('\n');
        text.Append("End Type\n");
        break;
      case VbConst constant:
        text.Append(Access(constant.IsPublic)).Append(" Const ").Append(constant.Name).Append(" As ").Append(constant.Type.Name).Append(" = ").Append(Expression(constant.Value)).Append('\n');
        break;
      case VbModuleVariable variable:
        text.Append(Access(variable.IsPublic)).Append(' ').Append(Variable(variable.Variable)).Append('\n');
        break;
      case VbProcedure procedure:
        var kind = procedure.IsFunction ? "Function" : "Sub";
        text.Append(Access(procedure.IsPublic)).Append(' ').Append(kind).Append(' ').Append(procedure.Name).Append('(')
          .Append(string.Join(", ", procedure.Parameters.Select(Parameter))).Append(')');
        if (procedure.Returns is { } returns)
          text.Append(" As ").Append(returns.Name).Append(procedure.ReturnsArray ? "()" : "");
        text.Append('\n');
        Statements(text, procedure.Body, 1);
        text.Append("End ").Append(kind).Append('\n');
        break;
      default:
        throw new ArgumentException($"The declaration '{declaration.GetType().Name}' cannot be printed.", nameof(declaration));
    }
  }

  private static string Parameter(VbParameter parameter) {
    var passing = parameter.ByVal ? "ByVal " : parameter.IsArray ? "" : "ByRef ";
    return passing + parameter.Name + (parameter.IsArray ? "()" : "") + " As " + parameter.Type.Name;
  }

  private static string Variable(VbVariable variable) {
    var dimensions = variable.Bounds.Count > 0 ? "(" + string.Join(", ", variable.Bounds.Select(Bounds)) + ")" : variable.IsArray ? "()" : "";
    return variable.Name + dimensions + " As " + variable.Type.Name;
  }

  private static string Bounds(VbBounds bounds) => Expression(bounds.Lower) + " To " + Expression(bounds.Upper);

  private static void Statements(StringBuilder text, IReadOnlyList<VbStatement> statements, int depth) {
    foreach (var statement in statements)
      Statement(text, statement, depth);
  }

  private static void Line(StringBuilder text, int depth, string line) => text.Append(Pad(depth)).Append(line).Append('\n');

  private static void Statement(StringBuilder text, VbStatement statement, int depth) {
    Trivia(text, statement.Leading, depth);
    switch (statement) {
      case VbDim dim:
        Line(text, depth, "Dim " + string.Join(", ", dim.Variables.Select(Variable)));
        break;
      case VbStatic variables:
        Line(text, depth, "Static " + string.Join(", ", variables.Variables.Select(Variable)));
        break;
      case VbAssign assign:
        Line(text, depth, Expression(assign.Target) + " = " + Expression(assign.Value));
        break;
      case VbCallStatement call:
        var arguments = call.Arguments.Select(Expression).ToList();
        // "F (a) + b" would read as the call F(a) followed by "+ b": such a call is written with Call.
        if (arguments.Count > 0 && arguments[0].StartsWith('('))
          Line(text, depth, "Call " + Operand(call.Target, Primary) + "(" + string.Join(", ", arguments) + ")");
        else
          Line(text, depth, Expression(call.Target) + (arguments.Count == 0 ? "" : " " + string.Join(", ", arguments)));
        break;
      case VbIf conditional:
        for (var index = 0; index < conditional.Branches.Count; ++index) {
          var branch = conditional.Branches[index];
          Line(text, depth, (index == 0 ? "If " : "ElseIf ") + Expression(branch.Condition) + " Then");
          Statements(text, branch.Body, depth + 1);
        }

        if (conditional.Else is { } otherwise) {
          Line(text, depth, "Else");
          Statements(text, otherwise, depth + 1);
        }

        Line(text, depth, "End If");
        break;
      case VbSelect select:
        Line(text, depth, "Select Case " + Expression(select.Value));
        foreach (var section in select.Cases) {
          Line(text, depth + 1, "Case " + string.Join(", ", section.Clauses.Select(Clause)));
          Statements(text, section.Body, depth + 2);
        }

        if (select.Else is { } fallback) {
          Line(text, depth + 1, "Case Else");
          Statements(text, fallback, depth + 2);
        }

        Line(text, depth, "End Select");
        break;
      case VbFor loop:
        Line(text, depth, "For " + loop.Variable + " = " + Expression(loop.Start) + " To " + Expression(loop.End) + (loop.Step is { } step ? " Step " + Expression(step) : ""));
        Statements(text, loop.Body, depth + 1);
        Line(text, depth, "Next " + loop.Variable);
        break;
      case VbDoLoop loop:
        Line(text, depth, "Do" + Test(loop.Pre));
        Statements(text, loop.Body, depth + 1);
        Line(text, depth, "Loop" + Test(loop.Post));
        break;
      case VbExit exit:
        Line(text, depth, "Exit " + exit.Kind);
        break;
      case VbGoTo jump:
        Line(text, depth, "GoTo " + jump.Label);
        break;
      case VbLabel label:
        text.Append(label.Name).Append(":\n");
        break;
      case VbOnError onError:
        Line(text, depth, onError.Label is null ? "On Error Resume Next" : "On Error GoTo " + onError.Label);
        break;
      case VbResume resume:
        Line(text, depth, resume.Target.Length == 0 ? "Resume" : "Resume " + resume.Target);
        break;
      case VbReDim redim:
        Line(text, depth, "ReDim " + (redim.Preserve ? "Preserve " : "") + Expression(redim.Target) + "(" + Bounds(redim.Bounds) + ")");
        break;
      case VbErase erase:
        Line(text, depth, "Erase " + Expression(erase.Target));
        break;
      default:
        throw new ArgumentException($"The statement '{statement.GetType().Name}' cannot be printed.", nameof(statement));
    }
  }

  private static string Test(VbLoopTest? test) => test is null ? "" : (test.Until ? " Until " : " While ") + Expression(test.Condition);

  private static string Clause(VbCaseClause clause) {
    if (clause.Is is { } comparison)
      return "Is " + comparison + " " + Expression(clause.Value);

    return clause.Upper is { } upper ? Expression(clause.Value) + " To " + Expression(upper) : Expression(clause.Value);
  }

  /// <summary>How tightly an expression binds as an operand.</summary>
  private static int Strength(VbExpression expression) => expression switch {
    VbBinary binary => _binary[binary.Operator],
    VbUnary { Operator: "Not" } => Not,
    VbUnary => Negation,
    VbInteger { Value: < 0, Hex: false } => Negation,
    VbFloat { Value: < 0 } => Negation,
    VbFloat { Value: 0 } zero when double.IsNegative(zero.Value) => Negation,
    _ => Primary
  };

  private static string Operand(VbExpression operand, int needed) {
    var text = Expression(operand);
    return Strength(operand) < needed ? "(" + text + ")" : text;
  }

  private static string Expression(VbExpression expression) {
    switch (expression) {
      case VbInteger integer:
        return Integer(integer);
      case VbFloat number:
        return Float(number);
      case VbString text:
        return "\"" + text.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
      case VbBoolean boolean:
        return boolean.Value ? "True" : "False";
      case VbName name:
        return name.Name;
      case VbMember member:
        return Operand(member.Target, Primary) + "." + member.Name;
      case VbCall call:
        return Operand(call.Target, Primary) + "(" + string.Join(", ", call.Arguments.Select(Expression)) + ")";
      case VbUnary { Operator: "Not" } not:
        return "Not " + Operand(not.Operand, Not);
      case VbUnary negation:
        // A negation of a negation, or of a negative literal, is parenthesized: "--x" is no Visual Basic.
        var inner = Operand(negation.Operand, Negation);
        return negation.Operator + (inner.StartsWith('-') ? "(" + inner + ")" : inner);
      case VbBinary binary:
        // A right operand that starts with a minus sign is parenthesized: "a - -b" is not read alike everywhere.
        var strength = _binary[binary.Operator];
        var right = Strength(binary.Right) == Negation ? "(" + Expression(binary.Right) + ")" : Operand(binary.Right, strength + 1);
        return Operand(binary.Left, strength) + " " + binary.Operator + " " + right;
      default:
        throw new ArgumentException($"The expression '{expression.GetType().Name}' cannot be printed.", nameof(expression));
    }
  }

  private static string Integer(VbInteger integer) {
    if (!integer.Hex)
      return integer.Value.ToString(CultureInfo.InvariantCulture) + integer.Suffix;

    // Without a type character the digits decide the type, so a Long is written with all eight; with
    // one, the fewest digits are written.
    var digits = integer.Suffix switch {
      "^" => integer.Value.ToString("X", CultureInfo.InvariantCulture),
      "&" => ((uint)integer.Value).ToString("X", CultureInfo.InvariantCulture),
      _ => ((int)integer.Value).ToString("X8", CultureInfo.InvariantCulture)
    };
    return "&H" + digits + integer.Suffix;
  }

  /// <summary>
  /// A finite number as the shortest digits that read back as the same value, in the form Visual Basic
  /// reads: an exponent as <c>E+n</c>/<c>E-n</c>, a <c>Double</c> whose digits alone would be an integer
  /// with <c>#</c>, a <c>Single</c> always with <c>!</c>.
  /// </summary>
  private static string Float(VbFloat number) {
    if (!double.IsFinite(number.Value))
      throw new ArgumentException("Visual Basic has no literal for an infinity or a NaN.", nameof(number));

    var digits = number.IsSingle ? ((float)number.Value).ToString("R", CultureInfo.InvariantCulture) : number.Value.ToString("R", CultureInfo.InvariantCulture);
    var exponent = digits.IndexOf('E', StringComparison.Ordinal);
    if (exponent >= 0) {
      var mantissa = digits[..exponent];
      var power = digits[(exponent + 1)..];
      if (!power.StartsWith('-') && !power.StartsWith('+'))
        power = "+" + power;
      digits = mantissa + "E" + power;
    }

    if (number.IsSingle)
      return digits + "!";

    return digits.Contains('.', StringComparison.Ordinal) || exponent >= 0 ? digits : digits + "#";
  }

}
