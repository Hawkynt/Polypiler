using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Go.Syntax;

namespace Polypiler.Core.Backends.Go.Printing;

/// <summary>
/// Turns a Go syntax tree into text in the layout <c>gofmt</c> gives it, so that <c>gofmt -l</c>
/// lists no generated file: tabs for indentation, one statement per line, the label of a statement
/// one level to the left, <c>case</c> at the level of its <c>switch</c>, the types of a struct's
/// fields aligned in one column, the imports sorted, and the spaces around a binary operator chosen
/// by the precedence and nesting depth of the expression as <c>go/printer</c> chooses them
/// (<c>a*b + c</c>, <c>x[i+1]</c>, <c>a &lt;&lt; (n &amp; 31)</c>). The rules of the binary
/// expressions are converted from <c>go/printer</c> (nodes.go: <c>walkBinary</c>, <c>cutoff</c>,
/// <c>diffPrec</c>, <c>reduceDepth</c>, <c>binaryExpr</c>, <c>expr1</c>; BSD-3-Clause, see
/// <c>THIRD-PARTY-NOTICE.go.txt</c>). Parentheses are written only where precedence needs them.
/// </summary>
public static class GoPrinter {

  /// <summary>The precedence of a unary operator, above every binary one.</summary>
  private const int UnaryPrecedence = 6;

  /// <summary>The precedence of a primary expression: an operand, a call, an index, a selector.</summary>
  private const int HighestPrecedence = 7;

  /// <summary>Prints a file, ending with a line break.</summary>
  /// <param name="file">The file.</param>
  /// <returns>The text.</returns>
  public static string Print(GoFile file) {
    ArgumentNullException.ThrowIfNull(file);
    var writer = new Writer();
    writer.File(file);
    return writer.ToString();
  }

  /// <summary>Prints a declaration alone, as it stands in a file, without the blank lines in front.</summary>
  /// <param name="declaration">The declaration.</param>
  /// <returns>The text, ending with a line break.</returns>
  public static string Print(GoDecl declaration) {
    ArgumentNullException.ThrowIfNull(declaration);
    var writer = new Writer();
    writer.Declaration(declaration with { Leading = declaration.Leading with { BlankLines = 0 } });
    return writer.ToString();
  }

  /// <summary>Prints a statement alone, at the outermost level.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The text, ending with a line break.</returns>
  public static string Print(GoStmt statement) {
    ArgumentNullException.ThrowIfNull(statement);
    var writer = new Writer();
    writer.Statement(statement with { Leading = statement.Leading with { BlankLines = 0 } });
    return writer.ToString();
  }

  /// <summary>Prints an expression alone, at the outermost depth.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The text.</returns>
  public static string Print(GoExpr expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var writer = new Writer();
    writer.Expression(expression);
    return writer.ToString();
  }

  /// <summary>The precedence of a binary operator (The Go Programming Language Specification, "Operator precedence").</summary>
  /// <param name="op">The operator.</param>
  /// <returns>5 for the multiplicative operators, 4 for the additive, 3 for comparisons, 2 for <c>&amp;&amp;</c>, 1 for <c>||</c>.</returns>
  /// <exception cref="ArgumentException">The text is no binary operator of Go.</exception>
  public static int Precedence(string op) => op switch {
    "*" or "/" or "%" or "<<" or ">>" or "&" or "&^" => 5,
    "+" or "-" or "|" or "^" => 4,
    "==" or "!=" or "<" or "<=" or ">" or ">=" => 3,
    "&&" => 2,
    "||" => 1,
    _ => throw new ArgumentException($"'{op}' is no binary operator of Go.", nameof(op))
  };

  /// <summary>An interpreted string literal of Go for a text: quotes, and the escapes Go's <c>strconv.Quote</c> writes for controls, quotes and backslashes.</summary>
  /// <param name="value">The text, which holds no lone surrogate.</param>
  /// <returns>The literal.</returns>
  public static string Quote(string value) {
    ArgumentNullException.ThrowIfNull(value);
    var text = new StringBuilder("\"");
    foreach (var character in value)
      text.Append(character switch {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\a' => "\\a",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\v' => "\\v",
        < ' ' or '\x7F' => "\\x" + ((int)character).ToString("x2", CultureInfo.InvariantCulture),
        _ => character.ToString()
      });

    return text.Append('"').ToString();
  }

