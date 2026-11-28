using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Kotlin.Syntax;

namespace Polypiler.Core.Backends.Kotlin.Printing;

/// <summary>
/// Turns a Kotlin syntax tree into text, the layout of the Kotlin coding conventions: four spaces, LF,
/// a final newline, one statement per line, a blank line between the declarations of a file, the
/// opening brace on the line that opens the block. Parentheses stand exactly where the precedence of
/// the Kotlin grammar needs them (kotlinlang.org/spec, "Expressions": postfix, prefix, type operators,
/// multiplicative, additive, range, infix function, elvis, named checks, comparison, equality,
/// conjunction, disjunction), and around an operand of an infix function that is itself an operator
/// of another kind, so that <c>(x + y) shl 2</c> reads as it computes: every infix function binds
/// alike and from the left, which is not the order C gives <c>&amp;</c>, <c>|</c> and <c>&lt;&lt;</c>.
/// </summary>
public static class KotlinPrinter {

  private const string Indent = "    ";

  /// <summary>The infix functions the backend writes, which share one level of precedence.</summary>
  public static IReadOnlySet<string> InfixFunctions { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "shl", "shr", "ushr", "and", "or", "xor", "until", "downTo", "step"
  };

  /// <summary>The level of precedence of a binary operator; higher binds tighter.</summary>
  /// <param name="op">The operator.</param>
  /// <returns>The level.</returns>
  /// <exception cref="ArgumentException">The operator is not one the tree has.</exception>
  public static int Precedence(string op) => op switch {
    "*" or "/" or "%" => 12,
    "+" or "-" => 11,
    ".." or "..<" => 10,
    _ when InfixFunctions.Contains(op) => 9,
    "?:" => 8,
    "in" or "!in" => 7,
    "<" or ">" or "<=" or ">=" => 6,
    "==" or "!=" or "===" or "!==" => 5,
    "&&" => 4,
    "||" => 3,
    _ => throw new ArgumentException($"'{op}' is no binary operator of the Kotlin tree.", nameof(op))
  };

  private const int Postfix = 15;
  private const int PrefixLevel = 14;
  private const int TypeOperator = 13;
  private const int NamedCheck = 7;
  private const int Lowest = 1;

  /// <summary>The level of an expression.</summary>
  private static int Level(KtExpression expression) => expression switch {
    KtBinary binary => Precedence(binary.Operator),
    KtPrefix => PrefixLevel,
    KtAs => TypeOperator,
    KtIs => NamedCheck,
    KtIfExpression or KtThrowExpression or KtLambda => Lowest,
    _ => Postfix
  };

  /// <summary>Prints a file.</summary>
  /// <param name="file">The file.</param>
  /// <returns>The text, LF line endings and a final newline.</returns>
  public static string Print(KtFile file) {
    ArgumentNullException.ThrowIfNull(file);
    var text = new StringBuilder();
    foreach (var line in file.Header)
      text.Append(line.Length == 0 ? "//" : "// " + line).Append('\n');

    // Every section after the first is set off by a blank line.
    void Section() {
      if (text.Length > 0)
        text.Append('\n');
    }

    if (file.Suppressions.Count > 0) {
      Section();
      text.Append("@file:Suppress(").Append(string.Join(", ", file.Suppressions.Select(Quoted))).Append(")\n");
    }

    if (file.Package is { } package) {
      Section();
      text.Append("package ").Append(package).Append('\n');
    }

    if (file.Imports.Count > 0) {
      Section();
      foreach (var import in file.Imports)
        text.Append("import ").Append(import).Append('\n');
    }

    foreach (var declaration in file.Declarations) {
      Section();
      Declaration(text, declaration, 0);
    }

    return text.ToString();
  }

  /// <summary>Prints one declaration, as it stands at the level of a file.</summary>
  /// <param name="declaration">The declaration.</param>
  /// <returns>The text with a final newline.</returns>
  public static string Print(KtDeclaration declaration) {
    ArgumentNullException.ThrowIfNull(declaration);
    var text = new StringBuilder();
    Declaration(text, declaration, 0);
    return text.ToString();
  }

  /// <summary>Prints one statement at the outermost level of indentation.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The text with a final newline.</returns>
  public static string Print(KtStatement statement) {
    ArgumentNullException.ThrowIfNull(statement);
    var text = new StringBuilder();
    Statement(text, statement, 0);
    return text.ToString();
  }

