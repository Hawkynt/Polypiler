using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// The ownership analysis of the objects of a module (#1393), on the module as
/// <see cref="ObjectLowering"/> leaves it. A class is <em>single-owner</em> when no object of it is
/// ever held by two places at once: every variable of the class is given a new object - made by
/// <c>new</c> or returned by a function of the module that makes one - and is read only as the
/// object of a field, as an argument of a call of a function of the module (once per call) or as the
/// result a function moves out; no field, global, array, collection or other compound type names the
/// class, no reference to it may be null, and it takes part in no hierarchy. Such an object is a plain
/// struct in Rust, moved where the IR returns it and borrowed where the IR passes it; every other
/// class keeps the shared form, an <c>Rc</c> of cells.
/// </summary>
internal sealed class ObjectOwnership {

  private readonly HashSet<IrClassType> _single;
  private readonly Dictionary<string, HashSet<IrLocal>> _mutated;

  private ObjectOwnership(HashSet<IrClassType> single, Dictionary<string, HashSet<IrLocal>> mutated) {
    this._single = single;
    this._mutated = mutated;
  }

  /// <summary>An analysis that finds no class single-owner.</summary>
  public static ObjectOwnership None { get; } = new(new HashSet<IrClassType>(ReferenceEqualityComparer.Instance), new Dictionary<string, HashSet<IrLocal>>(StringComparer.Ordinal));

  /// <summary>The single-owner classes.</summary>
  public IReadOnlySet<IrClassType> Single => this._single;

  /// <summary>Whether values of a type are objects of a single-owner class.</summary>
  public bool IsSingle(IrType type) => type.IsObject && type.ClassType is { } declared && this._single.Contains(declared);

  /// <summary>Whether a function writes the object a variable or parameter of a single-owner class holds, itself or through a callee.</summary>
  public bool Mutates(string function, IrLocal local) => this._mutated.TryGetValue(function, out var mutated) && mutated.Contains(local);

  /// <summary>Whether a function writes the object its parameter at a position holds.</summary>
  public bool MutatesParameter(IrFunction function, int index) => index < function.Parameters.Count && this.Mutates(function.Name, function.Parameters[index]);

  /// <summary>
  /// Finds the single-owner classes of a module and what each function writes of their objects.
  /// </summary>
  /// <param name="module">The module, its objects lowered to calls.</param>
  /// <param name="candidates">The classes the backend could write as plain structs: of the module, in no hierarchy.</param>
  /// <param name="nullness">Which variables and results may be null.</param>
  /// <param name="arrays">What every function does with arrays: which field arrays it writes.</param>
  public static ObjectOwnership Of(IrModule module, IEnumerable<IrClassType> candidates, Nullness nullness, IReadOnlyDictionary<string, FunctionArrays> arrays) {
    ArgumentNullException.ThrowIfNull(module);
    var single = new HashSet<IrClassType>(candidates.Where(Eligible), ReferenceEqualityComparer.Instance);
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    if (single.Count > 0) {
      // A field, a global or a compound type that names a class holds its objects a second time.
      foreach (var type in module.Types.SelectMany(declaration => declaration.IsDefined ? declaration.Fields : []).Select(field => field.Type)
        .Concat(module.Globals.Select(global => global.Type)))
        single.ExceptWith(Mentioned(type));

      foreach (var function in module.Functions)
        single.ExceptWith(Disqualified(function, functions, nullness));
    }

    return new ObjectOwnership(single, Mutations(module, single, arrays));
  }

  /// <summary>Whether a class can be a plain struct at all: of the module, no record, no part of a hierarchy, every field a value or an array of values.</summary>
  private static bool Eligible(IrClassType type)
    => !type.IsLibrary && !type.IsAbstract && !type.IsRecord && type.Base is null && type.Interfaces.Count == 0
      && !IrExceptionClass.IsException(type) && type.AllFields.All(field => IsPlainField(field.Type));

  private static bool IsPlainField(IrType type) => type.IsArray && type.Element is { } element && IsValue(element) || IsValue(type);

  private static bool IsValue(IrType type) => type.IsNumeric || type == IrType.Bool || type.IsEnum || type.StructType is { } nested && nested.Fields.All(field => IsValue(field.Type));

  /// <summary>Every class a type names: itself, or the classes of its elements, keys, values and signature.</summary>
  private static IEnumerable<IrClassType> Mentioned(IrType? type) {
    if (type is null)
      yield break;
    if (type.ClassType is { } declared)
      yield return declared;

    foreach (var inner in new[] { type.Element, type.Underlying, type.Key }.Concat(type.Signature is { } signature ? [.. signature.Parameters, signature.Result] : []))
      foreach (var mentioned in Mentioned(inner))
        yield return mentioned;
  }

