namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  private sealed partial class Machine {

    /// <summary>A resource scope, run as the statements it means (<see cref="IrUsing.Lowered"/>).</summary>
    private Flow Using(IrUsing scope, Frame frame) => this.Run(scope.Lowered(), frame);

  }

}
