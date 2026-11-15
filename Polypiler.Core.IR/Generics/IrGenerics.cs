using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>What a constraint of a type parameter demands of its type argument.</summary>
public enum IrGenericConstraintKind {

  /// <summary>A value type, C# <c>struct</c>.</summary>
  Struct,

  /// <summary>A reference type, C# <c>class</c>.</summary>
  Class,

  /// <summary>A value type without references, C# <c>unmanaged</c> (C# 7.3).</summary>
  Unmanaged,

  /// <summary>A type that is not nullable, C# <c>notnull</c> (C# 8).</summary>
  NotNull,

  /// <summary>An implementation of <c>IEquatable&lt;T&gt;</c> of the parameter itself.</summary>
  Equatable,

  /// <summary>An implementation of <c>IComparable&lt;T&gt;</c> of the parameter itself.</summary>
  Comparable,

  /// <summary>An implementation of an interface of the module, which <see cref="IrGenericConstraint.Interface"/> names.</summary>
  Interface,

  /// <summary>A public constructor without parameters, C# <c>new()</c>.</summary>
  New

}

/// <summary>A constraint of a type parameter, as the source states it; the backends write it back where they write the definition generic.</summary>
/// <param name="Parameter">The name of the type parameter.</param>
/// <param name="Kind">What it demands.</param>
public sealed record IrGenericConstraint(string Parameter, IrGenericConstraintKind Kind) {

  /// <summary>The interface of the module of an <see cref="IrGenericConstraintKind.Interface"/> constraint; otherwise <see langword="null"/>.</summary>
  public IrType? Interface { get; init; }

}

/// <summary>
/// Where a function or a class of the module comes from when it is one instantiation of a generic
/// definition of the source. The IR holds no type parameter: a generic method or class becomes one
/// function or class per list of type arguments the module uses (monomorphization), each typed with
/// those arguments and named <c>Name&lt;arguments&gt;</c> (<see cref="IrGenerics.Mangle"/>). The
/// origin is metadata for the backends, which may write a set of instantiations back as one generic
/// definition (C#) or as one function when their text is the same (JavaScript).
/// </summary>
/// <param name="Name">The name of the generic definition, without its type parameters.</param>
/// <param name="TypeParameters">The names of its type parameters, in order.</param>
/// <param name="TypeArguments">The type arguments of this instantiation, one per type parameter.</param>
/// <param name="Constraints">The constraints of the type parameters, in the order the source states them.</param>
public sealed record IrGenericOrigin(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<IrType> TypeArguments, IReadOnlyList<IrGenericConstraint> Constraints) {

  /// <summary>
  /// The definition with its type parameters, when the frontend could state it: every instantiation
  /// whose body is <see cref="IrGenerics.Instantiate"/> of it with its own type arguments is that
  /// definition, so that a backend may write the definition instead of the instantiations.
  /// </summary>
  public IrGenericTemplate? Template { get; init; }

  /// <summary>The definition as the source names it, with its type parameters: <c>IndexOf&lt;T&gt;</c>.</summary>
  public string Display => $"{this.Name}<{string.Join(", ", this.TypeParameters)}>";

}

/// <summary>
/// The body of a generic method with its type parameters standing as types of their own: each is an
/// interface without members (<see cref="Parameters"/>), which no module declares. It is never
/// verified, interpreted or emitted as it stands; <see cref="IrGenerics.Instantiate"/> turns it into
/// an instantiation, and a backend that writes a generic definition writes it.
/// <para>
/// In a template <c>default(T)</c> is <see cref="IrNull"/> of the parameter, <c>new T()</c> is
/// <see cref="IrNew"/> of it with <see cref="IrGenerics.Parameterless"/>, and a call of another
/// generic method with arguments that name the parameters is an <see cref="IrCall"/> of the name
/// <see cref="Calls"/> explains.
/// </para>
/// </summary>
/// <param name="Function">The body, named with the parameters: <c>IndexOf&lt;T&gt;</c>.</param>
/// <param name="Parameters">The types that stand for the type parameters, in order.</param>
public sealed record IrGenericTemplate(IrFunction Function, IReadOnlyList<IrType> Parameters) {

