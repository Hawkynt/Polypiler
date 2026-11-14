using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// What a constructor reaches while it runs: the calls made on the object being constructed, by the
/// constructor itself and by everything it calls. A virtual or an interface call among them is what
/// the IR forbids (see <see cref="IrMethodCallKind"/>), since C# and JavaScript run the initializers
/// of a derived class at different times relative to it.
/// </summary>
public static class IrConstruction {

  /// <summary>
  /// The virtual and interface calls on the object being constructed that a constructor of a class of
  /// the module makes, directly or through any function it calls with that object: as the receiver
  /// of a direct or base call, as an argument, or through a local that holds it.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>Each call once, with the name of the first constructor found to reach it.</returns>
  public static IReadOnlyList<(string Constructor, IrMethodCall Call)> VirtualCalls(IrModule module) {
    var functions = module.Functions.GroupBy(function => function.Name).ToDictionary(group => group.Key, group => group.First());
    var classes = module.Types.OfType<IrClassType>().Where(type => type.HasMembers).ToList();
    var found = new List<(string, IrMethodCall)>();
    var reported = new HashSet<IrMethodCall>(ReferenceEqualityComparer.Instance);
    foreach (var constructor in classes.SelectMany(type => type.Constructors)) {
      var visited = new HashSet<(string, int)>();
      var pending = new Stack<(string Function, int Parameter)>([(constructor.Function, 0)]);
      while (pending.TryPop(out var next)) {
        if (!visited.Add(next) || !functions.TryGetValue(next.Function, out var function) || next.Parameter >= function.Parameters.Count)
          continue;

        foreach (var (callee, parameter, call) in Reached(function, function.Parameters[next.Parameter], classes)) {
          if (call is not null && reported.Add(call))
            found.Add((constructor.Function, call));

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

  /// <summary>The functions a call of a method may run: the method's own, and for a virtual or an interface call every override and implementation of it.</summary>
  private static IEnumerable<string> Implementations(IrMethodCall call, List<IrClassType> classes)
    => call.Kind is IrMethodCallKind.Virtual or IrMethodCallKind.Interface
      ? classes.SelectMany(type => type.Methods).Where(method => method.Root == call.Method.Root || method.Implements.Contains(call.Method)
        || method.Overrides is not null && method.Root.Implements.Contains(call.Method)).Select(method => method.Function).Append(call.Method.Function)
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
