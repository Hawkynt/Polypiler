namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  private sealed partial class Machine {

    /// <summary>
    /// An element address (#111): the base, then the indices in order, each checked against its own dimension
    /// as soon as it is known where the address is checked; then the base moved by the linear position.
    /// </summary>
    private object ElementAddress(IrElementAddress address, Frame frame) {
      var pointer = AsPointer(this.Evaluate(address.Base, frame));
      var indices = new long[address.Indices.Count];
      for (var dimension = 0; dimension < indices.Length; ++dimension) {
        var index = address.Indices[dimension];
        indices[dimension] = (long)this.Evaluate(index, frame);
        if (address.Checked && !IrArrays.Inside(address.Shape.Dimensions[dimension], index.Type, indices[dimension]))
          throw new IrTrapException(IrTrap.IndexOutOfRange);
      }

      var linear = address.Shape.Unchecked(indices);
      return pointer with { Offset = unchecked(pointer.Offset + (linear * address.Type.Element!.Size)) };
    }

  }

}