  /// <summary>Prints an expression on one line.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The text.</returns>
  public static string Print(KtExpression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var text = new StringBuilder();
    Expression(text, expression, 0, 0);
    return text.ToString();
  }

  /// <summary>Prints a type.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The text.</returns>
  public static string Print(KtType type) {
    ArgumentNullException.ThrowIfNull(type);
    var text = type switch {
      KtNamedType named => named.Arguments.Count == 0 ? named.Name : $"{named.Name}<{string.Join(", ", named.Arguments.Select(Print))}>",
      KtFunctionType function => $"({string.Join(", ", function.Parameters.Select(Print))}) -> {Print(function.Result)}",
      _ => throw new ArgumentException($"'{type.GetType().Name}' is no type of the Kotlin tree.", nameof(type))
    };
    return !type.Nullable ? text : type is KtFunctionType ? $"({text})?" : text + "?";
  }

  #region Declarations

  private static void Pad(StringBuilder text, int depth) {
    for (var level = 0; level < depth; ++level)
      text.Append(Indent);
  }

  private static void Documentation(StringBuilder text, KtDeclaration declaration, int depth) {
    if (declaration.Documentation.Count == 0)
      return;

    Pad(text, depth);
    text.Append("/**\n");
    foreach (var line in declaration.Documentation) {
      Pad(text, depth);
      text.Append(line.Length == 0 ? " *" : " * " + line).Append('\n');
    }

    Pad(text, depth);
    text.Append(" */\n");
  }

  private static void Modifiers(StringBuilder text, KtDeclaration declaration) {
    foreach (var modifier in declaration.Modifiers)
      text.Append(modifier).Append(' ');
  }

  private static void Declaration(StringBuilder text, KtDeclaration declaration, int depth) {
    Documentation(text, declaration, depth);
    Pad(text, depth);
    Modifiers(text, declaration);
    switch (declaration) {
      case KtFunction function:
        Function(text, function, depth);
        break;
      case KtProperty property:
        text.Append(property.Mutable ? "var " : "val ").Append(property.Name);
        if (property.Type is { } propertyType)
          text.Append(": ").Append(Print(propertyType));

        if (property.Initializer is { } value) {
          text.Append(" = ");
          Expression(text, value, 0, depth);
        }

        text.Append('\n');
        break;
      case KtClass type:
        Class(text, type, depth);
        break;
      case KtConstructor constructor:
        text.Append("constructor(").Append(Parameters(constructor.Parameters, depth)).Append(')');
        if (constructor.Delegation is { } delegation) {
          text.Append(" : ").Append(delegation).Append('(');
          Arguments(text, constructor.Arguments, depth);
          text.Append(')');
        }

        text.Append(' ');
        Block(text, constructor.Body, depth);
        text.Append('\n');
        break;
      case KtInitializer initializer:
        text.Append("init ");
        Block(text, initializer.Body, depth);
        text.Append('\n');
        break;
      default:
        throw new ArgumentException($"'{declaration.GetType().Name}' is no declaration of the Kotlin tree.", nameof(declaration));
    }
  }

  private static string Parameters(IReadOnlyList<KtParameter> parameters, int depth) => string.Join(", ", parameters.Select(parameter => Parameter(parameter, depth)));

  private static string Parameter(KtParameter parameter, int depth) {
    var text = new StringBuilder();
    if (parameter.Property is { } property)
      text.Append(property).Append(' ');

    text.Append(parameter.Name);
    if (parameter.Type is { } type)
      text.Append(": ").Append(Print(type));

    if (parameter.Default is { } value) {
      text.Append(" = ");
      Expression(text, value, 0, depth);
    }

    return text.ToString();
  }

  private static void Function(StringBuilder text, KtFunction function, int depth) {
    text.Append("fun ").Append(function.Name).Append('(').Append(Parameters(function.Parameters, depth)).Append(')');
    if (function.Result is { } result)
      text.Append(": ").Append(Print(result));

    if (function.Expression is { } expression) {
      text.Append(" = ");
      Expression(text, expression, 0, depth);
      text.Append('\n');
      return;
    }

    if (function.Body is null) {
      text.Append('\n');
      return;
    }

    text.Append(' ');
    Block(text, function.Body, depth);
    text.Append('\n');
  }

