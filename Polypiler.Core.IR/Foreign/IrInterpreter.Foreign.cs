using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  private sealed partial class Machine {

    /// <summary>
    /// A foreign call, or the last error one left (#1332). The interpreter has no native library to call and is no
    /// oracle for one: the arguments are evaluated, left to right, and then it traps <see cref="IrTrap.InvalidOperation"/>
    /// - the <see cref="IrForeign.Trap"/>.
    /// </summary>
    private object Foreign(IrExpr expression, Frame frame) {
      var arguments = IrTree.Operands(expression).Select(argument => this.Evaluate(argument, frame)).ToList();
      // A run resolves the call through its external model (#119).
      if (this.Probe is { } probe && expression is IrForeignCall call)
        return this.Modelled(probe, call, arguments)!;

      throw new IrTrapException(IrTrap.InvalidOperation);
    }

  }

}
