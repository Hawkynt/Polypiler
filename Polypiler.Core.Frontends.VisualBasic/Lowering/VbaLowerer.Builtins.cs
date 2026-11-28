using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>
  /// The functions of the frontend's own that the built-in functions are lowered to, written in the
  /// subset itself: their floating-point arithmetic is the IR's, and they name the IR's operations
  /// through <c>PlyIr…</c>. A module carries those it calls.
  /// </summary>
  private const string HelperSource = """
    Private Function PlyCheckD(ByVal x As Double) As Double
        If x <> x Then Error 5
        If x - x <> 0 Then Error 6
        PlyCheckD = x
    End Function

    Private Function PlyCheckS(ByVal x As Single) As Single
        If x <> x Then Error 5
        If x - x <> 0 Then Error 6
        PlyCheckS = x
    End Function

    Private Function PlyDivD(ByVal a As Double, ByVal b As Double) As Double
        If b = 0 Then
            If a = 0 Or a <> a Then Error 6
            Error 11
        End If
        PlyDivD = PlyCheckD(a / b)
    End Function

    Private Function PlyDivS(ByVal a As Single, ByVal b As Single) As Single
        If b = 0 Then
            If a = 0 Or a <> a Then Error 6
            Error 11
        End If
        PlyDivS = PlyCheckS(a / b)
    End Function

    Private Function PlyPow(ByVal a As Double, ByVal b As Double) As Double
        PlyPow = PlyCheckD(PlyIrPow(a, b))
    End Function

    Private Function PlySqr(ByVal x As Double) As Double
        If x < 0 Then Error 5
        PlySqr = PlyIrSqrt(x)
    End Function

    Private Function PlyLog(ByVal x As Double) As Double
        If x <= 0 Then Error 5
        PlyLog = PlyIrLog(x)
    End Function

    Private Function PlyExp(ByVal x As Double) As Double
        PlyExp = PlyCheckD(PlyIrExp(x))
    End Function

    Private Function PlyLeft(ByVal s As String, ByVal n As Long) As String
        If n < 0 Then Error 5
        If n > Len(s) Then n = Len(s)
        PlyLeft = PlyIrSubstring(s, 0, n)
    End Function

    Private Function PlyRight(ByVal s As String, ByVal n As Long) As String
        If n < 0 Then Error 5
        If n > Len(s) Then n = Len(s)
        PlyRight = PlyIrSubstring(s, Len(s) - n, n)
    End Function

    Private Function PlyMid(ByVal s As String, ByVal start As Long, ByVal count As Long) As String
        If start <= 0 Or count < 0 Then Error 5
        If start > Len(s) Then
            PlyMid = ""
        Else
            If count > Len(s) - start + 1 Then count = Len(s) - start + 1
            PlyMid = PlyIrSubstring(s, start - 1, count)
        End If
    End Function

    Private Function PlyInStr(ByVal start As Long, ByVal s As String, ByVal find As String) As Long
        If start <= 0 Then Error 5
        If Len(s) = 0 Then
            PlyInStr = 0
        ElseIf Len(find) = 0 Then
            PlyInStr = start
        ElseIf start > Len(s) Then
            PlyInStr = 0
        Else
            PlyInStr = PlyIrIndexOf(s, find, start - 1) + 1
        End If
    End Function

    Private Function PlyAscW(ByVal s As String) As Long
        If Len(s) = 0 Then Error 5
        PlyAscW = PlyIrCodeUnit(s, 0)
    End Function

    Private Function PlyChr(ByVal n As Long) As String
        If n < 0 Or n > 255 Then Error 5
        PlyChr = PlyIrFromCodeUnit(n)
    End Function

    Private Function PlyChrW(ByVal n As Long) As String
        If n < -32768 Or n > 65535 Then Error 5
        PlyChrW = PlyIrFromCodeUnit(n)
    End Function

    Private Function PlyRepeat(ByVal n As Long, ByVal c As String) As String
        If n < 0 Or Len(c) = 0 Then Error 5
        PlyRepeat = PlyIrRepeat(PlyIrCodeUnit(c, 0), n)
    End Function
    """;

  private readonly Dictionary<string, Signature> _helperSignatures = new(StringComparer.Ordinal);
  private Dictionary<string, VbaProcedure>? _helperProcedures;

  /// <summary>Reads the helpers once, so that their signatures are known to the calls that name them.</summary>
  private void ReadHelpers() {
    if (this._helperProcedures is not null)
      return;

    var parsed = VbaParser.Parse(HelperSource + "\n" + VariantHelperSource + "\n" + LocaleHelperSource + "\n" + CurrencyDateHelperSource);
    this._helperProcedures = parsed.Declarations.Cast<VbaProcedure>().ToDictionary(procedure => procedure.Name, StringComparer.Ordinal);
    foreach (var procedure in this._helperProcedures.Values)
      this._helperSignatures[procedure.Name] = this.SignatureOf(procedure) with { IsPublic = false };
  }

  /// <summary>The call of a helper by name, which the module then carries.</summary>
  private IrExpr HelperCall(string name, params IrExpr[] arguments) {
    this.ReadHelpers();
    var signature = this._helperSignatures[name];
    this._usedHelpers.Add(name);
    return new IrCall(name, signature.Returns!.Ir, arguments);
  }

  private VbaValue Helper(string name, params VbaValue[] arguments) {
    this.ReadHelpers();
    var signature = this._helperSignatures[name];
    var values = arguments.Select((argument, index) => this.Coerce(argument, signature.Parameters[index].Type, 0)).ToArray();
    return new(this.HelperCall(name, values), signature.Returns!);
  }

  /// <summary>The helpers the module calls, lowered as procedures of their own, and the helpers those call.</summary>
  private IEnumerable<IrFunction> Helpers() {
    var written = new HashSet<string>(StringComparer.Ordinal);
    var functions = new List<IrFunction>();
    this._raw = true;
    try {
      while (this._usedHelpers.Except(written).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault() is { } next) {
        written.Add(next);
        this._procedures[next] = this._helperSignatures[next];
        functions.Add(this.Procedure(this._helperProcedures![next]));
        this._procedures.Remove(next);
      }
    } catch (VbaRefusal refusal) {
      this.Report(refusal);
    } finally {
      this._raw = false;
    }

    return functions;
  }

  /// <summary>
  /// A built-in function of the subset, or <see langword="null"/> where the name is none: the
  /// conversions, the numeric functions, the string functions by code unit, <c>LBound</c> and
  /// <c>UBound</c>, <c>IIf</c> and <c>Hex</c>.
  /// </summary>
  private VbaValue? Builtin(string name, IReadOnlyList<VbaExpr> arguments, int line) {
    var key = name.TrimEnd('$').ToLowerInvariant();
    // A Variant takes part only in the built-in functions that convert or look at one.
    var aware = key is "cbool" or "cbyte" or "cint" or "clng" or "clnglng" or "csng" or "cdbl" or "cstr" or "iif" or "isempty" or "isnull" or "vartype" or "typename" or "isnumeric" or "cvar"
        or "ccur" or "cdate" or "dateserial" or "timeserial" or "year" or "month" or "day" or "weekday" or "hour" or "minute" or "second"
      || key.StartsWith("plyir", StringComparison.Ordinal);
    VbaValue Argument(int index) {
      var value = index < arguments.Count ? this.Expression(arguments[index]) : throw this.Unresolved(line, $"{name} needs {index + 1} arguments");
      return value.Type.IsVariant && !aware ? throw this.Unsupported(line, $"{name} of a Variant") : value;
    }
    void Arity(params int[] counts) {
      if (!counts.Contains(arguments.Count))
        throw this.Unresolved(line, $"{name} takes {string.Join(" or ", counts)} arguments, not {arguments.Count}");
    }

    VbaValue Convert(VbaType type) {
      Arity(1);
      var value = Argument(0);
      return new(this.Coerce(value, type, line), type);
    }

    VbaValue Float(string helper, IrIntrinsicOp op) {
      Arity(1);
      var value = Argument(0);
      if (!value.Type.IsNumeric)
        throw this.Mismatch(line, name, value.Type);
      var number = this.ToFloat(value, IrType.F64);
      return helper.Length > 0 ? new(this.HelperCall(helper, number), VbaType.Double) : new(new IrIntrinsic(op, IrType.F64, [number]), VbaType.Double);
    }

    VbaValue Text(int index) {
      var value = Argument(index);
      return value.Type.Kind == VbaKind.String ? value : throw this.Mismatch(line, name, value.Type);
    }

    VbaValue Long(int index) => new(this.Coerce(Argument(index), VbaType.Long, line), VbaType.Long);

    if (this._raw && key.StartsWith("plyir", StringComparison.Ordinal))
      return this.Primitive(key, arguments, line);

    if (key is "isempty" or "isnull" or "vartype" or "typename" or "isnumeric" or "cvar") {
      Arity(1);
      return this.VariantBuiltin(key, Argument, line);
    }

    switch (key) {
      case "cbool":
        return Convert(VbaType.Boolean);
      case "cbyte":
        return Convert(VbaType.Byte);
      case "cint":
        return Convert(VbaType.Integer);
      case "clng":
        return Convert(VbaType.Long);
      case "clnglng":
        return Convert(VbaType.LongLong);
      case "csng":
        return Convert(VbaType.Single);
      case "ccur":
        return Convert(VbaType.Currency);
      case "cdate":
        return Convert(VbaType.Date);
      case "dateserial" or "timeserial":
        Arity(3);
        return new(this.HelperCall(key == "dateserial" ? "PlyDateSerial" : "PlyTimeSerial", Long(0).Expr, Long(1).Expr, Long(2).Expr), VbaType.Date);
      case "year" or "month" or "day" or "hour" or "minute" or "second":
        Arity(1);
        var part = Array.IndexOf<string>(["year", "month", "day", "hour", "minute", "second"], key) + 1;
        return new(this.HelperCall("PlyDatePart", this.Coerce(Argument(0), VbaType.Date, line), I16(part)), VbaType.Integer);
      case "weekday":
        Arity(1, 2);
        var dated = this.Coerce(Argument(0), VbaType.Date, line);
        return new(this.HelperCall("PlyWeekday", dated, arguments.Count == 2 ? this.Coerce(Argument(1), VbaType.Integer, line) : I16(1)), VbaType.Integer);
      case "cdbl":
        return Convert(VbaType.Double);
      case "cstr":
        Arity(1);
        return new(this.Text(Argument(0), line), VbaType.String);
      case "int" or "fix":
        Arity(1);
        var whole = Argument(0);
        if (whole.Type.IsInteger)
          return whole;
        if (!whole.Type.IsFloat)
          throw this.Mismatch(line, name, whole.Type);
        return new(new IrIntrinsic(key == "int" ? IrIntrinsicOp.Floor : IrIntrinsicOp.Truncate, whole.Type.Ir, [whole.Expr]), whole.Type);
      case "round":
        Arity(1);
        var rounded = Argument(0);
        if (rounded.Type.IsInteger)
          return rounded;
        if (!rounded.Type.IsFloat)
          throw this.Mismatch(line, name, rounded.Type);
        return new(new IrIntrinsic(IrIntrinsicOp.RoundEven, rounded.Type.Ir, [rounded.Expr]), rounded.Type);
      case "abs":
        Arity(1);
        var magnitude = Argument(0);
        if (magnitude.Type.IsFloat)
          return new(new IrIntrinsic(IrIntrinsicOp.Abs, magnitude.Type.Ir, [magnitude.Expr]), magnitude.Type);
        if (!magnitude.Type.IsInteger)
          throw this.Mismatch(line, name, magnitude.Type);
        var held = this.Temporary(magnitude.Type.Ir, magnitude.Expr);
        var heldValue = new VbaValue(new IrLocalRef(held), magnitude.Type);
        var negative = this.Compare("<", heldValue, new(I32(0), VbaType.Long), line);
        var negated = this.Arithmetic("-", magnitude.Type == VbaType.Byte ? VbaType.Byte : magnitude.Type, new(new IrConst(magnitude.Type.Ir, 0), magnitude.Type), heldValue);
        return new(new IrConditional(negative, negated.Expr, heldValue.Expr), magnitude.Type);
      case "sgn":
        Arity(1);
        var signed = Argument(0);
        if (!signed.Type.IsNumeric)
          throw this.Mismatch(line, name, signed.Type);
        var subject = this.Temporary(signed.Type.Ir, signed.Expr);
        var value = new VbaValue(new IrLocalRef(subject), signed.Type);
        var zero = new VbaValue(I32(0), VbaType.Long);
        return new(new IrConditional(this.Compare(">", value, zero, line), new IrConst(IrType.I16, 1),
          new IrConditional(this.Compare("<", value, zero, line), new IrConst(IrType.I16, -1), new IrConst(IrType.I16, 0))), VbaType.Integer);
      case "sqr":
        return Float("PlySqr", IrIntrinsicOp.Sqrt);
      case "log":
        return Float("PlyLog", IrIntrinsicOp.Log);
      case "exp":
        return Float("PlyExp", IrIntrinsicOp.Exp);
      case "sin":
        return Float("", IrIntrinsicOp.Sin);
      case "cos":
        return Float("", IrIntrinsicOp.Cos);
      case "tan":
        return Float("", IrIntrinsicOp.Tan);
      case "atn":
        return Float("", IrIntrinsicOp.Atan);
      case "len":
        Arity(1);
        return new(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [Text(0).Expr]), VbaType.Long);
      case "left":
        Arity(2);
        return this.Helper("PlyLeft", Text(0), Long(1));
      case "right":
        Arity(2);
        return this.Helper("PlyRight", Text(0), Long(1));
      case "mid":
        Arity(2, 3);
        return this.Helper("PlyMid", Text(0), Long(1), arguments.Count == 3 ? Long(2) : new(I32(int.MaxValue), VbaType.Long));
      case "instr":
        Arity(2, 3);
        return arguments.Count == 2 ? this.Helper("PlyInStr", new(I32(1), VbaType.Long), Text(0), Text(1)) : this.Helper("PlyInStr", Long(0), Text(1), Text(2));
      case "ascw" or "asc":
        Arity(1);
        var code = this.Helper("PlyAscW", Text(0));
        return new(new IrConvert(IrType.I16, code.Expr), VbaType.Integer);
      case "chr":
        Arity(1);
        return this.Helper("PlyChr", Long(0));
      case "chrw":
        Arity(1);
        return this.Helper("PlyChrW", Long(0));
      case "space":
        Arity(1);
        return this.Helper("PlyRepeat", Long(0), new(new IrStrConst(" "), VbaType.String));
      case "string":
        Arity(2);
        var count = Long(0);
        var filler = Argument(1);
        return filler.Type.Kind == VbaKind.String ? this.Helper("PlyRepeat", count, filler) : this.Helper("PlyRepeat", count, this.Helper("PlyChrW", filler));
      case "strcomp":
        Arity(2, 3);
        if (arguments.Count == 3 && this.Expression(arguments[2]).Expr is not IrConst { Value: 0 })
          throw this.Unsupported(line, "StrComp with a comparison other than vbBinaryCompare (0)");
        var compared = new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [Text(0).Expr, Text(1).Expr]);
        return new(new IrConvert(IrType.I16, compared, IrConversion.Wrap), VbaType.Integer);
      case "lbound" or "ubound":
        Arity(1);
        var array = Argument(0);
        if (!array.Type.IsArray)
          throw this.Mismatch(line, name, array.Type);
        if (key == "lbound")
          return new(I32(array.Type.LowerBound), VbaType.Long);
        return new(new IrBinary(IrBinaryOp.Add, new IrArrayLength(array.Expr), I32(array.Type.LowerBound - 1)), VbaType.Long);
      case "iif":
        Arity(3);
        var condition = this.Condition(arguments[0]);
        var yes = Argument(1);
        var no = Argument(2);
        var type = yes.Type == no.Type ? yes.Type : yes.Type.IsVariant || no.Type.IsVariant ? VbaType.Variant
          : yes.Type.IsNumeric && no.Type.IsNumeric ? Promote(yes.Type, no.Type) : throw this.Mismatch(line, name, no.Type);
        // IIf evaluates both values before it chooses.
        var whenTrue = Pure(yes.Expr) ? this.Coerce(yes, type, line) : new IrLocalRef(this.Temporary(type.Ir, this.Coerce(yes, type, line)));
        var whenFalse = Pure(no.Expr) ? this.Coerce(no, type, line) : new IrLocalRef(this.Temporary(type.Ir, this.Coerce(no, type, line)));
        return new(new IrConditional(condition, whenTrue, whenFalse), type) { Tags = type.IsVariant ? TagsOf(yes) | TagsOf(no) : VbaTags.None };
      case "hex":
        Arity(1);
        var number = Argument(0);
        if (!number.Type.IsInteger)
          throw this.Mismatch(line, name, number.Type);
        var digits = number.Type.Kind switch { VbaKind.Byte or VbaKind.Integer => number.Type.Kind == VbaKind.Byte ? IrType.U8 : IrType.I16, _ => number.Expr.Type };
        var bits = digits == IrType.I16 ? new IrConvert(IrType.U16, number.Expr) : number.Expr;
        return new(new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String, [bits, I32(16), I32(1), new IrConst(IrType.Bool, 1), I32((int)RadixMode.TwoComplement)]), VbaType.String);
      case "val":
        Arity(1);
        return this.Helper("PlyVal", Text(0));
      case "array":
        var items = arguments.Select(item => this.Box(this.Expression(item), line)).ToList();
        var listed = VbaType.ArrayOf(VbaType.Variant, this._optionBase);
        return new(items.Count == 0 ? new IrNewArray(listed.Ir, I32(0)) : new IrIntrinsic(IrIntrinsicOp.ArrayOf, listed.Ir, items), listed);
      case "isarray":
        Arity(1);
        var probed = this.Expression(arguments[0]);
        if (!Pure(probed.Expr))
          this.Temporary(probed.Expr.Type, probed.Expr);
        return new(new IrConst(IrType.Bool, probed.Type.IsArray ? 1 : 0), VbaType.Boolean);
      case "ismissing" or "isobject" or "format" or "ucase" or "lcase"
        or "trim" or "ltrim" or "rtrim" or "replace" or "split" or "join" or "now" or "date" or "time" or "timer" or "rnd" or "cdec"
        or "strconv" or "strreverse" or "instrrev" or "doevents" or "environ" or "shell" or "createobject" or "getobject" or "msgbox" or "inputbox" or "error":
        throw this.Unsupported(line, name);
      default:
        return null;
    }
  }

  /// <summary>An operation of the IR as a helper names it: <c>PlyIrSubstring</c>, <c>PlyIrIndexOf</c>, <c>PlyIrCodeUnit</c>, <c>PlyIrFromCodeUnit</c>, <c>PlyIrRepeat</c>, <c>PlyIrPow</c>, <c>PlyIrSqrt</c>, <c>PlyIrLog</c>, <c>PlyIrExp</c>.</summary>
  private VbaValue Primitive(string key, IReadOnlyList<VbaExpr> arguments, int line) {
    var values = arguments.Select(this.Expression).ToList();
    switch (key) {
      case "plyirdec":
        return new(I32(this._locale.Decimal), VbaType.Long);
      case "plyirgrp":
        return new(I32(this._locale.Group), VbaType.Long);
      case "plyirformat":
        return new(NumberTextOperations.VisualBasic(values[0].Expr, this._locale.Decimal), VbaType.String);
      case "plyirparse":
        return new(new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.F64, [values[0].Expr]), VbaType.Double);
      case "plyirparses":
        return new(new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.F32, [values[0].Expr]), VbaType.Single);
    }

    if (VariantPrimitive(key, values[0].Expr) is { } variant)
      return variant;
    IrExpr Long(int index) => this.Coerce(values[index], VbaType.Long, line);
    IrExpr Double(int index) => this.Coerce(values[index], VbaType.Double, line);
    return key switch {
      "plyirsubstring" => new(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [values[0].Expr, Long(1), Long(2)]), VbaType.String),
      "plyirindexof" => new(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [values[0].Expr, values[1].Expr, Long(2)]), VbaType.Long),
      "plyircodeunit" => new(new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [values[0].Expr, Long(1)])), VbaType.Long),
      "plyirfromcodeunit" => new(new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [new IrConvert(IrType.U16, Long(0))]), VbaType.String),
      "plyirrepeat" => new(new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [new IrConvert(IrType.U16, Long(0)), Long(1)]), VbaType.String),
      "plyirpow" => new(new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F64, [Double(0), Double(1)]), VbaType.Double),
      "plyirsqrt" => new(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [Double(0)]), VbaType.Double),
      "plyirlog" => new(new IrIntrinsic(IrIntrinsicOp.Log, IrType.F64, [Double(0)]), VbaType.Double),
      _ => new(new IrIntrinsic(IrIntrinsicOp.Exp, IrType.F64, [Double(0)]), VbaType.Double)
    };
  }

}