  private static void Class(StringBuilder text, KtClass type, int depth) {
    text.Append(type.Kind).Append(' ').Append(type.Name);
    if (type.Parameters is { } parameters)
      text.Append('(').Append(Parameters(parameters, depth)).Append(')');

    if (type.SuperTypes.Count > 0) {
      text.Append(" : ");
      for (var index = 0; index < type.SuperTypes.Count; ++index) {
        if (index > 0)
          text.Append(", ");

        var super = type.SuperTypes[index];
        text.Append(Print(super.Type));
        if (super.Arguments is { } arguments) {
          text.Append('(');
          Arguments(text, arguments, depth);
          text.Append(')');
        }
      }
    }

    if (type.Members.Count == 0 && type.Entries.Count == 0) {
      text.Append(type.Kind is "class" or "enum class" && type.Parameters is not null ? "\n" : " {}\n");
      return;
    }

    text.Append(" {\n");
    for (var index = 0; index < type.Entries.Count; ++index) {
      var entry = type.Entries[index];
      Pad(text, depth + 1);
      text.Append(entry.Name);
      if (entry.Arguments.Count > 0) {
        text.Append('(');
        Arguments(text, entry.Arguments, depth + 1);
        text.Append(')');
      }

      text.Append(index < type.Entries.Count - 1 ? ",\n" : type.Members.Count > 0 ? ";\n" : "\n");
    }

    for (var index = 0; index < type.Members.Count; ++index) {
      if (index > 0 || type.Entries.Count > 0)
        text.Append('\n');

      Declaration(text, type.Members[index], depth + 1);
    }

    Pad(text, depth);
    text.Append("}\n");
  }

  #endregion

  #region Statements

  /// <summary>Writes <c>{</c>, the statements one level deeper and <c>}</c>, without a final newline; <c>{}</c> for none.</summary>
  private static void Block(StringBuilder text, KtBlock block, int depth) {
    if (block.Statements.Count == 0) {
      text.Append("{}");
      return;
    }

    text.Append("{\n");
    foreach (var statement in block.Statements)
      Statement(text, statement, depth + 1);

    Pad(text, depth);
    text.Append('}');
  }

  private static string LabelOf(string? label) => label is null ? "" : label + "@ ";

  private static void Statement(StringBuilder text, KtStatement statement, int depth) {
    Pad(text, depth);
    switch (statement) {
      case KtVariable variable:
        text.Append(variable.Mutable ? "var " : "val ").Append(variable.Name);
        if (variable.Type is { } type)
          text.Append(": ").Append(Print(type));

        if (variable.Initializer is { } initializer) {
          text.Append(" = ");
          Expression(text, initializer, 0, depth);
        }

        break;
      case KtAssignment assignment:
        Expression(text, assignment.Target, Postfix, depth);
        text.Append(' ').Append(assignment.Operator).Append(' ');
        Expression(text, assignment.Value, 0, depth);
        break;
      case KtExpressionStatement expression:
        Expression(text, expression.Expression, 0, depth);
        break;
      case KtIf conditional:
        If(text, conditional, depth);
        break;
      case KtWhile loop:
        text.Append(LabelOf(loop.Label)).Append("while (");
        Expression(text, loop.Condition, 0, depth);
        text.Append(") ");
        Block(text, loop.Body, depth);
        break;
      case KtDoWhile loop:
        text.Append(LabelOf(loop.Label)).Append("do ");
        Block(text, loop.Body, depth);
        text.Append(" while (");
        Expression(text, loop.Condition, 0, depth);
        text.Append(')');
        break;
      case KtFor loop:
        text.Append(LabelOf(loop.Label)).Append("for (").Append(loop.Variable).Append(" in ");
        Expression(text, loop.Range, 0, depth);
        text.Append(") ");
        Block(text, loop.Body, depth);
        break;
      case KtWhen choice:
        When(text, choice, depth);
        break;
      case KtBreak jump:
        text.Append(jump.Label is null ? "break" : "break@" + jump.Label);
        break;
      case KtContinue jump:
        text.Append(jump.Label is null ? "continue" : "continue@" + jump.Label);
        break;
      case KtReturn result:
        text.Append("return");
        if (result.Value is { } value) {
          text.Append(' ');
          Expression(text, value, 0, depth);
        }

        break;
      case KtThrow raise:
        text.Append("throw ");
        Expression(text, raise.Value, 0, depth);
        break;
      case KtTry attempt:
        text.Append("try ");
        Block(text, attempt.Body, depth);
        foreach (var clause in attempt.Catches) {
          text.Append(" catch (").Append(clause.Name).Append(": ").Append(Print(clause.Type)).Append(") ");
          Block(text, clause.Body, depth);
        }

        if (attempt.Finally is { } final) {
          text.Append(" finally ");
          Block(text, final, depth);
        }

        break;
      case KtLocalFunction local:
        Modifiers(text, local.Function);
        Function(text, local.Function, depth);
        // The function ended its own line.
        return;
      default:
        throw new ArgumentException($"'{statement.GetType().Name}' cannot stand as a statement of a Kotlin block: a block of its own would be a lambda.", nameof(statement));
    }

    text.Append('\n');
  }

