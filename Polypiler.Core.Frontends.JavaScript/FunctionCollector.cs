using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>A function of the source file that the import considers. Two candidates are equal only when they are the same one.</summary>
/// <param name="name">The name of the function: of the declaration, or of the property that holds it.</param>
/// <param name="anchor">The node the function starts with, which its JSDoc comment precedes: the declaration, the <c>export</c> or the property.</param>
/// <param name="function">The function itself.</param>
internal sealed class Candidate(string name, Node anchor, IFunction function) {

  /// <summary>The name of the function.</summary>
  public string Name { get; } = name;

  /// <summary>The node the JSDoc comment precedes and diagnostics about the whole function point at.</summary>
  public Node Anchor { get; } = anchor;

  /// <summary>The function itself.</summary>
  public IFunction Function { get; } = function;

  /// <summary>Whether the function is a property of an object literal rather than a declaration.</summary>
  public bool IsProperty { get; init; }

  /// <summary>The variable the object literal of a property is assigned to, through which its functions call each other.</summary>
  public string? Owner { get; init; }

  /// <summary>Whether the source file itself makes the function visible to others.</summary>
  public bool IsPublic { get; init; }

  /// <summary>The text of the JSDoc comment right before the function, or <see langword="null"/>.</summary>
  public string? Documentation { get; init; }

  /// <summary>Why the function cannot be imported whatever its body is, or <see langword="null"/>.</summary>
  public string? Problem { get; set; }

}

/// <summary>
/// Finds the functions of a file: its function declarations, exported or not, and the
/// function-valued properties of an object literal that stands at the top level or directly inside
/// the one immediately invoked function that wraps the file.
/// </summary>
internal sealed class FunctionCollector(string source, List<Comment> comments) {

  private readonly List<Candidate> _candidates = [];

  public List<Candidate> Collect(Program program) {
    // A file that exports nothing is a script, whose declarations are all visible to whoever loads it.
    var isScript = !program.Body.Any(statement => statement is ExportNamedDeclaration or ExportDefaultDeclaration or ExportAllDeclaration);
    foreach (var statement in program.Body) {
      switch (statement) {
        case FunctionDeclaration declaration:
          this.Add(new Candidate(declaration.Id!.Name, declaration, declaration) { IsPublic = isScript, Documentation = this.Documentation(declaration) });
          break;
        case ExportNamedDeclaration { Declaration: FunctionDeclaration declaration }:
          // The comment stands before the export or, less usually, between the export and the function.
          this.Add(new Candidate(declaration.Id!.Name, statement, declaration) { IsPublic = true, Documentation = this.Documentation(statement) ?? this.Documentation(declaration) });
          break;
        default:
          this.Objects(statement);
          break;
      }
    }

    var wrappers = program.Body.Select(Wrapped).OfType<FunctionBody>().ToList();
    if (wrappers.Count == 1)
      foreach (var statement in wrappers[0].Body)
        this.Objects(statement);

    return this._candidates.OrderBy(candidate => candidate.Anchor.Start).ToList();
  }

  /// <summary>The body of the function a statement invokes on the spot, as <c>(function () { … })()</c> does.</summary>
  private static FunctionBody? Wrapped(Statement statement)
    => statement is ExpressionStatement { Expression: CallExpression { Callee: IFunction wrapper } } ? wrapper.Body as FunctionBody : null;

  /// <summary>Takes the functions of an object literal that a statement declares, assigns or returns.</summary>
  private void Objects(Statement statement) {
    switch (statement) {
      case ExportNamedDeclaration { Declaration: VariableDeclaration exported }:
        this.Objects(exported);
        break;
      case VariableDeclaration declaration:
        foreach (var declarator in declaration.Declarations)
          if (declarator.Init is ObjectExpression initializer)
            this.Properties(initializer, (declarator.Id as Identifier)?.Name);

        break;
      case ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Right: ObjectExpression assigned } assignment }:
        this.Properties(assigned, (assignment.Left as Identifier)?.Name);
        break;
      case ReturnStatement { Argument: ObjectExpression returned }:
        this.Properties(returned, null);
        break;
    }
  }

  private void Properties(ObjectExpression literal, string? owner) {
    foreach (var member in literal.Properties) {
      if (member is not Property { Value: IFunction function } property)
        continue;

      var named = property is { Computed: false, Key: Identifier };
      var problem = !named ? "a computed or quoted property name"
        : property.Kind == PropertyKind.Get ? "a getter"
        : property.Kind == PropertyKind.Set ? "a setter"
        : function is ArrowFunctionExpression ? "an arrow function"
        : null;
      this.Add(new Candidate(named ? ((Identifier)property.Key).Name : source[property.Key.Start..property.Key.End], property, function) {
        IsProperty = true,
        Owner = owner,
        IsPublic = true,
        Documentation = this.Documentation(property),
        Problem = problem
      });
    }
  }

  /// <summary>
  /// Adds a function. A name that two functions share names neither: JavaScript would run the later
  /// declaration, and a module of the IR has one function per name.
  /// </summary>
  private void Add(Candidate candidate) {
    foreach (var earlier in this._candidates.Where(earlier => earlier.Name == candidate.Name)) {
      earlier.Problem ??= $"a second function named {candidate.Name}";
      candidate.Problem ??= $"a second function named {candidate.Name}";
    }

    this._candidates.Add(candidate);
  }

  /// <summary>The <c>/** … */</c> comment that ends right before a node, with nothing but white space between the two.</summary>
  private string? Documentation(Node node) {
    var index = comments.FindLastIndex(comment => comment.End <= node.Start);
    if (index < 0)
      return null;

    var comment = comments[index];
    var text = source[comment.Start..comment.End];
    return comment.Kind == CommentKind.Block && text.StartsWith("/**", System.StringComparison.Ordinal) && string.IsNullOrWhiteSpace(source[comment.End..node.Start])
      ? text
      : null;
  }

}
