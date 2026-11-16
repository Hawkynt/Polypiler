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

  /// <summary>The class the function is a member of, or <see langword="null"/> for a function of the module.</summary>
  public ClassModel? Class { get; init; }

  /// <summary>What the function is to its class.</summary>
  public MemberKind Member { get; init; }

  /// <summary>Whether a member is static: called on the class, without <c>this</c>.</summary>
  public bool IsStatic { get; init; }

  /// <summary>The method of a base class this method replaces, or <see langword="null"/>.</summary>
  public Candidate? Overrides { get; set; }

  /// <summary>Whether a method of a class derived from this one's replaces it.</summary>
  public bool IsOverridden { get; set; }

  /// <summary>The field of a base class a getter or a setter replaces (#1402), or <see langword="null"/>.</summary>
  public ClassField? OverridesField { get; set; }

  /// <summary>The key of a member as the source spells it, with <c>#</c> when it is private.</summary>
  public string Key { get; init; } = "";

  /// <summary>The function a nested function stands in - an arrow, a function expression or a function declaration inside a function - or <see langword="null"/>.</summary>
  public FunctionModel? Parent { get; init; }

  /// <summary>Whether the function stands inside another one: it is lowered as a lambda of that one.</summary>
  public bool IsNested => this.Parent is not null;

  /// <summary>The variables of the functions around a nested function that its body sees, by name.</summary>
  public IReadOnlyDictionary<string, Variable>? Scope { get; init; }

  /// <summary>
  /// The class whose object <c>this</c> is in the body: that of a method or a constructor, and of the
  /// function an arrow stands in; <see langword="null"/> where <c>this</c> is no object of a class.
  /// </summary>
  public ClassModel? ThisClass => this.Parent is { } parent
    ? this.Function is ArrowFunctionExpression ? parent.Candidate.ThisClass : null
    : this.IsStatic ? null : this.Class;

  /// <summary>Whether a value of this function has been made: its shape exists.</summary>
  public bool HasShape => this._shape is not null;

  private FunctionShape? _shape;

  /// <summary>The shape of the function values that are this function.</summary>
  public FunctionShape Shape => this._shape ??= new FunctionShape(this);

}

/// <summary>
/// Finds the functions of a file - its function declarations, exported or not, the declarations
/// directly inside the one immediately invoked function that wraps the file, and the function-valued
/// properties of an object literal that stands in either place, also of a literal inside it - and the
/// constants that enclose them: a <c>const</c> of a number or of a table of numbers.
/// </summary>
internal sealed partial class FunctionCollector(string source, List<Comment> comments) {

  private readonly List<Candidate> _candidates = [];
  private readonly List<ModuleConstant> _constants = [];
  private readonly HashSet<string> _escaped = [];
  private readonly List<Variable> _globals = [];
  private readonly List<EnumObject> _enums = [];

  /// <summary>
  /// The enum objects of the scope that encloses the functions, in source order, and the objects of
  /// numbers that are not ones. A name that two of them share, or one of them and a function, names none.
  /// </summary>
  public IReadOnlyList<EnumObject> EnumObjects
    => this._enums.Where(named => this._enums.Count(other => other.Name == named.Name) == 1 && !this._candidates.Exists(candidate => candidate.Name == named.Name)).ToList();

  /// <summary>
  /// The variables of the module, in source order: every <c>let</c> and <c>var</c> at the top level of
  /// the file, and every <c>const</c> there of an array that is no constant table - one whose elements
  /// the file writes or that is not a list of numbers. A name that two of them share, or one of them
  /// and a function, names none.
  /// </summary>
  public IReadOnlyList<Variable> Globals {
    get {
      var tables = this.Constants.Select(constant => constant.Name).ToHashSet();
      return this._globals.Where(global => this._globals.Count(other => other.Name == global.Name) == 1
        && !tables.Contains(global.Name)
        && !this._candidates.Exists(candidate => candidate.Name == global.Name)).ToList();
    }
  }

  /// <summary>
  /// The constants of the scope that encloses the functions, in source order. A name that two
  /// constants share, or a constant and a function, names no constant. Nor does a table that the
  /// file does anything with but read an element or the length: handed on, given another name or
  /// stored into, it need not be the list its declaration shows.
  /// </summary>
  public IReadOnlyList<ModuleConstant> Constants
    => this._constants.Where(constant => this._constants.Count(other => other.Name == constant.Name) == 1
      && !this._candidates.Exists(candidate => candidate.Name == constant.Name)
      && !(constant.Type.Kind == JsKind.Array && this._escaped.Contains(constant.Name))).ToList();

