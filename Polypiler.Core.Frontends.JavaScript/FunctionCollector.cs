using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

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
/// Finds the functions of a file - its function declarations, exported or not, the declarations
/// directly inside the one immediately invoked function that wraps the file, and the function-valued
/// properties of an object literal that stands in either place, also of a literal inside it - and the
/// constants that enclose them: a <c>const</c> of a number or of a table of numbers.
/// </summary>
internal sealed class FunctionCollector(string source, List<Comment> comments) {

  private readonly List<Candidate> _candidates = [];
  private readonly List<ModuleConstant> _constants = [];

  /// <summary>
  /// The constants of the scope that encloses the functions, in source order. A name that two
  /// constants share, or a constant and a function, names no constant.
  /// </summary>
  public IReadOnlyList<ModuleConstant> Constants
    => this._constants.Where(constant => this._constants.Count(other => other.Name == constant.Name) == 1 && !this._candidates.Exists(candidate => candidate.Name == constant.Name)).ToList();

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
          this.Constant(statement);
          break;
      }
    }

    var wrappers = program.Body.Select(Wrapped).OfType<FunctionBody>().ToList();
    if (wrappers.Count == 1) {
      foreach (var statement in wrappers[0].Body) {
        // A function of the wrapper is a helper of the file: nothing outside can call it.
        if (statement is FunctionDeclaration helper)
          this.Add(new Candidate(helper.Id!.Name, helper, helper) { Documentation = this.Documentation(helper) });

        this.Objects(statement);
        this.Constant(statement);
      }
    }

    return this._candidates.OrderBy(candidate => candidate.Anchor.Start).ToList();
  }

  /// <summary>Takes the constants a <c>const</c> declares with a number, a list of numbers or a typed array of listed numbers.</summary>
  private void Constant(Statement statement) {
    var declaration = statement is ExportNamedDeclaration { Declaration: VariableDeclaration exported } ? exported : statement as VariableDeclaration;
    if (declaration is not { Kind: VariableDeclarationKind.Const })
      return;

    foreach (var declarator in declaration.Declarations)
      if (declarator is { Id: Identifier { Name: var name }, Init: { } value } && !name.Contains('$') && ConstantOf(name, value) is { } constant)
        this._constants.Add(constant);
  }

  private static ModuleConstant? ConstantOf(string name, Node value) {
    if (Literal(value) is { } scalar)
      return scalar.Type.Ir is { } type ? new ModuleConstant(name, scalar.Type, new IrModuleConst(name, type, [Encode(scalar, type)!.Value])) : null;

    // A typed array of listed numbers holds them as its elements do; a list holds them as they are.
    var typed = value is NewExpression { Callee: Identifier { Name: var constructor }, Arguments: [ArrayExpression] } && TypeInference.TypedArrays.TryGetValue(constructor, out var stated) ? stated : null;
    if ((value is NewExpression creation ? typed is null ? null : creation.Arguments[0] : value) is not ArrayExpression { Elements.Count: > 0 } list)
      return null;

    var items = list.Elements.Select(item => item is null ? null : Literal(item)).ToList();
    if (items.Contains(null))
      return null;

    var element = typed ?? items.Select(item => item!.Value.Type).Aggregate(JsType.Join).Ir;
    var values = items.Select(item => element is null ? null : Encode(item!.Value, element)).ToList();
    return values.Contains(null) ? null : new ModuleConstant(name, JsType.ArrayOf(JsType.Of(element!), false), new IrModuleConst(name, IrType.Array(element!), values.Select(item => item!.Value).ToList()));
  }

  /// <summary>A numeric or <c>BigInt</c> literal, negated or not: its type and its value.</summary>
  private static (JsType Type, double Number, System.Numerics.BigInteger Big)? Literal(Node node) => node switch {
    NumericLiteral number => (JsType.Literal(number.Value), number.Value, default),
    BigIntLiteral big => (JsType.BigLiteral(big.Value), 0, big.Value),
    UnaryExpression { Operator: Operator.UnaryNegation, Argument: NumericLiteral number } => (JsType.Literal(-number.Value), -number.Value, default),
    UnaryExpression { Operator: Operator.UnaryNegation, Argument: BigIntLiteral big } => (JsType.BigLiteral(-big.Value), 0, -big.Value),
    _ => null
  };

  /// <summary>
  /// A literal as a constant of a type carries it, converted the way a store into a typed array
  /// converts; <see langword="null"/> when the literal is of the other kind than the type.
  /// </summary>
  private static long? Encode((JsType Type, double Number, System.Numerics.BigInteger Big) literal, IrType type) {
    if (type.IsLong != (literal.Type.Kind == JsKind.BigInt))
      return null;

    if (type.IsLong)
      return (long)(ulong)(literal.Big & ulong.MaxValue);

    return type.IsFloat ? IrOperators.Bits(type, literal.Number) : IrOperators.Wrap(type, IrOperators.ToWord(IrType.I32, literal.Number));
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
      // The functions of a literal inside the literal are reached through the property that holds it.
      if (member is Property { Computed: false, Key: Identifier { Name: var inner }, Value: ObjectExpression nested })
        this.Properties(nested, owner is null ? null : $"{owner}.{inner}");

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
