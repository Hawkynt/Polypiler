using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>
/// Performs typed stack verification, stack-to-local lifting, exception-region structuring, and
/// control-flow emission for a single MSIL method body.
/// </summary>
internal sealed class MethodLifter {

  /// <summary>Decoded method metadata and CFG ready for lifting once globals are finalized.</summary>
  internal sealed record PreparedMethod(
    MethodEntry Entry,
    IrType ReturnType,
    IReadOnlyList<IrLocal> Parameters,
    IReadOnlyList<IrLocal> ClrLocals,
    ControlFlowGraph Cfg);

  /// <summary>An item on the symbolic evaluation stack during IR lifting.</summary>
  private sealed record StackItem(
    IrExpr? Expr,
    MsilType Type,
    IrLocal? ByRefLocal = null,
    FieldDefinitionHandle RvaField = default);

  private abstract record BlockExit;
  private sealed record FallthroughExit(int Target, List<IrStmt> EdgeAssigns) : BlockExit;
  private sealed record LeaveExit(int Target) : BlockExit;
  private sealed record BranchExit(IrExpr Condition, int TrueTarget, List<IrStmt> TrueAssigns, int FalseTarget, List<IrStmt> FalseAssigns) : BlockExit;
  private sealed record SwitchExit(IrExpr Value, int[] CaseTargets, List<IrStmt>[] CaseAssigns, int DefaultTarget, List<IrStmt> DefaultAssigns) : BlockExit;
  private sealed record ReturnExit(IrExpr? Value) : BlockExit;
  private sealed record ThrowExit(IrExpr Exception) : BlockExit;
  private sealed record RethrowExit : BlockExit;
  private sealed record EndFinallyExit : BlockExit;
  private sealed record EndFilterExit(IrExpr Condition) : BlockExit;

  private sealed record LiftedBlock(int Id, List<IrStmt> Statements, BlockExit Exit);

  private sealed record ConversionSpec(IrType Target, bool Checked, bool UnsignedSource, bool SignedSource);

  private static readonly Dictionary<ILOpCode, ConversionSpec> _conversions = new() {
    [ILOpCode.Conv_i1] = new(IrType.I8, false, false, false),
    [ILOpCode.Conv_u1] = new(IrType.U8, false, false, false),
    [ILOpCode.Conv_i2] = new(IrType.I16, false, false, false),
    [ILOpCode.Conv_u2] = new(IrType.U16, false, false, false),
    [ILOpCode.Conv_i4] = new(IrType.I32, false, false, false),
    [ILOpCode.Conv_i] = new(IrType.I32, false, false, false),
    [ILOpCode.Conv_u4] = new(IrType.U32, false, false, false),
    [ILOpCode.Conv_u] = new(IrType.U32, false, false, false),
    [ILOpCode.Conv_i8] = new(IrType.I64, false, false, true),
    [ILOpCode.Conv_u8] = new(IrType.U64, false, true, false),
    [ILOpCode.Conv_r4] = new(IrType.F32, false, false, true),
    [ILOpCode.Conv_r8] = new(IrType.F64, false, false, true),
    [ILOpCode.Conv_r_un] = new(IrType.F64, false, true, false),
    [ILOpCode.Conv_ovf_i1] = new(IrType.I8, true, false, true),
    [ILOpCode.Conv_ovf_u1] = new(IrType.U8, true, false, true),
    [ILOpCode.Conv_ovf_i2] = new(IrType.I16, true, false, true),
    [ILOpCode.Conv_ovf_u2] = new(IrType.U16, true, false, true),
    [ILOpCode.Conv_ovf_i4] = new(IrType.I32, true, false, true),
    [ILOpCode.Conv_ovf_i] = new(IrType.I32, true, false, true),
    [ILOpCode.Conv_ovf_u4] = new(IrType.U32, true, false, true),
    [ILOpCode.Conv_ovf_u] = new(IrType.U32, true, false, true),
    [ILOpCode.Conv_ovf_i8] = new(IrType.I64, true, false, true),
    [ILOpCode.Conv_ovf_u8] = new(IrType.U64, true, false, true),
    [ILOpCode.Conv_ovf_i1_un] = new(IrType.I8, true, true, false),
    [ILOpCode.Conv_ovf_u1_un] = new(IrType.U8, true, true, false),
    [ILOpCode.Conv_ovf_i2_un] = new(IrType.I16, true, true, false),
    [ILOpCode.Conv_ovf_u2_un] = new(IrType.U16, true, true, false),
    [ILOpCode.Conv_ovf_i4_un] = new(IrType.I32, true, true, false),
    [ILOpCode.Conv_ovf_i_un] = new(IrType.I32, true, true, false),
    [ILOpCode.Conv_ovf_u4_un] = new(IrType.U32, true, true, false),
    [ILOpCode.Conv_ovf_u_un] = new(IrType.U32, true, true, false),
    [ILOpCode.Conv_ovf_i8_un] = new(IrType.I64, true, true, false),
    [ILOpCode.Conv_ovf_u8_un] = new(IrType.U64, true, true, false)
  };

  private readonly PeMetadataLoader _loader;
  private readonly PreparedMethod _method;
  private readonly List<IrLocal> _allLocals = [];
  private readonly HashSet<IrLocal> _catchLocals = [];
  private readonly Dictionary<int, IrLocal[]> _entrySlots = [];
  private readonly Dictionary<int, (IrLocal ExceptionLocal, IrClassType ExceptionClass)> _handlerExceptionLocals = [];
  private readonly Dictionary<int, LiftedBlock> _liftedBlocks = [];
  private int _tempCounter;

  private MethodLifter(PeMetadataLoader loader, PreparedMethod method) {
    this._loader = loader;
    this._method = method;
    this._allLocals.AddRange(method.ClrLocals);
  }

  /// <summary>Decodes signature, locals, IL instructions, CFG, and registers callee/field dependencies.</summary>
  public static PreparedMethod Prepare(PeMetadataLoader loader, MethodEntry entry) {
    var sig = loader.DecodeMethodSignature(entry.Handle);
    var returnType = sig.ReturnType.RequireIr($"return type of '{entry.TypeName}.{entry.MethodName}'");
    var paramNames = loader.GetParameterNames(entry.Handle, sig.ParameterTypes.Length);
    var parameters = new IrLocal[sig.ParameterTypes.Length];
    for (var i = 0; i < sig.ParameterTypes.Length; ++i)
      parameters[i] = new(paramNames[i], sig.ParameterTypes[i].RequireIr($"parameter '{paramNames[i]}' of '{entry.TypeName}.{entry.MethodName}'"));

    var body = loader.GetMethodBody(entry.Handle);
    var localTypes = loader.DecodeLocals(body.LocalSignature);
    var clrLocals = new IrLocal[localTypes.Length];
    for (var i = 0; i < localTypes.Length; ++i)
      clrLocals[i] = new($"loc_{i}", localTypes[i].RequireIr($"local {i} of '{entry.TypeName}.{entry.MethodName}'"));

    var instructions = IlDecoder.Decode(body.GetILBytes(), $"{entry.TypeName}.{entry.MethodName}");
    var regions = body.ExceptionRegions.Select(r => new IlExceptionRegion(
      r.Kind, r.TryOffset, r.TryOffset + r.TryLength, r.HandlerOffset, r.HandlerOffset + r.HandlerLength, r.FilterOffset, r.CatchType)).ToList();

    var cfg = ControlFlowGraph.Build(instructions, regions, $"{entry.TypeName}.{entry.MethodName}");

    foreach (var ins in instructions) {
      if (ins.OpCode is ILOpCode.Call or ILOpCode.Callvirt) {
        var resolved = loader.ResolveCallToken(ins.Token);
        if (resolved.IsIntraAssembly)
          loader.RequireIntraAssemblyMethod(resolved.LocalHandle);
      } else if (ins.OpCode is ILOpCode.Ldsfld or ILOpCode.Stsfld) {
        loader.RequireStaticField(loader.ResolveFieldToken(ins.Token));
      }
    }

    return new(entry, returnType, parameters, clrLocals, cfg);
  }

  /// <summary>Lifts a prepared method into a verified <see cref="IrFunction"/>.</summary>
  public static IrFunction Lift(PeMetadataLoader loader, PreparedMethod prepared)
    => new MethodLifter(loader, prepared).Run();

