using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Polypiler.Core.Backends.CSharp.Syntax;

namespace Polypiler.Core.Backends.CSharp.Printing;

/// <summary>
/// Turns a C# syntax tree into text, the only place where generated C# becomes text. The house style:
/// two spaces per level, the opening brace at the end of the line that opens the block, one statement
/// or member per line, LF line endings. Parentheses stand where the tree has them; where a rewritten
/// tree lacks parentheses that the precedence of C# needs (ECMA-334 §12.4.2), the printer adds
/// exactly those. A literal is spelled by its value - the shortest digits that read back as the same
/// number - unless it carries the spelling of a helper's source that still denotes it and the
/// profile's language version has every feature that spelling uses (a digit separator, a binary
/// literal).
/// </summary>
public static class CSharpPrinter {

  private const string Indent = "  ";

  /// <summary>How many elements of a tabulated initializer stand on one line.</summary>
  private const int RowLength = 8;

  /// <summary>Prints a file.</summary>
  /// <param name="unit">The tree.</param>
  /// <param name="profile">The profile, which decides what spellings of a literal the language version reads.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>The text.</returns>
  public static string Print(CsCompilationUnit unit, CSharpProfile profile, CSharpCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(unit);
    var printer = new Writer(profile, capabilities ?? CSharpCapabilities.Default);
    printer.Unit(unit);
    return printer.Text;
  }

  /// <summary>Prints a declaration at the outermost level, as a test or a diagnostic shows it.</summary>
  /// <param name="member">The declaration.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The text, ending with a line break.</returns>
  public static string Print(CsMember member, CSharpProfile profile) {
    var printer = new Writer(profile, CSharpCapabilities.Default);
    printer.Member(member, 0);
    return printer.Text;
  }

  /// <summary>Prints a statement at the outermost level.</summary>
  /// <param name="statement">The statement.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The text, ending with a line break.</returns>
  public static string Print(CsStatement statement, CSharpProfile profile) {
    var printer = new Writer(profile, CSharpCapabilities.Default);
    printer.Statement(statement, 0);
    return printer.Text;
  }

  /// <summary>Prints an expression on its own.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The text.</returns>
  public static string Print(CsExpression expression, CSharpProfile profile) {
    var printer = new Writer(profile, CSharpCapabilities.Default);
    printer.Expression(expression, 0);
    return printer.Text;
  }

  /// <summary>How tightly an expression binds, by the precedence of its operator; higher binds tighter.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The precedence: 15 for a primary expression down to 1 for an assignment.</returns>
  public static int Precedence(CsExpression expression) => expression switch {
    CsIntegerLiteral { Value.Sign: < 0 } or CsRealLiteral { Value: < 0 } => Unary,
    CsRealLiteral real when double.IsNegative(real.Value) => Unary,
    CsPrefixUnary or CsCast => Unary,
    CsBinary binary => Of(binary.Operator),
    CsIsType => Relational,
    CsConditional => Conditional,
    CsAssignment => Assignment,
    _ => Primary
  };

  private const int Primary = 15, Unary = 14, Relational = 10, Conditional = 2, Assignment = 1;

  /// <summary>The precedence of a binary operator.</summary>
  private static int Of(string op) => op switch {
    "*" or "/" or "%" => 13,
    "+" or "-" => 12,
    "<<" or ">>" or ">>>" => 11,
    "<" or ">" or "<=" or ">=" => Relational,
    "==" or "!=" => 9,
    "&" => 8,
    "^" => 7,
    "|" => 6,
    "&&" => 5,
    "||" => 4,
    "??" => 3,
    _ => throw new ArgumentException($"'{op}' is no binary operator of C#.", nameof(op))
  };

  /// <summary>Whether an operand needs no parentheses where only a primary expression or a cast may stand: the operand of a cast or a prefix operator.</summary>
  private static bool IsTight(CsExpression operand, string? prefix) => operand switch {
    CsCast => true,
    CsPrefixUnary inner => prefix is not ("-" or "+") || inner.Operator is "!" or "~",
    _ => Precedence(operand) == Primary
  };

  private sealed class Writer(CSharpProfile profile, CSharpCapabilities capabilities) {

