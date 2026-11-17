using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>A type parameter of a generic definition the crate writes: its name, the type that stands for it while the body is written, and its bounds.</summary>
/// <param name="Name">The name the source gives it, <c>T</c>.</param>
/// <param name="StandIn">The scalar the body is instantiated with to be written; no other value of the body has its type.</param>
/// <param name="Bounds">The traits it is bounded by, in the order they are written: <c>Ord</c> or <c>Eq</c>, <c>Default</c>, <c>Copy</c>.</param>
internal sealed record GenericParameter(string Name, IrType StandIn, IReadOnlyList<string> Bounds) {

  /// <summary>The parameter with its bounds, <c>T: Ord + Copy</c>.</summary>
  public string Bounded => $"{this.Name}: {string.Join(" + ", this.Bounds)}";

}

/// <summary>
/// A generic method of the source the crate writes as one Rust generic function instead of one
/// function per instantiation.
/// </summary>
/// <param name="Function">The definition: its template instantiated with the stand-ins of its parameters, named as the source names the method.</param>
/// <param name="Parameters">Its type parameters.</param>
/// <param name="Instances">The type arguments of every instantiation of the module it takes the place of, by the instantiation's name.</param>
/// <param name="Inferred">Whether Rust infers the type arguments of a call from its arguments; otherwise a call names them, <c>def::&lt;i32&gt;()</c>.</param>
internal sealed record GenericDefinition(IrFunction Function, IReadOnlyList<GenericParameter> Parameters, IReadOnlyDictionary<string, IReadOnlyList<IrType>> Instances, bool Inferred) {

  /// <summary>
  /// The generic parameters as they follow the function's name, with a lifetime before them: the
  /// bounds inline where there is one parameter with at most two bounds, <c>&lt;T: Ord + Copy&gt;</c>,
  /// else the names alone, <c>&lt;T, U&gt;</c>, and the bounds in <see cref="Where"/>.
  /// </summary>
  /// <param name="lifetime">The lifetime, <c>'a</c>, or empty.</param>
  public string Generics(string lifetime) {
    var inline = this.Where.Count == 0;
    var parameters = this.Parameters.Select(parameter => inline ? parameter.Bounded : parameter.Name);
    return $"<{string.Join(", ", lifetime.Length > 0 ? [lifetime, .. parameters] : parameters)}>";
  }

  /// <summary>The clauses of the <c>where</c> the function carries: one per parameter where the bounds are long, none where they stand inline.</summary>
  public IReadOnlyList<string> Where => this.Parameters is [{ Bounds.Count: <= 2 }] ? [] : [.. this.Parameters.Select(parameter => parameter.Bounded)];

  /// <summary>The path a call of an instantiation names: the function's, with the type arguments where Rust cannot infer them.</summary>
  /// <param name="name">The Rust name of the function.</param>
  /// <param name="instance">The IR name of the instantiation.</param>
  public string CallPath(string name, string instance)
    => this.Inferred ? name : $"{name}::<{string.Join(", ", this.Instances[instance].Select(type => RustEmitter.TypeName(type)))}>";

}

/// <summary>
/// The generic methods of a module the crate writes as Rust generics with trait bounds. The IR holds no
/// type parameter: a generic method is one function per list of type arguments the module uses, and
/// its template (<see cref="IrGenericTemplate"/>) the body with the parameters standing as types of
/// their own. A method is written generic where its template does nothing with a value of a parameter
/// but hold it, store it in an array of it, compare it and make its default, the bounds are what those
/// operations need, and an instantiation keeps the meaning .NET gives it under them:
/// <list type="table">
///   <item><term>a value held, passed, stored or returned</term><description><c>Copy</c>: every scalar</description></item>
///   <item><term><c>default(T)</c>, <c>new T()</c></term><description><c>Default</c>: every scalar</description></item>
///   <item><term><c>Equals</c> of <c>IEquatable&lt;T&gt;</c></term><description><c>Eq</c>: integers and truth values; a floating-point number, which equals a NaN under .NET's <c>Equals</c> and not under <c>==</c>, keeps its own function</description></item>
///   <item><term><c>CompareTo</c> of <c>IComparable&lt;T&gt;</c>, its sign tested</term><description><c>Ord</c>, the test on the values themselves: integers and truth values; a floating-point number, which .NET orders with a NaN first, keeps its own function</description></item>
/// </list>
/// Every other operation on a value of a parameter - arithmetic of a fixed width, a conversion, a call
/// of another generic method, the value of <c>CompareTo</c> itself, which is the difference of two
/// narrow integers in .NET - keeps one function per instantiation, as do the instantiations with a type
/// argument the bounds do not admit and those an array that may be null is passed to.
/// </summary>
internal static class GenericDefinitions {

  /// <summary>The integer types a parameter may stand as, the widest first.</summary>
  private static readonly IrType[] StandIns = [IrType.U64, IrType.I64, IrType.U32, IrType.I32, IrType.U16, IrType.I16, IrType.U8, IrType.I8];

