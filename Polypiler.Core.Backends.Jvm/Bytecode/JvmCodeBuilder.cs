using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Jvm.ClassFile;

namespace Polypiler.Core.Backends.Jvm.Bytecode;

/// <summary>A symbolic jump or exception-handler target within a <see cref="JvmCodeBuilder"/>.</summary>
internal sealed class JvmLabel {

  internal bool IsBranchTarget { get; set; }
  internal VType[]? ExpectedStack { get; set; }
  internal int InstructionIndex { get; set; } = -1;

}

/// <summary>
/// Two-pass symbolic JVM bytecode builder that tracks exact operand-stack types, computes exact
/// <c>max_stack</c> and <c>max_locals</c>, prunes unreachable code after unconditional transfers,
/// patches branch and switch offsets (with 4-byte alignment), and emits verifier-clean JVMS §4.7.4
/// <c>StackMapTable</c> (<c>full_frame</c>) entries for every branch target and exception handler.
/// </summary>
internal sealed class JvmCodeBuilder {

  private readonly ConstantPool _pool;
  private readonly List<VType> _paramTypes;
  private readonly List<(int Slot, VType Type)> _allocatedLocals = [];
  private readonly List<Instruction> _instructions = [];
  private readonly List<JvmLabel> _labels = [];
  private readonly List<(JvmLabel Start, JvmLabel End, JvmLabel Handler, ushort CatchType)> _exceptions = [];
  private readonly List<VType> _stack = [];
  private ushort _nextLocalSlot;
  private int _maxStack;
  private bool _reachable = true;

  /// <summary>Initializes a method bytecode builder with the method's parameter verification types.</summary>
  public JvmCodeBuilder(ConstantPool pool, IReadOnlyList<VType> paramTypes) {
    this._pool = pool;
    this._paramTypes = [.. paramTypes];
    foreach (var param in paramTypes)
      this._nextLocalSlot = (ushort)(this._nextLocalSlot + param.Slots);
  }

  /// <summary>Whether control can currently reach the emission cursor.</summary>
  public bool IsReachable => this._reachable;

  /// <summary>Allocates a typed local-variable slot (zero-initialized in the method prologue).</summary>
  public ushort AllocateLocal(VType type) {
    var slot = this._nextLocalSlot;
    this._allocatedLocals.Add((slot, type));
    this._nextLocalSlot = (ushort)(this._nextLocalSlot + type.Slots);
    return slot;
  }

  /// <summary>Defines a new symbolic label.</summary>
  public JvmLabel DefineLabel() {
    var label = new JvmLabel();
    this._labels.Add(label);
    return label;
  }

  /// <summary>Binds a label at the current instruction index, restoring or merging stack state.</summary>
  public void MarkLabel(JvmLabel label) {
    if (label.ExpectedStack is { } expected) {
      if (this._reachable) {
        for (var i = 0; i < expected.Length; ++i)
          this._stack[i] = this.MergeVType(this._stack[i], expected[i]);
        label.ExpectedStack = [.. this._stack];
      } else {
        this._reachable = true;
        this._stack.Clear();
        this._stack.AddRange(expected);
        this.UpdateMaxStack();
      }
    }

    label.InstructionIndex = this._instructions.Count;
  }

  /// <summary>Binds an exception handler entry label with the caught exception on the operand stack.</summary>
  public void MarkHandler(JvmLabel label, ushort catchTypeCpIndex) {
    var exType = VType.Object(catchTypeCpIndex == 0 ? this._pool.AddClass("java/lang/Throwable") : catchTypeCpIndex);
    label.IsBranchTarget = true;
    label.ExpectedStack = [exType];
    this._reachable = true;
    this._stack.Clear();
    this._stack.Add(exType);
    this.UpdateMaxStack();
    label.InstructionIndex = this._instructions.Count;
  }

  /// <summary>Records an exception handler table range covering <c>[start, end)</c>.</summary>
  public void AddExceptionRange(JvmLabel start, JvmLabel end, JvmLabel handler, ushort catchTypeCpIndex)
    => this._exceptions.Add((start, end, handler, catchTypeCpIndex));