  /// <summary>Extracts static field initializer expressions from a type's <c>.cctor</c>.</summary>
  public static void ExtractStaticInitializers(
    PeMetadataLoader loader,
    MethodDefinitionHandle cctorHandle,
    Dictionary<FieldDefinitionHandle, IrExpr> initializers) {
    var body = loader.GetMethodBody(cctorHandle);
    var instructions = IlDecoder.Decode(body.GetILBytes(), ".cctor");
    var stack = new List<StackItem>();

    foreach (var ins in instructions) {
      switch (ins.OpCode) {
        case ILOpCode.Nop or ILOpCode.Volatile:
          break;
        case ILOpCode.Ldc_i4_m1:
          PushValue(stack, new IrConst(IrType.I32, -1));
          break;
        case >= ILOpCode.Ldc_i4_0 and <= ILOpCode.Ldc_i4_8:
          PushValue(stack, new IrConst(IrType.I32, ins.OpCode - ILOpCode.Ldc_i4_0));
          break;
        case ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4:
          PushValue(stack, new IrConst(IrType.I32, ins.IntValue));
          break;
        case ILOpCode.Ldc_i8:
          PushValue(stack, new IrConst(IrType.I64, ins.IntValue));
          break;
        case ILOpCode.Ldc_r4:
          PushValue(stack, new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, ins.FloatValue)));
          break;
        case ILOpCode.Ldc_r8:
          PushValue(stack, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, ins.FloatValue)));
          break;
        case ILOpCode.Ldstr:
          PushValue(stack, new IrStrConst(loader.ResolveUserString(ins.Token)));
          break;
        case ILOpCode.Ldnull:
          stack.Add(new(null, MsilType.Null));
          break;
        case ILOpCode.Newarr: {
          var len = PopExpr(stack);
          var elemType = loader.ResolveTypeToken(ins.Token).RequireIr(".cctor newarr");
          PushValue(stack, new IrNewArray(IrType.Array(elemType), CoerceToType(len, IrType.I32)));
          break;
        }
        case ILOpCode.Dup:
          stack.Add(stack[^1]);
          break;
        case ILOpCode.Ldtoken:
          stack.Add(new(null, MsilType.Marker("System.RuntimeFieldHandle"), RvaField: loader.ResolveFieldToken(ins.Token)));
          break;
        case ILOpCode.Call: {
          var call = loader.ResolveCallToken(ins.Token);
          if (call.DeclaringType != "System.Runtime.CompilerServices.RuntimeHelpers" || call.MethodName != "InitializeArray")
            return;

          var fieldItem = stack[^1];
          stack.RemoveAt(stack.Count - 1);
          var arrItem = stack[^1];
          stack.RemoveAt(stack.Count - 1);
          var arrNew = (IrNewArray)arrItem.Expr!;
          var elements = loader.ReadRvaArrayElements(fieldItem.RvaField, arrNew.Type.Element!, (int)((IrConst)arrNew.Length).Value);
          stack[^1] = new(new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrNew.Type, elements), MsilType.FromIr(arrNew.Type));
          break;
        }
        case ILOpCode.Stelem_i1 or ILOpCode.Stelem_i2 or ILOpCode.Stelem_i4 or ILOpCode.Stelem_i8
          or ILOpCode.Stelem_r4 or ILOpCode.Stelem_r8 or ILOpCode.Stelem_ref or ILOpCode.Stelem: {
          var val = PopExpr(stack);
          var index = (int)((IrConst)PopExpr(stack)).Value;
          var arr = PopExpr(stack);
          var arrType = arr.Type;
          var elem = arrType.Element!;
          IrExpr[] elements;
          if (arr is IrNewArray { Length: IrConst { Value: var len } }) {
            elements = new IrExpr[len];
            Array.Fill(elements, DefaultValueExpr(elem)!);
          } else {
            elements = ((IrIntrinsic)arr).Arguments.ToArray();
          }

          elements[index] = CoerceToType(val, elem);
          stack[^1] = new(new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrType, elements), MsilType.FromIr(arrType));
          break;
        }
        case ILOpCode.Stsfld: {
          var item = PopItem(stack);
          if (item.Type.Kind == MsilTypeKind.Null)
            break;

          var fieldHandle = loader.ResolveFieldToken(ins.Token);
          var fieldType = loader.Metadata.GetFieldDefinition(fieldHandle).DecodeSignature(new MsilSignatureTypeProvider(), null).RequireIr(".cctor stsfld");
          initializers[fieldHandle] = CoerceToType(item.Expr!, fieldType);
          break;
        }
        default:
          return;
      }
    }
  }

  private IrFunction Run() {
    this.VerifyStackAndAllocateSlots();
    this.LiftAllReachableBlocks();

    var statements = new List<IrStmt>();
    var bodyStmts = this.EmitRegion(0, this._method.Cfg.Blocks[^1].EndOffset, this._method.Cfg.TopLevelTryGroups, 0, null);

    foreach (var local in this._allLocals)
      if (!this._catchLocals.Contains(local))
        statements.Add(new IrDeclare(local, DefaultValueExpr(local.Type)));

    statements.AddRange(bodyStmts);
    return new(this._method.Entry.IrName, this._method.Entry.IsPublic, this._method.ReturnType, this._method.Parameters, this._allLocals, new(statements));
  }

  // ------------------------------------------------------------------ Phase 1: stack verification

  private void VerifyStackAndAllocateSlots() {
    var cfg = this._method.Cfg;
    var worklist = new Queue<int>();

    cfg.Blocks[0].EntryStack = [];
    worklist.Enqueue(0);

    foreach (var region in cfg.Regions) {
      if (region.Kind == ExceptionRegionKind.Catch) {
        var catchClass = this.ResolveCatchClass(region.CatchType);
        var handlerBlock = cfg.OffsetToBlock[region.HandlerOffset];
        var exLocal = this.AddCatchLocal(catchClass);
        this._handlerExceptionLocals[handlerBlock] = (exLocal, catchClass);
        cfg.Blocks[handlerBlock].EntryStack = [MsilType.FromIr(IrType.Object(catchClass))];
        worklist.Enqueue(handlerBlock);
      } else if (region.Kind == ExceptionRegionKind.Filter) {
        var filterBlock = cfg.OffsetToBlock[region.FilterOffset];
        var handlerBlock = cfg.OffsetToBlock[region.HandlerOffset];
        var exLocal = this.AddCatchLocal(IrExceptionClass.Exception);
        this._handlerExceptionLocals[filterBlock] = (exLocal, IrExceptionClass.Exception);
        this._handlerExceptionLocals[handlerBlock] = (exLocal, IrExceptionClass.Exception);
        cfg.Blocks[filterBlock].EntryStack = [MsilType.FromIr(IrType.Object(IrExceptionClass.Exception))];
        cfg.Blocks[handlerBlock].EntryStack = [MsilType.FromIr(IrType.Object(IrExceptionClass.Exception))];
        worklist.Enqueue(filterBlock);
        worklist.Enqueue(handlerBlock);
      } else {
        var handlerBlock = cfg.OffsetToBlock[region.HandlerOffset];
        cfg.Blocks[handlerBlock].EntryStack = [];
        worklist.Enqueue(handlerBlock);
      }
    }

    while (worklist.Count > 0) {
      var bId = worklist.Dequeue();
      var block = cfg.Blocks[bId];
      var stack = new List<MsilType>(block.EntryStack!);
      this.SimulateBlockTypes(block, stack);

      var outgoing = block.Last.OpCode is ILOpCode.Leave or ILOpCode.Leave_s ? [] : stack;

      foreach (var succId in block.Successors) {
        var succ = cfg.Blocks[succId];
        if (succ.EntryStack is null) {
          succ.EntryStack = [.. outgoing];
          worklist.Enqueue(succId);
        } else {
          if (succ.EntryStack.Count != outgoing.Count)
            throw new MsilImportException(MsilFrontend.UnverifiableStack,
              $"stack depth mismatch entering IL_{succ.StartOffset:X4} in '{this._method.Entry.MethodName}' ({succ.EntryStack.Count} vs {outgoing.Count})");

          var changed = false;
          for (var i = 0; i < outgoing.Count; ++i) {
            var merged = this.MergeStackTypes(succ.EntryStack[i], outgoing[i], succ.StartOffset);
            if (merged != succ.EntryStack[i]) {
              succ.EntryStack[i] = merged;
              changed = true;
            }
          }

          if (changed)
            worklist.Enqueue(succId);
        }
      }
    }

    foreach (var block in cfg.Blocks) {
      if (block.EntryStack is not { Count: > 0 } entry || this._handlerExceptionLocals.ContainsKey(block.Id))
        continue;

      var slots = new IrLocal[entry.Count];
      for (var i = 0; i < entry.Count; ++i)
        slots[i] = this.AddTempLocal(entry[i].RequireIr($"stack slot at IL_{block.StartOffset:X4}"), $"s{block.Id}_{i}");

      this._entrySlots[block.Id] = slots;
    }
  }

  private void SimulateBlockTypes(BasicBlock block, List<MsilType> stack) {
    foreach (var ins in block.Instructions) {
      switch (ins.OpCode) {
        case ILOpCode.Nop or ILOpCode.Volatile or ILOpCode.Readonly or ILOpCode.Tail or ILOpCode.Br or ILOpCode.Br_s:
          break;
        case >= ILOpCode.Ldarg_0 and <= ILOpCode.Ldarg_3:
          stack.Add(StackTypeOf(this.RequireParameter(ins.OpCode - ILOpCode.Ldarg_0).Type));
          break;
        case ILOpCode.Ldarg_s or ILOpCode.Ldarg:
          stack.Add(StackTypeOf(this.RequireParameter((int)ins.IntValue).Type));
          break;
        case ILOpCode.Ldarga_s or ILOpCode.Ldarga:
          stack.Add(MsilType.ByRef(MsilType.FromIr(this.RequireParameter((int)ins.IntValue).Type)));
          break;
        case ILOpCode.Starg_s or ILOpCode.Starg:
          this.RequireParameter((int)ins.IntValue);
          this.PopType(stack, ins);
          break;
        case >= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3:
          stack.Add(StackTypeOf(this.RequireClrLocal(ins.OpCode - ILOpCode.Ldloc_0).Type));
          break;
        case ILOpCode.Ldloc_s or ILOpCode.Ldloc:
          stack.Add(StackTypeOf(this.RequireClrLocal((int)ins.IntValue).Type));
          break;
        case ILOpCode.Ldloca_s or ILOpCode.Ldloca:
          stack.Add(MsilType.ByRef(MsilType.FromIr(this.RequireClrLocal((int)ins.IntValue).Type)));
          break;
        case >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3:
          this.RequireClrLocal(ins.OpCode - ILOpCode.Stloc_0);
          this.PopType(stack, ins);
          break;
        case ILOpCode.Stloc_s or ILOpCode.Stloc:
          this.RequireClrLocal((int)ins.IntValue);
          this.PopType(stack, ins);
          break;
        case ILOpCode.Ldsfld:
          stack.Add(StackTypeOf(this._loader.RequireStaticField(this._loader.ResolveFieldToken(ins.Token)).Type));
          break;
        case ILOpCode.Stsfld or ILOpCode.Pop or ILOpCode.Initobj
          or ILOpCode.Brfalse or ILOpCode.Brfalse_s or ILOpCode.Brtrue or ILOpCode.Brtrue_s or ILOpCode.Switch:
          this.PopType(stack, ins);
          break;
        case ILOpCode.Ldnull:
          stack.Add(MsilType.Null);
          break;
        case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4:
          stack.Add(MsilType.FromIr(IrType.I32));
          break;
        case ILOpCode.Ldc_i8:
          stack.Add(MsilType.FromIr(IrType.I64));
          break;
        case ILOpCode.Ldc_r4:
          stack.Add(MsilType.FromIr(IrType.F32));
          break;
        case ILOpCode.Ldc_r8:
          stack.Add(MsilType.FromIr(IrType.F64));
          break;
        case ILOpCode.Ldstr:
          stack.Add(MsilType.FromIr(IrType.String));
          break;
        case ILOpCode.Ldtoken:
          stack.Add(MsilType.Marker("System.RuntimeFieldHandle"));
          break;
        case ILOpCode.Dup: {
          var top = this.PopType(stack, ins);
          stack.Add(top);
          stack.Add(top);
          break;
        }
        case ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Div_un
          or ILOpCode.Rem or ILOpCode.Rem_un or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor
          or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un
          or ILOpCode.Mul_ovf or ILOpCode.Mul_ovf_un: {
          var right = this.PopType(stack, ins);
          var left = this.PopType(stack, ins);
          stack.Add(BinaryResultType(ins.OpCode, left, right));
          break;
        }
        case ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un: {
          this.PopType(stack, ins);
          var left = this.PopType(stack, ins);
          stack.Add(ShiftResultType(ins.OpCode, left));
          break;
        }
        case ILOpCode.Neg: {
          var op = this.PopType(stack, ins);
          stack.Add(op.IrType!.IsFloat ? op : op.IrType.IsLong ? MsilType.FromIr(IrType.I64) : MsilType.FromIr(IrType.I32));
          break;
        }
        case ILOpCode.Not: {
          var op = this.PopType(stack, ins);
          stack.Add(op.IrType!.IsWideInteger ? op : MsilType.FromIr(IrType.I32));
          break;
        }
        case ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un:
          this.PopType(stack, ins);
          this.PopType(stack, ins);
          stack.Add(MsilType.FromIr(IrType.Bool));
          break;
        case var convOp when _conversions.TryGetValue(convOp, out var convSpec):
          this.PopType(stack, ins);
          stack.Add(StackTypeOf(convSpec.Target));
          break;
        case ILOpCode.Newarr: {
          this.PopType(stack, ins);
          var elem = this._loader.ResolveTypeToken(ins.Token).RequireIr("newarr");
          stack.Add(MsilType.FromIr(IrType.Array(elem)));
          break;
        }
        case ILOpCode.Ldlen:
          this.PopType(stack, ins);
          stack.Add(MsilType.FromIr(IrType.I32));
          break;
        case ILOpCode.Ldelem_i1 or ILOpCode.Ldelem_u1 or ILOpCode.Ldelem_i2 or ILOpCode.Ldelem_u2
          or ILOpCode.Ldelem_i4 or ILOpCode.Ldelem_u4 or ILOpCode.Ldelem_i8 or ILOpCode.Ldelem_i
          or ILOpCode.Ldelem_r4 or ILOpCode.Ldelem_r8 or ILOpCode.Ldelem_ref or ILOpCode.Ldelem: {
          this.PopType(stack, ins);
          var arr = this.PopType(stack, ins);
          var elem = arr.IrType!.Element!;
          stack.Add(StackTypeOf(elem));
          break;
        }
        case ILOpCode.Stelem_i1 or ILOpCode.Stelem_i2 or ILOpCode.Stelem_i4 or ILOpCode.Stelem_i8
          or ILOpCode.Stelem_r4 or ILOpCode.Stelem_r8 or ILOpCode.Stelem_ref or ILOpCode.Stelem_i or ILOpCode.Stelem:
          this.PopType(stack, ins);
          this.PopType(stack, ins);
          this.PopType(stack, ins);
          break;
        case ILOpCode.Newobj: {
          var call = this._loader.ResolveCallToken(ins.Token);
          for (var i = 0; i < call.ParameterTypes.Count; ++i)
            this.PopType(stack, ins);

          stack.Add(ResolveNewObjType(call));
          break;
        }
        case ILOpCode.Call or ILOpCode.Callvirt: {
          var call = this._loader.ResolveCallToken(ins.Token);
          var totalArgs = call.ParameterTypes.Count + (call.HasThis ? 1 : 0);
          for (var i = 0; i < totalArgs; ++i)
            this.PopType(stack, ins);

          if (call.ReturnType.IrType != IrType.Void)
            stack.Add(call.ReturnType.IrType is { } retIr ? StackTypeOf(retIr) : call.ReturnType);

          break;
        }
        case ILOpCode.Isinst or ILOpCode.Castclass:
          this.PopType(stack, ins);
          stack.Add(this._loader.ResolveTypeToken(ins.Token));
          break;
        case ILOpCode.Beq or ILOpCode.Beq_s or ILOpCode.Bge or ILOpCode.Bge_s or ILOpCode.Bge_un or ILOpCode.Bge_un_s
          or ILOpCode.Bgt or ILOpCode.Bgt_s or ILOpCode.Bgt_un or ILOpCode.Bgt_un_s
          or ILOpCode.Ble or ILOpCode.Ble_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s
          or ILOpCode.Blt or ILOpCode.Blt_s or ILOpCode.Blt_un or ILOpCode.Blt_un_s
          or ILOpCode.Bne_un or ILOpCode.Bne_un_s:
          this.PopType(stack, ins);
          this.PopType(stack, ins);
          break;
        case ILOpCode.Leave or ILOpCode.Leave_s or ILOpCode.Endfinally or ILOpCode.Rethrow:
          stack.Clear();
          break;
        case ILOpCode.Throw or ILOpCode.Endfilter:
          this.PopType(stack, ins);
          stack.Clear();
          break;
        case ILOpCode.Ret: {
          var expected = this._method.ReturnType == IrType.Void ? 0 : 1;
          if (stack.Count != expected)
            throw new MsilImportException(MsilFrontend.UnverifiableStack, $"method '{this._method.Entry.MethodName}' has stack depth {stack.Count} at ret (expected {expected})");

          stack.Clear();
          break;
        }
        default:
          throw new MsilImportException(MsilFrontend.UnsupportedOpcode, $"IL opcode '{ins.OpCode}' at IL_{ins.Offset:X4} in '{this._method.Entry.MethodName}' is not supported");
      }
    }
  }

  // ------------------------------------------------------------------ Phase 2: lift blocks to IR

  private void LiftAllReachableBlocks() {
    var cfg = this._method.Cfg;
    for (var b = 0; b < cfg.Blocks.Count; ++b)
      if (cfg.Blocks[b].EntryStack is not null)
        this._liftedBlocks[b] = this.LiftBlock(cfg.Blocks[b]);
  }

  private LiftedBlock LiftBlock(BasicBlock block) {
    var stmts = new List<IrStmt>();
    var stack = new List<StackItem>();

    if (this._handlerExceptionLocals.TryGetValue(block.Id, out var handlerEx))
      stack.Add(new(new IrLocalRef(handlerEx.ExceptionLocal), MsilType.FromIr(IrType.Object(handlerEx.ExceptionClass))));
    else if (this._entrySlots.TryGetValue(block.Id, out var slots))
      foreach (var slot in slots)
        stack.Add(new(new IrLocalRef(slot), MsilType.FromIr(slot.Type)));

    BlockExit? exit = null;

    foreach (var ins in block.Instructions) {
      switch (ins.OpCode) {
        case ILOpCode.Nop or ILOpCode.Volatile or ILOpCode.Readonly or ILOpCode.Tail:
          break;
        case >= ILOpCode.Ldarg_0 and <= ILOpCode.Ldarg_3:
          PushValue(stack, WidenForStack(new IrLocalRef(this.RequireParameter(ins.OpCode - ILOpCode.Ldarg_0))));
          break;
        case ILOpCode.Ldarg_s or ILOpCode.Ldarg:
          PushValue(stack, WidenForStack(new IrLocalRef(this.RequireParameter((int)ins.IntValue))));
          break;
        case ILOpCode.Ldarga_s or ILOpCode.Ldarga: {
          var param = this.RequireParameter((int)ins.IntValue);
          stack.Add(new(null, MsilType.ByRef(MsilType.FromIr(param.Type)), ByRefLocal: param));
          break;
        }
        case ILOpCode.Starg_s or ILOpCode.Starg: {
          var val = PopItem(stack);
          var param = this.RequireParameter((int)ins.IntValue);
          this.SpillStack(stack, stmts);
          stmts.Add(new IrAssign(param, CoerceItem(val, param.Type)));
          break;
        }
        case >= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3:
          PushValue(stack, WidenForStack(new IrLocalRef(this.RequireClrLocal(ins.OpCode - ILOpCode.Ldloc_0))));
          break;
        case ILOpCode.Ldloc_s or ILOpCode.Ldloc:
          PushValue(stack, WidenForStack(new IrLocalRef(this.RequireClrLocal((int)ins.IntValue))));
          break;
        case ILOpCode.Ldloca_s or ILOpCode.Ldloca: {
          var loc = this.RequireClrLocal((int)ins.IntValue);
          stack.Add(new(null, MsilType.ByRef(MsilType.FromIr(loc.Type)), ByRefLocal: loc));
          break;
        }
        case >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3: {
          var val = PopItem(stack);
          var loc = this.RequireClrLocal(ins.OpCode - ILOpCode.Stloc_0);
          this.SpillStack(stack, stmts);
          stmts.Add(new IrAssign(loc, CoerceItem(val, loc.Type)));
          break;
        }
        case ILOpCode.Stloc_s or ILOpCode.Stloc: {
          var val = PopItem(stack);
          var loc = this.RequireClrLocal((int)ins.IntValue);
          this.SpillStack(stack, stmts);
          stmts.Add(new IrAssign(loc, CoerceItem(val, loc.Type)));
          break;
        }
        case ILOpCode.Ldsfld:
          PushValue(stack, WidenForStack(new IrGlobalRef(this._loader.GetGlobal(this._loader.ResolveFieldToken(ins.Token)))));
          break;
        case ILOpCode.Stsfld: {
          var val = PopItem(stack);
          var global = this._loader.GetGlobal(this._loader.ResolveFieldToken(ins.Token));
          this.SpillStack(stack, stmts);
          stmts.Add(new IrGlobalAssign(global, CoerceItem(val, global.Type)));
          break;
        }
        case ILOpCode.Ldnull:
          stack.Add(new(null, MsilType.Null));
          break;
        case ILOpCode.Ldc_i4_m1:
          PushValue(stack, new IrConst(IrType.I32, -1));
          break;
        case >= ILOpCode.Ldc_i4_0 and <= ILOpCode.Ldc_i4_8:
          PushValue(stack, new IrConst(IrType.I32, ins.OpCode - ILOpCode.Ldc_i4_0));
          break;
        case ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4:
          PushValue(stack, new IrConst(IrType.I32, ins.IntValue));
          break;
        case ILOpCode.Ldc_i8:
          PushValue(stack, new IrConst(IrType.I64, ins.IntValue));
          break;
        case ILOpCode.Ldc_r4:
          PushValue(stack, new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, ins.FloatValue)));
          break;
        case ILOpCode.Ldc_r8:
          PushValue(stack, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, ins.FloatValue)));
          break;
        case ILOpCode.Ldstr:
          PushValue(stack, new IrStrConst(this._loader.ResolveUserString(ins.Token)));
          break;
        case ILOpCode.Ldtoken:
          stack.Add(new(null, MsilType.Marker("System.RuntimeFieldHandle"), RvaField: this._loader.ResolveFieldToken(ins.Token)));
          break;
        case ILOpCode.Dup: {
          var top = stack[^1];
          if (top.Expr is not null && !IsTrivial(top.Expr)) {
            var tmp = this.AddTempLocal(top.Expr.Type, "dup");
            stmts.Add(new IrAssign(tmp, top.Expr));
            top = new(new IrLocalRef(tmp), top.Type);
            stack[^1] = top;
          }

          stack.Add(top);
          break;
        }
        case ILOpCode.Pop: {
          var popped = PopItem(stack);
          if (popped.Expr is not null && !IsTrivial(popped.Expr))
            stmts.Add(new IrExprStmt(popped.Expr));

          break;
        }
        case ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Div_un
          or ILOpCode.Rem or ILOpCode.Rem_un or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor
          or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un
          or ILOpCode.Mul_ovf or ILOpCode.Mul_ovf_un: {
          var right = PopItem(stack);
          var left = PopItem(stack);
          PushValue(stack, LiftBinary(ins.OpCode, left, right));
          break;
        }
        case ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un: {
          var shift = PopItem(stack);
          var left = PopItem(stack);
          PushValue(stack, LiftShift(ins.OpCode, left, shift));
          break;
        }
        case ILOpCode.Neg: {
          var op = PopItem(stack).Expr!;
          if (op.Type == IrType.Bool)
            op = BoolToI32(op);
          else if (op.Type is { IsInteger: true, IsSigned: false })
            op = new IrConvert(op.Type.IsLong ? IrType.I64 : IrType.I32, op, IrConversion.Wrap);

          PushValue(stack, new IrUnary(IrUnaryOp.Negate, op));
          break;
        }
        case ILOpCode.Not: {
          var op = PopItem(stack).Expr!;
          if (op.Type == IrType.Bool)
            op = BoolToI32(op);

          PushValue(stack, new IrUnary(IrUnaryOp.BitNot, op));
          break;
        }
        case ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un: {
          var right = PopItem(stack);
          var left = PopItem(stack);
          PushValue(stack, LiftCompare(ins.OpCode, left, right));
          break;
        }
        case var convOp when _conversions.TryGetValue(convOp, out var convSpec):
          PushValue(stack, LiftConversion(convSpec, PopItem(stack).Expr!));
          break;
        case ILOpCode.Newarr: {
          var len = CoerceItem(PopItem(stack), IrType.I32);
          var elem = this._loader.ResolveTypeToken(ins.Token).RequireIr("newarr");
          this.SpillStack(stack, stmts);
          var arrType = IrType.Array(elem);
          var tmp = this.AddTempLocal(arrType, "arr");
          IrExpr creation;
          if (elem == IrType.String) {
            if (len is not IrConst { Value: >= 0 and <= 1024 } constLen)
              throw new MsilImportException(MsilFrontend.UnsupportedType, "dynamic-length string arrays are not supported");

            var empties = new IrExpr[constLen.Value];
            Array.Fill(empties, new IrStrConst(""));
            creation = new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrType, empties);
          } else {
            creation = new IrNewArray(arrType, len);
          }

          stmts.Add(new IrAssign(tmp, creation));
          PushValue(stack, new IrLocalRef(tmp));
          break;
        }
        case ILOpCode.Ldlen:
          PushValue(stack, new IrArrayLength(PopItem(stack).Expr!));
          break;
        case ILOpCode.Ldelem_i1 or ILOpCode.Ldelem_u1 or ILOpCode.Ldelem_i2 or ILOpCode.Ldelem_u2
          or ILOpCode.Ldelem_i4 or ILOpCode.Ldelem_u4 or ILOpCode.Ldelem_i8 or ILOpCode.Ldelem_i
          or ILOpCode.Ldelem_r4 or ILOpCode.Ldelem_r8 or ILOpCode.Ldelem_ref or ILOpCode.Ldelem: {
          var idx = CoerceItem(PopItem(stack), IrType.I32);
          var arr = PopItem(stack).Expr!;
          PushValue(stack, WidenForStack(new IrArrayLoad(arr, idx)));
          break;
        }
        case ILOpCode.Stelem_i1 or ILOpCode.Stelem_i2 or ILOpCode.Stelem_i4 or ILOpCode.Stelem_i8
          or ILOpCode.Stelem_r4 or ILOpCode.Stelem_r8 or ILOpCode.Stelem_ref or ILOpCode.Stelem_i or ILOpCode.Stelem: {
          var val = PopItem(stack);
          var idx = CoerceItem(PopItem(stack), IrType.I32);
          var arr = PopItem(stack).Expr!;
          this.SpillStack(stack, stmts);
          stmts.Add(new IrStore(arr, idx, CoerceItem(val, arr.Type.Element!)));
          break;
        }
        case ILOpCode.Newobj: {
          var call = this._loader.ResolveCallToken(ins.Token);
          var rawArgs = PopItems(stack, call.ParameterTypes.Count);
          this.SpillStack(stack, stmts);
          PushValue(stack, LiftNewObj(call, rawArgs));
          break;
        }
        case ILOpCode.Call or ILOpCode.Callvirt: {
          var call = this._loader.ResolveCallToken(ins.Token);
          var rawArgs = PopItems(stack, call.ParameterTypes.Count + (call.HasThis ? 1 : 0));
          this.LiftCall(call, rawArgs, stack, stmts);
          break;
        }
        case ILOpCode.Isinst:
          PushValue(stack, new IrAsCast(this._loader.ResolveTypeToken(ins.Token).RequireIr("isinst"), PopItem(stack).Expr!));
          break;
        case ILOpCode.Castclass:
          PushValue(stack, new IrCast(this._loader.ResolveTypeToken(ins.Token).RequireIr("castclass"), PopItem(stack).Expr!));
          break;
        case ILOpCode.Initobj: {
          var target = PopItem(stack);
          if (target.ByRefLocal is { } loc && DefaultValueExpr(loc.Type) is { } defVal)
            stmts.Add(new IrAssign(loc, defVal));

          break;
        }
        case ILOpCode.Br or ILOpCode.Br_s: {
          var targetBlock = this._method.Cfg.OffsetToBlock[(int)ins.IntValue];
          exit = new FallthroughExit(targetBlock, this.BuildEdgeAssigns(targetBlock, stack));
          break;
        }
        case ILOpCode.Leave or ILOpCode.Leave_s: {
          stack.Clear();
          exit = new LeaveExit(this._method.Cfg.OffsetToBlock[(int)ins.IntValue]);
          break;
        }
        case ILOpCode.Brfalse or ILOpCode.Brfalse_s or ILOpCode.Brtrue or ILOpCode.Brtrue_s: {
          var cond = ToBool(PopItem(stack));
          if (ins.OpCode is ILOpCode.Brfalse or ILOpCode.Brfalse_s)
            cond = new IrUnary(IrUnaryOp.LogicalNot, cond);

          var trueBlock = this._method.Cfg.OffsetToBlock[(int)ins.IntValue];
          var falseBlock = this._method.Cfg.OffsetToBlock[ins.NextOffset];
          exit = new BranchExit(cond, trueBlock, this.BuildEdgeAssigns(trueBlock, stack), falseBlock, this.BuildEdgeAssigns(falseBlock, stack));
          break;
        }
        case ILOpCode.Beq or ILOpCode.Beq_s or ILOpCode.Bge or ILOpCode.Bge_s or ILOpCode.Bge_un or ILOpCode.Bge_un_s
          or ILOpCode.Bgt or ILOpCode.Bgt_s or ILOpCode.Bgt_un or ILOpCode.Bgt_un_s
          or ILOpCode.Ble or ILOpCode.Ble_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s
          or ILOpCode.Blt or ILOpCode.Blt_s or ILOpCode.Blt_un or ILOpCode.Blt_un_s
          or ILOpCode.Bne_un or ILOpCode.Bne_un_s: {
          var right = PopItem(stack);
          var left = PopItem(stack);
          var cond = LiftCompare(ins.OpCode, left, right);
          var trueBlock = this._method.Cfg.OffsetToBlock[(int)ins.IntValue];
          var falseBlock = this._method.Cfg.OffsetToBlock[ins.NextOffset];
          exit = new BranchExit(cond, trueBlock, this.BuildEdgeAssigns(trueBlock, stack), falseBlock, this.BuildEdgeAssigns(falseBlock, stack));
          break;
        }
        case ILOpCode.Switch: {
          var val = CoerceItem(PopItem(stack), IrType.I32);
          var targets = ins.SwitchTargets!;
          var caseBlocks = new int[targets.Count];
          var caseAssigns = new List<IrStmt>[targets.Count];
          for (var i = 0; i < targets.Count; ++i) {
            caseBlocks[i] = this._method.Cfg.OffsetToBlock[targets[i]];
            caseAssigns[i] = this.BuildEdgeAssigns(caseBlocks[i], stack);
          }

          var defaultBlock = this._method.Cfg.OffsetToBlock[ins.NextOffset];
          exit = new SwitchExit(val, caseBlocks, caseAssigns, defaultBlock, this.BuildEdgeAssigns(defaultBlock, stack));
          break;
        }
        case ILOpCode.Ret:
          exit = new ReturnExit(this._method.ReturnType == IrType.Void ? null : CoerceItem(PopItem(stack), this._method.ReturnType));
          break;
        case ILOpCode.Throw:
          exit = new ThrowExit(CoerceItem(PopItem(stack), IrExceptionClass.Reference));
          break;
        case ILOpCode.Rethrow:
          exit = new RethrowExit();
          break;
        case ILOpCode.Endfinally:
          exit = new EndFinallyExit();
          break;
        case ILOpCode.Endfilter:
          exit = new EndFilterExit(ToBool(PopItem(stack)));
          break;
      }
    }

    if (exit is null) {
      var nextBlock = this._method.Cfg.OffsetToBlock[block.EndOffset];
      exit = new FallthroughExit(nextBlock, this.BuildEdgeAssigns(nextBlock, stack));
    }

    return new(block.Id, stmts, exit);
  }

  private static IrExpr LiftNewObj(ResolvedCall call, IReadOnlyList<StackItem> rawArgs) {
    if (MsilType.TryResolveExceptionClass(call.DeclaringType) is { } exClass) {
      var ctor = IrExceptionClass.Constructor(exClass, rawArgs.Count);
      var expectedParams = IrExceptionClass.Parameters(ctor)!;
      var args = new IrExpr[rawArgs.Count];
      for (var i = 0; i < rawArgs.Count; ++i)
        args[i] = CoerceItem(rawArgs[i], expectedParams[i]);

      return new IrNew(IrType.Object(exClass), ctor, args);
    }

    var arr = rawArgs[0].Expr!;
    var viewType = call.DeclaringType == "System.Span`1" ? IrType.Span(arr.Type.Element!) : IrType.ReadOnlySpan(arr.Type.Element!);
    var viewArgs = rawArgs.Count == 1
      ? new IrExpr[] { arr }
      : [arr, CoerceItem(rawArgs[1], IrType.I32), CoerceItem(rawArgs[2], IrType.I32)];
    return new IrIntrinsic(IrIntrinsicOp.ViewOf, viewType, viewArgs);
  }

  private void LiftCall(ResolvedCall call, IReadOnlyList<StackItem> rawArgs, List<StackItem> stack, List<IrStmt> stmts) {
    if (call.DeclaringType == "System.Runtime.CompilerServices.RuntimeHelpers" && call.MethodName == "InitializeArray") {
      var arrLocal = ((IrLocalRef)rawArgs[0].Expr!).Local;
      var assign = (IrAssign)stmts[^1];
      var arrNew = (IrNewArray)assign.Value;
      var elements = this._loader.ReadRvaArrayElements(rawArgs[1].RvaField, arrNew.Type.Element!, (int)((IrConst)arrNew.Length).Value);
      stmts[^1] = new IrAssign(arrLocal, new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrNew.Type, elements));
      return;
    }

    if (call.MethodName == ".ctor" && call.DeclaringType is "System.Span`1" or "System.ReadOnlySpan`1" && rawArgs[0].ByRefLocal is { } spanLocal) {
      this.SpillStack(stack, stmts);
      stmts.Add(new IrAssign(spanLocal, LiftNewObj(call, [.. rawArgs.Skip(1)])));
      return;
    }

    this.SpillStack(stack, stmts);

    if (call.IsIntraAssembly) {
      var targetEntry = this._loader.RequireIntraAssemblyMethod(call.LocalHandle);
      var irArgs = new IrExpr[rawArgs.Count];
      for (var i = 0; i < rawArgs.Count; ++i)
        irArgs[i] = CoerceItem(rawArgs[i], call.ParameterTypes[i].RequireIr($"argument {i} of '{targetEntry.MethodName}'"));

      var retType = call.ReturnType.RequireIr($"return of '{targetEntry.MethodName}'");
      var callExpr = new IrCall(targetEntry.IrName, retType, irArgs);
      if (retType == IrType.Void)
        stmts.Add(new IrExprStmt(callExpr));
      else
        PushValue(stack, WidenForStack(callExpr));

      return;
    }

    var bclArgs = new IrExpr[rawArgs.Count];
    var offset = 0;
    if (call.HasThis) {
      bclArgs[0] = rawArgs[0].ByRefLocal is { } byRef ? new IrLocalRef(byRef) : rawArgs[0].Expr!;
      offset = 1;
    }

    for (var i = 0; i < call.ParameterTypes.Count; ++i) {
      var pType = call.ParameterTypes[i];
      bclArgs[offset + i] = pType.Kind == MsilTypeKind.Ir
        ? CoerceItem(rawArgs[offset + i], pType.IrType!)
        : rawArgs[offset + i].Expr!;
    }

    var retIr = call.ReturnType.Kind == MsilTypeKind.Ir ? call.ReturnType.IrType! : IrType.Void;
    var lowered = BclIntrinsics.TryLower(call.DeclaringType, call.MethodName, retIr, bclArgs);
    if (lowered is null)
      throw new MsilImportException(MsilFrontend.UnsupportedCall, $"call to '{call.DeclaringType}.{call.MethodName}' is not supported");

    if (lowered.Type == IrType.Void)
      stmts.Add(new IrExprStmt(lowered));
    else
      PushValue(stack, WidenForStack(lowered));
  }

  // ------------------------------------------------------------------ Phase 3: structured control-flow emission

  private List<IrStmt> EmitRegion(
    int startOffset,
    int endOffset,
    IReadOnlyList<TryGroup> childGroups,
    int entryBlockId,
    IrLocal? outerPc) {
    var cfg = this._method.Cfg;
    var groupByStartBlock = new Dictionary<int, TryGroup>();
    foreach (var g in childGroups)
      groupByStartBlock[cfg.OffsetToBlock[g.TryOffset]] = g;

    bool CoveredByChildGroup(int offset) {
      foreach (var g in childGroups)
        if (offset >= g.SpanStart && offset < g.SpanEnd)
          return true;

      return false;
    }

    var regionNodes = new List<int>();
    var nodeSet = new HashSet<int>();
    for (var b = 0; b < cfg.Blocks.Count; ++b) {
      var blk = cfg.Blocks[b];
      if (blk.StartOffset < startOffset || blk.StartOffset >= endOffset)
        continue;

      if (groupByStartBlock.ContainsKey(b) || (!CoveredByChildGroup(blk.StartOffset) && this._liftedBlocks.ContainsKey(b))) {
        regionNodes.Add(b);
        nodeSet.Add(b);
      }
    }

    if (regionNodes.Count == 1 && !groupByStartBlock.ContainsKey(entryBlockId)) {
      var direct = TryEmitDirectSingleBlock(this._liftedBlocks[entryBlockId], outerPc);
      if (direct is not null)
        return direct;
    }

    if (childGroups.Count == 0 && !HasBackEdge(cfg, nodeSet)) {
      var acyclic = this.TryEmitSimpleAcyclic(entryBlockId, nodeSet, outerPc);
      if (acyclic is not null)
        return acyclic;
    }

    var pc = this.AddTempLocal(IrType.I32, "pc");
    var stmts = new List<IrStmt> { new IrAssign(pc, new IrConst(IrType.I32, entryBlockId)) };

    var sections = new List<IrSwitchSection>();
    foreach (var nodeId in regionNodes) {
      List<IrStmt> body = groupByStartBlock.TryGetValue(nodeId, out var group)
        ? [this.EmitTryGroup(group, pc)]
        : [.. this._liftedBlocks[nodeId].Statements, .. EmitDispatcherExit(this._liftedBlocks[nodeId].Exit, nodeSet, pc, outerPc)];

      sections.Add(new([nodeId], false, new(body)));
    }

    if (outerPc is not null)
      sections.Add(new([], true, new([
        new IrAssign(outerPc, new IrLocalRef(pc)),
        new IrAssign(pc, new IrConst(IrType.I32, -1))
      ])));

    var loopCond = new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(pc), new IrConst(IrType.I32, 0));
    stmts.Add(new IrWhile(loopCond, new([new IrSwitch(new IrLocalRef(pc), sections)])));
    return stmts;
  }

  private IrTry EmitTryGroup(TryGroup group, IrLocal enclosingPc) {
    var cfg = this._method.Cfg;
    var tryEntry = cfg.OffsetToBlock[group.TryOffset];
    var tryBody = new IrBlock(this.EmitRegion(group.TryOffset, group.TryEnd, group.TryChildren, tryEntry, enclosingPc));

    var catches = new List<IrCatch>();
    IrBlock? finallyBlock = null;

    for (var i = 0; i < group.Handlers.Count; ++i) {
      var h = group.Handlers[i];
      var handlerChildren = group.HandlerChildren.TryGetValue(i, out var hc) ? hc : [];
      var handlerEntry = cfg.OffsetToBlock[h.HandlerOffset];

      switch (h.Kind) {
        case ExceptionRegionKind.Catch: {
          var (exLocal, exClass) = this._handlerExceptionLocals[handlerEntry];
          catches.Add(new(exClass, exLocal, null, new(this.EmitRegion(h.HandlerOffset, h.HandlerEnd, handlerChildren, handlerEntry, enclosingPc))));
          break;
        }
        case ExceptionRegionKind.Filter: {
          var (exLocal, _) = this._handlerExceptionLocals[handlerEntry];
          var filterExpr = this.LiftFilterExpression(h.FilterOffset);
          catches.Add(new(IrExceptionClass.Exception, exLocal, filterExpr, new(this.EmitRegion(h.HandlerOffset, h.HandlerEnd, handlerChildren, handlerEntry, enclosingPc))));
          break;
        }
        case ExceptionRegionKind.Finally:
          finallyBlock = new(this.EmitRegion(h.HandlerOffset, h.HandlerEnd, handlerChildren, handlerEntry, null));
          break;
        default: {
          var faultStmts = this.EmitRegion(h.HandlerOffset, h.HandlerEnd, handlerChildren, handlerEntry, null);
          faultStmts.Add(new IrRethrow());
          catches.Add(new(IrExceptionClass.Exception, null, null, new(faultStmts)));
          break;
        }
      }
    }

    return new(tryBody, catches, finallyBlock);
  }

  private IrExpr LiftFilterExpression(int filterOffset) {
    var filterEntry = this._method.Cfg.OffsetToBlock[filterOffset];
    var lifted = this._liftedBlocks[filterEntry];
    if (lifted.Exit is EndFilterExit endFilter)
      return endFilter.Condition;

    throw new MsilImportException(MsilFrontend.UnsupportedOpcode, "complex multi-block exception filters are not supported");
  }

  private static List<IrStmt>? TryEmitDirectSingleBlock(LiftedBlock single, IrLocal? outerPc) => single.Exit switch {
    ReturnExit ret => [.. single.Statements, new IrReturn(ret.Value)],
    ThrowExit thr => [.. single.Statements, new IrThrow(thr.Exception)],
    RethrowExit => [.. single.Statements, new IrRethrow()],
    EndFinallyExit => [.. single.Statements],
    LeaveExit lv => [.. single.Statements, new IrAssign(outerPc!, new IrConst(IrType.I32, lv.Target))],
    _ => null
  };

  private List<IrStmt>? TryEmitSimpleAcyclic(int entryBlockId, HashSet<int> nodeSet, IrLocal? outerPc) {
    var cfg = this._method.Cfg;
    var emitted = new HashSet<int>();
    var result = new List<IrStmt>();
    var current = entryBlockId;

    while (emitted.Add(current)) {
      var blk = this._liftedBlocks[current];
      result.AddRange(blk.Statements);

      switch (blk.Exit) {
        case ReturnExit ret:
          result.Add(new IrReturn(ret.Value));
          return emitted.Count == nodeSet.Count ? result : null;
        case FallthroughExit ft when nodeSet.Contains(ft.Target) && cfg.Blocks[ft.Target].Predecessors.Count == 1:
          result.AddRange(ft.EdgeAssigns);
          current = ft.Target;
          break;
        case BranchExit br when nodeSet.Contains(br.TrueTarget) && nodeSet.Contains(br.FalseTarget)
            && cfg.Blocks[br.TrueTarget].Predecessors.Count == 1 && cfg.Blocks[br.FalseTarget].Predecessors.Count == 1: {
          var tBlk = this._liftedBlocks[br.TrueTarget];
          var fBlk = this._liftedBlocks[br.FalseTarget];

          var tTerm = TryEmitDirectSingleBlock(tBlk, outerPc);
          var fTerm = TryEmitDirectSingleBlock(fBlk, outerPc);
          if (tTerm is not null && fTerm is not null) {
            emitted.Add(br.TrueTarget);
            emitted.Add(br.FalseTarget);
            result.Add(new IrIf(br.Condition, new([.. br.TrueAssigns, .. tTerm]), new([.. br.FalseAssigns, .. fTerm])));
            return emitted.Count == nodeSet.Count ? result : null;
          }

          if (tBlk.Exit is FallthroughExit tFt && fBlk.Exit is FallthroughExit fFt && tFt.Target == fFt.Target && nodeSet.Contains(tFt.Target) && cfg.Blocks[tFt.Target].Predecessors.Count == 2) {
            emitted.Add(br.TrueTarget);
            emitted.Add(br.FalseTarget);
            result.Add(new IrIf(
              br.Condition,
              new([.. br.TrueAssigns, .. tBlk.Statements, .. tFt.EdgeAssigns]),
              new([.. br.FalseAssigns, .. fBlk.Statements, .. fFt.EdgeAssigns])));
            current = tFt.Target;
            break;
          }

          return null;
        }
        default:
          return null;
      }
    }

    return null;
  }

  private static List<IrStmt> EmitDispatcherExit(BlockExit exit, HashSet<int> nodeSet, IrLocal pc, IrLocal? outerPc) {
    List<IrStmt> JumpTo(int target, List<IrStmt>? edgeAssigns) {
      var list = edgeAssigns is not null ? new List<IrStmt>(edgeAssigns) : [];
      if (nodeSet.Contains(target)) {
        list.Add(new IrAssign(pc, new IrConst(IrType.I32, target)));
      } else {
        if (outerPc is not null)
          list.Add(new IrAssign(outerPc, new IrConst(IrType.I32, target)));

        list.Add(new IrAssign(pc, new IrConst(IrType.I32, -1)));
      }

      return list;
    }

    return exit switch {
      FallthroughExit ft => JumpTo(ft.Target, ft.EdgeAssigns),
      LeaveExit lv => JumpTo(lv.Target, null),
      BranchExit br => [new IrIf(br.Condition, new(JumpTo(br.TrueTarget, br.TrueAssigns)), new(JumpTo(br.FalseTarget, br.FalseAssigns)))],
      SwitchExit sw => [EmitSwitchExit(sw, JumpTo)],
      ReturnExit ret => [new IrReturn(ret.Value)],
      ThrowExit thr => [new IrThrow(thr.Exception)],
      RethrowExit => [new IrRethrow()],
      _ => [new IrAssign(pc, new IrConst(IrType.I32, -1))]
    };
  }

  private static IrSwitch EmitSwitchExit(SwitchExit sw, Func<int, List<IrStmt>?, List<IrStmt>> jumpTo) {
    var sections = new List<IrSwitchSection>(sw.CaseTargets.Length + 1);
    for (var i = 0; i < sw.CaseTargets.Length; ++i)
      sections.Add(new([i], false, new(jumpTo(sw.CaseTargets[i], sw.CaseAssigns[i]))));

    sections.Add(new([], true, new(jumpTo(sw.DefaultTarget, sw.DefaultAssigns))));
    return new(sw.Value, sections);
  }

  private static bool HasBackEdge(ControlFlowGraph cfg, HashSet<int> nodeSet) {
    foreach (var id in nodeSet)
      foreach (var succ in cfg.Blocks[id].Successors)
        if (nodeSet.Contains(succ) && succ <= id)
          return true;

    return false;
  }

  // ------------------------------------------------------------------ Helpers

  private List<IrStmt> BuildEdgeAssigns(int targetBlockId, List<StackItem> stack) {
    if (!this._entrySlots.TryGetValue(targetBlockId, out var slots))
      return [];

    var assigns = new List<IrStmt>(slots.Length);
    for (var i = 0; i < slots.Length; ++i)
      assigns.Add(new IrAssign(slots[i], CoerceItem(stack[i], slots[i].Type)));

    return assigns;
  }

  private void SpillStack(List<StackItem> stack, List<IrStmt> stmts) {
    for (var i = 0; i < stack.Count; ++i) {
      var item = stack[i];
      if (item.Expr is not null && !IsConstantExpr(item.Expr)) {
        var tmp = this.AddTempLocal(item.Expr.Type, "t");
        stmts.Add(new IrAssign(tmp, item.Expr));
        stack[i] = new(new IrLocalRef(tmp), item.Type);
      }
    }
  }

  private IrLocal AddTempLocal(IrType type, string prefix) {
    var local = new IrLocal($"__{prefix}_{this._tempCounter++}", type);
    this._allLocals.Add(local);
    return local;
  }

  private IrLocal AddCatchLocal(IrClassType exceptionClass) {
    var local = new IrLocal($"__ex_{this._tempCounter++}", IrType.Object(exceptionClass));
    this._allLocals.Add(local);
    this._catchLocals.Add(local);
    return local;
  }

  private IrLocal RequireParameter(int index) {
    if ((uint)index >= (uint)this._method.Parameters.Count)
      throw new MsilImportException(MsilFrontend.UnverifiableStack, $"parameter index {index} is out of range in '{this._method.Entry.MethodName}'");

    return this._method.Parameters[index];
  }

  private IrLocal RequireClrLocal(int index) {
    if ((uint)index >= (uint)this._method.ClrLocals.Count)
      throw new MsilImportException(MsilFrontend.UnverifiableStack, $"local index {index} is out of range in '{this._method.Entry.MethodName}'");

    return this._method.ClrLocals[index];
  }

  private IrClassType ResolveCatchClass(EntityHandle catchType) {
    var resolved = this._loader.ResolveTypeToken(MetadataTokens.GetToken(catchType));
    if (resolved.IrType?.ClassType is { } cls)
      return cls;

    throw new MsilImportException(MsilFrontend.UnsupportedType, $"unsupported catch type '{resolved.Name}'");
  }

  private static MsilType ResolveNewObjType(ResolvedCall call) {
    if (MsilType.TryResolveExceptionClass(call.DeclaringType) is { } exClass)
      return MsilType.FromIr(IrType.Object(exClass));

    if (call.DeclaringType is "System.Span`1" or "System.ReadOnlySpan`1" && call.ParameterTypes[0].IrType?.Element is { IsNumeric: true } elem)
      return MsilType.FromIr(call.DeclaringType == "System.Span`1" ? IrType.Span(elem) : IrType.ReadOnlySpan(elem));

    throw new MsilImportException(MsilFrontend.UnsupportedCall, $"constructor '{call.DeclaringType}..ctor' is not supported");
  }

  private MsilType PopType(List<MsilType> stack, IlInstruction ins) {
    if (stack.Count == 0)
      throw new MsilImportException(MsilFrontend.UnverifiableStack, $"evaluation stack underflow at {ins.OpCode} (IL_{ins.Offset:X4}) in '{this._method.Entry.MethodName}'");

    var top = stack[^1];
    stack.RemoveAt(stack.Count - 1);
    return top;
  }

  private static StackItem PopItem(List<StackItem> stack) {
    var top = stack[^1];
    stack.RemoveAt(stack.Count - 1);
    return top;
  }

  private static StackItem[] PopItems(List<StackItem> stack, int count) {
    var result = new StackItem[count];
    for (var i = count - 1; i >= 0; --i)
      result[i] = PopItem(stack);

    return result;
  }

  private MsilType MergeStackTypes(MsilType a, MsilType b, int offset) {
    if (a == b)
      return a;

    if (a.Kind == MsilTypeKind.Null && b.IrType is { IsNullable: true })
      return b;

    if (b.Kind == MsilTypeKind.Null && a.IrType is { IsNullable: true })
      return a;

    if (a.IrType is { } ia && b.IrType is { } ib) {
      if ((ia == IrType.Bool || ia.IsWord) && (ib == IrType.Bool || ib.IsWord))
        return ia == IrType.U32 || ib == IrType.U32 ? MsilType.FromIr(IrType.U32) : MsilType.FromIr(IrType.I32);

      if (ia.IsLong && ib.IsLong)
        return MsilType.FromIr(IrType.I64);

      if (ia.IsFloat && ib.IsFloat)
        return MsilType.FromIr(IrType.F64);
    }

    throw new MsilImportException(MsilFrontend.UnverifiableStack, $"incompatible stack types '{a.Name}' and '{b.Name}' at IL_{offset:X4} in '{this._method.Entry.MethodName}'");
  }

  private static MsilType StackTypeOf(IrType type) => type.Kind switch {
    IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16 => MsilType.FromIr(IrType.I32),
    _ => MsilType.FromIr(type)
  };

  private static MsilType BinaryResultType(ILOpCode op, MsilType left, MsilType right) {
    var lt = left.IrType!;
    var rt = right.IrType!;
    if (lt.IsFloat || rt.IsFloat)
      return lt == IrType.F64 || rt == IrType.F64 ? MsilType.FromIr(IrType.F64) : MsilType.FromIr(IrType.F32);

    var is64 = lt.IsLong || rt.IsLong;
    if (op is ILOpCode.Div_un or ILOpCode.Rem_un or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf_un or ILOpCode.Mul_ovf_un)
      return MsilType.FromIr(is64 ? IrType.U64 : IrType.U32);

    if (op is ILOpCode.Div or ILOpCode.Rem or ILOpCode.Add_ovf or ILOpCode.Sub_ovf or ILOpCode.Mul_ovf)
      return MsilType.FromIr(is64 ? IrType.I64 : IrType.I32);

    if (is64)
      return lt == IrType.U64 && rt == IrType.U64 ? MsilType.FromIr(IrType.U64) : MsilType.FromIr(IrType.I64);

    return lt == IrType.U32 && rt == IrType.U32 ? MsilType.FromIr(IrType.U32) : MsilType.FromIr(IrType.I32);
  }

  private static MsilType ShiftResultType(ILOpCode op, MsilType left) {
    var is64 = left.IrType!.IsLong;
    if (op == ILOpCode.Shr_un)
      return MsilType.FromIr(is64 ? IrType.U64 : IrType.U32);

    if (op == ILOpCode.Shr)
      return MsilType.FromIr(is64 ? IrType.I64 : IrType.I32);

    return left.IrType.IsWideInteger ? left : MsilType.FromIr(IrType.I32);
  }

  private static IrExpr LiftBinary(ILOpCode opCode, StackItem leftItem, StackItem rightItem) {
    var resultType = BinaryResultType(opCode, leftItem.Type, rightItem.Type).IrType!;
    var left = CoerceToType(leftItem.Expr!, resultType);
    var right = CoerceToType(rightItem.Expr!, resultType);

    var irOp = opCode switch {
      ILOpCode.Add => IrBinaryOp.Add,
      ILOpCode.Sub => IrBinaryOp.Subtract,
      ILOpCode.Mul => IrBinaryOp.Multiply,
      ILOpCode.Div or ILOpCode.Div_un => IrBinaryOp.Divide,
      ILOpCode.Rem or ILOpCode.Rem_un => IrBinaryOp.Remainder,
      ILOpCode.And => IrBinaryOp.And,
      ILOpCode.Or => IrBinaryOp.Or,
      ILOpCode.Xor => IrBinaryOp.Xor,
      ILOpCode.Add_ovf or ILOpCode.Add_ovf_un => IrBinaryOp.AddChecked,
      ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un => IrBinaryOp.SubtractChecked,
      _ => IrBinaryOp.MultiplyChecked
    };

    return new IrBinary(irOp, left, right);
  }

  private static IrExpr LiftShift(ILOpCode opCode, StackItem leftItem, StackItem shiftItem) {
    var resultType = ShiftResultType(opCode, leftItem.Type).IrType!;
    var left = CoerceToType(leftItem.Expr!, resultType);
    var shift = CoerceToType(shiftItem.Expr!, IrType.I32);
    var irOp = opCode switch {
      ILOpCode.Shl => IrBinaryOp.ShiftLeft,
      ILOpCode.Shr => IrBinaryOp.ShiftRight,
      _ => IrBinaryOp.ShiftRightLogical
    };

    return new IrBinary(irOp, left, shift);
  }

  private sealed record CompareSpec(IrBinaryOp IntOp, bool Unsigned, IrBinaryOp FloatOp, bool InvertFloat);

  private static readonly Dictionary<ILOpCode, CompareSpec> _compareSpecs = new() {
    [ILOpCode.Ceq] = new(IrBinaryOp.Equal, false, IrBinaryOp.Equal, false),
    [ILOpCode.Cgt] = new(IrBinaryOp.Greater, false, IrBinaryOp.Greater, false),
    [ILOpCode.Clt] = new(IrBinaryOp.Less, false, IrBinaryOp.Less, false),
    [ILOpCode.Cgt_un] = new(IrBinaryOp.Greater, true, IrBinaryOp.LessOrEqual, true),
    [ILOpCode.Clt_un] = new(IrBinaryOp.Less, true, IrBinaryOp.GreaterOrEqual, true),
    [ILOpCode.Beq] = new(IrBinaryOp.Equal, false, IrBinaryOp.Equal, false),
    [ILOpCode.Beq_s] = new(IrBinaryOp.Equal, false, IrBinaryOp.Equal, false),
    [ILOpCode.Bne_un] = new(IrBinaryOp.NotEqual, false, IrBinaryOp.NotEqual, false),
    [ILOpCode.Bne_un_s] = new(IrBinaryOp.NotEqual, false, IrBinaryOp.NotEqual, false),
    [ILOpCode.Bgt] = new(IrBinaryOp.Greater, false, IrBinaryOp.Greater, false),
    [ILOpCode.Bgt_s] = new(IrBinaryOp.Greater, false, IrBinaryOp.Greater, false),
    [ILOpCode.Bge] = new(IrBinaryOp.GreaterOrEqual, false, IrBinaryOp.GreaterOrEqual, false),
    [ILOpCode.Bge_s] = new(IrBinaryOp.GreaterOrEqual, false, IrBinaryOp.GreaterOrEqual, false),
    [ILOpCode.Blt] = new(IrBinaryOp.Less, false, IrBinaryOp.Less, false),
    [ILOpCode.Blt_s] = new(IrBinaryOp.Less, false, IrBinaryOp.Less, false),
    [ILOpCode.Ble] = new(IrBinaryOp.LessOrEqual, false, IrBinaryOp.LessOrEqual, false),
    [ILOpCode.Ble_s] = new(IrBinaryOp.LessOrEqual, false, IrBinaryOp.LessOrEqual, false),
    [ILOpCode.Bgt_un] = new(IrBinaryOp.Greater, true, IrBinaryOp.LessOrEqual, true),
    [ILOpCode.Bgt_un_s] = new(IrBinaryOp.Greater, true, IrBinaryOp.LessOrEqual, true),
    [ILOpCode.Bge_un] = new(IrBinaryOp.GreaterOrEqual, true, IrBinaryOp.Less, true),
    [ILOpCode.Bge_un_s] = new(IrBinaryOp.GreaterOrEqual, true, IrBinaryOp.Less, true),
    [ILOpCode.Blt_un] = new(IrBinaryOp.Less, true, IrBinaryOp.GreaterOrEqual, true),
    [ILOpCode.Blt_un_s] = new(IrBinaryOp.Less, true, IrBinaryOp.GreaterOrEqual, true),
    [ILOpCode.Ble_un] = new(IrBinaryOp.LessOrEqual, true, IrBinaryOp.Greater, true),
    [ILOpCode.Ble_un_s] = new(IrBinaryOp.LessOrEqual, true, IrBinaryOp.Greater, true)
  };

  private static IrExpr LiftCompare(ILOpCode opCode, StackItem leftItem, StackItem rightItem) {
    var spec = _compareSpecs[opCode];
    var isEq = spec.IntOp == IrBinaryOp.Equal;

    if (leftItem.Type.Kind == MsilTypeKind.Null || rightItem.Type.Kind == MsilTypeKind.Null) {
      if (leftItem.Type.Kind == MsilTypeKind.Null && rightItem.Type.Kind == MsilTypeKind.Null)
        return new IrConst(IrType.Bool, isEq ? 1 : 0);

      var nonNull = leftItem.Type.Kind == MsilTypeKind.Null ? rightItem.Expr! : leftItem.Expr!;
      var isNull = new IrIsNull(nonNull);
      return isEq ? isNull : new IrUnary(IrUnaryOp.LogicalNot, isNull);
    }

    var left = leftItem.Expr!;
    var right = rightItem.Expr!;

    if (left.Type.IsNullable) {
      var same = new IrReferenceEquals(left, right);
      return isEq ? same : new IrUnary(IrUnaryOp.LogicalNot, same);
    }

    if (left.Type.IsFloat || right.Type.IsFloat) {
      var floatType = left.Type == IrType.F64 || right.Type == IrType.F64 ? IrType.F64 : IrType.F32;
      IrExpr cmp = new IrBinary(spec.FloatOp, CoerceToType(left, floatType), CoerceToType(right, floatType));
      return spec.InvertFloat ? new IrUnary(IrUnaryOp.LogicalNot, cmp) : cmp;
    }

    var is64 = left.Type.IsLong || right.Type.IsLong;
    var cmpType = spec.Unsigned ? (is64 ? IrType.U64 : IrType.U32) : (is64 ? IrType.I64 : IrType.I32);
    return new IrBinary(spec.IntOp, CoerceToType(left, cmpType), CoerceToType(right, cmpType));
  }

  private static IrExpr LiftConversion(ConversionSpec spec, IrExpr value) {
    if (value.Type == IrType.Bool)
      value = BoolToI32(value);

    if (value.Type.IsInteger) {
      if (spec.UnsignedSource)
        value = CoerceToType(value, value.Type.IsLong ? IrType.U64 : IrType.U32);
      else if (spec.SignedSource)
        value = CoerceToType(value, value.Type.IsLong ? IrType.I64 : IrType.I32);
    }

    var target = spec.Target;
    var contract = target.IsFloat
      ? IrConversion.Wrap
      : spec.Checked ? IrConversion.Trap
      : value.Type.IsFloat ? IrConversion.Saturate
      : IrConversion.Wrap;

    var converted = value.Type == target && !spec.Checked ? value : new IrConvert(target, value, contract);
    return WidenForStack(converted);
  }

  private static IrExpr WidenForStack(IrExpr expr)
    => expr.Type.Kind is IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16
      ? new IrConvert(IrType.I32, expr, IrConversion.Wrap)
      : expr;

  private static IrExpr CoerceItem(StackItem item, IrType targetType)
    => item.Type.Kind == MsilTypeKind.Null ? new IrNull(targetType) : CoerceToType(item.Expr!, targetType);

  private static IrExpr CoerceToType(IrExpr expr, IrType targetType) {
    if (expr.Type.IsAssignableTo(targetType))
      return expr;

    if (targetType == IrType.Bool)
      return ToBoolExpr(expr);

    if (expr.Type == IrType.Bool)
      expr = BoolToI32(expr);

    if (expr.Type == targetType)
      return expr;

    var contract = !targetType.IsFloat && expr.Type.IsFloat ? IrConversion.Saturate : IrConversion.Wrap;
    return new IrConvert(targetType, expr, contract);
  }

  private static IrExpr ToBool(StackItem item)
    => item.Type.Kind == MsilTypeKind.Null ? new IrConst(IrType.Bool, 0) : ToBoolExpr(item.Expr!);

  private static IrExpr ToBoolExpr(IrExpr expr) {
    if (expr.Type == IrType.Bool)
      return expr;

    if (expr.Type.IsNullable)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(expr));

    var wide = WidenForStack(expr);
    return new IrBinary(IrBinaryOp.NotEqual, wide, new IrConst(wide.Type, 0));
  }

  private static IrExpr BoolToI32(IrExpr expr)
    => new IrConditional(expr, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));

  private static IrExpr? DefaultValueExpr(IrType type) => type.Kind switch {
    IrTypeKind.Bool => new IrConst(IrType.Bool, 0),
    IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16
      or IrTypeKind.I32 or IrTypeKind.U32 or IrTypeKind.I64 or IrTypeKind.U64 => new IrConst(type, 0),
    IrTypeKind.F32 => new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 0.0)),
    IrTypeKind.F64 => new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 0.0)),
    _ when type.IsNullable => new IrNull(type),
    _ => null
  };

  private static bool IsTrivial(IrExpr expr) => expr is IrConst or IrStrConst or IrNull or IrLocalRef;

  private static bool IsConstantExpr(IrExpr expr) => expr is IrConst or IrStrConst or IrNull;

  private static void PushValue(List<StackItem> stack, IrExpr expr)
    => stack.Add(new(expr, MsilType.FromIr(expr.Type)));

  private static IrExpr PopExpr(List<StackItem> stack) {
    var top = stack[^1];
    stack.RemoveAt(stack.Count - 1);
    return top.Expr!;
  }

}
