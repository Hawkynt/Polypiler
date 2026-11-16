using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>String.fromCharCode.apply(null, a)</c> and <c>String.fromCharCode(...a)</c> (#1400): the string of
/// one code unit per element of an array, a list or a typed array, each <c>ToUint16</c> of the element
/// (ECMA-262 §22.1.2.1). The units are made by a function of the module per type of the codes, which
/// fills a <c>u16[]</c> and turns it into a string; codes that already are <c>u16</c> need none. Any
/// other function's <c>apply</c> stays refused as a call of it.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The functions that turn codes into a string, by the type of the codes.</summary>
  private readonly Dictionary<IrType, string> _charCodes = [];

  /// <summary><c>String.fromCharCode.apply(receiver, codes)</c>; the receiver has to be <c>null</c> or <c>undefined</c>.</summary>
  private IrExpr FromCharCodeApply(CallExpression call) {
    if (call.Arguments.Count != 2)
      throw this.Unsupported(call, $"String.fromCharCode.apply with {call.Arguments.Count} arguments");

    var receiver = call.Arguments[0];
    if (!(receiver is NullLiteral || (receiver is Identifier identifier && types.Names.IsAbsentLiteral(identifier))))
      throw this.Unsupported(receiver, "String.fromCharCode.apply with a receiver other than null or undefined");

    return this.CharCodes(call.Arguments[1]);
  }

  /// <summary>The string of the code units of the elements of an array, a list or a typed array.</summary>
  private IrExpr CharCodes(Node node) {
    var codes = this.Expression(node);
    var type = codes.Type;
    if (!(type.IsArray || type.IsList) || type.Element is not { IsNumeric: true } element)
      throw this.Unsupported(node, $"String.fromCharCode of the elements of {types.Of(node).Describe()}");

    if (type.IsArray && element == IrType.U16)
      return Str(IrIntrinsicOp.StrFromCodeUnits, _str, codes);

    return new IrCall(this.CharCodesFunction(type), _str, [codes]);
  }

  /// <summary>The function of the module that makes the string of codes of a type: each element through <c>ToUint16</c> into a <c>u16[]</c>.</summary>
  private string CharCodesFunction(IrType type) {
    if (this._charCodes.TryGetValue(type, out var known))
      return known;

    var name = $"$fromCharCodes{this._charCodes.Count}";
    this._charCodes[type] = name;
    var units = IrType.Array(IrType.U16);
    var (codes, length, unitArray, at) = (new IrLocal("a", type), new IrLocal("n", IrType.I32), new IrLocal("u", units), new IrLocal("i", IrType.I32));
    IrExpr Ref(IrLocal local) => new IrLocalRef(local);
    IrExpr element = type.IsArray ? new IrArrayLoad(Ref(codes), Ref(at)) : new IrCollection(IrCollectionOp.ListGet, type.Element!, [Ref(codes), Ref(at)]);
    // ToUint16 is ToUint32 cut to its low 16 bits; an integer is cut directly.
    var unit = element.Type.IsInteger
      ? Convert(element, IrType.U16)
      : Convert(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [Convert(element, IrType.F64)]), IrType.U16);
    this.Callbacks.Add(new IrFunction(name, false, _str, [codes], [length, unitArray, at], new IrBlock([
      new IrDeclare(length, type.IsArray ? new IrArrayLength(Ref(codes)) : new IrCollection(IrCollectionOp.ListCount, IrType.I32, [Ref(codes)])),
      new IrDeclare(unitArray, new IrNewArray(units, Ref(length))),
      new IrDeclare(at, I32(0)),
      new IrWhile(new IrBinary(IrBinaryOp.Less, Ref(at), Ref(length)), new IrBlock([
        new IrStore(Ref(unitArray), Ref(at), unit),
        new IrAssign(at, new IrBinary(IrBinaryOp.Add, Ref(at), I32(1)))
      ])),
      new IrReturn(Str(IrIntrinsicOp.StrFromCodeUnits, _str, Ref(unitArray)))
    ])));
    return name;
  }

}