  /// <summary>Emits the shortest instruction pushing a 32-bit integer constant.</summary>
  public void EmitConstInt(int value) {
    if (!this._reachable)
      return;

    if (value is >= -1 and <= 5)
      this.Append(Instruction.Op((JvmOpcode)((int)JvmOpcode.Iconst0 + value)));
    else if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
      this.Append(Instruction.U1(JvmOpcode.Bipush, (byte)(sbyte)value));
    else if (value is >= short.MinValue and <= short.MaxValue)
      this.Append(Instruction.U2(JvmOpcode.Sipush, (ushort)(short)value));
    else
      this.EmitLdcIndex(this._pool.AddInteger(value));

    this.Push(VType.Int);
  }

  /// <summary>Emits the shortest instruction pushing a 64-bit integer constant.</summary>
  public void EmitConstLong(long value) {
    if (!this._reachable)
      return;

    if (value == 0L)
      this.Append(Instruction.Op(JvmOpcode.Lconst0));
    else if (value == 1L)
      this.Append(Instruction.Op(JvmOpcode.Lconst1));
    else
      this.Append(Instruction.U2(JvmOpcode.Ldc2W, this._pool.AddLong(value)));

    this.Push(VType.Long);
  }

  /// <summary>Emits the shortest instruction pushing a 32-bit float constant from its exact IEEE 754 bits.</summary>
  public void EmitConstFloatBits(int bits) {
    if (!this._reachable)
      return;

    if (bits == 0)
      this.Append(Instruction.Op(JvmOpcode.Fconst0));
    else if (bits == 0x3F800000)
      this.Append(Instruction.Op(JvmOpcode.Fconst1));
    else if (bits == 0x40000000)
      this.Append(Instruction.Op(JvmOpcode.Fconst2));
    else
      this.EmitLdcIndex(this._pool.AddFloatBits(bits));

    this.Push(VType.Float);
  }

  /// <summary>Emits the shortest instruction pushing a 64-bit double constant from its exact IEEE 754 bits.</summary>
  public void EmitConstDoubleBits(long bits) {
    if (!this._reachable)
      return;

    if (bits == 0L)
      this.Append(Instruction.Op(JvmOpcode.Dconst0));
    else if (bits == 0x3FF0000000000000L)
      this.Append(Instruction.Op(JvmOpcode.Dconst1));
    else
      this.Append(Instruction.U2(JvmOpcode.Ldc2W, this._pool.AddDoubleBits(bits)));

    this.Push(VType.Double);
  }

  /// <summary>Emits <c>ldc</c>/<c>ldc_w</c> for a string literal.</summary>
  public void EmitConstString(string value) {
    if (!this._reachable)
      return;

    this.EmitLdcIndex(this._pool.AddString(value));
    this.Push(VType.Object(this._pool.AddClass("java/lang/String")));
  }

  /// <summary>Emits <c>aconst_null</c>.</summary>
  public void EmitNull() {
    if (!this._reachable)
      return;

    this.Append(Instruction.Op(JvmOpcode.AconstNull));
    this.Push(VType.Null);
  }

  /// <summary>Emits a local-variable load instruction for the given slot and verification type.</summary>
  public void EmitLoad(ushort slot, VType type) {
    if (!this._reachable)
      return;

    var (baseOp, compactBase) = type.Tag switch {
      VType.IntegerTag => (JvmOpcode.Iload, JvmOpcode.Iload0),
      VType.LongTag => (JvmOpcode.Lload, JvmOpcode.Lload0),
      VType.FloatTag => (JvmOpcode.Fload, JvmOpcode.Fload0),
      VType.DoubleTag => (JvmOpcode.Dload, JvmOpcode.Dload0),
      _ => (JvmOpcode.Aload, JvmOpcode.Aload0)
    };
    this.Append(MakeLocalInstruction(baseOp, compactBase, slot));
    this.Push(type);
  }

