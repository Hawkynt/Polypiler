using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Backends.JavaScript.Syntax;

/// <summary>
/// Reads JavaScript into the syntax tree of the backend, through Acornima's parser with its
/// parentheses kept: the helpers, which stay readable JavaScript, and every generated file when a test
/// parses it back. Operators and the spelling of literals are taken from the source text between the
/// nodes, and so is what the tree keeps of the layout - a blank line or a comment in front of a
/// statement, a block on one line, an <c>else</c> on the line of the statement before it - so that the
/// printer writes a helper as its source has it.
/// </summary>
public static class JavaScriptSyntaxReader {

  /// <summary>The statements of a script, as a helper's source is.</summary>
  /// <param name="source">The source.</param>
  /// <returns>The statements.</returns>
  /// <exception cref="ArgumentException">The source is no script, or one with syntax the tree does not have.</exception>
  public static IReadOnlyList<JsStatement> Statements(string source) {
    Script script;
    try {
      script = new Parser(new ParserOptions { EcmaVersion = EcmaVersion.Latest, PreserveParens = true }).ParseScript(source);
    } catch (ParseErrorException exception) {
      throw new ArgumentException($"The source is no JavaScript: {exception.Message}", nameof(source), exception);
    }

    return new Reader(source).Statements(script.Body, 0);
  }

  /// <summary>A file, parsed at a language level.</summary>
  /// <param name="source">The text of the file.</param>
  /// <param name="version">The edition of ECMA-262 to parse at.</param>
  /// <param name="module">Whether the file is an ES module.</param>
  /// <param name="problem">What the parser reports, such as syntax the level does not have.</param>
  /// <returns>The file, or <see langword="null"/> when the parser reports a problem.</returns>
  public static JsProgram? Program(string source, EcmaVersion version, bool module, out string? problem) {
    var parser = new Parser(new ParserOptions { EcmaVersion = version, PreserveParens = true });
    Acornima.Ast.Program program;
    try {
      program = module ? parser.ParseModule(source) : parser.ParseScript(source);
    } catch (ParseErrorException exception) {
      problem = exception.Message;
      return null;
    }

    problem = null;
    return new JsProgram([], new Reader(source).Statements(program.Body, 0), module);
  }

  private sealed class Reader(string source) {

    private static ArgumentException Unknown(Node node) => new($"The syntax tree has no node for '{node.Type}'.");

    private string Text(Node node) => source[node.Start..node.End];

    /// <summary>The text between two positions without its white space: the operator between two operands.</summary>
    private string Between(int from, int to) => source[from..to].Trim();

    /// <summary>
    /// What stands between the end of the statement before and a statement: a blank line where an
    /// empty line is there, and the line comments, each without its <c>//</c>.
    /// </summary>
    private JsTrivia Trivia(int from, int to) {
      var gap = source[from..to].Split('\n');
      var blank = 0;
      var comments = new List<string>();
      // The first part ends the line of what came before; every part after it is a line of its own.
      foreach (var line in gap.Skip(1).Take(gap.Length - 2).Select(line => line.Trim())) {
        if (line.Length == 0 && comments.Count == 0)
          ++blank;
        else if (line.StartsWith("//", StringComparison.Ordinal))
          comments.Add(line.Length > 2 && line[2] == ' ' ? line[3..] : line[2..]);
      }

      return blank == 0 && comments.Count == 0 ? JsTrivia.None : new JsTrivia(blank, comments);
    }

    public IReadOnlyList<JsStatement> Statements(IEnumerable<Statement> statements, int start) {
      var read = new List<JsStatement>();
      var position = start;
      foreach (var statement in statements) {
        read.Add(this.Statement(statement) with { Leading = this.Trivia(position, statement.Start) });
        position = statement.End;
      }

      return read;
    }

    private JsBlock Block(Node block, IEnumerable<Statement> statements)
      => new(this.Statements(statements, block.Start + 1)) { Inline = !this.Text(block).Contains('\n') };

    private JsVariableDeclaration Declaration(VariableDeclaration declaration)
      => new(this.Text(declaration).Split(' ')[0], [.. declaration.Declarations.Select(declarator => new JsDeclarator(
        ((Identifier)declarator.Id).Name, declarator.Init is null ? null : this.Expression(declarator.Init)))]);

    private IReadOnlyList<string> Parameters(IFunction function) => [.. function.Params.Select(parameter => ((Identifier)parameter).Name)];