  /// <summary>Every name of a call of the template that names an instantiation in terms of the parameters, with what it instantiates.</summary>
  public IReadOnlyDictionary<string, IrGenericCall> Calls { get; init; } = new Dictionary<string, IrGenericCall>();

}

/// <summary>A call of a template that instantiates a generic method with type arguments that name the parameters of the template.</summary>
/// <param name="Origin">The name of the generic method called.</param>
/// <param name="Arguments">Its type arguments, in terms of the template's parameters.</param>
public sealed record IrGenericCall(string Origin, IReadOnlyList<IrType> Arguments);

/// <summary>
/// The equality a generic definition uses - <c>IEquatable&lt;T&gt;.Equals</c>,
/// <c>EqualityComparer&lt;T&gt;.Default.Equals</c> - which is not the operator: integers and truth
/// values compare by value, a floating-point number as <c>Double.Equals</c> does, so that a NaN
/// equals a NaN and <c>-0.0</c> equals <c>0.0</c> (ECMA-262's SameValueZero), and references by
/// identity, a record by its fields as <see cref="IrStructEquals"/> says. Both operands are evaluated,
/// the left first; neither may trap on <c>null</c>.
/// </summary>
/// <param name="Left">The value evaluated first.</param>
/// <param name="Right">The value evaluated second, of the same type.</param>
public sealed record IrGenericEquals(IrExpr Left, IrExpr Right) : IrExpr(IrType.Bool);

/// <summary>
/// The order a generic definition uses - <c>IComparable&lt;T&gt;.CompareTo</c>,
/// <c>Comparer&lt;T&gt;.Default.Compare</c> - as .NET computes it for the instantiated type: for
/// <c>sbyte</c>, <c>byte</c>, <c>short</c>, <c>ushort</c> and <c>char</c> the difference of the two
/// values, for every other integer, <c>bool</c> (false first) and floating-point number -1, 0 or 1,
/// a NaN below every number and equal to a NaN, <c>-0.0</c> equal to <c>0.0</c>. Both operands are
/// evaluated, the left first.
/// </summary>
/// <param name="Left">The value evaluated first.</param>
/// <param name="Right">The value evaluated second, of the same type.</param>
public sealed record IrGenericCompare(IrExpr Left, IrExpr Right) : IrExpr(IrType.I32);

/// <summary>The names, the defaults and the substitution of the instantiations of generic definitions.</summary>
public static class IrGenerics {

  /// <summary>The constructor <c>new T()</c> names in a template, where the class is not known yet.</summary>
  public static IrConstructor Parameterless { get; } = new("new()");

  /// <summary>The name of an instantiation: the definition's name and the type arguments as the IR writes them, <c>IndexOf&lt;f64&gt;</c>.</summary>
  /// <param name="name">The name of the definition.</param>
  /// <param name="arguments">The type arguments.</param>
  /// <returns>The name.</returns>
  public static string Mangle(string name, IEnumerable<IrType> arguments) => $"{name}<{string.Join(",", arguments)}>";

  /// <summary>The name of the definition an instantiation's name starts with: the name itself for any other.</summary>
  /// <param name="name">The name of a function or a class.</param>
  /// <returns>The part before the first <c>&lt;</c>.</returns>
  public static string SourceName(string name) => name.IndexOf('<', StringComparison.Ordinal) is var at and >= 0 ? name[..at] : name;

  /// <summary>Whether a name is the name of an instantiation or of a clone: one with type arguments.</summary>
  /// <param name="name">The name.</param>
  /// <returns><see langword="true"/> when it carries type arguments.</returns>
  public static bool IsInstance(string name) => name.Contains('<', StringComparison.Ordinal) && name.EndsWith('>');

  /// <summary>Whether <see cref="IrGenericEquals"/> is defined on a type: a number, a truth value or a reference.</summary>
  /// <param name="type">The type of both operands.</param>
  /// <returns><see langword="true"/> when the equality has a meaning on it.</returns>
  public static bool Equates(IrType type) => type.IsNumeric || type == IrType.Bool || type.IsObject;

