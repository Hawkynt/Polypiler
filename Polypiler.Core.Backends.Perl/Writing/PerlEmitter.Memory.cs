using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer {

    private bool IsStringBytes(IrType type)
      => type.IsArray && type.Element == IrType.U8 && this._profile.ByteStorage == PerlByteStorage.String;

    private string EmitNewArray(IrNewArray array) {
      var len = this.EmitExpr(array.Length);
      var elem = array.Type.Element!;
      if (this.IsStringBytes(array.Type))
        return $"do {{ my $_n = {len}; die \"Overflow: negative array length\\n\" if $_n < 0; my $_b = \"\\0\" x $_n; \\$_b }}";
      if (elem.IsStruct)
        return $"do {{ my $_n = {len}; die \"Overflow: negative array length\\n\" if $_n < 0; [ map {{ {this.DefaultValue(elem)} }} 1 .. $_n ] }}";
      return $"do {{ my $_n = {len}; die \"Overflow: negative array length\\n\" if $_n < 0; [ ({this.DefaultValue(elem)}) x $_n ] }}";
    }

    private string EmitArrayLength(IrArrayLength length) {
      var arr = this.EmitExpr(length.Array);
      return this.IsStringBytes(length.Array.Type)
        ? $"length(${{{arr}}})"
        : $"scalar(@{{{arr}}})";
    }

    private string EmitArrayLoad(IrArrayLoad load) {
      var arr = this.EmitExpr(load.Array);
      var idx = this.EmitExpr(load.Index);
      if (load.Array.Type.IsView)
        return $"do {{ my ($_v, $_i) = ({arr}, {idx}); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= $_v->[2]; $_v->[0]->[$_v->[1] + $_i] }}";
      if (this.IsStringBytes(load.Array.Type))
        return $"do {{ my ($_b, $_i) = ({arr}, {idx}); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= length($$_b); vec($$_b, $_i, 8) }}";
      return $"do {{ my ($_a, $_i) = ({arr}, {idx}); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i > $#$_a; {this.CopyIfStruct(load.Type, "$_a->[$_i]")} }}";
    }

    private void EmitStoreStatement(IrStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var arr = this.EmitExpr(store.Array);
      var idx = this.EmitExpr(store.Index);
      var val = this.CopyIfStruct(store.Value.Type, this.EmitExpr(store.Value));
      if (store.Array.Type.IsView) {
        text.Append(pad).Append("do { my ($_v, $_i, $_x) = (").Append(arr).Append(", ").Append(idx).Append(", ").Append(val)
          .Append("); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= $_v->[2]; $_v->[0]->[$_v->[1] + $_i] = $_x; };\n");
        return;
      }
      if (this.IsStringBytes(store.Array.Type)) {
        text.Append(pad).Append("do { my ($_b, $_i, $_x) = (").Append(arr).Append(", ").Append(idx).Append(", ").Append(val)
          .Append("); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= length($$_b); vec($$_b, $_i, 8) = $_x; };\n");
        return;
      }
      text.Append(pad).Append("do { my ($_a, $_i, $_x) = (").Append(arr).Append(", ").Append(idx).Append(", ").Append(val)
        .Append("); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i > $#$_a; $_a->[$_i] = $_x; };\n");
    }

    private string EmitIntrinsic(IrIntrinsic intrinsic) {
      var args = intrinsic.Arguments.Select(this.EmitExpr).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf:
          if (this.IsStringBytes(intrinsic.Type))
            return $"do {{ my $_b = pack('C*', {string.Join(", ", args)}); \\$_b }}";
          return $"[{string.Join(", ", args)}]";

        case IrIntrinsicOp.ArrayCopy:
          if (this.IsStringBytes(intrinsic.Arguments[0].Type))
            return $"do {{ my ($_s, $_si, $_d, $_di, $_n) = ({args[0]}, {args[1]}, {args[2]}, {args[3]}, {args[4]}); die \"IndexOutOfRange: invalid copy range\\n\" if $_n < 0 || $_si < 0 || $_di < 0 || $_si + $_n > length($$_s) || $_di + $_n > length($$_d); substr($$_d, $_di, $_n, substr($$_s, $_si, $_n)) if $_n > 0; }}";
          this.UseHelper(PerlHelpers.ArrayCopy);
          return $"{PerlHelpers.ArrayCopy}({string.Join(", ", args)})";

        case IrIntrinsicOp.ArrayFill:
          if (this.IsStringBytes(intrinsic.Arguments[0].Type))
            return $"do {{ my ($_b, $_v) = ({args[0]}, {args[1]}); $$_b = chr($_v & 0xFF) x length($$_b); }}";
          return $"do {{ my ($_a, $_v) = ({args[0]}, {args[1]}); @$_a[0 .. $#$_a] = ($_v) x scalar(@$_a) if @$_a; }}";

        case IrIntrinsicOp.ArrayClear:
          if (this.IsStringBytes(intrinsic.Arguments[0].Type))
            return $"do {{ my $_b = {args[0]}; $$_b = \"\\0\" x length($$_b); }}";
          return $"do {{ my $_a = {args[0]}; @$_a[0 .. $#$_a] = ({this.DefaultValue(intrinsic.Arguments[0].Type.Element!)}) x scalar(@$_a) if @$_a; }}";

        case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig:
          return this.EmitEndianLoad(intrinsic.Op == IrIntrinsicOp.LoadLittle, intrinsic.Type, intrinsic.Arguments[0].Type, args[0], args[1]);

        case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig:
          return this.EmitEndianStore(intrinsic.Op == IrIntrinsicOp.StoreLittle, intrinsic.Arguments[2].Type, intrinsic.Arguments[0].Type, args[0], args[1], args[2]);

        case IrIntrinsicOp.ViewOf:
          if (intrinsic.Arguments.Count == 1)
            return $"do {{ my $_s = {args[0]}; [$_s, 0, scalar(@$_s)] }}";
          if (intrinsic.Arguments.Count == 2) {
            this.UseHelper(PerlHelpers.ViewSlice);
            return $"do {{ my $_s = {args[0]}; {PerlHelpers.ViewSlice}([$_s, 0, scalar(@$_s)], {args[1]}, undef) }}";
          }
          this.UseHelper(PerlHelpers.ViewSlice);
          return $"do {{ my $_s = {args[0]}; {PerlHelpers.ViewSlice}([$_s, 0, scalar(@$_s)], {args[1]}, {args[2]}) }}";

        case IrIntrinsicOp.Slice:
          this.UseHelper(PerlHelpers.ViewSlice);
          return intrinsic.Arguments.Count == 2
            ? $"{PerlHelpers.ViewSlice}({args[0]}, {args[1]}, undef)"
            : $"{PerlHelpers.ViewSlice}({args[0]}, {args[1]}, {args[2]})";

        case IrIntrinsicOp.ViewLength:
          return $"({args[0]})->[2]";

        case IrIntrinsicOp.StackAlloc:
          return $"do {{ my $_n = {args[0]}; die \"Overflow: negative stackalloc\\n\" if $_n < 0; [[({this.DefaultValue(intrinsic.Type.Element!)}) x $_n], 0, $_n] }}";

        case IrIntrinsicOp.ViewCopy:
          this.UseHelper(PerlHelpers.ViewCopy);
          return $"{PerlHelpers.ViewCopy}({args[0]}, {args[1]}, 0)";

        case IrIntrinsicOp.ViewTryCopy:
          this.UseHelper(PerlHelpers.ViewCopy);
          return $"{PerlHelpers.ViewCopy}({args[0]}, {args[1]}, 1)";

        case IrIntrinsicOp.ViewFill:
          return $"do {{ my ($_v, $_x) = ({args[0]}, {args[1]}); my ($_b, $_o, $_l) = @$_v; @$_b[$_o .. $_o + $_l - 1] = ($_x) x $_l if $_l > 0; }}";

        case IrIntrinsicOp.ViewEqual:
          this.UseHelper(PerlHelpers.ViewEqual);
          return $"{PerlHelpers.ViewEqual}({args[0]}, {args[1]})";

        case IrIntrinsicOp.ViewToArray:
          return $"do {{ my ($_b, $_o, $_l) = @{{{args[0]}}}; $_l > 0 ? [@$_b[$_o .. $_o + $_l - 1]] : [] }}";

        case IrIntrinsicOp.Reinterpret:
          this.Problems.Add(Diagnostic.Create(OverlayUnsupported, null,
            intrinsic.Arguments[0].Type.Element?.ToString() ?? intrinsic.Arguments[0].Type.ToString(),
            intrinsic.Type.Element?.ToString() ?? intrinsic.Type.ToString()) with {
              Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
            });
          return "0";

        case IrIntrinsicOp.Pin:
          if (intrinsic.Arguments.Count == 1)
            return $"do {{ my $_s = {args[0]}; defined($_s) && @$_s ? [$_s, 0] : undef }}";
          return $"do {{ my ($_s, $_i) = ({args[0]}, {args[1]}); die \"NullReference: null array\\n\" unless defined $_s; die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= scalar(@$_s); [$_s, $_i] }}";

        case IrIntrinsicOp.AddressOf:
          return $"[[{this.LocalName(((IrLocalRef)intrinsic.Arguments[0]).Local)}], 0]";

        case IrIntrinsicOp.PointerAdd:
          return $"do {{ my ($_p, $_n) = ({args[0]}, {args[1]}); defined($_p) ? [$_p->[0], $_p->[1] + $_n] : undef }}";

        case IrIntrinsicOp.PointerDifference:
          return $"do {{ my ($_p, $_q) = ({args[0]}, {args[1]}); die \"InvalidOperation: pointers into different regions\\n\" unless defined($_p) && defined($_q) && $_p->[0] == $_q->[0]; $_p->[1] - $_q->[1] }}";

        case IrIntrinsicOp.PointerEqual:
          return $"do {{ my ($_p, $_q) = ({args[0]}, {args[1]}); (!defined($_p) && !defined($_q)) || (defined($_p) && defined($_q) && $_p->[0] == $_q->[0] && $_p->[1] == $_q->[1]) ? 1 : 0 }}";

        case IrIntrinsicOp.PointerNotEqual:
          return $"do {{ my ($_p, $_q) = ({args[0]}, {args[1]}); (!defined($_p) && !defined($_q)) || (defined($_p) && defined($_q) && $_p->[0] == $_q->[0] && $_p->[1] == $_q->[1]) ? 0 : 1 }}";

        case IrIntrinsicOp.PointerLess or IrIntrinsicOp.PointerLessOrEqual or IrIntrinsicOp.PointerGreater or IrIntrinsicOp.PointerGreaterOrEqual: {
          var cmp = intrinsic.Op switch {
            IrIntrinsicOp.PointerLess => "<",
            IrIntrinsicOp.PointerLessOrEqual => "<=",
            IrIntrinsicOp.PointerGreater => ">",
            _ => ">="
          };
          return $"do {{ my ($_p, $_q) = ({args[0]}, {args[1]}); die \"InvalidOperation: pointers into different regions\\n\" unless defined($_p) && defined($_q) && $_p->[0] == $_q->[0]; ($_p->[1] {cmp} $_q->[1]) ? 1 : 0 }}";
        }

        case IrIntrinsicOp.PointerLoad:
          return $"do {{ my $_p = {args[0]}; die \"NullReference: null pointer\\n\" unless defined $_p; my ($_b, $_o) = @$_p; die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o > $#$_b; $_b->[$_o] }}";

        case IrIntrinsicOp.PointerStore:
          return $"do {{ my ($_p, $_v) = ({args[0]}, {args[1]}); die \"NullReference: null pointer\\n\" unless defined $_p; my ($_b, $_o) = @$_p; die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o > $#$_b; $_b->[$_o] = $_v; }}";

        case IrIntrinsicOp.PointerCast:
          if (intrinsic.Arguments[0].Type.Element != intrinsic.Type.Element && intrinsic.Type.Element != IrType.Void && intrinsic.Arguments[0].Type.Element != IrType.Void) {
            this.Problems.Add(Diagnostic.Create(PointerUnsupported, null, $"{intrinsic.Arguments[0].Type} -> {intrinsic.Type}"));
            return "0";
          }
          return args[0];

        case >= IrIntrinsicOp.StrLength and <= IrIntrinsicOp.StrNextCodePoint:
          return this.EmitStringIntrinsic(intrinsic, args);

        case >= IrIntrinsicOp.EnumToString and <= IrIntrinsicOp.EnumValues:
          return this.EmitEnumIntrinsic(intrinsic, args);

        default:
          return this.EmitScalarIntrinsic(intrinsic);
      }
    }

    private string EmitEndianLoad(bool little, IrType valueType, IrType bufferType, string buf, string offset) {
      var width = valueType.Size;
      var letter = PackLetter(valueType, little);
      if (this.IsStringBytes(bufferType)) {
        return $"do {{ my ($_b, $_o) = ({buf}, {offset}); die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o + {width} > length($$_b); unpack('{letter}', substr($$_b, $_o, {width})) }}";
      }

      if (valueType is { IsInteger: true, IsLong: false }) {
        // Explicit shift-or for 16/32-bit integers in array storage (#432).
        var terms = Enumerable.Range(0, width).Select(k => {
          var byteIdx = little ? k : (width - 1 - k);
          var idxExpr = byteIdx == 0 ? "$_o" : $"$_o + {byteIdx}";
          return k == 0 ? $"$_b->[{idxExpr}]" : $"($_b->[{idxExpr}] << {k * 8})";
        });
        var expr = string.Join(" | ", terms);
        if (valueType.IsSigned)
          expr = Wrap(valueType, expr);
        return $"do {{ my ($_b, $_o) = ({buf}, {offset}); die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o + {width} > scalar(@$_b); {expr} }}";
      }

      return $"do {{ my ($_b, $_o) = ({buf}, {offset}); die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o + {width} > scalar(@$_b); unpack('{letter}', pack('C{width}', @{{$_b}}[$_o .. $_o + {width - 1}])) }}";
    }

    private string EmitEndianStore(bool little, IrType valueType, IrType bufferType, string buf, string offset, string value) {
      var width = valueType.Size;
      var letter = PackLetter(valueType, little);
      if (this.IsStringBytes(bufferType))
        return $"do {{ my ($_b, $_o, $_v) = ({buf}, {offset}, {value}); die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o + {width} > length($$_b); substr($$_b, $_o, {width}, pack('{letter}', $_v)); }}";

      return $"do {{ my ($_b, $_o, $_v) = ({buf}, {offset}, {value}); die \"IndexOutOfRange: offset $_o\\n\" if $_o < 0 || $_o + {width} > scalar(@$_b); @{{$_b}}[$_o .. $_o + {width - 1}] = unpack('C{width}', pack('{letter}', $_v)); }}";
    }

    private static string PackLetter(IrType type, bool little) => (type.Kind, little) switch {
      (IrTypeKind.U16, true) => "v",
      (IrTypeKind.U16, false) => "n",
      (IrTypeKind.I16, true) => "s<",
      (IrTypeKind.I16, false) => "s>",
      (IrTypeKind.U32, true) => "V",
      (IrTypeKind.U32, false) => "N",
      (IrTypeKind.I32, true) => "l<",
      (IrTypeKind.I32, false) => "l>",
      (IrTypeKind.U64, true) => "Q<",
      (IrTypeKind.U64, false) => "Q>",
      (IrTypeKind.I64, true) => "q<",
      (IrTypeKind.I64, false) => "q>",
      (IrTypeKind.F32, true) => "f<",
      (IrTypeKind.F32, false) => "f>",
      (IrTypeKind.F64, true) => "d<",
      _ => "d>"
    };

    private string EmitStringIntrinsic(IrIntrinsic intrinsic, string[] args) {
      switch (intrinsic.Op) {
        case IrIntrinsicOp.StrLength:
          return $"length({args[0]})";
        case IrIntrinsicOp.StrCodeUnitAt:
          return $"do {{ my ($_s, $_i) = ({args[0]}, {args[1]}); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i >= length($_s); ord(substr($_s, $_i, 1)) }}";
        case IrIntrinsicOp.StrConcat:
          return "(" + string.Join(" . ", args) + ")";
        case IrIntrinsicOp.StrFromCodeUnit:
          return $"chr({args[0]})";
        case IrIntrinsicOp.StrFromCodeUnits:
          return args.Length == 1
            ? $"join('', map {{ chr($_) }} @{{{args[0]}}})"
            : $"do {{ my ($_a, $_i, $_n) = ({args[0]}, {args[1]}, {args[2]}); die \"IndexOutOfRange: invalid range\\n\" if $_i < 0 || $_n < 0 || $_i + $_n > scalar(@$_a); $_n == 0 ? '' : join('', map {{ chr($_) }} @$_a[$_i .. $_i + $_n - 1]) }}";
        case IrIntrinsicOp.StrToCodeUnits:
          return $"[ map {{ ord($_) }} split(//, {args[0]}) ]";
        case IrIntrinsicOp.StrRepeat: {
          var part = intrinsic.Arguments[0].Type == IrType.String ? args[0] : $"chr({args[0]})";
          return $"do {{ my ($_s, $_n) = ({part}, {args[1]}); die \"Overflow: negative repeat\\n\" if $_n < 0; $_s x $_n }}";
        }
        case IrIntrinsicOp.StrSubstring:
          return args.Length == 2
            ? $"do {{ my ($_s, $_i) = ({args[0]}, {args[1]}); die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i > length($_s); substr($_s, $_i) }}"
            : $"do {{ my ($_s, $_i, $_n) = ({args[0]}, {args[1]}, {args[2]}); die \"IndexOutOfRange: invalid range\\n\" if $_i < 0 || $_n < 0 || $_i + $_n > length($_s); substr($_s, $_i, $_n) }}";
        case IrIntrinsicOp.StrEquals:
          return $"(({args[0]} eq {args[1]}) ? 1 : 0)";
        case IrIntrinsicOp.StrCompareOrdinal:
          return $"({args[0]} cmp {args[1]})";
        case IrIntrinsicOp.StrIndexOf: {
          var needle = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return args.Length == 2
            ? $"index({args[0]}, {needle})"
            : $"do {{ my ($_s, $_n, $_f) = ({args[0]}, {needle}, {args[2]}); die \"IndexOutOfRange: index $_f\\n\" if $_f < 0 || $_f > length($_s); index($_s, $_n, $_f) }}";
        }
        case IrIntrinsicOp.StrLastIndexOf: {
          var needle = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return args.Length == 2
            ? $"rindex({args[0]}, {needle})"
            : $"do {{ my ($_s, $_n, $_f) = ({args[0]}, {needle}, {args[2]}); die \"IndexOutOfRange: index $_f\\n\" if $_f < 0 || $_f > length($_s); rindex($_s, $_n, $_f) }}";
        }
        case IrIntrinsicOp.StrStartsWith: {
          var needle = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return $"do {{ my ($_s, $_n) = ({args[0]}, {needle}); (length($_s) >= length($_n) && substr($_s, 0, length($_n)) eq $_n) ? 1 : 0 }}";
        }
        case IrIntrinsicOp.StrEndsWith: {
          var needle = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return $"do {{ my ($_s, $_n) = ({args[0]}, {needle}); (length($_s) >= length($_n) && substr($_s, length($_s) - length($_n)) eq $_n) ? 1 : 0 }}";
        }
        case IrIntrinsicOp.StrContains: {
          var needle = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return $"(index({args[0]}, {needle}) >= 0 ? 1 : 0)";
        }
        case IrIntrinsicOp.StrReplaceAll: {
          var oldVal = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          var newVal = intrinsic.Arguments[2].Type == IrType.String ? args[2] : $"chr({args[2]})";
          return $"do {{ my ($_s, $_o, $_n) = ({args[0]}, {oldVal}, {newVal}); die \"InvalidOperation: empty needle\\n\" if length($_o) == 0; $_s =~ s/\\Q$_o\\E/$_n/g; $_s }}";
        }
        case IrIntrinsicOp.StrReplaceFirst:
          return $"do {{ my ($_s, $_o, $_n) = ({args[0]}, {args[1]}, {args[2]}); $_s =~ s/\\Q$_o\\E/$_n/; $_s }}";
        case IrIntrinsicOp.StrSplit: {
          var sep = intrinsic.Arguments[1].Type == IrType.String ? args[1] : $"chr({args[1]})";
          return $"do {{ my ($_s, $_p, $_l) = ({args[0]}, {sep}, {args[2]}); [ length($_p) == 0 ? split(//, $_s, $_l < 0 ? -1 : $_l) : split(/\\Q$_p\\E/, $_s, $_l < 0 ? -1 : $_l) ] }}";
        }
        case IrIntrinsicOp.StrJoin:
          return $"join({args[0]}, @{{{args[1]}}})";
        case IrIntrinsicOp.StrTrim:
          this.UseHelper(PerlHelpers.StrTrim);
          return $"{PerlHelpers.StrTrim}({args[0]}, 0)";
        case IrIntrinsicOp.StrTrimStart:
          this.UseHelper(PerlHelpers.StrTrim);
          return $"{PerlHelpers.StrTrim}({args[0]}, 1)";
        case IrIntrinsicOp.StrTrimEnd:
          this.UseHelper(PerlHelpers.StrTrim);
          return $"{PerlHelpers.StrTrim}({args[0]}, 2)";
        case IrIntrinsicOp.StrPadStart:
          this.UseHelper(PerlHelpers.StrPad);
          return $"{PerlHelpers.StrPad}({args[0]}, {args[1]}, {args[2]}, 1)";
        case IrIntrinsicOp.StrPadEnd:
          this.UseHelper(PerlHelpers.StrPad);
          return $"{PerlHelpers.StrPad}({args[0]}, {args[1]}, {args[2]}, 0)";
        default:
          return this.UnsupportedOp(intrinsic.Op.ToString());
      }
    }

  }

}
