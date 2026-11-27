using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    private readonly IrModule _module;
    private readonly JvmProfile _profile;
    private readonly JvmEmitOptions _options;
    private readonly JvmModuleNames _names;
    private readonly string _internalClassName;
    private readonly ConstantPool _pool = new();
    private readonly Dictionary<string, IrFunction> _functions;
    private readonly Dictionary<string, IrForeignFunction> _foreignFunctions;
    private readonly Dictionary<IrMethod, IrTypeDeclaration> _methodOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrClassType> _ctorOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _memberFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<IrModuleConst, string> _constFieldNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, string> _globalFieldNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, byte[]> _extraClasses = new(StringComparer.Ordinal);
    private readonly List<JvmMethodDef> _moduleHelpers = [];
    private readonly Dictionary<string, string> _helperNames = new(StringComparer.Ordinal);
    private readonly Dictionary<IrEnumType, string> _enumIsDefinedHelpers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrEnumType, string> _enumToStringHelpers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrEnumType, string> _enumParseHelpers = new(ReferenceEqualityComparer.Instance);
    private int _syntheticCounter;

    public ModuleEmitter(IrModule module, JvmProfile profile, JvmEmitOptions options) {
      this._module = module;
      this._profile = profile;
      this._options = options;
      this._names = new JvmModuleNames(module, options.PackageName);
      this._internalClassName = this._names.ModuleInternalName;
      this._functions = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
      this._foreignFunctions = module.ForeignFunctions.ToDictionary(f => f.Name, StringComparer.Ordinal);

      foreach (var decl in module.Types) {
        if (decl is IrInterfaceType iface) {
          foreach (var method in iface.Methods) {
            this._methodOwners[method] = iface;
            this._memberFunctions.Add(method.Function);
          }
        } else if (decl is IrClassType cls) {
          foreach (var ctor in cls.Constructors) {
            this._ctorOwners[ctor.Function] = cls;
            this._memberFunctions.Add(ctor.Function);
          }

          foreach (var method in cls.Methods) {
            this._methodOwners[method] = cls;
            this._memberFunctions.Add(method.Function);
          }
        }
      }
    }

    /// <summary>Compiles the module into <c>.class</c> or deterministic <c>.jar</c> generated files.</summary>
    public IReadOnlyList<GeneratedFile> EmitFiles() {
      // Emit companion classes for all declared structs, interfaces, and classes.
      foreach (var decl in this._module.Types) {
        if (decl is IrStructType st)
          this.EmitStructClass(st);
        else if (decl is IrInterfaceType iface)
          this.EmitInterfaceClass(iface);
        else if (decl is IrClassType cls)
          this.EmitClass(cls);
      }

      var writer = new ClassFileWriter(this._pool, this._profile.MajorVersion, this._internalClassName);
      this.DeclareFields(writer);
      this.EmitDefaultConstructor(writer);
      this.EmitStaticInitializerIfNeeded(writer);

      foreach (var foreign in this._module.ForeignFunctions)
        this.EmitForeignDeclaration(writer, foreign);

      foreach (var function in this._module.Functions) {
        if (this._memberFunctions.Contains(function.Name))
          continue;

        this.EmitFunction(writer, function);
      }

      var hasMain = this.EmitMainBridgeIfNeeded(writer);
      for (var i = 0; i < this._moduleHelpers.Count; ++i)
        writer.AddMethod(this._moduleHelpers[i]);
      var classBytes = writer.ToBytes();
      var classEntryPath = this._internalClassName + ".class";

      var allEntries = new List<(string Path, byte[] Bytes)>(1 + this._extraClasses.Count) {
        (classEntryPath, classBytes)
      };
      foreach (var kvp in this._extraClasses.OrderBy(e => e.Key, StringComparer.Ordinal))
        allEntries.Add((kvp.Key, kvp.Value));

      var format = this._options.PackageFormat ?? this._profile.PackageFormat;
      if (format == JvmPackageFormat.Class)
        return [.. allEntries.Select(e => new GeneratedFile(e.Path, "") { BinaryContent = e.Bytes })];

      var mainClass = this._options.MainClass ?? (hasMain ? this._internalClassName.Replace('/', '.') : null);
      var jarBytes = JarWriter.Write(allEntries, mainClass);
      var jarName = JvmTypeMapper.ClassName(this._module.Name) + ".jar";
      return [new GeneratedFile(jarName, "") { BinaryContent = jarBytes }];
    }

    private void RegisterExtraClass(string internalName, byte[] bytes)
      => this._extraClasses[internalName + ".class"] = bytes;

    private string NextSyntheticName(string prefix)
      => $"{this._internalClassName}${prefix}{++this._syntheticCounter}";

    private void DeclareFields(ClassFileWriter writer) {
      foreach (var constant in this._module.Constants) {
        if (!constant.Type.IsArray)
          continue;

        var fieldName = "$const_" + JvmTypeMapper.SanitizeIdentifier(constant.Name);
        this._constFieldNames[constant] = fieldName;
        writer.AddField(new JvmFieldDef(ClassFileWriter.AccStatic | ClassFileWriter.AccFinal, fieldName, this._names.Descriptor(constant.Type)));
      }

      foreach (var global in this._module.Globals) {
        var fieldName = JvmTypeMapper.SanitizeIdentifier(global.Name);
        this._globalFieldNames[global] = fieldName;
        var flags = (ushort)(ClassFileWriter.AccPublic | ClassFileWriter.AccStatic);
        writer.AddField(new JvmFieldDef(flags, fieldName, this._names.Descriptor(global.Type)));
      }
    }

    private void EmitDefaultConstructor(ClassFileWriter writer) {
      var thisType = VType.Object(this._pool.AddClass(this._internalClassName));
      var code = new JvmCodeBuilder(this._pool, [thisType], isConstructor: true);
      code.EmitLoad(0, thisType);
      code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/Object", "<init>", "()V", 0);
      code.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic, "<init>", "()V", code.Build()));
    }

    private void EmitStaticInitializerIfNeeded(ClassFileWriter writer) {
      var hasArrayConsts = this._constFieldNames.Count > 0;
      var hasInitializedGlobals = this._module.Globals.Any(g => g.Initializer is not null);
      var hasModuleInit = this._module.Initializer is not null;
      if (!hasArrayConsts && !hasInitializedGlobals && !hasModuleInit)
        return;

      var ctx = new MethodContext(this, this._pool, [], IrType.Void);
      foreach (var constant in this._module.Constants) {
        if (!this._constFieldNames.TryGetValue(constant, out var fieldName))
          continue;

        var elem = constant.Type.Element!;
        var count = elem.IsString ? constant.Strings.Count : constant.Values.Count;
        ctx.Code.EmitConstInt(count);
        this.EmitArrayAlloc(ctx.Code, elem, this._names.Descriptor(constant.Type));
        for (var i = 0; i < count; ++i) {
          ctx.Code.EmitDup();
          ctx.Code.EmitConstInt(i);
          if (elem.IsString)
            ctx.Code.EmitConstString(constant.Strings[i]);
          else
            EmitScalarConst(ctx.Code, elem, constant.Values[i]);
          ctx.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(elem), 3);
        }

        ctx.Code.EmitStaticField(JvmOpcode.Putstatic, this._internalClassName, fieldName, this._names.Descriptor(constant.Type), this._names.VerificationType(constant.Type, this._pool));
      }

      foreach (var global in this._module.Globals) {
        if (global.Initializer is null)
          continue;

        ctx.EmitExpr(global.Initializer);
        ctx.Code.EmitStaticField(JvmOpcode.Putstatic, this._internalClassName, this._globalFieldNames[global], this._names.Descriptor(global.Type), this._names.VerificationType(global.Type, this._pool));
      }

      if (this._module.Initializer is { } initName)
        ctx.Code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, JvmTypeMapper.SanitizeIdentifier(initName), "()V", 0);

      ctx.Code.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccStatic, "<clinit>", "()V", ctx.Code.Build()));
    }

    private void EmitForeignDeclaration(ClassFileWriter writer, IrForeignFunction foreign) {
      var flags = (ushort)((foreign.IsPublic ? ClassFileWriter.AccPublic : 0) | ClassFileWriter.AccStatic | ClassFileWriter.AccNative);
      writer.AddMethod(new JvmMethodDef(flags, JvmTypeMapper.SanitizeIdentifier(foreign.Name), this._names.MethodDescriptor(foreign), null));
    }

    private void EmitFunction(ClassFileWriter writer, IrFunction function) {
      var flags = (ushort)((function.IsPublic ? ClassFileWriter.AccPublic : 0) | ClassFileWriter.AccStatic);
      var methodName = JvmTypeMapper.SanitizeIdentifier(function.Name);
      var descriptor = this._names.MethodDescriptor(function);

      if (IrSequences.IsIterator(function)) {
        var iterClass = this.EmitIteratorClass(function);
        var ctx = new MethodContext(this, this._pool, function.Parameters, function.ReturnType);
        ctx.Code.EmitNew(iterClass);
        ctx.Code.EmitDup();
        foreach (var param in function.Parameters) {
          var vtype = ctx.VTypeOf(param.Type);
          ctx.Code.EmitLoad(ctx.SlotOf(param), vtype);
          if (param.Type.IsStruct)
            this.EmitStructCopyCall(ctx.Code, param.Type.StructType!);
        }

        var ctorDesc = this._names.MethodDescriptor([.. function.Parameters.Select(p => p.Type)], IrType.Void);
        ctx.Code.EmitInvoke(JvmOpcode.Invokespecial, iterClass, "<init>", ctorDesc, function.Parameters.Count);
        ctx.Code.EmitReturn(JvmOpcode.Areturn);
        writer.AddMethod(new JvmMethodDef(flags, methodName, descriptor, ctx.Code.Build()));
        return;
      }

      var bodyCtx = this.EmitExecutableBody(this._pool, function.Parameters, function.Locals, function.ReturnType, function.Body, receiver: null, isConstructor: false);
      writer.AddMethod(new JvmMethodDef(flags, methodName, descriptor, bodyCtx.Code.Build()));
    }

    private MethodContext EmitExecutableBody(
      ConstantPool pool,
      IReadOnlyList<IrLocal> parameters,
      IReadOnlyList<IrLocal> locals,
      IrType returnType,
      IrBlock body,
      (string InternalName, IrLocal? ParamLocal)? receiver,
      bool isConstructor,
      IReadOnlyList<(IrLocal Captured, string FieldName, string CellClass)>? closureCaptures = null) {
      var ctx = new MethodContext(this, pool, parameters, returnType, receiver, isConstructor, closureCaptures);
      ctx.PrepareBodyStorage(parameters, locals, body);

      if (returnType.IsAsync) {
        ctx.EmitAsyncBody(body);
        return ctx;
      }

      ctx.EmitStmt(body);
      if (ctx.Code.IsReachable && returnType == IrType.Void)
        ctx.Code.EmitReturn(JvmOpcode.Return);

      return ctx;
    }

    private bool EmitMainBridgeIfNeeded(ClassFileWriter writer) {
      var candidate = this._module.Functions.FirstOrDefault(f =>
        !this._memberFunctions.Contains(f.Name)
        && f.IsPublic
        && (f.Name is "main" or "Main")
        && (f.ReturnType == IrType.Void || f.ReturnType == IrType.I32)
        && (f.Parameters.Count == 0 || (f.Parameters.Count == 1 && f.Parameters[0].Type == IrType.Array(IrType.String))));
      if (candidate is null)
        return false;

      if (candidate.Name == "main" && candidate.ReturnType == IrType.Void && candidate.Parameters.Count == 1)
        return true;

      var strArrayType = VType.Object(this._pool.AddClass("[Ljava/lang/String;"));
      var code = new JvmCodeBuilder(this._pool, [strArrayType]);
      if (candidate.Parameters.Count == 1)
        code.EmitLoad(0, strArrayType);

      var retVType = candidate.ReturnType == IrType.Void ? (VType?)null : VType.Int;
      code.EmitInvoke(JvmOpcode.Invokestatic, this._internalClassName, candidate.Name, this._names.MethodDescriptor(candidate), candidate.Parameters.Count, retVType);
      if (candidate.ReturnType == IrType.I32) {
        var zeroLabel = code.DefineLabel();
        code.EmitDup();
        code.EmitBranch(JvmOpcode.Ifeq, zeroLabel);
        code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/System", "exit", "(I)V", 1);
        code.EmitReturn(JvmOpcode.Return);
        code.MarkLabel(zeroLabel);
        code.EmitPop();
      }

      code.EmitReturn(JvmOpcode.Return);
      writer.AddMethod(new JvmMethodDef(ClassFileWriter.AccPublic | ClassFileWriter.AccStatic, "main", "([Ljava/lang/String;)V", code.Build()));
      return true;
    }

    private void EmitArrayAlloc(JvmCodeBuilder code, IrType elem, string arrayDesc) {
      if (JvmTypeMapper.IsJvmPrimitive(elem))
        code.EmitNewPrimitiveArray(JvmTypeMapper.PrimitiveArrayType(elem), arrayDesc);
      else
        code.EmitNewObjectArray(this._names.ReferenceInternalName(elem), arrayDesc);
    }

    private static void EmitScalarConst(JvmCodeBuilder code, IrType type, long value) {
      switch (type.Integral.Kind) {
        case IrTypeKind.Bool:
          code.EmitConstInt(value != 0 ? 1 : 0);
          break;
        case IrTypeKind.U8:
          code.EmitConstInt((int)(value & 0xFF));
          break;
        case IrTypeKind.U16:
          code.EmitConstInt((int)(value & 0xFFFF));
          break;
        case IrTypeKind.I64 or IrTypeKind.U64:
          code.EmitConstLong(value);
          break;
        case IrTypeKind.F32:
          code.EmitConstFloatBits(unchecked((int)value));
          break;
        case IrTypeKind.F64:
          code.EmitConstDoubleBits(value);
          break;
        default:
          code.EmitConstInt(unchecked((int)value));
          break;
      }
    }

    private sealed partial class MethodContext {

      private readonly ModuleEmitter _owner;
      private readonly ConstantPool _pool;
      private readonly IrType _returnType;
      private readonly string? _receiverInternalName;
      private ushort? _yieldListSlot;
      private readonly Dictionary<IrLocal, ushort> _slots = new(ReferenceEqualityComparer.Instance);
      private readonly Dictionary<IrLocal, VType> _localVTypes = new(ReferenceEqualityComparer.Instance);
      private readonly Dictionary<IrLocal, (ushort CellSlot, string CellClass)> _capturedCells = new(ReferenceEqualityComparer.Instance);
      private readonly Dictionary<IrLocal, (string FieldName, string CellClass)> _closureCaptures = new(ReferenceEqualityComparer.Instance);
      private readonly Dictionary<IrLocal, (ushort ArraySlot, VType ArrayVType)> _addressedLocals = new(ReferenceEqualityComparer.Instance);
      private readonly Stack<(JvmLabel BreakLabel, JvmLabel? ContinueLabel, int FinallyDepth)> _controls = new();
      private readonly List<IrBlock> _finallyBlocks = [];
      private readonly Stack<ushort> _catchExSlots = new();

      public JvmCodeBuilder Code { get; }

      public ConstantPool Pool => this._pool;

      public JvmModuleNames Names => this._owner._names;

      public MethodContext(
        ModuleEmitter owner,
        ConstantPool pool,
        IReadOnlyList<IrLocal> parameters,
        IrType returnType,
        (string InternalName, IrLocal? ParamLocal)? receiver = null,
        bool isConstructor = false,
        IReadOnlyList<(IrLocal Captured, string FieldName, string CellClass)>? closureCaptures = null) {
        this._owner = owner;
        this._pool = pool;
        this._returnType = returnType;
        this._receiverInternalName = receiver?.InternalName;

        var paramVTypes = new List<VType>(parameters.Count + (receiver is not null ? 1 : 0));
        ushort slot = 0;
        var paramStart = 0;
        if (receiver is { } recv) {
          var thisVType = VType.Object(pool.AddClass(recv.InternalName));
          paramVTypes.Add(thisVType);
          if (recv.ParamLocal is { } recvLocal) {
            this._slots[recvLocal] = 0;
            this._localVTypes[recvLocal] = thisVType;
            paramStart = 1;
          }

          slot = 1;
        }

        for (var i = paramStart; i < parameters.Count; ++i) {
          var vtype = owner._names.VerificationType(parameters[i].Type, pool);
          paramVTypes.Add(vtype);
          this._slots[parameters[i]] = slot;
          this._localVTypes[parameters[i]] = vtype;
          slot = (ushort)(slot + vtype.Slots);
        }

        if (closureCaptures is not null)
          foreach (var (cap, fieldName, cellClass) in closureCaptures)
            this._closureCaptures[cap] = (fieldName, cellClass);

        this.Code = new JvmCodeBuilder(pool, paramVTypes, isConstructor);
      }

      public void PrepareBodyStorage(IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, IrBlock body) {
        var captured = IrClosures.Captured(body);
        var addressed = FindAddressedLocals(body);

        // Bind declared locals first so slots are allocated cleanly.
        foreach (var local in locals) {
          if (captured.Contains(local)) {
            var cellClass = this._owner.EnsureCellClass(local.Type);
            var cellVType = VType.Object(this.Pool.AddClass(cellClass));
            var cellSlot = this.Code.AllocateLocal(cellVType);
            this._capturedCells[local] = (cellSlot, cellClass);
          } else if (addressed.Contains(local)) {
            var arrType = IrType.Array(local.Type);
            var arrVType = this.VTypeOf(arrType);
            var arrSlot = this.Code.AllocateLocal(arrVType);
            this._addressedLocals[local] = (arrSlot, arrVType);
          } else {
            this.BindLocal(local);
          }
        }

        // Normalize u8 parameters, copy mutable struct parameters, and box captured/addressed parameters.
        foreach (var param in parameters) {
          if (this._closureCaptures.ContainsKey(param))
            continue;
          if (!this._slots.TryGetValue(param, out var paramSlot))
            continue;

          if (param.Type.Integral == IrType.U8) {
            this.Code.EmitLoad(paramSlot, VType.Int);
            this.Code.EmitConstInt(0xFF);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            this.Code.EmitStore(paramSlot, VType.Int);
          } else if (param.Type.IsStruct && !param.Type.StructType!.IsReadOnly) {
            this.Code.EmitLoad(paramSlot, this._localVTypes[param]);
            this._owner.EmitStructCopyCall(this.Code, param.Type.StructType!);
            this.Code.EmitStore(paramSlot, this._localVTypes[param]);
          }

          if (captured.Contains(param)) {
            var cellClass = this._owner.EnsureCellClass(param.Type);
            var cellVType = VType.Object(this.Pool.AddClass(cellClass));
            var cellSlot = this.Code.AllocateLocal(cellVType);
            this.Code.EmitNew(cellClass);
            this.Code.EmitDup();
            this.Code.EmitLoad(paramSlot, this._localVTypes[param]);
            this.Code.EmitInvoke(JvmOpcode.Invokespecial, cellClass, "<init>", $"({this.Names.Descriptor(param.Type)})V", 1);
            this.Code.EmitStore(cellSlot, cellVType);
            this._capturedCells[param] = (cellSlot, cellClass);
          } else if (addressed.Contains(param)) {
            var arrType = IrType.Array(param.Type);
            var arrVType = this.VTypeOf(arrType);
            var arrSlot = this.Code.AllocateLocal(arrVType);
            this.Code.EmitConstInt(1);
            this._owner.EmitArrayAlloc(this.Code, param.Type, this.Names.Descriptor(arrType));
            this.Code.EmitDup();
            this.Code.EmitConstInt(0);
            this.Code.EmitLoad(paramSlot, this._localVTypes[param]);
            this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(param.Type), 3);
            this.Code.EmitStore(arrSlot, arrVType);
            this._addressedLocals[param] = (arrSlot, arrVType);
          }
        }
      }

      private static HashSet<IrLocal> FindAddressedLocals(IrBlock body) {
        var set = new HashSet<IrLocal>(ReferenceEqualityComparer.Instance);
        foreach (var node in IrTree.Nodes(body))
          if (node is IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef target] })
            set.Add(target.Local);
        return set;
      }

      public void BindLocal(IrLocal local) {
        var vtype = this.VTypeOf(local.Type);
        this._slots[local] = this.Code.AllocateLocal(vtype);
        this._localVTypes[local] = vtype;
      }

      public ushort SlotOf(IrLocal local) => this._slots[local];

      public VType VTypeOf(IrType type) => this.Names.VerificationType(type, this.Pool);

      public void EmitLoadLocal(IrLocal local, bool copyStruct = true) {
        if (this._closureCaptures.TryGetValue(local, out var cap)) {
          var cellVType = VType.Object(this.Pool.AddClass(cap.CellClass));
          this.Code.EmitLoad(0, VType.Object(this.Pool.AddClass(this._receiverInternalName!)));
          this.Code.EmitField(JvmOpcode.Getfield, this._receiverInternalName!, cap.FieldName, $"L{cap.CellClass};", cellVType);
          this.Code.EmitField(JvmOpcode.Getfield, cap.CellClass, "value", this.Names.Descriptor(local.Type), this.VTypeOf(local.Type));
        } else if (this._capturedCells.TryGetValue(local, out var cell)) {
          var cellVType = VType.Object(this.Pool.AddClass(cell.CellClass));
          this.Code.EmitLoad(cell.CellSlot, cellVType);
          this.Code.EmitField(JvmOpcode.Getfield, cell.CellClass, "value", this.Names.Descriptor(local.Type), this.VTypeOf(local.Type));
        } else if (this._addressedLocals.TryGetValue(local, out var addr)) {
          this.Code.EmitLoad(addr.ArraySlot, addr.ArrayVType);
          this.Code.EmitConstInt(0);
          this.Code.EmitOp(JvmTypeMapper.ArrayLoadOpcode(local.Type), 2, this.VTypeOf(local.Type));
        } else {
          this.Code.EmitLoad(this._slots[local], this._localVTypes[local]);
        }

        if (local.Type.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        } else if (copyStruct && local.Type.IsStruct) {
          this._owner.EmitStructCopyCall(this.Code, local.Type.StructType!);
        }
      }

      public void EmitStoreLocalValue(IrLocal local, Action emitValue) {
        if (this._closureCaptures.TryGetValue(local, out var cap)) {
          var cellVType = VType.Object(this.Pool.AddClass(cap.CellClass));
          this.Code.EmitLoad(0, VType.Object(this.Pool.AddClass(this._receiverInternalName!)));
          this.Code.EmitField(JvmOpcode.Getfield, this._receiverInternalName!, cap.FieldName, $"L{cap.CellClass};", cellVType);
          emitValue();
          this.Code.EmitField(JvmOpcode.Putfield, cap.CellClass, "value", this.Names.Descriptor(local.Type), this.VTypeOf(local.Type));
        } else if (this._capturedCells.TryGetValue(local, out var cell)) {
          var cellVType = VType.Object(this.Pool.AddClass(cell.CellClass));
          this.Code.EmitLoad(cell.CellSlot, cellVType);
          emitValue();
          this.Code.EmitField(JvmOpcode.Putfield, cell.CellClass, "value", this.Names.Descriptor(local.Type), this.VTypeOf(local.Type));
        } else if (this._addressedLocals.TryGetValue(local, out var addr)) {
          this.Code.EmitLoad(addr.ArraySlot, addr.ArrayVType);
          this.Code.EmitConstInt(0);
          emitValue();
          this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(local.Type), 3);
        } else {
          emitValue();
          this.Code.EmitStore(this._slots[local], this._localVTypes[local]);
        }
      }

      public void EmitLoadCellRef(IrLocal local) {
        if (this._closureCaptures.TryGetValue(local, out var cap)) {
          var cellVType = VType.Object(this.Pool.AddClass(cap.CellClass));
          this.Code.EmitLoad(0, VType.Object(this.Pool.AddClass(this._receiverInternalName!)));
          this.Code.EmitField(JvmOpcode.Getfield, this._receiverInternalName!, cap.FieldName, $"L{cap.CellClass};", cellVType);
        } else {
          var cell = this._capturedCells[local];
          this.Code.EmitLoad(cell.CellSlot, VType.Object(this.Pool.AddClass(cell.CellClass)));
        }
      }

      public void EmitStmt(IrStmt stmt) {
        if (!this.Code.IsReachable)
          return;

        switch (stmt) {
          case IrBlock block:
            foreach (var child in block.Statements) {
              this.EmitStmt(child);
              if (!this.Code.IsReachable)
                break;
            }

            break;
          case IrDeclare decl:
            this.EmitDeclare(decl);
            break;
          case IrAssign assign:
            this.EmitStoreLocalValue(assign.Local, () => this.EmitExpr(assign.Value));
            break;
          case IrGlobalAssign assign:
            this.EmitExpr(assign.Value);
            this.Code.EmitStaticField(JvmOpcode.Putstatic, this._owner._internalClassName, this._owner._globalFieldNames[assign.Global],
              this.Names.Descriptor(assign.Global.Type), this.VTypeOf(assign.Global.Type));
            break;
          case IrStore store:
            if (store.Array.Type.IsView) {
              this.EmitViewStore(store.Array, store.Index, store.Value);
            } else {
              this.EmitExpr(store.Array);
              this.EmitExpr(store.Index);
              this.EmitExpr(store.Value);
              this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(store.Array.Type.Element!), 3);
            }

            break;
          case IrFieldStore fieldStore:
            this.EmitFieldStore(fieldStore);
            break;
          case IrExprStmt exprStmt:
            this.EmitExpr(exprStmt.Expression);
            if (exprStmt.Expression.Type != IrType.Void)
              this.Code.EmitPop();
            break;
          case IrWriteLine write: {
            var printStreamType = VType.Object(this.Pool.AddClass("java/io/PrintStream"));
            var notNullLabel = this.Code.DefineLabel();
            this.Code.EmitStaticField(JvmOpcode.Getstatic, "java/lang/System", "out", "Ljava/io/PrintStream;", printStreamType);
            this.EmitExpr(write.Value);
            this.Code.EmitDup();
            this.Code.EmitBranch(JvmOpcode.Ifnonnull, notNullLabel);
            this.Code.EmitPop();
            this.Code.EmitConstString("");
            this.Code.MarkLabel(notNullLabel);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/io/PrintStream", "println", "(Ljava/lang/String;)V", 1);
            break;
          }
          case IrIf conditional:
            this.EmitIf(conditional);
            break;
          case IrWhile loop:
            this.EmitWhile(loop);
            break;
          case IrDoWhile loop:
            this.EmitDoWhile(loop);
            break;
          case IrFor loop:
            this.EmitFor(loop);
            break;
          case IrSwitch choice:
            this.EmitSwitch(choice);
            break;
          case IrBreak:
            this.EmitBreak();
            break;
          case IrContinue:
            this.EmitContinue();
            break;
          case IrReturn ret:
            this.EmitReturn(ret);
            break;
          case IrThrow thrown:
            this.EmitExpr(thrown.Exception);
            this.Code.EmitAthrow();
            break;
          case IrRethrow:
            this.Code.EmitLoad(this._catchExSlots.Peek(), VType.Object(this.Pool.AddClass("java/lang/Throwable")));
            this.Code.EmitAthrow();
            break;
          case IrTry attempt:
            this.EmitTry(attempt);
            break;
          case IrUsing scope:
            this.EmitStmt(scope.Lowered());
            break;
          case IrForEachCollection loop:
            this.EmitForEachCollection(loop);
            break;
          case IrYield produced:
            this.EmitYield(produced);
            break;
          case IrYieldBreak:
            this.EmitYieldBreak();
            break;
          case IrYieldFrom delegated:
            this.EmitYieldFrom(delegated);
            break;
          case IrForEachSequence loop:
            this.EmitForEachSequence(loop);
            break;
        }
      }

      private void EmitDeclare(IrDeclare decl) {
        var local = decl.Local;
        if (this._capturedCells.TryGetValue(local, out var cell)) {
          var cellVType = VType.Object(this.Pool.AddClass(cell.CellClass));
          this.Code.EmitNew(cell.CellClass);
          this.Code.EmitDup();
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, cell.CellClass, "<init>", "()V", 0);
          this.Code.EmitStore(cell.CellSlot, cellVType);
          if (decl.Initializer is { } init)
            this.EmitStoreLocalValue(local, () => this.EmitExpr(init));
          else if (local.Type.IsStruct && local.Type.StructType!.HasDefault)
            this.EmitStoreLocalValue(local, () => this.EmitStructDefault(local.Type.StructType!));
          return;
        }

        if (this._addressedLocals.TryGetValue(local, out var addr)) {
          this.Code.EmitConstInt(1);
          this._owner.EmitArrayAlloc(this.Code, local.Type, this.Names.Descriptor(IrType.Array(local.Type)));
          this.Code.EmitStore(addr.ArraySlot, addr.ArrayVType);
          if (decl.Initializer is { } init)
            this.EmitStoreLocalValue(local, () => this.EmitExpr(init));
          return;
        }

        if (decl.Initializer is { } initializer) {
          this.EmitExpr(initializer);
          this.Code.EmitStore(this._slots[local], this._localVTypes[local]);
        } else if (local.Type.IsStruct && local.Type.StructType!.HasDefault) {
          this.EmitStructDefault(local.Type.StructType!);
          this.Code.EmitStore(this._slots[local], this._localVTypes[local]);
        }
      }

      private void EmitIf(IrIf conditional) {
        var elseLabel = this.Code.DefineLabel();
        this.EmitExpr(conditional.Condition);
        this.Code.EmitBranch(JvmOpcode.Ifeq, elseLabel);
        this.EmitStmt(conditional.Then);
        if (conditional.Else is { } otherwise) {
          var endLabel = this.Code.DefineLabel();
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(elseLabel);
          this.EmitStmt(otherwise);
          this.Code.MarkLabel(endLabel);
        } else {
          this.Code.MarkLabel(elseLabel);
        }
      }

      private void EmitWhile(IrWhile loop) {
        var condLabel = this.Code.DefineLabel();
        var breakLabel = this.Code.DefineLabel();
        this.Code.MarkLabel(condLabel);
        this.EmitExpr(loop.Condition);
        this.Code.EmitBranch(JvmOpcode.Ifeq, breakLabel);
        this._controls.Push((breakLabel, condLabel, this._finallyBlocks.Count));
        this.EmitStmt(loop.Body);
        this._controls.Pop();
        this.Code.EmitGoto(condLabel);
        this.Code.MarkLabel(breakLabel);
      }

      private void EmitDoWhile(IrDoWhile loop) {
        var bodyLabel = this.Code.DefineLabel();
        var condLabel = this.Code.DefineLabel();
        var breakLabel = this.Code.DefineLabel();
        this.Code.MarkLabel(bodyLabel);
        this._controls.Push((breakLabel, condLabel, this._finallyBlocks.Count));
        this.EmitStmt(loop.Body);
        this._controls.Pop();
        this.Code.MarkLabel(condLabel);
        this.EmitExpr(loop.Condition);
        this.Code.EmitBranch(JvmOpcode.Ifne, bodyLabel);
        this.Code.MarkLabel(breakLabel);
      }

      private void EmitFor(IrFor loop) {
        var condLabel = this.Code.DefineLabel();
        var stepLabel = this.Code.DefineLabel();
        var breakLabel = this.Code.DefineLabel();
        this.EmitClonePerIterationCells(loop.PerIteration);
        this.Code.MarkLabel(condLabel);
        this.EmitExpr(loop.Condition);
        this.Code.EmitBranch(JvmOpcode.Ifeq, breakLabel);
        this._controls.Push((breakLabel, stepLabel, this._finallyBlocks.Count));
        this.EmitStmt(loop.Body);
        this._controls.Pop();
        this.Code.MarkLabel(stepLabel);
        this.EmitClonePerIterationCells(loop.PerIteration);
        this.EmitStmt(loop.Step);
        this.Code.EmitGoto(condLabel);
        this.Code.MarkLabel(breakLabel);
      }

      private void EmitClonePerIterationCells(IReadOnlyList<IrLocal> perIteration) {
        foreach (var local in perIteration) {
          if (!this._capturedCells.TryGetValue(local, out var cell))
            continue;

          var cellVType = VType.Object(this.Pool.AddClass(cell.CellClass));
          var valVType = this.VTypeOf(local.Type);
          var tmpSlot = this.Code.AllocateLocal(valVType);
          this.Code.EmitLoad(cell.CellSlot, cellVType);
          this.Code.EmitField(JvmOpcode.Getfield, cell.CellClass, "value", this.Names.Descriptor(local.Type), valVType);
          this.Code.EmitStore(tmpSlot, valVType);
          this.Code.EmitNew(cell.CellClass);
          this.Code.EmitDup();
          this.Code.EmitLoad(tmpSlot, valVType);
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, cell.CellClass, "<init>", $"({this.Names.Descriptor(local.Type)})V", 1);
          this.Code.EmitStore(cell.CellSlot, cellVType);
        }
      }

      private void EmitSwitch(IrSwitch choice) {
        var breakLabel = this.Code.DefineLabel();
        var sectionLabels = new JvmLabel[choice.Sections.Count];
        JvmLabel? defaultLabel = null;
        for (var i = 0; i < choice.Sections.Count; ++i) {
          sectionLabels[i] = this.Code.DefineLabel();
          if (choice.Sections[i].IsDefault)
            defaultLabel = sectionLabels[i];
        }

        var fallDefault = defaultLabel ?? breakLabel;
        if (choice.Value.Type.Integral.IsLong) {
          var valSlot = this.Code.AllocateLocal(VType.Long);
          this.EmitExpr(choice.Value);
          this.Code.EmitStore(valSlot, VType.Long);
          for (var i = 0; i < choice.Sections.Count; ++i) {
            foreach (var key in choice.Sections[i].Labels) {
              this.Code.EmitLoad(valSlot, VType.Long);
              this.Code.EmitConstLong(key);
              this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
              this.Code.EmitBranch(JvmOpcode.Ifeq, sectionLabels[i]);
            }
          }

          this.Code.EmitGoto(fallDefault);
        } else {
          this.EmitExpr(choice.Value);
          var pairs = new List<(int Key, JvmLabel Target)>();
          for (var i = 0; i < choice.Sections.Count; ++i)
            foreach (var key in choice.Sections[i].Labels)
              pairs.Add((unchecked((int)key), sectionLabels[i]));

          if (pairs.Count == 0) {
            this.Code.EmitPop();
            this.Code.EmitGoto(fallDefault);
          } else {
            var min = pairs.Min(p => p.Key);
            var max = pairs.Max(p => p.Key);
            var span = (long)max - min + 1;
            if (span <= 1024 && span <= (pairs.Count * 2L) + 4) {
              var table = new JvmLabel[(int)span];
              Array.Fill(table, fallDefault);
              foreach (var (key, target) in pairs)
                table[key - min] = target;
              this.Code.EmitTableSwitch(fallDefault, min, table);
            } else {
              this.Code.EmitLookupSwitch(fallDefault, pairs);
            }
          }
        }

        this._controls.Push((breakLabel, null, this._finallyBlocks.Count));
        for (var i = 0; i < choice.Sections.Count; ++i) {
          this.Code.MarkLabel(sectionLabels[i]);
          this.EmitStmt(choice.Sections[i].Body);
          this.Code.EmitGoto(breakLabel);
        }

        this._controls.Pop();
        this.Code.MarkLabel(breakLabel);
      }

      private void EmitBreak() {
        var target = this._controls.Peek();
        this.EmitEnclosingFinallies(target.FinallyDepth);
        this.Code.EmitGoto(target.BreakLabel);
      }

      private void EmitContinue() {
        var target = this._controls.First(c => c.ContinueLabel is not null);
        this.EmitEnclosingFinallies(target.FinallyDepth);
        this.Code.EmitGoto(target.ContinueLabel!);
      }

      private void EmitReturn(IrReturn ret) {
        if (this._returnType.IsAsync) {
          var elem = this._returnType.Element!;
          if (elem == IrType.Void) {
            this.EmitEnclosingFinallies(0);
            this.Code.EmitNull();
          } else {
            this.EmitExpr(ret.Value!);
            this.EmitBox(elem);
            if (this._finallyBlocks.Count > 0) {
              var objType = VType.Object(this.Pool.AddClass("java/lang/Object"));
              var tmpSlot = this.Code.AllocateLocal(objType);
              this.Code.EmitStore(tmpSlot, objType);
              this.EmitEnclosingFinallies(0);
              this.Code.EmitLoad(tmpSlot, objType);
            }
          }

          var cfType = VType.Object(this.Pool.AddClass("java/util/concurrent/CompletableFuture"));
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/concurrent/CompletableFuture", "completedFuture",
            "(Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", 1, cfType);
          this.Code.EmitReturn(JvmOpcode.Areturn);
          return;
        }

        if (ret.Value is { } value) {
          this.EmitExpr(value);
          if (this._finallyBlocks.Count > 0) {
            var retVType = this.VTypeOf(this._returnType);
            var tmpSlot = this.Code.AllocateLocal(retVType);
            this.Code.EmitStore(tmpSlot, retVType);
            this.EmitEnclosingFinallies(0);
            this.Code.EmitLoad(tmpSlot, retVType);
          }

          this.Code.EmitReturn(JvmTypeMapper.ReturnOpcode(this._returnType));
        } else {
          this.EmitEnclosingFinallies(0);
          this.Code.EmitReturn(JvmOpcode.Return);
        }
      }

      private void EmitEnclosingFinallies(int targetDepth) {
        if (this._finallyBlocks.Count <= targetDepth)
          return;

        var saved = new List<IrBlock>();
        while (this._finallyBlocks.Count > targetDepth) {
          var block = this._finallyBlocks[^1];
          this._finallyBlocks.RemoveAt(this._finallyBlocks.Count - 1);
          saved.Add(block);
          this.EmitStmt(block);
        }

        for (var i = saved.Count - 1; i >= 0; --i)
          this._finallyBlocks.Add(saved[i]);
      }

      private void EmitTry(IrTry attempt) {
        var tryStart = this.Code.DefineLabel();
        var tryEnd = this.Code.DefineLabel();
        var afterAll = this.Code.DefineLabel();
        var catchRanges = new List<(JvmLabel Start, JvmLabel End)>();

        if (attempt.Finally is { } fin)
          this._finallyBlocks.Add(fin);

        this.Code.MarkLabel(tryStart);
        this.EmitStmt(attempt.Body);
        this.Code.MarkLabel(tryEnd);

        if (attempt.Finally is not null)
          this._finallyBlocks.RemoveAt(this._finallyBlocks.Count - 1);

        if (this.Code.IsReachable) {
          if (attempt.Finally is { } normalFin)
            this.EmitStmt(normalFin);
          this.Code.EmitGoto(afterAll);
        }

        var hasFilters = attempt.Catches.Any(c => c.Filter is not null);
        if (hasFilters && attempt.Catches.Count > 0) {
          // Unified Throwable dispatcher so a false `when` filter falls through to subsequent catch clauses.
          var dispatchLabel = this.Code.DefineLabel();
          this.Code.AddExceptionRange(tryStart, tryEnd, dispatchLabel, 0);
          this.Code.MarkHandler(dispatchLabel, 0);
          var throwableType = VType.Object(this.Pool.AddClass("java/lang/Throwable"));
          var rawExSlot = this.Code.AllocateLocal(throwableType);
          this.Code.EmitStore(rawExSlot, throwableType);

          foreach (var clause in attempt.Catches) {
            var nextClause = this.Code.DefineLabel();
            var catchInternal = this.Names.DeclarationInternalName(clause.Class);
            this.Code.EmitLoad(rawExSlot, throwableType);
            this.Code.EmitInstanceOf(catchInternal);
            this.Code.EmitBranch(JvmOpcode.Ifeq, nextClause);

            var catchCpIdx = this.Pool.AddClass(catchInternal);
            var exVType = VType.Object(catchCpIdx);
            ushort exSlot;
            this.Code.EmitLoad(rawExSlot, throwableType);
            this.Code.EmitCheckCast(catchInternal);
            if (clause.Local is { } exLocal) {
              exSlot = this.Code.AllocateLocal(exVType);
              this.Code.EmitDup();
              this.Code.EmitStore(exSlot, exVType);
              this.EmitStoreLocalValue(exLocal, () => { });
            } else {
              exSlot = this.Code.AllocateLocal(exVType);
              this.Code.EmitStore(exSlot, exVType);
            }

            if (clause.Filter is { } filter) {
              this.EmitExpr(filter);
              this.Code.EmitBranch(JvmOpcode.Ifeq, nextClause);
            }

            this.EmitCatchBody(attempt.Finally, clause.Body, exSlot, afterAll, catchRanges);
            this.Code.MarkLabel(nextClause);
          }

          if (attempt.Finally is { } unmatchedFin)
            this.EmitStmt(unmatchedFin);
          if (this.Code.IsReachable) {
            this.Code.EmitLoad(rawExSlot, throwableType);
            this.Code.EmitAthrow();
          }
        } else {
          foreach (var clause in attempt.Catches) {
            var handlerLabel = this.Code.DefineLabel();
            var catchInternal = this.Names.DeclarationInternalName(clause.Class);
            var catchCpIdx = this.Pool.AddClass(catchInternal);
            this.Code.AddExceptionRange(tryStart, tryEnd, handlerLabel, catchCpIdx);
            this.Code.MarkHandler(handlerLabel, catchCpIdx);

            var exVType = VType.Object(catchCpIdx);
            ushort exSlot;
            if (clause.Local is { } exLocal) {
              exSlot = this.Code.AllocateLocal(exVType);
              this.Code.EmitDup();
              this.Code.EmitStore(exSlot, exVType);
              this.EmitStoreLocalValue(exLocal, () => { });
            } else {
              exSlot = this.Code.AllocateLocal(exVType);
              this.Code.EmitStore(exSlot, exVType);
            }

            this.EmitCatchBody(attempt.Finally, clause.Body, exSlot, afterAll, catchRanges);
          }
        }

        if (attempt.Finally is { } exceptionalFin) {
          var finallyHandler = this.Code.DefineLabel();
          if (!hasFilters || attempt.Catches.Count == 0)
            this.Code.AddExceptionRange(tryStart, tryEnd, finallyHandler, 0);
          foreach (var (cbStart, cbEnd) in catchRanges)
            this.Code.AddExceptionRange(cbStart, cbEnd, finallyHandler, 0);

          if (catchRanges.Count > 0 || !hasFilters || attempt.Catches.Count == 0) {
            this.Code.MarkHandler(finallyHandler, 0);
            var throwableType = VType.Object(this.Pool.AddClass("java/lang/Throwable"));
            var exSlot = this.Code.AllocateLocal(throwableType);
            this.Code.EmitStore(exSlot, throwableType);
            this.EmitStmt(exceptionalFin);
            if (this.Code.IsReachable) {
              this.Code.EmitLoad(exSlot, throwableType);
              this.Code.EmitAthrow();
            }
          }
        }

        this.Code.MarkLabel(afterAll);
      }

      private void EmitCatchBody(IrBlock? finallyBlock, IrBlock body, ushort exSlot, JvmLabel afterAll, List<(JvmLabel Start, JvmLabel End)> catchRanges) {
        this._catchExSlots.Push(exSlot);
        if (finallyBlock is { } catchFin) {
          var catchBodyStart = this.Code.DefineLabel();
          var catchBodyEnd = this.Code.DefineLabel();
          this._finallyBlocks.Add(catchFin);
          this.Code.MarkLabel(catchBodyStart);
          this.EmitStmt(body);
          this.Code.MarkLabel(catchBodyEnd);
          this._finallyBlocks.RemoveAt(this._finallyBlocks.Count - 1);
          catchRanges.Add((catchBodyStart, catchBodyEnd));
          if (this.Code.IsReachable) {
            this.EmitStmt(catchFin);
            this.Code.EmitGoto(afterAll);
          }
        } else {
          this.EmitStmt(body);
          this.Code.EmitGoto(afterAll);
        }

        this._catchExSlots.Pop();
      }

      public void EmitExpr(IrExpr expr) {
        if (!this.Code.IsReachable)
          return;

        switch (expr) {
          case IrConst constant:
            EmitScalarConst(this.Code, constant.Type, constant.Value);
            break;
          case IrStrConst str:
            this.Code.EmitConstString(str.Value);
            break;
          case IrNull:
            this.Code.EmitNull();
            break;
          case IrConstRef constRef:
            if (constRef.Constant.Type.IsArray) {
              this.Code.EmitStaticField(JvmOpcode.Getstatic, this._owner._internalClassName,
                this._owner._constFieldNames[constRef.Constant], this.Names.Descriptor(constRef.Constant.Type), this.VTypeOf(constRef.Constant.Type));
            } else if (constRef.Constant.Type.IsString) {
              this.Code.EmitConstString(constRef.Constant.Strings[0]);
            } else {
              EmitScalarConst(this.Code, constRef.Constant.Type, constRef.Constant.Values[0]);
            }

            break;
          case IrLocalRef localRef:
            this.EmitLoadLocal(localRef.Local);
            break;
          case IrGlobalRef globalRef:
            this.Code.EmitStaticField(JvmOpcode.Getstatic, this._owner._internalClassName,
              this._owner._globalFieldNames[globalRef.Global], this.Names.Descriptor(globalRef.Global.Type), this.VTypeOf(globalRef.Global.Type));
            if (globalRef.Global.Type.Integral == IrType.U8) {
              this.Code.EmitConstInt(0xFF);
              this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            }

            break;
          case IrUnary unary:
            this.EmitUnary(unary);
            break;
          case IrBinary binary:
            this.EmitBinary(binary);
            break;
          case IrConvert conv:
            this.EmitConvert(conv);
            break;
          case IrConditional choice: {
            var falseLabel = this.Code.DefineLabel();
            var endLabel = this.Code.DefineLabel();
            var targetVType = this.VTypeOf(choice.Type);
            this.EmitExpr(choice.Condition);
            this.Code.EmitBranch(JvmOpcode.Ifeq, falseLabel);
            this.EmitExpr(choice.WhenTrue);
            this.Code.CoerceStackTop(targetVType);
            this.Code.EmitGoto(endLabel);
            this.Code.MarkLabel(falseLabel);
            this.EmitExpr(choice.WhenFalse);
            this.Code.CoerceStackTop(targetVType);
            this.Code.MarkLabel(endLabel);
            break;
          }
          case IrArrayLoad load:
            if (load.Array.Type.IsView) {
              this.EmitViewLoad(load.Array, load.Index);
            } else {
              var elem = load.Array.Type.Element!;
              this.EmitExpr(load.Array);
              this.EmitExpr(load.Index);
              this.Code.EmitOp(JvmTypeMapper.ArrayLoadOpcode(elem), 2, this.VTypeOf(elem));
              if (elem.Integral == IrType.U8) {
                this.Code.EmitConstInt(0xFF);
                this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
              } else if (elem.IsStruct) {
                this._owner.EmitStructCopyCall(this.Code, elem.StructType!);
              }
            }

            break;
          case IrArrayLength length:
            this.EmitExpr(length.Array);
            this.Code.EmitOp(JvmOpcode.Arraylength, 1, VType.Int);
            break;
          case IrNewArray create:
            this.EmitNewArray(create);
            break;
          case IrCall call:
            this.EmitCall(call);
            break;
          case IrForeignCall foreignCall:
            this.EmitForeignCall(foreignCall);
            break;
          case IrStep step:
            this.EmitStep(step);
            break;
          case IrBind bind:
            this.EmitBind(bind);
            break;
          case IrIsNull isNull:
            this.EmitIsNullCheck(isNull.Value);
            break;
          case IrNone:
            this.Code.EmitNull();
            break;
          case IrSome some:
            this.EmitExpr(some.Value);
            if (!some.Value.Type.IsStruct)
              this.EmitBox(some.Value.Type);
            break;
          case IrIsNone isNone:
            this.EmitIsNullCheck(isNone.Value);
            break;
          case IrUnwrap unwrap: {
            var underlying = unwrap.Value.Type.Underlying!;
            this.EmitExpr(unwrap.Value);
            if (!unwrap.IsTested) {
              var okLabel = this.Code.DefineLabel();
              this.Code.EmitDup();
              this.Code.EmitBranch(JvmOpcode.Ifnonnull, okLabel);
              this.Code.EmitNew("java/lang/IllegalStateException");
              this.Code.EmitDup();
              this.Code.EmitConstString("Nullable object must have a value.");
              this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/IllegalStateException", "<init>", "(Ljava/lang/String;)V", 1);
              this.Code.EmitAthrow();
              this.Code.MarkLabel(okLabel);
            }

            if (!underlying.IsStruct)
              this.EmitUnbox(underlying);
            break;
          }
          case IrCoalesce coalesce: {
            var underlying = coalesce.Value.Type.Underlying!;
            this.EmitExpr(coalesce.Value);
            if (coalesce.Fallback.Type.IsOptional) {
              var endLabel = this.Code.DefineLabel();
              this.Code.EmitDup();
              this.Code.EmitBranch(JvmOpcode.Ifnonnull, endLabel);
              this.Code.EmitPop();
              this.EmitExpr(coalesce.Fallback);
              this.Code.MarkLabel(endLabel);
            } else {
              var hasVal = this.Code.DefineLabel();
              var endLabel = this.Code.DefineLabel();
              this.Code.EmitDup();
              this.Code.EmitBranch(JvmOpcode.Ifnonnull, hasVal);
              this.Code.EmitPop();
              this.EmitExpr(coalesce.Fallback);
              this.Code.EmitGoto(endLabel);
              this.Code.MarkLabel(hasVal);
              if (!underlying.IsStruct)
                this.EmitUnbox(underlying);
              this.Code.MarkLabel(endLabel);
            }

            break;
          }
          case IrStructNew created:
            this.EmitStructNew(created);
            break;
          case IrStructDefault def:
            this.EmitStructDefault(def.Type.StructType!);
            break;
          case IrStructEquals eq:
            this.EmitStructEquals(eq);
            break;
          case IrNew created:
            this.EmitNewObject(created);
            break;
          case IrMethodCall call:
            this.EmitMethodCall(call);
            break;
          case IrFieldLoad load:
            this.EmitFieldLoad(load);
            break;
          case IrReferenceEquals eq:
            this.EmitReferenceEquals(eq.Left, eq.Right);
            break;
          case IrTypeTest test:
            this.EmitExpr(test.Value);
            this.Code.EmitInstanceOf(this.Names.ReferenceInternalName(test.Target));
            break;
          case IrCast cast:
            this.EmitExpr(cast.Value);
            this.Code.EmitCheckCast(this.Names.ReferenceInternalName(cast.Type));
            break;
          case IrAsCast asCast:
            this.EmitAsCast(asCast);
            break;
          case IrGenericEquals eq:
            this.EmitGenericEquals(eq.Left, eq.Right);
            break;
          case IrGenericCompare cmp:
            this.EmitGenericCompare(cmp.Left, cmp.Right);
            break;
          case IrLambda lambda:
            this.EmitLambda(lambda);
            break;
          case IrFunctionRef funcRef:
            this.EmitFunctionRef(funcRef);
            break;
          case IrBoundMethod bound:
            this.EmitBoundMethod(bound);
            break;
          case IrInvoke invoke:
            this.EmitInvoke(invoke);
            break;
          case IrCollection col:
            this.EmitCollection(col);
            break;
          case IrArraySequence seq:
            this.EmitArraySequence(seq);
            break;
          case IrAsync asyncOp:
            this.EmitAsyncOp(asyncOp);
            break;
          case IrAwait awaited:
            this.EmitAwait(awaited);
            break;
          case IrThrowExpression thrown:
            this.EmitExpr(thrown.Exception);
            this.Code.EmitAthrow();
            break;
          case IrIntrinsic intrinsic:
            this.EmitIntrinsic(intrinsic);
            break;
        }
      }

      private void EmitNewArray(IrNewArray create) {
        var lenSlot = this.Code.AllocateLocal(VType.Int);
        this.EmitExpr(create.Length);
        this.Code.EmitStore(lenSlot, VType.Int);
        this.Code.EmitLoad(lenSlot, VType.Int);
        var okLabel = this.Code.DefineLabel();
        this.Code.EmitBranch(JvmOpcode.Ifge, okLabel);
        this.EmitThrowArithmetic("Array length must not be negative.");
        this.Code.MarkLabel(okLabel);
        this.Code.EmitLoad(lenSlot, VType.Int);
        var elem = create.Type.Element!;
        this._owner.EmitArrayAlloc(this.Code, elem, this.Names.Descriptor(create.Type));
        if (elem.StructType is { HasDefault: true } st) {
          var arrVType = this.VTypeOf(create.Type);
          var arrSlot = this.Code.AllocateLocal(arrVType);
          var iSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitStore(arrSlot, arrVType);
          this.Code.EmitConstInt(0);
          this.Code.EmitStore(iSlot, VType.Int);
          var loopStart = this.Code.DefineLabel();
          var loopEnd = this.Code.DefineLabel();
          this.Code.MarkLabel(loopStart);
          this.Code.EmitLoad(iSlot, VType.Int);
          this.Code.EmitLoad(lenSlot, VType.Int);
          this.Code.EmitBranch(JvmOpcode.IfIcmpge, loopEnd);
          this.Code.EmitLoad(arrSlot, arrVType);
          this.Code.EmitLoad(iSlot, VType.Int);
          this.EmitStructDefault(st);
          this.Code.EmitOp(JvmOpcode.Aastore, 3);
          this.Code.EmitLoad(iSlot, VType.Int);
          this.Code.EmitConstInt(1);
          this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
          this.Code.EmitStore(iSlot, VType.Int);
          this.Code.EmitGoto(loopStart);
          this.Code.MarkLabel(loopEnd);
          this.Code.EmitLoad(arrSlot, arrVType);
        }
      }

      private void EmitCall(IrCall call) {
        foreach (var arg in call.Arguments)
          this.EmitExpr(arg);

        var target = this._owner._functions[call.Function];
        var retVType = target.ReturnType == IrType.Void ? (VType?)null : this.VTypeOf(target.ReturnType);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, JvmTypeMapper.SanitizeIdentifier(target.Name),
          this.Names.MethodDescriptor(target), call.Arguments.Count, retVType);
        if (target.ReturnType.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
      }

      private void EmitForeignCall(IrForeignCall foreignCall) {
        foreach (var arg in foreignCall.Arguments)
          this.EmitExpr(arg);

        var target = this._owner._foreignFunctions[foreignCall.Function];
        var retVType = target.ReturnType == IrType.Void ? (VType?)null : this.VTypeOf(target.ReturnType);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, this._owner._internalClassName, JvmTypeMapper.SanitizeIdentifier(target.Name),
          this.Names.MethodDescriptor(target), foreignCall.Arguments.Count, retVType);
        if (target.ReturnType.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
      }

      private void EmitBind(IrBind bind) {
        var valVType = this.VTypeOf(bind.Local.Type);
        var tmpSlot = this.Code.AllocateLocal(valVType);
        this.EmitExpr(bind.Value);
        this.Code.EmitStore(tmpSlot, valVType);
        this.EmitStoreLocalValue(bind.Local, () => this.Code.EmitLoad(tmpSlot, valVType));
        this.Code.EmitLoad(tmpSlot, valVType);
      }

      private void EmitIsNullCheck(IrExpr value) {
        var trueLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.EmitExpr(value);
        this.Code.EmitBranch(JvmOpcode.Ifnull, trueLabel);
        this.Code.EmitConstInt(0);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(trueLabel);
        this.Code.EmitConstInt(1);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitBox(IrType type, bool normalizeNegativeZero = false) {
        if (!JvmTypeMapper.IsJvmPrimitive(type))
          return;

        if (normalizeNegativeZero && (type == IrType.F32 || type == IrType.F64)) {
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitDup();
          if (type == IrType.F32) {
            this.Code.EmitConstFloatBits(0);
            this.Code.EmitOp(JvmOpcode.Fcmpl, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
            this.Code.EmitPop();
            this.Code.EmitConstFloatBits(0);
          } else {
            this.Code.EmitConstDoubleBits(0L);
            this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
            this.Code.EmitPop();
            this.Code.EmitConstDoubleBits(0L);
          }

          this.Code.MarkLabel(okLabel);
        }

        if (type.Integral == IrType.U8)
          this.Code.EmitOp(JvmOpcode.I2b, 1, VType.Int);

        var (boxClass, _) = JvmTypeMapper.BoxInfo(type);
        var primDesc = this.Names.Descriptor(type);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, boxClass, "valueOf", $"({primDesc})L{boxClass};", 1, VType.Object(this.Pool.AddClass(boxClass)));
      }

      private void EmitUnbox(IrType underlying) {
        var (boxClass, unboxMethod) = JvmTypeMapper.BoxInfo(underlying);
        var primDesc = this.Names.Descriptor(underlying);
        this.Code.EmitInvoke(JvmOpcode.Invokevirtual, boxClass, unboxMethod, $"(){primDesc}", 0, this.VTypeOf(underlying));
        if (underlying.Integral == IrType.U8) {
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
        }
      }

      private void EmitUnboxFromObject(IrType targetType) {
        if (JvmTypeMapper.IsJvmPrimitive(targetType)) {
          var (boxClass, _) = JvmTypeMapper.BoxInfo(targetType);
          this.Code.EmitCheckCast(boxClass);
          this.EmitUnbox(targetType);
        } else {
          var internalName = this.Names.ReferenceInternalName(targetType);
          if (internalName != "java/lang/Object")
            this.Code.EmitCheckCast(internalName);
        }
      }

      private void EmitNewException(IrNew created) {
        var cls = created.Type.ClassType!;
        var internalName = JvmTypeMapper.ExceptionInternalName(cls);
        this.Code.EmitNew(internalName);
        this.Code.EmitDup();
        if (created.Arguments.Count == 0) {
          this.Code.EmitConstString(IrExceptionClass.DefaultMessage(cls));
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "(Ljava/lang/String;)V", 1);
        } else if (created.Arguments.Count == 1) {
          this.EmitExpr(created.Arguments[0]);
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "(Ljava/lang/String;)V", 1);
        } else if (JvmTypeMapper.HasMessageAndCauseCtor(internalName)) {
          this.EmitExpr(created.Arguments[0]);
          this.EmitExpr(created.Arguments[1]);
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "(Ljava/lang/String;Ljava/lang/Throwable;)V", 2);
        } else {
          this.EmitExpr(created.Arguments[0]);
          this.Code.EmitInvoke(JvmOpcode.Invokespecial, internalName, "<init>", "(Ljava/lang/String;)V", 1);
          this.Code.EmitDup();
          this.EmitExpr(created.Arguments[1]);
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/Throwable", "initCause", "(Ljava/lang/Throwable;)Ljava/lang/Throwable;", 1,
            VType.Object(this.Pool.AddClass("java/lang/Throwable")));
          this.Code.EmitPop();
        }
      }

      private void EmitExceptionFieldLoad(IrFieldLoad load) {
        this.EmitExpr(load.Target);
        if (load.Field == IrExceptionClass.Message) {
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/Throwable", "getMessage", "()Ljava/lang/String;", 0,
            VType.Object(this.Pool.AddClass("java/lang/String")));
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/Throwable", "getCause", "()Ljava/lang/Throwable;", 0,
            VType.Object(this.Pool.AddClass("java/lang/Throwable")));
          this.Code.EmitCheckCast("java/lang/Exception");
        }
      }

      private void EmitUnary(IrUnary unary) {
        this.EmitExpr(unary.Operand);
        var kind = unary.Operand.Type.Integral.Kind;
        switch (unary.Op) {
          case IrUnaryOp.Negate:
            this.Code.EmitOp(kind switch {
              IrTypeKind.I64 => JvmOpcode.Lneg,
              IrTypeKind.F32 => JvmOpcode.Fneg,
              IrTypeKind.F64 => JvmOpcode.Dneg,
              _ => JvmOpcode.Ineg
            }, 1, this.VTypeOf(unary.Type));
            break;
          case IrUnaryOp.NegateChecked:
            if (kind == IrTypeKind.I64)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "negateExact", "(J)J", 1, VType.Long);
            else
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "negateExact", "(I)I", 1, VType.Int);
            break;
          case IrUnaryOp.BitNot:
            if (kind is IrTypeKind.I64 or IrTypeKind.U64) {
              this.Code.EmitConstLong(-1L);
              this.Code.EmitOp(JvmOpcode.Lxor, 2, VType.Long);
            } else {
              this.Code.EmitConstInt(-1);
              this.Code.EmitOp(JvmOpcode.Ixor, 2, VType.Int);
            }

            break;
          case IrUnaryOp.LogicalNot:
            this.Code.EmitConstInt(1);
            this.Code.EmitOp(JvmOpcode.Ixor, 2, VType.Int);
            break;
        }
      }

      private void EmitBinary(IrBinary binary) {
        if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr) {
          this.EmitShortCircuit(binary);
          return;
        }

        if (binary.Op >= IrBinaryOp.Equal) {
          this.EmitComparison(binary);
          return;
        }

        var type = binary.Left.Type.Integral;
        var isLong = type.IsLong;
        var isFloat = type == IrType.F32;
        var isDouble = type == IrType.F64;
        var isUnsigned = !type.IsSigned && type.IsInteger;

        switch (binary.Op) {
          case IrBinaryOp.Add:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isDouble ? JvmOpcode.Dadd : isFloat ? JvmOpcode.Fadd : isLong ? JvmOpcode.Ladd : JvmOpcode.Iadd, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.Subtract:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isDouble ? JvmOpcode.Dsub : isFloat ? JvmOpcode.Fsub : isLong ? JvmOpcode.Lsub : JvmOpcode.Isub, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.Multiply:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isDouble ? JvmOpcode.Dmul : isFloat ? JvmOpcode.Fmul : isLong ? JvmOpcode.Lmul : JvmOpcode.Imul, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.AddChecked:
          case IrBinaryOp.SubtractChecked:
          case IrBinaryOp.MultiplyChecked:
            this.EmitCheckedArithmetic(binary.Op, type, binary.Left, binary.Right);
            break;
          case IrBinaryOp.Divide:
          case IrBinaryOp.Remainder:
            this.EmitDivRem(binary.Op, type, binary.Left, binary.Right);
            break;
          case IrBinaryOp.And:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? JvmOpcode.Land : JvmOpcode.Iand, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.Or:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? JvmOpcode.Lor : JvmOpcode.Ior, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.Xor:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? JvmOpcode.Lxor : JvmOpcode.Ixor, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.ShiftLeft:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? JvmOpcode.Lshl : JvmOpcode.Ishl, 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.ShiftRight:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? (isUnsigned ? JvmOpcode.Lushr : JvmOpcode.Lshr) : (isUnsigned ? JvmOpcode.Iushr : JvmOpcode.Ishr), 2, this.VTypeOf(type));
            break;
          case IrBinaryOp.ShiftRightLogical:
            this.EmitExpr(binary.Left);
            this.EmitExpr(binary.Right);
            this.Code.EmitOp(isLong ? JvmOpcode.Lushr : JvmOpcode.Iushr, 2, this.VTypeOf(type));
            break;
        }
      }

      private void EmitCheckedArithmetic(IrBinaryOp op, IrType type, IrExpr left, IrExpr right) {
        this.EmitExpr(left);
        this.EmitExpr(right);
        if (type.IsSigned) {
          var method = op switch {
            IrBinaryOp.AddChecked => "addExact",
            IrBinaryOp.SubtractChecked => "subtractExact",
            _ => "multiplyExact"
          };
          if (type.IsLong)
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(JJ)J", 2, VType.Long);
          else
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(II)I", 2, VType.Int);
          return;
        }

        if (!type.IsLong) {
          var rSlot = this.Code.AllocateLocal(VType.Int);
          var lSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitStore(rSlot, VType.Int);
          this.Code.EmitStore(lSlot, VType.Int);
          if (op == IrBinaryOp.SubtractChecked) {
            var okLabel = this.Code.DefineLabel();
            this.Code.EmitLoad(lSlot, VType.Int);
            this.Code.EmitLoad(rSlot, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compareUnsigned", "(II)I", 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifge, okLabel);
            this.EmitThrowArithmetic("Unsigned 32-bit subtraction underflow.");
            this.Code.MarkLabel(okLabel);
            this.Code.EmitLoad(lSlot, VType.Int);
            this.Code.EmitLoad(rSlot, VType.Int);
            this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
          } else {
            var resSlot = this.Code.AllocateLocal(VType.Long);
            this.Code.EmitLoad(lSlot, VType.Int);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            this.Code.EmitConstLong(0xFFFFFFFFL);
            this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            this.Code.EmitLoad(rSlot, VType.Int);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            this.Code.EmitConstLong(0xFFFFFFFFL);
            this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            this.Code.EmitOp(op == IrBinaryOp.AddChecked ? JvmOpcode.Ladd : JvmOpcode.Lmul, 2, VType.Long);
            this.Code.EmitDup();
            this.Code.EmitStore(resSlot, VType.Long);
            this.Code.EmitConstInt(32);
            this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
            this.Code.EmitConstLong(0L);
            this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
            var okLabel = this.Code.DefineLabel();
            this.Code.EmitBranch(JvmOpcode.Ifeq, okLabel);
            this.EmitThrowArithmetic("Unsigned 32-bit overflow.");
            this.Code.MarkLabel(okLabel);
            this.Code.EmitLoad(resSlot, VType.Long);
            this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
          }

          return;
        }

        var rb = this.Code.AllocateLocal(VType.Long);
        var la = this.Code.AllocateLocal(VType.Long);
        this.Code.EmitStore(rb, VType.Long);
        this.Code.EmitStore(la, VType.Long);
        var ok64 = this.Code.DefineLabel();
        if (op == IrBinaryOp.AddChecked) {
          var sum = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitOp(JvmOpcode.Ladd, 2, VType.Long);
          this.Code.EmitDup();
          this.Code.EmitStore(sum, VType.Long);
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, ok64);
          this.EmitThrowArithmetic("Unsigned 64-bit addition overflow.");
          this.Code.MarkLabel(ok64);
          this.Code.EmitLoad(sum, VType.Long);
        } else if (op == IrBinaryOp.SubtractChecked) {
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, ok64);
          this.EmitThrowArithmetic("Unsigned 64-bit subtraction underflow.");
          this.Code.MarkLabel(ok64);
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lsub, 2, VType.Long);
        } else {
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitConstLong(0L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifeq, ok64);
          this.Code.EmitConstLong(-1L);
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "divideUnsigned", "(JJ)J", 2, VType.Long);
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, ok64);
          this.EmitThrowArithmetic("Unsigned 64-bit multiplication overflow.");
          this.Code.MarkLabel(ok64);
          this.Code.EmitLoad(la, VType.Long);
          this.Code.EmitLoad(rb, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lmul, 2, VType.Long);
        }
      }

      private void EmitDivRem(IrBinaryOp op, IrType type, IrExpr left, IrExpr right) {
        this.EmitExpr(left);
        this.EmitExpr(right);
        var isDiv = op == IrBinaryOp.Divide;
        if (type == IrType.F32) {
          this.Code.EmitOp(isDiv ? JvmOpcode.Fdiv : JvmOpcode.Frem, 2, VType.Float);
          return;
        }

        if (type == IrType.F64) {
          this.Code.EmitOp(isDiv ? JvmOpcode.Ddiv : JvmOpcode.Drem, 2, VType.Double);
          return;
        }

        if (!type.IsSigned) {
          var method = isDiv ? "divideUnsigned" : "remainderUnsigned";
          if (type.IsLong)
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", method, "(JJ)J", 2, VType.Long);
          else
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", method, "(II)I", 2, VType.Int);
          return;
        }

        if (type.IsLong) {
          var rSlot = this.Code.AllocateLocal(VType.Long);
          var lSlot = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitStore(rSlot, VType.Long);
          this.Code.EmitStore(lSlot, VType.Long);
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitLoad(lSlot, VType.Long);
          this.Code.EmitConstLong(long.MinValue);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
          this.Code.EmitLoad(rSlot, VType.Long);
          this.Code.EmitConstLong(-1L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
          this.EmitThrowArithmetic("Signed 64-bit division overflow.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(lSlot, VType.Long);
          this.Code.EmitLoad(rSlot, VType.Long);
          this.Code.EmitOp(isDiv ? JvmOpcode.Ldiv : JvmOpcode.Lrem, 2, VType.Long);
        } else {
          var rSlot = this.Code.AllocateLocal(VType.Int);
          var lSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitStore(rSlot, VType.Int);
          this.Code.EmitStore(lSlot, VType.Int);
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitLoad(lSlot, VType.Int);
          this.Code.EmitConstInt(int.MinValue);
          this.Code.EmitBranch(JvmOpcode.IfIcmpne, okLabel);
          this.Code.EmitLoad(rSlot, VType.Int);
          this.Code.EmitConstInt(-1);
          this.Code.EmitBranch(JvmOpcode.IfIcmpne, okLabel);
          this.EmitThrowArithmetic("Signed 32-bit division overflow.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(lSlot, VType.Int);
          this.Code.EmitLoad(rSlot, VType.Int);
          this.Code.EmitOp(isDiv ? JvmOpcode.Idiv : JvmOpcode.Irem, 2, VType.Int);
        }
      }

      private void EmitShortCircuit(IrBinary binary) {
        var shortLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.EmitExpr(binary.Left);
        if (binary.Op == IrBinaryOp.LogicalAnd) {
          this.Code.EmitBranch(JvmOpcode.Ifeq, shortLabel);
          this.EmitExpr(binary.Right);
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(shortLabel);
          this.Code.EmitConstInt(0);
        } else {
          this.Code.EmitBranch(JvmOpcode.Ifne, shortLabel);
          this.EmitExpr(binary.Right);
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(shortLabel);
          this.Code.EmitConstInt(1);
        }

        this.Code.MarkLabel(endLabel);
      }

      private void EmitComparison(IrBinary binary) {
        this.EmitExpr(binary.Left);
        this.EmitExpr(binary.Right);
        var type = binary.Left.Type.Integral;
        var trueLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();

        if (type == IrType.F32 || type == IrType.F64) {
          var useGreater = binary.Op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
          var cmpOp = type == IrType.F32
            ? (useGreater ? JvmOpcode.Fcmpg : JvmOpcode.Fcmpl)
            : (useGreater ? JvmOpcode.Dcmpg : JvmOpcode.Dcmpl);
          this.Code.EmitOp(cmpOp, 2, VType.Int);
          this.Code.EmitBranch(ZeroBranchOpcode(binary.Op), trueLabel);
        } else if (type.IsLong) {
          if (!type.IsSigned && binary.Op is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual))
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
          else
            this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(ZeroBranchOpcode(binary.Op), trueLabel);
        } else if (type == IrType.U32 && binary.Op is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual)) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compareUnsigned", "(II)I", 2, VType.Int);
          this.Code.EmitBranch(ZeroBranchOpcode(binary.Op), trueLabel);
        } else {
          this.Code.EmitBranch(IntCmpBranchOpcode(binary.Op), trueLabel);
        }

        this.Code.EmitConstInt(0);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(trueLabel);
        this.Code.EmitConstInt(1);
        this.Code.MarkLabel(endLabel);
      }

      private static JvmOpcode ZeroBranchOpcode(IrBinaryOp op) => op switch {
        IrBinaryOp.Equal => JvmOpcode.Ifeq,
        IrBinaryOp.NotEqual => JvmOpcode.Ifne,
        IrBinaryOp.Less => JvmOpcode.Iflt,
        IrBinaryOp.LessOrEqual => JvmOpcode.Ifle,
        IrBinaryOp.Greater => JvmOpcode.Ifgt,
        _ => JvmOpcode.Ifge
      };

      private static JvmOpcode IntCmpBranchOpcode(IrBinaryOp op) => op switch {
        IrBinaryOp.Equal => JvmOpcode.IfIcmpeq,
        IrBinaryOp.NotEqual => JvmOpcode.IfIcmpne,
        IrBinaryOp.Less => JvmOpcode.IfIcmplt,
        IrBinaryOp.LessOrEqual => JvmOpcode.IfIcmple,
        IrBinaryOp.Greater => JvmOpcode.IfIcmpgt,
        _ => JvmOpcode.IfIcmpge
      };

      private void EmitConvert(IrConvert conv) {
        this.EmitExpr(conv.Operand);
        var src = conv.Operand.Type.Integral;
        var dst = conv.Type.Integral;
        if (src == dst)
          return;

        if (src.IsFloat && dst.IsFloat) {
          this.Code.EmitOp(src == IrType.F32 ? JvmOpcode.F2d : JvmOpcode.D2f, 1, this.VTypeOf(dst));
          return;
        }

        if (src.IsInteger && dst.IsFloat) {
          this.EmitIntToFloat(src, dst);
          return;
        }

        if (src.IsFloat && dst.IsInteger) {
          this.EmitFloatToInt(src, dst, conv.Contract);
          return;
        }

        if (conv.Contract == IrConversion.Trap && !IrOperators.Contains(dst, src))
          this.EmitIntRangeCheck(src, dst);

        this.EmitIntWrapConvert(src, dst);
      }

      private void EmitIntToFloat(IrType src, IrType dst) {
        var toDouble = dst == IrType.F64;
        if (src == IrType.U32) {
          this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
          this.Code.EmitConstLong(0xFFFFFFFFL);
          this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
          this.Code.EmitOp(toDouble ? JvmOpcode.L2d : JvmOpcode.L2f, 1, this.VTypeOf(dst));
        } else if (src == IrType.U64) {
          var valSlot = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitStore(valSlot, VType.Long);
          var negLabel = this.Code.DefineLabel();
          var endLabel = this.Code.DefineLabel();
          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitConstLong(0L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Iflt, negLabel);
          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitOp(toDouble ? JvmOpcode.L2d : JvmOpcode.L2f, 1, this.VTypeOf(dst));
          this.Code.EmitGoto(endLabel);
          this.Code.MarkLabel(negLabel);
          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitConstInt(1);
          this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
          this.Code.EmitLoad(valSlot, VType.Long);
          this.Code.EmitConstLong(1L);
          this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
          this.Code.EmitOp(JvmOpcode.Lor, 2, VType.Long);
          this.Code.EmitOp(toDouble ? JvmOpcode.L2d : JvmOpcode.L2f, 1, this.VTypeOf(dst));
          if (toDouble) {
            this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(2.0));
            this.Code.EmitOp(JvmOpcode.Dmul, 2, VType.Double);
          } else {
            this.Code.EmitConstFloatBits(BitConverter.SingleToInt32Bits(2.0f));
            this.Code.EmitOp(JvmOpcode.Fmul, 2, VType.Float);
          }

          this.Code.MarkLabel(endLabel);
        } else if (src == IrType.I64) {
          this.Code.EmitOp(toDouble ? JvmOpcode.L2d : JvmOpcode.L2f, 1, this.VTypeOf(dst));
        } else {
          this.Code.EmitOp(toDouble ? JvmOpcode.I2d : JvmOpcode.I2f, 1, this.VTypeOf(dst));
        }
      }

      private void EmitFloatToInt(IrType src, IrType dst, IrConversion contract) {
        if (src == IrType.F32)
          this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);

        if (contract == IrConversion.Trap) {
          var dSlot = this.Code.AllocateLocal(VType.Double);
          this.Code.EmitStore(dSlot, VType.Double);
          var trapLabel = this.Code.DefineLabel();
          var okLabel = this.Code.DefineLabel();

          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "isNaN", "(D)Z", 1, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, trapLabel);

          if (dst == IrType.I64) {
            this.Code.EmitLoad(dSlot, VType.Double);
            this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(-9223372036854775808.0));
            this.Code.EmitOp(JvmOpcode.Dcmpg, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
          } else {
            var lowExclusive = (double)IrOperators.Minimum(dst) - 1.0;
            this.Code.EmitLoad(dSlot, VType.Double);
            this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(lowExclusive));
            this.Code.EmitOp(JvmOpcode.Dcmpg, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifle, trapLabel);
          }

          var highExclusive = dst == IrType.U64
            ? 18446744073709551616.0
            : dst == IrType.I64
              ? 9223372036854775808.0
              : (double)IrOperators.Maximum(dst) + 1.0;
          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(highExclusive));
          this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, trapLabel);
          this.Code.EmitGoto(okLabel);

          this.Code.MarkLabel(trapLabel);
          this.EmitThrowArithmetic($"Floating-point value out of range for '{dst}'.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(dSlot, VType.Double);
        }

        if (dst == IrType.I32) {
          this.Code.EmitOp(JvmOpcode.D2i, 1, VType.Int);
        } else if (dst == IrType.I64) {
          this.Code.EmitOp(JvmOpcode.D2l, 1, VType.Long);
        } else if (dst == IrType.U64) {
          var dSlot = this.Code.AllocateLocal(VType.Double);
          this.Code.EmitStore(dSlot, VType.Double);
          var zeroLabel = this.Code.DefineLabel();
          var maxLabel = this.Code.DefineLabel();
          var highLabel = this.Code.DefineLabel();
          var endLabel = this.Code.DefineLabel();

          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitConstDoubleBits(0L);
          this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifle, zeroLabel);

          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(18446744073709551616.0));
          this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, maxLabel);

          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(9223372036854775808.0));
          this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, highLabel);

          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitOp(JvmOpcode.D2l, 1, VType.Long);
          this.Code.EmitGoto(endLabel);

          this.Code.MarkLabel(zeroLabel);
          this.Code.EmitConstLong(0L);
          this.Code.EmitGoto(endLabel);

          this.Code.MarkLabel(maxLabel);
          this.Code.EmitConstLong(-1L);
          this.Code.EmitGoto(endLabel);

          this.Code.MarkLabel(highLabel);
          this.Code.EmitLoad(dSlot, VType.Double);
          this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(9223372036854775808.0));
          this.Code.EmitOp(JvmOpcode.Dsub, 2, VType.Double);
          this.Code.EmitOp(JvmOpcode.D2l, 1, VType.Long);
          this.Code.EmitConstLong(long.MinValue);
          this.Code.EmitOp(JvmOpcode.Lor, 2, VType.Long);

          this.Code.MarkLabel(endLabel);
        } else {
          this.Code.EmitOp(JvmOpcode.D2l, 1, VType.Long);
          var minVal = (long)IrOperators.Minimum(dst);
          var maxVal = (long)IrOperators.Maximum(dst);
          this.Code.EmitConstLong(minVal);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "max", "(JJ)J", 2, VType.Long);
          this.Code.EmitConstLong(maxVal);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "min", "(JJ)J", 2, VType.Long);
          this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
        }
      }

      private void EmitIntRangeCheck(IrType src, IrType dst) {
        var okLabel = this.Code.DefineLabel();
        var trapLabel = this.Code.DefineLabel();
        if (src.IsLong) {
          var vSlot = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitStore(vSlot, VType.Long);
          if (src == IrType.U64) {
            var maxU64 = unchecked((long)(ulong)IrOperators.Maximum(dst));
            this.Code.EmitLoad(vSlot, VType.Long);
            this.Code.EmitConstLong(maxU64);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifgt, trapLabel);
          } else if (dst == IrType.U64) {
            this.Code.EmitLoad(vSlot, VType.Long);
            this.Code.EmitConstLong(0L);
            this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
          } else {
            var minVal = (long)IrOperators.Minimum(dst);
            var maxVal = (long)IrOperators.Maximum(dst);
            this.Code.EmitLoad(vSlot, VType.Long);
            this.Code.EmitConstLong(minVal);
            this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
            this.Code.EmitLoad(vSlot, VType.Long);
            this.Code.EmitConstLong(maxVal);
            this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifgt, trapLabel);
          }

          this.Code.EmitGoto(okLabel);
          this.Code.MarkLabel(trapLabel);
          this.EmitThrowArithmetic($"Integer conversion from '{src}' to '{dst}' overflowed.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(vSlot, VType.Long);
        } else {
          var vSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitStore(vSlot, VType.Int);
          if (src == IrType.U32) {
            var maxU32 = unchecked((int)(uint)IrOperators.Maximum(dst));
            this.Code.EmitLoad(vSlot, VType.Int);
            this.Code.EmitConstInt(maxU32);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compareUnsigned", "(II)I", 2, VType.Int);
            this.Code.EmitBranch(JvmOpcode.Ifgt, trapLabel);
          } else {
            if (!dst.IsSigned) {
              this.Code.EmitLoad(vSlot, VType.Int);
              this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
              if (dst.Bits < 32) {
                this.Code.EmitLoad(vSlot, VType.Int);
                this.Code.EmitConstInt((int)IrOperators.Maximum(dst));
                this.Code.EmitBranch(JvmOpcode.IfIcmpgt, trapLabel);
              }
            } else {
              this.Code.EmitLoad(vSlot, VType.Int);
              this.Code.EmitConstInt((int)IrOperators.Minimum(dst));
              this.Code.EmitBranch(JvmOpcode.IfIcmplt, trapLabel);
              this.Code.EmitLoad(vSlot, VType.Int);
              this.Code.EmitConstInt((int)IrOperators.Maximum(dst));
              this.Code.EmitBranch(JvmOpcode.IfIcmpgt, trapLabel);
            }
          }

          this.Code.EmitGoto(okLabel);
          this.Code.MarkLabel(trapLabel);
          this.EmitThrowArithmetic($"Integer conversion from '{src}' to '{dst}' overflowed.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(vSlot, VType.Int);
        }
      }

      private void EmitIntWrapConvert(IrType src, IrType dst) {
        if (!src.IsLong && dst.IsLong) {
          this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
          if (src == IrType.U32) {
            this.Code.EmitConstLong(0xFFFFFFFFL);
            this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
          }

          return;
        }

        if (src.IsLong && !dst.IsLong)
          this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);

        switch (dst.Kind) {
          case IrTypeKind.I8:
            this.Code.EmitOp(JvmOpcode.I2b, 1, VType.Int);
            break;
          case IrTypeKind.U8:
            this.Code.EmitConstInt(0xFF);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            break;
          case IrTypeKind.I16:
            this.Code.EmitOp(JvmOpcode.I2s, 1, VType.Int);
            break;
          case IrTypeKind.U16:
            this.Code.EmitOp(JvmOpcode.I2c, 1, VType.Int);
            break;
        }
      }

      private void EmitStep(IrStep step) {
        var type = step.Type.Integral;
        var vtype = this.VTypeOf(type);
        var oldSlot = this.Code.AllocateLocal(vtype);
        var newSlot = this.Code.AllocateLocal(vtype);

        if (step.Target is IrLocalRef localRef) {
          this.EmitLoadLocal(localRef.Local, copyStruct: false);
          this.Code.EmitStore(oldSlot, vtype);
          this.EmitSteppedValue(oldSlot, newSlot, type, step.Increment, step.Trap);
          this.EmitStoreLocalValue(localRef.Local, () => this.Code.EmitLoad(newSlot, vtype));
        } else if (step.Target is IrGlobalRef globalRef) {
          this.EmitExpr(globalRef);
          this.Code.EmitStore(oldSlot, vtype);
          this.EmitSteppedValue(oldSlot, newSlot, type, step.Increment, step.Trap);
          this.Code.EmitLoad(newSlot, vtype);
          this.Code.EmitStaticField(JvmOpcode.Putstatic, this._owner._internalClassName,
            this._owner._globalFieldNames[globalRef.Global], this.Names.Descriptor(globalRef.Global.Type), vtype);
        } else {
          var load = (IrArrayLoad)step.Target;
          var arrVType = this.VTypeOf(load.Array.Type);
          var arrSlot = this.Code.AllocateLocal(arrVType);
          var idxSlot = this.Code.AllocateLocal(VType.Int);
          this.EmitExpr(load.Array);
          this.Code.EmitStore(arrSlot, arrVType);
          this.EmitExpr(load.Index);
          this.Code.EmitStore(idxSlot, VType.Int);
          this.Code.EmitLoad(arrSlot, arrVType);
          this.Code.EmitLoad(idxSlot, VType.Int);
          this.Code.EmitOp(JvmTypeMapper.ArrayLoadOpcode(type), 2, vtype);
          if (type == IrType.U8) {
            this.Code.EmitConstInt(0xFF);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          }

          this.Code.EmitStore(oldSlot, vtype);
          this.EmitSteppedValue(oldSlot, newSlot, type, step.Increment, step.Trap);
          this.Code.EmitLoad(arrSlot, arrVType);
          this.Code.EmitLoad(idxSlot, VType.Int);
          this.Code.EmitLoad(newSlot, vtype);
          this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(type), 3);
        }

        this.Code.EmitLoad(step.Postfix ? oldSlot : newSlot, vtype);
      }

      private void EmitSteppedValue(ushort oldSlot, ushort newSlot, IrType type, bool increment, bool trap) {
        var vtype = this.VTypeOf(type);
        this.Code.EmitLoad(oldSlot, vtype);
        if (type == IrType.F32) {
          this.Code.EmitConstFloatBits(BitConverter.SingleToInt32Bits(1.0f));
          this.Code.EmitOp(increment ? JvmOpcode.Fadd : JvmOpcode.Fsub, 2, VType.Float);
        } else if (type == IrType.F64) {
          this.Code.EmitConstDoubleBits(BitConverter.DoubleToInt64Bits(1.0));
          this.Code.EmitOp(increment ? JvmOpcode.Dadd : JvmOpcode.Dsub, 2, VType.Double);
        } else if (trap) {
          var op = increment ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked;
          this.Code.EmitPop();
          var one = new IrConst(type.IsWideInteger ? type : IrType.I32, 1);
          if (type.IsWideInteger) {
            var fakeLeft = new IrLocal("$step", type);
            this._slots[fakeLeft] = oldSlot;
            this._localVTypes[fakeLeft] = vtype;
            this.EmitCheckedArithmetic(op, type, new IrLocalRef(fakeLeft), one);
          } else {
            this.Code.EmitLoad(oldSlot, VType.Int);
            this.Code.EmitConstInt(increment ? 1 : -1);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
            this.EmitIntRangeCheck(IrType.I32, type);
          }
        } else if (type.IsLong) {
          this.Code.EmitConstLong(1L);
          this.Code.EmitOp(increment ? JvmOpcode.Ladd : JvmOpcode.Lsub, 2, VType.Long);
        } else {
          this.Code.EmitConstInt(1);
          this.Code.EmitOp(increment ? JvmOpcode.Iadd : JvmOpcode.Isub, 2, VType.Int);
          this.EmitIntWrapConvert(IrType.I32, type);
        }

        this.Code.EmitStore(newSlot, vtype);
      }

      private void EmitThrowArithmetic(string message) {
        this.Code.EmitNew("java/lang/ArithmeticException");
        this.Code.EmitDup();
        this.Code.EmitConstString(message);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/ArithmeticException", "<init>", "(Ljava/lang/String;)V", 1);
        this.Code.EmitAthrow();
      }

      private void EmitThrowIndexOutOfBounds(string message) {
        this.Code.EmitNew("java/lang/ArrayIndexOutOfBoundsException");
        this.Code.EmitDup();
        this.Code.EmitConstString(message);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/ArrayIndexOutOfBoundsException", "<init>", "(Ljava/lang/String;)V", 1);
        this.Code.EmitAthrow();
      }

      private void EmitThrowIllegalArgument(string message) {
        this.Code.EmitNew("java/lang/IllegalArgumentException");
        this.Code.EmitDup();
        this.Code.EmitConstString(message);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/IllegalArgumentException", "<init>", "(Ljava/lang/String;)V", 1);
        this.Code.EmitAthrow();
      }

      private void EmitThrowIllegalState(string message) {
        this.Code.EmitNew("java/lang/IllegalStateException");
        this.Code.EmitDup();
        this.Code.EmitConstString(message);
        this.Code.EmitInvoke(JvmOpcode.Invokespecial, "java/lang/IllegalStateException", "<init>", "(Ljava/lang/String;)V", 1);
        this.Code.EmitAthrow();
      }

    }

  }

}
