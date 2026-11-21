using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// What a constructor reaches while it runs: the calls made on the object being constructed, by the
/// constructor itself and by everything it calls. A virtual or an interface call among them that a
/// class derived from the constructor's own class implements is what the IR forbids (see
/// <see cref="IrMethodCallKind"/>), since C# and JavaScript run the initializers of a derived class
/// at different times relative to it. A call no class below implements selects a method of the
/// class or of a base class, whose fields have their values in both languages by then (#1368). Nor
/// is a call forbidden whose implementations below cannot tell the two orders apart (#1402): they
/// read no field of a class below, and every such field they write, each constructor of its class
/// writes again before anything else reaches the object - JavaScript's setter that a base
/// constructor's store runs, whose copy the derived constructor then replaces.
/// </summary>
public static class IrConstruction {

  /// <summary>
  /// The virtual and interface calls on the object being constructed that a constructor of a class of
  /// the module makes, directly or through any function it calls with that object: as the receiver
  /// of a direct or base call, as an argument, or through a local that holds it.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="strict">
  /// Whether every such call counts, also one whose implementations below cannot tell the two orders
  /// apart: a C# source, whose field initializers run before the base constructor, has a third order.
  /// </param>
  /// <returns>Each call once, with the name of the first constructor found to reach it.</returns>
  public static IReadOnlyList<(string Constructor, IrMethodCall Call)> VirtualCalls(IrModule module, bool strict = false) {
    var functions = module.Functions.GroupBy(function => function.Name).ToDictionary(group => group.Key, group => group.First());
    var classes = module.Types.OfType<IrClassType>().Where(type => type.HasMembers).ToList();
    var found = new List<(string, IrMethodCall)>();
    var reported = new HashSet<IrMethodCall>(ReferenceEqualityComparer.Instance);
    foreach (var (owner, constructor) in classes.SelectMany(type => type.Constructors.Select(constructor => (type, constructor)))) {
      var below = classes.Where(type => !ReferenceEquals(type, owner) && type.DerivesFrom(owner)).ToList();
      var visited = new HashSet<(string, int)>();
      var pending = new Stack<(string Function, int Parameter)>([(constructor.Function, 0)]);
      while (pending.TryPop(out var next)) {
        if (!visited.Add(next) || !functions.TryGetValue(next.Function, out var function) || next.Parameter >= function.Parameters.Count)
          continue;

        foreach (var (callee, parameter, call) in Reached(function, function.Parameters[next.Parameter], classes)) {
          if (call is not null && Overridden(call, below) && (strict || !Unobserved(call, below, functions, classes))) {
            if (reported.Add(call))
              found.Add((constructor.Function, call));
          } else if (call is not null && (call.Kind == IrMethodCallKind.Interface ? owner.Implementation(call.Method) : owner.Select(call.Method)) is { } selected) {
            // The method that runs is the class's own or a base class's: what it does to the object is reached as well.
            pending.Push((selected.Function, 0));
          }

          if (callee is not null)
            pending.Push((callee, parameter));
        }
      }
    }

    return found;
  }

