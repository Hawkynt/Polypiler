using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    // --------------------------------------------------------- Lambdas, cells, functional interfaces

    private string EnsureFunctionInterface(IrFunctionType signature) {
      var internalName = this._names.FunctionInterfaceName(signature);
      if (this._extraClasses.ContainsKey(internalName + ".class"))
        return internalName;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName,
        ClassFileWriter.AccPublic | ClassFileWriter.AccInterface | ClassFileWriter.AccAbstract);
      var desc = this._names.MethodDescriptor(signature.Parameters, signature.Result);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccAbstract, "invoke", desc, null));
      this.RegisterExtraClass(internalName, writer.ToBytes());
      return internalName;
    }

    private string EnsureCellClass(IrType valueType) {
      var internalName = this._names.CellClassName(valueType);
      if (this._extraClasses.ContainsKey(internalName + ".class"))
        return internalName;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper);
      var fdesc = this._names.Descriptor(valueType);
      var fv = this._names.VerificationType(valueType, pool);
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic, "value", fdesc));

      var thisType = VType.Object(pool.AddClass(internalName));
      var c0 = new JvmCodeBuilder(pool, [thisType], isConstructor: true);
      c0.EmitLoad(0, thisType);
      c0.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      c0.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", c0.Build()));

      var c1 = new JvmCodeBuilder(pool, [thisType, fv], isConstructor: true);
      c1.EmitLoad(0, thisType);
      c1.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      c1.EmitLoad(0, thisType);
      c1.EmitLoad(1, fv);
      c1.EmitField(JvmOpcode.Putfield, internalName, "value", fdesc, fv);
      c1.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"({fdesc})V", c1.Build()));

      this.RegisterExtraClass(internalName, writer.ToBytes());
      return internalName;
    }

    private string EmitLambdaClass(IrLambda lambda) {
      var ifaceName = this.EnsureFunctionInterface(lambda.Type.Signature!);
      var className = this.NextSyntheticName("Lam");
      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper,
        "java/lang/Object", [ifaceName]);

      var captureSpecs = new List<(IrLocal Local, string FieldName, string CellClass)>(lambda.Captures.Count);
      var thisType = VType.Object(pool.AddClass(className));
      var ctorParams = new List<VType>(1 + lambda.Captures.Count) { thisType };
      var ctorDescParts = new List<string>(lambda.Captures.Count);
      for (var i = 0; i < lambda.Captures.Count; ++i) {
        var cap = lambda.Captures[i];
        var cellClass = this.EnsureCellClass(cap.Type);
        var fieldName = "cap" + i;
        var fieldDesc = $"L{cellClass};";
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, fieldName, fieldDesc));
        captureSpecs.Add((cap, fieldName, cellClass));
        ctorParams.Add(VType.Object(pool.AddClass(cellClass)));
        ctorDescParts.Add(fieldDesc);
      }

      var ctorCode = new JvmCodeBuilder(pool, ctorParams, isConstructor: true);
      ctorCode.EmitLoad(0, thisType);
      ctorCode.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      for (var i = 0; i < captureSpecs.Count; ++i) {
        var (_, fieldName, cellClass) = captureSpecs[i];
        var cellVType = VType.Object(pool.AddClass(cellClass));
        ctorCode.EmitLoad(0, thisType);
        ctorCode.EmitLoad((ushort)(i + 1), cellVType);
        ctorCode.EmitField(JvmOpcode.Putfield, className, fieldName, $"L{cellClass};", cellVType);
      }

      ctorCode.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"({string.Concat(ctorDescParts)})V", ctorCode.Build()));

      var invokeDesc = this._names.MethodDescriptor(lambda.Type.Signature!.Parameters, lambda.ReturnType);
      var invokeCtx = this.EmitExecutableBody(pool, lambda.Parameters, lambda.Locals, lambda.ReturnType, lambda.Body,
        receiver: (className, null), isConstructor: false, closureCaptures: captureSpecs);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "invoke", invokeDesc, invokeCtx.Code.Build()));

      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    private string EmitFunctionRefClass(IrFunctionRef funcRef) {
      var sig = funcRef.Type.Signature!;
      var ifaceName = this.EnsureFunctionInterface(sig);
      var className = this.NextSyntheticName("Ref");
      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper,
        "java/lang/Object", [ifaceName]);

      var thisType = VType.Object(pool.AddClass(className));
      var ctor = new JvmCodeBuilder(pool, [thisType], isConstructor: true);
      ctor.EmitLoad(0, thisType);
      ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      ctor.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", ctor.Build()));

      var paramVTypes = new List<VType>(1 + sig.Parameters.Count) { thisType };
      foreach (var p in sig.Parameters)
        paramVTypes.Add(this._names.VerificationType(p, pool));

      var inv = new JvmCodeBuilder(pool, paramVTypes);
      ushort slot = 1;
      for (var i = 0; i < sig.Parameters.Count; ++i) {
        var pv = this._names.VerificationType(sig.Parameters[i], pool);
        inv.EmitLoad(slot, pv);
        slot = (ushort)(slot + pv.Slots);
      }

      var targetFn = this._functions[funcRef.Function];
      var desc = this._names.MethodDescriptor(targetFn);
      var retVType = targetFn.ReturnType == IrType.Void ? (VType?)null : this._names.VerificationType(targetFn.ReturnType, pool);
      inv.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, JvmTypeMapper.SanitizeIdentifier(targetFn.Name), desc, sig.Parameters.Count, retVType);
      inv.EmitReturn(JvmTypeMapper.ReturnOpcode(targetFn.ReturnType));
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "invoke", desc, inv.Build()));

      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    private string EmitBoundMethodClass(IrBoundMethod bound) {
      var sig = bound.Type.Signature!;
      var ifaceName = this.EnsureFunctionInterface(sig);
      var className = this.NextSyntheticName("Bound");
      var ownerDecl = this._methodOwners[bound.Method];
      var ownerInternal = this._names.DeclarationInternalName(ownerDecl);
      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper,
        "java/lang/Object", [ifaceName]);

      var thisType = VType.Object(pool.AddClass(className));
      var hasRecv = bound.Receiver is not null;
      var ownerVType = VType.Object(pool.AddClass(ownerInternal));
      if (hasRecv) {
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "recv", $"L{ownerInternal};"));
        var ctor = new JvmCodeBuilder(pool, [thisType, ownerVType], isConstructor: true);
        ctor.EmitLoad(0, thisType);
        ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
        ctor.EmitLoad(0, thisType);
        ctor.EmitLoad(1, ownerVType);
        ctor.EmitField(JvmOpcode.Putfield, className, "recv", $"L{ownerInternal};", ownerVType);
        ctor.EmitReturn(JvmOpcode.Return);
        writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"(L{ownerInternal};)V", ctor.Build()));
      } else {
        var ctor = new JvmCodeBuilder(pool, [thisType], isConstructor: true);
        ctor.EmitLoad(0, thisType);
        ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
        ctor.EmitReturn(JvmOpcode.Return);
        writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", ctor.Build()));
      }

      var paramVTypes = new List<VType>(1 + sig.Parameters.Count) { thisType };
      foreach (var p in sig.Parameters)
        paramVTypes.Add(this._names.VerificationType(p, pool));

      var inv = new JvmCodeBuilder(pool, paramVTypes);
      if (hasRecv) {
        inv.EmitLoad(0, thisType);
        inv.EmitField(JvmOpcode.Getfield, className, "recv", $"L{ownerInternal};", ownerVType);
      }

      ushort slot = 1;
      for (var i = 0; i < sig.Parameters.Count; ++i) {
        var pv = this._names.VerificationType(sig.Parameters[i], pool);
        inv.EmitLoad(slot, pv);
        slot = (ushort)(slot + pv.Slots);
      }

      var fn = this._functions[bound.Method.Function];
      var mName = JvmTypeMapper.SanitizeIdentifier(bound.Method.Name);
      var mDesc = this._names.MethodDescriptor(fn, skipFirstParam: hasRecv);
      var retVType = fn.ReturnType == IrType.Void ? (VType?)null : this._names.VerificationType(fn.ReturnType, pool);
      if (!hasRecv) {
        inv.EmitInvoke(JvmOpcode.Invokestatic, ownerInternal, mName, mDesc, sig.Parameters.Count, retVType);
      } else if (bound.Kind == IrMethodCallKind.Interface) {
        var argSlots = sig.Parameters.Sum(JvmTypeMapper.SlotCount);
        inv.EmitInvokeInterface(ownerInternal, mName, mDesc, sig.Parameters.Count, argSlots, retVType);
      } else {
        inv.EmitInvoke(JvmOpcode.Invokevirtual, ownerInternal, mName, mDesc, sig.Parameters.Count, retVType);
      }

      inv.EmitReturn(JvmTypeMapper.ReturnOpcode(fn.ReturnType));
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "invoke", mDesc, inv.Build()));

      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    // --------------------------------------------------------- Sequences & iterators

    private string EnsureSequenceInterface() {
      var internalName = this._names.SequenceInterfaceName;
      if (this._extraClasses.ContainsKey(internalName + ".class"))
        return internalName;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName,
        ClassFileWriter.AccPublic | ClassFileWriter.AccInterface | ClassFileWriter.AccAbstract);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccAbstract, "enumerate", "()Ljava/util/ArrayList;", null));
      this.RegisterExtraClass(internalName, writer.ToBytes());
      return internalName;
    }

    private string EmitIteratorClass(IrFunction function) {
      var seqIface = this.EnsureSequenceInterface();
      var className = this.NextSyntheticName("Iter_" + JvmTypeMapper.SanitizeIdentifier(function.Name) + "_");
      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper,
        "java/lang/Object", [seqIface]);

      var thisType = VType.Object(pool.AddClass(className));
      var ctorParams = new List<VType>(1 + function.Parameters.Count) { thisType };
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var p = function.Parameters[i];
        var fdesc = this._names.Descriptor(p.Type);
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "p" + i, fdesc));
        ctorParams.Add(this._names.VerificationType(p.Type, pool));
      }

      if (!function.ReturnType.Restartable)
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPrivate, "consumed", "Z"));

      var ctor = new JvmCodeBuilder(pool, ctorParams, isConstructor: true);
      ctor.EmitLoad(0, thisType);
      ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      ushort slot = 1;
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var p = function.Parameters[i];
        var fv = this._names.VerificationType(p.Type, pool);
        ctor.EmitLoad(0, thisType);
        ctor.EmitLoad(slot, fv);
        ctor.EmitField(JvmOpcode.Putfield, className, "p" + i, this._names.Descriptor(p.Type), fv);
        slot = (ushort)(slot + fv.Slots);
      }

      ctor.EmitReturn(JvmOpcode.Return);
      var ctorDesc = this._names.MethodDescriptor([.. function.Parameters.Select(p => p.Type)], IrType.Void);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", ctorDesc, ctor.Build()));

      // enumerate() method: builds an ArrayList by running the iterator body.
      var listType = VType.Object(pool.AddClass("java/util/ArrayList"));
      var ctx = new MethodContext(this, pool, [], function.ReturnType, receiver: (className, null), isConstructor: false);
      var yieldListSlot = ctx.Code.AllocateLocal(listType);
      ctx.Code.EmitNew("java/util/ArrayList");
      ctx.Code.EmitDup();
      ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
      ctx.Code.EmitStore(yieldListSlot, listType);

      if (!function.ReturnType.Restartable) {
        var runLabel = ctx.Code.DefineLabel();
        ctx.Code.EmitLoad(0, thisType);
        ctx.Code.EmitField(JvmOpcode.Getfield, className, "consumed", "Z", VType.Int);
        ctx.Code.EmitBranch(JvmOpcode.Ifeq, runLabel);
        ctx.Code.EmitLoad(yieldListSlot, listType);
        ctx.Code.EmitReturn(JvmOpcode.Areturn);
        ctx.Code.MarkLabel(runLabel);
        ctx.Code.EmitLoad(0, thisType);
        ctx.Code.EmitConstInt(1);
        ctx.Code.EmitField(JvmOpcode.Putfield, className, "consumed", "Z", VType.Int);
      }

      // Bind parameters as locals loaded from this.p0, this.p1, ...
      var allLocals = new List<IrLocal>(function.Parameters.Count + function.Locals.Count);
      allLocals.AddRange(function.Parameters);
      allLocals.AddRange(function.Locals);
      ctx.SetYieldListSlot(yieldListSlot);
      ctx.PrepareBodyStorage([], allLocals, function.Body);
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var p = function.Parameters[i];
        var pIndex = i;
        ctx.EmitStoreLocalValue(p, () => {
          var fv = ctx.VTypeOf(p.Type);
          ctx.Code.EmitLoad(0, thisType);
          ctx.Code.EmitField(JvmOpcode.Getfield, className, "p" + pIndex, this._names.Descriptor(p.Type), fv);
          if (p.Type.Integral == IrType.U8) {
            ctx.Code.EmitConstInt(0xFF);
            ctx.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          } else if (p.Type.IsStruct) {
            this.EmitStructCopyCall(ctx.Code, p.Type.StructType!);
          }
        });
      }

      ctx.EmitStmt(function.Body);
      if (ctx.Code.IsReachable) {
        ctx.Code.EmitLoad(yieldListSlot, listType);
        ctx.Code.EmitReturn(JvmOpcode.Areturn);
      }

      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "enumerate", "()Ljava/util/ArrayList;", ctx.Code.Build()));
      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    private string EnsureArraySequenceClass(IrType elemType) {
      var seqIface = this.EnsureSequenceInterface();
      var className = this._names.ArraySequenceClassName(elemType);
      if (this._extraClasses.ContainsKey(className + ".class"))
        return className;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper,
        "java/lang/Object", [seqIface]);

      var arrType = IrType.Array(elemType);
      var arrDesc = this._names.Descriptor(arrType);
      var arrVType = this._names.VerificationType(arrType, pool);
      var thisType = VType.Object(pool.AddClass(className));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "array", arrDesc));

      var ctor = new JvmCodeBuilder(pool, [thisType, arrVType], isConstructor: true);
      ctor.EmitLoad(0, thisType);
      ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(1, arrVType);
      ctor.EmitField(JvmOpcode.Putfield, className, "array", arrDesc, arrVType);
      ctor.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"({arrDesc})V", ctor.Build()));

      // enumerate()
      var ctx = new MethodContext(this, pool, [], IrType.Void, receiver: (className, null));
      var listType = VType.Object(pool.AddClass("java/util/ArrayList"));
      var listSlot = ctx.Code.AllocateLocal(listType);
      var iSlot = ctx.Code.AllocateLocal(VType.Int);
      var lenSlot = ctx.Code.AllocateLocal(VType.Int);
      ctx.Code.EmitNew("java/util/ArrayList");
      ctx.Code.EmitDup();
      ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
      ctx.Code.EmitStore(listSlot, listType);

      ctx.Code.EmitLoad(0, thisType);
      ctx.Code.EmitField(JvmOpcode.Getfield, className, "array", arrDesc, arrVType);
      ctx.Code.EmitOp(JvmOpcode.Arraylength, 1, VType.Int);
      ctx.Code.EmitStore(lenSlot, VType.Int);
      ctx.Code.EmitConstInt(0);
      ctx.Code.EmitStore(iSlot, VType.Int);

      var loopStart = ctx.Code.DefineLabel();
      var loopEnd = ctx.Code.DefineLabel();
      ctx.Code.MarkLabel(loopStart);
      ctx.Code.EmitLoad(iSlot, VType.Int);
      ctx.Code.EmitLoad(lenSlot, VType.Int);
      ctx.Code.EmitBranch(JvmOpcode.IfIcmpge, loopEnd);
      ctx.Code.EmitLoad(listSlot, listType);
      ctx.Code.EmitLoad(0, thisType);
      ctx.Code.EmitField(JvmOpcode.Getfield, className, "array", arrDesc, arrVType);
      ctx.Code.EmitLoad(iSlot, VType.Int);
      ctx.Code.EmitOp(JvmTypeMapper.ArrayLoadOpcode(elemType), 2, ctx.VTypeOf(elemType));
      if (elemType.Integral == IrType.U8) {
        ctx.Code.EmitConstInt(0xFF);
        ctx.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
      } else if (elemType.IsStruct) {
        this.EmitStructCopyCall(ctx.Code, elemType.StructType!);
      }

      ctx.EmitBoxPublic(elemType);
      ctx.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
      ctx.Code.EmitPop();
      ctx.Code.EmitLoad(iSlot, VType.Int);
      ctx.Code.EmitConstInt(1);
      ctx.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
      ctx.Code.EmitStore(iSlot, VType.Int);
      ctx.Code.EmitGoto(loopStart);
      ctx.Code.MarkLabel(loopEnd);
      ctx.Code.EmitLoad(listSlot, listType);
      ctx.Code.EmitReturn(JvmOpcode.Areturn);

      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "enumerate", "()Ljava/util/ArrayList;", ctx.Code.Build()));
      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    // --------------------------------------------------------- Spans & Pointers helper classes

    private string EnsureSpanClass(IrType elemType) {
      var className = this._names.SpanClassName(elemType);
      if (this._extraClasses.ContainsKey(className + ".class"))
        return className;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper);

      var arrDesc = this._names.Descriptor(IrType.Array(elemType));
      var arrVType = this._names.VerificationType(IrType.Array(elemType), pool);
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "array", arrDesc));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "offset", "I"));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "length", "I"));

      var thisType = VType.Object(pool.AddClass(className));
      var ctor = new JvmCodeBuilder(pool, [thisType, arrVType, VType.Int, VType.Int], isConstructor: true);
      ctor.EmitLoad(0, thisType);
      ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(1, arrVType);
      ctor.EmitField(JvmOpcode.Putfield, className, "array", arrDesc, arrVType);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(2, VType.Int);
      ctor.EmitField(JvmOpcode.Putfield, className, "offset", "I", VType.Int);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(3, VType.Int);
      ctor.EmitField(JvmOpcode.Putfield, className, "length", "I", VType.Int);
      ctor.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"({arrDesc}II)V", ctor.Build()));

      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    private string EnsurePointerClass(IrType elemType) {
      var className = this._names.PointerClassName(elemType);
      if (elemType == IrType.Void || this._extraClasses.ContainsKey(className + ".class"))
        return className;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, className,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper);

      var arrDesc = this._names.Descriptor(IrType.Array(elemType));
      var arrVType = this._names.VerificationType(IrType.Array(elemType), pool);
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "array", arrDesc));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "offset", "I"));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "minOffset", "I"));
      writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic | ClassFileWriter.AccFinal, "maxOffset", "I"));

      var thisType = VType.Object(pool.AddClass(className));
      var ctor = new JvmCodeBuilder(pool, [thisType, arrVType, VType.Int, VType.Int, VType.Int], isConstructor: true);
      ctor.EmitLoad(0, thisType);
      ctor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(1, arrVType);
      ctor.EmitField(JvmOpcode.Putfield, className, "array", arrDesc, arrVType);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(2, VType.Int);
      ctor.EmitField(JvmOpcode.Putfield, className, "offset", "I", VType.Int);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(3, VType.Int);
      ctor.EmitField(JvmOpcode.Putfield, className, "minOffset", "I", VType.Int);
      ctor.EmitLoad(0, thisType);
      ctor.EmitLoad(4, VType.Int);
      ctor.EmitField(JvmOpcode.Putfield, className, "maxOffset", "I", VType.Int);
      ctor.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", $"({arrDesc}III)V", ctor.Build()));

      this.RegisterExtraClass(className, writer.ToBytes());
      return className;
    }

    private sealed partial class MethodContext {

      public void SetYieldListSlot(ushort slot) => this._yieldListSlot = slot;

      public void EmitBoxPublic(IrType type) => this.EmitBox(type);

      // --------------------------------------------------------- Lambdas & invokes

      private void EmitLambda(IrLambda lambda) {
        var lamClass = this._owner.EmitLambdaClass(lambda);
        this.Code.EmitNew(lamClass);
        this.Code.EmitDup();
        var ctorDescParts = new List<string>(lambda.Captures.Count);
        foreach (var cap in lambda.Captures) {
          this.EmitLoadCellRef(cap);
          ctorDescParts.Add($"L{this._owner.EnsureCellClass(cap.Type)};");
        }

        this.Code.EmitInvoke(JvmOpcode.Invokespecial, lamClass, "<init>", $"({string.Concat(ctorDescParts)})V", lambda.Captures.Count);
      }

      private void EmitFunctionRef(IrFunctionRef funcRef) {
        var refClass = this._owner.EmitFunctionRefClass(funcRef);
        this.Code.EmitNew(refClass);
        this.Code.EmitDup();
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, refClass, "<init>", "()V", 0);
      }

      private void EmitBoundMethod(IrBoundMethod bound) {
        var boundClass = this._owner.EmitBoundMethodClass(bound);
        if (bound.Receiver is { } recv) {
          var ownerInternal = this.Names.DeclarationInternalName(this._owner._methodOwners[bound.Method]);
          var recvVType = VType.Object(this.Pool.AddClass(ownerInternal));
          var recvSlot = this.Code.AllocateLocal(recvVType);
          this.EmitExpr(recv);
          this.Code.EmitDup();
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitBranch(JvmOpcode.Ifnonnull, okLabel);
          this.Code.EmitNew("java/lang/NullPointerException");
          this.Code.EmitDup();
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/NullPointerException", "<init>", "()V", 0);
          this.Code.EmitAthrow();
          this.Code.MarkLabel(okLabel);
          this.Code.EmitStore(recvSlot, recvVType);
          this.Code.EmitNew(boundClass);
          this.Code.EmitDup();
          this.Code.EmitLoad(recvSlot, recvVType);
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, boundClass, "<init>", $"(L{ownerInternal};)V", 1);
        } else {
          this.Code.EmitNew(boundClass);
          this.Code.EmitDup();
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, boundClass, "<init>", "()V", 0);
        }
      }

      private void EmitInvoke(IrInvoke invoke) {
        var sig = invoke.Callee.Type.Signature!;
        var ifaceName = this._owner.EnsureFunctionInterface(sig);
        this.EmitExpr(invoke.Callee);
        foreach (var arg in invoke.Arguments)
          this.EmitExpr(arg);

        var desc = this.Names.MethodDescriptor(sig.Parameters, sig.Result);
        var argSlots = sig.Parameters.Sum(JvmTypeMapper.SlotCount);
        var retVType = sig.Result == IrType.Void ? (VType?)null : this.VTypeOf(sig.Result);
        this.Code.EmitInvokeInterface(ifaceName, "invoke", desc, invoke.Arguments.Count, argSlots, retVType);
        if (sig.Result.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
      }

      // --------------------------------------------------------- Collections

      private void EmitCollection(IrCollection col) {
        var args = col.Arguments;
        var colType = IrCollections.Constructs(col.Op) ? col.Type : args[0].Type;
        var objType = VType.Object(this.Pool.AddClass("java/lang/Object"));
        switch (col.Op) {
          case IrCollectionOp.ListNew:
            this.Code.EmitNew("java/util/ArrayList");
            this.Code.EmitDup();
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
            break;
          case IrCollectionOp.ListOf: {
            var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
            var listSlot = this.Code.AllocateLocal(listType);
            this.Code.EmitNew("java/util/ArrayList");
            this.Code.EmitDup();
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
            this.Code.EmitStore(listSlot, listType);
            foreach (var arg in args) {
              this.Code.EmitLoad(listSlot, listType);
              this.EmitExpr(arg);
              this.EmitBox(colType.Element!, normalizeNegativeZero: true);
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
              this.Code.EmitPop();
            }

            this.Code.EmitLoad(listSlot, listType);
            break;
          }
          case IrCollectionOp.ListFromArray: {
            var elem = colType.Element!;
            var arrVType = this.VTypeOf(args[0].Type);
            var arrSlot = this.Code.AllocateLocal(arrVType);
            var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
            var listSlot = this.Code.AllocateLocal(listType);
            var iSlot = this.Code.AllocateLocal(VType.Int);
            var lenSlot = this.Code.AllocateLocal(VType.Int);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(arrSlot, arrVType);
            this.Code.EmitNew("java/util/ArrayList");
            this.Code.EmitDup();
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/ArrayList", "<init>", "()V", 0);
            this.Code.EmitStore(listSlot, listType);
            this.Code.EmitLoad(arrSlot, arrVType);
            this.Code.EmitOp(JvmOpcode.Arraylength, 1, VType.Int);
            this.Code.EmitStore(lenSlot, VType.Int);
            this.Code.EmitConstInt(0);
            this.Code.EmitStore(iSlot, VType.Int);
            var start = this.Code.DefineLabel();
            var end = this.Code.DefineLabel();
            this.Code.MarkLabel(start);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitLoad(lenSlot, VType.Int);
            this.Code.EmitBranch(JvmOpcode.IfIcmpge, end);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitLoad(arrSlot, arrVType);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitOp(JvmTypeMapper.ArrayLoadOpcode(elem), 2, this.VTypeOf(elem));
            if (elem.Integral == IrType.U8) {
              this.Code.EmitConstInt(0xFF);
              this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            } else if (elem.IsStruct) {
              this._owner.EmitStructCopyCall(this.Code, elem.StructType!);
            }

            this.EmitBox(elem, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitPop();
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitConstInt(1);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
            this.Code.EmitStore(iSlot, VType.Int);
            this.Code.EmitGoto(start);
            this.Code.MarkLabel(end);
            this.Code.EmitLoad(listSlot, listType);
            break;
          }
          case IrCollectionOp.ListCount:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
            break;
          case IrCollectionOp.ListGet:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "get", "(I)Ljava/lang/Object;", 1, objType);
            this.EmitUnboxFromObject(colType.Element!);
            if (colType.Element!.IsStruct)
              this._owner.EmitStructCopyCall(this.Code, colType.Element!.StructType!);
            break;
          case IrCollectionOp.ListSet:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitExpr(args[2]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "set", "(ILjava/lang/Object;)Ljava/lang/Object;", 2, objType);
            this.Code.EmitPop();
            break;
          case IrCollectionOp.ListAdd:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitPop();
            break;
          case IrCollectionOp.ListInsert:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitExpr(args[2]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(ILjava/lang/Object;)V", 2);
            break;
          case IrCollectionOp.ListRemoveAt:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "remove", "(I)Ljava/lang/Object;", 1, objType);
            this.Code.EmitPop();
            break;
          case IrCollectionOp.ListRemoveLast: {
            var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
            var listSlot = this.Code.AllocateLocal(listType);
            var resVType = this.VTypeOf(col.Type);
            var emptyLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            this.EmitExpr(args[0]);
            this.Code.EmitStore(listSlot, listType);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifeq, emptyLabel);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
            this.Code.EmitConstInt(1);
            this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "remove", "(I)Ljava/lang/Object;", 1, objType);
            this.Code.EmitCheckCast(this.Names.ReferenceInternalName(col.Type));
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(emptyLabel);
            this.Code.EmitNull();
            this.Code.CoerceStackTop(resVType);
            this.Code.MarkLabel(endLabel);
            break;
          }
          case IrCollectionOp.ListClear:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "clear", "()V", 0);
            break;
          case IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "contains", "(Ljava/lang/Object;)Z", 1, VType.Int);
            break;
          case IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "indexOf", "(Ljava/lang/Object;)I", 1, VType.Int);
            break;
          case IrCollectionOp.ListCopyToArray: {
            var elem = colType.Element!;
            var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
            var listSlot = this.Code.AllocateLocal(listType);
            var lenSlot = this.Code.AllocateLocal(VType.Int);
            var arrVType = this.VTypeOf(col.Type);
            var arrSlot = this.Code.AllocateLocal(arrVType);
            var iSlot = this.Code.AllocateLocal(VType.Int);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(listSlot, listType);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
            this.Code.EmitStore(lenSlot, VType.Int);
            this.Code.EmitLoad(lenSlot, VType.Int);
            this._owner.EmitArrayAlloc(this.Code, elem, this.Names.Descriptor(col.Type));
            this.Code.EmitStore(arrSlot, arrVType);
            this.Code.EmitConstInt(0);
            this.Code.EmitStore(iSlot, VType.Int);
            var start = this.Code.DefineLabel();
            var end = this.Code.DefineLabel();
            this.Code.MarkLabel(start);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitLoad(lenSlot, VType.Int);
            this.Code.EmitBranch(JvmOpcode.IfIcmpge, end);
            this.Code.EmitLoad(arrSlot, arrVType);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitLoad(listSlot, listType);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "get", "(I)Ljava/lang/Object;", 1, objType);
            this.EmitUnboxFromObject(elem);
            if (elem.IsStruct)
              this._owner.EmitStructCopyCall(this.Code, elem.StructType!);
            this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(elem), 3);
            this.Code.EmitLoad(iSlot, VType.Int);
            this.Code.EmitConstInt(1);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
            this.Code.EmitStore(iSlot, VType.Int);
            this.Code.EmitGoto(start);
            this.Code.MarkLabel(end);
            this.Code.EmitLoad(arrSlot, arrVType);
            break;
          }
          case IrCollectionOp.MapNew:
            this.Code.EmitNew("java/util/LinkedHashMap");
            this.Code.EmitDup();
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/LinkedHashMap", "<init>", "()V", 0);
            break;
          case IrCollectionOp.MapCount:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "size", "()I", 0, VType.Int);
            break;
          case IrCollectionOp.MapGet: {
            var mapType = VType.Object(this.Pool.AddClass("java/util/LinkedHashMap"));
            var mapSlot = this.Code.AllocateLocal(mapType);
            var keySlot = this.Code.AllocateLocal(objType);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(mapSlot, mapType);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitStore(keySlot, objType);
            var okLabel = this.Code.DefineLabel();
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "containsKey", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
            this.Code.EmitNew("java/util/NoSuchElementException");
            this.Code.EmitDup();
            this.Code.EmitConstString("The given key was not present in the dictionary.");
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/NoSuchElementException", "<init>", "(Ljava/lang/String;)V", 1);
            this.Code.EmitAthrow();
            this.Code.MarkLabel(okLabel);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "get", "(Ljava/lang/Object;)Ljava/lang/Object;", 1, objType);
            this.EmitUnboxFromObject(colType.Element!);
            if (colType.Element!.IsStruct)
              this._owner.EmitStructCopyCall(this.Code, colType.Element!.StructType!);
            break;
          }
          case IrCollectionOp.MapTryGet: {
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "get", "(Ljava/lang/Object;)Ljava/lang/Object;", 1, objType);
            this.Code.EmitCheckCast(this.Names.ReferenceInternalName(col.Type));
            break;
          }
          case IrCollectionOp.MapContainsKey:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "containsKey", "(Ljava/lang/Object;)Z", 1, VType.Int);
            break;
          case IrCollectionOp.MapPut:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.EmitExpr(args[2]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", 2, objType);
            this.Code.EmitPop();
            break;
          case IrCollectionOp.MapAdd: {
            var mapType = VType.Object(this.Pool.AddClass("java/util/LinkedHashMap"));
            var mapSlot = this.Code.AllocateLocal(mapType);
            var keySlot = this.Code.AllocateLocal(objType);
            var valSlot = this.Code.AllocateLocal(objType);
            this.EmitExpr(args[0]);
            this.Code.EmitStore(mapSlot, mapType);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitStore(keySlot, objType);
            this.EmitExpr(args[2]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitStore(valSlot, objType);
            var okLabel = this.Code.DefineLabel();
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "containsKey", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifeq, okLabel);
            this.EmitThrowIllegalArgument("An item with the same key has already been added.");
            this.Code.MarkLabel(okLabel);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitLoad(valSlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", 2, objType);
            this.Code.EmitPop();
            break;
          }
          case IrCollectionOp.MapTryAdd: {
            var mapType = VType.Object(this.Pool.AddClass("java/util/LinkedHashMap"));
            var mapSlot = this.Code.AllocateLocal(mapType);
            var keySlot = this.Code.AllocateLocal(objType);
            var valSlot = this.Code.AllocateLocal(objType);
            var existsLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            this.EmitExpr(args[0]);
            this.Code.EmitStore(mapSlot, mapType);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitStore(keySlot, objType);
            this.EmitExpr(args[2]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitStore(valSlot, objType);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "containsKey", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifne, existsLabel);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitLoad(valSlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", 2, objType);
            this.Code.EmitPop();
            this.Code.EmitConstInt(1);
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(existsLabel);
            this.Code.EmitConstInt(0);
            this.Code.MarkLabel(endLabel);
            break;
          }
          case IrCollectionOp.MapRemove: {
            var mapType = VType.Object(this.Pool.AddClass("java/util/LinkedHashMap"));
            var mapSlot = this.Code.AllocateLocal(mapType);
            var keySlot = this.Code.AllocateLocal(objType);
            var missingLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            this.EmitExpr(args[0]);
            this.Code.EmitStore(mapSlot, mapType);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Key!, normalizeNegativeZero: true);
            this.Code.EmitStore(keySlot, objType);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "containsKey", "(Ljava/lang/Object;)Z", 1, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifeq, missingLabel);
            this.Code.EmitLoad(mapSlot, mapType);
            this.Code.EmitLoad(keySlot, objType);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "remove", "(Ljava/lang/Object;)Ljava/lang/Object;", 1, objType);
            this.Code.EmitPop();
            this.Code.EmitConstInt(1);
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(missingLabel);
            this.Code.EmitConstInt(0);
            this.Code.MarkLabel(endLabel);
            break;
          }
          case IrCollectionOp.MapClear:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "clear", "()V", 0);
            break;
          case IrCollectionOp.SetNew:
            this.Code.EmitNew("java/util/LinkedHashSet");
            this.Code.EmitDup();
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/LinkedHashSet", "<init>", "()V", 0);
            break;
          case IrCollectionOp.SetCount:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "size", "()I", 0, VType.Int);
            break;
          case IrCollectionOp.SetAdd:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
            break;
          case IrCollectionOp.SetRemove:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "remove", "(Ljava/lang/Object;)Z", 1, VType.Int);
            break;
          case IrCollectionOp.SetContains:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.EmitBox(colType.Element!, normalizeNegativeZero: true);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "contains", "(Ljava/lang/Object;)Z", 1, VType.Int);
            break;
          case IrCollectionOp.SetClear:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "clear", "()V", 0);
            break;
        }
      }

      private void EmitForEachCollection(IrForEachCollection loop) {
        var colType = loop.Collection.Type;
        var iterType = VType.Object(this.Pool.AddClass("java/util/Iterator"));
        var objType = VType.Object(this.Pool.AddClass("java/lang/Object"));
        var iterSlot = this.Code.AllocateLocal(iterType);
        this.EmitExpr(loop.Collection);
        if (colType.Kind == IrTypeKind.Map) {
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashMap", "entrySet", "()Ljava/util/Set;", 0, VType.Object(this.Pool.AddClass("java/util/Set")));
          this.Code.EmitInvokeInterface("java/util/Set", "iterator", "()Ljava/util/Iterator;", 0, 0, iterType);
        } else if (colType.IsList) {
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "iterator", "()Ljava/util/Iterator;", 0, iterType);
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/LinkedHashSet", "iterator", "()Ljava/util/Iterator;", 0, iterType);
        }

        this.Code.EmitStore(iterSlot, iterType);

        var condLabel = this.Code.DefineLabel();
        var breakLabel = this.Code.DefineLabel();
        this.Code.MarkLabel(condLabel);
        this.Code.EmitLoad(iterSlot, iterType);
        this.Code.EmitInvokeInterface("java/util/Iterator", "hasNext", "()Z", 0, 0, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifeq, breakLabel);

        if (colType.Kind == IrTypeKind.Map) {
          var entryType = VType.Object(this.Pool.AddClass("java/util/Map$Entry"));
          var entrySlot = this.Code.AllocateLocal(entryType);
          this.Code.EmitLoad(iterSlot, iterType);
          this.Code.EmitInvokeInterface("java/util/Iterator", "next", "()Ljava/lang/Object;", 0, 0, objType);
          this.Code.EmitCheckCast("java/util/Map$Entry");
          this.Code.EmitStore(entrySlot, entryType);
          if (loop.Element is { } keyLocal) {
            this.EmitStoreLocalValue(keyLocal, () => {
              this.Code.EmitLoad(entrySlot, entryType);
              this.Code.EmitInvokeInterface("java/util/Map$Entry", "getKey", "()Ljava/lang/Object;", 0, 0, objType);
              this.EmitUnboxFromObject(keyLocal.Type);
            });
          }

          if (loop.Value is { } valLocal) {
            this.EmitStoreLocalValue(valLocal, () => {
              this.Code.EmitLoad(entrySlot, entryType);
              this.Code.EmitInvokeInterface("java/util/Map$Entry", "getValue", "()Ljava/lang/Object;", 0, 0, objType);
              this.EmitUnboxFromObject(valLocal.Type);
              if (valLocal.Type.IsStruct)
                this._owner.EmitStructCopyCall(this.Code, valLocal.Type.StructType!);
            });
          }
        } else {
          var elemLocal = loop.Element!;
          this.EmitStoreLocalValue(elemLocal, () => {
            this.Code.EmitLoad(iterSlot, iterType);
            this.Code.EmitInvokeInterface("java/util/Iterator", "next", "()Ljava/lang/Object;", 0, 0, objType);
            this.EmitUnboxFromObject(elemLocal.Type);
            if (elemLocal.Type.IsStruct)
              this._owner.EmitStructCopyCall(this.Code, elemLocal.Type.StructType!);
          });
        }

        this._controls.Push((breakLabel, condLabel, this._finallyBlocks.Count));
        this.EmitStmt(loop.Body);
        this._controls.Pop();
        this.Code.EmitGoto(condLabel);
        this.Code.MarkLabel(breakLabel);
      }

      // --------------------------------------------------------- Iterators & sequences

      private ushort ActiveYieldListSlot => this._yieldListSlot!.Value;

      private void EmitYield(IrYield produced) {
        var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
        this.Code.EmitLoad(this.ActiveYieldListSlot, listType);
        this.EmitExpr(produced.Value);
        this.EmitBox(produced.Value.Type);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "add", "(Ljava/lang/Object;)Z", 1, VType.Int);
        this.Code.EmitPop();
      }

      private void EmitYieldBreak() {
        this.EmitEnclosingFinallies(0);
        this.Code.EmitLoad(this.ActiveYieldListSlot, VType.Object(this.Pool.AddClass("java/util/ArrayList")));
        this.Code.EmitReturn(JvmOpcode.Areturn);
      }

      private void EmitYieldFrom(IrYieldFrom delegated) {
        var seqIface = this._owner.EnsureSequenceInterface();
        var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
        this.Code.EmitLoad(this.ActiveYieldListSlot, listType);
        this.EmitExpr(delegated.Sequence);
        this.Code.EmitInvokeInterface(seqIface, "enumerate", "()Ljava/util/ArrayList;", 0, 0, listType);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "addAll", "(Ljava/util/Collection;)Z", 1, VType.Int);
        this.Code.EmitPop();
      }

      private void EmitArraySequence(IrArraySequence seq) {
        var elem = seq.Type.Element!;
        var arrSeqClass = this._owner.EnsureArraySequenceClass(elem);
        var arrVType = this.VTypeOf(seq.Array.Type);
        var arrSlot = this.Code.AllocateLocal(arrVType);
        this.EmitExpr(seq.Array);
        this.Code.EmitStore(arrSlot, arrVType);
        this.Code.EmitNew(arrSeqClass);
        this.Code.EmitDup();
        this.Code.EmitLoad(arrSlot, arrVType);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, arrSeqClass, "<init>", $"({this.Names.Descriptor(seq.Array.Type)})V", 1);
      }

      private void EmitForEachSequence(IrForEachSequence loop) {
        var seqIface = this._owner.EnsureSequenceInterface();
        var listType = VType.Object(this.Pool.AddClass("java/util/ArrayList"));
        var objType = VType.Object(this.Pool.AddClass("java/lang/Object"));
        var listSlot = this.Code.AllocateLocal(listType);
        var iSlot = this.Code.AllocateLocal(VType.Int);
        var lenSlot = this.Code.AllocateLocal(VType.Int);

        this.EmitExpr(loop.Sequence);
        this.Code.EmitInvokeInterface(seqIface, "enumerate", "()Ljava/util/ArrayList;", 0, 0, listType);
        this.Code.EmitStore(listSlot, listType);
        this.Code.EmitLoad(listSlot, listType);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "size", "()I", 0, VType.Int);
        this.Code.EmitStore(lenSlot, VType.Int);
        this.Code.EmitConstInt(0);
        this.Code.EmitStore(iSlot, VType.Int);

        var condLabel = this.Code.DefineLabel();
        var stepLabel = this.Code.DefineLabel();
        var breakLabel = this.Code.DefineLabel();
        this.Code.MarkLabel(condLabel);
        this.Code.EmitLoad(iSlot, VType.Int);
        this.Code.EmitLoad(lenSlot, VType.Int);
        this.Code.EmitBranch(JvmOpcode.IfIcmpge, breakLabel);

        this.EmitStoreLocalValue(loop.Element, () => {
          this.Code.EmitLoad(listSlot, listType);
          this.Code.EmitLoad(iSlot, VType.Int);
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/ArrayList", "get", "(I)Ljava/lang/Object;", 1, objType);
          this.EmitUnboxFromObject(loop.Element.Type);
          if (loop.Element.Type.IsStruct)
            this._owner.EmitStructCopyCall(this.Code, loop.Element.Type.StructType!);
        });

        this._controls.Push((breakLabel, stepLabel, this._finallyBlocks.Count));
        this.EmitStmt(loop.Body);
        this._controls.Pop();
        this.Code.MarkLabel(stepLabel);
        this.Code.EmitLoad(iSlot, VType.Int);
        this.Code.EmitConstInt(1);
        this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
        this.Code.EmitStore(iSlot, VType.Int);
        this.Code.EmitGoto(condLabel);
        this.Code.MarkLabel(breakLabel);
      }

      // --------------------------------------------------------- Tasks & Async/Await

      public void EmitAsyncBody(IrBlock body) {
        var tryStart = this.Code.DefineLabel();
        var tryEnd = this.Code.DefineLabel();
        var catchLabel = this.Code.DefineLabel();

        this.Code.MarkLabel(tryStart);
        this.EmitStmt(body);
        if (this.Code.IsReachable) {
          this.Code.EmitNull();
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/concurrent/CompletableFuture", "completedFuture",
            "(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", 1, VType.Object(this.Pool.AddClass("java/util/concurrent/CompletableFuture")));
          this.Code.EmitReturn(JvmOpcode.Areturn);
        }

        this.Code.MarkLabel(tryEnd);
        this.Code.AddExceptionRange(tryStart, tryEnd, catchLabel, 0);
        this.Code.MarkHandler(catchLabel, 0);
        var throwableType = VType.Object(this.Pool.AddClass("java/lang/Throwable"));
        var exSlot = this.Code.AllocateLocal(throwableType);
        var cfType = VType.Object(this.Pool.AddClass("java/util/concurrent/CompletableFuture"));
        var cfSlot = this.Code.AllocateLocal(cfType);
        this.Code.EmitStore(exSlot, throwableType);
        this.Code.EmitNew("java/util/concurrent/CompletableFuture");
        this.Code.EmitDup();
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/util/concurrent/CompletableFuture", "<init>", "()V", 0);
        this.Code.EmitStore(cfSlot, cfType);
        this.Code.EmitLoad(cfSlot, cfType);
        this.Code.EmitLoad(exSlot, throwableType);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/concurrent/CompletableFuture", "completeExceptionally", "(Ljava/lang/Throwable;)Z", 1, VType.Int);
        this.Code.EmitPop();
        this.Code.EmitLoad(cfSlot, cfType);
        this.Code.EmitReturn(JvmOpcode.Areturn);
      }

      private void EmitAsyncOp(IrAsync asyncOp) {
        var cfType = VType.Object(this.Pool.AddClass("java/util/concurrent/CompletableFuture"));
        if (asyncOp.Op == IrAsyncOp.Completed) {
          this.Code.EmitNull();
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/concurrent/CompletableFuture", "completedFuture",
            "(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", 1, cfType);
          return;
        }

        if (asyncOp.Op == IrAsyncOp.FromResult) {
          this.EmitExpr(asyncOp.Arguments[0]);
          this.EmitBox(asyncOp.Arguments[0].Type);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/concurrent/CompletableFuture", "completedFuture",
            "(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", 1, cfType);
          return;
        }

        // Task.WhenAll
        var futureSlots = this.EvaluateToTempLocals(asyncOp.Arguments);
        var resultType = asyncOp.Type.Element!;
        if (resultType == IrType.Void) {
          foreach (var (slot, vtype) in futureSlots) {
            this.Code.EmitLoad(slot, vtype);
            this.EmitJoinUnwrap();
            this.Code.EmitPop();
          }

          this.Code.EmitNull();
        } else {
          var elemType = resultType.Element!;
          var arrVType = this.VTypeOf(resultType);
          var arrSlot = this.Code.AllocateLocal(arrVType);
          this.Code.EmitConstInt(futureSlots.Count);
          this._owner.EmitArrayAlloc(this.Code, elemType, this.Names.Descriptor(resultType));
          this.Code.EmitStore(arrSlot, arrVType);
          for (var i = 0; i < futureSlots.Count; ++i) {
            this.Code.EmitLoad(arrSlot, arrVType);
            this.Code.EmitConstInt(i);
            this.Code.EmitLoad(futureSlots[i].Slot, futureSlots[i].Type);
            this.EmitJoinUnwrap();
            this.EmitUnboxFromObject(elemType);
            this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(elemType), 3);
          }

          this.Code.EmitLoad(arrSlot, arrVType);
        }

        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/concurrent/CompletableFuture", "completedFuture",
          "(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", 1, cfType);
      }

      private void EmitAwait(IrAwait awaited) {
        this.EmitExpr(awaited.Operand);
        this.EmitJoinUnwrap();
        if (awaited.Type == IrType.Void)
          this.Code.EmitPop();
        else
          this.EmitUnboxFromObject(awaited.Type);
      }

      private void EmitJoinUnwrap() {
        var cfType = VType.Object(this.Pool.AddClass("java/util/concurrent/CompletableFuture"));
        var objType = VType.Object(this.Pool.AddClass("java/lang/Object"));
        var cfSlot = this.Code.AllocateLocal(cfType);
        var resSlot = this.Code.AllocateLocal(objType);
        this.Code.EmitStore(cfSlot, cfType);

        var tryStart = this.Code.DefineLabel();
        var tryEnd = this.Code.DefineLabel();
        var catchLabel = this.Code.DefineLabel();
        var afterLabel = this.Code.DefineLabel();

        this.Code.MarkLabel(tryStart);
        this.Code.EmitLoad(cfSlot, cfType);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/util/concurrent/CompletableFuture", "join", "()Ljava/lang/Object;", 0, objType);
        this.Code.EmitStore(resSlot, objType);
        this.Code.MarkLabel(tryEnd);
        this.Code.EmitGoto(afterLabel);

        var ceCpIdx = this.Pool.AddClass("java/util/concurrent/CompletionException");
        var throwableType = VType.Object(this.Pool.AddClass("java/lang/Throwable"));
        this.Code.AddExceptionRange(tryStart, tryEnd, catchLabel, ceCpIdx);
        this.Code.MarkHandler(catchLabel, ceCpIdx);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/Throwable", "getCause", "()Ljava/lang/Throwable;", 0, throwableType);
        this.Code.EmitAthrow();

        this.Code.MarkLabel(afterLabel);
        this.Code.EmitLoad(resSlot, objType);
      }

    }

  }

}