  /// <summary>The spelling of an integer literal: decimal, or <c>0x</c> and upper-case hexadecimal digits.</summary>
  /// <param name="literal">The literal.</param>
  /// <returns>The text.</returns>
  public static string Spell(GoInt literal) {
    ArgumentNullException.ThrowIfNull(literal);
    if (!literal.Hex)
      return literal.Value.ToString(CultureInfo.InvariantCulture);

    var digits = literal.Value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
    return "0x" + (digits.Length == 0 ? "0" : digits);
  }

  private sealed class Writer {

    private readonly StringBuilder _text = new();
    private int _indent;

    public override string ToString() => this._text.ToString();

    private void Write(string text) => this._text.Append(text);

    private void NewLine() => this._text.Append('\n');

    private void Indentation() => this._text.Append('\t', this._indent);

    private void Leading(GoTrivia leading) {
      for (var line = 0; line < Math.Min(leading.BlankLines, 1); ++line)
        this.NewLine();

      foreach (var comment in leading.Comments) {
        this.Indentation();
        this.Write(comment.Length == 0 ? "//" : "// " + comment);
        this.NewLine();
      }
    }

    #region Files and declarations

    public void File(GoFile file) {
      foreach (var line in file.Header) {
        this.Write(line.Length == 0 ? "//" : "// " + line);
        this.NewLine();
      }

      if (file.Header.Count > 0)
        this.NewLine();

      this.Write("package " + file.Package);
      this.NewLine();
      var imports = file.Imports.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
      if (imports.Count == 1) {
        this.NewLine();
        this.Write("import " + Quote(imports[0]));
        this.NewLine();
      } else if (imports.Count > 1) {
        this.NewLine();
        this.Write("import (");
        this.NewLine();
        foreach (var path in imports) {
          this.Write("\t" + Quote(path));
          this.NewLine();
        }

        this.Write(")");
        this.NewLine();
      }

      foreach (var declaration in file.Declarations)
        this.Declaration(declaration with { Leading = declaration.Leading with { BlankLines = 1 } });
    }

    public void Declaration(GoDecl declaration) {
      this.Leading(declaration.Leading);
      switch (declaration) {
        case GoFuncDecl function:
          this.Write("func ");
          if (function.Receiver is { } receiver) {
            this.Write("(");
            this.Field(receiver);
            this.Write(") ");
          }

          this.Write(function.Name);
          this.Signature(function.Signature);
          this.Write(" ");
          this.Block(function.Body);
          break;
        case GoConstDecl constant:
          this.Write("const ");
          this.Spec(constant.Spec);
          break;
        case GoVarDecl variable:
          this.Write("var ");
          this.Spec(variable.Spec);
          break;
        case GoTypeDecl type:
          this.Write("type " + type.Name + " ");
          this.Expression(type.Declared);
          break;
        default:
          throw new ArgumentException($"The printer has no form for {declaration.GetType().Name}.", nameof(declaration));
      }

      this.NewLine();
    }

    private void Spec(GoValueSpec spec) {
      this.Write(string.Join(", ", spec.Names));
      if (spec.SpecType is { } type) {
        this.Write(" ");
        this.Expression(type);
      }

      if (spec.Values.Count > 0) {
        this.Write(" = ");
        this.List(spec.Values, 1);
      }
    }

    private void Field(GoField field) {
      if (field.Names.Count > 0)
        this.Write(string.Join(", ", field.Names) + " ");

      this.Expression(field.FieldType);
    }

    private void Signature(GoFuncType signature) {
      this.Write("(");
      for (var index = 0; index < signature.Parameters.Count; ++index) {
        if (index > 0)
          this.Write(", ");

        this.Field(signature.Parameters[index]);
      }

      this.Write(")");
      if (signature.Results.Count == 0)
        return;

      if (signature.Results is [{ Names.Count: 0 } single]) {
        this.Write(" ");
        this.Expression(single.FieldType);
        return;
      }

      this.Write(" (");
      for (var index = 0; index < signature.Results.Count; ++index) {
        if (index > 0)
          this.Write(", ");

        this.Field(signature.Results[index]);
      }

      this.Write(")");
    }

    #endregion

    #region Statements

    private void Block(GoBlock block) {
      this.Write("{");
      this.NewLine();
      ++this._indent;
      this.Statements(block.Statements);
      --this._indent;
      this.Indentation();
      this.Write("}");
    }

    private void Statements(IReadOnlyList<GoStmt> statements) {
      for (var index = 0; index < statements.Count; ++index) {
        var statement = statements[index];
        this.Statement(index == 0 ? statement with { Leading = statement.Leading with { BlankLines = 0 } } : statement);
      }
    }