  /// <summary>
  /// What a function does with a value it was given: the functions it passes the value to, with the
  /// parameter that receives it, and the virtual and interface calls it makes on it.
  /// </summary>
  private static IEnumerable<(string? Callee, int Parameter, IrMethodCall? Call)> Reached(IrFunction function, IrLocal given, List<IrClassType> classes) {
    var holders = new HashSet<IrLocal> { given };
    bool Holds(IrExpr expression) => expression switch {
      IrLocalRef reference => holders.Contains(reference.Local),
      IrCast cast => Holds(cast.Value),
      IrAsCast cast => Holds(cast.Value),
      _ => false
    };

    foreach (var statement in Statements(function.Body)) {
      switch (statement) {
        case IrDeclare { Initializer: { } value } declare when Holds(value):
          holders.Add(declare.Local);
          break;
        case IrAssign assign when Holds(assign.Value):
          holders.Add(assign.Local);
          break;
      }

      foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
        switch (node) {
          case IrMethodCall { Target: { } target } call when Holds(target):
            if (call.Kind is IrMethodCallKind.Virtual or IrMethodCallKind.Interface)
              yield return (null, 0, call);
            else
              yield return (call.Method.Function, 0, null);

            break;
        }

        var (arguments, offset, callees) = node switch {
          IrCall call => (call.Arguments, 0, (IEnumerable<string>)[call.Function]),
          IrNew creation => (creation.Arguments, 1, [creation.Constructor.Function]),
          IrMethodCall call => (call.Arguments, call.Method.IsStatic ? 0 : 1, Implementations(call, classes)),
          _ => ([], 0, [])
        };
        for (var index = 0; index < arguments.Count; ++index)
          if (Holds(arguments[index]))
            foreach (var callee in callees)
              yield return (callee, index + offset, null);
      }
    }
  }

  /// <summary>
  /// Whether what the implementations of a call in the classes below the one constructed do to the
  /// object is the same in either order (#1402): everything they reach with it reads no field of a class
  /// below, and every field of a class below it writes, each constructor of that class writes again
  /// first thing after the call of the base constructor, before anything else reaches the object.
  /// </summary>
  private static bool Unobserved(IrMethodCall call, List<IrClassType> below, Dictionary<string, IrFunction> functions, List<IrClassType> classes) {
    var fields = below.SelectMany(type => type.Fields).ToHashSet(ReferenceEqualityComparer.Instance);
    var written = new HashSet<IrField>(ReferenceEqualityComparer.Instance);
    var visited = new HashSet<(string, int)>();
    var pending = new Stack<(string Function, int Parameter)>(below.SelectMany(type => type.Methods).Where(method => Implements(method, call)).Select(method => (method.Function, 0)));
    while (pending.TryPop(out var next)) {
      if (!visited.Add(next) || !functions.TryGetValue(next.Function, out var function) || next.Parameter >= function.Parameters.Count)
        continue;

      var holders = Holders(function, function.Parameters[next.Parameter]);
      bool Holds(IrExpr expression) => expression switch {
        IrLocalRef reference => holders.Contains(reference.Local),
        IrCast cast => Holds(cast.Value),
        IrAsCast cast => Holds(cast.Value),
        _ => false
      };
      if (function.Body.Statements.SelectMany(IrTree.Nodes).Any(node => node is IrFieldLoad load && Holds(load.Target) && fields.Contains(load.Field)))
        return false;

      written.UnionWith(Statements(function.Body).OfType<IrFieldStore>().Where(store => Holds(store.Target) && fields.Contains(store.Field)).Select(store => store.Field));
      foreach (var (callee, parameter, reached) in Reached(function, function.Parameters[next.Parameter], classes)) {
        if (callee is not null)
          pending.Push((callee, parameter));

        foreach (var implementation in reached is null ? [] : Implementations(reached, classes))
          pending.Push((implementation, 0));
      }
    }

    return written.All(field => below.First(type => type.Fields.Contains(field)).Constructors.All(constructor => functions.TryGetValue(constructor.Function, out var made) && Renews(made, field)));
  }

  /// <summary>Whether a method is one of the slot of the method a call names, or implements it.</summary>
  private static bool Implements(IrMethod method, IrMethodCall call)
    => ReferenceEquals(method.Root, call.Method.Root) || method.Implements.Contains(call.Method) || method.Overrides is not null && method.Root.Implements.Contains(call.Method);

  /// <summary>The locals of a function that may hold a value it was given: the parameter and every local a declaration or an assignment gives it.</summary>
  private static HashSet<IrLocal> Holders(IrFunction function, IrLocal given) {
    var holders = new HashSet<IrLocal> { given };
    for (var grown = true; grown;) {
      grown = false;
      foreach (var statement in Statements(function.Body))
        grown |= statement switch {
          IrDeclare { Initializer: IrLocalRef value } declare when holders.Contains(value.Local) => holders.Add(declare.Local),
          IrAssign { Value: IrLocalRef value } assign when holders.Contains(value.Local) => holders.Add(assign.Local),
          _ => false
        };
    }

    return holders;
  }

  /// <summary>
  /// Whether a constructor writes a field of its object again before anything else reaches the
  /// object: after the call of the base constructor only stores of values that do not name the object
  /// into its fields, and statements that do not name it, stand before the store of the field.
  /// </summary>
  private static bool Renews(IrFunction constructor, IrField field) {
    var self = constructor.Parameters[0];
    bool Names(IrStmt statement) => IrTree.Nodes(statement).Any(node => node is IrLocalRef reference && reference.Local == self);
    var statements = constructor.Body.Statements;
    var start = statements.ToList().FindIndex(Names);
    foreach (var statement in statements.Skip(start + 1)) {
      if (statement is IrFieldStore { Target: IrLocalRef target } store && target.Local == self && !IrTree.Nodes(store.Value).Any(node => node is IrLocalRef reference && reference.Local == self)) {
        if (ReferenceEquals(store.Field, field))
          return true;

        continue;
      }

      if (Names(statement))
        return false;
    }

    return false;
  }

  /// <summary>The functions a call of a method may run: the method's own, and for a virtual or an interface call every override and implementation of it.</summary>
  /// <summary>Whether a class derived from the class being constructed implements the method a virtual or an interface call reaches.</summary>
  private static bool Overridden(IrMethodCall call, List<IrClassType> below)
    => below.SelectMany(type => type.Methods).Any(method => Implements(method, call));

  private static IEnumerable<string> Implementations(IrMethodCall call, List<IrClassType> classes)
    => call.Kind is IrMethodCallKind.Virtual or IrMethodCallKind.Interface
      ? classes.SelectMany(type => type.Methods).Where(method => Implements(method, call)).Select(method => method.Function).Append(call.Method.Function)
      : [call.Method.Function];

  /// <summary>Every statement of a block, nested ones included, in order.</summary>
  private static IEnumerable<IrStmt> Statements(IrBlock block) {
    foreach (var statement in block.Statements) {
      yield return statement;
      foreach (var inner in IrTree.Blocks(statement).SelectMany(Statements))
        yield return inner;
    }
  }

}