  /// <summary>Whether <see cref="IrGenericCompare"/> is defined on a type: a number or a truth value.</summary>
  /// <param name="type">The type of both operands.</param>
  /// <returns><see langword="true"/> when the order has a meaning on it.</returns>
  public static bool Orders(IrType type) => type.IsNumeric || type == IrType.Bool;

  /// <summary>
  /// The default value of a type, which <c>default(T)</c> is and <c>new T()</c> of a value type: zero,
  /// false, the default struct, <c>null</c>.
  /// </summary>
  /// <param name="type">A number, a truth value, a struct or a reference type.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The type has no default value in the IR.</exception>
  public static IrExpr DefaultOf(IrType type) => type switch {
    { IsNumeric: true } or { Kind: IrTypeKind.Bool } => new IrConst(type, 0),
    { IsStruct: true } => new IrStructDefault(type),
    { IsObject: true } => new IrNull(type),
    _ => throw new ArgumentException($"The type {type} has no default value.", nameof(type))
  };

  /// <summary>
  /// A template with its parameters replaced by type arguments: every type, every name of a call
  /// <see cref="IrGenericTemplate.Calls"/> explains, <c>default(T)</c> as the default of the argument
  /// and <c>new T()</c> as a new object of a class made by its constructor without parameters, or the
  /// default of a value type.
  /// </summary>
  /// <param name="template">The template.</param>
  /// <param name="arguments">One type per parameter.</param>
  /// <param name="name">The name of the instantiation.</param>
  /// <param name="module">The module whose functions the constructors of its classes are, for <c>new T()</c>.</param>
  /// <returns>The instantiation.</returns>
  /// <exception cref="ArgumentException">The number of arguments differs from the number of parameters, or <c>new T()</c> names a class without a constructor without parameters.</exception>
  public static IrFunction Instantiate(IrGenericTemplate template, IReadOnlyList<IrType> arguments, string name, IrModule module) {
    if (arguments.Count != template.Parameters.Count)
      throw new ArgumentException($"The template '{template.Function.Name}' takes {template.Parameters.Count} type arguments, not {arguments.Count}.", nameof(arguments));

    var map = new Dictionary<IrTypeDeclaration, IrType>(ReferenceEqualityComparer.Instance);
    foreach (var (parameter, argument) in template.Parameters.Zip(arguments))
      map[parameter.Declaration!] = argument;

    var functions = module.Functions.ToDictionary(function => function.Name);
    IrType Type(IrType type) => type.Declaration is { } declared && map.TryGetValue(declared, out var argument) ? argument
      : type.Element is { } element ? type.Kind switch {
        IrTypeKind.Span => IrType.Span(Type(element)),
        IrTypeKind.ReadOnlySpan => IrType.ReadOnlySpan(Type(element)),
        _ => IrType.Array(Type(element))
      } : type;

    string Call(string callee) => template.Calls.TryGetValue(callee, out var call) ? Mangle(call.Origin, call.Arguments.Select(Type)) : callee;

    IrExpr? Special(IrExpr node) {
      if (node is IrNull { Type.Declaration: { } nulled } && map.TryGetValue(nulled, out var defaulted))
        return DefaultOf(defaulted);

      if (node is not IrNew { Type.Declaration: { } made } || !map.TryGetValue(made, out var created))
        return null;

      if (created.ClassType is not { } owner)
        return DefaultOf(created);

      var constructor = owner.Constructors.FirstOrDefault(candidate => functions.TryGetValue(candidate.Function, out var body) && body.Parameters.Count == 1)
        ?? throw new ArgumentException($"The class {owner.Name} has no constructor without parameters.", nameof(arguments));
      return new IrNew(created, constructor, []);
    }

    return new Rewriter(Type, Call, Special).Function(template.Function) with { Name = name };
  }

