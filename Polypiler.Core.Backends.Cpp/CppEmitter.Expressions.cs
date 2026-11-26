using System;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Cpp;

public static partial class CppEmitter {

  private sealed partial class Writer {

    private string EmitExpr(IrExpr expression) {
      return expression switch {
        IrConst constant => this.FormatScalarConstant(constant.Type, constant.Value),
        IrStrConst str => this.EmitStringLiteral(str.Value),
        IrConstRef constRef => this._constNames[constRef.Constant],
        IrLocalRef localRef => this.EmitLocalRef(localRef),
        IrGlobalRef globalRef => this._globalNames[globalRef.Global],
        IrUnary unary => this.EmitUnary(unary),
        IrBinary binary => this.EmitBinary(binary),
        IrConvert convert => this.EmitConvert(convert),
        IrConditional cond => $"({this.EmitExpr(cond.Condition)} ? {this.EmitExpr(cond.WhenTrue)} : {this.EmitExpr(cond.WhenFalse)})",
        IrCall call => this.EmitCall(call),
        IrStep step => this.EmitStep(step),
        IrBind bind => $"[&]() {{ {this.LocalName(bind.Local)} = {this.EmitExpr(bind.Value)}; return {this.LocalName(bind.Local)}; }}()",
        IrArrayLoad load => this.EmitArrayLoad(load),
        IrArrayLength length => this.EmitArrayLength(length),
        IrNewArray newArray => this.EmitNewArray(newArray),
        IrIntrinsic intrinsic => this.EmitIntrinsic(intrinsic),
        IrStructNew structNew => this.EmitStructNew(structNew),
        IrStructDefault structDefault => $"{this.CppType(structDefault.Type, inHeader: false)}{{}}",
        IrFieldLoad fieldLoad => this.EmitFieldLoad(fieldLoad),
        IrStructEquals structEquals => this.EmitStructEquals(structEquals),
        IrNew objNew => this.EmitObjectNew(objNew),
        IrNull n when n.Type == IrType.String => this.EmitStringLiteral(""),
        IrNull => "nullptr",
        IrMethodCall methodCall => this.EmitMethodCall(methodCall),
        IrReferenceEquals refEq => $"({this.EmitExpr(refEq.Left)} == {this.EmitExpr(refEq.Right)})",
        IrNone none => $"{this.CppType(none.Type, inHeader: false)}{{}}",
        IrSome some => $"{this.CppType(some.Type, inHeader: false)}({this.EmitExpr(some.Value)})",
        IrIsNone isNone => $"(!({this.EmitExpr(isNone.Value)}).has_value())",
        IrUnwrap unwrap => this.EmitUnwrap(unwrap),
        IrCoalesce coalesce => this.EmitCoalesce(coalesce),
        IrIsNull isNull => $"(({this.EmitExpr(isNull.Value)}) == nullptr)",
        IrCollection collection => this.EmitCollection(collection),
        IrThrowExpression throwExpr => $"[&]() -> {this.CppType(throwExpr.Type, inHeader: false)} {{ throw {this.EmitExceptionValue(throwExpr.Exception)}; }}()",
        IrTypeTest typeTest => this.EmitTypeTest(typeTest),
        IrCast cast => this.EmitCast(cast),
        IrAsCast asCast => this.EmitAsCast(asCast),
        IrGenericCompare genericCompare => this.EmitGenericCompare(genericCompare),
        IrForeignCall foreignCall => this.EmitForeignCall(foreignCall),
        _ => this.ReportUnsupportedConstruct(expression.GetType().Name)
      };
    }

    private string EmitStringLiteral(string value) {
      this._sourceIncludes.Add("<string>");
      return CppNaming.StringLiteral(value);
    }

    private string EmitLocalRef(IrLocalRef localRef) {
      if (ReferenceEquals(localRef.Local, this._currentThis)) {
        if (this._currentClass!.Base is null)
          return "this->shared_from_this()";
        return $"std::static_pointer_cast<{this._typeNames[this._currentClass]}>(this->shared_from_this())";
      }
      return this.LocalName(localRef.Local);
    }

    private string EmitCall(IrCall call) {
      var targetName = this._functionNames[call.Function];
      var args = call.Arguments.Select(this.EmitExpr).ToArray();
      if (this._functionMap.TryGetValue(call.Function, out var targetFn) &&
          this._paramModes.TryGetValue(targetFn, out var modes)) {
        var needsTemp = false;
        for (var i = 0; i < args.Length && i < modes.Length; ++i)
          if (modes[i] == ParamMode.MutRef && call.Arguments[i] is not (IrLocalRef or IrGlobalRef))
            needsTemp = true;

        if (needsTemp) {
          var binds = new System.Text.StringBuilder();
          var callArgs = new string[args.Length];
          for (var i = 0; i < args.Length; ++i) {
            if (i < modes.Length && modes[i] == ParamMode.MutRef && call.Arguments[i] is not (IrLocalRef or IrGlobalRef)) {
              var tmp = this.Temp("ply_arg");
              binds.Append("auto ").Append(tmp).Append(" = ").Append(args[i]).Append("; ");
              callArgs[i] = tmp;
            } else {
              callArgs[i] = args[i];
            }
          }
          var retPrefix = call.Type == IrType.Void ? "" : "return ";
          return $"[&]() {{ {binds}{retPrefix}{targetName}({string.Join(", ", callArgs)}); }}()";
        }
      }

      return $"{targetName}({string.Join(", ", args)})";
    }