  /// <summary>Emits a local-variable store instruction for the given slot and verification type.</summary>
  public void EmitStore(ushort slot, VType type) {
    if (!this._reachable)
      return;

    this.Pop(1);
    var (baseOp, compactBase) = type.Tag switch {
      VType.IntegerTag => (JvmOpcode.Istore, JvmOpcode.Istore0),
      VType.LongTag => (JvmOpcode.Lstore, JvmOpcode.Lstore0),
      VType.FloatTag => (JvmOpcode.Fstore, JvmOpcode.Fstore0),
      VType.DoubleTag => (JvmOpcode.Dstore, JvmOpcode.Dstore0),
      _ => (JvmOpcode.Astore, JvmOpcode.Astore0)
    };
    this.Append(MakeLocalInstruction(baseOp, compactBase, slot));
  }

  /// <summary>Emits a simple 1-byte opcode with explicit stack pop/push effects.</summary>
  public void EmitOp(JvmOpcode opcode, int popCount, VType? pushType = null) {
    if (!this._reachable)
      return;

    this.Pop(popCount);
    this.Append(Instruction.Op(opcode));
    if (pushType is { } pushed)
      this.Push(pushed);
  }

  /// <summary>Emits <c>dup</c> (1-slot top value) or <c>dup2</c> (2-slot top value).</summary>
  public void EmitDup() {
    if (!this._reachable)
      return;

    var top = this._stack[^1];
    this.Append(Instruction.Op(top.Slots == 2 ? JvmOpcode.Dup2 : JvmOpcode.Dup));
    this.Push(top);
  }

  /// <summary>Emits <c>pop</c> (1-slot top value) or <c>pop2</c> (2-slot top value).</summary>
  public void EmitPop() {
    if (!this._reachable)
      return;

    var top = this._stack[^1];
    this.Pop(1);
    this.Append(Instruction.Op(top.Slots == 2 ? JvmOpcode.Pop2 : JvmOpcode.Pop));
  }

  /// <summary>Emits a return instruction and marks subsequent code unreachable.</summary>
  public void EmitReturn(JvmOpcode opcode) {
    if (!this._reachable)
      return;

    if (opcode != JvmOpcode.Return)
      this.Pop(1);
    this.Append(Instruction.Op(opcode));
    this._reachable = false;
    this._stack.Clear();
  }

  /// <summary>Emits <c>athrow</c> and marks subsequent code unreachable.</summary>
  public void EmitAthrow() {
    if (!this._reachable)
      return;

    this.Pop(1);
    this.Append(Instruction.Op(JvmOpcode.Athrow));
    this._reachable = false;
    this._stack.Clear();
  }

  /// <summary>Emits an unconditional <c>goto</c> to <paramref name="target"/> and marks subsequent code unreachable.</summary>
  public void EmitGoto(JvmLabel target) {
    if (!this._reachable)
      return;

    this.RecordBranchTarget(target);
    this.Append(Instruction.Branch(JvmOpcode.Goto, target));
    this._reachable = false;
    this._stack.Clear();
  }

  /// <summary>Emits a conditional branch opcode (<c>ifeq</c>, <c>if_icmp*</c>, <c>ifnull</c>, etc.).</summary>
  public void EmitBranch(JvmOpcode opcode, JvmLabel target) {
    if (!this._reachable)
      return;

    var popCount = opcode is >= JvmOpcode.IfIcmpeq and <= JvmOpcode.IfIcmple ? 2 : 1;
    this.Pop(popCount);
    this.RecordBranchTarget(target);
    this.Append(Instruction.Branch(opcode, target));
  }

  /// <summary>Emits a <c>tableswitch</c> instruction and marks subsequent code unreachable.</summary>
  public void EmitTableSwitch(JvmLabel defaultTarget, int low, IReadOnlyList<JvmLabel> targets) {
    if (!this._reachable)
      return;

    this.Pop(1);
    this.RecordBranchTarget(defaultTarget);
    foreach (var target in targets)
      this.RecordBranchTarget(target);

    this.Append(Instruction.TableSwitch(defaultTarget, low, [.. targets]));
    this._reachable = false;
    this._stack.Clear();
  }

  /// <summary>Emits a <c>lookupswitch</c> instruction (pairs sorted ascending by key) and marks subsequent code unreachable.</summary>
  public void EmitLookupSwitch(JvmLabel defaultTarget, IReadOnlyList<(int Key, JvmLabel Target)> pairs) {
    if (!this._reachable)
      return;

    this.Pop(1);
    var sorted = pairs.OrderBy(p => p.Key).ToArray();
    this.RecordBranchTarget(defaultTarget);
    foreach (var (_, target) in sorted)
      this.RecordBranchTarget(target);

    this.Append(Instruction.LookupSwitch(defaultTarget, sorted));
    this._reachable = false;
    this._stack.Clear();
  }