    public void Statement(GoStmt statement) {
      this.Leading(statement.Leading);
      if (statement is GoLabeled labeled) {
        // gofmt writes the label one level to the left of the statement it labels.
        this._text.Append('\t', Math.Max(this._indent - 1, 0));
        this.Write(labeled.Label + ":");
        this.NewLine();
        this.Statement(labeled.Statement with { Leading = GoTrivia.None });
        return;
      }

      this.Indentation();
      this.Simple(statement);
      this.NewLine();
    }

    /// <summary>A statement without its indentation and line break; the simple ones also stand in the clauses of <c>if</c>, <c>for</c> and <c>switch</c>.</summary>
    private void Simple(GoStmt statement) {
      switch (statement) {
        case GoExprStmt expression:
          this.Expr0(expression.Expression, 1);
          break;
        case GoAssign assignment: {
          var depth = assignment.Left.Count > 1 && assignment.Right.Count > 1 ? 2 : 1;
          this.List(assignment.Left, depth);
          this.Write(" " + assignment.Op + " ");
          this.List(assignment.Right, depth);
          break;
        }
        case GoIncDec step:
          this.Expr0(step.Target, 2);
          this.Write(step.Increment ? "++" : "--");
          break;
        case GoVarStmt variable:
          this.Write("var ");
          this.Spec(variable.Spec);
          break;
        case GoReturn result:
          this.Write("return");
          if (result.Results.Count > 0) {
            this.Write(" ");
            this.List(result.Results, 1);
          }

          break;
        case GoBlockStmt block:
          this.Block(block.Body);
          break;
        case GoIf conditional:
          this.If(conditional);
          break;
        case GoFor loop:
          this.Write("for");
          this.ControlClause(true, loop.Init, loop.Condition, loop.Post);
          this.Block(loop.Body);
          break;
        case GoRange range:
          this.Write("for ");
          if (range.Key is { } key) {
            this.Expression(key);
            if (range.Value is { } value) {
              this.Write(", ");
              this.Expression(value);
            }

            this.Write(range.Define ? " := " : " = ");
          }

          this.Write("range ");
          this.Expression(range.Over);
          this.Write(" ");
          this.Block(range.Body);
          break;
        case GoSwitch choice:
          this.Write("switch");
          this.ControlClause(false, choice.Init, choice.Tag, null);
          this.Write("{");
          this.NewLine();
          foreach (var clause in choice.Clauses) {
            this.Indentation();
            if (clause.Values is { } values) {
              this.Write("case ");
              this.List(values, 1);
              this.Write(":");
            } else {
              this.Write("default:");
            }

            this.NewLine();
            ++this._indent;
            this.Statements(clause.Body);
            --this._indent;
          }

          this.Indentation();
          this.Write("}");
          break;
        case GoBranch branch:
          this.Write(branch.Label is null ? branch.Keyword : branch.Keyword + " " + branch.Label);
          break;
        case GoDefer deferred:
          this.Write("defer ");
          this.Expression(deferred.Call);
          break;
        default:
          throw new ArgumentException($"The printer has no form for {statement.GetType().Name}.", nameof(statement));
      }
    }

    private void If(GoIf conditional) {
      this.Write("if");
      this.ControlClause(false, conditional.Init, conditional.Condition, null);
      this.Block(conditional.Then);
      switch (conditional.Else) {
        case null:
          break;
        case GoIf chained:
          this.Write(" else ");
          this.If(chained);
          break;
        case GoBlockStmt block:
          this.Write(" else ");
          this.Block(block.Body);
          break;
        default:
          throw new ArgumentException("The else branch of an if statement is an if statement or a block.", nameof(conditional));
      }
    }

    /// <summary>The clause of <c>if</c>, <c>for</c> and <c>switch</c> as <c>go/printer</c>'s <c>controlClause</c> writes it, with the blank before the brace.</summary>
    private void ControlClause(bool isFor, GoStmt? init, GoExpr? condition, GoStmt? post) {
      this.Write(" ");
      var blank = false;
      if (init is null && post is null) {
        if (condition is not null) {
          this.Expression(condition);
          blank = true;
        }
      } else {
        if (init is not null)
          this.Simple(init);

        this.Write("; ");
        if (condition is not null) {
          this.Expression(condition);
          blank = true;
        }

        if (isFor) {
          this.Write("; ");
          blank = false;
          if (post is not null) {
            this.Simple(post);
            blank = true;
          }
        }
      }

      if (blank)
        this.Write(" ");
    }

