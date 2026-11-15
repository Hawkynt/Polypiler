using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// A resource scope - C#'s <c>using</c> (ECMA-334, "The using statement"): evaluates
/// <paramref name="Initializer"/>, binds it to <paramref name="Resource"/>, runs <paramref name="Body"/>,
/// and on every way out of the body - its end, <c>break</c>, <c>continue</c>, <c>return</c> (whose
/// value is computed before) and an exception - calls <paramref name="Dispose"/> on the resource
/// through the interface (<see cref="IrMethodCallKind.Interface"/>) unless the resource is <c>null</c>.
/// <para>
/// It is exactly <see cref="Lowered"/>: the declaration of the resource followed by
/// <c>try { Body } finally { if (Resource != null) Resource.Dispose(); }</c>, so the rules of
/// <see cref="IrThrow"/> hold - an exception thrown by <c>Dispose</c> replaces the one leaving the
/// body. Several resources are scopes nested in the order they are acquired, so they are disposed in
/// the reverse order. The resource is read-only: nothing in the body assigns it.
/// </para>
/// </summary>
/// <param name="Resource">The local that holds the resource; the body sees it, nothing assigns it.</param>
/// <param name="Initializer">The value acquired, of the type of the resource.</param>
/// <param name="Dispose">The method <c>Dispose()</c> of an interface that <see cref="IrInterfaceType.IsDisposable"/>.</param>
/// <param name="Body">The statements the resource is held for.</param>
public sealed record IrUsing(IrLocal Resource, IrExpr Initializer, IrMethod Dispose, IrBlock Body) : IrStmt {

  /// <summary>
  /// Whether the source wrote the scope as a declaration - C# 8's <c>using var r = e;</c>, whose scope
  /// is the rest of its block - rather than as a statement. It changes no meaning: a backend keeps the
  /// form where its language has it and the scope is the last statement of its block.
  /// </summary>
  public bool IsDeclaration { get; init; }

  /// <summary>The call of <see cref="Dispose"/> on the resource.</summary>
  public IrMethodCall DisposeCall => new(IrType.Void, this.Dispose, new IrLocalRef(this.Resource), []) { Kind = IrMethodCallKind.Interface };

  /// <summary>Whether the resource is not <c>null</c>, which the call of <see cref="Dispose"/> is guarded by.</summary>
  public IrExpr Held => new IrUnary(IrUnaryOp.LogicalNot, new IrReferenceEquals(new IrLocalRef(this.Resource), new IrNull(this.Resource.Type)));

  /// <summary>
  /// The scope as the statements it means:
  /// <c>{ Resource = Initializer; try { Body } finally { if (Resource != null) Resource.Dispose(); } }</c>.
  /// </summary>
  /// <returns>A block of the declaration and the try.</returns>
  public IrBlock Lowered()
    => new([new IrDeclare(this.Resource, this.Initializer), new IrTry(this.Body, [], new IrBlock([new IrIf(this.Held, new IrBlock([new IrExprStmt(this.DisposeCall)]), null)]))]);

}

/// <summary>
/// The interface of deterministic cleanup - .NET's <c>System.IDisposable</c> - as a module declares
/// it: an interface with <see cref="IrInterfaceType.IsDisposable"/> set and the one method
/// <c>Dispose()</c>, whose signature is a function of the module like that of every other method of an
/// interface. Classes implement it as any interface; a backend names the platform's own.
/// </summary>
public static class IrDisposable {

  /// <summary>The name of the interface.</summary>
  public const string Name = "IDisposable";

  /// <summary>The name of its method.</summary>
  public const string Method = "Dispose";

  /// <summary>Makes the interface for a module.</summary>
  /// <returns>The interface, its method <c>Dispose</c>, and the function that is the method's signature, which the module has to hold.</returns>
  public static (IrInterfaceType Interface, IrMethod Dispose, IrFunction Signature) Declare() {
    var type = new IrInterfaceType(Name) { IsDisposable = true };
    var dispose = new IrMethod(Method, $"{Name}.{Method}", false) { Dispatch = IrDispatch.Abstract };
    type.DefineMembers([dispose], [], []);
    return (type, dispose, new IrFunction(dispose.Function, false, IrType.Void, [new IrLocal("this", IrType.Object(type))], [], new IrBlock([])));
  }

  /// <summary>The method <c>Dispose</c> of an interface of cleanup, or <see langword="null"/> for any other interface.</summary>
  /// <param name="type">The interface.</param>
  /// <returns>The method.</returns>
  public static IrMethod? DisposeOf(IrInterfaceType type) => type.IsDisposable ? type.Methods.FirstOrDefault(method => method.Name == Method) : null;

}
