using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Jvm.Bytecode;
using Polypiler.Core.Frontends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm.Lowering;

internal sealed record JvmMethodRef(string IrName, JvmMethodDescriptor Descriptor, JvmClassFile OwnerClass, JvmMethodInfo Method);

internal sealed record JvmFieldRef(IrGlobal Global, JvmType JvmType);

internal sealed class JvmLiftContext(
  IReadOnlyDictionary<(string Owner, string Name, string Descriptor), JvmMethodRef> methods,
  IReadOnlyDictionary<(string Owner, string Name, string Descriptor), JvmFieldRef> fields,
  JvmIntrinsics intrinsics,
  List<Diagnostic> diagnostics) {

  public IReadOnlyDictionary<(string Owner, string Name, string Descriptor), JvmMethodRef> Methods { get; } = methods;
  public IReadOnlyDictionary<(string Owner, string Name, string Descriptor), JvmFieldRef> Fields { get; } = fields;
  public JvmIntrinsics Intrinsics { get; } = intrinsics;
  public List<Diagnostic> Diagnostics { get; } = diagnostics;
  public HashSet<JvmMethodRef> ReferencedMethods { get; } = [];

}

internal sealed class JvmMethodLifter {

  private static readonly IrType _nullSentinel = IrType.Object(IrExceptionClass.Exception);

  private readonly JvmClassFile _cls;
  private readonly JvmMethodInfo _method;
  private readonly JvmMethodDescriptor _descriptor;
  private readonly JvmLiftContext _ctx;
  private readonly string _irName;
  private readonly bool _isPublic;
  private readonly bool _skipArrayGlobalPutStatic;

  private readonly Dictionary<(int Slot, IrType Type), IrLocal> _localSlots = [];
  private readonly Dictionary<(int Depth, IrType Type), IrLocal> _stackSlots = [];
  private readonly List<IrLocal> _prologueLocals = [];
  private readonly List<IrLocal> _catchLocals = [];
  private readonly Dictionary<int, int> _localSlotCounter = [];
  private int _tempCounter;

  public JvmMethodLifter(
    JvmClassFile cls,
    JvmMethodInfo method,
    JvmMethodDescriptor descriptor,
    string irName,
    bool isPublic,
    JvmLiftContext ctx,
    bool skipArrayGlobalPutStatic = false) {
    this._cls = cls;
    this._method = method;
    this._descriptor = descriptor;
    this._irName = irName;
    this._isPublic = isPublic;
    this._ctx = ctx;
    this._skipArrayGlobalPutStatic = skipArrayGlobalPutStatic;
  }

  public IrFunction? Lift() {
    var codeAttr = this._method.Code;
    if (codeAttr is null || codeAttr.Code.Length == 0) {
      this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedMethodBody.At(
        $"method '{this._cls.ThisClass}.{this._method.Name}{this._method.Descriptor}' has no liftable bytecode body", this._cls.SourceName));
      return null;
    }

    var instructions = JvmBytecodeDecoder.Decode(codeAttr.Code, this._cls.SourceName, this._ctx.Diagnostics);
    if (instructions is null)
      return null;

    var leaders = CollectLeaders(instructions, codeAttr.ExceptionTable);
    var blockInstructions = SplitBlocks(instructions, leaders);

    var parameters = new List<IrLocal>(this._descriptor.Parameters.Count);
    var entryLocalTypes = new Dictionary<int, IrType>();
    var prologueInit = new List<IrStmt>();
    var slot = 0;
    for (var i = 0; i < this._descriptor.Parameters.Count; ++i) {
      var jvmType = this._descriptor.Parameters[i];
      var paramLocal = new IrLocal($"p{i}", jvmType.IrType);
      parameters.Add(paramLocal);
      var compType = JvmDescriptor.ComputationalType(jvmType.IrType);
      var slotLocal = this.GetLocalSlot(slot, compType);
      entryLocalTypes[slot] = compType;
      prologueInit.Add(new IrAssign(slotLocal, WidenToComputational(new IrLocalRef(paramLocal))));
      slot += jvmType.SlotWidth;
    }

    var entryStacks = this.AnalyzeStacks(blockInstructions, codeAttr.ExceptionTable, entryLocalTypes);
    if (this._ctx.Diagnostics.Count > 0)
      return null;

    var blocks = new Dictionary<int, JvmBasicBlock>();
    foreach (var (startPc, insts) in blockInstructions) {
      if (!entryStacks.TryGetValue(startPc, out var entryState))
        continue;

      var translated = this.TranslateBlock(startPc, insts, entryState.Stack, entryState.Locals, entryStacks);
      if (translated is null)
        return null;

      blocks[startPc] = translated;
    }

    var tryRegions = new List<JvmTryRegion>();
    foreach (var group in codeAttr.ExceptionTable.GroupBy(e => (e.StartPc, e.EndPc))) {
      var handlers = new List<(IrClassType, int, IrLocal)>();
      foreach (var entry in group) {
        var catchClass = JvmDescriptor.MapExceptionClass(entry.CatchType) ?? IrExceptionClass.Exception;
        var catchType = IrType.Object(catchClass);
        handlers.Add((catchClass, entry.HandlerPc, this.GetStackSlot(0, catchType)));
      }

      tryRegions.Add(new(group.Key.StartPc, group.Key.EndPc, handlers));
    }

    var structurer = new JvmControlFlowStructurer(blocks, tryRegions, this._prologueLocals, this._catchLocals);
    var structured = structurer.Structure(0);

    var bodyStmts = new List<IrStmt>();
    foreach (var local in this._prologueLocals)
      bodyStmts.Add(new IrDeclare(local, DefaultInitializer(local.Type)));

    bodyStmts.AddRange(prologueInit);
    bodyStmts.AddRange(structured);