    #endregion

    #region Expressions

    public void Expression(GoExpr expression) => this.Expr1(expression, 0, 1);

    private void Expr0(GoExpr expression, int depth) => this.Expr1(expression, 0, depth);

    private void List(IReadOnlyList<GoExpr> expressions, int depth) {
      for (var index = 0; index < expressions.Count; ++index) {
        if (index > 0)
          this.Write(", ");

        this.Expr0(expressions[index], depth);
      }
    }

    private void Expr1(GoExpr expression, int precedence, int depth) {
      switch (expression) {
        case GoIdent name:
          this.Write(name.Name);
          break;
        case GoInt literal:
          this.Write(Spell(literal));
          break;
        case GoFloat literal:
          this.Write(literal.Text);
          break;
        case GoString literal:
          this.Write(Quote(literal.Value));
          break;
        case GoBinary binary:
          this.Binary(binary, precedence, Cutoff(binary, depth), depth);
          break;
        case GoKeyValue pair:
          this.Expression(pair.Key);
          this.Write(": ");
          this.Expression(pair.Value);
          break;
        case GoStar star when UnaryPrecedence < precedence:
          this.Write("(*");
          this.Expression(star.Operand);
          this.Write(")");
          break;
        case GoStar star:
          this.Write("*");
          this.Expression(star.Operand);
          break;
        case GoUnary unary when UnaryPrecedence < precedence:
          this.Write("(");
          this.Expression(unary);
          this.Write(")");
          break;
        case GoUnary unary:
          this.Write(unary.Op);
          // Two operators that would read as one token are kept apart, as go/printer's mayCombine does.
          if (unary.Operand is GoUnary { Op: var inner } && Combine(unary.Op, inner))
            this.Write(" ");

          this.Expr1(unary.Operand, UnaryPrecedence, depth);
          break;
        case GoFuncLit literal:
          this.Write("func");
          this.Signature(literal.Signature);
          this.Write(" ");
          this.Block(literal.Body);
          break;
        case GoSelector selector:
          this.Expr1(selector.Target, HighestPrecedence, depth);
          this.Write("." + selector.Name);
          break;
        case GoIndex index:
          this.Expr1(index.Target, HighestPrecedence, 1);
          this.Write("[");
          this.Expr0(index.Index, depth + 1);
          this.Write("]");
          break;
        case GoSliceExpr slice:
          this.Slice(slice, depth);
          break;
        case GoCall call:
          this.Call(call, depth);
          break;
        case GoComposite composite:
          this.Composite(composite, depth);
          break;
        case GoArrayType array:
          this.Write("[");
          if (array.Length is { } length)
            this.Expression(length);

          this.Write("]");
          this.Expression(array.Element);
          break;
        case GoMapType map:
          this.Write("map[");
          this.Expression(map.Key);
          this.Write("]");
          this.Expression(map.Value);
          break;
        case GoFuncType function:
          this.Write("func");
          this.Signature(function);
          break;
        case GoStructType structure:
          this.Struct(structure);
          break;
        default:
          throw new ArgumentException($"The printer has no form for {expression.GetType().Name}.", nameof(expression));
      }
    }

    private static bool Combine(string outer, string inner) => (outer, inner) is ("-", "-") or ("+", "+") or ("&", "&") or ("&", "^");

    /// <summary>A binary expression as <c>go/printer</c>'s <c>binaryExpr</c> writes it.</summary>
    private void Binary(GoBinary binary, int precedence1, int cutoff, int depth) {
      var precedence = Precedence(binary.Op);
      if (precedence < precedence1) {
        this.Write("(");
        this.Expr0(binary, ReduceDepth(depth));
        this.Write(")");
        return;
      }

      var blank = precedence < cutoff;
      this.Expr1(binary.Left, precedence, depth + DiffPrecedence(binary.Left, precedence));
      this.Write(blank ? " " + binary.Op + " " : binary.Op);
      this.Expr1(binary.Right, precedence + 1, depth + 1);
    }

