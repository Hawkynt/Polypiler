using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Bytes and their layouts: <c>bytes</c> and <c>bytearray</c> made from a count, a list of integers or
/// other bytes; <c>int.from_bytes</c> and <c>int.to_bytes</c> in the byte order the source names;
/// <c>str.encode</c> to UTF-8; and <c>struct</c> with the formats of explicit byte order. A value
/// beyond what its field holds raises what CPython raises.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>A bytes literal: a new array of its bytes.</summary>
  private IrExpr BytesConstant(byte[] bytes)
    => Intrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [.. bytes.Select(octet => (IrExpr)new IrConst(IrType.U8, octet))]);

  /// <summary><c>bytes(…)</c> and <c>bytearray(…)</c>: empty, of a count of zero bytes, of the integers of a list, a copy of bytes, or a str encoded.</summary>
  private (IrExpr, PyType) MakeBytes(PyNode call, bool immutable) {
    var type = immutable ? PyType.Bytes : PyType.ByteArray;
    var arguments = call.Nodes("args");
    if (call.Nodes("keywords").Count > 0 || arguments.Count > 2)
      throw this.Unsupported(call, this.Quote(call));

    if (arguments.Count == 0)
      return (new IrNewArray(IrType.Array(IrType.U8), Int32(0)), type);

    var source = this.Value(arguments[0]);
    if (arguments.Count == 2) {
      if (source.Type.Kind != PyKind.Str)
        throw this.Unsupported(call, $"{this.Quote(call)} of a value of the type {source.Type} with an encoding, which raises TypeError,");

      return (this.EncodeTo(call, source, arguments[1]), type);
    }

    switch (ExpressionTypes.Value(source.Type).Kind) {
      case PyKind.Int or PyKind.Bool:
        return (this.Effect(this.Helpers.ZeroBytes(this.Integer(this.Plain(source, call)).Item1)), type);
      case PyKind.Bytes or PyKind.ByteArray:
        return (this.Effect(this.Helpers.BytesCopy(source.Value)), type);
      case PyKind.List or PyKind.Iterator or PyKind.Table when ExpressionTypes.Element(source.Type) is { Kind: PyKind.Int or PyKind.Bool } element:
        var integers = element.Kind == PyKind.Int ? this.Sequence(call, source) : this.Widened(this.Sequence(call, source), element);
        return (this.Effect(this.Helpers.BytesOf(integers)), type);
      case PyKind.Str:
        throw this.Unsupported(call, $"{this.Quote(call)} of a str without an encoding, which raises TypeError,");
      default:
        throw this.Unsupported(call, $"{this.Quote(call)} of a value of the type {source.Type}");
    }
  }

  /// <summary><c>s.encode()</c> and <c>s.encode("utf-8")</c>: the UTF-8 bytes; a lone surrogate raises <c>UnicodeEncodeError</c>.</summary>
  private (IrExpr, PyType) Encode(PyNode call, (IrExpr Value, PyType Type) text) {
    var arguments = call.Nodes("args");
    if (call.Nodes("keywords").Count > 0 || arguments.Count > 1)
      throw this.Refuse(call, PythonFrontend.UnsupportedStringDescriptor, $"string operation 'encode with an error handler' is not supported by the Python frontend");

    return (arguments.Count == 0 ? this.Effect(this.Helpers.Encode(text.Value, this.DeclaredException("UnicodeEncodeError"))) : this.EncodeTo(call, text, arguments[0]), PyType.Bytes);
  }

  private IrExpr EncodeTo(PyNode call, (IrExpr Value, PyType Type) text, PyNode encoding) {
    if (!(encoding.Is("Constant") && encoding.Text("value")?.ToLowerInvariant().Replace("-", "", System.StringComparison.Ordinal).Replace("_", "", System.StringComparison.Ordinal) is "utf8"))
      throw this.Refuse(encoding, PythonFrontend.UnsupportedStringDescriptor, $"string operation 'encode to {this.Quote(encoding)}' is not supported by the Python frontend; UTF-8 is");

    return this.Effect(this.Helpers.Encode(text.Value, this.DeclaredException("UnicodeEncodeError")));
  }

  /// <summary>Whether a byte order names the lowest byte first: <c>"little"</c>; <c>"big"</c> where none is named (SubsetCheck refused any other).</summary>
  private static bool Little(PyNode? order) => order?.Text("value") == "little";

  /// <summary>The value of a constant <c>True</c>/<c>False</c> keyword of a call, or its default.</summary>
  private bool Flag(PyNode call, string name) {
    var keyword = call.Nodes("keywords").FirstOrDefault(keyword => keyword.Text("arg") == name)?.Node("value");
    if (keyword is null)
      return false;

    return keyword.Is("Constant") && keyword.ConstantKind == "bool"
      ? keyword.Value is true
      : throw this.Unsupported(keyword, $"{name}= of a value other than True or False");
  }

  /// <summary>An argument given by position or by name, or <see langword="null"/>.</summary>
  private static PyNode? Argument(PyNode call, int position, string name)
    => call.Nodes("args").ElementAtOrDefault(position) ?? call.Nodes("keywords").FirstOrDefault(keyword => keyword.Text("arg") == name)?.Node("value");

  /// <summary>
  /// <c>int.from_bytes(b, byteorder, signed=…)</c>: the integer of any number of bytes, its order
  /// <c>"big"</c> where none is named; one beyond 64 bits raises <c>OverflowError</c>.
  /// </summary>
  private (IrExpr, PyType) FromBytes(PyNode call) {
    var bytes = this.Value(Argument(call, 0, "bytes") ?? throw this.Unsupported(call, "int.from_bytes without its bytes"));
    if (!bytes.Type.IsBytes)
      throw this.Unsupported(call, $"int.from_bytes of a value of the type {bytes.Type}");

    var little = Little(Argument(call, 1, "byteorder"));
    return (this.Effect(this.Helpers.FromBytes(bytes.Value, little, this.Flag(call, "signed"))), PyType.Int);
  }

  /// <summary>
  /// <c>x.to_bytes(length, byteorder, signed=…)</c>: the bytes of an integer, of length 1 and order
  /// <c>"big"</c> where none is named; <c>OverflowError</c> where the value does not fit.
  /// </summary>
  private (IrExpr, PyType) ToBytes(PyNode call, (IrExpr Value, PyType Type) value) {
    var lengthNode = Argument(call, 0, "length");
    var length = lengthNode is null ? Int(1) : this.Integer(this.Plain(this.Value(lengthNode), lengthNode)).Item1;
    var little = Little(Argument(call, 1, "byteorder"));
    return (this.Effect(this.Helpers.ToBytes(this.Integer(value).Item1, length, little, this.Flag(call, "signed"))), PyType.Bytes);
  }

  /// <summary><c>struct.pack</c>, <c>unpack</c>, <c>unpack_from</c> and <c>calcsize</c> of a constant format of explicit byte order.</summary>
  private (IrExpr, PyType) Struct(PyNode call, string name) {
    var arguments = call.Nodes("args");
    if (arguments.Count == 0)
      throw this.Unsupported(call, $"struct.{name} without a format");

    // SubsetCheck refused every other format, and a pack of other than as many values as it packs.
    var format = StructFormat.Parse(arguments[0]).Format!;
    var error = this.DeclaredException("error");
    switch (name) {
      case "calcsize" when arguments.Count == 1:
        return (Int(format.Size), PyType.Int);
      case "unpack" when arguments.Count == 2:
        var buffer = this.Value(arguments[1]);
        return buffer.Type.IsBytes
          ? (this.Effect(this.Helpers.Unpack(format, buffer.Value, null, PyTypes.Ir(format.Type), error)), format.Type)
          : throw this.Unsupported(call, $"struct.unpack of a value of the type {buffer.Type}");
      case "unpack_from" when arguments.Count is 2 or 3 || Argument(call, 2, "offset") is not null:
        var source = this.Value(arguments[1]);
        var offsetNode = Argument(call, 2, "offset");
        var offset = offsetNode is null ? Int(0) : this.Integer(this.Plain(this.Value(offsetNode), offsetNode)).Item1;
        return source.Type.IsBytes
          ? (this.Effect(this.Helpers.Unpack(format, source.Value, offset, PyTypes.Ir(format.Type), error)), format.Type)
          : throw this.Unsupported(call, $"struct.unpack_from of a value of the type {source.Type}");
      case "pack":
        var values = arguments.Skip(1).ToList();
        var packed = new List<IrExpr>();
        foreach (var (field, node) in format.Fields.Zip(values)) {
          var value = this.Plain(this.Value(node), node);
          packed.Add(field.Type.Kind switch {
            PyKind.Float when value.Type.IsNumber => Floating(value),
            PyKind.Bool => this.Truth(value),
            _ when value.Type.Kind is PyKind.Int or PyKind.Bool => this.Integer(value).Item1,
            _ => throw this.Unsupported(node, $"packing a value of the type {value.Type} with the letter {field.Letter}, which raises struct.error,")
          });
        }

        return (this.Effect(this.Helpers.Pack(format, packed, error)), PyType.Bytes);
      default:
        throw this.Unsupported(call, $"struct.{name} with {arguments.Count} arguments");
    }
  }

}