    var allLocals = new List<IrLocal>(this._prologueLocals.Count + this._catchLocals.Count);
    allLocals.AddRange(this._prologueLocals);
    allLocals.AddRange(this._catchLocals);
    return new IrFunction(this._irName, this._isPublic, this._descriptor.ReturnType.IrType, parameters, allLocals, new IrBlock(bodyStmts));
  }

  private static HashSet<int> CollectLeaders(IReadOnlyList<JvmInstruction> instructions, IReadOnlyList<JvmExceptionEntry> exceptions) {
    var leaders = new HashSet<int> { 0 };
    for (var i = 0; i < instructions.Count; ++i) {
      var inst = instructions[i];
      switch (inst.Opcode) {
        case >= JvmOpcodes.Ifeq and <= JvmOpcodes.Goto:
        case JvmOpcodes.Ifnull:
        case JvmOpcodes.Ifnonnull:
        case JvmOpcodes.GotoW:
          leaders.Add(inst.Operand1);
          if (i + 1 < instructions.Count)
            leaders.Add(inst.NextOffset);

          break;
        case JvmOpcodes.Tableswitch:
        case JvmOpcodes.Lookupswitch:
          leaders.Add(inst.Operand1);
          foreach (var t in inst.SwitchTargets!)
            leaders.Add(t);

          if (i + 1 < instructions.Count)
            leaders.Add(inst.NextOffset);

          break;
        case >= JvmOpcodes.Ireturn and <= JvmOpcodes.Return:
        case JvmOpcodes.Athrow:
          if (i + 1 < instructions.Count)
            leaders.Add(inst.NextOffset);

          break;
      }
    }

    foreach (var ex in exceptions) {
      leaders.Add(ex.StartPc);
      leaders.Add(ex.EndPc);
      leaders.Add(ex.HandlerPc);
    }

    return leaders;
  }

  private static Dictionary<int, List<JvmInstruction>> SplitBlocks(IReadOnlyList<JvmInstruction> instructions, HashSet<int> leaders) {
    var result = new Dictionary<int, List<JvmInstruction>>();
    List<JvmInstruction>? current = null;
    foreach (var inst in instructions) {
      if (leaders.Contains(inst.Offset)) {
        current = [];
        result[inst.Offset] = current;
      }

      current!.Add(inst);
    }

    return result;
  }

  private sealed record BlockTypeState(List<IrType> Stack, Dictionary<int, IrType> Locals);

  private Dictionary<int, BlockTypeState> AnalyzeStacks(
    Dictionary<int, List<JvmInstruction>> blockInsts,
    IReadOnlyList<JvmExceptionEntry> exceptions,
    Dictionary<int, IrType> entryLocals) {
    var states = new Dictionary<int, BlockTypeState>();
    var work = new Queue<int>();

    void Propagate(int targetPc, List<IrType> stack, Dictionary<int, IrType> locals) {
      if (!blockInsts.ContainsKey(targetPc))
        return;

      if (!states.TryGetValue(targetPc, out var existing)) {
        states[targetPc] = new([.. stack], new(locals));
        work.Enqueue(targetPc);
        return;
      }

      var changed = false;
      for (var i = 0; i < Math.Min(existing.Stack.Count, stack.Count); ++i)
        if (existing.Stack[i] == _nullSentinel && stack[i] != _nullSentinel && stack[i].IsNullable) {
          existing.Stack[i] = stack[i];
          changed = true;
        }

      foreach (var (slot, type) in locals)
        if (!existing.Locals.TryGetValue(slot, out var cur) || (cur == _nullSentinel && type != _nullSentinel && type.IsNullable)) {
          existing.Locals[slot] = type;
          changed = true;
        }

      if (changed)
        work.Enqueue(targetPc);
    }

    Propagate(0, [], entryLocals);
    while (work.Count > 0) {
      var pc = work.Dequeue();
      var state = states[pc];
      var stack = new List<IrType>(state.Stack);
      var locals = new Dictionary<int, IrType>(state.Locals);
      var insts = blockInsts[pc];

      foreach (var ex in exceptions)
        if (pc >= ex.StartPc && pc < ex.EndPc) {
          var catchClass = JvmDescriptor.MapExceptionClass(ex.CatchType) ?? IrExceptionClass.Exception;
          Propagate(ex.HandlerPc, [IrType.Object(catchClass)], locals);
        }

      var terminated = false;
      foreach (var inst in insts) {
        if (!this.StepType(inst, stack, locals))
          return states;

        switch (inst.Opcode) {
          case JvmOpcodes.Goto:
          case JvmOpcodes.GotoW:
            Propagate(inst.Operand1, stack, locals);
            terminated = true;
            break;
          case >= JvmOpcodes.Ifeq and <= JvmOpcodes.IfAcmpne:
          case JvmOpcodes.Ifnull:
          case JvmOpcodes.Ifnonnull:
            Propagate(inst.Operand1, stack, locals);
            Propagate(inst.NextOffset, stack, locals);
            terminated = true;
            break;
          case JvmOpcodes.Tableswitch:
          case JvmOpcodes.Lookupswitch:
            Propagate(inst.Operand1, stack, locals);
            foreach (var t in inst.SwitchTargets!)
              Propagate(t, stack, locals);

            terminated = true;
            break;
          case >= JvmOpcodes.Ireturn and <= JvmOpcodes.Return:
          case JvmOpcodes.Athrow:
            terminated = true;
            break;
        }
      }

      if (!terminated)
        Propagate(insts[^1].NextOffset, stack, locals);
    }

    return states;
  }

  private bool StepType(JvmInstruction inst, List<IrType> stack, Dictionary<int, IrType> locals) {
    IrType Pop() => PopLast(stack);
    void Push(IrType t) => stack.Add(t);

    switch (inst.Opcode) {
      case JvmOpcodes.Nop:
        break;
      case JvmOpcodes.AconstNull:
        Push(_nullSentinel);
        break;
      case >= JvmOpcodes.IconstM1 and <= JvmOpcodes.Iconst5:
      case JvmOpcodes.Bipush:
      case JvmOpcodes.Sipush:
      case JvmOpcodes.Iload:
      case >= JvmOpcodes.Iload0 and <= JvmOpcodes.Iload3:
        Push(IrType.I32);
        break;
      case JvmOpcodes.Lconst0:
      case JvmOpcodes.Lconst1:
      case JvmOpcodes.Lload:
      case >= JvmOpcodes.Lload0 and <= JvmOpcodes.Lload3:
        Push(IrType.I64);
        break;
      case >= JvmOpcodes.Fconst0 and <= JvmOpcodes.Fconst2:
      case JvmOpcodes.Fload:
      case >= JvmOpcodes.Fload0 and <= JvmOpcodes.Fload3:
        Push(IrType.F32);
        break;
      case JvmOpcodes.Dconst0:
      case JvmOpcodes.Dconst1:
      case JvmOpcodes.Dload:
      case >= JvmOpcodes.Dload0 and <= JvmOpcodes.Dload3:
        Push(IrType.F64);
        break;
      case JvmOpcodes.Ldc:
      case JvmOpcodes.LdcW:
      case JvmOpcodes.Ldc2W: {
        var entry = this._cls.ConstantPool.GetEntry(inst.Operand1);
        if (entry is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.CorruptClassFile.At($"invalid ldc constant pool index {inst.Operand1}", this._cls.SourceName));
          return false;
        }

        var ldcType = entry.Tag switch {
          JvmConstantTag.Integer => IrType.I32,
          JvmConstantTag.Float => IrType.F32,
          JvmConstantTag.Long => IrType.I64,
          JvmConstantTag.Double => IrType.F64,
          JvmConstantTag.String => IrType.String,
          _ => null
        };
        if (ldcType is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At($"unsupported ldc constant tag {entry.Tag}", this._cls.SourceName));
          return false;
        }

        Push(ldcType);
        break;
      }
      case JvmOpcodes.Aload:
      case >= JvmOpcodes.Aload0 and <= JvmOpcodes.Aload3: {
        var slot = inst.Opcode == JvmOpcodes.Aload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Aload0;
        Push(locals.GetValueOrDefault(slot, _nullSentinel));
        break;
      }
      case >= JvmOpcodes.Istore and <= JvmOpcodes.Astore:
        locals[inst.Operand1] = Pop();
        break;
      case >= JvmOpcodes.Istore0 and <= JvmOpcodes.Astore3:
        locals[(inst.Opcode - JvmOpcodes.Istore0) & 3] = Pop();
        break;
      case JvmOpcodes.Iaload:
      case JvmOpcodes.Baload:
      case JvmOpcodes.Caload:
      case JvmOpcodes.Saload:
      case JvmOpcodes.Iadd:
      case JvmOpcodes.Isub:
      case JvmOpcodes.Imul:
      case JvmOpcodes.Idiv:
      case JvmOpcodes.Irem:
      case JvmOpcodes.Ishl:
      case JvmOpcodes.Ishr:
      case JvmOpcodes.Iushr:
      case JvmOpcodes.Iand:
      case JvmOpcodes.Ior:
      case JvmOpcodes.Ixor:
      case JvmOpcodes.Lcmp:
      case JvmOpcodes.Fcmpl:
      case JvmOpcodes.Fcmpg:
      case JvmOpcodes.Dcmpl:
      case JvmOpcodes.Dcmpg:
        Pop();
        Pop();
        Push(IrType.I32);
        break;
      case JvmOpcodes.Laload:
      case JvmOpcodes.Ladd:
      case JvmOpcodes.Lsub:
      case JvmOpcodes.Lmul:
      case JvmOpcodes.Ldiv:
      case JvmOpcodes.Lrem:
      case JvmOpcodes.Lshl:
      case JvmOpcodes.Lshr:
      case JvmOpcodes.Lushr:
      case JvmOpcodes.Land:
      case JvmOpcodes.Lor:
      case JvmOpcodes.Lxor:
        Pop();
        Pop();
        Push(IrType.I64);
        break;
      case JvmOpcodes.Faload:
      case JvmOpcodes.Fadd:
      case JvmOpcodes.Fsub:
      case JvmOpcodes.Fmul:
      case JvmOpcodes.Fdiv:
      case JvmOpcodes.Frem:
        Pop();
        Pop();
        Push(IrType.F32);
        break;
      case JvmOpcodes.Daload:
      case JvmOpcodes.Dadd:
      case JvmOpcodes.Dsub:
      case JvmOpcodes.Dmul:
      case JvmOpcodes.Ddiv:
      case JvmOpcodes.Drem:
        Pop();
        Pop();
        Push(IrType.F64);
        break;
      case JvmOpcodes.Aaload: {
        Pop();
        Push(Pop().Element!);
        break;
      }
      case >= JvmOpcodes.Iastore and <= JvmOpcodes.Sastore:
        Pop();
        Pop();
        Pop();
        break;
      case JvmOpcodes.Pop:
      case >= JvmOpcodes.Ifeq and <= JvmOpcodes.Ifle:
      case JvmOpcodes.Ifnull:
      case JvmOpcodes.Ifnonnull:
      case JvmOpcodes.Tableswitch:
      case JvmOpcodes.Lookupswitch:
      case >= JvmOpcodes.Ireturn and <= JvmOpcodes.Areturn:
      case JvmOpcodes.Athrow:
      case JvmOpcodes.Putstatic:
        Pop();
        break;
      case >= JvmOpcodes.IfIcmpeq and <= JvmOpcodes.IfAcmpne:
        Pop();
        Pop();
        break;
      case JvmOpcodes.Pop2: {
        var v1 = Pop();
        if (!JvmDescriptor.IsCategory2(v1))
          Pop();

        break;
      }
      case >= JvmOpcodes.Dup and <= JvmOpcodes.Swap:
        ApplyStackShuffle(inst.Opcode, stack, JvmDescriptor.IsCategory2);
        break;
      case JvmOpcodes.Ineg:
      case JvmOpcodes.Lneg:
      case JvmOpcodes.Fneg:
      case JvmOpcodes.Dneg:
      case JvmOpcodes.Iinc:
      case JvmOpcodes.I2b:
      case JvmOpcodes.I2c:
      case JvmOpcodes.I2s:
      case JvmOpcodes.Goto:
      case JvmOpcodes.GotoW:
      case JvmOpcodes.Return:
      case JvmOpcodes.Checkcast:
        break;
      case JvmOpcodes.I2l:
      case JvmOpcodes.F2l:
      case JvmOpcodes.D2l:
        Pop();
        Push(IrType.I64);
        break;
      case JvmOpcodes.I2f:
      case JvmOpcodes.L2f:
      case JvmOpcodes.D2f:
        Pop();
        Push(IrType.F32);
        break;
      case JvmOpcodes.I2d:
      case JvmOpcodes.L2d:
      case JvmOpcodes.F2d:
        Pop();
        Push(IrType.F64);
        break;
      case JvmOpcodes.L2i:
      case JvmOpcodes.F2i:
      case JvmOpcodes.D2i:
      case JvmOpcodes.Arraylength:
        Pop();
        Push(IrType.I32);
        break;
      case JvmOpcodes.Getstatic: {
        var member = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1);
        if (member is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.CorruptClassFile.At($"invalid getstatic index {inst.Operand1}", this._cls.SourceName));
          return false;
        }

        var fieldType = JvmDescriptor.TryParseField(member.Value.Descriptor);
        if (fieldType is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedDescriptor.At($"unsupported field descriptor '{member.Value.Descriptor}'", this._cls.SourceName));
          return false;
        }

        Push(JvmDescriptor.ComputationalType(fieldType.IrType));
        break;
      }
      case JvmOpcodes.Newarray: {
        Pop();
        var arrayType = MapNewArrayType(inst.Operand1);
        if (arrayType is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.CorruptClassFile.At($"invalid newarray atype {inst.Operand1}", this._cls.SourceName));
          return false;
        }

        Push(arrayType);
        break;
      }
      case JvmOpcodes.Anewarray: {
        Pop();
        var clsName = this._cls.ConstantPool.TryGetClassName(inst.Operand1);
        if (clsName != "java/lang/String") {
          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedDescriptor.At($"unsupported anewarray element type '{clsName}'", this._cls.SourceName));
          return false;
        }

        Push(IrType.Array(IrType.String));
        break;
      }
      case JvmOpcodes.New: {
        var clsName = this._cls.ConstantPool.TryGetClassName(inst.Operand1);
        var excClass = JvmDescriptor.MapExceptionClass(clsName);
        if (excClass is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At($"unsupported 'new {clsName}' instruction", this._cls.SourceName));
          return false;
        }

        Push(IrType.Object(excClass));
        break;
      }
      case JvmOpcodes.Invokestatic:
      case JvmOpcodes.Invokevirtual:
      case JvmOpcodes.Invokespecial: {
        var member = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1);
        if (member is null) {
          this._ctx.Diagnostics.Add(JvmFrontend.CorruptClassFile.At($"invalid invoke index {inst.Operand1}", this._cls.SourceName));
          return false;
        }

        var (owner, name, desc) = member.Value;
        var (argCount, retType) = ParseInvokeSignature(owner, name, desc);
        if (argCount < 0) {
          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedDescriptor.At($"unsupported call descriptor '{owner}.{name}{desc}'", this._cls.SourceName));
          return false;
        }

        var popped = new IrType[argCount];
        for (var i = argCount - 1; i >= 0; --i)
          popped[i] = Pop();

        if (inst.Opcode != JvmOpcodes.Invokestatic)
          Pop();

        if (owner == "java/util/Arrays" && name == "copyOf" && popped.Length == 2)
          Push(popped[0]);
        else if (retType != IrType.Void)
          Push(JvmDescriptor.ComputationalType(retType));

        break;
      }
      case JvmOpcodes.Invokedynamic:
        this._ctx.Diagnostics.Add(JvmFrontend.InvokeDynamicUnsupported.At(
          $"invokedynamic at offset {inst.Offset} in '{this._cls.ThisClass}.{this._method.Name}' is not supported", this._cls.SourceName));
        return false;
      default:
        this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
          $"unsupported JVM opcode 0x{inst.Opcode:X2} at offset {inst.Offset} in '{this._cls.ThisClass}.{this._method.Name}'", this._cls.SourceName));
        return false;
    }

    return true;
  }

  private JvmBasicBlock? TranslateBlock(
    int startPc,
    List<JvmInstruction> insts,
    List<IrType> entryStackTypes,
    Dictionary<int, IrType> entryLocalTypes,
    Dictionary<int, BlockTypeState> allEntryStates) {
    var block = new JvmBasicBlock(startPc);
    var stack = new List<IrExpr>(entryStackTypes.Count);
    var locals = new Dictionary<int, IrType>(entryLocalTypes);
    for (var d = 0; d < entryStackTypes.Count; ++d)
      stack.Add(new IrLocalRef(this.GetStackSlot(d, entryStackTypes[d])));

    IrExpr Pop() => PopLast(stack);
    void Push(IrExpr expr) => stack.Add(expr);

    void SpillStack() {
      for (var i = 0; i < stack.Count; ++i)
        if (stack[i] is not (IrConst or IrStrConst or IrNull)) {
          var tmp = this.NewTemp(stack[i].Type);
          block.Statements.Add(new IrAssign(tmp, stack[i]));
          stack[i] = new IrLocalRef(tmp);
        }
    }

    void FlushToTarget(int targetPc) {
      var targetTypes = allEntryStates[targetPc].Stack;
      for (var d = 0; d < stack.Count; ++d) {
        var targetType = targetTypes[d];
        var slotLocal = this.GetStackSlot(d, targetType);
        var val = CoerceNull(stack[d], targetType);
        if (val is not IrLocalRef { Local: var existing } || existing != slotLocal)
          block.Statements.Add(new IrAssign(slotLocal, val));
      }
    }

    IrExpr EnsureValue(IrExpr expr) {
      if (expr is IrConst or IrStrConst or IrLocalRef or IrNull)
        return expr;

      var tmp = this.NewTemp(expr.Type);
      block.Statements.Add(new IrAssign(tmp, expr));
      return new IrLocalRef(tmp);
    }

    for (var idx = 0; idx < insts.Count; ++idx) {
      var inst = insts[idx];
      switch (inst.Opcode) {
        case JvmOpcodes.Nop:
        case JvmOpcodes.Checkcast:
          break;
        case JvmOpcodes.AconstNull:
          Push(new IrNull(_nullSentinel));
          break;
        case >= JvmOpcodes.IconstM1 and <= JvmOpcodes.Iconst5:
          Push(new IrConst(IrType.I32, inst.Opcode - JvmOpcodes.Iconst0));
          break;
        case JvmOpcodes.Lconst0:
        case JvmOpcodes.Lconst1:
          Push(new IrConst(IrType.I64, inst.Opcode - JvmOpcodes.Lconst0));
          break;
        case >= JvmOpcodes.Fconst0 and <= JvmOpcodes.Fconst2:
          Push(new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, inst.Opcode - JvmOpcodes.Fconst0)));
          break;
        case JvmOpcodes.Dconst0:
        case JvmOpcodes.Dconst1:
          Push(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, inst.Opcode - JvmOpcodes.Dconst0)));
          break;
        case JvmOpcodes.Bipush:
        case JvmOpcodes.Sipush:
          Push(new IrConst(IrType.I32, inst.Operand1));
          break;
        case JvmOpcodes.Ldc:
        case JvmOpcodes.LdcW:
        case JvmOpcodes.Ldc2W: {
          var entry = this._cls.ConstantPool.GetEntry(inst.Operand1)!;
          switch (entry.Tag) {
            case JvmConstantTag.Integer:
              Push(new IrConst(IrType.I32, entry.IntValue));
              break;
            case JvmConstantTag.Float:
              Push(new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, entry.FloatValue)));
              break;
            case JvmConstantTag.Long:
              Push(new IrConst(IrType.I64, entry.IntValue));
              break;
            case JvmConstantTag.Double:
              Push(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, entry.FloatValue)));
              break;
            case JvmConstantTag.String: {
              var text = this._cls.ConstantPool.TryGetUtf8(entry.Index1);
              if (text is null) {
                this._ctx.Diagnostics.Add(JvmFrontend.CorruptClassFile.At($"invalid CONSTANT_String index {entry.Index1}", this._cls.SourceName));
                return null;
              }

              Push(new IrStrConst(text));
              break;
            }
          }

          break;
        }
        case JvmOpcodes.Iload:
        case >= JvmOpcodes.Iload0 and <= JvmOpcodes.Iload3:
          Push(new IrLocalRef(this.GetLocalSlot(inst.Opcode == JvmOpcodes.Iload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Iload0, IrType.I32)));
          break;
        case JvmOpcodes.Lload:
        case >= JvmOpcodes.Lload0 and <= JvmOpcodes.Lload3:
          Push(new IrLocalRef(this.GetLocalSlot(inst.Opcode == JvmOpcodes.Lload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Lload0, IrType.I64)));
          break;
        case JvmOpcodes.Fload:
        case >= JvmOpcodes.Fload0 and <= JvmOpcodes.Fload3:
          Push(new IrLocalRef(this.GetLocalSlot(inst.Opcode == JvmOpcodes.Fload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Fload0, IrType.F32)));
          break;
        case JvmOpcodes.Dload:
        case >= JvmOpcodes.Dload0 and <= JvmOpcodes.Dload3:
          Push(new IrLocalRef(this.GetLocalSlot(inst.Opcode == JvmOpcodes.Dload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Dload0, IrType.F64)));
          break;
        case JvmOpcodes.Aload:
        case >= JvmOpcodes.Aload0 and <= JvmOpcodes.Aload3: {
          var slot = inst.Opcode == JvmOpcodes.Aload ? inst.Operand1 : inst.Opcode - JvmOpcodes.Aload0;
          Push(new IrLocalRef(this.GetLocalSlot(slot, locals[slot])));
          break;
        }
        case >= JvmOpcodes.Istore and <= JvmOpcodes.Astore:
        case >= JvmOpcodes.Istore0 and <= JvmOpcodes.Astore3: {
          var slot = inst.Opcode <= JvmOpcodes.Astore ? inst.Operand1 : (inst.Opcode - JvmOpcodes.Istore0) & 3;
          var value = Pop();
          SpillStack();
          locals[slot] = value.Type;
          block.Statements.Add(new IrAssign(this.GetLocalSlot(slot, value.Type), value));
          break;
        }
        case >= JvmOpcodes.Iaload and <= JvmOpcodes.Saload: {
          var index = Pop();
          var array = Pop();
          SpillStack();
          var expr = WidenToComputational(new IrArrayLoad(array, index));
          var tmp = this.NewTemp(expr.Type);
          block.Statements.Add(new IrAssign(tmp, expr));
          Push(new IrLocalRef(tmp));
          break;
        }
        case >= JvmOpcodes.Iastore and <= JvmOpcodes.Sastore: {
          var val = Pop();
          var index = Pop();
          var array = Pop();
          SpillStack();
          var elemType = array.Type.Element!;
          var stored = inst.Opcode == JvmOpcodes.Bastore && elemType == IrType.U8
            ? new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, val, new IrConst(IrType.I32, 1)))
            : NarrowFromComputational(val, elemType);
          block.Statements.Add(new IrStore(array, index, stored));
          break;
        }
        case JvmOpcodes.Pop:
          _ = Pop();
          break;
        case JvmOpcodes.Pop2: {
          var v1 = Pop();
          if (!JvmDescriptor.IsCategory2(v1.Type))
            _ = Pop();

          break;
        }
        case >= JvmOpcodes.Dup and <= JvmOpcodes.Swap: {
          for (var i = Math.Max(0, stack.Count - 4); i < stack.Count; ++i)
            stack[i] = EnsureValue(stack[i]);

          ApplyStackShuffle(inst.Opcode, stack, expr => JvmDescriptor.IsCategory2(expr.Type));
          break;
        }
        case JvmOpcodes.Iadd:
        case JvmOpcodes.Ladd:
        case JvmOpcodes.Fadd:
        case JvmOpcodes.Dadd:
          BinaryOp(IrBinaryOp.Add);
          break;
        case JvmOpcodes.Isub:
        case JvmOpcodes.Lsub:
        case JvmOpcodes.Fsub:
        case JvmOpcodes.Dsub:
          BinaryOp(IrBinaryOp.Subtract);
          break;
        case JvmOpcodes.Imul:
        case JvmOpcodes.Lmul:
        case JvmOpcodes.Fmul:
        case JvmOpcodes.Dmul:
          BinaryOp(IrBinaryOp.Multiply);
          break;
        case JvmOpcodes.Idiv:
        case JvmOpcodes.Ldiv:
        case JvmOpcodes.Irem:
        case JvmOpcodes.Lrem: {
          var right = Pop();
          var left = Pop();
          SpillStack();
          var isRem = inst.Opcode is JvmOpcodes.Irem or JvmOpcodes.Lrem;
          var expr = right is IrConst { Value: not -1 }
            ? (IrExpr)new IrBinary(isRem ? IrBinaryOp.Remainder : IrBinaryOp.Divide, left, right)
            : this._ctx.Intrinsics.LowerSafeDivide(left.Type, left, right, remainder: isRem);
          var tmp = this.NewTemp(expr.Type);
          block.Statements.Add(new IrAssign(tmp, expr));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Fdiv:
        case JvmOpcodes.Ddiv:
          BinaryOp(IrBinaryOp.Divide);
          break;
        case JvmOpcodes.Frem:
        case JvmOpcodes.Drem:
          BinaryOp(IrBinaryOp.Remainder);
          break;
        case JvmOpcodes.Ineg:
        case JvmOpcodes.Lneg:
        case JvmOpcodes.Fneg:
        case JvmOpcodes.Dneg:
          Push(new IrUnary(IrUnaryOp.Negate, Pop()));
          break;
        case JvmOpcodes.Ishl:
        case JvmOpcodes.Lshl:
          BinaryOp(IrBinaryOp.ShiftLeft);
          break;
        case JvmOpcodes.Ishr:
        case JvmOpcodes.Lshr:
          BinaryOp(IrBinaryOp.ShiftRight);
          break;
        case JvmOpcodes.Iushr:
        case JvmOpcodes.Lushr:
          BinaryOp(IrBinaryOp.ShiftRightLogical);
          break;
        case JvmOpcodes.Iand:
        case JvmOpcodes.Land:
          BinaryOp(IrBinaryOp.And);
          break;
        case JvmOpcodes.Ior:
        case JvmOpcodes.Lor:
          BinaryOp(IrBinaryOp.Or);
          break;
        case JvmOpcodes.Ixor:
        case JvmOpcodes.Lxor:
          BinaryOp(IrBinaryOp.Xor);
          break;
        case JvmOpcodes.Iinc: {
          SpillStack();
          var target = this.GetLocalSlot(inst.Operand1, IrType.I32);
          block.Statements.Add(new IrAssign(target, new IrBinary(IrBinaryOp.Add, new IrLocalRef(target), new IrConst(IrType.I32, inst.Operand2))));
          break;
        }
        case JvmOpcodes.I2l:
          Push(new IrConvert(IrType.I64, Pop(), IrConversion.Wrap));
          break;
        case JvmOpcodes.I2f:
        case JvmOpcodes.L2f:
        case JvmOpcodes.D2f:
          Push(new IrConvert(IrType.F32, Pop(), IrConversion.Wrap));
          break;
        case JvmOpcodes.I2d:
        case JvmOpcodes.L2d:
        case JvmOpcodes.F2d:
          Push(new IrConvert(IrType.F64, Pop(), IrConversion.Wrap));
          break;
        case JvmOpcodes.L2i:
          Push(new IrConvert(IrType.I32, Pop(), IrConversion.Wrap));
          break;
        case JvmOpcodes.F2i:
        case JvmOpcodes.D2i:
          Push(new IrConvert(IrType.I32, Pop(), IrConversion.Saturate));
          break;
        case JvmOpcodes.F2l:
        case JvmOpcodes.D2l:
          Push(new IrConvert(IrType.I64, Pop(), IrConversion.Saturate));
          break;
        case JvmOpcodes.I2b:
          Push(new IrConvert(IrType.I32, new IrConvert(IrType.I8, Pop(), IrConversion.Wrap), IrConversion.Wrap));
          break;
        case JvmOpcodes.I2c:
          Push(new IrConvert(IrType.I32, new IrConvert(IrType.U16, Pop(), IrConversion.Wrap), IrConversion.Wrap));
          break;
        case JvmOpcodes.I2s:
          Push(new IrConvert(IrType.I32, new IrConvert(IrType.I16, Pop(), IrConversion.Wrap), IrConversion.Wrap));
          break;
        case JvmOpcodes.Lcmp: {
          var right = EnsureValue(Pop());
          var left = EnsureValue(Pop());
          Push(new IrConditional(
            new IrBinary(IrBinaryOp.Greater, left, right),
            new IrConst(IrType.I32, 1),
            new IrConditional(new IrBinary(IrBinaryOp.Less, left, right), new IrConst(IrType.I32, -1), new IrConst(IrType.I32, 0))));
          break;
        }
        case JvmOpcodes.Fcmpl:
        case JvmOpcodes.Dcmpl: {
          var right = EnsureValue(Pop());
          var left = EnsureValue(Pop());
          Push(new IrConditional(
            new IrBinary(IrBinaryOp.Greater, left, right),
            new IrConst(IrType.I32, 1),
            new IrConditional(new IrBinary(IrBinaryOp.Equal, left, right), new IrConst(IrType.I32, 0), new IrConst(IrType.I32, -1))));
          break;
        }
        case JvmOpcodes.Fcmpg:
        case JvmOpcodes.Dcmpg: {
          var right = EnsureValue(Pop());
          var left = EnsureValue(Pop());
          Push(new IrConditional(
            new IrBinary(IrBinaryOp.Less, left, right),
            new IrConst(IrType.I32, -1),
            new IrConditional(new IrBinary(IrBinaryOp.Equal, left, right), new IrConst(IrType.I32, 0), new IrConst(IrType.I32, 1))));
          break;
        }
        case >= JvmOpcodes.Ifeq and <= JvmOpcodes.Ifle: {
          var val = Pop();
          var cond = new IrBinary(CompareOp(inst.Opcode - JvmOpcodes.Ifeq), val, new IrConst(IrType.I32, 0));
          FlushToTarget(inst.Operand1);
          block.Terminator = new JvmBranch(cond, inst.Operand1, inst.NextOffset);
          return block;
        }
        case >= JvmOpcodes.IfIcmpeq and <= JvmOpcodes.IfIcmple: {
          var right = Pop();
          var left = Pop();
          var cond = new IrBinary(CompareOp(inst.Opcode - JvmOpcodes.IfIcmpeq), left, right);
          FlushToTarget(inst.Operand1);
          block.Terminator = new JvmBranch(cond, inst.Operand1, inst.NextOffset);
          return block;
        }
        case JvmOpcodes.IfAcmpeq:
        case JvmOpcodes.IfAcmpne: {
          var right = Pop();
          var left = Pop();
          IrExpr eq;
          if (left is IrNull && right is IrNull)
            eq = new IrConst(IrType.Bool, 1);
          else if (left is IrNull || (left.Type == _nullSentinel && right.Type != _nullSentinel))
            eq = new IrIsNull(right);
          else if (right is IrNull || (right.Type == _nullSentinel && left.Type != _nullSentinel))
            eq = new IrIsNull(left);
          else if (left.Type == IrType.String && right.Type == IrType.String)
            eq = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
          else
            eq = new IrReferenceEquals(left, right);

          var cond = inst.Opcode == JvmOpcodes.IfAcmpeq ? eq : new IrUnary(IrUnaryOp.LogicalNot, eq);
          FlushToTarget(inst.Operand1);
          block.Terminator = new JvmBranch(cond, inst.Operand1, inst.NextOffset);
          return block;
        }
        case JvmOpcodes.Ifnull:
        case JvmOpcodes.Ifnonnull: {
          var val = Pop();
          var isNull = new IrIsNull(val);
          var cond = inst.Opcode == JvmOpcodes.Ifnull ? (IrExpr)isNull : new IrUnary(IrUnaryOp.LogicalNot, isNull);
          FlushToTarget(inst.Operand1);
          block.Terminator = new JvmBranch(cond, inst.Operand1, inst.NextOffset);
          return block;
        }
        case JvmOpcodes.Goto:
        case JvmOpcodes.GotoW:
          FlushToTarget(inst.Operand1);
          block.Terminator = new JvmJump(inst.Operand1);
          return block;
        case JvmOpcodes.Tableswitch:
        case JvmOpcodes.Lookupswitch: {
          var key = Pop();
          FlushToTarget(inst.Operand1);
          var cases = new List<(long, int)>(inst.SwitchKeys!.Length);
          for (var i = 0; i < inst.SwitchKeys.Length; ++i)
            cases.Add((inst.SwitchKeys[i], inst.SwitchTargets![i]));

          block.Terminator = new JvmSwitchTerm(key, cases, inst.Operand1);
          return block;
        }
        case JvmOpcodes.Return:
          block.Terminator = new JvmReturnTerm(null);
          return block;
        case >= JvmOpcodes.Ireturn and <= JvmOpcodes.Areturn: {
          var raw = Pop();
          var converted = NarrowFromComputational(CoerceNull(raw, this._descriptor.ReturnType.IrType), this._descriptor.ReturnType.IrType);
          block.Terminator = new JvmReturnTerm(converted);
          return block;
        }
        case JvmOpcodes.Athrow:
          block.Terminator = new JvmThrowTerm(Pop());
          return block;
        case JvmOpcodes.Getstatic: {
          var member = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1)!.Value;
          if (!this._ctx.Fields.TryGetValue(member, out var fieldRef)) {
            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"unresolved static field '{member.Owner}.{member.Name}:{member.Descriptor}'", this._cls.SourceName));
            return null;
          }

          SpillStack();
          var expr = WidenToComputational(new IrGlobalRef(fieldRef.Global));
          var tmp = this.NewTemp(expr.Type);
          block.Statements.Add(new IrAssign(tmp, expr));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Putstatic: {
          var member = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1)!.Value;
          var value = Pop();
          SpillStack();
          if (!this._ctx.Fields.TryGetValue(member, out var fieldRef)) {
            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"unresolved static field '{member.Owner}.{member.Name}:{member.Descriptor}'", this._cls.SourceName));
            return null;
          }

          if (fieldRef.Global.Type.IsArray) {
            if (this._skipArrayGlobalPutStatic)
              break;

            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"reassigning static array field '{member.Owner}.{member.Name}' outside static initialization is not supported", this._cls.SourceName));
            return null;
          }

          var narrowed = NarrowFromComputational(CoerceNull(value, fieldRef.Global.Type), fieldRef.Global.Type);
          block.Statements.Add(new IrGlobalAssign(fieldRef.Global, narrowed));
          break;
        }
        case JvmOpcodes.Newarray: {
          var length = Pop();
          SpillStack();
          var arrType = MapNewArrayType(inst.Operand1)!;
          var tmp = this.NewTemp(arrType);
          block.Statements.Add(new IrAssign(tmp, new IrNewArray(arrType, length)));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Anewarray: {
          var length = Pop();
          SpillStack();
          if (length is not IrConst { Value: >= 0 and <= 1024 } lenConst) {
            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"dynamic-length String array allocation at offset {inst.Offset} is not supported", this._cls.SourceName));
            return null;
          }

          var arrType = IrType.Array(IrType.String);
          var elems = Enumerable.Repeat<IrExpr>(new IrStrConst(""), (int)lenConst.Value).ToArray();
          var tmp = this.NewTemp(arrType);
          block.Statements.Add(new IrAssign(tmp, new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrType, elems)));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Arraylength: {
          var array = Pop();
          SpillStack();
          var tmp = this.NewTemp(IrType.I32);
          block.Statements.Add(new IrAssign(tmp, new IrArrayLength(array)));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.New: {
          var clsName = this._cls.ConstantPool.TryGetClassName(inst.Operand1)!;
          var excClass = JvmDescriptor.MapExceptionClass(clsName)!;
          var tmp = this.NewTemp(IrType.Object(excClass));
          block.Statements.Add(new IrAssign(tmp, new IrNew(IrType.Object(excClass), IrExceptionClass.Constructor(excClass, 0), [])));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Invokespecial: {
          var (owner, name, desc) = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1)!.Value;
          var excClass = JvmDescriptor.MapExceptionClass(owner);
          if (name == "<init>" && excClass is not null && desc is "()V" or "(Ljava/lang/String;)V") {
            var ctorArgs = desc == "()V" ? Array.Empty<IrExpr>() : [Pop()];
            var receiver = Pop();
            var created = new IrNew(IrType.Object(excClass), IrExceptionClass.Constructor(excClass, ctorArgs.Length), ctorArgs);
            // Update the duplicated receiver local in place so the remaining dup on the stack sees the constructed exception.
            block.Statements.Add(new IrAssign(((IrLocalRef)receiver).Local, created));
            break;
          }

          this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
            $"unsupported invokespecial '{owner}.{name}{desc}'", this._cls.SourceName));
          return null;
        }
        case JvmOpcodes.Invokevirtual: {
          var (owner, name, desc) = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1)!.Value;
          var (argCount, _) = ParseInvokeSignature(owner, name, desc);
          var callArgs = new IrExpr[argCount];
          for (var i = argCount - 1; i >= 0; --i)
            callArgs[i] = Pop();

          var receiver = Pop();
          SpillStack();
          if (!this._ctx.Intrinsics.TryLowerVirtual(owner, name, desc, receiver, callArgs, out var lowered) || lowered is null) {
            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"unsupported invokevirtual '{owner}.{name}{desc}'", this._cls.SourceName));
            return null;
          }

          var tmp = this.NewTemp(lowered.Type);
          block.Statements.Add(new IrAssign(tmp, lowered));
          Push(new IrLocalRef(tmp));
          break;
        }
        case JvmOpcodes.Invokestatic: {
          var (owner, name, desc) = this._cls.ConstantPool.TryGetMemberRef(inst.Operand1)!.Value;
          var (argCount, _) = ParseInvokeSignature(owner, name, desc);
          var callArgs = new IrExpr[argCount];
          for (var i = argCount - 1; i >= 0; --i)
            callArgs[i] = Pop();

          SpillStack();
          if (this._ctx.Intrinsics.TryLowerStatic(owner, name, desc, callArgs, out var intrinsicExpr) && intrinsicExpr is not null) {
            if (intrinsicExpr.Type == IrType.Void) {
              block.Statements.Add(new IrExprStmt(intrinsicExpr));
            } else {
              var tmp = this.NewTemp(intrinsicExpr.Type);
              block.Statements.Add(new IrAssign(tmp, intrinsicExpr));
              Push(new IrLocalRef(tmp));
            }

            break;
          }

          if (!this._ctx.Methods.TryGetValue((owner, name, desc), out var target)) {
            this._ctx.Diagnostics.Add(JvmFrontend.UnsupportedOpcode.At(
              $"unresolved invokestatic '{owner}.{name}{desc}'", this._cls.SourceName));
            return null;
          }

          this._ctx.ReferencedMethods.Add(target);
          var convertedArgs = new IrExpr[callArgs.Length];
          for (var i = 0; i < callArgs.Length; ++i)
            convertedArgs[i] = NarrowFromComputational(CoerceNull(callArgs[i], target.Descriptor.Parameters[i].IrType), target.Descriptor.Parameters[i].IrType);

          var call = new IrCall(target.IrName, target.Descriptor.ReturnType.IrType, convertedArgs);
          if (call.Type == IrType.Void) {
            block.Statements.Add(new IrExprStmt(call));
          } else {
            var widened = WidenToComputational(call);
            var tmp = this.NewTemp(widened.Type);
            block.Statements.Add(new IrAssign(tmp, widened));
            Push(new IrLocalRef(tmp));
          }

          break;
        }
      }
    }

    var nextPc = insts[^1].NextOffset;
    FlushToTarget(nextPc);
    block.Terminator = new JvmJump(nextPc);
    return block;

    void BinaryOp(IrBinaryOp op) {
      var right = Pop();
      var left = Pop();
      Push(new IrBinary(op, left, right));
    }
  }

  private static void ApplyStackShuffle<T>(byte opcode, List<T> stack, Func<T, bool> isCat2) {
    T Pop() => PopLast(stack);
    void Push(T v) => stack.Add(v);

    switch (opcode) {
      case JvmOpcodes.Dup: {
        var v1 = Pop();
        Push(v1);
        Push(v1);
        break;
      }
      case JvmOpcodes.DupX1: {
        var v1 = Pop();
        var v2 = Pop();
        Push(v1);
        Push(v2);
        Push(v1);
        break;
      }
      case JvmOpcodes.DupX2: {
        var v1 = Pop();
        var v2 = Pop();
        if (isCat2(v2)) {
          Push(v1);
          Push(v2);
          Push(v1);
        } else {
          var v3 = Pop();
          Push(v1);
          Push(v3);
          Push(v2);
          Push(v1);
        }

        break;
      }
      case JvmOpcodes.Dup2: {
        var v1 = Pop();
        if (isCat2(v1)) {
          Push(v1);
          Push(v1);
        } else {
          var v2 = Pop();
          Push(v2);
          Push(v1);
          Push(v2);
          Push(v1);
        }

        break;
      }
      case JvmOpcodes.Dup2X1: {
        var v1 = Pop();
        if (isCat2(v1)) {
          var v2 = Pop();
          Push(v1);
          Push(v2);
          Push(v1);
        } else {
          var v2 = Pop();
          var v3 = Pop();
          Push(v2);
          Push(v1);
          Push(v3);
          Push(v2);
          Push(v1);
        }

        break;
      }
      case JvmOpcodes.Dup2X2: {
        var v1 = Pop();
        if (isCat2(v1)) {
          var v2 = Pop();
          if (isCat2(v2)) {
            Push(v1);
            Push(v2);
            Push(v1);
          } else {
            var v3 = Pop();
            Push(v1);
            Push(v3);
            Push(v2);
            Push(v1);
          }
        } else {
          var v2 = Pop();
          var v3 = Pop();
          if (isCat2(v3)) {
            Push(v2);
            Push(v1);
            Push(v3);
            Push(v2);
            Push(v1);
          } else {
            var v4 = Pop();
            Push(v2);
            Push(v1);
            Push(v4);
            Push(v3);
            Push(v2);
            Push(v1);
          }
        }

        break;
      }
      default: {
        var v1 = Pop();
        var v2 = Pop();
        Push(v1);
        Push(v2);
        break;
      }
    }
  }

  private IrLocal GetLocalSlot(int slot, IrType type) {
    if (this._localSlots.TryGetValue((slot, type), out var existing))
      return existing;

    var idx = this._localSlotCounter.GetValueOrDefault(slot);
    this._localSlotCounter[slot] = idx + 1;
    var name = idx == 0 ? $"l{slot}" : $"l{slot}_{JvmDescriptor.TypeTag(type)}_{idx}";
    var local = new IrLocal(name, type);
    this._localSlots[(slot, type)] = local;
    this._prologueLocals.Add(local);
    return local;
  }

  private IrLocal GetStackSlot(int depth, IrType type) {
    if (this._stackSlots.TryGetValue((depth, type), out var existing))
      return existing;

    var local = new IrLocal($"s{depth}_{JvmDescriptor.TypeTag(type)}", type);
    this._stackSlots[(depth, type)] = local;
    this._prologueLocals.Add(local);
    return local;
  }

  private IrLocal NewTemp(IrType type) {
    var local = new IrLocal($"t{this._tempCounter++}", type);
    this._prologueLocals.Add(local);
    return local;
  }

  private static IrBinaryOp CompareOp(int offset) => offset switch {
    0 => IrBinaryOp.Equal,
    1 => IrBinaryOp.NotEqual,
    2 => IrBinaryOp.Less,
    3 => IrBinaryOp.GreaterOrEqual,
    4 => IrBinaryOp.Greater,
    _ => IrBinaryOp.LessOrEqual
  };

  private static readonly Dictionary<int, IrType> _newArrayTypes = new() {
    [4] = IrType.Array(IrType.U8),
    [5] = IrType.Array(IrType.U16),
    [6] = IrType.Array(IrType.F32),
    [7] = IrType.Array(IrType.F64),
    [8] = IrType.Array(IrType.I8),
    [9] = IrType.Array(IrType.I16),
    [10] = IrType.Array(IrType.I32),
    [11] = IrType.Array(IrType.I64)
  };

  internal static IrType? MapNewArrayType(int atype) => _newArrayTypes.GetValueOrDefault(atype);

  private static (int ArgCount, IrType ReturnType) ParseInvokeSignature(string owner, string name, string descriptor) {
    if (owner == "java/lang/System" && name == "arraycopy" && descriptor == "(Ljava/lang/Object;ILjava/lang/Object;II)V")
      return (5, IrType.Void);

    if (owner == "java/lang/String" && name == "equals" && descriptor == "(Ljava/lang/Object;)Z")
      return (1, IrType.Bool);

    var parsed = JvmDescriptor.TryParseMethod(descriptor);
    return parsed is null ? (-1, IrType.Void) : (parsed.Parameters.Count, parsed.ReturnType.IrType);
  }

  private static IrExpr WidenToComputational(IrExpr expr) {
    if (expr.Type == IrType.Bool)
      return new IrConditional(expr, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));

    if (expr.Type.Kind is IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16)
      return new IrConvert(IrType.I32, expr, IrConversion.Wrap);

    return expr;
  }

  private static IrExpr NarrowFromComputational(IrExpr expr, IrType target) {
    if (expr.Type == target)
      return expr;

    if (target == IrType.Bool)
      return new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.And, expr, new IrConst(IrType.I32, 1)), new IrConst(IrType.I32, 0));

    return new IrConvert(target, expr, IrConversion.Wrap);
  }

  private static IrExpr CoerceNull(IrExpr expr, IrType targetType)
    => expr is IrNull && targetType.IsNullable && expr.Type != targetType ? new IrNull(targetType) : expr;

  private static IrExpr DefaultInitializer(IrType type) => type.Kind switch {
    IrTypeKind.String => new IrStrConst(""),
    IrTypeKind.Array or IrTypeKind.Object => new IrNull(type),
    _ => new IrConst(type, 0)
  };

  private static T PopLast<T>(List<T> list) {
    var value = list[^1];
    list.RemoveAt(list.Count - 1);
    return value;
  }

}
