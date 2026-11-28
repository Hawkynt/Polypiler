using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.VisualBasic.Net;

/// <summary>
/// Turns a Visual Basic .NET syntax tree into text: four spaces per level, LF line ends, every option
/// stated at the top, and parentheses exactly where the operator precedence of the language reference
/// needs them, from <c>^</c> down to <c>Xor</c>. A table of more than <see cref="ElementsPerLine"/>
/// elements is written over several lines, each ending with <c> _</c> where the profile has no
/// implicit line continuation.
/// </summary>
public static class VnPrinter {

  /// <summary>How many elements of an array literal stand on one line.</summary>
  public const int ElementsPerLine = 16;

  private const string Indent = "    ";

  /// <summary>The place of an expression that stands alone, which no operator binds less tightly than.</summary>
  private const int Top = -10;

  /// <summary>Prints a source file.</summary>
  /// <param name="module">The tree.</param>
  /// <param name="implicitContinuation">Whether a statement may go on to the next line without <c> _</c>.</param>
  /// <returns>The text.</returns>
  public static string Print(VnModule module, bool implicitContinuation) {
    ArgumentNullException.ThrowIfNull(module);
    var printer = new Printer(implicitContinuation);
    foreach (var line in module.Header)
      printer.Line(0, line.Length == 0 ? "'" : "' " + line);
    printer.Blank();
    foreach (var option in new[] { "Explicit On", "Strict On", "Infer On", "Compare Binary" })
      printer.Line(0, "Option " + option);
    printer.Blank();
    printer.Line(0, "Public Module " + module.Name);
    foreach (var member in module.Members) {
      printer.Blank();
      printer.Declaration(1, member);
    }

    printer.Blank();
    printer.Line(0, "End Module");
    return printer.Text;
  }

  /// <summary>Prints one declaration, as a module holds it: one level deep.</summary>
  /// <param name="declaration">The declaration.</param>
  /// <param name="implicitContinuation">Whether a statement may go on to the next line without <c> _</c>.</param>
  /// <returns>The text.</returns>
  public static string Print(VnDeclaration declaration, bool implicitContinuation = true) {
    ArgumentNullException.ThrowIfNull(declaration);
    var printer = new Printer(implicitContinuation);
    printer.Declaration(1, declaration);
    return printer.Text;
  }

  /// <summary>Prints one expression.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The text.</returns>
  public static string Print(VnExpression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return new Printer(true).Expression(expression, Top);
  }

  /// <summary>How tightly an operator binds; an atom binds tightest.</summary>
  internal static int Precedence(VnExpression expression) => expression switch {
    VnBinary binary => Binding(binary.Operator),
    VnUnary { Operator: "-" } => 10,
    VnUnary => 2,
    VnLiteral literal when literal.Text.StartsWith('-') => 10,
    _ => 12
  };

  private static int Binding(string op) => op switch {
    "^" => 11,
    "*" or "/" => 9,
    "\\" => 8,
    "Mod" => 7,
    "+" or "-" => 6,
    "&" => 5,
    "<<" or ">>" => 4,
    "=" or "<>" or "<" or "<=" or ">" or ">=" or "Is" or "IsNot" => 3,
    "And" or "AndAlso" => 1,
    "Or" or "OrElse" => 0,
    "Xor" => -1,
    _ => throw new ArgumentException($"'{op}' is no binary operator of Visual Basic.", nameof(op))
  };

  private sealed class Printer(bool implicitContinuation) {

    private readonly StringBuilder _text = new();

    public string Text => this._text.ToString();

    public void Line(int depth, string text) {
      for (var level = 0; level < depth; ++level)
        this._text.Append(Indent);
      this._text.Append(text).Append('\n');
    }

    public void Blank() => this._text.Append('\n');