    private string EmitUnary(IrUnary unary) {
      var operand = this.EmitExpr(unary.Operand);
      var rawType = unary.Type;
      var type = rawType.Integral;
      var cppType = this.CppType(type, inHeader: false);

      string result;
      switch (unary.Op) {
        case IrUnaryOp.LogicalNot:
          return $"(!({operand}))";
        case IrUnaryOp.BitNot:
          result = $"static_cast<{cppType}>(~static_cast<{this.UnsignedCppType(type)}>({this.UnwrapEnum(rawType, operand)}))";
          break;
        case IrUnaryOp.Negate when type.IsFloat:
          return $"(-({operand}))";
        case IrUnaryOp.Negate: {
          var utype = this.UnsignedCppType(type);
          result = $"static_cast<{cppType}>(static_cast<{utype}>(0) - static_cast<{utype}>({this.UnwrapEnum(rawType, operand)}))";
          break;
        }
        default: {
          this.UseHelper(CppHelpers.CheckedNeg);
          result = $"{CppHelpers.CheckedNeg}<{cppType}>({this.UnwrapEnum(rawType, operand)})";
          break;
        }
      }

      return this.WrapEnum(rawType, result);
    }

    private string EmitBinary(IrBinary binary) {
      var left = this.EmitExpr(binary.Left);
      var right = this.EmitExpr(binary.Right);

      if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr) {
        var tok = binary.Op == IrBinaryOp.LogicalAnd ? "&&" : "||";
        return $"({left} {tok} {right})";
      }

      if (ContainsMutation(binary.Right)) {
        var lvar = this.Temp("ply_l");
        var rvar = this.Temp("ply_r");
        var expr = this.EmitBinaryOp(binary, lvar, rvar);
        return $"[&]() {{ const auto {lvar} = {left}; const auto {rvar} = {right}; return {expr}; }}()";
      }

      return this.EmitBinaryOp(binary, left, right);
    }

    private static bool ContainsMutation(IrExpr expr)
      => IrTree.Nodes(expr).Any(n => n is IrBind or IrStep);

