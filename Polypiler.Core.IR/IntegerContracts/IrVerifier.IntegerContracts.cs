namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>An integer operation with its contract (#118): its operands, then the rules of <see cref="IrIntegerContracts.Problems"/>.</summary>
    private void IntegerOp(IrIntegerOp node) {
      foreach (var operand in node.Operands)
        this.Expression(operand);

      foreach (var (descriptor, message) in IrIntegerContracts.Problems(node))
        diagnostics.Add(descriptor == Descriptor ? Error(function.Name, message) : descriptor.At($"{message} in '{function.Name}'"));
    }

  }

}
