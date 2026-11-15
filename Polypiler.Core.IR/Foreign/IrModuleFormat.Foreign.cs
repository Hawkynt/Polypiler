using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IrModuleFormat {

  private sealed partial class Writer {

    /// <summary>The foreign functions of a module (#1332): each with its identity, its parameters and the marshalling of each.</summary>
    private void ForeignFunctions(IReadOnlyList<IrForeignFunction> functions) {
      this.Count(functions.Count);
      foreach (var function in functions) {
        writer.Write(function.Name);
        writer.Write(function.IsPublic);
        writer.Write(function.Library);
        this.Type(function.ReturnType);
        this.Count(function.Parameters.Count);
        foreach (var parameter in function.Parameters) {
          writer.Write(parameter.Name);
          this.Type(parameter.Type);
          this.Marshal(parameter.Marshal);
        }

        this.Count((int)function.Origin);
        writer.Write(function.EntryPoint is not null);
        if (function.EntryPoint is not null)
          writer.Write(function.EntryPoint);

        this.Count((int)function.Convention);
        this.Count(function.CharSet is { } encoding ? (int)encoding + 1 : 0);
        this.Count(function.SetLastError is { } keeps ? (keeps ? 2 : 1) : 0);
        this.Count(function.ExactSpelling is { } exact ? (exact ? 2 : 1) : 0);
        this.Marshal(function.Result);
      }
    }

    private void Marshal(IrMarshal marshal) {
      this.Count((int)marshal.Kind);
      this.Count((int)marshal.Direction);
      this.Count((int)marshal.Encoding);
      writer.Write(marshal.NativeSize);
      this.Count((int)marshal.BoolForm);
      writer.Write(marshal.Attributed);
      writer.Write(marshal.Directed);
    }

    /// <summary>The layout of a struct: none, or its kind, packing, size and offsets.</summary>
    private void Layout(IrStructLayout? layout) {
      writer.Write(layout is not null);
      if (layout is null)
        return;

      this.Count((int)layout.Kind);
      this.Count(layout.Pack);
      this.Count(layout.Size);
      this.Count(layout.Offsets.Count);
      foreach (var offset in layout.Offsets)
        this.Count(offset);
    }

  }

  private sealed partial class Reader {

    private IReadOnlyList<IrForeignFunction> ForeignFunctions() => this.List(() => {
      var name = reader.ReadString();
      var isPublic = reader.ReadBoolean();
      var library = reader.ReadString();
      var result = this.Type();
      var parameters = this.List(() => new IrForeignParameter(reader.ReadString(), this.Type(), this.Marshal()));
      return new IrForeignFunction(name, isPublic, library, result, parameters) {
        Origin = this.Enum<IrForeignOrigin>(),
        EntryPoint = reader.ReadBoolean() ? reader.ReadString() : null,
        Convention = this.Enum<IrCallingConvention>(),
        CharSet = this.Count() is var encoding and > 0 ? (IrStringEncoding)(encoding - 1) : null,
        SetLastError = this.Count() is var keeps and > 0 ? keeps == 2 : null,
        ExactSpelling = this.Count() is var exact and > 0 ? exact == 2 : null,
        Result = this.Marshal()
      };
    });

    private IrMarshal Marshal() => new(this.Enum<IrMarshalKind>(), this.Enum<IrMarshalDirection>()) {
      Encoding = this.Enum<IrStringEncoding>(),
      NativeSize = reader.ReadBoolean(),
      BoolForm = this.Enum<IrBoolForm>(),
      Attributed = reader.ReadBoolean(),
      Directed = reader.ReadBoolean()
    };

    private IrStructLayout? Layout() {
      if (!reader.ReadBoolean())
        return null;

      var kind = this.Enum<IrLayoutKind>();
      var pack = this.Count();
      var size = this.Count();
      return new IrStructLayout(kind, pack, size, this.List(this.Count));
    }

  }

}
