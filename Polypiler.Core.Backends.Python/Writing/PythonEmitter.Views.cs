using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The class a view - a span or a memory - is an object of, which the module then declares.</summary>
    private string ViewClass() {
      this._helpers.Add(PythonHelpers.View);
      return PythonHelpers.View;
    }

    /// <summary>The class a pointer is an object of, which the module then declares.</summary>
    private string PointerClass() {
      this._helpers.Add(PythonHelpers.Pointer);
      return PythonHelpers.Pointer;
    }

    /// <summary>An empty view, the default of a field of a memory type.</summary>
    private PyExpression EmptyView(IrType type) => new PyCall(Name(this.ViewClass()), [new PyList([]), Integer(0), Integer(0)]);

    /// <summary>The <c>struct</c> format letter of a number type, little-endian sizes: <c>b B h H i I q Q f d</c>.</summary>
    private static string Format(IrType type) => type.Kind switch {
      IrTypeKind.I8 => "b",
      IrTypeKind.U8 => "B",
      IrTypeKind.I16 => "h",
      IrTypeKind.U16 => "H",
      IrTypeKind.I32 => "i",
      IrTypeKind.U32 => "I",
      IrTypeKind.I64 => "q",
      IrTypeKind.U64 => "Q",
      IrTypeKind.F32 => "f",
      IrTypeKind.F64 => "d",
      _ => "?"
    };

    /// <summary>
    /// The operations on arrays and views. A view is a <c>_PlyView</c> of an array, a first index and a
    /// length, which checks every index against its own length; a view of the bytes of another type is a
    /// <c>_PlyView</c> that reads and writes them with <c>struct</c>. Copies go through a slice of the
    /// source, which is a new list, so overlapping ranges are safe; the endian accesses read and write the
    /// bytes with <c>int.from_bytes</c>, <c>int.to_bytes</c> and <c>struct</c>, after the range is checked.
    /// </summary>
    private PyExpression? ViewOperation(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      PyExpression Arg(int index) => this.Expression(arguments[index]);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayCopy:
          return this.Helper(PythonHelpers.ArrayCopy, Arg(0), Arg(1), Arg(2), Arg(3), Arg(4));
        case IrIntrinsicOp.ArrayFill:
          return this.Helper(PythonHelpers.ArrayFill, Arg(0), Arg(1));
        case IrIntrinsicOp.ArrayClear:
          return this.Helper(PythonHelpers.ArrayFill, Arg(0), this.FieldDefault(arguments[0].Type.Element!));
        case IrIntrinsicOp.ViewOf:
          this.ViewClass();
          return this.Helper(PythonHelpers.ViewOf, [.. arguments.Select(this.Expression)]);
        case IrIntrinsicOp.Slice:
          this.ViewClass();
          return this.Helper(PythonHelpers.ViewOf, [.. arguments.Select(this.Expression)]);
        case IrIntrinsicOp.ViewLength:
          return new PyCall(Name("len"), [Arg(0)]);
        case IrIntrinsicOp.StackAlloc when !intrinsic.Type.IsPointer:
          var element = intrinsic.Type.Element!;
          var length = this.NonNegative(arguments[0]) ? Arg(0) : this.Helper(PythonHelpers.Length, Arg(0));
          var zeros = Binary(new PyList([this.FieldDefault(element)]), "*", length);
          return this.Helper(PythonHelpers.ViewOf, zeros);
        case IrIntrinsicOp.ViewCopy:
          return this.Helper(PythonHelpers.ViewCopy, Arg(0), Arg(1), new PyBool(false));
        case IrIntrinsicOp.ViewTryCopy:
          return this.Helper(PythonHelpers.ViewCopy, Arg(0), Arg(1), new PyBool(true));
        case IrIntrinsicOp.ViewFill:
          return this.Helper(PythonHelpers.ArrayFill, Arg(0), Arg(1));
        case IrIntrinsicOp.ViewEqual:
          return this.Helper(arguments[0].Type.Element!.IsFloat ? PythonHelpers.ViewEqualFloat : PythonHelpers.ViewEqual, Arg(0), Arg(1));
        case IrIntrinsicOp.ViewToArray:
          return new PyCall(Name("list"), [Arg(0)]);
        case IrIntrinsicOp.Reinterpret:
          this.ViewClass();
          var from = arguments[0].Type.Element!;
          var to = intrinsic.Type.Element!;
          return this.Helper(PythonHelpers.Reinterpret, Arg(0), new PyString(Format(from)), new PyString(Format(to)));
        case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig:
          return this.Load(intrinsic.Type, Arg(0), Arg(1), intrinsic.Op == IrIntrinsicOp.LoadLittle);
        case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig:
          return this.Store(arguments[2].Type, Arg(0), Arg(1), Arg(2), intrinsic.Op == IrIntrinsicOp.StoreLittle);
        default:
          return null;
      }
    }

    /// <summary>
    /// An endian load from a byte array or view: <c>int.from_bytes(bytes, "little")</c> of the bytes
    /// the range check gives, <c>signed=True</c> for a signed type, or <c>struct.unpack</c> of a float.
    /// </summary>
    private PyExpression Load(IrType type, PyExpression buffer, PyExpression offset, bool little) {
      var bytes = this.Helper(PythonHelpers.Bytes, buffer, offset, Integer(type.Size));
      if (type.IsFloat)
        return new PySubscript(Struct("unpack", new PyString((little ? "<" : ">") + Format(type)), bytes), Integer(0));

      var call = new PyCall(new PyAttribute(Name("int"), "from_bytes"), [bytes, new PyString(little ? "little" : "big")]);
      return type.IsSigned ? call with { Keywords = [new("signed", new PyBool(true))] } : call;
    }

    /// <summary>An endian store into a byte array or view: the bytes of <c>int.to_bytes</c>, or of <c>struct.pack</c> of a float, put in after the range check.</summary>
    private PyExpression Store(IrType type, PyExpression buffer, PyExpression offset, PyExpression value, bool little) {
      PyExpression bytes;
      if (type.IsFloat) {
        bytes = Struct("pack", new PyString((little ? "<" : ">") + Format(type)), value);
      } else {
        var call = new PyCall(new PyAttribute(value, "to_bytes"), [Integer(type.Size), new PyString(little ? "little" : "big")]);
        bytes = type.IsSigned ? call with { Keywords = [new("signed", new PyBool(true))] } : call;
      }

      return this.Helper(PythonHelpers.PutBytes, buffer, offset, bytes);
    }

  }

}