  /// <summary>A module with functions renamed, every call of them included; a function the map does not name keeps its name.</summary>
  /// <param name="module">The module.</param>
  /// <param name="names">The new name of every renamed function by its old one.</param>
  /// <returns>The module with the renamed functions.</returns>
  public static IrModule Rename(IrModule module, IReadOnlyDictionary<string, string> names) {
    var rewriter = new Rewriter(type => type, name => names.GetValueOrDefault(name, name), _ => null);
    var globals = module.Globals.ToDictionary(global => global, global => global.Initializer is null ? global : global with { Initializer = rewriter.Expression(global.Initializer) });
    rewriter.Globals = globals;
    var functions = module.Functions.Select(function => rewriter.Function(function) with { Name = names.GetValueOrDefault(function.Name, function.Name) }).ToList();
    return module with { Functions = functions, Globals = [.. module.Globals.Select(global => globals[global])] };
  }

  /// <summary>
  /// Whether two functions are the same tree: the same statements and nodes with the same types,
  /// constants, names and operands, and locals of the same names and types. Declarations - classes,
  /// fields, methods, constants and globals - are compared by identity.
  /// </summary>
  /// <param name="left">One function.</param>
  /// <param name="right">The other.</param>
  /// <returns><see langword="true"/> when they are equal node by node.</returns>
  public static bool Equivalent(IrFunction left, IrFunction right)
    => left.Name == right.Name && left.IsPublic == right.IsPublic && left.ReturnType == right.ReturnType
      && Same(left.Parameters, right.Parameters) && Same(left.Locals, right.Locals) && Same(left.Body, right.Body);

  private static bool Same(object? left, object? right) {
    if (ReferenceEquals(left, right))
      return true;

    if (left is null || right is null)
      return false;

    // Two lists of nodes are the same whatever collection holds them.
    if (left is System.Collections.IEnumerable sequence and not string && right is System.Collections.IEnumerable others and not string) {
      var items = sequence.Cast<object?>().ToList();
      var compared = others.Cast<object?>().ToList();
      return items.Count == compared.Count && items.Zip(compared).All(pair => Same(pair.First, pair.Second));
    }

    if (left.GetType() != right.GetType())
      return false;

    switch (left) {
      case string or IrType or IrLocal or Enum or ValueType:
        return left.Equals(right);
      case IrExpr or IrStmt or IrSwitchSection:
        return left.GetType().GetProperties().Where(property => property.Name != "EqualityContract" && property.GetIndexParameters().Length == 0)
          .All(property => Same(property.GetValue(left), property.GetValue(right)));
      default:
        return left.Equals(right);
    }
  }

  /// <summary>Rebuilds a function with its types, the names it calls and some nodes replaced.</summary>
  private sealed class Rewriter(Func<IrType, IrType> type, Func<string, string> call, Func<IrExpr, IrExpr?> special) {

    private readonly Dictionary<IrLocal, IrLocal> _locals = new(ReferenceEqualityComparer.Instance);

    /// <summary>The globals as the rewritten module has them, by the old ones.</summary>
    public IReadOnlyDictionary<IrGlobal, IrGlobal> Globals { get; set; } = new Dictionary<IrGlobal, IrGlobal>();

    public IrFunction Function(IrFunction function) {
      this._locals.Clear();
      var parameters = function.Parameters.Select(this.Local).ToList();
      var locals = function.Locals.Select(this.Local).ToList();
      return function with { ReturnType = type(function.ReturnType), Parameters = parameters, Locals = locals, Body = this.Block(function.Body) };
    }

    private IrLocal Local(IrLocal local) {
      if (!this._locals.TryGetValue(local, out var made))
        this._locals[local] = made = new IrLocal(local.Name, type(local.Type));

      return made;
    }

    private IrGlobal Global(IrGlobal global) => this.Globals.GetValueOrDefault(global, global);

    private IrBlock Block(IrBlock block) => new([.. block.Statements.Select(this.Statement)]);

