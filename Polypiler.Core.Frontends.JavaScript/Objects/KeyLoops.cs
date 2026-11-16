using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// A loop over the keys of an object of a known shape (#1373): <c>Object.keys(o).forEach(key =&gt; …)</c>
/// or <c>for (const key of Object.keys(o)) …</c>, where <c>o</c> is an object of a class of the file or
/// a record. Its keys are known when the module is imported, so the loop is its body once per key, in
/// the order <c>Object.keys</c> gives them - the order the constructors create the fields, the base
/// class first - with <c>key</c> the string constant, <c>o[key]</c> the field and <c>r[key] = v</c> the
/// store into the field of that name.
/// </summary>
/// <param name="Key">The variable of the key.</param>
/// <param name="Object">The object whose keys the loop goes over.</param>
/// <param name="Body">The body: a block, or the expression of an arrow.</param>
internal sealed record KeyLoop(Identifier Key, Expression Object, Node Body) {

  /// <summary>The loop a statement is, or <see langword="null"/>.</summary>
  public static KeyLoop? Of(Statement statement) => statement switch {
    ExpressionStatement {
      Expression: CallExpression {
        Callee: MemberExpression { Computed: false, Object: CallExpression keys, Property: Identifier { Name: "forEach" } },
        Arguments: [ArrowFunctionExpression { Params: [Identifier key], Async: false } arrow]
      }
    } when Keys(keys) is { } target
      => new KeyLoop(key, target, arrow.Body),
    ForOfStatement { Await: false, Left: VariableDeclaration { Declarations: [{ Id: Identifier key }] }, Right: CallExpression keys } loop when Keys(keys) is { } target
      => new KeyLoop(key, target, loop.Body),
    _ => null
  };

  /// <summary>The argument of <c>Object.keys(o)</c>, or <see langword="null"/> for any other expression.</summary>
  public static Expression? Keys(CallExpression call)
    => call is { Callee: MemberExpression { Computed: false, Object: Identifier { Name: "Object" }, Property: Identifier { Name: "keys" } }, Arguments: [Expression target] } ? target : null;

  /// <summary>Whether the body leaves the loop or the function - a <c>return</c> of the callback, a <c>break</c> or a <c>continue</c> of its own - which a body written out once per key cannot.</summary>
  public bool Jumps => Lowering.Contains(this.Body, node => node is ReturnStatement or BreakStatement or ContinueStatement or YieldExpression or AwaitExpression);

  /// <summary>The keys of an object of a type, in the order <c>Object.keys</c> gives them; <see langword="null"/> for a type of no known shape.</summary>
  public static IReadOnlyList<string>? KeysOf(JsType type) => type switch {
    { Kind: JsKind.Object, IsAbsent: false, Class: { } owner } => owner.Keys,
    { Kind: JsKind.Record, IsAbsent: false, Shape: { } shape } => [.. shape.Keys],
    _ => null
  };

}

internal sealed partial class Lowering {

  /// <summary>The key each variable of a key loop stands for while its body is written out.</summary>
  private readonly Dictionary<Variable, string> _keys = [];

  /// <summary>Writes a key loop out once per key of its object, in their order; <see langword="false"/> for a statement that is none.</summary>
  private bool KeyLoop(Statement statement, List<IrStmt> into) {
    if (JavaScript.KeyLoop.Of(statement) is not { } loop)
      return false;

    if (loop.Jumps)
      throw this.Unsupported(statement, "a return, a break or a continue in a loop over Object.keys");

    var type = types.Of(loop.Object);
    var keys = JavaScript.KeyLoop.KeysOf(type) ?? throw this.Unsupported(loop.Object, $"Object.keys of {type.Describe()}, whose keys are not known");
    var variable = (Variable)this._model.Bindings[loop.Key];
    // An object that may be of a class made for literals or for copied keys has the keys of its class: the loop is written once per class (#1388).
    if (type.Class is { } owner && types.Shapes.Classes.Below(owner) is { Count: > 1 } classes) {
      if (loop.Object is not Identifier || this._model.Bindings.GetValueOrDefault(loop.Object) is not Variable narrowed)
        throw this.Unsupported(loop.Object, $"Object.keys of {types.Text(loop.Object)}, which may be an object of a synthesized class and is no variable");

      var target = this.Expression(loop.Object);
      IrStmt? choice = null;
      foreach (var below in classes.Reverse()) {
        this._narrowed[narrowed] = below;
        try {
          var written = new IrBlock([this.WrittenOut(loop, variable, below.Keys)]);
          this.Copied(narrowed, below, loop, written);
          choice = choice is null ? written : new IrIf(new IrTypeTest(target, IrType.Object(below.Ir)), written, choice as IrBlock ?? new IrBlock([choice]));
        } finally {
          this._narrowed.Remove(narrowed);
        }
      }

      into.Add(choice!);
      return true;
    }

    into.Add(this.WrittenOut(loop, variable, keys));
    return true;
  }