  private static void If(StringBuilder text, KtIf conditional, int depth) {
    text.Append("if (");
    Expression(text, conditional.Condition, 0, depth);
    text.Append(") ");
    Block(text, conditional.Then, depth);
    switch (conditional.Else) {
      case null:
        return;
      case KtIf chained:
        text.Append(" else ");
        If(text, chained, depth);
        return;
      case KtBlock block:
        text.Append(" else ");
        Block(text, block, depth);
        return;
      default:
        throw new ArgumentException("The else branch of an if is a block or another if.", nameof(conditional));
    }
  }

  private static void When(StringBuilder text, KtWhen choice, int depth) {
    text.Append("when ");
    if (choice.Subject is { } subject) {
      text.Append('(');
      Expression(text, subject, 0, depth);
      text.Append(") ");
    }

    text.Append("{\n");
    foreach (var entry in choice.Entries) {
      Pad(text, depth + 1);
      if (entry.Conditions.Count == 0) {
        text.Append("else");
      } else {
        for (var index = 0; index < entry.Conditions.Count; ++index) {
          if (index > 0)
            text.Append(", ");

          Expression(text, entry.Conditions[index], 0, depth + 1);
        }
      }

      text.Append(" -> ");
      Block(text, entry.Body, depth + 1);
      text.Append('\n');
    }

    Pad(text, depth);
    text.Append('}');
  }

  #endregion

  #region Expressions

  private static void Arguments(StringBuilder text, IReadOnlyList<KtExpression> arguments, int depth) {
    for (var index = 0; index < arguments.Count; ++index) {
      if (index > 0)
        text.Append(", ");

      Expression(text, arguments[index], 0, depth);
    }
  }

  /// <summary>The arguments of a long call eight to a line, one level deeper, the closing parenthesis on a line of its own.</summary>
  private static void Tabulated(StringBuilder text, IReadOnlyList<KtExpression> arguments, int depth) {
    text.Append('\n');
    for (var index = 0; index < arguments.Count; index += 8) {
      Pad(text, depth + 1);
      Arguments(text, [.. arguments.Skip(index).Take(8)], depth + 1);
      text.Append(index + 8 < arguments.Count ? ",\n" : "\n");
    }

    Pad(text, depth);
  }