    public void Declaration(int depth, VnDeclaration declaration) {
      switch (declaration) {
        case VnField field:
          this.Line(depth, $"{field.Modifiers} {field.Name} As {field.Type}" + (field.Value is null ? "" : " = " + this.Expression(field.Value, Top)));
          break;
        case VnStructure structure:
          this.Line(depth, "Public Structure " + structure.Name);
          var first = true;
          foreach (var member in structure.Members) {
            if (member is VnMethod && !first)
              this.Blank();
            this.Declaration(depth + 1, member);
            first = false;
          }

          this.Line(depth, "End Structure");
          break;
        case VnMethod method:
          var kind = method.Returns is null ? "Sub" : "Function";
          var generic = method.TypeParameter is null ? "" : $"(Of {method.TypeParameter})";
          var parameters = string.Join(", ", method.Parameters.Select(parameter => $"{(parameter.ByRef ? "ByRef" : "ByVal")} {parameter.Name} As {parameter.Type}"));
          this.Line(depth, $"{(method.Modifiers.Length == 0 ? "" : method.Modifiers + " ")}{kind} {method.Name}{generic}({parameters})" + (method.Returns is null ? "" : " As " + method.Returns));
          this.Statements(depth + 1, method.Body);
          this.Line(depth, "End " + kind);
          break;
        default:
          throw new ArgumentException($"The declaration {declaration.GetType().Name} cannot be printed.", nameof(declaration));
      }
    }

    private void Statements(int depth, IReadOnlyList<VnStatement> statements) {
      foreach (var statement in statements)
        this.Statement(depth, statement);
    }

    private void Statement(int depth, VnStatement statement) {
      switch (statement) {
        case VnDim dim:
          this.Line(depth, $"Dim {dim.Name} As {dim.Type} = {this.Expression(dim.Value, Top)}");
          break;
        case VnAssign assign:
          this.Line(depth, $"{this.Expression(assign.Target, 12)} {assign.Operator} {this.Expression(assign.Value, Top)}");
          break;
        case VnCallStatement call:
          this.Line(depth, this.Expression(call.Call, 12));
          break;
        case VnIf branch:
          this.If(depth, branch, "If");
          this.Line(depth, "End If");
          break;
        case VnSelect select:
          this.Line(depth, "Select Case " + this.Expression(select.Value, Top));
          foreach (var @case in select.Cases) {
            this.Line(depth + 1, "Case " + string.Join(", ", @case.Labels.Select(label => this.Expression(label, Top))));
            this.Statements(depth + 2, @case.Body);
          }

          if (select.Else is not null) {
            this.Line(depth + 1, "Case Else");
            this.Statements(depth + 2, select.Else);
          }

          this.Line(depth, "End Select");
          break;
        case VnDo loop:
          var test = loop.Condition is null ? "" : " While " + this.Expression(loop.Condition, Top);
          this.Line(depth, "Do" + (loop.Post ? "" : test));
          this.Statements(depth + 1, loop.Body);
          this.Line(depth, "Loop" + (loop.Post ? test : ""));
          break;
        case VnExit exit:
          this.Line(depth, "Exit " + exit.Kind);
          break;
        case VnContinue:
          this.Line(depth, "Continue Do");
          break;
        case VnReturn returned:
          this.Line(depth, returned.Value is null ? "Return" : "Return " + this.Expression(returned.Value, Top));
          break;
        case VnThrow thrown:
          this.Line(depth, thrown.Value is null ? "Throw" : "Throw " + this.Expression(thrown.Value, Top));
          break;
        case VnTry attempt:
          this.Line(depth, "Try");
          this.Statements(depth + 1, attempt.Body);
          foreach (var clause in attempt.Catches) {
            this.Line(depth, $"Catch {clause.Name} As {clause.Type}" + (clause.When is null ? "" : " When " + this.Expression(clause.When, Top)));
            this.Statements(depth + 1, clause.Body);
          }

          if (attempt.Finally is not null) {
            this.Line(depth, "Finally");
            this.Statements(depth + 1, attempt.Finally);
          }

          this.Line(depth, "End Try");
          break;
        case VnLabel label:
          this.Line(0, label.Name + ":");
          break;
        case VnGoTo jump:
          this.Line(depth, "GoTo " + jump.Label);
          break;
        default:
          throw new ArgumentException($"The statement {statement.GetType().Name} cannot be printed.", nameof(statement));
      }
    }

