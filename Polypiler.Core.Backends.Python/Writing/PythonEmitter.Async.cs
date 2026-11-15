using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The awaits of the IR (#1330); <see langword="null"/> for any other
    /// expression. The IR awaits every async value where it creates it, so nothing of the caller runs
    /// between the start of the awaited work and its end, and no coroutine ever suspends on anything but
    /// another coroutine: Python runs them in exactly the order the interpreter does. An await of a call
    /// is <c>await</c>, of a result the value, of the completed task <c>None</c> (left out as a
    /// statement), and WhenAll is <c>await asyncio.gather(…)</c>, whose list is the IR's array: gather
    /// starts the coroutines in argument order and each runs to its end before the next starts, so the
    /// first fault in time is the first in argument order.
    /// </summary>
    private PyExpression? AsyncExpression(IrExpr expression) {
      switch (expression) {
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.Completed } }:
          return new PyNone();
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.FromResult } result }:
          return this.Expression(result.Arguments[0]);
        case IrAwait { Operand: IrAsync all }:
          return new PyAwait(new PyCall(new PyAttribute(Name("asyncio"), "gather"), [.. all.Arguments.Select(this.Expression)]));
        case IrAwait awaited:
          return new PyAwait(this.Expression(awaited.Operand));
        default:
          return null;
      }
    }

  }

}