  /// <summary>
  /// Notes every name the file uses as anything but the array of an element read or of a length:
  /// <c>T[i]</c> and <c>T.length</c> leave a table what it is, <c>T[i] = …</c>, <c>T[i]++</c>,
  /// <c>f(T)</c>, <c>U = T</c> and <c>T.fill(0)</c> do not. A local of the same name counts as
  /// well, which errs on the safe side.
  /// </summary>
  private void Escapes(Node node, Node? parent, Node? grandparent) {
    if (node is Identifier { Name: var name }) {
      var read = parent is MemberExpression member && member.Object == node
        && (member.Computed || source[member.Property.Start..member.Property.End] == "length")
        && !(grandparent is AssignmentExpression assignment && assignment.Left == member)
        && grandparent is not UpdateExpression
        && !(grandparent is CallExpression call && call.Callee == member);
      var named = parent is VariableDeclarator declarator && declarator.Id == node
        || parent is MemberExpression { Computed: false } owner && owner.Property == node
        || parent is Property { Computed: false } property && property.Key == node;
      if (!read && !named)
        this._escaped.Add(name);
    }

    foreach (var child in node.ChildNodes)
      this.Escapes(child, node, parent);
  }

  public List<Candidate> Collect(Program program) {
    this.Escapes(program, null, null);
    // A file that exports nothing is a script, whose declarations are all visible to whoever loads it.
    var isScript = !program.Body.Any(statement => statement is ExportNamedDeclaration or ExportDefaultDeclaration or ExportAllDeclaration);
    var constructors = ConstructorFunctions(program);
    foreach (var statement in program.Body) {
      if (this.ClassOf(statement, isScript, constructors))
        continue;

      switch (statement) {
        case FunctionDeclaration declaration:
          this.Add(new Candidate(declaration.Id!.Name, declaration, declaration) { IsPublic = isScript && declaration.Location.Start.Line >= this.EntryLine, Documentation = this.Documentation(declaration) });
          break;
        case ExportNamedDeclaration { Declaration: FunctionDeclaration declaration }:
          // The comment stands before the export or, less usually, between the export and the function.
          this.Add(new Candidate(declaration.Id!.Name, statement, declaration) { IsPublic = true, Documentation = this.Documentation(statement) ?? this.Documentation(declaration) });
          break;
        default:
          this.Objects(statement);
          this.Constant(statement);
          this.Global(statement);
          this.Enum(statement, isScript);
          break;
      }
    }

    // A variable exported by name is exported as well as one exported where it is declared.
    var exported = program.Body.OfType<ExportNamedDeclaration>().Where(export => export.Declaration is null && export.Source is null)
      .SelectMany(export => export.Specifiers).Select(specifier => specifier.Local)
      .Concat(program.Body.OfType<ExportDefaultDeclaration>().Select(export => export.Declaration))
      .OfType<Identifier>().Select(identifier => identifier.Name).ToHashSet();
    foreach (var global in this._globals.Where(global => exported.Contains(global.Name)))
      global.Problem = $"the exported variable {global.Name}";

    this.Prototypes(program);
    for (var index = 0; index < this._enums.Count; ++index)
      if (exported.Contains(this._enums[index].Name) && this._enums[index].Declaration is VariableDeclarator { Init: { } value })
        this._enums[index] = EnumObject.Of(this._enums[index].Name, this._enums[index].Declaration, value, true)!;

    var wrappers = program.Body.Select(Wrapped).OfType<FunctionBody>().ToList();
    if (wrappers.Count == 1) {
      foreach (var statement in wrappers[0].Body) {
        // A function of the wrapper is a helper of the file: nothing outside can call it.
        if (statement is FunctionDeclaration helper)
          this.Add(new Candidate(helper.Id!.Name, helper, helper) { Documentation = this.Documentation(helper) });

        this.Objects(statement);
        this.Constant(statement);
        this.Enum(statement, false);
      }
    }

    this.Initialize(program);
    return this._candidates.OrderBy(candidate => candidate.Anchor.Start).ToList();
  }

  /// <summary>The statements at the top level that do something when the module is loaded, in source order (#1365).</summary>
  public IReadOnlyList<Statement> Effects { get; private set; } = [];

  /// <summary>The function of the effects, which the module runs once when it is loaded, or <see langword="null"/> for a module without any.</summary>
  public Candidate? Initializer { get; private set; }