  /// <summary>Emits <c>getstatic</c> or <c>putstatic</c>.</summary>
  public void EmitStaticField(JvmOpcode opcode, string owner, string name, string descriptor, VType fieldType) {
    if (!this._reachable)
      return;

    var index = this._pool.AddFieldref(owner, name, descriptor);
    if (opcode == JvmOpcode.Putstatic)
      this.Pop(1);
    this.Append(Instruction.U2(opcode, index));
    if (opcode == JvmOpcode.Getstatic)
      this.Push(fieldType);
  }

  /// <summary>Emits <c>invokestatic</c>, <c>invokevirtual</c>, or <c>invokespecial</c>.</summary>
  public void EmitInvoke(JvmOpcode opcode, string owner, string name, string descriptor, int argCount, VType? returnType = null) {
    if (!this._reachable)
      return;

    var index = this._pool.AddMethodref(owner, name, descriptor);
    var pops = opcode == JvmOpcode.Invokestatic ? argCount : argCount + 1;
    this.Pop(pops);
    this.Append(Instruction.U2(opcode, index));
    if (returnType is { } ret)
      this.Push(ret);
  }

  /// <summary>Emits <c>new</c> for an internal class name.</summary>
  public void EmitNew(string internalClassName) {
    if (!this._reachable)
      return;

    var classIdx = this._pool.AddClass(internalClassName);
    this.Append(Instruction.U2(JvmOpcode.New, classIdx));
    this.Push(VType.Object(classIdx));
  }

  /// <summary>Emits <c>newarray</c> for a primitive array type.</summary>
  public void EmitNewPrimitiveArray(JvmArrayType arrayType, string arrayDescriptor) {
    if (!this._reachable)
      return;

    this.Pop(1);
    this.Append(Instruction.U1(JvmOpcode.Newarray, (byte)arrayType));
    this.Push(VType.Object(this._pool.AddClass(arrayDescriptor)));
  }

  /// <summary>Emits <c>anewarray</c> for a reference array type.</summary>
  public void EmitNewObjectArray(string elementInternalName, string arrayDescriptor) {
    if (!this._reachable)
      return;

    this.Pop(1);
    this.Append(Instruction.U2(JvmOpcode.Anewarray, this._pool.AddClass(elementInternalName)));
    this.Push(VType.Object(this._pool.AddClass(arrayDescriptor)));
  }

  /// <summary>Emits <c>checkcast</c> to an internal class name.</summary>
  public void EmitCheckCast(string internalClassName) {
    if (!this._reachable)
      return;

    var classIdx = this._pool.AddClass(internalClassName);
    this.Pop(1);
    this.Append(Instruction.U2(JvmOpcode.Checkcast, classIdx));
    this.Push(VType.Object(classIdx));
  }