    private readonly StringBuilder _text = new();

    public string Text => this._text.ToString();

    private StringBuilder Line(int depth) {
      for (var level = 0; level < depth; ++level)
        this._text.Append(Indent);

      return this._text;
    }

    private void Leading(CsTrivia trivia, int depth) {
      if (trivia.BlankLine)
        this._text.Append('\n');

      foreach (var comment in trivia.Comments)
        this.Line(depth).Append(comment.Length == 0 ? "//" : "// " + comment).Append('\n');
    }

    public void Unit(CsCompilationUnit unit) {
      foreach (var line in unit.Header)
        this._text.Append(line.Length == 0 ? "//" : "// " + line).Append('\n');

      if (unit.Header.Count > 0)
        this._text.Append('\n');

      this.Separated(unit.Members, 0);
    }

    /// <summary>Declarations one blank line apart.</summary>
    private void Separated(IReadOnlyList<CsMember> members, int depth) {
      for (var index = 0; index < members.Count; ++index) {
        if (index > 0)
          this._text.Append('\n');

        this.Member(members[index] with { Leading = members[index].Leading with { BlankLine = false } }, depth);
      }
    }

    public void Member(CsMember member, int depth) {
      this.Leading(member.Leading, depth);
      switch (member) {
        case CsNamespace { FileScoped: true } scope:
          this._text.Append("namespace ").Append(scope.Name).Append(";\n\n");
          this.Separated(scope.Members, depth);
          break;
        case CsNamespace scope:
          this.Line(depth).Append("namespace ").Append(scope.Name).Append(" {\n\n");
          this.Separated(scope.Members, depth + 1);
          this._text.Append('\n');
          this.Line(depth).Append("}\n");
          break;
        case CsTypeDeclaration type:
          this.Type(type, depth);
          break;
        case CsField field:
          this.Modifiers(field.Modifiers, depth).Append(field.FieldType.Name).Append(' ').Append(field.Name);
          if (field.Initializer is not null) {
            this._text.Append(" = ");
            this.Expression(field.Initializer, depth);
          }

          this._text.Append(";\n");
          break;
        case CsMethod method:
          this.Modifiers(method.Modifiers, depth).Append(method.ReturnType.Name).Append(' ').Append(method.Name);
          if (method.TypeParameters.Count > 0)
            this._text.Append('<').AppendJoin(", ", method.TypeParameters).Append('>');

          this.Parameters(method.Parameters);
          this.Body(method.Body, method.ExpressionBody, depth);
          break;
        default:
          var constructor = (CsConstructor)member;
          this.Modifiers(constructor.Modifiers, depth).Append(constructor.Name);
          this.Parameters(constructor.Parameters);
          this.Body(constructor.Body, null, depth);
          break;
      }
    }

    private StringBuilder Modifiers(IReadOnlyList<string> modifiers, int depth) {
      this.Line(depth);
      foreach (var modifier in modifiers)
        this._text.Append(modifier).Append(' ');

      return this._text;
    }

    private void Parameters(IReadOnlyList<CsParameter> parameters) {
      this._text.Append('(');
      for (var index = 0; index < parameters.Count; ++index) {
        if (index > 0)
          this._text.Append(", ");

        var parameter = parameters[index];
        if (parameter.Modifier is not null)
          this._text.Append(parameter.Modifier).Append(' ');

        this._text.Append(parameter.ParameterType.Name).Append(' ').Append(parameter.Name);
      }

      this._text.Append(')');
    }

    private void Body(CsBlock? body, CsExpression? expression, int depth) {
      if (body is null) {
        this._text.Append(" => ");
        this.Expression(expression!, depth);
        this._text.Append(";\n");
        return;
      }

      this._text.Append(' ');
      this.Braces(body, depth);
      this._text.Append('\n');
    }