    private string EmitBinaryOp(IrBinary binary, string left, string right) {
      if (TryComparisonOperator(binary.Op, out var cmp))
        return $"({left} {cmp} {right})";

      var rawType = binary.Type;
      var type = rawType.Integral;
      if (type.IsFloat)
        return this.EmitFloatBinary(binary.Op, left, right);

      var cppType = this.CppType(type, inHeader: false);
      var utype = this.UnsignedCppType(type);
      var l = this.UnwrapEnum(rawType, left);
      var r = this.UnwrapEnum(binary.Right.Type, right);

      string result;
      switch (binary.Op) {
        case IrBinaryOp.Add:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) + static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.Subtract:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) - static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.Multiply:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) * static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.AddChecked:
          this.UseHelper(CppHelpers.CheckedAdd);
          result = $"{CppHelpers.CheckedAdd}<{cppType}>({l}, {r})";
          break;
        case IrBinaryOp.SubtractChecked:
          this.UseHelper(CppHelpers.CheckedSub);
          result = $"{CppHelpers.CheckedSub}<{cppType}>({l}, {r})";
          break;
        case IrBinaryOp.MultiplyChecked:
          this.UseHelper(CppHelpers.CheckedMul);
          result = $"{CppHelpers.CheckedMul}<{cppType}>({l}, {r})";
          break;
        case IrBinaryOp.Divide:
          this.UseHelper(CppHelpers.IntDiv);
          result = $"{CppHelpers.IntDiv}<{cppType}>({l}, {r})";
          break;
        case IrBinaryOp.Remainder:
          this.UseHelper(CppHelpers.IntRem);
          result = $"{CppHelpers.IntRem}<{cppType}>({l}, {r})";
          break;
        case IrBinaryOp.And:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) & static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.Or:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) | static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.Xor:
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) ^ static_cast<{utype}>({r}))";
          break;
        case IrBinaryOp.ShiftLeft: {
          var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) << (static_cast<unsigned>({r}) & {mask}u))";
          break;
        }
        case IrBinaryOp.ShiftRight when type.IsSigned: {
          if (this._profile.Standard >= CppStandard.Cpp20) {
            var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
            result = $"static_cast<{cppType}>(({l}) >> (static_cast<unsigned>({r}) & {mask}u))";
          } else {
            this.UseHelper(CppHelpers.ArithShiftRight);
            result = $"{CppHelpers.ArithShiftRight}<{cppType}>({l}, {r})";
          }
          break;
        }
        default: {
          var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
          result = $"static_cast<{cppType}>(static_cast<{utype}>({l}) >> (static_cast<unsigned>({r}) & {mask}u))";
          break;
        }
      }

      return this.WrapEnum(rawType, result);
    }

    private string EmitFloatBinary(IrBinaryOp op, string left, string right) {
      if (op == IrBinaryOp.Remainder) {
        this._sourceIncludes.Add("<cmath>");
        return $"std::fmod({left}, {right})";
      }

      var tok = op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        _ => "/"
      };
      return $"({left} {tok} {right})";
    }

    private string EmitConvert(IrConvert convert) {
      var operand = this.EmitExpr(convert.Operand);
      var dstRaw = convert.Type;
      var srcRaw = convert.Operand.Type;
      if (dstRaw == srcRaw)
        return operand;

      var dst = dstRaw.Integral;
      var src = srcRaw.Integral;
      var dstCpp = this.CppType(dst, inHeader: false);
      var unwrapped = this.UnwrapEnum(srcRaw, operand);

      if (dst == src)
        return this.WrapEnum(dstRaw, unwrapped);

      if (dst.IsFloat)
        return $"static_cast<{dstCpp}>({unwrapped})";

      string converted;
      if (src.IsFloat) {
        this.UseHelper(CppHelpers.FloatToInt);
        var trap = convert.Contract == IrConversion.Trap ? "true" : "false";
        converted = $"{CppHelpers.FloatToInt}<{dstCpp}, {trap}>({unwrapped})";
      } else if (convert.Contract == IrConversion.Trap) {
        this.UseHelper(CppHelpers.CheckedConv);
        converted = $"{CppHelpers.CheckedConv}<{dstCpp}>({unwrapped})";
      } else {
        converted = dst.IsSigned
          ? $"static_cast<{dstCpp}>(static_cast<{this.UnsignedCppType(dst)}>({unwrapped}))"
          : $"static_cast<{dstCpp}>({unwrapped})";
      }

      return this.WrapEnum(dstRaw, converted);
    }

    private string EmitStep(IrStep step) {
      var rawType = step.Type;
      var type = rawType.Integral;
      var cppType = this.CppType(type, inHeader: false);
      var utype = type.IsFloat ? "" : this.UnsignedCppType(type);

      string NextExpr(string cur) {
        if (type.IsFloat)
          return step.Increment ? $"({cur} + {(type == IrType.F32 ? "1.0f" : "1.0")})" : $"({cur} - {(type == IrType.F32 ? "1.0f" : "1.0")})";
        var unwrapped = this.UnwrapEnum(rawType, cur);
        string computed;
        if (step.Trap) {
          var helper = step.Increment ? CppHelpers.CheckedAdd : CppHelpers.CheckedSub;
          this.UseHelper(helper);
          computed = $"{helper}<{cppType}>({unwrapped}, static_cast<{cppType}>(1))";
        } else {
          var op = step.Increment ? "+" : "-";
          computed = $"static_cast<{cppType}>(static_cast<{utype}>({unwrapped}) {op} static_cast<{utype}>(1))";
        }
        return this.WrapEnum(rawType, computed);
      }

      if (step.Target is IrLocalRef localRef) {
        var name = this.LocalName(localRef.Local);
        return step.Postfix
          ? $"[&]() {{ auto ply_old = {name}; {name} = {NextExpr("ply_old")}; return ply_old; }}()"
          : $"({name} = {NextExpr(name)})";
      }

      var load = (IrArrayLoad)step.Target;
      this.UseHelper(CppHelpers.ArrayAt);
      var arr = this.EmitExpr(load.Array);
      var idx = this.EmitExpr(load.Index);
      return step.Postfix
        ? $"[&]() {{ auto& ply_el = {CppHelpers.ArrayAt}({arr}, {idx}); auto ply_old = ply_el; ply_el = {NextExpr("ply_old")}; return ply_old; }}()"
        : $"[&]() {{ auto& ply_el = {CppHelpers.ArrayAt}({arr}, {idx}); ply_el = {NextExpr("ply_el")}; return ply_el; }}()";
    }

    private string EmitNewArray(IrNewArray array) {
      this.UseHelper(CppHelpers.NewArray);
      var elemCpp = this.CppType(array.Type.Element!, inHeader: false);
      return $"{CppHelpers.NewArray}<{elemCpp}>({this.EmitExpr(array.Length)})";
    }

    private string EmitArrayLength(IrArrayLength length) {
      this._sourceIncludes.Add("<cstdint>");
      return $"static_cast<std::int32_t>(({this.EmitExpr(length.Array)}).size())";
    }

    private string EmitArrayLoad(IrArrayLoad load) {
      this.UseHelper(CppHelpers.ArrayAt);
      return $"{CppHelpers.ArrayAt}({this.EmitExpr(load.Array)}, {this.EmitExpr(load.Index)})";
    }

    private string EmitStructNew(IrStructNew structNew) {
      var st = structNew.Type.StructType!;
      var args = string.Join(", ", structNew.Arguments.Select(this.EmitExpr));
      return $"{this._typeNames[st]}{{{args}}}";
    }

    private string EmitFieldLoad(IrFieldLoad load) {
      if (load.Target.Type.IsObject)
        return this.EmitClassFieldLoad(load);
      return $"({this.EmitExpr(load.Target)}).{this._fieldNames[load.Field]}";
    }

    private string EmitStructEquals(IrStructEquals equals) {
      if (equals.Left.Type.IsObject)
        return this.EmitClassRecordEquals(equals);
      return $"({this.EmitExpr(equals.Left)} == {this.EmitExpr(equals.Right)})";
    }

    private string EmitUnwrap(IrUnwrap unwrap) {
      var val = this.EmitExpr(unwrap.Value);
      if (unwrap.IsTested)
        return $"(*({val}))";
      this.UseHelper(CppHelpers.Unwrap);
      return $"{CppHelpers.Unwrap}({val})";
    }

    private string EmitCoalesce(IrCoalesce coalesce) {
      var val = this.EmitExpr(coalesce.Value);
      var fb = this.EmitExpr(coalesce.Fallback);
      var unwrapExpr = coalesce.Fallback.Type.IsOptional ? "ply_opt" : "*ply_opt";
      return $"[&]() {{ auto ply_opt = {val}; return ply_opt.has_value() ? {unwrapExpr} : ({fb}); }}()";
    }

    private string EmitIntrinsic(IrIntrinsic intrinsic) {
      var args = intrinsic.Arguments.Select(this.EmitExpr).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf: {
          var elemCpp = this.CppType(intrinsic.Type.Element!, inHeader: false);
          this._sourceIncludes.Add("<vector>");
          return $"std::vector<{elemCpp}>{{{string.Join(", ", args)}}}";
        }
        case IrIntrinsicOp.ArrayCopy:
          this.UseHelper(CppHelpers.ArrayCopy);
          return $"{CppHelpers.ArrayCopy}({args[0]}, {args[1]}, {args[2]}, {args[3]}, {args[4]})";
        case IrIntrinsicOp.ArrayFill:
          this._sourceIncludes.Add("<algorithm>");
          return $"std::fill(({args[0]}).begin(), ({args[0]}).end(), {args[1]})";
        case IrIntrinsicOp.ArrayClear: {
          this._sourceIncludes.Add("<algorithm>");
          var def = this.DefaultValue(intrinsic.Arguments[0].Type.Element!);
          return $"std::fill(({args[0]}).begin(), ({args[0]}).end(), {def})";
        }
        case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig: {
          this.UseHelper(CppHelpers.LoadEndian);
          var little = intrinsic.Op == IrIntrinsicOp.LoadLittle ? "true" : "false";
          var valCpp = this.CppType(intrinsic.Type, inHeader: false);
          return $"{CppHelpers.LoadEndian}<{valCpp}, {little}>({args[0]}, {args[1]})";
        }
        case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig: {
          this.UseHelper(CppHelpers.StoreEndian);
          var little = intrinsic.Op == IrIntrinsicOp.StoreLittle ? "true" : "false";
          var valCpp = this.CppType(intrinsic.Arguments[2].Type, inHeader: false);
          return $"{CppHelpers.StoreEndian}<{valCpp}, {little}>({args[0]}, {args[1]}, {args[2]})";
        }
        case >= IrIntrinsicOp.StrLength and <= IrIntrinsicOp.StrPadEnd:
          return this.EmitStringIntrinsic(intrinsic, args);
        case >= IrIntrinsicOp.EnumToString and <= IrIntrinsicOp.EnumValues:
          return this.EmitEnumIntrinsic(intrinsic, args);
        default:
          return this.EmitScalarAndBitIntrinsic(intrinsic, args);
      }
    }

    private string EmitScalarAndBitIntrinsic(IrIntrinsic intrinsic, string[] args) {
      var type = intrinsic.Type.Integral;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.Abs when type.IsFloat:
          this._sourceIncludes.Add("<cmath>");
          return $"std::fabs({args[0]})";
        case IrIntrinsicOp.Abs when !type.IsSigned:
          return args[0];
        case IrIntrinsicOp.Abs:
          this.UseHelper(CppHelpers.IntAbs);
          return $"{CppHelpers.IntAbs}({args[0]})";

        case IrIntrinsicOp.Min when !type.IsFloat:
          this._sourceIncludes.Add("<algorithm>");
          return $"std::min({args[0]}, {args[1]})";
        case IrIntrinsicOp.Max when !type.IsFloat:
          this._sourceIncludes.Add("<algorithm>");
          return $"std::max({args[0]}, {args[1]})";
        case IrIntrinsicOp.Min:
          this.UseHelper(CppHelpers.FloatMin);
          return $"{CppHelpers.FloatMin}({args[0]}, {args[1]})";
        case IrIntrinsicOp.Max:
          this.UseHelper(CppHelpers.FloatMax);
          return $"{CppHelpers.FloatMax}({args[0]}, {args[1]})";

        case IrIntrinsicOp.Sign when intrinsic.Arguments[0].Type.IsFloat:
          this.UseHelper(CppHelpers.FloatSign);
          return $"{CppHelpers.FloatSign}({args[0]})";
        case IrIntrinsicOp.Sign:
          this.UseHelper(CppHelpers.IntSign);
          return $"{CppHelpers.IntSign}({args[0]})";

        case IrIntrinsicOp.Sqrt:
          this._sourceIncludes.Add("<cmath>");
          return $"std::sqrt({args[0]})";
        case IrIntrinsicOp.Floor:
          this._sourceIncludes.Add("<cmath>");
          return $"std::floor({args[0]})";
        case IrIntrinsicOp.Ceiling:
          this._sourceIncludes.Add("<cmath>");
          return $"std::ceil({args[0]})";
        case IrIntrinsicOp.Truncate:
          this._sourceIncludes.Add("<cmath>");
          return $"std::trunc({args[0]})";
        case IrIntrinsicOp.RoundEven:
          this._sourceIncludes.Add("<cmath>");
          return $"std::nearbyint({args[0]})";
        case IrIntrinsicOp.RoundAway:
          this._sourceIncludes.Add("<cmath>");
          return $"std::round({args[0]})";
        case IrIntrinsicOp.CopySign:
          this._sourceIncludes.Add("<cmath>");
          return $"std::copysign({args[0]}, {args[1]})";

        case IrIntrinsicOp.IsNaN:
          this._sourceIncludes.Add("<cmath>");
          return $"std::isnan({args[0]})";
        case IrIntrinsicOp.IsInfinity:
          this._sourceIncludes.Add("<cmath>");
          return $"std::isinf({args[0]})";
        case IrIntrinsicOp.IsFinite:
          this._sourceIncludes.Add("<cmath>");
          return $"std::isfinite({args[0]})";
        case IrIntrinsicOp.IsNegative:
          this._sourceIncludes.Add("<cmath>");
          return $"std::signbit({args[0]})";
        case IrIntrinsicOp.IsNormal:
          this._sourceIncludes.Add("<cmath>");
          return $"std::isnormal({args[0]})";
        case IrIntrinsicOp.IsSubnormal:
          this._sourceIncludes.Add("<cmath>");
          return $"(std::fpclassify({args[0]}) == FP_SUBNORMAL)";
        case IrIntrinsicOp.IsInteger:
          this._sourceIncludes.Add("<cmath>");
          return $"[&]() {{ auto ply_v = {args[0]}; return std::isfinite(ply_v) && std::trunc(ply_v) == ply_v; }}()";

        case IrIntrinsicOp.FloatBits or IrIntrinsicOp.BitsFloat: {
          var dstCpp = this.CppType(intrinsic.Type, inHeader: false);
          if (this._profile.HasBitHeader) {
            this._sourceIncludes.Add("<bit>");
            return $"std::bit_cast<{dstCpp}>({args[0]})";
          }
          this.UseHelper(CppHelpers.BitCast17);
          return $"{CppHelpers.BitCast17}<{dstCpp}>({args[0]})";
        }

        case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight: {
          var cppType = this.CppType(type, inHeader: false);
          var utype = this.UnsignedCppType(type);
          if (this._profile.HasBitHeader) {
            this._sourceIncludes.Add("<bit>");
            var fn = intrinsic.Op == IrIntrinsicOp.RotateLeft ? "std::rotl" : "std::rotr";
            return $"static_cast<{cppType}>({fn}(static_cast<{utype}>({args[0]}), static_cast<int>({args[1]})))";
          }
          var helper = intrinsic.Op == IrIntrinsicOp.RotateLeft ? CppHelpers.Rotl17 : CppHelpers.Rotr17;
          this.UseHelper(helper);
          return $"{helper}<{cppType}>({args[0]}, {args[1]})";
        }

        case IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount: {
          var opType = intrinsic.Arguments[0].Type.Integral;
          var utype = this.UnsignedCppType(opType);
          if (this._profile.HasBitHeader) {
            this._sourceIncludes.Add("<bit>");
            var fn = intrinsic.Op switch {
              IrIntrinsicOp.LeadingZeroCount => "std::countl_zero",
              IrIntrinsicOp.TrailingZeroCount => "std::countr_zero",
              _ => "std::popcount"
            };
            return $"static_cast<std::int32_t>({fn}(static_cast<{utype}>({args[0]})))";
          }
          var helper = intrinsic.Op switch {
            IrIntrinsicOp.LeadingZeroCount => CppHelpers.Clz17,
            IrIntrinsicOp.TrailingZeroCount => CppHelpers.Ctz17,
            _ => CppHelpers.PopCount17
          };
          this.UseHelper(helper);
          return $"{helper}<{utype}>(static_cast<{utype}>({args[0]}))";
        }

        case IrIntrinsicOp.ByteSwap: {
          if (type.Bits == 8)
            return args[0];
          var cppType = this.CppType(type, inHeader: false);
          var utype = this.UnsignedCppType(type);
          if (this._profile.HasByteSwap) {
            this._sourceIncludes.Add("<bit>");
            return $"static_cast<{cppType}>(std::byteswap(static_cast<{utype}>({args[0]})))";
          }
          this.UseHelper(CppHelpers.ByteSwap);
          return $"{CppHelpers.ByteSwap}<{cppType}>({args[0]})";
        }

        case IrIntrinsicOp.HighWord:
          this._sourceIncludes.Add("<cstdint>");
          return $"static_cast<std::uint32_t>(static_cast<std::uint64_t>({args[0]}) >> 32)";

        case IrIntrinsicOp.JoinWords:
          this._sourceIncludes.Add("<cstdint>");
          return $"static_cast<std::int64_t>((static_cast<std::uint64_t>({args[0]}) << 32) | static_cast<std::uint64_t>({args[1]}))";

        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma: {
          var argType = intrinsic.Arguments[0].Type;
          if (argType.IsFloat) {
            this.UseHelper(CppHelpers.FormatFloat);
            var dotnet = intrinsic.Op == IrIntrinsicOp.FormatDotNet ? "true" : "false";
            return $"{CppHelpers.FormatFloat}<{this.CppType(argType, inHeader: false)}, {dotnet}>({args[0]})";
          }
          this._sourceIncludes.Add("<string>");
          return $"std::to_string({args[0]})";
        }

        case IrIntrinsicOp.FormatBoolDotNet:
          this._sourceIncludes.Add("<string>");
          return $"({args[0]} ? std::string(\"True\") : std::string(\"False\"))";

        case IrIntrinsicOp.FormatBoolEcma:
          this._sourceIncludes.Add("<string>");
          return $"({args[0]} ? std::string(\"true\") : std::string(\"false\"))";

        default:
          return this.ReportUnsupportedIntrinsic(intrinsic.Op);
      }
    }

    private string EmitStringIntrinsic(IrIntrinsic intrinsic, string[] args) {
      string AsString(int index) {
        if (intrinsic.Arguments[index].Type == IrType.String)
          return args[index];
        this._sourceIncludes.Add("<string>");
        return $"std::string(1, static_cast<char>({args[index]}))";
      }

      switch (intrinsic.Op) {
        case IrIntrinsicOp.StrLength:
          this._sourceIncludes.Add("<cstdint>");
          return $"static_cast<std::int32_t>(({args[0]}).size())";
        case IrIntrinsicOp.StrCodeUnitAt:
          this.UseHelper(CppHelpers.StrCodeUnitAt);
          return $"{CppHelpers.StrCodeUnitAt}({args[0]}, {args[1]})";
        case IrIntrinsicOp.StrConcat:
          return "(" + string.Join(" + ", args) + ")";
        case IrIntrinsicOp.StrFromCodeUnit:
          this._sourceIncludes.Add("<string>");
          return $"std::string(1, static_cast<char>({args[0]}))";
        case IrIntrinsicOp.StrFromCodeUnits:
          this.UseHelper(CppHelpers.StrFromCodeUnits);
          return args.Length == 1
            ? $"{CppHelpers.StrFromCodeUnits}({args[0]}, 0, static_cast<std::int32_t>(({args[0]}).size()))"
            : $"{CppHelpers.StrFromCodeUnits}({args[0]}, {args[1]}, {args[2]})";
        case IrIntrinsicOp.StrToCodeUnits:
          this.UseHelper(CppHelpers.StrToCodeUnits);
          return $"{CppHelpers.StrToCodeUnits}({args[0]})";
        case IrIntrinsicOp.StrRepeat:
          this.UseHelper(CppHelpers.StrRepeat);
          return $"{CppHelpers.StrRepeat}({AsString(0)}, {args[1]})";
        case IrIntrinsicOp.StrSubstring:
          this.UseHelper(CppHelpers.StrSubstring);
          return args.Length == 2
            ? $"[&]() {{ const auto& ply_s = {args[0]}; const std::int32_t ply_i = {args[1]}; return {CppHelpers.StrSubstring}(ply_s, ply_i, static_cast<std::int32_t>(ply_s.size()) - ply_i); }}()"
            : $"{CppHelpers.StrSubstring}({args[0]}, {args[1]}, {args[2]})";
        case IrIntrinsicOp.StrEquals:
          return $"({args[0]} == {args[1]})";
        case IrIntrinsicOp.StrCompareOrdinal:
          this.UseHelper(CppHelpers.StrCompare);
          return $"{CppHelpers.StrCompare}({args[0]}, {args[1]})";
        case IrIntrinsicOp.StrIndexOf:
          this.UseHelper(CppHelpers.StrIndexOf);
          return args.Length == 2
            ? $"{CppHelpers.StrIndexOf}({args[0]}, {AsString(1)}, 0)"
            : $"{CppHelpers.StrIndexOf}({args[0]}, {AsString(1)}, {args[2]})";
        case IrIntrinsicOp.StrLastIndexOf:
          this.UseHelper(CppHelpers.StrLastIndexOf);
          return args.Length == 2
            ? $"[&]() {{ const auto& ply_s = {args[0]}; return {CppHelpers.StrLastIndexOf}(ply_s, {AsString(1)}, static_cast<std::int32_t>(ply_s.size())); }}()"
            : $"{CppHelpers.StrLastIndexOf}({args[0]}, {AsString(1)}, {args[2]})";
        case IrIntrinsicOp.StrStartsWith:
          this.UseHelper(CppHelpers.StrStartsWith);
          return $"{CppHelpers.StrStartsWith}({args[0]}, {AsString(1)})";
        case IrIntrinsicOp.StrEndsWith:
          this.UseHelper(CppHelpers.StrEndsWith);
          return $"{CppHelpers.StrEndsWith}({args[0]}, {AsString(1)})";
        case IrIntrinsicOp.StrContains:
          return $"(({args[0]}).find({AsString(1)}) != std::string::npos)";
        case IrIntrinsicOp.StrReplaceAll:
          this.UseHelper(CppHelpers.StrReplaceAll);
          return $"{CppHelpers.StrReplaceAll}({args[0]}, {AsString(1)}, {AsString(2)})";
        case IrIntrinsicOp.StrReplaceFirst:
          this.UseHelper(CppHelpers.StrReplaceFirst);
          return $"{CppHelpers.StrReplaceFirst}({args[0]}, {args[1]}, {args[2]})";
        case IrIntrinsicOp.StrSplit:
          this.UseHelper(CppHelpers.StrSplit);
          return $"{CppHelpers.StrSplit}({args[0]}, {AsString(1)}, {args[2]})";
        case IrIntrinsicOp.StrJoin:
          this.UseHelper(CppHelpers.StrJoin);
          return $"{CppHelpers.StrJoin}({args[0]}, {args[1]})";
        case IrIntrinsicOp.StrTrim:
          this.UseHelper(CppHelpers.StrTrim);
          return $"{CppHelpers.StrTrim}({args[0]}, true, true)";
        case IrIntrinsicOp.StrTrimStart:
          this.UseHelper(CppHelpers.StrTrim);
          return $"{CppHelpers.StrTrim}({args[0]}, true, false)";
        case IrIntrinsicOp.StrTrimEnd:
          this.UseHelper(CppHelpers.StrTrim);
          return $"{CppHelpers.StrTrim}({args[0]}, false, true)";
        case IrIntrinsicOp.StrPadStart:
          this.UseHelper(CppHelpers.StrPad);
          return $"{CppHelpers.StrPad}({args[0]}, {args[1]}, {args[2]}, true)";
        default:
          this.UseHelper(CppHelpers.StrPad);
          return $"{CppHelpers.StrPad}({args[0]}, {args[1]}, {args[2]}, false)";
      }
    }

    private string EmitEnumIntrinsic(IrIntrinsic intrinsic, string[] args) {
      var enumType = (intrinsic.Arguments[0].Type.EnumType ?? intrinsic.Type.EnumType)!;
      var enumName = this._typeNames[enumType];
      var underlyingCpp = this.CppType(enumType.Underlying, inHeader: false);
      var memberNames = this._enumMembers[enumType];

      switch (intrinsic.Op) {
        case IrIntrinsicOp.EnumHasFlag:
          return $"((static_cast<{underlyingCpp}>({args[0]}) & static_cast<{underlyingCpp}>({args[1]})) == static_cast<{underlyingCpp}>({args[1]}))";
        case IrIntrinsicOp.EnumIsDefined: {
          var checks = enumType.Members.Select(m => $"ply_e == {enumName}::{memberNames[m]}");
          return $"[&]() {{ auto ply_e = {args[0]}; return {string.Join(" || ", checks)}; }}()";
        }
        case IrIntrinsicOp.EnumNames:
          this._sourceIncludes.Add("<string>");
          this._sourceIncludes.Add("<vector>");
          return $"std::vector<std::string>{{{string.Join(", ", enumType.Ordered.Select(m => CppNaming.StringLiteral(m.Name)))}}}";
        case IrIntrinsicOp.EnumValues:
          this._sourceIncludes.Add("<vector>");
          return $"std::vector<{enumName}>{{{string.Join(", ", enumType.Ordered.Select(m => $"{enumName}::{memberNames[m]}"))}}}";
        case IrIntrinsicOp.EnumToString: {
          this._sourceIncludes.Add("<string>");
          var cases = string.Join(" ", enumType.Members.Select(m => $"case {enumName}::{memberNames[m]}: return {CppNaming.StringLiteral(m.Name)};"));
          return $"[&]() -> std::string {{ auto ply_e = {args[0]}; switch (ply_e) {{ {cases} default: return std::to_string(static_cast<{underlyingCpp}>(ply_e)); }} }}()";
        }
        default: {
          this._sourceIncludes.Add("<stdexcept>");
          this._sourceIncludes.Add("<string>");
          var checks = string.Join(" ", enumType.Members.Select(m => $"if (ply_s == {CppNaming.StringLiteral(m.Name)}) return {enumName}::{memberNames[m]};"));
          return $"[&]() -> {enumName} {{ const auto& ply_s = {args[0]}; {checks} throw std::invalid_argument(\"unknown enum member\"); }}()";
        }
      }
    }

    private string EmitCollection(IrCollection collection) {
      var args = collection.Arguments.Select(this.EmitExpr).ToArray();
      switch (collection.Op) {
        case IrCollectionOp.ListNew:
          return $"{this.CppType(collection.Type, inHeader: false)}{{}}";
        case IrCollectionOp.ListOf: {
          var elemCpp = this.CppType(collection.Type.Element!, inHeader: false);
          this._sourceIncludes.Add("<vector>");
          return $"std::vector<{elemCpp}>{{{string.Join(", ", args)}}}";
        }
        case IrCollectionOp.ListFromArray or IrCollectionOp.ListCopyToArray:
          return $"{this.CppType(collection.Type, inHeader: false)}({args[0]})";
        case IrCollectionOp.ListCount:
          this._sourceIncludes.Add("<cstdint>");
          return $"static_cast<std::int32_t>(({args[0]}).size())";
        case IrCollectionOp.ListGet:
          this.UseHelper(CppHelpers.ArrayAt);
          return $"{CppHelpers.ArrayAt}({args[0]}, {args[1]})";
        case IrCollectionOp.ListSet:
          this.UseHelper(CppHelpers.ArrayAt);
          return $"({CppHelpers.ArrayAt}({args[0]}, {args[1]}) = {args[2]})";
        case IrCollectionOp.ListAdd:
          return $"({args[0]}).push_back({args[1]})";
        case IrCollectionOp.ListInsert:
          this.UseHelper(CppHelpers.ListInsert);
          return $"{CppHelpers.ListInsert}({args[0]}, {args[1]}, {args[2]})";
        case IrCollectionOp.ListRemoveAt:
          this.UseHelper(CppHelpers.ListRemoveAt);
          return $"{CppHelpers.ListRemoveAt}({args[0]}, {args[1]})";
        case IrCollectionOp.ListRemoveLast:
          this.UseHelper(CppHelpers.ListRemoveLast);
          return $"{CppHelpers.ListRemoveLast}({args[0]})";
        case IrCollectionOp.ListClear:
          return $"({args[0]}).clear()";
        case IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero:
          this.UseHelper(CppHelpers.ListIndexOf);
          return $"({CppHelpers.ListIndexOf}({args[0]}, {args[1]}) >= 0)";
        case IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict:
          this.UseHelper(CppHelpers.ListIndexOf);
          return $"{CppHelpers.ListIndexOf}({args[0]}, {args[1]})";
        default:
          return this.ReportUnsupportedConstruct(collection.Op.ToString());
      }
    }

    private string EmitGenericCompare(IrGenericCompare cmp) {
      this.UseHelper(CppHelpers.GenericCompare);
      return $"{CppHelpers.GenericCompare}({this.EmitExpr(cmp.Left)}, {this.EmitExpr(cmp.Right)})";
    }

    private string EmitForeignCall(IrForeignCall call) {
      var fn = this._foreignMap[call.Function];
      var pre = new System.Text.StringBuilder();
      var post = new System.Text.StringBuilder();
      var args = new string[call.Arguments.Count];

      for (var i = 0; i < call.Arguments.Count; ++i) {
        var param = fn.Parameters[i];
        var raw = this.EmitExpr(call.Arguments[i]);
        args[i] = this.MarshalForeignArg(param, raw, pre, post);
      }

      var rawCall = $"{fn.Symbol}({string.Join(", ", args)})";
      var marshalledResult = call.Type == IrType.Void
        ? rawCall
        : this.MarshalForeignResult(call.Type, fn.Result, rawCall);

      if (pre.Length == 0 && post.Length == 0)
        return marshalledResult;

      if (call.Type == IrType.Void)
        return $"[&]() {{ {pre}{rawCall}; {post}}}()";

      var retVar = this.Temp("ply_fr");
      return $"[&]() {{ {pre}auto {retVar} = {marshalledResult}; {post}return {retVar}; }}()";
    }

    private string MarshalForeignArg(IrForeignParameter param, string expr, System.Text.StringBuilder pre, System.Text.StringBuilder post) {
      var marshal = param.Marshal;
      switch (marshal.Kind) {
        case IrMarshalKind.Blittable:
          if (marshal.NativeSize) {
            this._sourceIncludes.Add("<cstdint>");
            var native = param.Type == IrType.I64 ? "std::intptr_t" : "std::uintptr_t";
            return $"static_cast<{native}>({expr})";
          }
          return expr;

        case IrMarshalKind.Bool: {
          var cType = this.ForeignCppType(param.Type, marshal, inHeader: false);
          return $"static_cast<{cType}>(({expr}) ? 1 : 0)";
        }

        case IrMarshalKind.String:
          if (marshal.Encoding == IrStringEncoding.Unicode) {
            this.UseHelper(CppHelpers.ToUtf16);
            return $"{CppHelpers.ToUtf16}({expr}).c_str()";
          }
          return $"({expr}).c_str()";

        case IrMarshalKind.Array:
          return $"({expr}).data()";

        case IrMarshalKind.ByRef:
          if (!marshal.NativeSize)
            return $"({expr}).data()";
          this._sourceIncludes.Add("<cstdint>");
          var elem = param.Type.Element!;
          var nativeElem = elem == IrType.I64 ? "std::intptr_t" : "std::uintptr_t";
          var arrVar = this.Temp("ply_br");
          var valVar = this.Temp("ply_bv");
          pre.Append("auto& ").Append(arrVar).Append(" = ").Append(expr).Append("; ")
            .Append(nativeElem).Append(' ').Append(valVar).Append(" = static_cast<").Append(nativeElem).Append(">(").Append(arrVar).Append("[0]); ");
          if (marshal.Direction != IrMarshalDirection.In)
            post.Append(arrVar).Append("[0] = static_cast<").Append(this.CppType(elem, inHeader: false)).Append(">(").Append(valVar).Append("); ");
          return $"&{valVar}";

        case IrMarshalKind.Struct:
          return expr;

        default:
          this._sourceIncludes.Add("<cstdint>");
          return $"static_cast<std::intptr_t>({expr})";
      }
    }

    private string MarshalForeignResult(IrType type, IrMarshal marshal, string callExpr) => marshal.Kind switch {
      IrMarshalKind.Bool => $"({callExpr} != 0)",
      IrMarshalKind.Handle => $"static_cast<std::int64_t>({callExpr})",
      IrMarshalKind.Blittable when marshal.NativeSize => $"static_cast<{this.CppType(type, inHeader: false)}>({callExpr})",
      _ => callExpr
    };

    private string UnwrapEnum(IrType type, string expr)
      => type.IsEnum ? $"static_cast<{this.CppType(type.Integral, inHeader: false)}>({expr})" : expr;

    private string WrapEnum(IrType type, string expr)
      => type.IsEnum ? $"static_cast<{this.CppType(type, inHeader: false)}>({expr})" : expr;

    private string FormatScalarConstant(IrType type, long value) {
      if (type.EnumType is { } enumType) {
        var enumName = this._typeNames[enumType];
        if (enumType.MemberOf(value) is { } member && this._enumMembers.TryGetValue(enumType, out var map) && map.TryGetValue(member, out var mname))
          return $"{enumName}::{mname}";
        return $"static_cast<{enumName}>({FormatIntLiteral(enumType.Underlying, value)})";
      }

      if (type == IrType.Bool)
        return value != 0 ? "true" : "false";

      if (type == IrType.F32) {
        var bits = unchecked((uint)value);
        var f = BitConverter.UInt32BitsToSingle(bits);
        if (float.IsNaN(f)) {
          this._sourceIncludes.Add("<limits>");
          return "std::numeric_limits<float>::quiet_NaN()";
        }
        if (float.IsPositiveInfinity(f)) {
          this._sourceIncludes.Add("<limits>");
          return "std::numeric_limits<float>::infinity()";
        }
        if (float.IsNegativeInfinity(f)) {
          this._sourceIncludes.Add("<limits>");
          return "(-std::numeric_limits<float>::infinity())";
        }
        if (f == 0f && (bits & 0x80000000u) != 0)
          return "-0.0f";
        var text = f.ToString("G9", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
          text += ".0";
        return text + "f";
      }

      if (type == IrType.F64) {
        var bits = unchecked((ulong)value);
        var d = BitConverter.UInt64BitsToDouble(bits);
        if (double.IsNaN(d)) {
          this._sourceIncludes.Add("<limits>");
          return "std::numeric_limits<double>::quiet_NaN()";
        }
        if (double.IsPositiveInfinity(d)) {
          this._sourceIncludes.Add("<limits>");
          return "std::numeric_limits<double>::infinity()";
        }
        if (double.IsNegativeInfinity(d)) {
          this._sourceIncludes.Add("<limits>");
          return "(-std::numeric_limits<double>::infinity())";
        }
        if (d == 0d && (bits & 0x8000000000000000UL) != 0)
          return "-0.0";
        var text = d.ToString("G17", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
          text += ".0";
        return text;
      }

      this._sourceIncludes.Add("<cstdint>");
      return FormatIntLiteral(type, value);
    }

    private static string FormatIntLiteral(IrType type, long value) => type.Kind switch {
      IrTypeKind.I8 => $"static_cast<std::int8_t>({value.ToString(CultureInfo.InvariantCulture)})",
      IrTypeKind.U8 => $"static_cast<std::uint8_t>({(value & 0xFF).ToString(CultureInfo.InvariantCulture)}u)",
      IrTypeKind.I16 => $"static_cast<std::int16_t>({value.ToString(CultureInfo.InvariantCulture)})",
      IrTypeKind.U16 => $"static_cast<std::uint16_t>({(value & 0xFFFF).ToString(CultureInfo.InvariantCulture)}u)",
      IrTypeKind.I32 => value == int.MinValue ? "(-2147483647 - 1)" : value.ToString(CultureInfo.InvariantCulture),
      IrTypeKind.U32 => $"{unchecked((uint)value).ToString(CultureInfo.InvariantCulture)}u",
      IrTypeKind.I64 => value == long.MinValue ? "(-9223372036854775807LL - 1)" : $"{value.ToString(CultureInfo.InvariantCulture)}LL",
      _ => $"{unchecked((ulong)value).ToString(CultureInfo.InvariantCulture)}ULL"
    };

    private static bool TryComparisonOperator(IrBinaryOp op, out string symbol) {
      symbol = op switch {
        IrBinaryOp.Equal => "==",
        IrBinaryOp.NotEqual => "!=",
        IrBinaryOp.Less => "<",
        IrBinaryOp.LessOrEqual => "<=",
        IrBinaryOp.Greater => ">",
        IrBinaryOp.GreaterOrEqual => ">=",
        _ => ""
      };
      return symbol.Length > 0;
    }

  }

}