    private void Slice(GoSliceExpr slice, int depth) {
      this.Expr1(slice.Target, HighestPrecedence, 1);
      this.Write("[");
      GoExpr?[] indices = slice.Max is null ? [slice.Low, slice.High] : [slice.Low, slice.High, slice.Max];
      var blanks = depth <= 1 && indices.Count(index => index is not null) > 1 && indices.Any(index => index is GoBinary);
      for (var position = 0; position < indices.Length; ++position) {
        if (position > 0) {
          if (indices[position - 1] is not null && blanks)
            this.Write(" ");

          this.Write(":");
          if (indices[position] is not null && blanks)
            this.Write(" ");
        }

        if (indices[position] is { } index)
          this.Expr0(index, depth + 1);
      }

      this.Write("]");
    }

    private void Call(GoCall call, int depth) {
      if (call.Arguments.Count > 1)
        ++depth;

      if (call.Callee is GoFuncType) {
        this.Write("(");
        this.Expression(call.Callee);
        this.Write(")");
      } else {
        this.Expr1(call.Callee, HighestPrecedence, depth);
      }

      this.Write("(");
      this.List(call.Arguments, depth);
      if (call.Spread)
        this.Write("...");

      this.Write(")");
    }

    private void Composite(GoComposite composite, int depth) {
      if (composite.LiteralType is { } type)
        this.Expr1(type, HighestPrecedence, depth);

      this.Write("{");
      if (composite.PerLine <= 0 || composite.Elements.Count == 0) {
        this.List(composite.Elements, 1);
        this.Write("}");
        return;
      }

      this.NewLine();
      ++this._indent;
      for (var start = 0; start < composite.Elements.Count; start += composite.PerLine) {
        this.Indentation();
        var row = composite.Elements.Skip(start).Take(composite.PerLine).ToList();
        for (var index = 0; index < row.Count; ++index) {
          if (index > 0)
            this.Write(" ");

          this.Expr0(row[index], 1);
          this.Write(",");
        }

        this.NewLine();
      }

      --this._indent;
      this.Indentation();
      this.Write("}");
    }

    /// <summary>A struct type: <c>struct{}</c>, or one field to a line with the types aligned in one column, as gofmt's tabwriter aligns them.</summary>
    private void Struct(GoStructType structure) {
      if (structure.Fields.Count == 0) {
        this.Write("struct{}");
        return;
      }

      this.Write("struct {");
      this.NewLine();
      ++this._indent;
      var width = structure.Fields.Max(field => string.Join(", ", field.Names).Length);
      foreach (var field in structure.Fields) {
        this.Indentation();
        var names = string.Join(", ", field.Names);
        if (names.Length > 0)
          this.Write(names + new string(' ', width - names.Length + 1));

        this.Expression(field.FieldType);
        this.NewLine();
      }

      --this._indent;
      this.Indentation();
      this.Write("}");
    }

    #endregion

  }

  /// <summary>
  /// What decides the spaces around the operators of a binary expression, as <c>go/printer</c>'s
  /// <c>walkBinary</c> finds it: whether operators of the levels 4 and 5 occur outside parentheses,
  /// and the cutoff a right operand that would clash with its operator forces.
  /// </summary>
  private static (bool Has4, bool Has5, int MaxProblem) Walk(GoBinary binary) {
    var precedence = Precedence(binary.Op);
    var (has4, has5, problem) = (precedence == 4, precedence == 5, 0);
    if (binary.Left is GoBinary left && Precedence(left.Op) >= precedence) {
      var (h4, h5, p) = Walk(left);
      (has4, has5, problem) = (has4 || h4, has5 || h5, Math.Max(problem, p));
    }

    switch (binary.Right) {
      case GoBinary right when Precedence(right.Op) > precedence: {
        var (h4, h5, p) = Walk(right);
        (has4, has5, problem) = (has4 || h4, has5 || h5, Math.Max(problem, p));
        break;
      }
      case GoStar when binary.Op == "/":
        problem = 5;
        break;
      case GoUnary unary:
        switch (binary.Op + unary.Op) {
          case "/*" or "&&" or "&^":
            problem = 5;
            break;
          case "++" or "--":
            problem = Math.Max(problem, 4);
            break;
        }

        break;
    }

    return (has4, has5, problem);
  }

  private static int Cutoff(GoBinary binary, int depth) {
    var (has4, has5, problem) = Walk(binary);
    if (problem > 0)
      return problem + 1;

    if (has4 && has5)
      return depth == 1 ? 5 : 4;

    return depth == 1 ? 6 : 4;
  }

  private static int DiffPrecedence(GoExpr expression, int precedence)
    => expression is GoBinary binary && Precedence(binary.Op) == precedence ? 0 : 1;

  private static int ReduceDepth(int depth) => Math.Max(depth - 1, 1);

}