  /// <summary>Writes an expression, in parentheses where its level is below what the place needs.</summary>
  private static void Expression(StringBuilder text, KtExpression expression, int needed, int depth) {
    var parenthesized = Level(expression) < needed;
    if (parenthesized)
      text.Append('(');

    switch (expression) {
      case KtLiteral literal:
        text.Append(literal.Text);
        break;
      case KtString value:
        text.Append(Quoted(value.Value));
        break;
      case KtName name:
        text.Append(name.Name);
        break;
      case KtThis:
        text.Append("this");
        break;
      case KtReference reference:
        text.Append("::").Append(reference.Name);
        break;
      case KtMember member:
        Expression(text, member.Receiver, Postfix, depth);
        text.Append(member.Safe ? "?." : ".").Append(member.Name);
        break;
      case KtCall call:
        Expression(text, call.Callee, Postfix, depth);
        if (call.TypeArguments.Count > 0)
          text.Append('<').Append(string.Join(", ", call.TypeArguments.Select(Print))).Append('>');

        if (call.Arguments.Count > 0 || call.Trailing is null) {
          text.Append('(');
          if (call.Arguments.Count > 8)
            Tabulated(text, call.Arguments, depth);
          else
            Arguments(text, call.Arguments, depth);

          text.Append(')');
        }

        if (call.Trailing is { } trailing) {
          text.Append(' ');
          Lambda(text, trailing, depth);
        }

        break;
      case KtIndex index:
        Expression(text, index.Receiver, Postfix, depth);
        text.Append('[');
        Expression(text, index.Index, 0, depth);
        text.Append(']');
        break;
      case KtPrefix prefix:
        text.Append(prefix.Operator);
        // A prefix operand of a prefix operator would read as another operator: - -a as --a.
        Expression(text, prefix.Operand, prefix.Operand is KtPrefix ? Postfix : PrefixLevel, depth);
        break;
      case KtPostfix postfix:
        Expression(text, postfix.Operand, Postfix, depth);
        text.Append(postfix.Operator);
        break;
      case KtBinary binary:
        Binary(text, binary, depth);
        break;
      case KtIs test:
        Expression(text, test.Value, NamedCheck + 1, depth);
        text.Append(test.Negated ? " !is " : " is ").Append(Print(test.Type));
        break;
      case KtAs cast:
        Expression(text, cast.Value, TypeOperator, depth);
        text.Append(cast.Safe ? " as? " : " as ").Append(Print(cast.Type));
        break;
      case KtIfExpression conditional:
        text.Append("if (");
        Expression(text, conditional.Condition, 0, depth);
        text.Append(") ");
        Expression(text, conditional.Then, conditional.Then is KtIfExpression ? Postfix : 0, depth);
        text.Append(" else ");
        Expression(text, conditional.Else, 0, depth);
        break;
      case KtThrowExpression raise:
        text.Append("throw ");
        Expression(text, raise.Value, 0, depth);
        break;
      case KtLambda lambda:
        Lambda(text, lambda, depth);
        break;
      default:
        throw new ArgumentException($"'{expression.GetType().Name}' is no expression of the Kotlin tree.", nameof(expression));
    }

    if (parenthesized)
      text.Append(')');
  }

  private static void Binary(StringBuilder text, KtBinary binary, int depth) {
    var level = Precedence(binary.Operator);
    var infix = level == 9;
    // An operand of an infix function that is an operator of another kind is parenthesized, as is a
    // comparison inside a comparison, which the grammar does not chain.
    var left = infix && binary.Left is KtBinary inner && inner.Operator != binary.Operator ? Postfix : level == 6 ? level + 1 : level;
    var right = infix && binary.Right is KtBinary ? Postfix : level + 1;
    Expression(text, binary.Left, left, depth);
    text.Append(binary.Operator is ".." or "..<" ? binary.Operator : $" {binary.Operator} ");
    Expression(text, binary.Right, right, depth);
  }

  private static void Lambda(StringBuilder text, KtLambda lambda, int depth) {
    text.Append('{');
    if (lambda.Parameters.Count > 0)
      text.Append(' ').Append(Parameters(lambda.Parameters, depth)).Append(" ->");

    if (lambda.Body.Count == 1 && lambda.Body[0] is KtExpressionStatement single) {
      text.Append(' ');
      Expression(text, single.Expression, 0, depth);
      text.Append(" }");
      return;
    }

    text.Append('\n');
    foreach (var statement in lambda.Body)
      Statement(text, statement, depth + 1);

    Pad(text, depth);
    text.Append('}');
  }

  /// <summary>
  /// A string in double quotes: the backslash, the quote and the dollar sign escaped, the control
  /// characters and everything outside printable ASCII as <c>\uXXXX</c>, so that the file is ASCII and
  /// every UTF-16 code unit - a lone surrogate too - survives.
  /// </summary>
  /// <param name="value">The characters.</param>
  /// <returns>The literal.</returns>
  public static string Quoted(string value) {
    ArgumentNullException.ThrowIfNull(value);
    var text = new StringBuilder("\"");
    foreach (var unit in value) {
      text.Append(unit switch {
        '\\' => "\\\\",
        '"' => "\\\"",
        '$' => "\\$",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\b' => "\\b",
        >= ' ' and <= '~' => unit.ToString(),
        _ => "\\u" + ((int)unit).ToString("x4", CultureInfo.InvariantCulture)
      });
    }

    return text.Append('"').ToString();
  }

  #endregion

}
