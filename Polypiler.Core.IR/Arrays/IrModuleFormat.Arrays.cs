using System.IO;

namespace Polypiler.Core.IR;

public static partial class IrModuleFormat {

  private sealed partial class Writer {

    /// <summary>An element address (#111): the shape - order, rank, each lower bound and extent - whether it is checked, the base and the indices.</summary>
    private void ElementAddress(IrElementAddress address) {
      this.Count((int)address.Shape.Order);
      this.Count(address.Shape.Rank);
      foreach (var dimension in address.Shape.Dimensions) {
        writer.Write(dimension.Lower);
        writer.Write(dimension.Extent);
      }

      writer.Write(address.Checked);
      this.Node(address.Base);
      this.Nodes(address.Indices);
    }

  }

  private sealed partial class Reader {

    /// <summary>An element address, as <see cref="Writer"/> wrote it.</summary>
    private IrElementAddress ElementAddress() {
      var order = this.Enum<IrStorageOrder>();
      var dimensions = this.List(() => new IrDimension(reader.ReadInt64(), reader.ReadInt64()));
      IrArrayShape shape;
      try {
        shape = new IrArrayShape(dimensions, order);
      } catch (System.ArgumentException exception) {
        throw new InvalidDataException($"An element address of no shape: {exception.Message}");
      }

      var isChecked = reader.ReadBoolean();
      return new IrElementAddress(this.Node<IrExpr>(), shape, this.Nodes<IrExpr>(), isChecked);
    }

  }

}
