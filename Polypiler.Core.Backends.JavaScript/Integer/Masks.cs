using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Masks and the extensions of a field at the bottom. A mask has the width of the type of the
    /// node; the forms of the lowest set bit are the expressions of Warren's §2-1 on the value read
    /// once by a helper, cut back to the type.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> MaskForms => [
      (IrIntrinsicOp.LowMask, (writer, intrinsic) => intrinsic.Type.IsLong
        ? writer.Typed(intrinsic.Type, writer.Call("$lowmask64", intrinsic).Item1)
        : Cut(intrinsic.Type, writer.Call("$lowmask", intrinsic, Bits(intrinsic.Type)).Item1)),
      (IrIntrinsicOp.IsolateLowestSet, (writer, intrinsic) => writer.Lowest(intrinsic, "$blsi")),
      (IrIntrinsicOp.ClearLowestSet, (writer, intrinsic) => writer.Lowest(intrinsic, "$blsr")),
      (IrIntrinsicOp.MaskToLowestSet, (writer, intrinsic) => writer.Lowest(intrinsic, "$blsmsk")),
      (IrIntrinsicOp.SignExtend, (writer, intrinsic) => writer.Extended(intrinsic, "$sext")),
      (IrIntrinsicOp.ZeroExtend, (writer, intrinsic) => writer.Extended(intrinsic, "$zext"))
    ];

    private (JsExpression, bool) Lowest(IrIntrinsic intrinsic, string helper)
      => this.Typed(intrinsic.Type, this.Call(intrinsic.Type.IsLong ? helper + "64" : helper, intrinsic).Item1);

    private (JsExpression, bool) Extended(IrIntrinsic intrinsic, string helper)
      => intrinsic.Type.IsLong
        ? this.Typed(intrinsic.Type, this.Call(helper + "64", intrinsic).Item1)
        : Cut(intrinsic.Type, this.Call(helper, intrinsic, Bits(intrinsic.Type)).Item1);

  }

}
