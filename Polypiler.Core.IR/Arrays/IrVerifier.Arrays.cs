namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// An element address (#111): a pointer to a number as the base and as the type, one integer index per
    /// dimension, and every constant index inside its dimension, checked or not.
    /// </summary>
    private void ElementAddress(IrElementAddress address) {
      this.Expression(address.Base);
      foreach (var index in address.Indices)
        this.Expression(index);

      this.Require(address.Base.Type.IsPointer && address.Base.Type.Element!.IsNumeric, $"the base of an element address is a {address.Base.Type}, not a pointer to a number");
      this.Require(address.Type == address.Base.Type, $"an element address has the type {address.Type}, expected {address.Base.Type}");
      var shape = address.Shape;
      this.Require(address.Indices.Count == shape.Rank, $"'ElemAddr' on a rank-{shape.Rank} array needs {shape.Rank} indices, got {address.Indices.Count}");
      for (var dimension = 0; dimension < address.Indices.Count; ++dimension) {
        var index = address.Indices[dimension];
        var integer = index.Type.IsInteger && !index.Type.IsEnum;
        this.Require(integer, $"index {dimension} has type {index.Type}, expected an integer");
        if (integer && dimension < shape.Rank && index is IrConst constant && !IrArrays.Inside(shape.Dimensions[dimension], index.Type, constant.Value))
          this.Require(false, $"constant index {IrArrays.Spelled(index.Type, constant.Value)} is outside {shape.Dimensions[dimension]} in dimension {dimension}");
      }
    }

  }

}