  /// <summary>Finalizes the method body, laying out offsets, patching jumps, and building the <c>StackMapTable</c>.</summary>
  public JvmMethodCode Build() {
    var prologue = this.BuildPrologue(out var prologueStack);
    var hasZeroTarget = prologue.Count == 0 && this._labels.Any(l => l.IsBranchTarget && l.InstructionIndex == 0);
    if (hasZeroTarget)
      prologue.Add(Instruction.Op(JvmOpcode.Nop));

    var shift = prologue.Count;
    var all = new List<Instruction>(shift + this._instructions.Count);
    all.AddRange(prologue);
    all.AddRange(this._instructions);

    var offsets = new int[all.Count + 1];
    var pc = 0;
    for (var i = 0; i < all.Count; ++i) {
      offsets[i] = pc;
      pc += all[i].SizeAt(pc);
    }

    offsets[all.Count] = pc;

    var bytecode = new byte[pc];
    for (var i = 0; i < all.Count; ++i)
      all[i].Write(bytecode, offsets[i], label => offsets[label.InstructionIndex + shift]);

    var exceptions = new List<JvmExceptionEntry>();
    foreach (var (start, end, handler, catchType) in this._exceptions) {
      var startPc = offsets[start.InstructionIndex + shift];
      var endPc = offsets[end.InstructionIndex + shift];
      var handlerPc = offsets[handler.InstructionIndex + shift];
      if (endPc > startPc)
        exceptions.Add(new JvmExceptionEntry((ushort)startPc, (ushort)endPc, (ushort)handlerPc, catchType));
    }

    VType[] fullLocals = [.. this._paramTypes, .. this._allocatedLocals.Select(l => l.Type)];
    var framesByOffset = new SortedDictionary<int, VType[]>();
    foreach (var label in this._labels) {
      if (!label.IsBranchTarget || label.ExpectedStack is null || label.InstructionIndex < 0)
        continue;

      var targetPc = offsets[label.InstructionIndex + shift];
      if (targetPc > 0 && targetPc < bytecode.Length)
        framesByOffset[targetPc] = label.ExpectedStack;
    }

    var frames = framesByOffset.Select(kvp => new JvmStackMapFrame(kvp.Key, fullLocals, kvp.Value)).ToList();
    var maxStack = (ushort)Math.Max(this._maxStack, prologueStack);
    return new JvmMethodCode(maxStack, this._nextLocalSlot, bytecode, exceptions, frames);
  }

  private List<Instruction> BuildPrologue(out int prologueStack) {
    var list = new List<Instruction>();
    prologueStack = 0;
    foreach (var (slot, type) in this._allocatedLocals) {
      var (constOp, storeOp, compactStore, slots) = type.Tag switch {
        VType.IntegerTag => (JvmOpcode.Iconst0, JvmOpcode.Istore, JvmOpcode.Istore0, 1),
        VType.LongTag => (JvmOpcode.Lconst0, JvmOpcode.Lstore, JvmOpcode.Lstore0, 2),
        VType.FloatTag => (JvmOpcode.Fconst0, JvmOpcode.Fstore, JvmOpcode.Fstore0, 1),
        VType.DoubleTag => (JvmOpcode.Dconst0, JvmOpcode.Dstore, JvmOpcode.Dstore0, 2),
        _ => (JvmOpcode.AconstNull, JvmOpcode.Astore, JvmOpcode.Astore0, 1)
      };
      list.Add(Instruction.Op(constOp));
      list.Add(MakeLocalInstruction(storeOp, compactStore, (ushort)slot));
      if (slots > prologueStack)
        prologueStack = slots;
    }

    return list;
  }

  private void EmitLdcIndex(ushort index) {
    if (index <= 255)
      this.Append(Instruction.U1(JvmOpcode.Ldc, (byte)index));
    else
      this.Append(Instruction.U2(JvmOpcode.LdcW, index));
  }

  private static Instruction MakeLocalInstruction(JvmOpcode baseOp, JvmOpcode compactBase, ushort slot)
    => slot <= 3
      ? Instruction.Op((JvmOpcode)((int)compactBase + slot))
      : slot <= 255
        ? Instruction.U1(baseOp, (byte)slot)
        : Instruction.WideLocal(baseOp, slot);

  private void Append(Instruction instruction) => this._instructions.Add(instruction);

  private void Push(VType type) {
    this._stack.Add(type);
    this.UpdateMaxStack();
  }

  private void Pop(int count) {
    for (var i = 0; i < count; ++i)
      this._stack.RemoveAt(this._stack.Count - 1);
  }

  private void UpdateMaxStack() {
    var words = 0;
    foreach (var item in this._stack)
      words += item.Slots;
    if (words > this._maxStack)
      this._maxStack = words;
  }

  private void RecordBranchTarget(JvmLabel target) {
    target.IsBranchTarget = true;
    if (target.ExpectedStack is null) {
      target.ExpectedStack = [.. this._stack];
    } else {
      for (var i = 0; i < target.ExpectedStack.Length; ++i)
        target.ExpectedStack[i] = this.MergeVType(target.ExpectedStack[i], this._stack[i]);
    }
  }