    private void Type(CsTypeDeclaration type, int depth) {
      this.Modifiers(type.Modifiers, depth).Append(type.Keyword).Append(' ').Append(type.Name);
      if (type.Parameters is not null)
        this.Parameters(type.Parameters);

      if (type.BaseTypes.Count > 0)
        this._text.Append(" : ").AppendJoin(", ", type.BaseTypes.Select(baseType => baseType.Name));

      if (type.Members is null) {
        this._text.Append(";\n");
        return;
      }

      this._text.Append(" {\n");
      if (type.OpenBlank)
        this._text.Append('\n');

      foreach (var member in type.Members)
        this.Member(member, depth + 1);

      this._text.Append('\n');
      this.Line(depth).Append("}\n");
    }

    /// <summary>A block whose opening brace stands where the text is, not at the start of a line; the closing brace ends without a line break.</summary>
    private void Braces(CsBlock block, int depth) {
      if (block.Inline) {
        this._text.Append('{');
        foreach (var statement in block.Statements) {
          this._text.Append(' ');
          this.Embedded(statement, depth);
        }

        this._text.Append(" }");
        return;
      }

      this._text.Append("{\n");
      foreach (var statement in block.Statements)
        this.Statement(statement, depth + 1);

      this.Line(depth).Append('}');
    }

    /// <summary>A statement where another one stands - the body of a loop, a branch of an if - after a space, without a line break at its end.</summary>
    private void Embedded(CsStatement statement, int depth) {
      if (statement is CsBlock block)
        this.Braces(block, depth);
      else
        this.Bare(statement, depth);
    }

    /// <summary>A statement on its own line: what stands in front of it, its indentation, the statement and the line break.</summary>
    public void Statement(CsStatement statement, int depth) {
      this.Leading(statement.Leading, depth);
      this.Line(depth);
      this.Bare(statement, depth);
      this._text.Append('\n');
    }

    /// <summary>A statement from where the text is, without a line break at its end; lines inside it are indented for <paramref name="depth"/>.</summary>
    private void Bare(CsStatement statement, int depth) {
      switch (statement) {
        case CsBlock block:
          this.Braces(block, depth);
          break;
        case CsLocalDeclaration declaration:
          this.Declaration(declaration, depth);
          this._text.Append(';');
          break;
        case CsExpressionStatement expression:
          this.Expression(expression.Expression, depth);
          this._text.Append(';');
          break;
        case CsIf conditional:
          this.If(conditional, depth);
          break;
        case CsWhile loop:
          this._text.Append("while (");
          this.Expression(loop.Condition, depth);
          this._text.Append(") ");
          this.Embedded(loop.Body, depth);
          break;
        case CsDo loop:
          this._text.Append("do ");
          this.Embedded(loop.Body, depth);
          if (loop.Body is CsBlock)
            this._text.Append(' ');
          else
            this.NewLine(depth);

          this._text.Append("while (");
          this.Expression(loop.Condition, depth);
          this._text.Append(");");
          break;
        case CsFor loop:
          this._text.Append("for (");
          if (loop.Declaration is not null)
            this.Declaration(loop.Declaration, depth);

          this._text.Append(';');
          if (loop.Condition is not null) {
            this._text.Append(' ');
            this.Expression(loop.Condition, depth);
          }

          this._text.Append(';');
          for (var index = 0; index < loop.Incrementors.Count; ++index) {
            this._text.Append(index == 0 ? " " : ", ");
            this.Expression(loop.Incrementors[index], depth);
          }

          this._text.Append(") ");
          this.Embedded(loop.Body, depth);
          break;
        case CsSwitch choice:
          this.Switch(choice, depth);
          break;
        case CsBreak:
          this._text.Append("break;");
          break;
        case CsContinue:
          this._text.Append("continue;");
          break;
        case CsReturn result:
          this._text.Append("return");
          if (result.Value is not null) {
            this._text.Append(' ');
            this.Expression(result.Value, depth);
          }

          this._text.Append(';');
          break;
        case CsThrow exception:
          this._text.Append("throw ");
          this.Expression(exception.Value, depth);
          this._text.Append(';');
          break;
        default:
          var context = (CsCheckedStatement)statement;
          this._text.Append(context.Checked ? "checked " : "unchecked ");
          this.Braces(context.Block, depth);
          break;
      }
    }

    /// <summary>A line break and the indentation of <paramref name="depth"/>.</summary>
    private void NewLine(int depth) {
      this._text.Append('\n');
      this.Line(depth);
    }

