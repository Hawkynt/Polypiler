using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VBScript;

public static partial class VBScriptEmitter {

  private sealed partial class Writer {

    private static int ElemKindCode(IrType type) => type.Integral.Kind switch {
      IrTypeKind.I8 => 1,
      IrTypeKind.U8 => 2,
      IrTypeKind.I16 => 3,
      IrTypeKind.U16 => 4,
      IrTypeKind.I32 => 5,
      IrTypeKind.U32 => 6,
      IrTypeKind.I64 => 7,
      IrTypeKind.U64 => 8,
      IrTypeKind.F32 => 9,
      _ => 10
    };

    private static int ListEqualityMode(IrCollectionOp op, IrType elementType) {
      if (op == IrCollectionOp.ListIndexOfStrict)
        return elementType.IsStruct ? 1 : 0;
      if (op == IrCollectionOp.ListIncludesSameValueZero)
        return elementType.IsStruct ? 1 : 0;
      return elementType.IsStruct || (elementType.IsObject && elementType.ClassType is { IsRecord: true }) ? 1 : 0;
    }

    private void EmitVoidCollection(IrCollection col, StringBuilder text, int indent) {
      this.Use(VBScriptHelpers.CollectionSupport);
      var pad = Indent(indent);
      var colExpr = this.EmitExpr(col.Arguments[0], text, indent);
      var colTmp = this.NewTemp("ply_col");
      text.Append(pad).Append("Set ").Append(colTmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(colExpr).Append(")\n");

      switch (col.Op) {
        case IrCollectionOp.ListAdd: {
          var v = this.CopyIfStruct(col.Arguments[1].Type, this.EmitExpr(col.Arguments[1], text, indent), text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".AddItem(").Append(v).Append(")\n");
          break;
        }
        case IrCollectionOp.ListSet: {
          var idx = this.EmitExpr(col.Arguments[1], text, indent);
          var v = this.CopyIfStruct(col.Arguments[2].Type, this.EmitExpr(col.Arguments[2], text, indent), text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".SetAt(").Append(idx).Append(", ").Append(v).Append(")\n");
          break;
        }
        case IrCollectionOp.ListInsert: {
          var idx = this.EmitExpr(col.Arguments[1], text, indent);
          var v = this.CopyIfStruct(col.Arguments[2].Type, this.EmitExpr(col.Arguments[2], text, indent), text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".InsertAt(").Append(idx).Append(", ").Append(v).Append(")\n");
          break;
        }
        case IrCollectionOp.ListRemoveAt: {
          var idx = this.EmitExpr(col.Arguments[1], text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".RemoveAt(").Append(idx).Append(")\n");
          break;
        }
        case IrCollectionOp.ListClear or IrCollectionOp.MapClear or IrCollectionOp.SetClear:
          text.Append(pad).Append("Call ").Append(colTmp).Append(".ClearAll()\n");
          break;
        case IrCollectionOp.MapPut: {
          var k = this.EmitExpr(col.Arguments[1], text, indent);
          var v = this.CopyIfStruct(col.Arguments[2].Type, this.EmitExpr(col.Arguments[2], text, indent), text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".PutEntry(").Append(k).Append(", ").Append(v).Append(")\n");
          break;
        }
        default: {
          var k = this.EmitExpr(col.Arguments[1], text, indent);
          var v = this.CopyIfStruct(col.Arguments[2].Type, this.EmitExpr(col.Arguments[2], text, indent), text, indent);
          text.Append(pad).Append("Call ").Append(colTmp).Append(".AddEntry(").Append(k).Append(", ").Append(v).Append(")\n");
          break;
        }
      }
    }

    private string EmitCollection(IrCollection col, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.CollectionSupport);
      var pad = Indent(indent);
      switch (col.Op) {
        case IrCollectionOp.ListNew:
          return "ply_new_list()";
        case IrCollectionOp.ListFromArray: {
          var arr = this.EmitExpr(col.Arguments[0], pre, indent);
          var isStruct = col.Type.Element!.IsStruct ? "True" : "False";
          return $"ply_list_from_arr({arr}, {isStruct})";
        }
        case IrCollectionOp.ListOf: {
          var tmp = this.NewTemp("ply_lo");
          pre.Append(pad).Append("Set ").Append(tmp).Append(" = ply_new_list()\n");
          foreach (var arg in col.Arguments) {
            var v = this.CopyIfStruct(arg.Type, this.EmitExpr(arg, pre, indent), pre, indent);
            pre.Append(pad).Append("Call ").Append(tmp).Append(".AddItem(").Append(v).Append(")\n");
          }
          return tmp;
        }
        case IrCollectionOp.MapNew:
          return $"ply_new_map({(col.Type.Order == MapOrder.SlotReuse ? "1" : "0")})";
        case IrCollectionOp.SetNew:
          return $"ply_new_set({(col.Type.Order == MapOrder.SlotReuse ? "1" : "0")})";
      }

      var colType = col.Arguments[0].Type;
      var colExpr = this.EmitExpr(col.Arguments[0], pre, indent);
      var colTmp = this.NewTemp("ply_col");
      pre.Append(pad).Append("Set ").Append(colTmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(colExpr).Append(")\n");

      switch (col.Op) {
        case IrCollectionOp.ListCount or IrCollectionOp.MapCount or IrCollectionOp.SetCount:
          return $"{colTmp}.Count";
        case IrCollectionOp.ListGet: {
          var idx = this.EmitExpr(col.Arguments[1], pre, indent);
          return this.CopyIfStruct(col.Type, $"{colTmp}.GetAt({idx})", pre, indent);
        }
        case IrCollectionOp.ListRemoveLast: {
          var isObj = NeedsSet(col.Type) ? "True" : "False";
          return $"{colTmp}.RemoveLast({isObj})";
        }
        case IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero: {
          var v = this.EmitExpr(col.Arguments[1], pre, indent);
          var eqMode = ListEqualityMode(col.Op, colType.Element!);
          return $"{colTmp}.ContainsItem({v}, {eqMode.ToString(CultureInfo.InvariantCulture)})";
        }
        case IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict: {
          var v = this.EmitExpr(col.Arguments[1], pre, indent);
          var eqMode = ListEqualityMode(col.Op, colType.Element!);
          return $"{colTmp}.IndexOfItem({v}, {eqMode.ToString(CultureInfo.InvariantCulture)})";
        }
        case IrCollectionOp.ListCopyToArray: {
          var isStruct = colType.Element!.IsStruct ? "True" : "False";
          return $"{colTmp}.ToArr({isStruct})";
        }
        case IrCollectionOp.MapGet: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return this.CopyIfStruct(col.Type, $"{colTmp}.GetEntry({k})", pre, indent);
        }
        case IrCollectionOp.MapTryGet: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          var isObj = NeedsSet(col.Type) ? "True" : "False";
          var res = $"{colTmp}.TryGetEntry({k}, {isObj})";
          if (col.Type.IsOptional && col.Type.Underlying!.IsStruct) {
            var tmp = this.NewTemp("ply_tg");
            pre.Append(pad).Append("Set ").Append(tmp).Append(" = ").Append(res).Append('\n');
            pre.Append(pad).Append("If Not (").Append(tmp).Append(" Is Nothing) Then Set ").Append(tmp).Append(" = ").Append(tmp).Append(".ply_clone()\n");
            return tmp;
          }
          return res;
        }
        case IrCollectionOp.MapContainsKey: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return $"{colTmp}.HasKey({k})";
        }
        case IrCollectionOp.MapTryAdd: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          var v = this.CopyIfStruct(col.Arguments[2].Type, this.EmitExpr(col.Arguments[2], pre, indent), pre, indent);
          return $"{colTmp}.TryAddEntry({k}, {v})";
        }
        case IrCollectionOp.MapRemove: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return $"{colTmp}.RemoveKey({k})";
        }
        case IrCollectionOp.SetAdd: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return $"{colTmp}.AddVal({k})";
        }
        case IrCollectionOp.SetRemove: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return $"{colTmp}.RemoveVal({k})";
        }
        default: {
          var k = this.EmitExpr(col.Arguments[1], pre, indent);
          return $"{colTmp}.HasItem({k})";
        }
      }
    }

    private void EmitForEachCollection(IrForEachCollection loop, StringBuilder text, int indent) {
      this.Use(VBScriptHelpers.CollectionSupport);
      var pad = Indent(indent);
      var colExpr = this.EmitExpr(loop.Collection, text, indent);
      var colTmp = this.NewTemp("ply_fc");
      var idxTmp = this.NewTemp("ply_fi");
      var isChecked = loop.Mode == EnumerationMode.Checked;
      var verTmp = isChecked ? this.NewTemp("ply_fv") : null;
      var hasContinue = ContainsDirectJump(loop.Body, wantBreak: false);
      var hasBreak = ContainsDirectJump(loop.Body, wantBreak: true);

      text.Append(pad).Append("Set ").Append(colTmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(colExpr).Append(")\n");
      if (verTmp is not null)
        text.Append(pad).Append(verTmp).Append(" = ").Append(colTmp).Append(".Version\n");
      text.Append(pad).Append(idxTmp).Append(" = 0\n");
      text.Append(pad).Append("Do\n");

      var innerPad = Indent(indent + 1);
      if (verTmp is not null)
        text.Append(innerPad).Append("If ").Append(colTmp).Append(".Version <> ").Append(verTmp)
          .Append(" Then Err.Raise vbObjectError + 1005, \"InvalidOperationException\", \"Collection was modified.\"\n");

      if (loop.Collection.Type.IsList) {
        text.Append(innerPad).Append("If ").Append(idxTmp).Append(" >= ").Append(colTmp).Append(".Count Then Exit Do\n");
        var val = this.CopyIfStruct(loop.Element!.Type, $"{colTmp}.GetAt({idxTmp})", text, indent + 1);
        this.EmitLocalInit(loop.Element, val, text, indent + 1);
      } else {
        text.Append(innerPad).Append("Do While ").Append(idxTmp).Append(" < ").Append(colTmp).Append(".SlotCount\n");
        text.Append(Indent(indent + 2)).Append("If ").Append(colTmp).Append(".SlotUsed(").Append(idxTmp).Append(") Then Exit Do\n");
        text.Append(Indent(indent + 2)).Append(idxTmp).Append(" = ").Append(idxTmp).Append(" + 1\n");
        text.Append(innerPad).Append("Loop\n");
        text.Append(innerPad).Append("If ").Append(idxTmp).Append(" >= ").Append(colTmp).Append(".SlotCount Then Exit Do\n");
        if (loop.Element is { } keyLocal)
          this.EmitLocalInit(keyLocal, $"{colTmp}.KeyAt({idxTmp})", text, indent + 1);
        if (loop.Value is { } valLocal) {
          var val = this.CopyIfStruct(valLocal.Type, $"{colTmp}.ValAt({idxTmp})", text, indent + 1);
          this.EmitLocalInit(valLocal, val, text, indent + 1);
        }
      }

      this.EmitLoopBody(loop.Body, null, hasBreak, hasContinue, text, indent + 1);
      text.Append(innerPad).Append(idxTmp).Append(" = ").Append(idxTmp).Append(" + 1\n");
      text.Append(pad).Append("Loop\n");
    }

    private string EmitViewIntrinsic(IrIntrinsic intrinsic, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.SpanSupport);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ViewOf: {
          var srcArg = intrinsic.Arguments[0];
          var start = intrinsic.Arguments.Count > 1 ? this.EmitExpr(intrinsic.Arguments[1], pre, indent) : "0";
          var count = intrinsic.Arguments.Count > 2 ? this.EmitExpr(intrinsic.Arguments[2], pre, indent) : "-1";
          if (srcArg.Type.IsView || srcArg.Type.IsMemory) {
            var sp = this.EmitExpr(srcArg, pre, indent);
            return intrinsic.Arguments.Count == 1 ? sp : $"{sp}.Slice({start}, {count})";
          }
          if (srcArg is IrLocalRef boxArr && this._arrayBoxedLocals.Contains(boxArr.Local))
            return $"ply_span_from_buf({this.LocalName(boxArr.Local)}, {start}, {count})";
          var arr = this.EmitExpr(srcArg, pre, indent);
          var elem = srcArg.Type.Element!;
          return $"ply_span_from_arr({arr}, {ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)}, {start}, {count})";
        }
        case IrIntrinsicOp.Slice: {
          var sp = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var start = this.EmitExpr(intrinsic.Arguments[1], pre, indent);
          var count = intrinsic.Arguments.Count > 2 ? this.EmitExpr(intrinsic.Arguments[2], pre, indent) : "-1";
          return $"{sp}.Slice({start}, {count})";
        }
        case IrIntrinsicOp.ViewLength: {
          var sp = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          return $"{sp}.Length";
        }
        case IrIntrinsicOp.StackAlloc: {
          var n = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var elem = intrinsic.Type.Element!;
          return $"ply_span_stackalloc({n}, {ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)})";
        }
        case IrIntrinsicOp.ViewTryCopy: {
          var src = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var dst = this.EmitExpr(intrinsic.Arguments[1], pre, indent);
          return $"{src}.TryCopyTo({dst})";
        }
        case IrIntrinsicOp.ViewEqual: {
          var left = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var right = this.EmitExpr(intrinsic.Arguments[1], pre, indent);
          return $"{left}.SeqEqual({right})";
        }
        case IrIntrinsicOp.ViewToArray: {
          var sp = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          return $"{sp}.ToArr()";
        }
        default: {
          var sp = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var elem = intrinsic.Type.Element!;
          return $"{sp}.Reinterpret({ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)})";
        }
      }
    }

    private string EmitPointerIntrinsic(IrIntrinsic intrinsic, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.PtrSupport);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.AddressOf: {
          var target = ((IrLocalRef)intrinsic.Arguments[0]).Local;
          var size = target.Type.Size.ToString(CultureInfo.InvariantCulture);
          return $"ply_ptr_from_buf({this.LocalName(target)}, 0, {size}, 0)";
        }
        case IrIntrinsicOp.StackAlloc: {
          var n = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var elem = intrinsic.Type.Element!;
          return $"ply_ptr_stackalloc({n}, {ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)})";
        }
        case IrIntrinsicOp.Pin: {
          var srcArg = intrinsic.Arguments[0];
          var hasIdx = intrinsic.Arguments.Count > 1 ? "True" : "False";
          var idx = intrinsic.Arguments.Count > 1 ? this.EmitExpr(intrinsic.Arguments[1], pre, indent) : "0";
          if (srcArg.Type.IsView) {
            var sp = this.EmitExpr(srcArg, pre, indent);
            return $"ply_ptr_pin_span({sp}, {hasIdx}, {idx})";
          }
          if (srcArg is IrLocalRef boxArr && this._arrayBoxedLocals.Contains(boxArr.Local))
            return $"ply_ptr_pin_buf({this.LocalName(boxArr.Local)}, {hasIdx}, {idx})";
          var arr = this.EmitExpr(srcArg, pre, indent);
          var elem = srcArg.Type.Element!;
          return $"ply_ptr_pin_arr({arr}, {ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)}, {hasIdx}, {idx})";
        }
        case IrIntrinsicOp.PointerAdd: {
          var ptr = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var delta = this.EmitExpr(intrinsic.Arguments[1], pre, indent);
          var size = intrinsic.Type.Element!.Size.ToString(CultureInfo.InvariantCulture);
          return $"ply_ptr_add({ptr}, {delta}, {size})";
        }
        case IrIntrinsicOp.PointerCast:
          return this.EmitExpr(intrinsic.Arguments[0], pre, indent);
        case IrIntrinsicOp.PointerLoad: {
          var ptr = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var elem = intrinsic.Type;
          return $"ply_ptr_load({ptr}, {ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)})";
        }
        case IrIntrinsicOp.PointerDifference: {
          var left = this.EmitExpr(intrinsic.Arguments[0], pre, indent);
          var right = this.EmitExpr(intrinsic.Arguments[1], pre, indent);
          var size = intrinsic.Arguments[0].Type.Element!.Size.ToString(CultureInfo.InvariantCulture);
          return $"ply_ptr_diff({left}, {right}, {size})";
        }
        case IrIntrinsicOp.PointerEqual:
          return $"ply_ptr_eq({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)})";
        case IrIntrinsicOp.PointerNotEqual:
          return $"(Not ply_ptr_eq({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)}))";
        case IrIntrinsicOp.PointerLess:
          return $"(ply_ptr_cmp({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)}) < 0)";
        case IrIntrinsicOp.PointerLessOrEqual:
          return $"(ply_ptr_cmp({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)}) <= 0)";
        case IrIntrinsicOp.PointerGreater:
          return $"(ply_ptr_cmp({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)}) > 0)";
        default:
          return $"(ply_ptr_cmp({this.EmitExpr(intrinsic.Arguments[0], pre, indent)}, {this.EmitExpr(intrinsic.Arguments[1], pre, indent)}) >= 0)";
      }
    }

    private string EmitBitPermutation(IrIntrinsic intrinsic, string[] args, bool isDeposit) {
      var type = intrinsic.Type.Integral;
      if (type.IsLong) {
        var helper = this.Use(isDeposit ? VBScriptHelpers.Pdep64 : VBScriptHelpers.Pext64);
        return this.WrapInteger(type, $"{helper}({args[0]}, {args[1]})");
      }
      var fn = this.Use(isDeposit ? VBScriptHelpers.Pdep32 : VBScriptHelpers.Pext32);
      return this.WrapInteger(type, $"{fn}({args[0]}, {args[1]}, {type.Bits.ToString(CultureInfo.InvariantCulture)})");
    }

  }

}
