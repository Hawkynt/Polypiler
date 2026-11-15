using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Waits for the async value of its operand and gives its result, or throws its fault (#1330): C#'s and
/// JavaScript's <c>await</c>. It stands in the body of an async function only, never inside a lambda,
/// and its operand is where the async value is created - the call of an async function of the module
/// or an <see cref="IrAsync"/> - so that no effect of the caller runs between the start of the awaited
/// work and its end, which is where .NET and JavaScript order effects differently.
/// </summary>
/// <param name="Operand">The async value awaited.</param>
public sealed record IrAwait(IrExpr Operand) : IrExpr(Operand.Type.IsAsync ? Operand.Type.Element! : IrType.Void);

/// <summary>The async values the IR makes itself, which <see cref="IrAsync"/> names.</summary>
public enum IrAsyncOp {

  /// <summary>The async value that has completed without a value: <c>Task.CompletedTask</c>; of the type <c>async&lt;void&gt;</c>, without operands.</summary>
  Completed,

  /// <summary>The async value that has completed with the value of its one operand: <c>Task.FromResult(v)</c>, <c>Promise.resolve(v)</c>.</summary>
  FromResult,

  /// <summary>
  /// Waits for every operand, each the call of an async function of the module, and completes with
  /// their results in argument order - an array, or nothing when they have none - or, when one of
  /// them faulted, with the fault of the first that faulted in argument order: <c>Task.WhenAll</c>.
  /// </summary>
  WhenAll

}

/// <summary>An async value the IR makes itself (<see cref="IrAsyncOp"/>).</summary>
/// <param name="Op">The operation.</param>
/// <param name="Type">Its type, an <see cref="IrTypeKind.Async"/>.</param>
/// <param name="Arguments">The operands.</param>
public sealed record IrAsync(IrAsyncOp Op, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>
/// The rules of async functions (#1330). The IR keeps to the subset whose order of effects is the same
/// in .NET and in JavaScript: every async value is awaited where it is created, and the calls handed to
/// <see cref="IrAsyncOp.WhenAll"/> are isolated, so that their interleaving is unobservable.
/// </summary>
public static class IrAsyncFunctions {

  /// <summary>What every diagnostic says of an async value that is not awaited where it is created.</summary>
  public const string NotAwaited = "a task that is not awaited where it is created; its order differs between .NET and JavaScript";

  /// <summary>Whether a type may be the result of an async function: none, or a value a local holds that is no view, memory, pointer or lazy sequence.</summary>
  /// <param name="result">The type of the result.</param>
  /// <returns><see langword="true"/> when an async function may return it.</returns>
  public static bool IsResult(IrType result)
    => result == IrType.Void || (result.IsStorable && !result.IsView && !result.IsMemory && !result.IsPointer && !result.IsLazySequence);

  /// <summary>
  /// The type of an operation on its operands, or why it has none: <see cref="IrAsyncOp.Completed"/>
  /// takes nothing, <see cref="IrAsyncOp.FromResult"/> one value, <see cref="IrAsyncOp.WhenAll"/> async
  /// values of one result type - an array of it, or nothing for none.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="arguments">The types of its operands.</param>
  /// <param name="element">The result type of <see cref="IrAsyncOp.WhenAll"/> when it has no operand.</param>
  /// <returns>The type, or <see langword="null"/> and the reason.</returns>
  public static (IrType? Type, string? Problem) Signature(IrAsyncOp op, IReadOnlyList<IrType> arguments, IrType? element = null) {
    switch (op) {
      case IrAsyncOp.Completed:
        return arguments.Count == 0 ? (IrType.Async(IrType.Void), null) : (null, "Completed takes no operand");
      case IrAsyncOp.FromResult:
        return arguments is [var value] && value != IrType.Void && IsResult(value) ? (IrType.Async(value), null) : (null, "FromResult takes one value");
      default:
        if (arguments.Any(argument => !argument.IsAsync || argument.IsValueTask))
          return (null, "WhenAll takes async values");

        var results = arguments.Select(argument => argument.Element!).Distinct().ToList();
        if (results.Count > 1)
          return (null, "WhenAll takes async values of one result type");

        var result = results.Count == 1 ? results[0] : element ?? IrType.Void;
        return result == IrType.Void ? (IrType.Async(IrType.Void), null)
          : IsResult(IrType.Array(result)) ? (IrType.Async(IrType.Array(result)), null)
          : (null, $"WhenAll of results of the type {result}, of which there is no array");
    }
  }

  /// <summary>
  /// The functions of a module that write nothing but their own locals and call nothing but such
  /// functions: their effects cannot interleave with those of another call, so <see cref="IrAsyncOp.WhenAll"/>
  /// may run them in any order. A function that throws is isolated still - the fault is chosen by
  /// argument order - but one that writes an array, a field or a global, that calls a method, a
  /// function value, native code or another function that is not isolated, that reads the error a
  /// native call left, or that touches a collection is not.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names of the isolated functions.</returns>
  public static IReadOnlySet<string> Isolated(IrModule module) => Closure(module, Writes);

  /// <summary>
  /// The functions of a module that cannot fault: whose bodies throw nothing, trap nowhere and call
  /// nothing but such functions. Of the calls a JavaScript <c>Promise.all</c> is given, at most one may
  /// fault, for its rejection is the first fault in time where <c>Task.WhenAll</c>'s is the first in
  /// argument order.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names of the functions that cannot fault.</returns>
  public static IReadOnlySet<string> FaultFree(IrModule module) => Closure(module, MayFault);

  /// <summary>
  /// The calls of <see cref="IrAsyncOp.WhenAll"/> of a module whose order is observable: one of the
  /// functions they call is not <see cref="Isolated"/>.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>Each function, the operation and the first function it calls that is not isolated.</returns>
  public static IEnumerable<(IrFunction Function, IrAsync WhenAll, string Callee)> Timed(IrModule module) {
    var isolated = Isolated(module);
    foreach (var function in module.Functions)
      foreach (var operation in IrTree.Nodes(function.Body).OfType<IrAsync>().Where(operation => operation.Op == IrAsyncOp.WhenAll))
        if (operation.Arguments.OfType<IrCall>().FirstOrDefault(call => !isolated.Contains(call.Function)) is { } call)
          yield return (function, operation, call.Function);
  }

  /// <summary>The functions none of whose statements and expressions is <paramref name="excluded"/>, and whose callees are of them as well: the greatest fixed point.</summary>
  private static HashSet<string> Closure(IrModule module, System.Func<object, bool> excluded) {
    var functions = new Dictionary<string, IrFunction>();
    foreach (var function in module.Functions)
      functions.TryAdd(function.Name, function);

    var kept = functions.Values.Where(function => !Parts(function.Body).Any(excluded)).Select(function => function.Name).ToHashSet();
    for (var changed = true; changed;) {
      changed = false;
      foreach (var name in kept.ToList())
        if (IrTree.Nodes(functions[name].Body).OfType<IrCall>().Any(call => !kept.Contains(call.Function))) {
          kept.Remove(name);
          changed = true;
        }
    }

    return kept;
  }

  /// <summary>Every statement and expression node of a block, those inside lambdas included.</summary>
  private static IEnumerable<object> Parts(IrBlock block) => IrSequences.Statements(block).Cast<object>().Concat(IrTree.Nodes(block));

  /// <summary>Whether a part of a function writes, or may write, anything but a local of its own.</summary>
  private static bool Writes(object part) => part switch {
    IrGlobalAssign or IrStore or IrMethodCall or IrInvoke or IrCollection or IrLambda => true,
    // A native call (#1332) has effects outside the module, and the error one left is state another call changes.
    IrForeignCall or IrLastError => true,
    // An exception of the library is made without code of the module; a constructor of the module writes the fields of its object.
    IrNew creation => creation.Type.ClassType is not { IsLibrary: true },
    IrFieldStore store => store.Target.Type.IsObject || !IsLocal(store.Target),
    IrStep step => step.Target is not IrLocalRef,
    IrIntrinsic intrinsic => intrinsic.Type == IrType.Void,
    _ => false
  };

  /// <summary>Whether a place is a local or a field of a struct a local holds.</summary>
  private static bool IsLocal(IrExpr place) => place switch {
    IrLocalRef => true,
    IrFieldLoad load => !load.Target.Type.IsObject && IsLocal(load.Target),
    _ => false
  };

  /// <summary>Whether a part of a function may throw or trap: everything but the plain arithmetic, reads of locals, globals and constants, and calls.</summary>
  private static bool MayFault(object part) => part switch {
    IrStmt statement => statement is IrThrow or IrRethrow or IrStore or IrFieldStore or IrForEachCollection or IrForEachSequence or IrUsing or IrYieldFrom,
    IrConst or IrLocalRef or IrGlobalRef or IrStrConst or IrConstRef or IrConditional or IrCall or IrAwait or IrAsync or IrStructNew or IrStructDefault or IrStructEquals => false,
    IrUnary unary => unary.Op == IrUnaryOp.NegateChecked,
    IrBinary binary => IrOperators.Traps(binary.Op) || (binary.Op is IrBinaryOp.Divide or IrBinaryOp.Remainder && binary.Type.IsInteger),
    IrConvert conversion => conversion.Contract == IrConversion.Trap,
    IrFieldLoad load => load.Target.Type.IsObject,
    IrStep step => step.Trap || step.Target is not IrLocalRef,
    _ => true
  };

}