    private void Declaration(CsLocalDeclaration declaration, int depth) {
      this._text.Append(declaration.LocalType.Name).Append(' ').Append(declaration.Name);
      if (declaration.Initializer is null)
        return;

      this._text.Append(" = ");
      this.Expression(declaration.Initializer, depth);
    }

    /// <summary>An if, its else on the line of the closing brace before it, an else-if chained on that line too.</summary>
    private void If(CsIf conditional, int depth) {
      this._text.Append("if (");
      this.Expression(conditional.Condition, depth);
      this._text.Append(") ");
      this.Embedded(conditional.Then, depth);
      if (conditional.Else is null)
        return;

      if (conditional.Then is CsBlock)
        this._text.Append(' ');
      else
        this.NewLine(depth);

      this._text.Append("else ");
      this.Embedded(conditional.Else, depth);
    }

    private void Switch(CsSwitch choice, int depth) {
      this._text.Append("switch (");
      this.Expression(choice.Value, depth);
      this._text.Append(") {\n");
      foreach (var section in choice.Sections) {
        for (var index = 0; index < section.Labels.Count; ++index) {
          this.Line(depth + 1);
          if (section.Labels[index] is { } label) {
            this._text.Append("case ");
            this.Expression(label, depth + 1);
            this._text.Append(':');
          } else {
            this._text.Append("default:");
          }

          if (index + 1 < section.Labels.Count)
            this._text.Append('\n');
        }

        if (section.Statements is [CsBlock block]) {
          this._text.Append(' ');
          this.Braces(block, depth + 1);
          this._text.Append('\n');
          continue;
        }

        this._text.Append('\n');
        foreach (var statement in section.Statements)
          this.Statement(statement, depth + 2);
      }

      this.Line(depth).Append('}');
    }

    /// <summary>An operand, in parentheses when <paramref name="tight"/> says it binds too loosely to stand there.</summary>
    private void Operand(CsExpression operand, bool tight, int depth) {
      if (tight) {
        this.Expression(operand, depth);
        return;
      }

      this._text.Append('(');
      this.Expression(operand, depth);
      this._text.Append(')');
    }

    /// <summary>A line break inside an expression: the continuation is indented one step beyond the statement.</summary>
    private void Break(int depth) => this._text.Append('\n').Append(string.Concat(Enumerable.Repeat(Indent, depth + 1)));

