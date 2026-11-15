namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  private sealed partial class Machine {

    /// <summary>
    /// A foreign call, or the last error one left (#1332). The interpreter has no native library to call and is no
    /// oracle for one: the arguments are evaluated, left to right, and then it traps <see cref="IrTrap.InvalidOperation"/>
    /// - the <see cref="IrForeign.Trap"/>.
    /// </summary>
    private object Foreign(IrExpr expression, Frame frame) {
      foreach (var argument in IrTree.Operands(expression))
        this.Evaluate(argument, frame);

      throw new IrTrapException(IrTrap.InvalidOperation);
    }

  }

}