  /// <summary>The variables of the objects of key loops that a branch of the loop knows the class of, with that class (#1388).</summary>
  private readonly Dictionary<Variable, ClassModel> _narrowed = [];

  /// <summary>
  /// Refuses a branch of a key loop that does not store every field of the class made for the keys it
  /// copies on every pass - a key a condition leaves out or copies only at run time - which the object
  /// of JavaScript would lack, while the class has its field (#1388).
  /// </summary>
  private void Copied(Variable source, ClassModel from, KeyLoop loop, IrBlock written) {
    static IEnumerable<IrStmt> Always(IrBlock block) => block.Statements.SelectMany(statement => statement is IrBlock inner ? Always(inner) : [statement]);
    static IEnumerable<IrStmt> All(IrStmt statement)
      => (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements)).SelectMany(All).Prepend(statement);

    var always = Always(written).OfType<IrFieldStore>().ToHashSet(ReferenceEqualityComparer.Instance);
    var stores = All(written).OfType<IrFieldStore>().ToList();
    foreach (var extension in types.Shapes.Classes.Extensions.Where(extension => this._model.Bindings.GetValueOrDefault(extension.Source) == source)) {
      if (!extension.Made.TryGetValue(from, out var made))
        continue;

      foreach (var field in made.Fields) {
        var into = stores.Where(store => store.Field == field.Ir).ToList();
        if (into.Count == 0 || into.Exists(store => !always.Contains(store)))
          throw this.Unsupported(loop.Object, $"the key {field.Key} of {from.Name}, which is not copied into new {extension.Created.Name} on every pass");
      }
    }
  }

  /// <summary>The body of a key loop once per key, in order.</summary>
  private IrBlock WrittenOut(KeyLoop loop, Variable variable, IReadOnlyList<string> keys) {
    // The object is read for its keys only; it is no part of what the body computes.
    var statements = new List<IrStmt>();
    foreach (var key in keys) {
      this._keys[variable] = key;
      try {
        if (loop.Body is Statement body)
          statements.Add(this.Body(body));
        else
          this.Effect((Expression)loop.Body, statements);
      } finally {
        this._keys.Remove(variable);
      }
    }

    return new IrBlock(statements);
  }

  /// <summary>
  /// <c>Object.keys(o)</c> as a value, of an object of a known shape: the Array of its keys, which are
  /// constants; the object is read for nothing but its shape, so it may have no effect.
  /// </summary>
  private IrExpr? KeysValue(Node node) {
    // An enum object handed to Object.keys is used as a value, which the call refuses by its name.
    if (node is not CallExpression call || JavaScript.KeyLoop.Keys(call) is not { } target || this._model.Bindings.GetValueOrDefault(target) is EnumObject)
      return null;

    var type = types.Of(target);
    var keys = JavaScript.KeyLoop.KeysOf(type) ?? throw this.Unsupported(target, $"Object.keys of {type.Describe()}, whose keys are not known");
    if (IrTree.Nodes(this.Expression(target)).Any(part => part is IrCall or IrStep or IrMethodCall or IrNew or IrBind or IrCollection))
      throw this.Unsupported(call, "Object.keys of an operand with an effect");

    static IrExpr Keys(IEnumerable<string> keys) => new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [.. keys.Select(key => (IrExpr)new IrStrConst(key))]);
    // An object that may be of a class made for literals or for copied keys has the keys of its class (#1388).
    if (type.Class is { } owner && types.Shapes.Classes.Below(owner) is { Count: > 1 } classes) {
      var value = this.Expression(target);
      return classes.Reverse().Skip(1).Aggregate(Keys(owner.Keys), (rest, below) => new IrConditional(new IrTypeTest(value, IrType.Object(below.Ir)), Keys(below.Keys), rest));
    }

    return Keys(keys);
  }

  /// <summary>The value of a condition that is a constant or the negation of one; <see langword="null"/> for any other.</summary>
  private static bool? Decided(IrExpr condition) => condition switch {
    IrConst { Type.Kind: IrTypeKind.Bool } constant => constant.Value != 0,
    IrUnary { Op: IrUnaryOp.LogicalNot, Operand: var operand } => !Decided(operand),
    _ => null
  };

  /// <summary>The key a node stands for in a key loop, or <see langword="null"/>.</summary>
  private string? KeyOf(Node node)
    => node is Identifier && this._model.Bindings.GetValueOrDefault(node) is Variable variable && this._keys.TryGetValue(variable, out var key) ? key : null;

  /// <summary>
  /// <c>o[key]</c> of the key of a key loop: the field of that name, of an object or of a record;
  /// <see langword="null"/> for a member whose key is no such key.
  /// </summary>
  private IrExpr? KeyedMember(MemberExpression member) {
    if (!member.Computed || this.KeyOf(member.Property) is not { } key)
      return null;

    var holder = types.Of(member.Object);
    // In the branch of a class of its own, the object is read as one of that class (#1388).
    if (member.Object is Identifier && this._model.Bindings.GetValueOrDefault(member.Object) is Variable read && this._narrowed.TryGetValue(read, out var narrowed) && narrowed.Field(key) is { } own)
      return Read(new IrFieldLoad(new IrCast(IrType.Object(narrowed.Ir), this.Expression(member.Object)), own.Ir!));

    if (holder is { Kind: JsKind.Object, Class: { } owner } && owner.Field(key) is { } field)
      return Read(new IrFieldLoad(this.Expression(member.Object), field.Ir!));

    if (holder is { Kind: JsKind.Record, Shape: { } shape } && shape.TypeOf(key) is not null && this.Expression(member.Object) is { Type.StructType: { } record } value)
      return new IrFieldLoad(value, record.Fields.First(item => item.Name == key));

    throw this.Unsupported(member, $"the key {key} of {holder.Describe()}, which it does not have");
  }

  /// <summary><c>r[key] = v</c> of the key of a key loop: the store into the field of that name; <see langword="false"/> for any other assignment.</summary>
  private bool KeyedStore(AssignmentExpression assignment, List<IrStmt> into) {
    if (assignment is not { Left: MemberExpression { Computed: true } member, Operator: Acornima.Operator.Assignment } || this.KeyOf(member.Property) is not { } key)
      return false;

    var holder = types.Of(member.Object);
    if (holder is not { Kind: JsKind.Object, Class: { } owner } || owner.Frozen is not null)
      throw this.Unsupported(assignment, $"a store of the key {key} into {holder.Describe()}");

    var target = this.Expression(member.Object);
    // The one object a new made that keys are copied into is of the class made for the class of the object they come from (#1388).
    if (member.Object is Identifier && this._model.Bindings.GetValueOrDefault(member.Object) is Variable { Creation: { } creation }
        && types.Shapes.Classes.ExtensionsOf(creation) is { } extension && this._model.Bindings.GetValueOrDefault(extension.Source) is Variable copiedFrom
        && this._narrowed.TryGetValue(copiedFrom, out var fromClass) && extension.Made.TryGetValue(fromClass, out var made)) {
      owner = made;
      target = new IrCast(IrType.Object(made.Ir), target);
    }

    if (owner.Field(key) is not { } field)
      throw this.Unsupported(assignment, $"the property {key} added to {owner.Name} outside its constructor");

    // A field copied from the object of a branch is that class's field, of its own type (#1388).
    var value = assignment.Right is MemberExpression { Computed: true, Object: Identifier copied } source && this.KeyOf(source.Property) == key
      && this._model.Bindings.GetValueOrDefault(copied) is Variable origin && this._narrowed.TryGetValue(origin, out var from)
      ? this.To(new Typed(this.KeyedMember(source)!, from.Field(key)!.Variable.Type), field.Ir!.Type, assignment.Right)
      : this.Value(assignment.Right, field.Ir!.Type);
    into.Add(new IrFieldStore(target, field.Ir!, value));
    return true;
  }

  /// <summary>
  /// <c>[…].includes(key)</c> of a literal array of strings and a key of a key loop, or any other
  /// constant string: whether the literal holds it, decided when the module is imported.
  /// </summary>
  private IrExpr? IncludesKey(Node node) {
    if (node is not CallExpression { Callee: MemberExpression { Computed: false, Object: ArrayExpression literal, Property: Identifier { Name: "includes" } }, Arguments: [var argument] }
        || !literal.Elements.All(element => element is StringLiteral) || this.KeyOf(argument) is not { } key)
      return null;

    return new IrConst(IrType.Bool, literal.Elements.Cast<StringLiteral>().Any(element => element.Value == key) ? 1 : 0);
  }

}
