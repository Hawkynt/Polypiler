using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IrModuleFormat {

  private sealed partial class Writer {

    /// <summary>A trap label (#115): its name, its kind and the name of its exception type, if any.</summary>
    private void Label(TrapLabel label) {
      writer.Write(label.Name);
      this.Count((int)label.Kind);
      writer.Write(label.ExceptionType is not null);
      if (label.ExceptionType is not null)
        writer.Write(label.ExceptionType);
    }

    /// <summary>The trap table of a module, or the labels of a handler.</summary>
    private void Labels(IReadOnlyList<TrapLabel> labels) {
      this.Count(labels.Count);
      foreach (var label in labels)
        this.Label(label);
    }

  }

  private sealed partial class Reader {

    private TrapLabel Label() {
      var name = reader.ReadString();
      var kind = this.Enum<TrapKind>();
      return new TrapLabel(name, kind, reader.ReadBoolean() ? reader.ReadString() : null);
    }

    private IReadOnlyList<TrapLabel> Labels() => this.List(this.Label);

  }

}