  private VType MergeVType(VType a, VType b) {
    if (a == b)
      return a;
    if (a.Tag == VType.NullTag)
      return b;
    if (b.Tag == VType.NullTag)
      return a;

    return VType.Object(this._pool.AddClass("java/lang/Object"));
  }

  private readonly struct Instruction {

    private readonly JvmOpcode _opcode;
    private readonly byte _kind;
    private readonly ushort _operand;
    private readonly JvmLabel? _target;
    private readonly int _low;
    private readonly JvmLabel[]? _tableTargets;
    private readonly (int Key, JvmLabel Target)[]? _lookupPairs;

    private Instruction(JvmOpcode opcode, byte kind, ushort operand = 0, JvmLabel? target = null, int low = 0, JvmLabel[]? tableTargets = null, (int, JvmLabel)[]? lookupPairs = null) {
      this._opcode = opcode;
      this._kind = kind;
      this._operand = operand;
      this._target = target;
      this._low = low;
      this._tableTargets = tableTargets;
      this._lookupPairs = lookupPairs;
    }

    public static Instruction Op(JvmOpcode op) => new(op, 0);
    public static Instruction U1(JvmOpcode op, byte arg) => new(op, 1, arg);
    public static Instruction U2(JvmOpcode op, ushort arg) => new(op, 2, arg);
    public static Instruction WideLocal(JvmOpcode op, ushort slot) => new(op, 3, slot);
    public static Instruction Branch(JvmOpcode op, JvmLabel target) => new(op, 4, target: target);
    public static Instruction TableSwitch(JvmLabel def, int low, JvmLabel[] targets) => new(JvmOpcode.Tableswitch, 5, target: def, low: low, tableTargets: targets);
    public static Instruction LookupSwitch(JvmLabel def, (int, JvmLabel)[] pairs) => new(JvmOpcode.Lookupswitch, 6, target: def, lookupPairs: pairs);

    public int SizeAt(int pc) => this._kind switch {
      0 => 1,
      1 => 2,
      2 => 3,
      3 => 4,
      4 => 3,
      5 => 1 + Padding(pc) + 12 + (this._tableTargets!.Length * 4),
      _ => 1 + Padding(pc) + 8 + (this._lookupPairs!.Length * 8)
    };

    public void Write(byte[] code, int pc, Func<JvmLabel, int> resolve) {
      switch (this._kind) {
        case 0:
          code[pc] = (byte)this._opcode;
          break;
        case 1:
          code[pc] = (byte)this._opcode;
          code[pc + 1] = (byte)this._operand;
          break;
        case 2:
          code[pc] = (byte)this._opcode;
          BinaryPrimitives.WriteUInt16BigEndian(code.AsSpan(pc + 1, 2), this._operand);
          break;
        case 3:
          code[pc] = (byte)JvmOpcode.Wide;
          code[pc + 1] = (byte)this._opcode;
          BinaryPrimitives.WriteUInt16BigEndian(code.AsSpan(pc + 2, 2), this._operand);
          break;
        case 4:
          code[pc] = (byte)this._opcode;
          BinaryPrimitives.WriteInt16BigEndian(code.AsSpan(pc + 1, 2), (short)(resolve(this._target!) - pc));
          break;
        case 5: {
          code[pc] = (byte)JvmOpcode.Tableswitch;
          var pos = pc + 1 + Padding(pc);
          BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos, 4), resolve(this._target!) - pc);
          BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos + 4, 4), this._low);
          BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos + 8, 4), this._low + this._tableTargets!.Length - 1);
          pos += 12;
          foreach (var t in this._tableTargets) {
            BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos, 4), resolve(t) - pc);
            pos += 4;
          }

          break;
        }
        default: {
          code[pc] = (byte)JvmOpcode.Lookupswitch;
          var pos = pc + 1 + Padding(pc);
          BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos, 4), resolve(this._target!) - pc);
          BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos + 4, 4), this._lookupPairs!.Length);
          pos += 8;
          foreach (var (key, t) in this._lookupPairs) {
            BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos, 4), key);
            BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(pos + 4, 4), resolve(t) - pc);
            pos += 8;
          }

          break;
        }
      }
    }

    private static int Padding(int pc) => (4 - ((pc + 1) % 4)) % 4;

  }

}