  /// <summary>The classes a function holds an object of otherwise than the rules allow.</summary>
  private static IEnumerable<IrClassType> Disqualified(IrFunction function, IReadOnlyDictionary<string, IrFunction> functions, Nullness nullness) {
    var allowed = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    var problems = new List<IrClassType>();
    bool Fresh(IrExpr value) => value is IrNew || value is IrCall call && functions.ContainsKey(call.Function);
    void Allow(IrExpr value) {
      if (value.Type.IsObject)
        allowed.Add(value);
    }

    void Expression(IrExpr expression) {
      if (expression is IrLambda)
        return;

      switch (expression) {
        case IrFieldLoad { Target: var target } when target is IrLocalRef || Fresh(target):
          Allow(target);
          break;
        case IrCall call when functions.ContainsKey(call.Function): {
          var seen = new HashSet<IrLocal>();
          foreach (var argument in call.Arguments.Where(argument => argument.Type.IsObject)) {
            if (argument is IrLocalRef reference && !seen.Add(reference.Local))
              problems.Add(argument.Type.ClassType!);
            else if (argument is IrLocalRef || Fresh(argument))
              Allow(argument);
          }

          break;
        }
      }

      foreach (var operand in IrTree.Operands(expression))
        Expression(operand);
    }

    foreach (var statement in ArrayOwnership.Statements(function.Body)) {
      switch (statement) {
        case IrDeclare { Local.Type.IsObject: true, Initializer: null } declare:
          problems.Add(declare.Local.Type.ClassType!);
          break;
        case IrDeclare { Local.Type.IsObject: true, Initializer: { } value } when Fresh(value):
          Allow(value);
          break;
        case IrAssign { Local.Type.IsObject: true } assign when function.Parameters.Contains(assign.Local):
          problems.Add(assign.Local.Type.ClassType!);
          break;
        case IrAssign { Local.Type.IsObject: true, Value: var value } when Fresh(value):
          Allow(value);
          break;
        case IrReturn { Value: { Type.IsObject: true } value } when Fresh(value) || value is IrLocalRef { Local: var local } && !function.Parameters.Contains(local):
          Allow(value);
          break;
        case IrExprStmt { Expression: var value } when Fresh(value):
          Allow(value);
          break;
        case IrFieldStore { Target: var target } when target is IrLocalRef || Fresh(target):
          Allow(target);
          break;
      }

      foreach (var expression in IrTree.Expressions(statement))
        Expression(expression);
    }

    // Every object the function names must be named where the rules allow it; a lambda's body never is.
    foreach (var node in IrTree.Nodes(function.Body).Where(node => node.Type.IsObject && !allowed.Contains(node)))
      problems.Add(node.Type.ClassType!);

    foreach (var local in function.Parameters.Concat(function.Locals).Where(local => local.Type.IsObject && nullness.MayBeNull(function.Name, local)))
      problems.Add(local.Type.ClassType!);
    if (function.ReturnType.IsObject && nullness.ReturnsNull(function.Name))
      problems.Add(function.ReturnType.ClassType!);

    // A compound type of a variable or a value (a list of objects, a function taking one) holds the objects of the classes it names.
    foreach (var type in function.Parameters.Concat(function.Locals).Select(local => local.Type).Append(function.ReturnType).Concat(IrTree.Nodes(function.Body).Select(node => node.Type)).Where(type => !type.IsObject))
      problems.AddRange(Mentioned(type));

    return problems.Where(type => type is not null);
  }

  /// <summary>
  /// What each function writes of the objects of single-owner classes, a fixed point over the calls: a
  /// variable is written where a field of it is stored, where an array of one of its fields is written
  /// - directly, through a variable that names it or by a callee - and where it is passed to a
  /// function that writes its parameter.
  /// </summary>
  private static Dictionary<string, HashSet<IrLocal>> Mutations(IrModule module, HashSet<IrClassType> single, IReadOnlyDictionary<string, FunctionArrays> arrays) {
    var mutated = module.Functions.ToDictionary(function => function.Name, _ => new HashSet<IrLocal>(), StringComparer.Ordinal);
    if (single.Count == 0)
      return mutated;

    bool Plain(IrType type) => type.IsObject && type.ClassType is { } declared && single.Contains(declared);
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    foreach (var function in module.Functions) {
      var own = mutated[function.Name];
      foreach (var statement in ArrayOwnership.Statements(function.Body))
        if (statement is IrFieldStore { Target: IrLocalRef target } && Plain(target.Type))
          own.Add(target.Local);

      if (arrays.TryGetValue(function.Name, out var facts)) {
        own.UnionWith(facts.WrittenFieldTargets.Where(local => Plain(local.Type)));
        foreach (var (alias, load) in facts.FieldAliases)
          if (facts.MutableLocals.Contains(alias) && load.Target is IrLocalRef { Local: var owner } && Plain(owner.Type))
            own.Add(owner);
      }
    }

    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions)
        foreach (var call in IrTree.Nodes(function.Body).OfType<IrCall>().Where(call => functions.ContainsKey(call.Function)))
          for (var index = 0; index < call.Arguments.Count; ++index)
            if (call.Arguments[index] is IrLocalRef { Local: var local } && Plain(local.Type) && index < functions[call.Function].Parameters.Count
                && mutated[call.Function].Contains(functions[call.Function].Parameters[index]))
              changed |= mutated[function.Name].Add(local);
    }

    return mutated;
  }

}
