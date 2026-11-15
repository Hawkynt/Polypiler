using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// The panics of the imported subset as the throws of the IR's exception classes
/// (<c>docs/reference/exceptions.md</c>). A Rust panic has no type; what the import keeps of it is
/// its kind, which the IR's trap classes carry:
/// <list type="table">
///   <listheader><term>Rust failure</term><description>IR class (trap)</description></listheader>
///   <item><term>arithmetic overflow, a shift amount out of range, <c>MIN / -1</c>, <c>MIN % -1</c></term><description><c>OverflowException</c> (<c>IntegerOverflow</c>)</description></item>
///   <item><term>division or remainder by zero</term><description><c>DivideByZeroException</c> (<c>DivideByZero</c>)</description></item>
///   <item><term><c>unwrap()</c> of a <c>None</c>, <c>expect("msg")</c>, <c>step_by(0)</c></term><description><c>InvalidOperationException</c></description></item>
///   <item><term><c>clamp</c> with <c>min &gt; max</c></term><description><c>ArithmeticException</c> (<c>InvalidOperation</c>, the IR's <c>Clamp</c>)</description></item>
/// </list>
/// The message of a throw is Rust's own panic message; messages are no part of the cross-language
/// contract, the class is.
/// </summary>
internal static class RustTraps {

  /// <summary>A panic for an arithmetic overflow, standing where a value of <paramref name="type"/> is expected.</summary>
  public static IrExpr Overflow(IrType type, string message) => Throw(type, IrExceptionClass.OverflowException, message);

  /// <summary>An explicit panic: <c>unwrap</c> of a <c>None</c>, <c>expect</c>, <c>step_by(0)</c>.</summary>
  public static IrExpr Panic(IrType type, string message) => Throw(type, IrExceptionClass.InvalidOperationException, message);

  /// <summary>The panic statement of <see cref="Panic(IrType, string)"/>.</summary>
  public static IrStmt PanicStatement(string message) => new IrThrow(New(IrExceptionClass.InvalidOperationException, message));

  private static IrExpr Throw(IrType type, IrClassType exception, string message) => new IrThrowExpression(type, New(exception, message));

  private static IrNew New(IrClassType exception, string message)
    => new(IrType.Object(exception), IrExceptionClass.Constructor(exception, 1), [new IrStrConst(message)]);

}
