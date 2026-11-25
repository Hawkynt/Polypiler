using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Jvm.Bytecode;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm.Lowering;

internal sealed class JvmIntrinsics {

  private readonly Dictionary<string, IrFunction> _helpers = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Func<IReadOnlyList<IrExpr>, IrExpr>> _staticTable;
  private readonly Dictionary<string, Func<IrExpr, IReadOnlyList<IrExpr>, IrExpr>> _virtualTable;

  public JvmIntrinsics() {
    this._staticTable = new(StringComparer.Ordinal) {
      ["java/lang/Math.abs(I)I"] = a => this.CallAbsInt(IrType.I32, a[0]),
      ["java/lang/Math.abs(J)J"] = a => this.CallAbsInt(IrType.I64, a[0]),
      ["java/lang/Math.abs(F)F"] = a => new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F32, [a[0]]),
      ["java/lang/Math.abs(D)D"] = a => new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [a[0]]),
      ["java/lang/Math.min(II)I"] = a => new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [a[0], a[1]]),
      ["java/lang/Math.min(JJ)J"] = a => new IrIntrinsic(IrIntrinsicOp.Min, IrType.I64, [a[0], a[1]]),
      ["java/lang/Math.min(FF)F"] = a => new IrIntrinsic(IrIntrinsicOp.Min, IrType.F32, [a[0], a[1]]),
      ["java/lang/Math.min(DD)D"] = a => new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [a[0], a[1]]),
      ["java/lang/Math.max(II)I"] = a => new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [a[0], a[1]]),
      ["java/lang/Math.max(JJ)J"] = a => new IrIntrinsic(IrIntrinsicOp.Max, IrType.I64, [a[0], a[1]]),
      ["java/lang/Math.max(FF)F"] = a => new IrIntrinsic(IrIntrinsicOp.Max, IrType.F32, [a[0], a[1]]),
      ["java/lang/Math.max(DD)D"] = a => new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [a[0], a[1]]),
      ["java/lang/Math.sqrt(D)D"] = a => new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [a[0]]),
      ["java/lang/Math.floor(D)D"] = a => new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [a[0]]),
      ["java/lang/Math.ceil(D)D"] = a => new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [a[0]]),
      ["java/lang/Math.rint(D)D"] = a => new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [a[0]]),
      ["java/lang/Math.copySign(DD)D"] = a => this.CallCopySign(IrType.F64, a[0], a[1]),
      ["java/lang/Math.copySign(FF)F"] = a => this.CallCopySign(IrType.F32, a[0], a[1]),
      ["java/lang/Math.floorDiv(II)I"] = a => this.CallFloorDiv(IrType.I32, a[0], a[1]),
      ["java/lang/Math.floorDiv(JJ)J"] = a => this.CallFloorDiv(IrType.I64, a[0], a[1]),
      ["java/lang/Math.floorDiv(JI)J"] = a => this.CallFloorDiv(IrType.I64, a[0], new IrConvert(IrType.I64, a[1])),
      ["java/lang/Math.floorMod(II)I"] = a => this.CallFloorMod(IrType.I32, a[0], a[1]),
      ["java/lang/Math.floorMod(JJ)J"] = a => this.CallFloorMod(IrType.I64, a[0], a[1]),
      ["java/lang/Math.floorMod(JI)I"] = a => new IrConvert(IrType.I32, this.CallFloorMod(IrType.I64, a[0], new IrConvert(IrType.I64, a[1]))),
      ["java/lang/Integer.rotateLeft(II)I"] = a => this.CallRotate(IrType.I32, left: true, a[0], a[1]),
      ["java/lang/Integer.rotateRight(II)I"] = a => this.CallRotate(IrType.I32, left: false, a[0], a[1]),
      ["java/lang/Integer.numberOfLeadingZeros(I)I"] = a => this.CallLeadingZeros(IrType.I32, a[0]),
      ["java/lang/Integer.numberOfTrailingZeros(I)I"] = a => this.CallTrailingZeros(IrType.I32, a[0]),
      ["java/lang/Integer.bitCount(I)I"] = a => new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [a[0]]),
      ["java/lang/Integer.divideUnsigned(II)I"] = a => new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Divide, new IrConvert(IrType.U32, a[0]), new IrConvert(IrType.U32, a[1]))),
      ["java/lang/Integer.remainderUnsigned(II)I"] = a => new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Remainder, new IrConvert(IrType.U32, a[0]), new IrConvert(IrType.U32, a[1]))),
      ["java/lang/Integer.compareUnsigned(II)I"] = a => this.CallCompareUnsigned(IrType.I32, IrType.U32, a[0], a[1]),
      ["java/lang/Long.rotateLeft(JI)J"] = a => this.CallRotate(IrType.I64, left: true, a[0], a[1]),
      ["java/lang/Long.rotateRight(JI)J"] = a => this.CallRotate(IrType.I64, left: false, a[0], a[1]),
      ["java/lang/Long.numberOfLeadingZeros(J)I"] = a => this.CallLeadingZeros(IrType.I64, a[0]),
      ["java/lang/Long.numberOfTrailingZeros(J)I"] = a => this.CallTrailingZeros(IrType.I64, a[0]),
      ["java/lang/Long.bitCount(J)I"] = a => new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [a[0]]),
      ["java/lang/Long.divideUnsigned(JJ)J"] = a => new IrConvert(IrType.I64, new IrBinary(IrBinaryOp.Divide, new IrConvert(IrType.U64, a[0]), new IrConvert(IrType.U64, a[1]))),
      ["java/lang/Long.remainderUnsigned(JJ)J"] = a => new IrConvert(IrType.I64, new IrBinary(IrBinaryOp.Remainder, new IrConvert(IrType.U64, a[0]), new IrConvert(IrType.U64, a[1]))),
      ["java/lang/Long.compareUnsigned(JJ)I"] = a => this.CallCompareUnsigned(IrType.I64, IrType.U64, a[0], a[1]),
      ["java/lang/Double.isNaN(D)Z"] = a => this.CallFloatClassify(IrType.F64, "isnan", a[0]),
      ["java/lang/Double.isInfinite(D)Z"] = a => this.CallFloatClassify(IrType.F64, "isinf", a[0]),
      ["java/lang/Double.isFinite(D)Z"] = a => this.CallFloatClassify(IrType.F64, "isfinite", a[0]),
      ["java/lang/Double.doubleToRawLongBits(D)J"] = a => new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [a[0]]),
      ["java/lang/Double.doubleToLongBits(D)J"] = a => this.CallCanonicalFloatBits(IrType.F64, IrType.I64, 0x7FF8_0000_0000_0000L, a[0]),
      ["java/lang/Double.longBitsToDouble(J)D"] = a => new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [a[0]]),
      ["java/lang/Float.isNaN(F)Z"] = a => this.CallFloatClassify(IrType.F32, "isnan", a[0]),
      ["java/lang/Float.isInfinite(F)Z"] = a => this.CallFloatClassify(IrType.F32, "isinf", a[0]),
      ["java/lang/Float.isFinite(F)Z"] = a => this.CallFloatClassify(IrType.F32, "isfinite", a[0]),
      ["java/lang/Float.floatToRawIntBits(F)I"] = a => new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [a[0]]),
      ["java/lang/Float.floatToIntBits(F)I"] = a => this.CallCanonicalFloatBits(IrType.F32, IrType.I32, 0x7FC0_0000, a[0]),
      ["java/lang/Float.intBitsToFloat(I)F"] = a => new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [a[0]])
    };

    this._virtualTable = new(StringComparer.Ordinal) {
      ["java/lang/String.length()I"] = (r, _) => new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [r]),
      ["java/lang/String.isEmpty()Z"] = (r, _) => BoolToInt(new IrBinary(IrBinaryOp.Equal, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [r]), new IrConst(IrType.I32, 0))),
      ["java/lang/String.equals(Ljava/lang/Object;)Z"] = (r, a) => BoolToInt(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [r, a[0]])),
      ["java/lang/String.charAt(I)C"] = (r, a) => new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [r, a[0]]))
    };
  }

  public IReadOnlyCollection<IrFunction> Helpers => this._helpers.Values;

  public bool TryLowerStatic(string owner, string name, string descriptor, IReadOnlyList<IrExpr> args, out IrExpr? result) {
    if (this._staticTable.TryGetValue($"{owner}.{name}{descriptor}", out var handler)) {
      result = handler(args);
      return true;
    }

    if (owner == "java/lang/System" && name == "arraycopy" && descriptor == "(Ljava/lang/Object;ILjava/lang/Object;II)V")
      return this.TryLowerArrayCopy(args, out result);

    if (owner == "java/util/Arrays")
      return this.TryLowerArrays(name, args, out result);

    result = null;
    return false;
  }

  public bool TryLowerVirtual(string owner, string name, string descriptor, IrExpr receiver, IReadOnlyList<IrExpr> args, out IrExpr? result) {
    if (this._virtualTable.TryGetValue($"{owner}.{name}{descriptor}", out var handler)) {
      result = handler(receiver, args);
      return true;
    }

    if (name == "getMessage" && descriptor == "()Ljava/lang/String;"
        && receiver.Type.ClassType is { } cls && IrExceptionClass.IsException(cls)) {
      result = new IrFieldLoad(receiver, IrExceptionClass.Message);
      return true;
    }

    result = null;
    return false;
  }

  public IrExpr LowerSafeDivide(IrType type, IrExpr left, IrExpr right, bool remainder) {
    var name = (remainder ? "__jvm_rem_" : "__jvm_div_") + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var a = new IrLocal("a", type);
      var b = new IrLocal("b", type);
      var negOne = new IrConst(type, -1);
      IrExpr special = remainder ? new IrConst(type, 0) : new IrUnary(IrUnaryOp.Negate, new IrLocalRef(a));
      var normal = new IrBinary(remainder ? IrBinaryOp.Remainder : IrBinaryOp.Divide, new IrLocalRef(a), new IrLocalRef(b));
      var body = new IrBlock([
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrLocalRef(b), negOne),
          new IrBlock([new IrReturn(special)]),
          null),
        new IrReturn(normal)
      ]);
      this._helpers[name] = new IrFunction(name, false, type, [a, b], [], body);
    }

    return new IrCall(name, type, [left, right]);
  }

  private bool TryLowerArrayCopy(IReadOnlyList<IrExpr> args, out IrExpr? result) {
    var arrType = args[0].Type;
    var elemType = arrType.Element!;
    var name = "__jvm_arraycopy_" + JvmDescriptor.TypeTag(elemType);
    if (!this._helpers.ContainsKey(name)) {
      var src = new IrLocal("src", arrType);
      var srcPos = new IrLocal("src_pos", IrType.I32);
      var dest = new IrLocal("dest", arrType);
      var destPos = new IrLocal("dest_pos", IrType.I32);
      var len = new IrLocal("length", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      var zero = new IrConst(IrType.I32, 0);
      var one = new IrConst(IrType.I32, 1);

      var badBounds = new IrBinary(IrBinaryOp.LogicalOr,
        new IrBinary(IrBinaryOp.LogicalOr,
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(srcPos), zero),
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(destPos), zero)),
        new IrBinary(IrBinaryOp.LogicalOr,
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(len), zero),
          new IrBinary(IrBinaryOp.LogicalOr,
            new IrBinary(IrBinaryOp.Greater,
              new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I64, new IrLocalRef(srcPos)), new IrConvert(IrType.I64, new IrLocalRef(len))),
              new IrConvert(IrType.I64, new IrArrayLength(new IrLocalRef(src)))),
            new IrBinary(IrBinaryOp.Greater,
              new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I64, new IrLocalRef(destPos)), new IrConvert(IrType.I64, new IrLocalRef(len))),
              new IrConvert(IrType.I64, new IrArrayLength(new IrLocalRef(dest)))))));

      var throwOob = new IrThrow(new IrNew(
        IrType.Object(IrExceptionClass.IndexOutOfRangeException),
        IrExceptionClass.Constructor(IrExceptionClass.IndexOutOfRangeException, 0),
        []));

      var locals = new List<IrLocal> { i };
      var stmts = new List<IrStmt> {
        new IrIf(badBounds, new IrBlock([throwOob]), null),
        new IrDeclare(i, zero)
      };

      if (elemType.IsNumeric) {
        var tmp = new IrLocal("tmp", arrType);
        locals.Add(tmp);
        stmts.Add(new IrDeclare(tmp, new IrNewArray(arrType, new IrLocalRef(len))));
        stmts.Add(new IrWhile(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(len)),
          new IrBlock([
            new IrStore(new IrLocalRef(tmp), new IrLocalRef(i), new IrArrayLoad(new IrLocalRef(src), new IrBinary(IrBinaryOp.Add, new IrLocalRef(srcPos), new IrLocalRef(i)))),
            new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), one))
          ])));
        stmts.Add(new IrAssign(i, zero));
        stmts.Add(new IrWhile(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(len)),
          new IrBlock([
            new IrStore(new IrLocalRef(dest), new IrBinary(IrBinaryOp.Add, new IrLocalRef(destPos), new IrLocalRef(i)), new IrArrayLoad(new IrLocalRef(tmp), new IrLocalRef(i))),
            new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), one))
          ])));
      } else {
        stmts.Add(new IrWhile(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(len)),
          new IrBlock([
            new IrStore(new IrLocalRef(dest), new IrBinary(IrBinaryOp.Add, new IrLocalRef(destPos), new IrLocalRef(i)),
              new IrArrayLoad(new IrLocalRef(src), new IrBinary(IrBinaryOp.Add, new IrLocalRef(srcPos), new IrLocalRef(i)))),
            new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), one))
          ])));
      }

      this._helpers[name] = new IrFunction(name, false, IrType.Void, [src, srcPos, dest, destPos, len], locals, new IrBlock(stmts));
    }

    result = new IrCall(name, IrType.Void, args);
    return true;
  }

  private bool TryLowerArrays(string name, IReadOnlyList<IrExpr> args, out IrExpr? result) {
    result = null;
    if (name == "fill" && args.Count is 2 or 4) {
      var arrType = args[0].Type;
      var elemType = arrType.Element!;
      var valArg = NarrowIfNeeded(args[^1], elemType);
      var range = args.Count == 4;
      var helperName = (range ? "__jvm_fill_range_" : "__jvm_fill_") + JvmDescriptor.TypeTag(elemType);
      if (!this._helpers.ContainsKey(helperName)) {
        var arr = new IrLocal("a", arrType);
        var val = new IrLocal("v", elemType);
        var i = new IrLocal("i", IrType.I32);
        var one = new IrConst(IrType.I32, 1);
        if (range) {
          var from = new IrLocal("from", IrType.I32);
          var to = new IrLocal("to", IrType.I32);
          var body = new IrBlock([
            new IrDeclare(i, new IrLocalRef(from)),
            new IrWhile(
              new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(to)),
              new IrBlock([
                new IrStore(new IrLocalRef(arr), new IrLocalRef(i), new IrLocalRef(val)),
                new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), one))
              ]))
          ]);
          this._helpers[helperName] = new IrFunction(helperName, false, IrType.Void, [arr, from, to, val], [i], body);
        } else {
          var body = new IrBlock([
            new IrDeclare(i, new IrConst(IrType.I32, 0)),
            new IrWhile(
              new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrArrayLength(new IrLocalRef(arr))),
              new IrBlock([
                new IrStore(new IrLocalRef(arr), new IrLocalRef(i), new IrLocalRef(val)),
                new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), one))
              ]))
          ]);
          this._helpers[helperName] = new IrFunction(helperName, false, IrType.Void, [arr, val], [i], body);
        }
      }

      result = range
        ? new IrCall(helperName, IrType.Void, [args[0], args[1], args[2], valArg])
        : new IrCall(helperName, IrType.Void, [args[0], valArg]);
      return true;
    }

    if (name == "copyOf" && args.Count == 2 && args[0].Type.Element!.IsNumeric) {
      var arrType = args[0].Type;
      var elemType = arrType.Element!;
      var helperName = "__jvm_copyof_" + JvmDescriptor.TypeTag(elemType);
      if (!this._helpers.ContainsKey(helperName)) {
        var orig = new IrLocal("orig", arrType);
        var newLen = new IrLocal("new_len", IrType.I32);
        var copy = new IrLocal("copy", arrType);
        var limit = new IrLocal("limit", IrType.I32);
        var i = new IrLocal("i", IrType.I32);
        var body = new IrBlock([
          new IrDeclare(copy, new IrNewArray(arrType, new IrLocalRef(newLen))),
          new IrDeclare(limit, new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [new IrArrayLength(new IrLocalRef(orig)), new IrLocalRef(newLen)])),
          new IrDeclare(i, new IrConst(IrType.I32, 0)),
          new IrWhile(
            new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(limit)),
            new IrBlock([
              new IrStore(new IrLocalRef(copy), new IrLocalRef(i), new IrArrayLoad(new IrLocalRef(orig), new IrLocalRef(i))),
              new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))
            ])),
          new IrReturn(new IrLocalRef(copy))
        ]);
        this._helpers[helperName] = new IrFunction(helperName, false, arrType, [orig, newLen], [copy, limit, i], body);
      }

      result = new IrCall(helperName, arrType, [args[0], args[1]]);
      return true;
    }

    return false;
  }

  private IrExpr CallAbsInt(IrType type, IrExpr arg) {
    var name = "__jvm_abs_" + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var body = new IrBlock([
        new IrReturn(new IrConditional(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(x), new IrConst(type, 0)),
          new IrUnary(IrUnaryOp.Negate, new IrLocalRef(x)),
          new IrLocalRef(x)))
      ]);
      this._helpers[name] = new IrFunction(name, false, type, [x], [], body);
    }

    return new IrCall(name, type, [arg]);
  }

  private IrExpr CallCopySign(IrType floatType, IrExpr mag, IrExpr sign) {
    var bitsType = floatType == IrType.F64 ? IrType.I64 : IrType.I32;
    var magMask = floatType == IrType.F64 ? 0x7FFF_FFFF_FFFF_FFFFL : 0x7FFF_FFFFL;
    var signMask = floatType == IrType.F64 ? long.MinValue : (long)int.MinValue;
    var name = "__jvm_copysign_" + JvmDescriptor.TypeTag(floatType);
    if (!this._helpers.ContainsKey(name)) {
      var m = new IrLocal("mag", floatType);
      var s = new IrLocal("sign", floatType);
      var mb = new IrBinary(IrBinaryOp.And, new IrIntrinsic(IrIntrinsicOp.FloatBits, bitsType, [new IrLocalRef(m)]), new IrConst(bitsType, magMask));
      var sb = new IrBinary(IrBinaryOp.And, new IrIntrinsic(IrIntrinsicOp.FloatBits, bitsType, [new IrLocalRef(s)]), new IrConst(bitsType, signMask));
      var body = new IrBlock([
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, floatType, [new IrBinary(IrBinaryOp.Or, mb, sb)]))
      ]);
      this._helpers[name] = new IrFunction(name, false, floatType, [m, s], [], body);
    }

    return new IrCall(name, floatType, [mag, sign]);
  }

  private IrExpr CallFloorDiv(IrType type, IrExpr left, IrExpr right) {
    var name = "__jvm_floordiv_" + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var y = new IrLocal("y", type);
      var q = new IrLocal("q", type);
      var zero = new IrConst(type, 0);
      var one = new IrConst(type, 1);
      var negOne = new IrConst(type, -1);
      var body = new IrBlock([
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrLocalRef(y), negOne),
          new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, new IrLocalRef(x)))]),
          null),
        new IrDeclare(q, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(x), new IrLocalRef(y))),
        new IrIf(
          new IrBinary(IrBinaryOp.LogicalAnd,
            new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Xor, new IrLocalRef(x), new IrLocalRef(y)), zero),
            new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(q), new IrLocalRef(y)), new IrLocalRef(x))),
          new IrBlock([new IrAssign(q, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(q), one))]),
          null),
        new IrReturn(new IrLocalRef(q))
      ]);
      this._helpers[name] = new IrFunction(name, false, type, [x, y], [q], body);
    }

    return new IrCall(name, type, [left, right]);
  }

  private IrExpr CallFloorMod(IrType type, IrExpr left, IrExpr right) {
    var name = "__jvm_floormod_" + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var y = new IrLocal("y", type);
      var r = new IrLocal("r", type);
      var zero = new IrConst(type, 0);
      var negOne = new IrConst(type, -1);
      var body = new IrBlock([
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrLocalRef(y), negOne),
          new IrBlock([new IrReturn(zero)]),
          null),
        new IrDeclare(r, new IrBinary(IrBinaryOp.Remainder, new IrLocalRef(x), new IrLocalRef(y))),
        new IrIf(
          new IrBinary(IrBinaryOp.LogicalAnd,
            new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Xor, new IrLocalRef(x), new IrLocalRef(y)), zero),
            new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(r), zero)),
          new IrBlock([new IrAssign(r, new IrBinary(IrBinaryOp.Add, new IrLocalRef(r), new IrLocalRef(y)))]),
          null),
        new IrReturn(new IrLocalRef(r))
      ]);
      this._helpers[name] = new IrFunction(name, false, type, [x, y], [r], body);
    }

    return new IrCall(name, type, [left, right]);
  }

  private IrExpr CallRotate(IrType type, bool left, IrExpr value, IrExpr distance) {
    var name = (left ? "__jvm_rotl_" : "__jvm_rotr_") + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var d = new IrLocal("d", IrType.I32);
      var negD = new IrUnary(IrUnaryOp.Negate, new IrLocalRef(d));
      var expr = left
        ? new IrBinary(IrBinaryOp.Or,
          new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(x), new IrLocalRef(d)),
          new IrBinary(IrBinaryOp.ShiftRightLogical, new IrLocalRef(x), negD))
        : new IrBinary(IrBinaryOp.Or,
          new IrBinary(IrBinaryOp.ShiftRightLogical, new IrLocalRef(x), new IrLocalRef(d)),
          new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(x), negD));
      this._helpers[name] = new IrFunction(name, false, type, [x, d], [], new IrBlock([new IrReturn(expr)]));
    }

    return new IrCall(name, type, [value, distance]);
  }

  private IrExpr CallLeadingZeros(IrType type, IrExpr arg) {
    var bits = type == IrType.I64 ? 64 : 32;
    var signBit = type == IrType.I64 ? long.MinValue : (long)int.MinValue;
    var name = "__jvm_nlz_" + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var v = new IrLocal("v", type);
      var n = new IrLocal("n", IrType.I32);
      var body = new IrBlock([
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrLocalRef(x), new IrConst(type, 0)),
          new IrBlock([new IrReturn(new IrConst(IrType.I32, bits))]),
          null),
        new IrDeclare(v, new IrLocalRef(x)),
        new IrDeclare(n, new IrConst(IrType.I32, 0)),
        new IrWhile(
          new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.And, new IrLocalRef(v), new IrConst(type, signBit)), new IrConst(type, 0)),
          new IrBlock([
            new IrAssign(n, new IrBinary(IrBinaryOp.Add, new IrLocalRef(n), new IrConst(IrType.I32, 1))),
            new IrAssign(v, new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(v), new IrConst(IrType.I32, 1)))
          ])),
        new IrReturn(new IrLocalRef(n))
      ]);
      this._helpers[name] = new IrFunction(name, false, IrType.I32, [x], [v, n], body);
    }

    return new IrCall(name, IrType.I32, [arg]);
  }

  private IrExpr CallTrailingZeros(IrType type, IrExpr arg) {
    var bits = type == IrType.I64 ? 64 : 32;
    var name = "__jvm_ntz_" + JvmDescriptor.TypeTag(type);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", type);
      var v = new IrLocal("v", type);
      var n = new IrLocal("n", IrType.I32);
      var body = new IrBlock([
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrLocalRef(x), new IrConst(type, 0)),
          new IrBlock([new IrReturn(new IrConst(IrType.I32, bits))]),
          null),
        new IrDeclare(v, new IrLocalRef(x)),
        new IrDeclare(n, new IrConst(IrType.I32, 0)),
        new IrWhile(
          new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.And, new IrLocalRef(v), new IrConst(type, 1)), new IrConst(type, 0)),
          new IrBlock([
            new IrAssign(n, new IrBinary(IrBinaryOp.Add, new IrLocalRef(n), new IrConst(IrType.I32, 1))),
            new IrAssign(v, new IrBinary(IrBinaryOp.ShiftRightLogical, new IrLocalRef(v), new IrConst(IrType.I32, 1)))
          ])),
        new IrReturn(new IrLocalRef(n))
      ]);
      this._helpers[name] = new IrFunction(name, false, IrType.I32, [x], [v, n], body);
    }

    return new IrCall(name, IrType.I32, [arg]);
  }

  private IrExpr CallCompareUnsigned(IrType signedType, IrType unsignedType, IrExpr left, IrExpr right) {
    var name = "__jvm_cmpu_" + JvmDescriptor.TypeTag(signedType);
    if (!this._helpers.ContainsKey(name)) {
      var a = new IrLocal("a", signedType);
      var b = new IrLocal("b", signedType);
      var ua = new IrLocal("ua", unsignedType);
      var ub = new IrLocal("ub", unsignedType);
      var body = new IrBlock([
        new IrDeclare(ua, new IrConvert(unsignedType, new IrLocalRef(a))),
        new IrDeclare(ub, new IrConvert(unsignedType, new IrLocalRef(b))),
        new IrReturn(new IrConditional(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(ua), new IrLocalRef(ub)),
          new IrConst(IrType.I32, -1),
          new IrConditional(
            new IrBinary(IrBinaryOp.Greater, new IrLocalRef(ua), new IrLocalRef(ub)),
            new IrConst(IrType.I32, 1),
            new IrConst(IrType.I32, 0))))
      ]);
      this._helpers[name] = new IrFunction(name, false, IrType.I32, [a, b], [ua, ub], body);
    }

    return new IrCall(name, IrType.I32, [left, right]);
  }

  private IrExpr CallFloatClassify(IrType floatType, string kind, IrExpr arg) {
    var name = $"__jvm_{kind}_{JvmDescriptor.TypeTag(floatType)}";
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", floatType);
      var bitsType = floatType == IrType.F64 ? IrType.I64 : IrType.I32;
      var absMask = floatType == IrType.F64 ? 0x7FFF_FFFF_FFFF_FFFFL : 0x7FFF_FFFFL;
      var infBits = floatType == IrType.F64 ? 0x7FF0_0000_0000_0000L : 0x7F80_0000L;
      IrExpr pred = kind switch {
        "isnan" => new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(x), new IrLocalRef(x)),
        "isinf" => new IrBinary(IrBinaryOp.Equal,
          new IrBinary(IrBinaryOp.And, new IrIntrinsic(IrIntrinsicOp.FloatBits, bitsType, [new IrLocalRef(x)]), new IrConst(bitsType, absMask)),
          new IrConst(bitsType, infBits)),
        _ => new IrBinary(IrBinaryOp.Less,
          new IrBinary(IrBinaryOp.And, new IrIntrinsic(IrIntrinsicOp.FloatBits, bitsType, [new IrLocalRef(x)]), new IrConst(bitsType, absMask)),
          new IrConst(bitsType, infBits))
      };
      this._helpers[name] = new IrFunction(name, false, IrType.I32, [x], [], new IrBlock([new IrReturn(BoolToInt(pred))]));
    }

    return new IrCall(name, IrType.I32, [arg]);
  }

  private IrExpr CallCanonicalFloatBits(IrType floatType, IrType bitsType, long canonicalNaN, IrExpr arg) {
    var name = "__jvm_bits_" + JvmDescriptor.TypeTag(floatType);
    if (!this._helpers.ContainsKey(name)) {
      var x = new IrLocal("x", floatType);
      var body = new IrBlock([
        new IrReturn(new IrConditional(
          new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(x), new IrLocalRef(x)),
          new IrConst(bitsType, canonicalNaN),
          new IrIntrinsic(IrIntrinsicOp.FloatBits, bitsType, [new IrLocalRef(x)])))
      ]);
      this._helpers[name] = new IrFunction(name, false, bitsType, [x], [], body);
    }

    return new IrCall(name, bitsType, [arg]);
  }

  private static IrExpr BoolToInt(IrExpr condition)
    => new IrConditional(condition, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));

  private static IrExpr NarrowIfNeeded(IrExpr expr, IrType target)
    => expr.Type == target ? expr : new IrConvert(target, expr);

}