  /// <summary>The generic definitions of a module, each with the instantiations it takes the place of.</summary>
  /// <param name="module">The module as the backend receives it.</param>
  /// <returns>The definitions, in the order of their first instantiation.</returns>
  public static IReadOnlyList<GenericDefinition> Of(IrModule module) {
    var definitions = new List<GenericDefinition>();
    var groups = module.Functions.Where(function => function.Origin?.Template is not null).GroupBy(function => function.Origin!.Name, StringComparer.Ordinal).ToList();
    if (groups.Count == 0)
      return definitions;

    var nulls = ArrayNullness.Of(ObjectLowering.Run(BindHoisting.Run(module)));
    foreach (var group in groups) {
      var origin = group.First().Origin!;
      var template = origin.Template!;
      if (group.Any(instance => !ReferenceEquals(instance.Origin!.Template, template)) || Needs(template) is not { } needs || Stand(template) is not { } standIns)
        continue;

      var admitted = group.Where(instance => instance.Origin!.TypeArguments.Select((argument, index) => Admits(argument, needs[index])).All(admits => admits)
          && !instance.Parameters.Any(parameter => parameter.Type.IsArray && nulls.MayBeNull(instance.Name, parameter))
          && IrGenerics.Equivalent(IrGenerics.Instantiate(template, instance.Origin.TypeArguments, instance.Name, module), instance))
        .ToDictionary(instance => instance.Name, instance => instance.Origin!.TypeArguments, StringComparer.Ordinal);
      if (admitted.Count == 0)
        continue;

      var function = IrGenerics.Instantiate(template, standIns, group.Key, module) with { Origin = null };
      function = function with { Body = AliasSpecialization.Rewrite(function.Body, SignTest) };
      var parameters = origin.TypeParameters.Select((name, index) => new GenericParameter(name, standIns[index], Bounds(needs[index]))).ToList();
      var inferred = template.Parameters.All(parameter => template.Function.Parameters.Any(local => local.Type == parameter || local.Type.Element == parameter));
      definitions.Add(new GenericDefinition(function, parameters, admitted, inferred));
    }

    return definitions;
  }

  /// <summary>What a template needs of each of its parameters, or <see langword="null"/> where it does something with one that no bound gives.</summary>
  private static Need[]? Needs(IrGenericTemplate template) {
    var standing = new Standing(template.Parameters);
    var function = template.Function;
    if (template.Calls.Count > 0 || !function.Parameters.Concat(function.Locals).All(local => standing.Plain(local.Type)) || function.ReturnType != IrType.Void && !standing.Plain(function.ReturnType))
      return null;

    // A parameter the signature does not name is one clippy calls unused (extra_unused_type_parameters).
    var signature = function.Parameters.Select(local => local.Type).Append(function.ReturnType).ToList();
    if (template.Parameters.Any(parameter => !signature.Any(type => type == parameter || type.Element == parameter)))
      return null;

    var needs = new Need[template.Parameters.Count];
    return standing.Collect(function.Body, needs, _ => false) ? needs : null;
  }

  /// <summary>One integer type for each parameter of a template that no value of the template has, or <see langword="null"/> where too few are left.</summary>
  private static IrType[]? Stand(IrGenericTemplate template) => Stand([template.Function], [], template.Parameters.Count);

  /// <summary>One integer type for each of a number of parameters that no value of the functions and no type of the list has, or <see langword="null"/> where too few are left.</summary>
  internal static IrType[]? Stand(IEnumerable<IrFunction> functions, IEnumerable<IrType> types, int count) {
    var used = functions.SelectMany(function => function.Parameters.Concat(function.Locals).Select(local => local.Type).Append(function.ReturnType)
      .Concat(IrTree.Nodes(function.Body).Select(node => node.Type))).Concat(types).SelectMany(Scalars).ToHashSet();
    var free = StandIns.Where(type => !used.Contains(type)).ToList();
    return free.Count < count ? null : [.. free.Take(count)];
  }

  /// <summary>A type and the types of its elements.</summary>
  private static IEnumerable<IrType> Scalars(IrType type) => type.Element is { } element ? [type, .. Scalars(element)] : [type];

  /// <summary>Whether a type argument has every bound a parameter needs with the meaning .NET gives the operations: a scalar, an integer or a truth value where it is compared.</summary>
  internal static bool Admits(IrType argument, Need need)
    => RustEmitter.TypeName(argument) is not null && ((need & (Need.Equality | Need.Order)) == Need.None || argument.IsInteger || argument == IrType.Bool);

  /// <summary>The bounds of what a parameter needs, in the order they are written.</summary>
  internal static IReadOnlyList<string> Bounds(Need need) {
    var bounds = new List<string>();
    if ((need & Need.Order) != 0)
      bounds.Add("Ord");
    else if ((need & Need.Equality) != 0)
      bounds.Add("Eq");
    if ((need & Need.Default) != 0)
      bounds.Add("Default");
    bounds.Add("Copy");
    return bounds;
  }