    private JsStatement Statement(Statement statement) {
      switch (statement) {
        case BlockStatement block:
          return this.Block(block, block.Body);
        case VariableDeclaration declaration:
          return this.Declaration(declaration);
        case ExpressionStatement expression:
          return new JsExpressionStatement(this.Expression(expression.Expression));
        case IfStatement conditional:
          var otherwise = conditional.Alternate is null ? null : this.Statement(conditional.Alternate);
          return new JsIf(this.Expression(conditional.Test), this.Statement(conditional.Consequent), otherwise) {
            ElseInline = conditional.Alternate is not null && !source[conditional.Consequent.End..conditional.Alternate.Start].Contains('\n')
          };
        case WhileStatement loop:
          return new JsWhile(this.Expression(loop.Test), this.Statement(loop.Body));
        case DoWhileStatement loop:
          return new JsDoWhile(this.Statement(loop.Body), this.Expression(loop.Test));
        case ForStatement { Init: null or VariableDeclaration } loop:
          return new JsFor(
            loop.Init is VariableDeclaration initial ? this.Declaration(initial) : null, loop.Test is null ? null : this.Expression(loop.Test),
            loop.Update is null ? null : this.Expression(loop.Update), this.Statement(loop.Body));
        case SwitchStatement choice:
          return new JsSwitch(this.Expression(choice.Discriminant), [.. choice.Cases.Select(@case => new JsSwitchCase(
            @case.Test is null ? null : this.Expression(@case.Test), this.Statements(@case.Consequent, @case.Start)))]);
        case BreakStatement { Label: null }:
          return new JsBreak();
        case ContinueStatement { Label: null }:
          return new JsContinue();
        case ReturnStatement result:
          return new JsReturn(result.Argument is null ? null : this.Expression(result.Argument));
        case ThrowStatement exception:
          return new JsThrow(this.Expression(exception.Argument));
        case FunctionDeclaration function:
          return new JsFunctionDeclaration(function.Id!.Name, this.Parameters(function), this.Block(function.Body, function.Body.Body));
        case ExportNamedDeclaration { Declaration: Statement declared }:
          return new JsExport(this.Statement(declared));
        default:
          throw Unknown(statement);
      }
    }

    private IReadOnlyList<JsExpression> Expressions(IEnumerable<Node?> expressions) => [.. expressions.Select(expression => this.Expression(expression!))];

    private JsExpression Expression(Node expression) {
      switch (expression) {
        case NumericLiteral number:
          return new JsNumber(number.Value) { Raw = this.Text(number) };
        case BigIntLiteral big:
          return new JsBigInt(big.Value) { Raw = this.Text(big) };
        case StringLiteral text:
          return new JsString(text.Value);
        case BooleanLiteral truth:
          return new JsBoolean(truth.Value);
        case Identifier identifier:
          return new JsIdentifier(identifier.Name);
        case MemberExpression { Computed: true } member:
          return new JsIndex(this.Expression(member.Object), this.Expression(member.Property));
        case MemberExpression member:
          return new JsMember(this.Expression(member.Object), ((Identifier)member.Property).Name);
        case CallExpression { Optional: false } call:
          return new JsCall(this.Expression(call.Callee), this.Expressions(call.Arguments));
        case NewExpression creation:
          return new JsNew(this.Expression(creation.Callee), this.Expressions(creation.Arguments));
        case UpdateExpression { Prefix: true } update:
          return new JsUpdate(this.Between(update.Start, update.Argument.Start), true, this.Expression(update.Argument));
        case UpdateExpression update:
          return new JsUpdate(this.Between(update.Argument.End, update.End), false, this.Expression(update.Argument));
        case UnaryExpression unary:
          return new JsUnary(this.Between(unary.Start, unary.Argument.Start), this.Expression(unary.Argument));
        case BinaryExpression binary:
          return new JsBinary(this.Expression(binary.Left), this.Between(binary.Left.End, binary.Right.Start), this.Expression(binary.Right));
        case AssignmentExpression assignment:
          return new JsAssignment(this.Expression(assignment.Left), this.Between(assignment.Left.End, assignment.Right.Start), this.Expression(assignment.Right));
        case ConditionalExpression choice:
          return new JsConditional(this.Expression(choice.Test), this.Expression(choice.Consequent), this.Expression(choice.Alternate)) {
            BreakBeforeQuestion = source[choice.Test.End..choice.Consequent.Start].Contains('\n'),
            BreakBeforeColon = source[choice.Consequent.End..choice.Alternate.Start].Contains('\n')
          };
        case SequenceExpression sequence:
          return new JsSequence(this.Expressions(sequence.Expressions));
        case ParenthesizedExpression parenthesized:
          return new JsParenthesized(this.Expression(parenthesized.Expression));
        case ArrayExpression array when array.Elements.All(element => element is not null):
          return new JsArray(this.Expressions(array.Elements)) { Tabulated = this.Text(array).Contains('\n') };
        case ObjectExpression literal:
          return new JsObject([.. literal.Properties.Select(this.Property)]) { Multiline = this.Text(literal).Contains('\n') };
        case FunctionExpression function:
          return new JsFunctionExpression(function.Id?.Name, this.Parameters(function), this.Block(function.Body, function.Body.Body));
        default:
          throw Unknown(expression);
      }
    }

    private JsProperty Property(Node node) {
      if (node is not ObjectProperty { Computed: false, Kind: PropertyKind.Init } property)
        throw Unknown(node);

      var key = property.Key is Identifier identifier ? identifier.Name : this.Text(property.Key);
      return new JsProperty(key, this.Expression(property.Value), property.Shorthand);
    }

  }

}