    public void Expression(CsExpression expression, int depth) {
      switch (expression) {
        case CsIntegerLiteral integer:
          this._text.Append(this.Spelled(integer));
          break;
        case CsRealLiteral real:
          this._text.Append(Spelled(real));
          break;
        case CsBooleanLiteral truth:
          this._text.Append(truth.Value ? "true" : "false");
          break;
        case CsStringLiteral text:
          this._text.Append('"').Append(text.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
          break;
        case CsDefaultLiteral:
          this._text.Append("default");
          break;
        case CsName name:
          this._text.Append(name.Identifier);
          break;
        case CsMemberAccess access:
          this.Operand(access.Target, Precedence(access.Target) == Primary, depth);
          this._text.Append('.').Append(access.Member);
          break;
        case CsInvocation call:
          this.Operand(call.Target, Precedence(call.Target) == Primary, depth);
          this.Arguments(call.Arguments, depth);
          break;
        case CsOutArgument argument:
          this._text.Append("out ");
          this.Expression(argument.Value, depth);
          break;
        case CsElementAccess element:
          this.Operand(element.Target, Precedence(element.Target) == Primary, depth);
          this._text.Append('[');
          this.Expression(element.Index, depth);
          this._text.Append(']');
          break;
        case CsCast cast:
          this._text.Append('(').Append(cast.TargetType.Name).Append(')');
          this.Operand(cast.Operand, IsTight(cast.Operand, null), depth);
          break;
        case CsPrefixUnary unary:
          this._text.Append(unary.Operator);
          this.Operand(unary.Operand, IsTight(unary.Operand, unary.Operator) && !(unary.Operator is "-" or "+" && Precedence(unary.Operand) == Unary && unary.Operand is not CsCast), depth);
          break;
        case CsPostfixUnary unary:
          this.Operand(unary.Operand, Precedence(unary.Operand) == Primary, depth);
          this._text.Append(unary.Operator);
          break;
        case CsBinary binary:
          var precedence = Of(binary.Operator);
          this.Operand(binary.Left, Precedence(binary.Left) >= precedence, depth);
          if (binary.BreakBefore)
            this.Break(depth);
          else
            this._text.Append(' ');

          this._text.Append(binary.Operator).Append(' ');
          this.Operand(binary.Right, Precedence(binary.Right) > precedence, depth);
          break;
        case CsIsType test:
          this.Operand(test.Operand, Precedence(test.Operand) >= Relational, depth);
          this._text.Append(" is ").Append(test.TestedType.Name);
          break;
        case CsAssignment assignment:
          this.Operand(assignment.Left, Precedence(assignment.Left) > Assignment, depth);
          this._text.Append(' ').Append(assignment.Operator).Append(' ');
          this.Operand(assignment.Right, true, depth);
          break;
        case CsConditional choice:
          this.Operand(choice.Condition, Precedence(choice.Condition) > Conditional, depth);
          if (choice.BreakBeforeQuestion)
            this.Break(depth);
          else
            this._text.Append(' ');

          this._text.Append("? ");
          this.Operand(choice.WhenTrue, Precedence(choice.WhenTrue) >= Conditional, depth);
          if (choice.BreakBeforeColon)
            this.Break(depth);
          else
            this._text.Append(' ');

          this._text.Append(": ");
          this.Operand(choice.WhenFalse, Precedence(choice.WhenFalse) >= Conditional, depth);
          break;
        case CsParenthesized parenthesized:
          this._text.Append('(');
          this.Expression(parenthesized.Inner, depth);
          this._text.Append(')');
          break;
        case CsCheckedExpression context:
          this._text.Append(context.Checked ? "checked(" : "unchecked(");
          this.Expression(context.Inner, depth);
          this._text.Append(')');
          break;
        case CsObjectCreation creation:
          this._text.Append("new ").Append(creation.CreatedType.Name);
          this.Arguments(creation.Arguments, depth);
          break;
        case CsArrayCreation creation:
          this._text.Append("new ").Append(creation.ElementType.Name).Append('[');
          if (creation.Size is not null)
            this.Expression(creation.Size, depth);

          this._text.Append(']');
          if (creation.Initializer is not null) {
            this._text.Append(' ');
            this.Expression(creation.Initializer, depth);
          }

          break;
        case CsArrayInitializer initializer:
          this.Initializer(initializer, depth);
          break;
        default:
          this._text.Append("default(").Append(((CsDefaultExpression)expression).DefaultType.Name).Append(')');
          break;
      }
    }

    private void Arguments(IReadOnlyList<CsExpression> arguments, int depth) {
      this._text.Append('(');
      for (var index = 0; index < arguments.Count; ++index) {
        if (index > 0)
          this._text.Append(", ");

        this.Expression(arguments[index], depth);
      }

      this._text.Append(')');
    }

    private void Initializer(CsArrayInitializer initializer, int depth) {
      var elements = initializer.Elements;
      if (elements.Count == 0) {
        this._text.Append("{ }");
        return;
      }

      if (!initializer.Tabulated || elements.Count <= RowLength) {
        this._text.Append("{ ");
        for (var index = 0; index < elements.Count; ++index) {
          if (index > 0)
            this._text.Append(", ");

          this.Expression(elements[index], depth);
        }

        this._text.Append(" }");
        return;
      }

      this._text.Append("{\n");
      for (var index = 0; index < elements.Count; ++index) {
        if (index % RowLength == 0)
          this.Line(depth + 1);

        this.Expression(elements[index], depth + 1);
        if (index + 1 < elements.Count)
          this._text.Append(index % RowLength == RowLength - 1 ? ",\n" : ", ");
      }

      this._text.Append('\n');
      this.Line(depth).Append('}');
    }

    /// <summary>An integer literal: its source spelling while that still denotes it and the language version reads it, else its digits and suffix.</summary>
    private string Spelled(CsIntegerLiteral literal) {
      if (literal.Raw is { } raw && this.Reads(raw) && CSharpLiterals.ParseInteger(raw) is { } parsed && parsed.Value == literal.Value && parsed.Suffix == literal.Suffix)
        return raw;

      var digits = literal.Value.ToString(CultureInfo.InvariantCulture);
      return literal.Suffix switch {
        CsIntegerSuffix.U => digits + "u",
        CsIntegerSuffix.L => digits + "L",
        CsIntegerSuffix.UL => digits + "UL",
        _ => digits
      };
    }

    /// <summary>Whether the language version of the profile reads a spelling: one with digit separators or binary digits only from C# 7 on.</summary>
    private bool Reads(string raw)
      => (!raw.Contains('_') || capabilities.Has(CSharpFeature.DigitSeparator, profile))
        && (!(raw.TrimStart('-').StartsWith("0b", StringComparison.OrdinalIgnoreCase)) || capabilities.Has(CSharpFeature.BinaryLiteral, profile));

    /// <summary>A floating-point literal: its source spelling while that still denotes it, else the shortest digits that read back as the value.</summary>
    private static string Spelled(CsRealLiteral literal) {
      if (literal.Raw is { } raw && CSharpLiterals.ParseReal(raw) is { } parsed && parsed.IsSingle == literal.IsSingle
          && BitConverter.DoubleToInt64Bits(parsed.Value) == BitConverter.DoubleToInt64Bits(literal.Value))
        return raw;

      if (literal.IsSingle)
        return ((float)literal.Value).ToString("R", CultureInfo.InvariantCulture) + "f";

      var digits = literal.Value.ToString("R", CultureInfo.InvariantCulture);
      return digits.Contains('.') || digits.Contains('E') ? digits : digits + ".0";
    }

  }

}

/// <summary>Reads the spelling of a C# numeric literal (ECMA-334, "Literals"), so that a spelling can be checked against a value.</summary>
public static class CSharpLiterals {