  /// <summary>
  /// The sign of <c>CompareTo</c> tested against zero, <c>a.CompareTo(b) &gt;= 0</c>, as the test of the
  /// values themselves, <c>a &gt;= b</c>; <c>0 &lt; a.CompareTo(b)</c> as <c>a &gt; b</c>. Both operands
  /// are still evaluated, the left first.
  /// </summary>
  internal static IrExpr SignTest(IrExpr expression) => expression switch {
    IrBinary { Left: IrGenericCompare order, Right: IrConst { Value: 0 } } test => new IrBinary(test.Op, order.Left, order.Right),
    IrBinary { Left: IrConst { Value: 0 }, Right: IrGenericCompare order } test => new IrBinary(test.Op switch {
      IrBinaryOp.Less => IrBinaryOp.Greater,
      IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
      IrBinaryOp.Greater => IrBinaryOp.Less,
      IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual,
      _ => test.Op
    }, order.Left, order.Right),
    _ => expression
  };

}

/// <summary>What a template does with the values of a parameter, beyond holding them.</summary>
[Flags]
internal enum Need {

  /// <summary>Nothing: the values are only held.</summary>
  None = 0,

  /// <summary><c>default(T)</c>, <c>new T()</c> or a new array of the parameter.</summary>
  Default = 1,

  /// <summary><c>Equals</c> of <c>IEquatable&lt;T&gt;</c>.</summary>
  Equality = 2,

  /// <summary>The sign of <c>CompareTo</c> of <c>IComparable&lt;T&gt;</c>.</summary>
  Order = 4

}

/// <summary>The type parameters of a template, by the types that stand for them, and what its nodes do with their values.</summary>
/// <param name="parameters">The types that stand for the parameters, in order.</param>
internal sealed class Standing(IReadOnlyList<IrType> parameters) {

  private readonly Dictionary<IrTypeDeclaration, int> _index = parameters.Select((parameter, index) => (parameter, index))
    .ToDictionary<(IrType Parameter, int Index), IrTypeDeclaration, int>(pair => pair.Parameter.Declaration!, pair => pair.Index, ReferenceEqualityComparer.Instance);

  /// <summary>The position of the parameter a type stands for, or <see langword="null"/> for any other type.</summary>
  public int? Of(IrType type) => type.Declaration is { } declared && this._index.TryGetValue(declared, out var index) ? index : null;

  /// <summary>Whether a type names a parameter, itself or as an element, key or underlying type.</summary>
  // A function type of the parameter is a lambda's, refused below, or a reference to an instantiation, which Calls holds.
  public bool Mentions(IrType type) => this.Of(type) is not null || type.Element is { } element && this.Mentions(element) || type.Key is { } key && this.Mentions(key)
    || type.Underlying is { } underlying && this.Mentions(underlying);

  /// <summary>Whether a value is a scalar or a parameter: a form whose Rust type the stand-in spells.</summary>
  public bool Scalar(IrType type) => RustEmitter.TypeName(type) is not null || this.Of(type) is not null;

  /// <summary>Whether a value is a scalar, a parameter, or an array of either.</summary>
  public bool Plain(IrType type) => this.Scalar(type) || type.IsArray && this.Scalar(type.Element!);

  /// <summary>
  /// Adds what the nodes of a body do with the values of the parameters to <paramref name="needs"/>:
  /// holding, reading an array, choosing, the default value, the equality and the sign of the order;
  /// <see langword="false"/> where a node does anything else with one that <paramref name="own"/> does not admit, or is a lambda.
  /// </summary>
  public bool Collect(IrBlock body, Need[] needs, Func<IrExpr, bool> own) {
    var nodes = IrTree.Nodes(body).ToList();
    var tested = nodes.OfType<IrBinary>().Where(binary => binary.Op is >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual)
      .SelectMany(binary => new[] { (binary.Left, binary.Right), (binary.Right, binary.Left) })
      .Where(pair => pair.Item1 is IrGenericCompare && pair.Item2 is IrConst { Value: 0 }).Select(pair => pair.Item1).ToHashSet(ReferenceEqualityComparer.Instance);
    foreach (var node in nodes) {
      if (node is IrLambda)
        return false;
      if (!this.Mentions(node.Type) && !IrTree.Operands(node).Any(operand => this.Mentions(operand.Type)))
        continue;

      switch (node) {
        case IrLocalRef or IrArrayLoad or IrArrayLength or IrConditional:
          continue;
        case IrNull or IrNew when this.Of(node.Type) is { } index:
          needs[index] |= Need.Default;
          continue;
        case IrGenericEquals equality when this.Of(equality.Left.Type) is { } index:
          needs[index] |= Need.Equality;
          continue;
        case IrGenericCompare order when tested.Contains(order) && this.Of(order.Left.Type) is { } index:
          needs[index] |= Need.Order;
          continue;
        default:
          if (!own(node))
            return false;
          continue;
      }
    }

    return true;
  }

}