    /// <summary>An <c>If</c> or <c>ElseIf</c> with what follows it; an <c>Else</c> of one <c>If</c> alone goes on as <c>ElseIf</c>.</summary>
    private void If(int depth, VnIf branch, string keyword) {
      this.Line(depth, $"{keyword} {this.Expression(branch.Condition, Top)} Then");
      this.Statements(depth + 1, branch.Then);
      if (branch.Else is [VnIf nested])
        this.If(depth, nested, "ElseIf");
      else if (branch.Else is { Count: > 0 } statements) {
        this.Line(depth, "Else");
        this.Statements(depth + 1, statements);
      }
    }

    /// <summary>An expression, in parentheses where it binds less tightly than its place needs.</summary>
    public string Expression(VnExpression expression, int needed) {
      var text = this.Bare(expression);
      return Precedence(expression) < needed ? "(" + text + ")" : text;
    }

    private string Bare(VnExpression expression) {
      switch (expression) {
        case VnLiteral literal:
          return literal.Text;
        case VnString text:
          return "\"" + text.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        case VnName name:
          return name.Name;
        case VnMember member:
          return this.Expression(member.Target, 12) + "." + member.Name;
        case VnInvoke invoke:
          return this.Expression(invoke.Target, 12) + "(" + string.Join(", ", invoke.Arguments.Select(argument => this.Expression(argument, Top))) + ")";
        case VnUnary { Operator: "-" } negation:
          return "-" + this.Expression(negation.Operand, 10);
        case VnUnary unary:
          return unary.Operator + " " + (unary.Operand is VnBinary ? "(" + this.Bare(unary.Operand) + ")" : this.Expression(unary.Operand, 2));
        case VnBinary binary:
          var binding = Binding(binary.Operator);
          return this.Expression(binary.Left, binding) + " " + binary.Operator + " " + this.Expression(binary.Right, binding + 1);
        case VnTernary choice:
          return $"If({this.Expression(choice.Condition, Top)}, {this.Expression(choice.WhenTrue, Top)}, {this.Expression(choice.WhenFalse, Top)})";
        case VnNew created:
          return $"New {created.Type}(" + string.Join(", ", created.Arguments.Select(argument => this.Expression(argument, Top))) + ")";
        case VnNewArray array:
          // The bound of a new array of arrays stands before the parentheses of its element type: New Integer(n)() {}.
          var cut = array.Element.IndexOf('(', StringComparison.Ordinal);
          var (head, tail) = cut < 0 ? (array.Element, "") : (array.Element[..cut], array.Element[cut..]);
          return array.Upper is { } upper ? $"New {head}({this.Expression(upper, Top)}){tail} {{}}" : $"New {head}(){tail} {{" + this.Elements(array.Elements) + "}";
        default:
          throw new ArgumentException($"The expression {expression.GetType().Name} cannot be printed.", nameof(expression));
      }
    }

    /// <summary>The elements of an array literal, over several lines when there are many of them.</summary>
    private string Elements(IReadOnlyList<VnExpression> elements) {
      var texts = elements.Select(element => this.Expression(element, Top)).ToList();
      if (texts.Count <= ElementsPerLine)
        return string.Join(", ", texts);

      var separator = implicitContinuation ? ",\n" : ", _\n";
      var lines = texts.Chunk(ElementsPerLine).Select(chunk => Indent + Indent + Indent + string.Join(", ", chunk));
      return (implicitContinuation ? "\n" : " _\n") + string.Join(separator, lines) + (implicitContinuation ? "\n" + Indent + Indent : " _\n" + Indent + Indent);
    }

  }

}
