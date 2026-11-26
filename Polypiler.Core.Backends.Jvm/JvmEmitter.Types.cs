using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    private void EmitStructClass(IrStructType st) {
      var internalName = this._names.DeclarationInternalName(st);
      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName,
        ClassFileWriter.AccPublic | ClassFileWriter.AccFinal | ClassFileWriter.AccSuper);

      foreach (var field in st.Fields)
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type)));

      // 0-arg default constructor (initializes nested struct fields to their defaults).
      var thisType = VType.Object(pool.AddClass(internalName));
      var defaultCtor = new JvmCodeBuilder(pool, [thisType], isConstructor: true);
      defaultCtor.EmitLoad(0, thisType);
      defaultCtor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      foreach (var field in st.Fields) {
        if (field.Type.StructType is { HasDefault: true } nested) {
          var nestedInternal = this._names.DeclarationInternalName(nested);
          defaultCtor.EmitLoad(0, thisType);
          defaultCtor.EmitNew(nestedInternal);
          defaultCtor.EmitDup();
          defaultCtor.EmitInvoke(JvmOpcode.Invokespecial, nestedInternal, "<init>", "()V", 0);
          defaultCtor.EmitField(JvmOpcode.Putfield, internalName, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type),
            VType.Object(pool.AddClass(nestedInternal)));
        }
      }

      defaultCtor.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", defaultCtor.Build()));

      // All-fields constructor when the struct has at least one field.
      if (st.Fields.Count > 0) {
        var ctorParamVTypes = new List<VType>(1 + st.Fields.Count) { thisType };
        foreach (var field in st.Fields)
          ctorParamVTypes.Add(this._names.VerificationType(field.Type, pool));

        var allCtor = new JvmCodeBuilder(pool, ctorParamVTypes, isConstructor: true);
        allCtor.EmitLoad(0, thisType);
        allCtor.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
        ushort slot = 1;
        foreach (var field in st.Fields) {
          var fv = this._names.VerificationType(field.Type, pool);
          allCtor.EmitLoad(0, thisType);
          allCtor.EmitLoad(slot, fv);
          allCtor.EmitField(JvmOpcode.Putfield, internalName, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type), fv);
          slot = (ushort)(slot + fv.Slots);
        }

        allCtor.EmitReturn(JvmOpcode.Return);
        var ctorDesc = this._names.MethodDescriptor([.. st.Fields.Select(f => f.Type)], IrType.Void);
        writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", ctorDesc, allCtor.Build()));
      }

      // Value-copy method: public <Struct> copy()
      var copyCode = new JvmCodeBuilder(pool, [thisType]);
      var copySlot = copyCode.AllocateLocal(thisType);
      copyCode.EmitNew(internalName);
      copyCode.EmitDup();
      copyCode.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "()V", 0);
      copyCode.EmitStore(copySlot, thisType);
      foreach (var field in st.Fields) {
        var fname = JvmTypeMapper.SanitizeIdentifier(field.Name);
        var fdesc = this._names.Descriptor(field.Type);
        var fv = this._names.VerificationType(field.Type, pool);
        copyCode.EmitLoad(copySlot, thisType);
        copyCode.EmitLoad(0, thisType);
        copyCode.EmitField(JvmOpcode.Getfield, internalName, fname, fdesc, fv);
        if (field.Type.StructType is { } nested)
          this.EmitStructCopyCall(copyCode, nested);
        copyCode.EmitField(JvmOpcode.Putfield, internalName, fname, fdesc, fv);
      }

      copyCode.EmitLoad(copySlot, thisType);
      copyCode.EmitReturn(JvmOpcode.Areturn);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "copy", $"()L{internalName};", copyCode.Build()));

      // Structural equality helper: public static boolean $eq(<Struct> a, <Struct> b)
      this.EmitStructuralEqualsMethod(writer, pool, internalName, st.Fields, nullCheck: false);

      this.RegisterExtraClass(internalName, writer.ToBytes());
    }

    private void EmitStructCopyCall(JvmCodeBuilder code, IrStructType st) {
      var internalName = this._names.DeclarationInternalName(st);
      var structVType = VType.Object(code.Pool.AddClass(internalName));
      code.EmitInvoke(JvmOpcode.Invokevirtual, internalName, "copy", $"()L{internalName};", 0, structVType);
    }

    private void EmitStructuralEqualsMethod(ClassFileWriter writer, ConstantPool pool, string internalName, IReadOnlyList<IrField> fields, bool nullCheck) {
      var thisType = VType.Object(pool.AddClass(internalName));
      var code = new JvmCodeBuilder(pool, [thisType, thisType]);
      var falseLabel = code.DefineLabel();

      if (nullCheck) {
        var sameLabel = code.DefineLabel();
        code.EmitLoad(0, thisType);
        code.EmitLoad(1, thisType);
        code.EmitBranch(JvmOpcode.IfAcmpeq, sameLabel);
        code.EmitLoad(0, thisType);
        code.EmitBranch(JvmOpcode.Ifnull, falseLabel);
        code.EmitLoad(1, thisType);
        code.EmitBranch(JvmOpcode.Ifnull, falseLabel);
        var compareLabel = code.DefineLabel();
        code.EmitGoto(compareLabel);
        code.MarkLabel(sameLabel);
        code.EmitConstInt(1);
        code.EmitReturn(JvmOpcode.Ireturn);
        code.MarkLabel(compareLabel);
      }

      foreach (var field in fields) {
        var fname = JvmTypeMapper.SanitizeIdentifier(field.Name);
        var fdesc = this._names.Descriptor(field.Type);
        var fv = this._names.VerificationType(field.Type, pool);
        code.EmitLoad(0, thisType);
        code.EmitField(JvmOpcode.Getfield, internalName, fname, fdesc, fv);
        code.EmitLoad(1, thisType);
        code.EmitField(JvmOpcode.Getfield, internalName, fname, fdesc, fv);
        this.EmitFieldEqualityBranchOnMismatch(code, pool, field.Type, falseLabel);
      }

      code.EmitConstInt(1);
      code.EmitReturn(JvmOpcode.Ireturn);
      code.MarkLabel(falseLabel);
      code.EmitConstInt(0);
      code.EmitReturn(JvmOpcode.Ireturn);

      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccStatic, "$eq", $"(L{internalName};L{internalName};)Z", code.Build()));
    }

    private void EmitFieldEqualityBranchOnMismatch(JvmCodeBuilder code, ConstantPool pool, IrType type, JvmLabel mismatchLabel) {
      var integral = type.Integral;
      if (integral == IrType.F32 || integral == IrType.F64) {
        // .NET Double.Equals / Float.Equals: +0.0 == -0.0 and NaN == NaN.
        var isF32 = integral == IrType.F32;
        var vtype = isF32 ? VType.Float : VType.Double;
        var rSlot = code.AllocateLocal(vtype);
        var lSlot = code.AllocateLocal(vtype);
        code.EmitStore(rSlot, vtype);
        code.EmitStore(lSlot, vtype);
        var equalLabel = code.DefineLabel();
        code.EmitLoad(lSlot, vtype);
        code.EmitLoad(rSlot, vtype);
        code.EmitOp(isF32 ? JvmOpcode.Fcmpl : JvmOpcode.Dcmpl, 2, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, equalLabel);
        code.EmitLoad(lSlot, vtype);
        code.EmitInvoke(JvmOpcode.Invokestatic, isF32 ? "java/lang/Float" : "java/lang/Double", "isNaN", isF32 ? "(F)Z" : "(D)Z", 1, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, mismatchLabel);
        code.EmitLoad(rSlot, vtype);
        code.EmitInvoke(JvmOpcode.Invokestatic, isF32 ? "java/lang/Float" : "java/lang/Double", "isNaN", isF32 ? "(F)Z" : "(D)Z", 1, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, mismatchLabel);
        code.MarkLabel(equalLabel);
      } else if (integral.IsLong) {
        code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
        code.EmitBranch(JvmOpcode.Ifne, mismatchLabel);
      } else if (JvmTypeMapper.IsJvmPrimitive(type)) {
        code.EmitBranch(JvmOpcode.IfIcmpne, mismatchLabel);
      } else if (type.StructType is { } st) {
        var sName = this._names.DeclarationInternalName(st);
        code.EmitInvoke(JvmOpcode.Invokestatic, sName, "$eq", $"(L{sName};L{sName};)Z", 2, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, mismatchLabel);
      } else if (type.ClassType is { IsRecord: true } recCls) {
        var cName = this._names.DeclarationInternalName(recCls);
        code.EmitInvoke(JvmOpcode.Invokestatic, cName, "$eq", $"(L{cName};L{cName};)Z", 2, VType.Int);
        code.EmitBranch(JvmOpcode.Ifeq, mismatchLabel);
      } else {
        code.EmitBranch(JvmOpcode.IfAcmpne, mismatchLabel);
      }
    }

    private void EmitInterfaceClass(IrInterfaceType iface) {
      var internalName = this._names.DeclarationInternalName(iface);
      var pool = new ConstantPool();
      var baseIfaces = iface.BaseInterfaces.Select(this._names.DeclarationInternalName).ToList();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName,
        ClassFileWriter.AccPublic | ClassFileWriter.AccInterface | ClassFileWriter.AccAbstract,
        "java/lang/Object", baseIfaces);

      foreach (var method in iface.Methods) {
        var fn = this._functions[method.Function];
        var desc = this._names.MethodDescriptor(fn, skipFirstParam: true);
        writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccAbstract,
          JvmTypeMapper.SanitizeIdentifier(method.Name), desc, null));
      }

      this.RegisterExtraClass(internalName, writer.ToBytes());
    }

    private void EmitClass(IrClassType cls) {
      var internalName = this._names.DeclarationInternalName(cls);
      var superInternal = this._names.SuperClassInternalName(cls);
      var ifaces = cls.Interfaces.Select(this._names.DeclarationInternalName).ToList();
      var flags = (ushort)(ClassFileWriter.AccPublic | ClassFileWriter.AccSuper);
      if (cls.IsAbstract)
        flags |= ClassFileWriter.AccAbstract;
      else if (cls.IsSealed)
        flags |= ClassFileWriter.AccFinal;

      var pool = new ConstantPool();
      var writer = new ClassFileWriter(pool, this._profile.MajorVersion, internalName, flags, superInternal, ifaces);

      foreach (var field in cls.Fields)
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccPublic, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type)));

      // Constructors
      if (cls.Constructors.Count == 0) {
        var thisType = VType.Object(pool.AddClass(internalName));
        var ctorCode = new JvmCodeBuilder(pool, [thisType], isConstructor: true);
        ctorCode.EmitLoad(0, thisType);
        ctorCode.EmitInvoke(JvmOpcode.Invokespecial, superInternal, "<init>", "()V", 0);
        ctorCode.EmitReturn(JvmOpcode.Return);
        writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", ctorCode.Build()));
      } else {
        foreach (var ctor in cls.Constructors)
          this.EmitClassConstructor(writer, pool, cls, internalName, superInternal, ctor);
      }

      // Methods
      foreach (var method in cls.Methods) {
        var fn = this._functions[method.Function];
        var methodName = JvmTypeMapper.SanitizeIdentifier(method.Name);
        if (method.Dispatch == IrDispatch.Abstract) {
          var desc = this._names.MethodDescriptor(fn, skipFirstParam: true);
          writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccAbstract, methodName, desc, null));
        } else if (method.IsStatic) {
          var desc = this._names.MethodDescriptor(fn);
          var ctx = this.EmitExecutableBody(pool, fn.Parameters, fn.Locals, fn.ReturnType, fn.Body, receiver: null, isConstructor: false);
          writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccStatic, methodName, desc, ctx.Code.Build()));
        } else {
          var desc = this._names.MethodDescriptor(fn, skipFirstParam: true);
          var ctx = this.EmitExecutableBody(pool, fn.Parameters, fn.Locals, fn.ReturnType, fn.Body,
            receiver: (internalName, fn.Parameters[0]), isConstructor: false);
          var mFlags = (ushort)(ClassFileWriter.AccPublic | (method.IsSealedOverride ? ClassFileWriter.AccFinal : 0));
          writer.AddMethod(new JvmMethodDef(mFlags, methodName, desc, ctx.Code.Build()));
        }
      }

      if (cls.IsRecord)
        this.EmitStructuralEqualsMethod(writer, pool, internalName, cls.AllFields, nullCheck: true);

      this.RegisterExtraClass(internalName, writer.ToBytes());
    }

    private void EmitClassConstructor(
      ClassFileWriter writer,
      ConstantPool pool,
      IrClassType cls,
      string internalName,
      string superInternal,
      IrConstructor ctor) {
      var fn = this._functions[ctor.Function];
      var desc = this._names.MethodDescriptor(fn, skipFirstParam: true);
      var ctx = new MethodContext(this, pool, fn.Parameters, IrType.Void, receiver: (internalName, fn.Parameters[0]), isConstructor: true);
      ctx.PrepareBodyStorage(fn.Parameters, fn.Locals, fn.Body);

      var thisType = VType.Object(pool.AddClass(internalName));
      var stmts = fn.Body.Statements;
      var initCallIdx = -1;
      IrCall? initCall = null;
      var isThisChain = false;
      for (var i = 0; i < stmts.Count; ++i) {
        if (stmts[i] is IrExprStmt { Expression: IrCall call } && this.IsConstructorChainingCall(cls, call.Function, out var chainsSelf)) {
          initCallIdx = i;
          initCall = call;
          isThisChain = chainsSelf;
          break;
        }
      }

      if (initCallIdx < 0) {
        ctx.Code.EmitLoad(0, thisType);
        ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, superInternal, "<init>", "()V", 0);
        this.EmitClassDefaultFieldInits(ctx, cls, internalName, thisType);
        ctx.EmitStmt(fn.Body);
      } else {
        for (var i = 0; i < initCallIdx; ++i)
          ctx.EmitStmt(stmts[i]);

        this.EmitConstructorChainingInvoke(ctx, cls, internalName, superInternal, thisType, initCall!, isThisChain);
        if (!isThisChain)
          this.EmitClassDefaultFieldInits(ctx, cls, internalName, thisType);
        for (var i = initCallIdx + 1; i < stmts.Count; ++i)
          ctx.EmitStmt(stmts[i]);
      }

      if (ctx.Code.IsReachable)
        ctx.Code.EmitReturn(JvmOpcode.Return);

      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", desc, ctx.Code.Build()));
    }

    private bool IsConstructorChainingCall(IrClassType cls, string calleeName, out bool chainsSelf) {
      if (this._ctorOwners.TryGetValue(calleeName, out var owner)) {
        if (ReferenceEquals(owner, cls)) {
          chainsSelf = true;
          return true;
        }

        if (ReferenceEquals(owner, cls.Base)) {
          chainsSelf = false;
          return true;
        }
      }

      if (cls.Base is not null && IrExceptionClass.ConstructorOf(calleeName) is not null) {
        chainsSelf = false;
        return true;
      }

      chainsSelf = false;
      return false;
    }

    private void EmitConstructorChainingInvoke(
      MethodContext ctx,
      IrClassType cls,
      string internalName,
      string superInternal,
      VType thisType,
      IrCall initCall,
      bool isThisChain) {
      var targetClass = isThisChain ? internalName : superInternal;
      if (IrExceptionClass.ConstructorOf(initCall.Function) is not null) {
        ctx.Code.EmitLoad(0, thisType);
        var argCount = initCall.Arguments.Count - 1;
        if (argCount == 0) {
          ctx.Code.EmitConstString(IrExceptionClass.DefaultMessage(cls));
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, targetClass, "<init>", "(Ljava/lang/String;)V", 1);
        } else if (argCount == 1) {
          ctx.EmitExpr(initCall.Arguments[1]);
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, targetClass, "<init>", "(Ljava/lang/String;)V", 1);
        } else if (JvmTypeMapper.HasMessageAndCauseCtor(targetClass)) {
          ctx.EmitExpr(initCall.Arguments[1]);
          ctx.EmitExpr(initCall.Arguments[2]);
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, targetClass, "<init>", "(Ljava/lang/String;Ljava/lang/Throwable;)V", 2);
        } else {
          ctx.EmitExpr(initCall.Arguments[1]);
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, targetClass, "<init>", "(Ljava/lang/String;)V", 1);
          ctx.Code.EmitLoad(0, thisType);
          ctx.EmitExpr(initCall.Arguments[2]);
          ctx.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/Throwable", "initCause", "(Ljava/lang/Throwable;)Ljava/lang/Throwable;", 1,
            VType.Object(ctx.Pool.AddClass("java/lang/Throwable")));
          ctx.Code.EmitPop();
        }

        return;
      }

      var targetFn = this._functions[initCall.Function];
      ctx.Code.EmitLoad(0, thisType);
      for (var i = 1; i < initCall.Arguments.Count; ++i)
        ctx.EmitExpr(initCall.Arguments[i]);

      var targetDesc = this._names.MethodDescriptor(targetFn, skipFirstParam: true);
      ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, targetClass, "<init>", targetDesc, initCall.Arguments.Count - 1);
    }

    private void EmitClassDefaultFieldInits(MethodContext ctx, IrClassType cls, string internalName, VType thisType) {
      foreach (var field in cls.Fields) {
        if (field.Type.StructType is { HasDefault: true } nested) {
          var nestedInternal = this._names.DeclarationInternalName(nested);
          ctx.Code.EmitLoad(0, thisType);
          ctx.Code.EmitNew(nestedInternal);
          ctx.Code.EmitDup();
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, nestedInternal, "<init>", "()V", 0);
          ctx.Code.EmitField(JvmOpcode.Putfield, internalName, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type),
            VType.Object(ctx.Pool.AddClass(nestedInternal)));
        } else if (field.Type.IsMemory) {
          var spanClass = this.EnsureSpanClass(field.Type.Element!);
          var spanVType = VType.Object(ctx.Pool.AddClass(spanClass));
          var arrDesc = this._names.Descriptor(IrType.Array(field.Type.Element!));
          ctx.Code.EmitLoad(0, thisType);
          ctx.Code.EmitNew(spanClass);
          ctx.Code.EmitDup();
          ctx.Code.EmitConstInt(0);
          this.EmitArrayAlloc(ctx.Code, field.Type.Element!, arrDesc);
          ctx.Code.EmitConstInt(0);
          ctx.Code.EmitConstInt(0);
          ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, spanClass, "<init>", $"({arrDesc}II)V", 3);
          ctx.Code.EmitField(JvmOpcode.Putfield, internalName, JvmTypeMapper.SanitizeIdentifier(field.Name), this._names.Descriptor(field.Type), spanVType);
        }
      }
    }

    private sealed partial class MethodContext {

      private void EmitStructDefault(IrStructType st) {
        var internalName = this.Names.DeclarationInternalName(st);
        this.Code.EmitNew(internalName);
        this.Code.EmitDup();
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "()V", 0);
      }

      private void EmitStructNew(IrStructNew created) {
        var st = created.Type.StructType!;
        var internalName = this.Names.DeclarationInternalName(st);
        var argSlots = this.EvaluateToTempLocals(created.Arguments);
        this.Code.EmitNew(internalName);
        this.Code.EmitDup();
        foreach (var (slot, vtype) in argSlots)
          this.Code.EmitLoad(slot, vtype);

        var ctorDesc = this.Names.MethodDescriptor([.. st.Fields.Select(f => f.Type)], IrType.Void);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", ctorDesc, argSlots.Count);
      }

      private void EmitStructEquals(IrStructEquals eq) {
        var type = eq.Left.Type;
        var internalName = this.Names.DeclarationInternalName(type.Declaration!);
        this.EmitExpr(eq.Left);
        this.EmitExpr(eq.Right);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, internalName, "$eq", $"(L{internalName};L{internalName};)Z", 2, VType.Int);
      }

      private void EmitStructPlace(IrExpr target) {
        switch (target) {
          case IrLocalRef localRef:
            this.EmitLoadLocal(localRef.Local, copyStruct: false);
            break;
          case IrArrayLoad load:
            this.EmitExpr(load.Array);
            this.EmitExpr(load.Index);
            this.Code.EmitOp(JvmOpcode.Aaload, 2, this.VTypeOf(load.Type));
            break;
          case IrFieldLoad load when load.Target.Type.IsObject:
            this.EmitExpr(load.Target);
            this.Code.EmitField(JvmOpcode.Getfield, this.FieldOwnerInternalName(load.Target.Type, load.Field),
              JvmTypeMapper.SanitizeIdentifier(load.Field.Name), this.Names.Descriptor(load.Field.Type), this.VTypeOf(load.Field.Type));
            break;
          case IrFieldLoad load:
            this.EmitStructPlace(load.Target);
            this.Code.EmitField(JvmOpcode.Getfield, this.FieldOwnerInternalName(load.Target.Type, load.Field),
              JvmTypeMapper.SanitizeIdentifier(load.Field.Name), this.Names.Descriptor(load.Field.Type), this.VTypeOf(load.Field.Type));
            break;
          default:
            this.EmitExpr(target);
            break;
        }
      }

      private string FieldOwnerInternalName(IrType targetType, IrField field) {
        if (targetType.ClassType is { } cls)
          return this.Names.DeclarationInternalName(cls.OwnerOf(field) ?? cls);

        return this.Names.DeclarationInternalName(targetType.Declaration!);
      }

      private void EmitNewObject(IrNew created) {
        var cls = created.Type.ClassType!;
        if (IrExceptionClass.IsLibrary(cls)) {
          this.EmitNewException(created);
          return;
        }

        var internalName = this.Names.DeclarationInternalName(cls);
        var argSlots = this.EvaluateToTempLocals(created.Arguments);
        this.Code.EmitNew(internalName);
        this.Code.EmitDup();
        foreach (var (slot, vtype) in argSlots)
          this.Code.EmitLoad(slot, vtype);

        var ctorFn = this._owner._functions[created.Constructor.Function];
        var ctorDesc = this.Names.MethodDescriptor(ctorFn, skipFirstParam: true);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", ctorDesc, argSlots.Count);
      }

      private void EmitFieldLoad(IrFieldLoad load) {
        if (load.Target.Type.ClassType is { } cls && IrExceptionClass.IsException(cls)
            && (load.Field == IrExceptionClass.Message || load.Field == IrExceptionClass.InnerException)) {
          this.EmitExceptionFieldLoad(load);
          return;
        }

        if (load.Target.Type.IsObject)
          this.EmitExpr(load.Target);
        else
          this.EmitStructPlace(load.Target);

        var ownerInternal = this.FieldOwnerInternalName(load.Target.Type, load.Field);
        this.Code.EmitField(JvmOpcode.Getfield, ownerInternal, JvmTypeMapper.SanitizeIdentifier(load.Field.Name),
          this.Names.Descriptor(load.Field.Type), this.VTypeOf(load.Field.Type));
        if (load.Field.Type.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        } else if (load.Field.Type.IsStruct) {
          this._owner.EmitStructCopyCall(this.Code, load.Field.Type.StructType!);
        }
      }

      private void EmitFieldStore(IrFieldStore store) {
        if (store.Target.Type.IsObject)
          this.EmitExpr(store.Target);
        else
          this.EmitStructPlace(store.Target);

        this.EmitExpr(store.Value);
        var ownerInternal = this.FieldOwnerInternalName(store.Target.Type, store.Field);
        this.Code.EmitField(JvmOpcode.Putfield, ownerInternal, JvmTypeMapper.SanitizeIdentifier(store.Field.Name),
          this.Names.Descriptor(store.Field.Type), this.VTypeOf(store.Field.Type));
      }

      private void EmitMethodCall(IrMethodCall call) {
        var ownerDecl = this._owner._methodOwners[call.Method];
        var ownerInternal = this.Names.DeclarationInternalName(ownerDecl);
        var methodName = JvmTypeMapper.SanitizeIdentifier(call.Method.Name);
        var fn = this._owner._functions[call.Method.Function];
        var retVType = fn.ReturnType == IrType.Void ? (VType?)null : this.VTypeOf(fn.ReturnType);

        if (call.Method.IsStatic) {
          foreach (var arg in call.Arguments)
            this.EmitExpr(arg);

          this.Code.EmitInvoke(JvmOpcode.Invokestatic, ownerInternal, methodName, this.Names.MethodDescriptor(fn), call.Arguments.Count, retVType);
        } else {
          this.EmitExpr(call.Target!);
          foreach (var arg in call.Arguments)
            this.EmitExpr(arg);

          var desc = this.Names.MethodDescriptor(fn, skipFirstParam: true);
          if (call.Kind == IrMethodCallKind.Interface) {
            var argSlots = call.Arguments.Sum(a => JvmTypeMapper.SlotCount(a.Type));
            this.Code.EmitInvokeInterface(ownerInternal, methodName, desc, call.Arguments.Count, argSlots, retVType);
          } else if (call.Kind == IrMethodCallKind.Base) {
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, ownerInternal, methodName, desc, call.Arguments.Count, retVType);
          } else {
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, ownerInternal, methodName, desc, call.Arguments.Count, retVType);
          }
        }

        if (fn.ReturnType.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
      }

      private void EmitReferenceEquals(IrExpr left, IrExpr right) {
        var trueLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.EmitExpr(left);
        this.EmitExpr(right);
        this.Code.EmitBranch(JvmOpcode.IfAcmpeq, trueLabel);
        this.Code.EmitConstInt(0);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(trueLabel);
        this.Code.EmitConstInt(1);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitAsCast(IrAsCast asCast) {
        var targetInternal = this.Names.ReferenceInternalName(asCast.Type);
        var targetVType = VType.Object(this.Pool.AddClass(targetInternal));
        var nullLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.EmitExpr(asCast.Value);
        this.Code.EmitDup();
        this.Code.EmitInstanceOf(targetInternal);
        this.Code.EmitBranch(JvmOpcode.Ifeq, nullLabel);
        this.Code.EmitCheckCast(targetInternal);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(nullLabel);
        this.Code.EmitPop();
        this.Code.EmitNull();
        this.Code.CoerceStackTop(targetVType);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitGenericEquals(IrExpr left, IrExpr right) {
        var mismatch = this.Code.DefineLabel();
        var end = this.Code.DefineLabel();
        this.EmitExpr(left);
        this.EmitExpr(right);
        this._owner.EmitFieldEqualityBranchOnMismatch(this.Code, this.Pool, left.Type, mismatch);
        this.Code.EmitConstInt(1);
        this.Code.EmitGoto(end);
        this.Code.MarkLabel(mismatch);
        this.Code.EmitConstInt(0);
        this.Code.MarkLabel(end);
      }

      private void EmitGenericCompare(IrExpr left, IrExpr right) {
        this.EmitExpr(left);
        this.EmitExpr(right);
        var type = left.Type.Integral;
        if (type == IrType.Bool) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Boolean", "compare", "(ZZ)I", 2, VType.Int);
        } else if (type.IsFloat) {
          var isF32 = type == IrType.F32;
          var vtype = isF32 ? VType.Float : VType.Double;
          var rSlot = this.Code.AllocateLocal(vtype);
          var lSlot = this.Code.AllocateLocal(vtype);
          this.Code.EmitStore(rSlot, vtype);
          this.Code.EmitStore(lSlot, vtype);

          var ltLabel = this.Code.DefineLabel();
          var gtLabel = this.Code.DefineLabel();
          var eqLabel = this.Code.DefineLabel();
          var bNanCheck = this.Code.DefineLabel();
          var endLabel = this.Code.DefineLabel();

          // a < b -> -1
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.EmitLoad(rSlot, vtype);
          this.Code.EmitOp(isF32 ? JvmOpcode.Fcmpg : JvmOpcode.Dcmpg, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Iflt, ltLabel);

          // a > b -> 1
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.EmitLoad(rSlot, vtype);
          this.Code.EmitOp(isF32 ? JvmOpcode.Fcmpl : JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifgt, gtLabel);

          // a == b -> 0 (handles -0.0 == +0.0)
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.EmitLoad(rSlot, vtype);
          this.Code.EmitOp(isF32 ? JvmOpcode.Fcmpl : JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifeq, eqLabel);

          // At least one is NaN: .NET orders NaN below all numbers, NaN == NaN -> 0.
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, isF32 ? "java/lang/Float" : "java/lang/Double", "isNaN", isF32 ? "(F)Z" : "(D)Z", 1, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, bNanCheck);
          // a is not NaN, so b is NaN -> a > b -> 1
          this.Code.EmitGoto(gtLabel);

          this.Code.MarkLabel(bNanCheck);
          this.Code.EmitLoad(rSlot, vtype);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, isF32 ? "java/lang/Float" : "java/lang/Double", "isNaN", isF32 ? "(F)Z" : "(D)Z", 1, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, eqLabel);
          // a is NaN, b is not NaN -> -1

          this.Code.MarkLabel(ltLabel);
          this.Code.EmitConstInt(-1);
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(gtLabel);
          this.Code.EmitConstInt(1);
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(eqLabel);
          this.Code.EmitConstInt(0);
          this.Code.MarkLabel(endLabel);
        } else if (type.Bits < 32) {
          this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
        } else if (type == IrType.I32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compare", "(II)I", 2, VType.Int);
        } else if (type == IrType.U32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compareUnsigned", "(II)I", 2, VType.Int);
        } else if (type == IrType.I64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compare", "(JJ)I", 2, VType.Int);
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
        }
      }

      private List<(ushort Slot, VType Type)> EvaluateToTempLocals(IReadOnlyList<IrExpr> exprs) {
        var result = new List<(ushort, VType)>(exprs.Count);
        foreach (var expr in exprs) {
          var vtype = this.VTypeOf(expr.Type);
          var slot = this.Code.AllocateLocal(vtype);
          this.EmitExpr(expr);
          this.Code.EmitStore(slot, vtype);
          result.Add((slot, vtype));
        }

        return result;
      }

    }

  }

}