  /// <summary>
  /// Gathers the statements of the top level that are no declaration - a call, an <c>if</c>, a loop, a
  /// variable of an object or a computed value - into one function, which the module runs once when it
  /// is loaded: the registration of what it defines, as the Cipher algorithms do. A variable it declares
  /// is a local of that function. A module without variables of its own has no state such statements
  /// could change for its functions, and they are not looked at, as before.
  /// </summary>
  private void Initialize(Program program) {
    if (this._globals.Count == 0)
      return;

    var held = this._constants.Select(constant => constant.Name).Concat(this._globals.Select(global => global.Name))
      .Concat(this._enums.Select(named => named.Name)).Concat(this._candidates.Select(candidate => candidate.Owner).OfType<string>()).ToHashSet();
    this.Effects = [.. program.Body.Where(statement => statement switch {
      FunctionDeclaration or ClassDeclaration or Directive or EmptyStatement or ImportDeclaration or ExportNamedDeclaration or ExportDefaultDeclaration or ExportAllDeclaration => false,
      VariableDeclaration declaration => !declaration.Declarations.Select(declarator => declarator.Id).OfType<Identifier>().Any(id => held.Contains(id.Name)),
      _ => !this.ClassStatements.Contains(statement) && Wrapped(statement) is null
    })];
    if (this.Effects.Count == 0)
      return;

    var (first, last) = (this.Effects[0], this.Effects[^1]);
    var range = Acornima.Range.From(first.Start, last.End);
    var location = SourceLocation.From(first.Location.Start, last.Location.End, first.Location.SourceFile);
    var body = new FunctionBody(NodeList.From(this.Effects), true) { Range = range, Location = location };
    var function = new FunctionExpression(null, NodeList.From<Node>([]), body, false, false) { Range = range, Location = location };
    var name = "initialize";
    for (var suffix = 1; this._candidates.Exists(candidate => candidate.Name == name) || held.Contains(name); ++suffix)
      name = $"initialize{suffix}";

    this.Initializer = new Candidate(name, first, function);
    this._candidates.Add(this.Initializer);
  }

  /// <summary>Takes the enum objects a <c>const</c> declares, exported where the file exports the declaration or is a script.</summary>
  private void Enum(Statement statement, bool isScript) {
    var exported = statement is ExportNamedDeclaration;
    var declaration = statement is ExportNamedDeclaration { Declaration: VariableDeclaration inner } ? inner : statement as VariableDeclaration;
    if (declaration is not { Kind: VariableDeclarationKind.Const })
      return;

    foreach (var declarator in declaration.Declarations)
      if (declarator is { Id: Identifier { Name: var name }, Init: { } value } && !name.Contains('$') && EnumObject.Of(name, declarator, value, exported || isScript) is { } named)
        this._enums.Add(named);
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

  /// <summary>
  /// Takes the variables a declaration at the top level of the file declares: those of a <c>let</c> or
  /// a <c>var</c>, and those of a <c>const</c> that holds an array. An exported one is taken as well,
  /// so that a function that uses it is refused for it rather than for an unknown name.
  /// </summary>
  private void Global(Statement statement) {
    var exported = statement is ExportNamedDeclaration;
    var declaration = statement is ExportNamedDeclaration { Declaration: VariableDeclaration inner } ? inner : statement as VariableDeclaration;
    if (declaration is null)
      return;

    foreach (var declarator in declaration.Declarations) {
      if (declarator.Id is not Identifier { Name: var name } id || name.Contains('$'))
        continue;

      var constant = declaration.Kind == VariableDeclarationKind.Const;
      if (constant && !IsArray(declarator.Init))
        continue;

      this._globals.Add(new Variable(name, id, false) {
        IsGlobal = true,
        IsConstant = constant,
        IsUninitialized = declarator.Init is null,
        Initializer = declarator.Init,
        Problem = exported ? $"the exported variable {name}" : null
      });
    }
  }

  /// <summary>Whether an initializer is an array: a list, or a new typed array.</summary>
  private static bool IsArray(Node? value)
    => value is ArrayExpression || value is NewExpression { Callee: Identifier { Name: var constructor } } && TypeInference.TypedArrays.ContainsKey(constructor);

  private static ModuleConstant? ConstantOf(string name, Node value) {
    // A string, and a list of strings, which is a table of strings.
    if (value is StringLiteral text)
      return new ModuleConstant(name, JsType.String, IrModuleConst.OfStrings(name, IrType.String, [text.Value]));

    if (value is ArrayExpression { Elements.Count: > 0 } texts && texts.Elements.All(item => item is StringLiteral))
      return new ModuleConstant(name, JsType.ArrayOf(JsType.String, true), IrModuleConst.OfStrings(name, IrType.Array(IrType.String), [.. texts.Elements.Select(item => ((StringLiteral)item!).Value)]));

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
        // A function of an object a required file declares is a helper of that file, as a function it declares is.
        IsPublic = property.Location.Start.Line >= this.EntryLine,
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