    private IrStmt Statement(IrStmt statement) => statement switch {
      IrBlock block => this.Block(block),
      IrDeclare declare => new IrDeclare(this.Local(declare.Local), declare.Initializer is null ? null : this.Expression(declare.Initializer)),
      IrAssign assign => new IrAssign(this.Local(assign.Local), this.Expression(assign.Value)),
      IrGlobalAssign assign => new IrGlobalAssign(this.Global(assign.Global), this.Expression(assign.Value)),
      IrStore store => new IrStore(this.Expression(store.Array), this.Expression(store.Index), this.Expression(store.Value)),
      IrFieldStore store => new IrFieldStore(this.Expression(store.Target), store.Field, this.Expression(store.Value)),
      IrExprStmt expression => new IrExprStmt(this.Expression(expression.Expression)),
      IrIf conditional => new IrIf(this.Expression(conditional.Condition), this.Block(conditional.Then), conditional.Else is null ? null : this.Block(conditional.Else)),
      IrWhile loop => new IrWhile(this.Expression(loop.Condition), this.Block(loop.Body)),
      IrDoWhile loop => new IrDoWhile(this.Block(loop.Body), this.Expression(loop.Condition)),
      IrFor loop => new IrFor(this.Expression(loop.Condition), this.Block(loop.Body), this.Block(loop.Step)),
      IrSwitch choice => new IrSwitch(this.Expression(choice.Value), [.. choice.Sections.Select(section => section with { Body = this.Block(section.Body) })]),
      IrReturn result => new IrReturn(result.Value is null ? null : this.Expression(result.Value)),
      _ => statement
    };

    private List<IrExpr> Expressions(IEnumerable<IrExpr> expressions) => [.. expressions.Select(this.Expression)];

    public IrExpr Expression(IrExpr expression) {
      var rebuilt = expression switch {
        IrConst constant => new IrConst(type(constant.Type), constant.Value),
        IrLocalRef reference => new IrLocalRef(this.Local(reference.Local)),
        IrGlobalRef reference => new IrGlobalRef(this.Global(reference.Global)),
        IrUnary unary => new IrUnary(unary.Op, this.Expression(unary.Operand)),
        IrBinary binary => new IrBinary(binary.Op, this.Expression(binary.Left), this.Expression(binary.Right)),
        IrConvert conversion => new IrConvert(type(conversion.Type), this.Expression(conversion.Operand), conversion.Contract),
        IrConditional choice => new IrConditional(this.Expression(choice.Condition), this.Expression(choice.WhenTrue), this.Expression(choice.WhenFalse)),
        IrArrayLoad load => new IrArrayLoad(this.Expression(load.Array), this.Expression(load.Index)),
        IrArrayLength length => new IrArrayLength(this.Expression(length.Array)),
        IrNewArray creation => new IrNewArray(type(creation.Type), this.Expression(creation.Length)),
        IrCall invoked => new IrCall(call(invoked.Function), type(invoked.Type), this.Expressions(invoked.Arguments)),
        IrIntrinsic intrinsic => new IrIntrinsic(intrinsic.Op, type(intrinsic.Type), this.Expressions(intrinsic.Arguments)),
        IrStep step => step with { Target = this.Expression(step.Target) },
        IrStructNew creation => new IrStructNew(type(creation.Type), this.Expressions(creation.Arguments)),
        IrStructDefault value => new IrStructDefault(type(value.Type)),
        IrFieldLoad load => new IrFieldLoad(this.Expression(load.Target), load.Field),
        IrStructEquals equality => new IrStructEquals(this.Expression(equality.Left), this.Expression(equality.Right)),
        IrNew creation => new IrNew(type(creation.Type), creation.Constructor, this.Expressions(creation.Arguments)),
        IrNull value => new IrNull(type(value.Type)),
        IrMethodCall invoked => invoked with { Type = type(invoked.Type), Target = invoked.Target is null ? null : this.Expression(invoked.Target), Arguments = this.Expressions(invoked.Arguments) },
        IrReferenceEquals equality => new IrReferenceEquals(this.Expression(equality.Left), this.Expression(equality.Right)),
        IrTypeTest test => new IrTypeTest(this.Expression(test.Value), type(test.Target)),
        IrCast cast => new IrCast(type(cast.Type), this.Expression(cast.Value)),
        IrAsCast cast => new IrAsCast(type(cast.Type), this.Expression(cast.Value)),
        IrGenericEquals equality => new IrGenericEquals(this.Expression(equality.Left), this.Expression(equality.Right)),
        IrGenericCompare order => new IrGenericCompare(this.Expression(order.Left), this.Expression(order.Right)),
        _ => expression
      };
      return special(expression) is { } replaced ? replaced : rebuilt;
    }

  }

}
