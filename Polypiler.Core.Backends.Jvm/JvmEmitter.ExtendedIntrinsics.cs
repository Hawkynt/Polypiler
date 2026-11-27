using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    private static readonly int[] _sharedWhitespaceChars = [
      0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x00A0, 0x1680,
      0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007,
      0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000
    ];

    private string EnsureIsWsHelper() {
      const string name = "$isWs";
      if (!this._helperNames.TryAdd(name, name))
        return name;

      var code = new JvmCodeBuilder(this._pool, [VType.Int, VType.Int]);
      var nelLabel = code.DefineLabel();
      var bomLabel = code.DefineLabel();
      var wsLabel = code.DefineLabel();
      var nonWsLabel = code.DefineLabel();

      // if (ch == 0x0085) return !isEcma;
      code.EmitLoad(0, VType.Int);
      code.EmitConstInt(0x0085);
      code.EmitBranch(JvmOpcode.IfIcmpeq, nelLabel);

      // if (ch == 0xFEFF) return isEcma;
      code.EmitLoad(0, VType.Int);
      code.EmitConstInt(0xFEFF);
      code.EmitBranch(JvmOpcode.IfIcmpeq, bomLabel);

      // lookupswitch over the 24 shared whitespace characters
      var pairs = new (int Key, JvmLabel Target)[_sharedWhitespaceChars.Length];
      for (var i = 0; i < _sharedWhitespaceChars.Length; ++i)
        pairs[i] = (_sharedWhitespaceChars[i], wsLabel);
      code.EmitLoad(0, VType.Int);
      code.EmitLookupSwitch(nonWsLabel, pairs);

      code.MarkLabel(nelLabel);
      var nelTrue = code.DefineLabel();
      code.EmitLoad(1, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, nelTrue);
      code.EmitConstInt(0);
      code.EmitReturn(JvmOpcode.Ireturn);
      code.MarkLabel(nelTrue);
      code.EmitConstInt(1);
      code.EmitReturn(JvmOpcode.Ireturn);

      code.MarkLabel(bomLabel);
      code.EmitLoad(1, VType.Int);
      code.EmitReturn(JvmOpcode.Ireturn);

      code.MarkLabel(wsLabel);
      code.EmitConstInt(1);
      code.EmitReturn(JvmOpcode.Ireturn);

      code.MarkLabel(nonWsLabel);
      code.EmitConstInt(0);
      code.EmitReturn(JvmOpcode.Ireturn);

      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, "(CZ)Z", code.Build()));
      return name;
    }

    private string EnsureStrTrimHelper() {
      const string name = "$strTrim";
      if (!this._helperNames.TryAdd(name, name))
        return name;

      var isWs = this.EnsureIsWsHelper();
      var strType = VType.Object(this._pool.AddClass("java/lang/String"));
      var code = new JvmCodeBuilder(this._pool, [strType, VType.Int, VType.Int, VType.Int]);
      var firstSlot = code.AllocateLocal(VType.Int);
      var lastSlot = code.AllocateLocal(VType.Int);

      code.EmitConstInt(0);
      code.EmitStore(firstSlot, VType.Int);
      code.EmitLoad(0, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitStore(lastSlot, VType.Int);

      // Trim start loop
      var startDone = code.DefineLabel();
      var startLoop = code.DefineLabel();
      code.EmitLoad(2, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, startDone);
      code.MarkLabel(startLoop);
      code.EmitLoad(firstSlot, VType.Int);
      code.EmitLoad(lastSlot, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, startDone);
      code.EmitLoad(0, strType);
      code.EmitLoad(firstSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
      code.EmitLoad(1, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, isWs, "(CZ)Z", 2, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, startDone);
      code.EmitLoad(firstSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(firstSlot, VType.Int);
      code.EmitGoto(startLoop);
      code.MarkLabel(startDone);

      // Trim end loop
      var endDone = code.DefineLabel();
      var endLoop = code.DefineLabel();
      code.EmitLoad(3, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, endDone);
      code.MarkLabel(endLoop);
      code.EmitLoad(lastSlot, VType.Int);
      code.EmitLoad(firstSlot, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmple, endDone);
      code.EmitLoad(0, strType);
      code.EmitLoad(lastSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
      code.EmitLoad(1, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, isWs, "(CZ)Z", 2, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, endDone);
      code.EmitLoad(lastSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
      code.EmitStore(lastSlot, VType.Int);
      code.EmitGoto(endLoop);
      code.MarkLabel(endDone);

      code.EmitLoad(0, strType);
      code.EmitLoad(firstSlot, VType.Int);
      code.EmitLoad(lastSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(II)Ljava/lang/String;", 2, strType);
      code.EmitReturn(JvmOpcode.Areturn);

      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, "(Ljava/lang/String;ZZZ)Ljava/lang/String;", code.Build()));
      return name;
    }

    private string EnsureStrSplitHelper() {
      const string name = "$strSplit";
      if (!this._helperNames.TryAdd(name, name))
        return name;

      var strType = VType.Object(this._pool.AddClass("java/lang/String"));
      var listType = VType.Object(this._pool.AddClass("java/util/ArrayList"));
      var arrType = VType.Object(this._pool.AddClass("[Ljava/lang/String;"));
      var objType = VType.Object(this._pool.AddClass("java/lang/Object"));
      var code = new JvmCodeBuilder(this._pool, [strType, strType, VType.Int]);
      var listSlot = code.AllocateLocal(listType);
      var startSlot = code.AllocateLocal(VType.Int);
      var atSlot = code.AllocateLocal(VType.Int);
      var countSlot = code.AllocateLocal(VType.Int);
      var outSlot = code.AllocateLocal(arrType);

      code.EmitNew("java/util/ArrayList");
      code.EmitDup();
      code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
      code.EmitStore(listSlot, listType);

      var nonEmptySep = code.DefineLabel();
      var buildArr = code.DefineLabel();
      code.EmitLoad(1, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitBranch(JvmOpcode.Ifne, nonEmptySep);

      // Empty separator: one 1-char string per code unit
      code.EmitConstInt(0);
      code.EmitStore(startSlot, VType.Int);
      var charLoop = code.DefineLabel();
      code.MarkLabel(charLoop);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitLoad(0, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, buildArr);

      code.EmitLoad(listSlot, listType);
      code.EmitLoad(0, strType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/String", "valueOf", "(C)Ljava/lang/String;", 1, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
      code.EmitPop();

      code.EmitLoad(startSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(startSlot, VType.Int);
      code.EmitGoto(charLoop);

      // Non-empty separator
      code.MarkLabel(nonEmptySep);
      code.EmitConstInt(0);
      code.EmitStore(startSlot, VType.Int);
      var sepLoop = code.DefineLabel();
      var sepDone = code.DefineLabel();
      code.MarkLabel(sepLoop);
      code.EmitLoad(0, strType);
      code.EmitLoad(1, strType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "indexOf", "(Ljava/lang/String;I)I", 2, VType.Int);
      code.EmitStore(atSlot, VType.Int);
      code.EmitLoad(atSlot, VType.Int);
      code.EmitBranch(JvmOpcode.Iflt, sepDone);

      code.EmitLoad(listSlot, listType);
      code.EmitLoad(0, strType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitLoad(atSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(II)Ljava/lang/String;", 2, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
      code.EmitPop();

      code.EmitLoad(atSlot, VType.Int);
      code.EmitLoad(1, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(startSlot, VType.Int);
      code.EmitGoto(sepLoop);

      code.MarkLabel(sepDone);
      code.EmitLoad(listSlot, listType);
      code.EmitLoad(0, strType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(I)Ljava/lang/String;", 1, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
      code.EmitPop();

      // Build result String[count] respecting limit
      code.MarkLabel(buildArr);
      code.EmitLoad(listSlot, listType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
      code.EmitStore(countSlot, VType.Int);

      var noLimit = code.DefineLabel();
      code.EmitLoad(2, VType.Int);
      code.EmitBranch(JvmOpcode.Iflt, noLimit);
      code.EmitLoad(2, VType.Int);
      code.EmitLoad(countSlot, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, noLimit);
      code.EmitLoad(2, VType.Int);
      code.EmitStore(countSlot, VType.Int);
      code.MarkLabel(noLimit);

      code.EmitLoad(countSlot, VType.Int);
      code.EmitNewObjectArray("java/lang/String", "[Ljava/lang/String;");
      code.EmitStore(outSlot, arrType);
      code.EmitConstInt(0);
      code.EmitStore(startSlot, VType.Int);

      var copyLoop = code.DefineLabel();
      var copyDone = code.DefineLabel();
      code.MarkLabel(copyLoop);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitLoad(countSlot, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, copyDone);

      code.EmitLoad(outSlot, arrType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitLoad(listSlot, listType);
      code.EmitLoad(startSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "get", "(I)Ljava/lang/Object;", 1, objType);
      code.EmitCheckCast("java/lang/String");
      code.EmitOp(JvmOpcode.Aastore, 3);

      code.EmitLoad(startSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(startSlot, VType.Int);
      code.EmitGoto(copyLoop);

      code.MarkLabel(copyDone);
      code.EmitLoad(outSlot, arrType);
      code.EmitReturn(JvmOpcode.Areturn);

      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, "(Ljava/lang/String;Ljava/lang/String;I)[Ljava/lang/String;", code.Build()));
      return name;
    }

    private string EnsureFmaHelper(bool isDouble) {
      var name = isDouble ? "$fmaD" : "$fmaF";
      if (!this._helperNames.TryAdd(name, name))
        return name;

      var vtype = isDouble ? VType.Double : VType.Float;
      var desc = isDouble ? "(DDD)D" : "(FFF)F";
      var bdType = VType.Object(this._pool.AddClass("java/math/BigDecimal"));
      var code = new JvmCodeBuilder(this._pool, [vtype, vtype, vtype]);
      ushort xSlot = 0;
      ushort ySlot = isDouble ? (ushort)2 : (ushort)1;
      ushort zSlot = isDouble ? (ushort)4 : (ushort)2;

      var fallbackLabel = code.DefineLabel();
      var wrapperClass = isDouble ? "java/lang/Double" : "java/lang/Float";
      var isFiniteDesc = isDouble ? "(D)Z" : "(F)Z";

      // If any operand is non-finite, or x == 0 or y == 0, return x * y + z (preserves IEEE 754 NaN/Inf/signed-zero)
      foreach (var s in new[] { xSlot, ySlot, zSlot }) {
        code.EmitLoad(s, vtype);
        code.EmitInvoke(JvmOpcode.Invokestatic, wrapperClass, "isFinite", isFiniteDesc, 1, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, fallbackLabel);
      }

      foreach (var s in new[] { xSlot, ySlot }) {
        code.EmitLoad(s, vtype);
        if (isDouble) {
          code.EmitConstDoubleBits(0L);
          code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
        } else {
          code.EmitConstFloatBits(0);
          code.EmitOp(JvmOpcode.Fcmpl, 2, VType.Int);
        }
        code.EmitBranch(JvmOpcode.Ifeq, fallbackLabel);
      }

      // Exact BigDecimal(x).multiply(BigDecimal(y)).add(BigDecimal(z)).doubleValue()/floatValue()
      void EmitBigDecimal(ushort slot) {
        code.EmitNew("java/math/BigDecimal");
        code.EmitDup();
        code.EmitLoad(slot, vtype);
        if (!isDouble)
          code.EmitOp(JvmOpcode.F2d, 1, VType.Double);
        code.EmitInvoke(JvmOpcode.Invokespecial, "java/math/BigDecimal", "<init>", "(D)V", 1);
      }

      EmitBigDecimal(xSlot);
      EmitBigDecimal(ySlot);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/math/BigDecimal", "multiply", "(Ljava/math/BigDecimal;)Ljava/math/BigDecimal;", 1, bdType);
      EmitBigDecimal(zSlot);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/math/BigDecimal", "add", "(Ljava/math/BigDecimal;)Ljava/math/BigDecimal;", 1, bdType);
      if (isDouble) {
        code.EmitInvoke(JvmOpcode.Invokevirtual, "java/math/BigDecimal", "doubleValue", "()D", 0, VType.Double);
        code.EmitReturn(JvmOpcode.Dreturn);
      } else {
        code.EmitInvoke(JvmOpcode.Invokevirtual, "java/math/BigDecimal", "floatValue", "()F", 0, VType.Float);
        code.EmitReturn(JvmOpcode.Freturn);
      }

      code.MarkLabel(fallbackLabel);
      code.EmitLoad(xSlot, vtype);
      code.EmitLoad(ySlot, vtype);
      code.EmitOp(isDouble ? JvmOpcode.Dmul : JvmOpcode.Fmul, 2, vtype);
      code.EmitLoad(zSlot, vtype);
      code.EmitOp(isDouble ? JvmOpcode.Dadd : JvmOpcode.Fadd, 2, vtype);
      code.EmitReturn(isDouble ? JvmOpcode.Dreturn : JvmOpcode.Freturn);

      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, desc, code.Build()));
      return name;
    }

    private string EnsureEnumIsDefinedHelper(IrEnumType enumType) {
      if (this._enumIsDefinedHelpers.TryGetValue(enumType, out var existing))
        return existing;

      var name = "$enumIsDefined_" + this._enumIsDefinedHelpers.Count;
      this._enumIsDefinedHelpers[enumType] = name;

      var underlying = enumType.Underlying;
      var vtype = this._names.VerificationType(underlying, this._pool);
      var code = new JvmCodeBuilder(this._pool, [vtype]);
      if (underlying == IrType.U8) {
        code.EmitLoad(0, VType.Int);
        code.EmitConstInt(0xFF);
        code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        code.EmitStore(0, VType.Int);
      }

      var trueLabel = code.DefineLabel();
      var seen = new HashSet<long>();
      foreach (var member in enumType.Members) {
        if (!seen.Add(member.Value))
          continue;

        code.EmitLoad(0, vtype);
        EmitScalarConst(code, underlying, member.Value);
        if (underlying.IsLong) {
          code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          code.EmitBranch(JvmOpcode.Ifeq, trueLabel);
        } else {
          code.EmitBranch(JvmOpcode.IfIcmpeq, trueLabel);
        }
      }

      code.EmitConstInt(0);
      code.EmitReturn(JvmOpcode.Ireturn);
      code.MarkLabel(trueLabel);
      code.EmitConstInt(1);
      code.EmitReturn(JvmOpcode.Ireturn);

      var desc = $"({this._names.Descriptor(underlying)})Z";
      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, desc, code.Build()));
      return name;
    }

    private string EnsureEnumToStringHelper(IrEnumType enumType) {
      if (this._enumToStringHelpers.TryGetValue(enumType, out var existing))
        return existing;

      var name = "$enumToString_" + this._enumToStringHelpers.Count;
      this._enumToStringHelpers[enumType] = name;

      var underlying = enumType.Underlying;
      var vtype = this._names.VerificationType(underlying, this._pool);
      var strType = VType.Object(this._pool.AddClass("java/lang/String"));
      var code = new JvmCodeBuilder(this._pool, [vtype]);
      if (underlying == IrType.U8) {
        code.EmitLoad(0, VType.Int);
        code.EmitConstInt(0xFF);
        code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        code.EmitStore(0, VType.Int);
      }

      var ordered = enumType.Ordered;
      // 1. Exact member match
      foreach (var member in ordered) {
        var nextLabel = code.DefineLabel();
        code.EmitLoad(0, vtype);
        EmitScalarConst(code, underlying, member.Value);
        if (underlying.IsLong) {
          code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          code.EmitBranch(JvmOpcode.Ifne, nextLabel);
        } else {
          code.EmitBranch(JvmOpcode.IfIcmpne, nextLabel);
        }

        code.EmitConstString(member.Name);
        code.EmitReturn(JvmOpcode.Areturn);
        code.MarkLabel(nextLabel);
      }

      // 2. [Flags] greedy decomposition from largest unsigned value down
      if (enumType.IsFlags) {
        var isLong = underlying.IsLong;
        var restVType = isLong ? VType.Long : VType.Int;
        var restSlot = code.AllocateLocal(restVType);
        var resSlot = code.AllocateLocal(strType);
        var fallbackLabel = code.DefineLabel();

        code.EmitLoad(0, vtype);
        if (underlying == IrType.I8) {
          code.EmitConstInt(0xFF);
          code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        } else if (underlying == IrType.I16) {
          code.EmitConstInt(0xFFFF);
          code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
        code.EmitStore(restSlot, restVType);
        code.EmitNull();
        code.EmitStore(resSlot, strType);

        for (var i = ordered.Count - 1; i >= 0; --i) {
          var flag = enumType.Unsigned(ordered[i].Value);
          if (flag == 0)
            continue;

          var skipLabel = code.DefineLabel();
          var firstLabel = code.DefineLabel();
          var afterName = code.DefineLabel();

          code.EmitLoad(restSlot, restVType);
          if (isLong) {
            code.EmitConstLong(unchecked((long)flag));
            code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            code.EmitConstLong(unchecked((long)flag));
            code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
            code.EmitBranch(JvmOpcode.Ifne, skipLabel);

            code.EmitLoad(restSlot, VType.Long);
            code.EmitConstLong(unchecked((long)~flag));
            code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            code.EmitStore(restSlot, VType.Long);
          } else {
            code.EmitConstInt(unchecked((int)flag));
            code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            code.EmitConstInt(unchecked((int)flag));
            code.EmitBranch(JvmOpcode.IfIcmpne, skipLabel);

            code.EmitLoad(restSlot, VType.Int);
            code.EmitConstInt(unchecked((int)~(uint)flag));
            code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            code.EmitStore(restSlot, VType.Int);
          }

          code.EmitLoad(resSlot, strType);
          code.EmitBranch(JvmOpcode.Ifnull, firstLabel);
          code.EmitConstString(ordered[i].Name);
          code.EmitConstString(", ");
          code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", 1, strType);
          code.EmitLoad(resSlot, strType);
          code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", 1, strType);
          code.EmitStore(resSlot, strType);
          code.EmitGoto(afterName);

          code.MarkLabel(firstLabel);
          code.EmitConstString(ordered[i].Name);
          code.EmitStore(resSlot, strType);

          code.MarkLabel(afterName);
          code.MarkLabel(skipLabel);
        }

        code.EmitLoad(restSlot, restVType);
        if (isLong) {
          code.EmitConstLong(0L);
          code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
        }
        code.EmitBranch(JvmOpcode.Ifne, fallbackLabel);
        code.EmitLoad(resSlot, strType);
        code.EmitBranch(JvmOpcode.Ifnull, fallbackLabel);
        code.EmitLoad(resSlot, strType);
        code.EmitReturn(JvmOpcode.Areturn);

        code.MarkLabel(fallbackLabel);
      }

      // 3. Decimal fallback
      code.EmitLoad(0, vtype);
      switch (underlying.Kind) {
        case IrTypeKind.U32:
          code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "toUnsignedString", "(I)Ljava/lang/String;", 1, strType);
          break;
        case IrTypeKind.I64:
          code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "toString", "(J)Ljava/lang/String;", 1, strType);
          break;
        case IrTypeKind.U64:
          code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "toUnsignedString", "(J)Ljava/lang/String;", 1, strType);
          break;
        default:
          code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "toString", "(I)Ljava/lang/String;", 1, strType);
          break;
      }

      code.EmitReturn(JvmOpcode.Areturn);
      var desc = $"({this._names.Descriptor(underlying)})Ljava/lang/String;";
      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, desc, code.Build()));
      return name;
    }

    private string EnsureEnumParseHelper(IrEnumType enumType) {
      if (this._enumParseHelpers.TryGetValue(enumType, out var existing))
        return existing;

      var name = "$enumParse_" + this._enumParseHelpers.Count;
      this._enumParseHelpers[enumType] = name;

      var trimHelper = this.EnsureStrTrimHelper();
      var splitHelper = this.EnsureStrSplitHelper();
      var underlying = enumType.Underlying;
      var isLong = underlying.IsLong;
      var accVType = isLong ? VType.Long : VType.Int;
      var strType = VType.Object(this._pool.AddClass("java/lang/String"));
      var arrType = VType.Object(this._pool.AddClass("[Ljava/lang/String;"));

      var code = new JvmCodeBuilder(this._pool, [strType, VType.Int]);
      var trimmedSlot = code.AllocateLocal(strType);
      var c0Slot = code.AllocateLocal(VType.Int);
      var numStrSlot = code.AllocateLocal(strType);
      var posSlot = code.AllocateLocal(VType.Int);
      var partsSlot = code.AllocateLocal(arrType);
      var idxSlot = code.AllocateLocal(VType.Int);
      var partSlot = code.AllocateLocal(strType);
      var accSlot = code.AllocateLocal(accVType);

      var throwInvalidLabel = code.DefineLabel();
      var throwOverflowLabel = code.DefineLabel();
      var tryNumericLabel = code.DefineLabel();
      var parseNamesLabel = code.DefineLabel();

      // trimmed = $strTrim(text, false, true, false)
      code.EmitLoad(0, strType);
      code.EmitConstInt(0);
      code.EmitConstInt(1);
      code.EmitConstInt(0);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, trimHelper, "(Ljava/lang/String;ZZZ)Ljava/lang/String;", 4, strType);
      code.EmitStore(trimmedSlot, strType);

      code.EmitLoad(trimmedSlot, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitBranch(JvmOpcode.Ifeq, throwInvalidLabel);

      code.EmitLoad(trimmedSlot, strType);
      code.EmitConstInt(0);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
      code.EmitStore(c0Slot, VType.Int);

      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('-');
      code.EmitBranch(JvmOpcode.IfIcmpeq, tryNumericLabel);
      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('+');
      code.EmitBranch(JvmOpcode.IfIcmpeq, tryNumericLabel);
      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('0');
      code.EmitBranch(JvmOpcode.IfIcmplt, parseNamesLabel);
      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('9');
      code.EmitBranch(JvmOpcode.IfIcmpgt, parseNamesLabel);

      // Numeric parse path: verify numStr matches [+-]?[0-9]+
      code.MarkLabel(tryNumericLabel);
      code.EmitLoad(trimmedSlot, strType);
      code.EmitConstInt(0);
      code.EmitConstInt(0);
      code.EmitConstInt(1);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, trimHelper, "(Ljava/lang/String;ZZZ)Ljava/lang/String;", 4, strType);
      code.EmitStore(numStrSlot, strType);

      // pos = (c0 == '-' || c0 == '+') ? 1 : 0
      var signOne = code.DefineLabel();
      var signDone = code.DefineLabel();
      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('-');
      code.EmitBranch(JvmOpcode.IfIcmpeq, signOne);
      code.EmitLoad(c0Slot, VType.Int);
      code.EmitConstInt('+');
      code.EmitBranch(JvmOpcode.IfIcmpeq, signOne);
      code.EmitConstInt(0);
      code.EmitGoto(signDone);
      code.MarkLabel(signOne);
      code.EmitConstInt(1);
      code.MarkLabel(signDone);
      code.EmitStore(posSlot, VType.Int);

      code.EmitLoad(posSlot, VType.Int);
      code.EmitLoad(numStrSlot, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, parseNamesLabel);

      var digitLoop = code.DefineLabel();
      var digitsOk = code.DefineLabel();
      code.MarkLabel(digitLoop);
      code.EmitLoad(posSlot, VType.Int);
      code.EmitLoad(numStrSlot, strType);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, digitsOk);
      code.EmitLoad(numStrSlot, strType);
      code.EmitLoad(posSlot, VType.Int);
      code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
      code.EmitDup();
      code.EmitConstInt('0');
      var badDigitPop = code.DefineLabel();
      code.EmitBranch(JvmOpcode.IfIcmplt, badDigitPop);
      code.EmitConstInt('9');
      code.EmitBranch(JvmOpcode.IfIcmpgt, parseNamesLabel);
      code.EmitLoad(posSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(posSlot, VType.Int);
      code.EmitGoto(digitLoop);

      code.MarkLabel(badDigitPop);
      code.EmitPop();
      code.EmitGoto(parseNamesLabel);

      code.MarkLabel(digitsOk);
      if (!underlying.IsSigned) {
        code.EmitLoad(c0Slot, VType.Int);
        code.EmitConstInt('-');
        code.EmitBranch(JvmOpcode.IfIcmpeq, throwOverflowLabel);
      }

      var tryStart = code.DefineLabel();
      var tryEnd = code.DefineLabel();
      var catchNfe = code.DefineLabel();
      var nfeClassIdx = this._pool.AddClass("java/lang/NumberFormatException");
      code.MarkLabel(tryStart);
      code.EmitLoad(numStrSlot, strType);
      if (underlying == IrType.I64) {
        code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "parseLong", "(Ljava/lang/String;)J", 1, VType.Long);
        code.EmitReturn(JvmOpcode.Lreturn);
      } else if (underlying == IrType.U64) {
        code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "parseUnsignedLong", "(Ljava/lang/String;)J", 1, VType.Long);
        code.EmitReturn(JvmOpcode.Lreturn);
      } else if (underlying == IrType.U32) {
        code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "parseUnsignedInt", "(Ljava/lang/String;)I", 1, VType.Int);
        code.EmitReturn(JvmOpcode.Ireturn);
      } else {
        var parsedSlot = code.AllocateLocal(VType.Long);
        code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "parseLong", "(Ljava/lang/String;)J", 1, VType.Long);
        code.EmitStore(parsedSlot, VType.Long);
        code.EmitLoad(parsedSlot, VType.Long);
        code.EmitConstLong((long)IrOperators.Minimum(underlying));
        code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
        code.EmitBranch(JvmOpcode.Iflt, throwOverflowLabel);
        code.EmitLoad(parsedSlot, VType.Long);
        code.EmitConstLong((long)IrOperators.Maximum(underlying));
        code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
        code.EmitBranch(JvmOpcode.Ifgt, throwOverflowLabel);
        code.EmitLoad(parsedSlot, VType.Long);
        code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
        code.EmitReturn(JvmOpcode.Ireturn);
      }

      code.MarkLabel(tryEnd);
      code.MarkHandler(catchNfe, nfeClassIdx);
      code.EmitPop();
      code.EmitGoto(throwOverflowLabel);
      code.AddExceptionRange(tryStart, tryEnd, catchNfe, nfeClassIdx);

      // Name-list parse path
      code.MarkLabel(parseNamesLabel);
      code.EmitLoad(trimmedSlot, strType);
      code.EmitConstString(",");
      code.EmitConstInt(-1);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, splitHelper, "(Ljava/lang/String;Ljava/lang/String;I)[Ljava/lang/String;", 3, arrType);
      code.EmitStore(partsSlot, arrType);

      if (isLong)
        code.EmitConstLong(0L);
      else
        code.EmitConstInt(0);
      code.EmitStore(accSlot, accVType);
      code.EmitConstInt(0);
      code.EmitStore(idxSlot, VType.Int);

      var loopCond = code.DefineLabel();
      var loopDone = code.DefineLabel();
      var ordered = enumType.Ordered;
      code.MarkLabel(loopCond);
      code.EmitLoad(idxSlot, VType.Int);
      code.EmitLoad(partsSlot, arrType);
      code.EmitOp(JvmOpcode.Arraylength, 1, VType.Int);
      code.EmitBranch(JvmOpcode.IfIcmpge, loopDone);

      code.EmitLoad(partsSlot, arrType);
      code.EmitLoad(idxSlot, VType.Int);
      code.EmitOp(JvmOpcode.Aaload, 2, strType);
      code.EmitConstInt(0);
      code.EmitConstInt(1);
      code.EmitConstInt(1);
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, trimHelper, "(Ljava/lang/String;ZZZ)Ljava/lang/String;", 4, strType);
      code.EmitStore(partSlot, strType);

      var nextPart = code.DefineLabel();
      foreach (var member in ordered) {
        var caseSens = code.DefineLabel();
        var cmpDone = code.DefineLabel();
        var noMatch = code.DefineLabel();
        code.EmitLoad(1, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, caseSens);
        code.EmitLoad(partSlot, strType);
        code.EmitConstString(member.Name);
        code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "equalsIgnoreCase", "(Ljava/lang/String;)Z", 1, VType.Int);
        code.EmitGoto(cmpDone);
        code.MarkLabel(caseSens);
        code.EmitLoad(partSlot, strType);
        code.EmitConstString(member.Name);
        code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "equals", "(Ljava/lang/Object;)Z", 1, VType.Int);
        code.MarkLabel(cmpDone);
        code.EmitBranch(JvmOpcode.Ifeq, noMatch);

        code.EmitLoad(accSlot, accVType);
        var memberBits = enumType.Unsigned(member.Value);
        if (isLong) {
          code.EmitConstLong(unchecked((long)memberBits));
          code.EmitOp(JvmOpcode.Lor, 2, VType.Long);
        } else {
          code.EmitConstInt(unchecked((int)memberBits));
          code.EmitOp(JvmOpcode.Ior, 2, VType.Int);
        }
        code.EmitStore(accSlot, accVType);
        code.EmitGoto(nextPart);
        code.MarkLabel(noMatch);
      }

      code.EmitGoto(throwInvalidLabel);

      code.MarkLabel(nextPart);
      code.EmitLoad(idxSlot, VType.Int);
      code.EmitConstInt(1);
      code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      code.EmitStore(idxSlot, VType.Int);
      code.EmitGoto(loopCond);

      code.MarkLabel(loopDone);
      code.EmitLoad(accSlot, accVType);
      switch (underlying.Kind) {
        case IrTypeKind.I8:
          code.EmitOp(JvmOpcode.I2b, 1, VType.Int);
          break;
        case IrTypeKind.U8:
          code.EmitConstInt(0xFF);
          code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          break;
        case IrTypeKind.I16:
          code.EmitOp(JvmOpcode.I2s, 1, VType.Int);
          break;
        case IrTypeKind.U16:
          code.EmitOp(JvmOpcode.I2c, 1, VType.Int);
          break;
      }
      code.EmitReturn(isLong ? JvmOpcode.Lreturn : JvmOpcode.Ireturn);

      code.MarkLabel(throwOverflowLabel);
      code.EmitNew("java/lang/ArithmeticException");
      code.EmitDup();
      code.EmitConstString($"Enum value overflow for '{enumType.Name}'.");
      code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/ArithmeticException", "<init>", "(Ljava/lang/String;)V", 1);
      code.EmitAthrow();

      code.MarkLabel(throwInvalidLabel);
      code.EmitNew("java/lang/IllegalArgumentException");
      code.EmitDup();
      code.EmitConstString($"Requested value was not found in enum '{enumType.Name}'.");
      code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/IllegalArgumentException", "<init>", "(Ljava/lang/String;)V", 1);
      code.EmitAthrow();

      var desc = $"(Ljava/lang/String;Z){this._names.Descriptor(underlying)}";
      this._moduleHelpers.Add(new JvmMethodDef(ClassFileWriter.AccStatic, name, desc, code.Build()));
      return name;
    }

    private sealed partial class MethodContext {

      private bool TryEmitExtendedIntrinsic(IrIntrinsic intrinsic) {
        var args = intrinsic.Arguments;
        var strType = VType.Object(this.Pool.AddClass("java/lang/String"));
        switch (intrinsic.Op) {
          case IrIntrinsicOp.EnumHasFlag: {
            var isLong = args[0].Type.IsLong;
            var vtype = isLong ? VType.Long : VType.Int;
            var fSlot = this.Code.AllocateLocal(vtype);
            var trueLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitStore(fSlot, vtype);
            this.Code.EmitLoad(fSlot, vtype);
            if (isLong) {
              this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
              this.Code.EmitLoad(fSlot, VType.Long);
              this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
              this.Code.EmitBranch(JvmOpcode.Ifeq, trueLabel);
            } else {
              this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
              this.Code.EmitLoad(fSlot, VType.Int);
              this.Code.EmitBranch(JvmOpcode.IfIcmpeq, trueLabel);
            }
            this.Code.EmitConstInt(0);
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(trueLabel);
            this.Code.EmitConstInt(1);
            this.Code.MarkLabel(endLabel);
            return true;
          }

          case IrIntrinsicOp.EnumIsDefined: {
            var enumType = EnumOperations.Of(intrinsic);
            var helper = this._owner.EnsureEnumIsDefinedHelper(enumType);
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, $"({this.Names.Descriptor(enumType.Underlying)})Z", 1, VType.Int);
            return true;
          }

          case IrIntrinsicOp.EnumToString: {
            var enumType = EnumOperations.Of(intrinsic);
            var helper = this._owner.EnsureEnumToStringHelper(enumType);
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, $"({this.Names.Descriptor(enumType.Underlying)})Ljava/lang/String;", 1, strType);
            return true;
          }

          case IrIntrinsicOp.EnumParse: {
            var enumType = EnumOperations.Of(intrinsic);
            var helper = this._owner.EnsureEnumParseHelper(enumType);
            var retVType = this.VTypeOf(enumType.Underlying);
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, $"(Ljava/lang/String;Z){this.Names.Descriptor(enumType.Underlying)}", 2, retVType);
            if (enumType.Underlying == IrType.U8) {
              this.Code.EmitConstInt(0xFF);
              this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            }
            return true;
          }

          case IrIntrinsicOp.EnumNames: {
            this.EmitExpr(args[0]);
            this.Code.EmitPop();
            var ordered = EnumOperations.Of(intrinsic).Ordered;
            this.Code.EmitConstInt(ordered.Count);
            this.Code.EmitNewObjectArray("java/lang/String", "[Ljava/lang/String;");
            for (var i = 0; i < ordered.Count; ++i) {
              this.Code.EmitDup();
              this.Code.EmitConstInt(i);
              this.Code.EmitConstString(ordered[i].Name);
              this.Code.EmitOp(JvmOpcode.Aastore, 3);
            }
            return true;
          }

          case IrIntrinsicOp.EnumValues: {
            this.EmitExpr(args[0]);
            this.Code.EmitPop();
            var enumType = EnumOperations.Of(intrinsic);
            var ordered = enumType.Ordered;
            var elem = enumType.Underlying;
            this.Code.EmitConstInt(ordered.Count);
            this._owner.EmitArrayAlloc(this.Code, elem, this.Names.Descriptor(intrinsic.Type));
            for (var i = 0; i < ordered.Count; ++i) {
              this.Code.EmitDup();
              this.Code.EmitConstInt(i);
              EmitScalarConst(this.Code, elem, ordered[i].Value);
              this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(elem), 3);
            }
            return true;
          }

          case IrIntrinsicOp.FormatBoolDotNet or IrIntrinsicOp.FormatBoolEcma: {
            var falseLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            var dotNet = intrinsic.Op == IrIntrinsicOp.FormatBoolDotNet;
            this.EmitExpr(args[0]);
            this.Code.EmitBranch(JvmOpcode.Ifeq, falseLabel);
            this.Code.EmitConstString(dotNet ? "True" : "true");
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(falseLabel);
            this.Code.EmitConstString(dotNet ? "False" : "false");
            this.Code.MarkLabel(endLabel);
            return true;
          }

          case IrIntrinsicOp.Sin or IrIntrinsicOp.Cos or IrIntrinsicOp.Tan
            or IrIntrinsicOp.Asin or IrIntrinsicOp.Acos or IrIntrinsicOp.Atan
            or IrIntrinsicOp.Sinh or IrIntrinsicOp.Cosh or IrIntrinsicOp.Tanh
            or IrIntrinsicOp.Exp or IrIntrinsicOp.Log or IrIntrinsicOp.Log10 or IrIntrinsicOp.Cbrt: {
            var method = char.ToLowerInvariant(intrinsic.Op.ToString()[0]) + intrinsic.Op.ToString()[1..];
            this.EmitMathUnaryFloat(args[0], method);
            return true;
          }

          case IrIntrinsicOp.Atan2 or IrIntrinsicOp.Pow: {
            var method = intrinsic.Op == IrIntrinsicOp.Atan2 ? "atan2" : "pow";
            var isF32 = args[0].Type == IrType.F32;
            this.EmitExpr(args[0]);
            if (isF32)
              this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);
            this.EmitExpr(args[1]);
            if (isF32)
              this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(DD)D", 2, VType.Double);
            if (isF32)
              this.Code.EmitOp(JvmOpcode.D2f, 1, VType.Float);
            return true;
          }

          case IrIntrinsicOp.FusedMultiplyAdd: {
            var isDouble = args[0].Type == IrType.F64;
            var desc = isDouble ? "(DDD)D" : "(FFF)F";
            var vtype = isDouble ? VType.Double : VType.Float;
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitExpr(args[2]);
            if (this._owner._profile.MajorVersion >= 53) {
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "fma", desc, 3, vtype);
            } else {
              var helper = this._owner.EnsureFmaHelper(isDouble);
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, desc, 3, vtype);
            }
            return true;
          }

          case IrIntrinsicOp.BitReverse: {
            var type = args[0].Type.Integral;
            this.EmitExpr(args[0]);
            if (type.Bits == 64) {
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "reverse", "(J)J", 1, VType.Long);
            } else {
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "reverse", "(I)I", 1, VType.Int);
              if (type.Bits < 32) {
                this.Code.EmitConstInt(32 - type.Bits);
                this.Code.EmitOp(JvmOpcode.Iushr, 2, VType.Int);
                this.EmitIntWrapConvert(IrType.I32, type);
              }
            }
            return true;
          }

          case IrIntrinsicOp.IsPow2: {
            this.EmitIsPow2(args[0]);
            return true;
          }

          case IrIntrinsicOp.BigMul: {
            this.EmitExpr(args[0]);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            if (!args[0].Type.IsSigned) {
              this.Code.EmitConstLong(0xFFFFFFFFL);
              this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            }
            this.EmitExpr(args[1]);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            if (!args[0].Type.IsSigned) {
              this.Code.EmitConstLong(0xFFFFFFFFL);
              this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            }
            this.Code.EmitOp(JvmOpcode.Lmul, 2, VType.Long);
            return true;
          }

          case IrIntrinsicOp.Clamp: {
            this.EmitClamp(args[0], args[1], args[2]);
            return true;
          }

          case IrIntrinsicOp.StrFromCodeUnit: {
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/String", "valueOf", "(C)Ljava/lang/String;", 1, strType);
            return true;
          }

          case IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith: {
            var method = intrinsic.Op == IrIntrinsicOp.StrStartsWith ? "startsWith" : "endsWith";
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            if (args[1].Type == IrType.U16)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/String", "valueOf", "(C)Ljava/lang/String;", 1, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", method, "(Ljava/lang/String;)Z", 1, VType.Int);
            return true;
          }

          case IrIntrinsicOp.StrContains: {
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            if (args[1].Type == IrType.U16) {
              var trueLabel = this.Code.DefineLabel();
              var endLabel = this.Code.DefineLabel();
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "indexOf", "(I)I", 1, VType.Int);
              this.Code.EmitBranch(JvmOpcode.Ifge, trueLabel);
              this.Code.EmitConstInt(0);
              this.Code.EmitGoto(endLabel);
              this.Code.MarkLabel(trueLabel);
              this.Code.EmitConstInt(1);
              this.Code.MarkLabel(endLabel);
            } else {
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "contains", "(Ljava/lang/CharSequence;)Z", 1, VType.Int);
            }
            return true;
          }

          case IrIntrinsicOp.StrIndexOf or IrIntrinsicOp.StrLastIndexOf: {
            this.EmitStrSearch(intrinsic);
            return true;
          }

          case IrIntrinsicOp.StrJoin: {
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/String", "join", "(Ljava/lang/CharSequence;[Ljava/lang/CharSequence;)Ljava/lang/String;", 2, strType);
            return true;
          }

          case IrIntrinsicOp.StrReplaceAll: {
            if (args[1].Type == IrType.U16) {
              this.EmitExpr(args[0]);
              this.EmitExpr(args[1]);
              this.EmitExpr(args[2]);
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "replace", "(CC)Ljava/lang/String;", 2, strType);
              return true;
            }

            var textSlot = this.Code.AllocateLocal(strType);
            var needleSlot = this.Code.AllocateLocal(strType);
            var replSlot = this.Code.AllocateLocal(strType);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(textSlot, strType);
            this.EmitExpr(args[1]);
            this.Code.EmitStore(needleSlot, strType);
            this.EmitExpr(args[2]);
            this.Code.EmitStore(replSlot, strType);

            var okLabel = this.Code.DefineLabel();
            this.Code.EmitLoad(needleSlot, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
            this.EmitThrowIllegalState("Replace needle cannot be empty.");
            this.Code.MarkLabel(okLabel);

            this.Code.EmitLoad(textSlot, strType);
            this.Code.EmitLoad(needleSlot, strType);
            this.Code.EmitLoad(replSlot, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "replace", "(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Ljava/lang/String;", 2, strType);
            return true;
          }

          case IrIntrinsicOp.StrReplaceFirst: {
            var textSlot = this.Code.AllocateLocal(strType);
            var needleSlot = this.Code.AllocateLocal(strType);
            var replSlot = this.Code.AllocateLocal(strType);
            var atSlot = this.Code.AllocateLocal(VType.Int);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(textSlot, strType);
            this.EmitExpr(args[1]);
            this.Code.EmitStore(needleSlot, strType);
            this.EmitExpr(args[2]);
            this.Code.EmitStore(replSlot, strType);

            this.Code.EmitLoad(textSlot, strType);
            this.Code.EmitLoad(needleSlot, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "indexOf", "(Ljava/lang/String;)I", 1, VType.Int);
            this.Code.EmitStore(atSlot, VType.Int);

            var notFoundLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            this.Code.EmitLoad(atSlot, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Iflt, notFoundLabel);

            this.Code.EmitLoad(textSlot, strType);
            this.Code.EmitConstInt(0);
            this.Code.EmitLoad(atSlot, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(II)Ljava/lang/String;", 2, strType);
            this.Code.EmitLoad(replSlot, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", 1, strType);
            this.Code.EmitLoad(textSlot, strType);
            this.Code.EmitLoad(atSlot, VType.Int);
            this.Code.EmitLoad(needleSlot, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(I)Ljava/lang/String;", 1, strType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", 1, strType);
            this.Code.EmitGoto(endLabel);

            this.Code.MarkLabel(notFoundLabel);
            this.Code.EmitLoad(textSlot, strType);
            this.Code.MarkLabel(endLabel);
            return true;
          }

          case IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd: {
            var set = StringOperations.SetOf(intrinsic)!.Value;
            var helper = this._owner.EnsureStrTrimHelper();
            this.EmitExpr(args[0]);
            this.Code.EmitConstInt(set == WhitespaceSet.Ecma ? 1 : 0);
            this.Code.EmitConstInt(intrinsic.Op != IrIntrinsicOp.StrTrimEnd ? 1 : 0);
            this.Code.EmitConstInt(intrinsic.Op != IrIntrinsicOp.StrTrimStart ? 1 : 0);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, "(Ljava/lang/String;ZZZ)Ljava/lang/String;", 4, strType);
            return true;
          }

          case IrIntrinsicOp.StrSplit: {
            var helper = this._owner.EnsureStrSplitHelper();
            var arrType = VType.Object(this.Pool.AddClass("[Ljava/lang/String;"));
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            if (args[1].Type == IrType.U16)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/String", "valueOf", "(C)Ljava/lang/String;", 1, strType);
            this.EmitExpr(args[2]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, helper, "(Ljava/lang/String;Ljava/lang/String;I)[Ljava/lang/String;", 3, arrType);
            return true;
          }

          default:
            return false;
        }
      }

      private void EmitIsPow2(IrExpr arg) {
        var type = arg.Type.Integral;
        var isLong = type.IsLong;
        var vtype = isLong ? VType.Long : VType.Int;
        var vSlot = this.Code.AllocateLocal(vtype);
        this.EmitExpr(arg);
        this.Code.EmitStore(vSlot, vtype);

        var falseLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.Code.EmitLoad(vSlot, vtype);
        if (isLong) {
          this.Code.EmitConstLong(0L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(type.IsSigned ? JvmOpcode.Ifle : JvmOpcode.Ifeq, falseLabel);
          this.Code.EmitLoad(vSlot, VType.Long);
          this.Code.EmitLoad(vSlot, VType.Long);
          this.Code.EmitConstLong(1L);
          this.Code.EmitOp(JvmOpcode.Lsub, 2, VType.Long);
          this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
          this.Code.EmitConstLong(0L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, falseLabel);
        } else {
          this.Code.EmitBranch(type.IsSigned ? JvmOpcode.Ifle : JvmOpcode.Ifeq, falseLabel);
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitConstInt(1);
          this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, falseLabel);
        }

        this.Code.EmitConstInt(1);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(falseLabel);
        this.Code.EmitConstInt(0);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitClamp(IrExpr valExpr, IrExpr lowExpr, IrExpr highExpr) {
        var type = valExpr.Type.Integral;
        var vtype = this.VTypeOf(type);
        var valSlot = this.Code.AllocateLocal(vtype);
        var lowSlot = this.Code.AllocateLocal(vtype);
        var highSlot = this.Code.AllocateLocal(vtype);

        this.EmitExpr(valExpr);
        this.Code.EmitStore(valSlot, vtype);
        this.EmitExpr(lowExpr);
        this.Code.EmitStore(lowSlot, vtype);
        this.EmitExpr(highExpr);
        this.Code.EmitStore(highSlot, vtype);

        var okLabel = this.Code.DefineLabel();
        var takeLow = this.Code.DefineLabel();
        var takeHigh = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();

        if (type == IrType.F32 || type == IrType.F64) {
          var cmpl = type == IrType.F32 ? JvmOpcode.Fcmpl : JvmOpcode.Dcmpl;
          var cmpg = type == IrType.F32 ? JvmOpcode.Fcmpg : JvmOpcode.Dcmpg;

          // if (low > high) trap
          this.Code.EmitLoad(lowSlot, vtype);
          this.Code.EmitLoad(highSlot, vtype);
          this.Code.EmitOp(cmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifle, okLabel);
          this.EmitThrowIllegalState("Clamp min bound exceeds max bound.");
          this.Code.MarkLabel(okLabel);

          // if (val < low) return low
          this.Code.EmitLoad(valSlot, vtype);
          this.Code.EmitLoad(lowSlot, vtype);
          this.Code.EmitOp(cmpg, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Iflt, takeLow);

          // if (val > high) return high
          this.Code.EmitLoad(valSlot, vtype);
          this.Code.EmitLoad(highSlot, vtype);
          this.Code.EmitOp(cmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifgt, takeHigh);
        } else if (type == IrType.U32 || type == IrType.U64) {
          var cls = type == IrType.U64 ? "java/lang/Long" : "java/lang/Integer";
          var desc = type == IrType.U64 ? "(JJ)I" : "(II)I";

          this.Code.EmitLoad(lowSlot, vtype);
          this.Code.EmitLoad(highSlot, vtype);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, cls, "compareUnsigned", desc, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifle, okLabel);
          this.EmitThrowIllegalState("Clamp min bound exceeds max bound.");
          this.Code.MarkLabel(okLabel);

          this.Code.EmitLoad(valSlot, vtype);
          this.Code.EmitLoad(lowSlot, vtype);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, cls, "compareUnsigned", desc, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Iflt, takeLow);

          this.Code.EmitLoad(valSlot, vtype);
          this.Code.EmitLoad(highSlot, vtype);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, cls, "compareUnsigned", desc, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifgt, takeHigh);
        } else if (type == IrType.I64) {
          this.Code.EmitLoad(lowSlot, VType.Long);
          this.Code.EmitLoad(highSlot, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifle, okLabel);
          this.EmitThrowIllegalState("Clamp min bound exceeds max bound.");
          this.Code.MarkLabel(okLabel);

          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitLoad(lowSlot, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Iflt, takeLow);

          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitLoad(highSlot, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifgt, takeHigh);
        } else {
          this.Code.EmitLoad(lowSlot, VType.Int);
          this.Code.EmitLoad(highSlot, VType.Int);
          this.Code.EmitBranch(JvmOpcode.IfIcmple, okLabel);
          this.EmitThrowIllegalState("Clamp min bound exceeds max bound.");
          this.Code.MarkLabel(okLabel);

          this.Code.EmitLoad(valSlot, VType.Int);
          this.Code.EmitLoad(lowSlot, VType.Int);
          this.Code.EmitBranch(JvmOpcode.IfIcmplt, takeLow);

          this.Code.EmitLoad(valSlot, VType.Int);
          this.Code.EmitLoad(highSlot, VType.Int);
          this.Code.EmitBranch(JvmOpcode.IfIcmpgt, takeHigh);
        }

        this.Code.EmitLoad(valSlot, vtype);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(takeLow);
        this.Code.EmitLoad(lowSlot, vtype);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(takeHigh);
        this.Code.EmitLoad(highSlot, vtype);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitStrSearch(IrIntrinsic intrinsic) {
        var args = intrinsic.Arguments;
        var method = intrinsic.Op == IrIntrinsicOp.StrIndexOf ? "indexOf" : "lastIndexOf";
        var isChar = args[1].Type == IrType.U16;
        if (args.Count == 2) {
          this.EmitExpr(args[0]);
          this.EmitExpr(args[1]);
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", method, isChar ? "(I)I" : "(Ljava/lang/String;)I", 1, VType.Int);
          return;
        }

        var strType = VType.Object(this.Pool.AddClass("java/lang/String"));
        var textSlot = this.Code.AllocateLocal(strType);
        var needleVType = isChar ? VType.Int : strType;
        var needleSlot = this.Code.AllocateLocal(needleVType);
        var fromSlot = this.Code.AllocateLocal(VType.Int);

        this.EmitExpr(args[0]);
        this.Code.EmitStore(textSlot, strType);
        this.EmitExpr(args[1]);
        this.Code.EmitStore(needleSlot, needleVType);
        this.EmitExpr(args[2]);
        this.Code.EmitStore(fromSlot, VType.Int);

        var trapLabel = this.Code.DefineLabel();
        var okLabel = this.Code.DefineLabel();
        this.Code.EmitLoad(fromSlot, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
        this.Code.EmitLoad(fromSlot, VType.Int);
        this.Code.EmitLoad(textSlot, strType);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
        this.Code.EmitBranch(JvmOpcode.IfIcmple, okLabel);
        this.Code.MarkLabel(trapLabel);
        this.EmitThrowIndexOutOfBounds("String search position out of range.");
        this.Code.MarkLabel(okLabel);

        this.Code.EmitLoad(textSlot, strType);
        this.Code.EmitLoad(needleSlot, needleVType);
        this.Code.EmitLoad(fromSlot, VType.Int);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", method, isChar ? "(II)I" : "(Ljava/lang/String;I)I", 2, VType.Int);
      }

    }

  }

}
