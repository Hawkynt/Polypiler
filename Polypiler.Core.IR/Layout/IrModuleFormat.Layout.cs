using System.IO;

namespace Polypiler.Core.IR;

public static partial class IrModuleFormat {

  private sealed partial class Writer {

    /// <summary>A layout query - what is asked, of which type, of which field - or a field address - the record, the field and the base (#110).</summary>
    private void Layout(IrExpr expression) {
      if (expression is IrLayoutOf query) {
        this.Count((int)query.Query);
        this.Type(query.Of);
        writer.Write(query.Field is not null);
        if (query.Field is not null)
          this.Field(query.Field);

        return;
      }

      var address = (IrFieldAddress)expression;
      this.Reference(address.Record);
      this.Field(address.Field);
      this.Node(address.Base);
    }

  }

  private sealed partial class Reader {

    /// <summary>A layout query or a field address, as <see cref="Writer"/> wrote it.</summary>
    private IrExpr Layout(Tag tag) {
      if (tag == Tag.LayoutOf) {
        var query = this.Enum<IrLayoutQuery>();
        var of = this.Type();
        return new IrLayoutOf(query, of, reader.ReadBoolean() ? this.Field() : null);
      }

      var record = this.Declaration("record") as IrStructType ?? throw new InvalidDataException("A record is expected where another declaration is named.");
      var field = this.Field();
      return new IrFieldAddress(this.Node<IrExpr>(), record, field);
    }

    /// <summary>The byte order of a field, written one above its value, 0 for none (#113).</summary>
    private IrByteOrder? ByteOrder() => this.Count() switch {
      0 => null,
      var order => (IrByteOrder)(order - 1) is var value && System.Enum.IsDefined(value) ? value : throw new InvalidDataException($"{order - 1} is no byte order.")
    };

  }

}
