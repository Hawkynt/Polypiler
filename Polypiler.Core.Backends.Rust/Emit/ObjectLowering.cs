using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// The objects of a module as calls, so that every analysis of the backend - which arrays a function
/// writes, which references may be null - sees one kind of call: a method called directly is a call
/// of its function with the receiver as the first argument, and <c>new T(...)</c> is a call of a
/// function the lowering adds per constructor, named <see cref="NewPrefix"/> and the constructor's
/// function, which makes the object, runs the constructor on it and returns it. A call that selects
/// its method by the receiver's class stays the call it is.
/// </summary>
internal static class ObjectLowering {

  /// <summary>What the name of a function that makes an object starts with.</summary>
  public const string NewPrefix = "new:";

  /// <summary>The module with its direct method calls and its constructions as calls.</summary>
  public static IrModule Run(IrModule module) {
    var classes = module.Types.OfType<IrClassType>().ToList();
    if (classes.Count == 0)
      return module;

    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var makers = new List<IrFunction>();
    foreach (var type in classes.Where(type => !type.IsAbstract)) {
      foreach (var constructor in type.Constructors) {
        if (!byName.TryGetValue(constructor.Function, out var function))
          continue;

        var objectType = IrType.Object(type);
        var self = new IrLocal(function.Parameters[0].Name, objectType);
        var parameters = function.Parameters.Skip(1).ToList();
        var body = new IrBlock([
          new IrDeclare(self, new IrNew(objectType, constructor, [])),
          new IrExprStmt(new IrCall(constructor.Function, IrType.Void, [new IrLocalRef(self), .. parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter))])),
          new IrReturn(new IrLocalRef(self))
        ]);
        makers.Add(new IrFunction(NewPrefix + constructor.Function, false, objectType, parameters, [self], body));
      }
    }

    var functions = module.Functions.Select(Lowered).Concat(makers).ToList();
    return module with { Functions = functions };
  }

  private static IrFunction Lowered(IrFunction function) {
    var body = AliasSpecialization.Rewrite(function.Body, expression => expression switch {
      IrMethodCall { Kind: IrMethodCallKind.Direct or IrMethodCallKind.Base } call
        => new IrCall(call.Method.Function, call.Type, call.Target is null ? call.Arguments : [call.Target, .. call.Arguments]),
      IrNew creation when creation.Type.ClassType is { IsLibrary: false }
        => new IrCall(NewPrefix + creation.Constructor.Function, creation.Type, creation.Arguments),
      _ => expression
    });
    return ReferenceEquals(body, function.Body) ? function : function with { Body = body };
  }

}