  /// <summary>The value and the suffix of an integer literal, with an optional minus sign in front.</summary>
  /// <param name="text">The spelling.</param>
  /// <returns>The value and the suffix, or <see langword="null"/> when the text is no integer literal.</returns>
  public static (BigInteger Value, CsIntegerSuffix Suffix)? ParseInteger(string text) {
    var negative = text.StartsWith('-');
    var body = (negative ? text[1..] : text).Replace("_", "", StringComparison.Ordinal);
    var suffix = CsIntegerSuffix.None;
    var lower = body.ToLowerInvariant();
    if (lower.EndsWith("ul", StringComparison.Ordinal) || lower.EndsWith("lu", StringComparison.Ordinal)) {
      suffix = CsIntegerSuffix.UL;
      body = body[..^2];
    } else if (lower.EndsWith('u')) {
      suffix = CsIntegerSuffix.U;
      body = body[..^1];
    } else if (lower.EndsWith('l')) {
      suffix = CsIntegerSuffix.L;
      body = body[..^1];
    }

    BigInteger value;
    if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      if (!BigInteger.TryParse("0" + body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
        return null;
    } else if (body.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) {
      if (body.Length == 2 || body[2..].Any(digit => digit is not ('0' or '1')))
        return null;

      value = body[2..].Aggregate(BigInteger.Zero, (sum, digit) => sum * 2 + (digit - '0'));
    } else if (body.Length == 0 || !body.All(char.IsAsciiDigit) || !BigInteger.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out value)) {
      return null;
    }

    return (negative ? -value : value, suffix);
  }

  /// <summary>The value of a real literal, with an optional minus sign in front, and whether it is a <c>float</c>.</summary>
  /// <param name="text">The spelling.</param>
  /// <returns>The value, or <see langword="null"/> when the text is no real literal.</returns>
  public static (double Value, bool IsSingle)? ParseReal(string text) {
    var body = text.Replace("_", "", StringComparison.Ordinal);
    var single = body.EndsWith('f') || body.EndsWith('F');
    if (single || body.EndsWith('d') || body.EndsWith('D'))
      body = body[..^1];

    if (!body.Contains('.') && !body.Contains('e') && !body.Contains('E') && !single)
      return null;

    if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
      return null;

    return (single ? (float)value : value, single);
  }

}
