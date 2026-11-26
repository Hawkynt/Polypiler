using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

/// <summary>Compiles IR functions, class members, synthesized helpers, static constructors, and console entry points into verifiable CIL.</summary>
internal sealed partial class MsilMethodCompiler {

  private sealed record LoopTarget(LabelHandle BreakLabel, LabelHandle? ContinueLabel, int TryDepth);

  private readonly MsilMetadataContext _ctx;
  private readonly IrType _returnType;
  private readonly Dictionary<IrLocal, int> _paramSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLocal, int> _localSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLocal, int> _capturedLocalSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLocal, FieldDefinitionHandle> _outerCaptureFields = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLocal, int> _addressedLocalSlots = new(ReferenceEqualityComparer.Instance);
  private readonly List<Action<SignatureTypeEncoder>> _localEncoders = [];
  private readonly Stack<LoopTarget> _loops = new();
  private readonly ControlFlowBuilder _flow = new();
  private readonly BlobBuilder _code = new();
  private readonly InstructionEncoder _il;
  private int _tryDepth;
  private int _returnSlot = -1;
  private LabelHandle _returnLabel;
  private bool _hasReturnLabel;
  private bool _isAsyncMethod;
  private int _iteratorListSlot = -1;
  private IrType? _iteratorElemType;
  private LabelHandle _iteratorEndLabel;

  private MsilMethodCompiler(
    MsilMetadataContext ctx,
    IrType returnType,
    IReadOnlyList<IrLocal> parameters,
    IReadOnlyList<IrLocal> locals,
    int paramArgOffset = 0) {
    this._ctx = ctx;
    this._returnType = returnType;
    this._il = new InstructionEncoder(this._code, this._flow);

    for (var i = 0; i < parameters.Count; i++) {
      var param = parameters[i];
      this._paramSlots[param] = i + paramArgOffset;
      if (ctx.Plan.CapturedLocals.Contains(param)) {
        var cell = ctx.CellTypes[param.Type];
        this._capturedLocalSlots[param] = this.AllocCustomTemp(enc => enc.Type(cell.Type, isValueType: false));
      } else if (ctx.Plan.AddressedLocals.Contains(param)) {
        this._addressedLocalSlots[param] = this.AllocTemp(IrType.Array(param.Type));
      }
    }

    foreach (var local in locals) {
      if (ctx.Plan.CapturedLocals.Contains(local)) {
        var cell = ctx.CellTypes[local.Type];
        this._capturedLocalSlots[local] = this.AllocCustomTemp(enc => enc.Type(cell.Type, isValueType: false));
      } else if (ctx.Plan.AddressedLocals.Contains(local)) {
        this._addressedLocalSlots[local] = this.AllocTemp(IrType.Array(local.Type));
      } else {
        this._localSlots[local] = this.AllocTemp(local.Type);
      }
    }
  }

  /// <summary>Compiles an <see cref="IrFunction"/> into <paramref name="bodies"/> and returns its IL body offset.</summary>
  public static int CompileFunction(IrFunction function, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    if (function.ReturnType.IsLazySequence)
      return CompileIteratorFactory(function, ctx, bodies);
    if (function.ReturnType.IsAsync)
      return CompileAsyncFunction(function, ctx, bodies);

    var compiler = new MsilMethodCompiler(ctx, function.ReturnType, function.Parameters, function.Locals);
    compiler.EmitCapturedParamInit(function.Parameters);
    compiler.EmitAddressedParamInit(function.Parameters);
    compiler.EmitStmt(function.Body);
    compiler.EmitEpilogue();
    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles a class constructor into <paramref name="bodies"/> and returns its IL body offset.</summary>
  public static int CompileClassConstructor(IrClassType cls, IrConstructor ctor, IrFunction fn, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Void, fn.Parameters, fn.Locals);
    compiler.EmitCapturedParamInit(fn.Parameters);
    compiler.EmitAddressedParamInit(fn.Parameters);

    var chainsToOwn = fn.Body.Statements is [IrExprStmt { Expression: IrCall first }, ..]
      && ctx.Plan.ClassConstructorsByFn.TryGetValue(first.Function, out var target)
      && ReferenceEquals(target.Owner, cls);

    if ((cls.Base is null && !chainsToOwn) || ctor.SkipsBase) {
      compiler._il.LoadArgument(0);
      compiler._il.Call(ctx.GetObjectCtor());
    }

    compiler.EmitStmt(fn.Body);
    compiler.EmitEpilogue();
    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles a structural equality helper for a record class <paramref name="cls"/>.</summary>
  public static int CompileRecordEqualsHelper(IrClassType cls, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Bool, [], []);
    var trueLabel = compiler._il.DefineLabel();
    var falseLabel = compiler._il.DefineLabel();

    // if (ReferenceEquals(a, b)) return true;
    compiler._il.LoadArgument(0);
    compiler._il.LoadArgument(1);
    compiler._il.Branch(ILOpCode.Beq, trueLabel);

    // if (a == null || b == null) return false;
    compiler._il.LoadArgument(0);
    compiler._il.Branch(ILOpCode.Brfalse, falseLabel);
    compiler._il.LoadArgument(1);
    compiler._il.Branch(ILOpCode.Brfalse, falseLabel);

    foreach (var field in cls.AllFields) {
      var token = ctx.FieldHandles[field];
      if (field.Type.IsStruct) {
        compiler.EmitStructFieldsEqual(
          field.Type.StructType!,
          () => { compiler._il.LoadArgument(0); compiler._il.OpCode(ILOpCode.Ldflda); compiler._il.Token(token); },
          () => { compiler._il.LoadArgument(1); compiler._il.OpCode(ILOpCode.Ldflda); compiler._il.Token(token); },
          falseLabel);
      } else if (field.Type.ClassType is { IsRecord: true } nestedRec) {
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.LoadArgument(1);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.Call(ctx.RecordEqualsHandles[nestedRec]);
        compiler._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else if (field.Type.IsString) {
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.LoadArgument(1);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.LoadConstantI4(4); // StringComparison.Ordinal
        compiler._il.Call(compiler.GetStringComparisonStaticRef("Equals", IrType.Bool));
        compiler._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else if (field.Type == IrType.F32) {
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldflda);
        compiler._il.Token(token);
        compiler._il.LoadArgument(1);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.Call(ctx.GetInstanceMethodRef("System", "Single", "Equals", IrType.Bool, IrType.F32));
        compiler._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else if (field.Type == IrType.F64) {
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldflda);
        compiler._il.Token(token);
        compiler._il.LoadArgument(1);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.Call(ctx.GetInstanceMethodRef("System", "Double", "Equals", IrType.Bool, IrType.F64));
        compiler._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else {
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.LoadArgument(1);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(token);
        compiler._il.Branch(ILOpCode.Bne_un, falseLabel);
      }
    }

    compiler._il.MarkLabel(trueLabel);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Ret);

    compiler._il.MarkLabel(falseLabel);
    compiler._il.LoadConstantI4(0);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the static constructor (<c>.cctor</c>) initializing module constants, globals, and optional initializer.</summary>
  public static int CompileStaticCtor(IrModule module, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Void, [], []);
    foreach (var constant in module.Constants) {
      compiler.EmitModuleConstValue(constant);
      compiler._il.OpCode(ILOpCode.Stsfld);
      compiler._il.Token(ctx.ConstantFields[constant]);
    }

    foreach (var global in module.Globals) {
      if (global.Initializer is not null) {
        compiler.EmitExpr(global.Initializer);
        compiler._il.OpCode(ILOpCode.Stsfld);
        compiler._il.Token(ctx.GlobalFields[global]);
      }
    }

    if (module.Initializer is not null)
      compiler._il.Call(ctx.FunctionHandles[module.Initializer]);

    compiler._il.OpCode(ILOpCode.Ret);
    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the console executable entry point wrapper (<c>&lt;Main&gt;$(string[] args) -&gt; int</c>).</summary>
  public static int CompileEntryPoint(IrFunction? candidate, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.I32, [], []);
    if (candidate is not null) {
      if (candidate.Parameters.Count == 1)
        compiler._il.LoadArgument(0);
      compiler._il.Call(ctx.FunctionHandles[candidate.Name]);
      if (candidate.ReturnType == IrType.Void)
        compiler._il.LoadConstantI4(0);
      else if (candidate.ReturnType.IsLong)
        compiler._il.OpCode(ILOpCode.Conv_i4);
    } else {
      compiler._il.LoadConstantI4(0);
    }

    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: 8);
  }

  private void EmitCapturedParamInit(IReadOnlyList<IrLocal> parameters) {
    foreach (var param in parameters) {
      if (!this._capturedLocalSlots.TryGetValue(param, out var cellSlot))
        continue;

      var cell = this._ctx.CellTypes[param.Type];
      this._il.OpCode(ILOpCode.Newobj);
      this._il.Token(cell.Ctor);
      this._il.OpCode(ILOpCode.Dup);
      this._il.LoadArgument(this._paramSlots[param]);
      this._il.OpCode(ILOpCode.Stfld);
      this._il.Token(cell.ValueField);
      this._il.StoreLocal(cellSlot);
    }
  }

  private void EmitAddressedParamInit(IReadOnlyList<IrLocal> parameters) {
    foreach (var param in parameters) {
      if (!this._addressedLocalSlots.TryGetValue(param, out var arrSlot))
        continue;

      this._il.LoadConstantI4(1);
      this._il.OpCode(ILOpCode.Newarr);
      this._il.Token(this._ctx.GetTypeToken(param.Type));
      this._il.OpCode(ILOpCode.Dup);
      this._il.LoadConstantI4(0);
      this._il.LoadArgument(this._paramSlots[param]);
      this.EmitStoreElement(param.Type);
      this._il.StoreLocal(arrSlot);
    }
  }

  private void EmitEpilogue() {
    if (this._hasReturnLabel) {
      this._il.MarkLabel(this._returnLabel);
      if (this._returnType != IrType.Void)
        this._il.LoadLocal(this._returnSlot);
      this._il.OpCode(ILOpCode.Ret);
    } else if (this._returnType == IrType.Void) {
      this._il.OpCode(ILOpCode.Ret);
    } else {
      this.EmitDefaultValue(this._returnType);
      this._il.OpCode(ILOpCode.Ret);
    }
  }

  private int AllocTemp(IrType type) {
    var slot = this._localEncoders.Count;
    this._localEncoders.Add(enc => this._ctx.EncodeType(enc, type));
    return slot;
  }

  private int AllocCustomTemp(Action<SignatureTypeEncoder> encode) {
    var slot = this._localEncoders.Count;
    this._localEncoders.Add(encode);
    return slot;
  }

  private void EnsureReturnTarget() {
    if (this._hasReturnLabel)
      return;

    this._hasReturnLabel = true;
    this._returnLabel = this._il.DefineLabel();
    if (this._returnType != IrType.Void)
      this._returnSlot = this.AllocTemp(this._returnType);
  }

  private void EmitModuleConstValue(IrModuleConst constant) {
    if (constant.Type == IrType.String) {
      this.EmitStringLiteral(constant.Strings[0]);
      return;
    }

    if (!constant.Type.IsArray) {
      this.EmitConstValue(constant.Type, constant.Values[0]);
      return;
    }

    var elemType = constant.Type.Element!;
    var count = elemType == IrType.String ? constant.Strings.Count : constant.Values.Count;
    this._il.LoadConstantI4(count);
    this._il.OpCode(ILOpCode.Newarr);
    this._il.Token(this._ctx.GetTypeToken(elemType));
    for (var i = 0; i < count; i++) {
      this._il.OpCode(ILOpCode.Dup);
      this._il.LoadConstantI4(i);
      if (elemType == IrType.String)
        this.EmitStringLiteral(constant.Strings[i]);
      else
        this.EmitConstValue(elemType, constant.Values[i]);
      this.EmitStoreElement(elemType);
    }
  }

  private void EmitStmt(IrStmt stmt) {
    switch (stmt) {
      case IrBlock block:
        foreach (var child in block.Statements)
          this.EmitStmt(child);
        break;

      case IrDeclare declare:
        this.EnsureCapturedCell(declare.Local);
        if (this._addressedLocalSlots.TryGetValue(declare.Local, out var addrSlot)) {
          this._il.LoadConstantI4(1);
          this._il.OpCode(ILOpCode.Newarr);
          this._il.Token(this._ctx.GetTypeToken(declare.Local.Type));
          this._il.StoreLocal(addrSlot);
        }
        if (declare.Initializer is not null) {
          this.EmitExpr(declare.Initializer);
          this.EmitStoreLocalOrParam(declare.Local);
        }
        break;

      case IrAssign assign:
        this.EmitExpr(assign.Value);
        this.EmitStoreLocalOrParam(assign.Local);
        break;

      case IrGlobalAssign globalAssign:
        this.EmitExpr(globalAssign.Value);
        this._il.OpCode(ILOpCode.Stsfld);
        this._il.Token(this._ctx.GlobalFields[globalAssign.Global]);
        break;

      case IrStore store:
        if (store.Array.Type.IsView) {
          this.EmitViewStore(store);
        } else {
          this.EmitExpr(store.Array);
          this.EmitExpr(store.Index);
          this.EmitExpr(store.Value);
          this.EmitStoreElement(store.Array.Type.Element!);
        }
        break;

      case IrFieldStore fieldStore:
        if (fieldStore.Target.Type.IsObject)
          this.EmitExpr(fieldStore.Target);
        else
          this.EmitAddress(fieldStore.Target);
        this.EmitExpr(fieldStore.Value);
        this._il.OpCode(ILOpCode.Stfld);
        this._il.Token(this._ctx.FieldHandles[fieldStore.Field]);
        break;

      case IrExprStmt exprStmt:
        this.EmitExpr(exprStmt.Expression);
        if (exprStmt.Expression.Type != IrType.Void)
          this._il.OpCode(ILOpCode.Pop);
        break;

      case IrWriteLine write:
        this.EmitExpr(write.Value);
        this._il.Call(this._ctx.GetMemberRef(
          "stat:System.Console::WriteLine(string)",
          this._ctx.GetAssemblyType("System.Console", "System", "Console"),
          "WriteLine",
          sig => sig.Parameters(1, r => r.Void(), p => p.AddParameter().Type().String())));
        break;

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

      case IrSwitch selection:
        this.EmitSwitch(selection);
        break;

      case IrBreak: {
        var target = this._loops.Peek();
        this._il.Branch(this._tryDepth > target.TryDepth ? ILOpCode.Leave : ILOpCode.Br, target.BreakLabel);
        break;
      }

      case IrContinue: {
        LoopTarget? target = null;
        foreach (var candidate in this._loops) {
          if (candidate.ContinueLabel is not null) {
            target = candidate;
            break;
          }
        }
        this._il.Branch(this._tryDepth > target!.TryDepth ? ILOpCode.Leave : ILOpCode.Br, target.ContinueLabel!.Value);
        break;
      }

      case IrReturn ret:
        if (this._isAsyncMethod) {
          if (this._returnType.Element == IrType.Void) {
            this._il.Call(this._ctx.GetTaskCompletedTask());
          } else {
            this.EmitExpr(ret.Value!);
            this._il.Call(this._ctx.GetTaskFromResult(this._returnType.Element!));
          }
          this._il.StoreLocal(this._returnSlot);
          this._il.Branch(ILOpCode.Leave, this._returnLabel);
        } else if (this._tryDepth > 0) {
          this.EnsureReturnTarget();
          if (ret.Value is not null) {
            this.EmitExpr(ret.Value);
            this._il.StoreLocal(this._returnSlot);
          }
          this._il.Branch(ILOpCode.Leave, this._returnLabel);
        } else {
          if (ret.Value is not null)
            this.EmitExpr(ret.Value);
          this._il.OpCode(ILOpCode.Ret);
        }
        break;

      case IrThrow throwing:
        this.EmitExpr(throwing.Exception);
        this._il.OpCode(ILOpCode.Throw);
        break;

      case IrRethrow:
        this._il.OpCode(ILOpCode.Rethrow);
        break;

      case IrTry trying:
        this.EmitTry(trying);
        break;

      case IrUsing scope:
        this.EmitStmt(scope.Lowered());
        break;

      case IrForEachCollection fe:
        this.EmitForEachCollection(fe);
        break;

      case IrYield yielded:
        this.EmitYield(yielded);
        break;

      case IrYieldBreak:
        this.EmitYieldBreak();
        break;

      case IrYieldFrom delegated:
        this.EmitYieldFrom(delegated);
        break;

      case IrForEachSequence feSeq:
        this.EmitForEachSequence(feSeq);
        break;
    }
  }

  private void EmitIf(IrIf conditional) {
    var elseLabel = this._il.DefineLabel();
    this.EmitExpr(conditional.Condition);
    this._il.Branch(ILOpCode.Brfalse, elseLabel);
    this.EmitStmt(conditional.Then);
    if (conditional.Else is not null) {
      var endLabel = this._il.DefineLabel();
      this._il.Branch(ILOpCode.Br, endLabel);
      this._il.MarkLabel(elseLabel);
      this.EmitStmt(conditional.Else);
      this._il.MarkLabel(endLabel);
    } else {
      this._il.MarkLabel(elseLabel);
    }
  }

  private void EmitWhile(IrWhile loop) {
    var condLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();
    this._il.MarkLabel(condLabel);
    this.EmitExpr(loop.Condition);
    this._il.Branch(ILOpCode.Brfalse, breakLabel);
    this._loops.Push(new LoopTarget(breakLabel, condLabel, this._tryDepth));
    this.EmitStmt(loop.Body);
    this._loops.Pop();
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(breakLabel);
  }

  private void EmitDoWhile(IrDoWhile loop) {
    var bodyLabel = this._il.DefineLabel();
    var condLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();
    this._il.MarkLabel(bodyLabel);
    this._loops.Push(new LoopTarget(breakLabel, condLabel, this._tryDepth));
    this.EmitStmt(loop.Body);
    this._loops.Pop();
    this._il.MarkLabel(condLabel);
    this.EmitExpr(loop.Condition);
    this._il.Branch(ILOpCode.Brtrue, bodyLabel);
    this._il.MarkLabel(breakLabel);
  }

  private void EmitFor(IrFor loop) {
    if (loop.PerIteration.Count > 0)
      this.EmitRenewPerIteration(loop.PerIteration);
    var condLabel = this._il.DefineLabel();
    var stepLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();
    this._il.MarkLabel(condLabel);
    this.EmitExpr(loop.Condition);
    this._il.Branch(ILOpCode.Brfalse, breakLabel);
    this._loops.Push(new LoopTarget(breakLabel, stepLabel, this._tryDepth));
    this.EmitStmt(loop.Body);
    this._loops.Pop();
    this._il.MarkLabel(stepLabel);
    if (loop.PerIteration.Count > 0)
      this.EmitRenewPerIteration(loop.PerIteration);
    this.EmitStmt(loop.Step);
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(breakLabel);
  }

  private void EmitSwitch(IrSwitch selection) {
    var valSlot = this.AllocTemp(selection.Value.Type);
    this.EmitExpr(selection.Value);
    this._il.StoreLocal(valSlot);

    var breakLabel = this._il.DefineLabel();
    var sectionLabels = new LabelHandle[selection.Sections.Count];
    var defaultLabel = breakLabel;
    for (var i = 0; i < selection.Sections.Count; i++) {
      sectionLabels[i] = this._il.DefineLabel();
      if (selection.Sections[i].IsDefault)
        defaultLabel = sectionLabels[i];
    }

    for (var i = 0; i < selection.Sections.Count; i++) {
      foreach (var labelValue in selection.Sections[i].Labels) {
        this._il.LoadLocal(valSlot);
        this.EmitConstValue(selection.Value.Type, labelValue);
        this._il.Branch(ILOpCode.Beq, sectionLabels[i]);
      }
    }

    this._il.Branch(ILOpCode.Br, defaultLabel);

    this._loops.Push(new LoopTarget(breakLabel, null, this._tryDepth));
    for (var i = 0; i < selection.Sections.Count; i++) {
      this._il.MarkLabel(sectionLabels[i]);
      this.EmitStmt(selection.Sections[i].Body);
      this._il.Branch(ILOpCode.Br, breakLabel);
    }
    this._loops.Pop();
    this._il.MarkLabel(breakLabel);
  }

  private void EmitTry(IrTry trying) {
    var hasCatches = trying.Catches.Count > 0;
    var hasFinally = trying.Finally is not null;
    var outerEnd = this._il.DefineLabel();

    if (hasFinally && hasCatches) {
      var outerTryStart = this._il.DefineLabel();
      var finallyStart = this._il.DefineLabel();
      var finallyEnd = this._il.DefineLabel();
      var innerEnd = this._il.DefineLabel();

      this._il.MarkLabel(outerTryStart);
      this._tryDepth++;
      this.EmitTryCatches(trying.Body, trying.Catches, innerEnd);
      this._il.MarkLabel(innerEnd);
      this._il.Branch(ILOpCode.Leave, outerEnd);
      this._tryDepth--;

      this._il.MarkLabel(finallyStart);
      this.EmitStmt(trying.Finally!);
      this._il.OpCode(ILOpCode.Endfinally);
      this._il.MarkLabel(finallyEnd);

      this._flow.AddFinallyRegion(outerTryStart, finallyStart, finallyStart, finallyEnd);
      this._il.MarkLabel(outerEnd);
    } else if (hasCatches) {
      this.EmitTryCatches(trying.Body, trying.Catches, outerEnd);
      this._il.MarkLabel(outerEnd);
    } else {
      var tryStart = this._il.DefineLabel();
      var finallyStart = this._il.DefineLabel();
      var finallyEnd = this._il.DefineLabel();

      this._il.MarkLabel(tryStart);
      this._tryDepth++;
      this.EmitStmt(trying.Body);
      this._il.Branch(ILOpCode.Leave, outerEnd);
      this._tryDepth--;

      this._il.MarkLabel(finallyStart);
      this.EmitStmt(trying.Finally!);
      this._il.OpCode(ILOpCode.Endfinally);
      this._il.MarkLabel(finallyEnd);

      this._flow.AddFinallyRegion(tryStart, finallyStart, finallyStart, finallyEnd);
      this._il.MarkLabel(outerEnd);
    }
  }

  private void EmitTryCatches(IrBlock body, IReadOnlyList<IrCatch> catches, LabelHandle afterLabel) {
    var tryStart = this._il.DefineLabel();
    var tryEnd = this._il.DefineLabel();

    this._il.MarkLabel(tryStart);
    this._tryDepth++;
    this.EmitStmt(body);
    this._il.Branch(ILOpCode.Leave, afterLabel);
    this._il.MarkLabel(tryEnd);

    foreach (var handler in catches) {
      var catchType = this._ctx.GetClassToken(handler.Class);
      if (handler.Filter is not null) {
        var filterStart = this._il.DefineLabel();
        var rejectLabel = this._il.DefineLabel();
        var filterEnd = this._il.DefineLabel();
        var handlerStart = this._il.DefineLabel();
        var handlerEnd = this._il.DefineLabel();

        this._il.MarkLabel(filterStart);
        this._il.OpCode(ILOpCode.Isinst);
        this._il.Token(catchType);
        this._il.OpCode(ILOpCode.Dup);
        this._il.Branch(ILOpCode.Brfalse, rejectLabel);
        if (handler.Local is not null) {
          this.EnsureCapturedCell(handler.Local);
          this.EmitStoreLocalOrParam(handler.Local);
        } else {
          this._il.OpCode(ILOpCode.Pop);
        }
        this.EmitExpr(handler.Filter);
        this._il.Branch(ILOpCode.Br, filterEnd);
        this._il.MarkLabel(rejectLabel);
        this._il.OpCode(ILOpCode.Pop);
        this._il.LoadConstantI4(0);
        this._il.MarkLabel(filterEnd);
        this._il.OpCode(ILOpCode.Endfilter);

        this._il.MarkLabel(handlerStart);
        if (handler.Local is not null) {
          this._il.OpCode(ILOpCode.Castclass);
          this._il.Token(catchType);
          this.EnsureCapturedCell(handler.Local);
          this.EmitStoreLocalOrParam(handler.Local);
        } else {
          this._il.OpCode(ILOpCode.Pop);
        }
        this.EmitStmt(handler.Body);
        this._il.Branch(ILOpCode.Leave, afterLabel);
        this._il.MarkLabel(handlerEnd);

        this._flow.AddFilterRegion(tryStart, tryEnd, handlerStart, handlerEnd, filterStart);
      } else {
        var handlerStart = this._il.DefineLabel();
        var handlerEnd = this._il.DefineLabel();

        this._il.MarkLabel(handlerStart);
        if (handler.Local is not null) {
          this.EnsureCapturedCell(handler.Local);
          this.EmitStoreLocalOrParam(handler.Local);
        } else {
          this._il.OpCode(ILOpCode.Pop);
        }
        this.EmitStmt(handler.Body);
        this._il.Branch(ILOpCode.Leave, afterLabel);
        this._il.MarkLabel(handlerEnd);

        this._flow.AddCatchRegion(tryStart, tryEnd, handlerStart, handlerEnd, catchType);
      }
    }

    this._tryDepth--;
  }

  private void EmitExpr(IrExpr expr) {
    switch (expr) {
      case IrConst constant:
        this.EmitConstValue(constant.Type, constant.Value);
        break;

      case IrStrConst str:
        this.EmitStringLiteral(str.Value);
        break;

      case IrConstRef constRef:
        this._il.OpCode(ILOpCode.Ldsfld);
        this._il.Token(this._ctx.ConstantFields[constRef.Constant]);
        break;

      case IrLocalRef localRef:
        this.EmitLoadLocalOrParam(localRef.Local);
        break;

      case IrGlobalRef globalRef:
        this._il.OpCode(ILOpCode.Ldsfld);
        this._il.Token(this._ctx.GlobalFields[globalRef.Global]);
        break;

      case IrUnary unary:
        this.EmitUnary(unary);
        break;

      case IrBinary binary:
        this.EmitBinary(binary);
        break;

      case IrConvert convert:
        this.EmitConvert(convert);
        break;

      case IrConditional conditional: {
        var falseLabel = this._il.DefineLabel();
        var endLabel = this._il.DefineLabel();
        this.EmitExpr(conditional.Condition);
        this._il.Branch(ILOpCode.Brfalse, falseLabel);
        this.EmitExpr(conditional.WhenTrue);
        this._il.Branch(ILOpCode.Br, endLabel);
        this._il.MarkLabel(falseLabel);
        this.EmitExpr(conditional.WhenFalse);
        this._il.MarkLabel(endLabel);
        break;
      }

      case IrArrayLoad load:
        if (load.Array.Type.IsView) {
          this.EmitViewLoad(load);
        } else {
          this.EmitExpr(load.Array);
          this.EmitExpr(load.Index);
          this.EmitLoadElement(load.Type);
        }
        break;

      case IrArrayLength length:
        this.EmitExpr(length.Array);
        this._il.OpCode(ILOpCode.Ldlen);
        this._il.OpCode(ILOpCode.Conv_i4);
        break;

      case IrNewArray create:
        this.EmitExpr(create.Length);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(create.Type.Element!));
        break;

      case IrCall call:
        foreach (var arg in call.Arguments)
          this.EmitExpr(arg);
        if (this._ctx.ClassCtorByFn.TryGetValue(call.Function, out var ctorHandle)) {
          this._il.Call(ctorHandle);
        } else if (IrExceptionClass.ConstructorOf(call.Function) is { } libCtor) {
          var libCls = IrExceptionClass.All.First(c => c.Constructors.Contains(libCtor));
          this._il.Call(this._ctx.GetExceptionCtor(libCls, call.Arguments.Count - 1));
        } else {
          this._il.Call(this._ctx.FunctionHandles[call.Function]);
        }
        break;

      case IrForeignCall foreignCall:
        this.EmitForeignCall(foreignCall);
        break;

      case IrLastError lastError: {
        var methodName = lastError.PInvoke ? "GetLastPInvokeError" : "GetLastWin32Error";
        this._il.Call(this._ctx.GetMemberRef(
          $"stat:System.Runtime.InteropServices.Marshal::{methodName}()",
          this._ctx.GetAssemblyType("System.Runtime.InteropServices", "System.Runtime.InteropServices", "Marshal"),
          methodName,
          sig => sig.Parameters(0, r => r.Type().Int32(), _ => { })));
        break;
      }

      case IrStep step:
        this.EmitStep(step);
        break;

      case IrStructDefault structDefault:
        this.EmitDefaultValue(structDefault.Type);
        break;

      case IrStructNew structNew: {
        var slot = this.AllocTemp(structNew.Type);
        this._il.LoadLocalAddress(slot);
        this._il.OpCode(ILOpCode.Initobj);
        this._il.Token(this._ctx.GetTypeToken(structNew.Type));
        var fields = structNew.Type.StructType!.Fields;
        for (var i = 0; i < fields.Count; i++) {
          this._il.LoadLocalAddress(slot);
          this.EmitExpr(structNew.Arguments[i]);
          this._il.OpCode(ILOpCode.Stfld);
          this._il.Token(this._ctx.FieldHandles[fields[i]]);
        }
        this._il.LoadLocal(slot);
        break;
      }

      case IrFieldLoad fieldLoad:
        if (ReferenceEquals(fieldLoad.Field, IrExceptionClass.Message)) {
          this.EmitExpr(fieldLoad.Target);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "Exception", "get_Message", IrType.String));
        } else if (ReferenceEquals(fieldLoad.Field, IrExceptionClass.InnerException)) {
          this.EmitExpr(fieldLoad.Target);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "Exception", "get_InnerException", IrExceptionClass.Reference));
        } else {
          this.EmitExpr(fieldLoad.Target);
          this._il.OpCode(ILOpCode.Ldfld);
          this._il.Token(this._ctx.FieldHandles[fieldLoad.Field]);
        }
        break;

      case IrStructEquals structEquals:
        if (structEquals.Left.Type.ClassType is { IsRecord: true } recCls) {
          this.EmitExpr(structEquals.Left);
          this.EmitExpr(structEquals.Right);
          this._il.Call(this._ctx.RecordEqualsHandles[recCls]);
        } else {
          this.EmitStructEquals(structEquals);
        }
        break;

      case IrNew create: {
        foreach (var arg in create.Arguments)
          this.EmitExpr(arg);
        this._il.OpCode(ILOpCode.Newobj);
        var cls = create.Type.ClassType!;
        this._il.Token(cls.IsLibrary
          ? this._ctx.GetExceptionCtor(cls, create.Arguments.Count)
          : this._ctx.ClassCtorHandles[create.Constructor]);
        break;
      }

      case IrMethodCall methodCall: {
        if (methodCall.Target is not null)
          this.EmitExpr(methodCall.Target);
        foreach (var arg in methodCall.Arguments)
          this.EmitExpr(arg);
        var token = this._ctx.GetMethodToken(methodCall.Method);
        var opcode = methodCall.Method.IsStatic || methodCall.Kind == IrMethodCallKind.Base
          ? ILOpCode.Call
          : ILOpCode.Callvirt;
        this._il.OpCode(opcode);
        this._il.Token(token);
        break;
      }

      case IrReferenceEquals refEq:
        this.EmitExpr(refEq.Left);
        this.EmitExpr(refEq.Right);
        this._il.OpCode(ILOpCode.Ceq);
        break;

      case IrTypeTest typeTest:
        this.EmitExpr(typeTest.Value);
        this._il.OpCode(ILOpCode.Isinst);
        this._il.Token(this._ctx.GetTypeToken(typeTest.Target));
        this._il.OpCode(ILOpCode.Ldnull);
        this._il.OpCode(ILOpCode.Cgt_un);
        break;

      case IrCast cast:
        this.EmitExpr(cast.Value);
        this._il.OpCode(ILOpCode.Castclass);
        this._il.Token(this._ctx.GetTypeToken(cast.Type));
        break;

      case IrAsCast asCast:
        this.EmitExpr(asCast.Value);
        this._il.OpCode(ILOpCode.Isinst);
        this._il.Token(this._ctx.GetTypeToken(asCast.Type));
        break;

      case IrGenericEquals geq:
        this.EmitGenericEquals(geq.Left, geq.Right);
        break;

      case IrGenericCompare gcmp:
        this.EmitGenericCompare(gcmp.Left, gcmp.Right);
        break;

      case IrNone none:
        this.EmitDefaultValue(none.Type);
        break;

      case IrSome some:
        this.EmitExpr(some.Value);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetNullableCtor(some.Value.Type));
        break;

      case IrIsNone isNone: {
        var slot = this.AllocTemp(isNone.Value.Type);
        this.EmitExpr(isNone.Value);
        this._il.StoreLocal(slot);
        this._il.LoadLocalAddress(slot);
        this._il.Call(this._ctx.GetNullableHasValue(isNone.Value.Type.Underlying!));
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
      }

      case IrUnwrap unwrap: {
        var slot = this.AllocTemp(unwrap.Value.Type);
        this.EmitExpr(unwrap.Value);
        this._il.StoreLocal(slot);
        this._il.LoadLocalAddress(slot);
        this._il.Call(this._ctx.GetNullableValue(unwrap.Value.Type.Underlying!));
        break;
      }

      case IrCoalesce coalesce:
        this.EmitCoalesce(coalesce);
        break;

      case IrIsNull isNull:
        this.EmitExpr(isNull.Value);
        this._il.OpCode(ILOpCode.Ldnull);
        this._il.OpCode(ILOpCode.Ceq);
        break;

      case IrNull:
        this._il.OpCode(ILOpCode.Ldnull);
        break;

      case IrBind bind:
        this.EmitExpr(bind.Value);
        this._il.OpCode(ILOpCode.Dup);
        this.EmitStoreLocalOrParam(bind.Local);
        break;

      case IrThrowExpression throwExpr:
        this.EmitExpr(throwExpr.Exception);
        this._il.OpCode(ILOpCode.Throw);
        break;

      case IrLambda lambda:
        this.EmitLambda(lambda);
        break;

      case IrFunctionRef fnRef:
        this.EmitFunctionRef(fnRef);
        break;

      case IrBoundMethod bound:
        this.EmitBoundMethod(bound);
        break;

      case IrInvoke invoke:
        this.EmitInvoke(invoke);
        break;

      case IrCollection collection:
        this.EmitCollection(collection);
        break;

      case IrArraySequence arrSeq:
        this.EmitArraySequence(arrSeq);
        break;

      case IrAwait awaited:
        this.EmitAwait(awaited);
        break;

      case IrAsync asyncOp:
        this.EmitAsyncOp(asyncOp);
        break;

      case IrIntrinsic intrinsic:
        this.EmitIntrinsic(intrinsic);
        break;
    }
  }

  private void EmitGenericEquals(IrExpr left, IrExpr right) {
    var t = left.Type;
    if (t.ClassType is { IsRecord: true } recCls) {
      this.EmitExpr(left);
      this.EmitExpr(right);
      this._il.Call(this._ctx.RecordEqualsHandles[recCls]);
    } else if (t == IrType.F32) {
      var slot = this.AllocTemp(IrType.F32);
      this.EmitExpr(left);
      this._il.StoreLocal(slot);
      this._il.LoadLocalAddress(slot);
      this.EmitExpr(right);
      this._il.Call(this._ctx.GetInstanceMethodRef("System", "Single", "Equals", IrType.Bool, IrType.F32));
    } else if (t == IrType.F64) {
      var slot = this.AllocTemp(IrType.F64);
      this.EmitExpr(left);
      this._il.StoreLocal(slot);
      this._il.LoadLocalAddress(slot);
      this.EmitExpr(right);
      this._il.Call(this._ctx.GetInstanceMethodRef("System", "Double", "Equals", IrType.Bool, IrType.F64));
    } else if (t.IsString) {
      this.EmitExpr(left);
      this.EmitExpr(right);
      this._il.LoadConstantI4(4); // StringComparison.Ordinal
      this._il.Call(this.GetStringComparisonStaticRef("Equals", IrType.Bool));
    } else {
      this.EmitExpr(left);
      this.EmitExpr(right);
      this._il.OpCode(ILOpCode.Ceq);
    }
  }

  private void EmitGenericCompare(IrExpr left, IrExpr right) {
    var t = left.Type.Integral;
    if (t.IsString) {
      this.EmitExpr(left);
      this.EmitExpr(right);
      this._il.Call(this._ctx.GetStaticMethodRef("System", "String", "CompareOrdinal", IrType.I32, IrType.String, IrType.String));
      return;
    }

    var bclName = t.Kind switch {
      IrTypeKind.Bool => "Boolean",
      IrTypeKind.I8 => "SByte",
      IrTypeKind.U8 => "Byte",
      IrTypeKind.I16 => "Int16",
      IrTypeKind.U16 => "UInt16",
      IrTypeKind.I32 => "Int32",
      IrTypeKind.U32 => "UInt32",
      IrTypeKind.I64 => "Int64",
      IrTypeKind.U64 => "UInt64",
      IrTypeKind.F32 => "Single",
      _ => "Double"
    };
    var slot = this.AllocTemp(t);
    this.EmitExpr(left);
    this._il.StoreLocal(slot);
    this._il.LoadLocalAddress(slot);
    this.EmitExpr(right);
    this._il.Call(this._ctx.GetInstanceMethodRef("System", bclName, "CompareTo", IrType.I32, t));
  }

  private void EmitUnary(IrUnary unary) {
    switch (unary.Op) {
      case IrUnaryOp.Negate:
        this.EmitExpr(unary.Operand);
        this._il.OpCode(ILOpCode.Neg);
        break;
      case IrUnaryOp.NegateChecked:
        if (unary.Type.IsLong)
          this._il.LoadConstantI8(0);
        else
          this._il.LoadConstantI4(0);
        this.EmitExpr(unary.Operand);
        this._il.OpCode(ILOpCode.Sub_ovf);
        break;
      case IrUnaryOp.BitNot:
        this.EmitExpr(unary.Operand);
        this._il.OpCode(ILOpCode.Not);
        break;
      case IrUnaryOp.LogicalNot:
        this.EmitExpr(unary.Operand);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
    }
  }

  private void EmitBinary(IrBinary binary) {
    if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr) {
      var shortLabel = this._il.DefineLabel();
      var endLabel = this._il.DefineLabel();
      this.EmitExpr(binary.Left);
      this._il.Branch(binary.Op == IrBinaryOp.LogicalAnd ? ILOpCode.Brfalse : ILOpCode.Brtrue, shortLabel);
      this.EmitExpr(binary.Right);
      this._il.Branch(ILOpCode.Br, endLabel);
      this._il.MarkLabel(shortLabel);
      this._il.LoadConstantI4(binary.Op == IrBinaryOp.LogicalAnd ? 0 : 1);
      this._il.MarkLabel(endLabel);
      return;
    }

    this.EmitExpr(binary.Left);
    this.EmitExpr(binary.Right);

    var opType = binary.Left.Type;
    var unsigned = opType.IsInteger && !opType.IsSigned;
    var isFloat = opType.IsFloat;

    switch (binary.Op) {
      case IrBinaryOp.Add:
        this._il.OpCode(ILOpCode.Add);
        if (opType == IrType.F32)
          this._il.OpCode(ILOpCode.Conv_r4);
        break;
      case IrBinaryOp.Subtract:
        this._il.OpCode(ILOpCode.Sub);
        if (opType == IrType.F32)
          this._il.OpCode(ILOpCode.Conv_r4);
        break;
      case IrBinaryOp.Multiply:
        this._il.OpCode(ILOpCode.Mul);
        if (opType == IrType.F32)
          this._il.OpCode(ILOpCode.Conv_r4);
        break;
      case IrBinaryOp.AddChecked:
        this._il.OpCode(unsigned ? ILOpCode.Add_ovf_un : ILOpCode.Add_ovf);
        break;
      case IrBinaryOp.SubtractChecked:
        this._il.OpCode(unsigned ? ILOpCode.Sub_ovf_un : ILOpCode.Sub_ovf);
        break;
      case IrBinaryOp.MultiplyChecked:
        this._il.OpCode(unsigned ? ILOpCode.Mul_ovf_un : ILOpCode.Mul_ovf);
        break;
      case IrBinaryOp.Divide:
        this._il.OpCode(unsigned ? ILOpCode.Div_un : ILOpCode.Div);
        if (opType == IrType.F32)
          this._il.OpCode(ILOpCode.Conv_r4);
        break;
      case IrBinaryOp.Remainder:
        this._il.OpCode(unsigned ? ILOpCode.Rem_un : ILOpCode.Rem);
        if (opType == IrType.F32)
          this._il.OpCode(ILOpCode.Conv_r4);
        break;
      case IrBinaryOp.And:
        this._il.OpCode(ILOpCode.And);
        break;
      case IrBinaryOp.Or:
        this._il.OpCode(ILOpCode.Or);
        break;
      case IrBinaryOp.Xor:
        this._il.OpCode(ILOpCode.Xor);
        break;
      case IrBinaryOp.ShiftLeft:
        this._il.LoadConstantI4(opType.IsLong ? 63 : 31);
        this._il.OpCode(ILOpCode.And);
        this._il.OpCode(ILOpCode.Shl);
        break;
      case IrBinaryOp.ShiftRight:
        this._il.LoadConstantI4(opType.IsLong ? 63 : 31);
        this._il.OpCode(ILOpCode.And);
        this._il.OpCode(unsigned ? ILOpCode.Shr_un : ILOpCode.Shr);
        break;
      case IrBinaryOp.ShiftRightLogical:
        this._il.LoadConstantI4(opType.IsLong ? 63 : 31);
        this._il.OpCode(ILOpCode.And);
        this._il.OpCode(ILOpCode.Shr_un);
        break;
      case IrBinaryOp.Equal:
        this._il.OpCode(ILOpCode.Ceq);
        break;
      case IrBinaryOp.NotEqual:
        this._il.OpCode(ILOpCode.Ceq);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
      case IrBinaryOp.Less:
        this._il.OpCode(unsigned ? ILOpCode.Clt_un : ILOpCode.Clt);
        break;
      case IrBinaryOp.Greater:
        this._il.OpCode(unsigned ? ILOpCode.Cgt_un : ILOpCode.Cgt);
        break;
      case IrBinaryOp.LessOrEqual:
        this._il.OpCode(unsigned || isFloat ? ILOpCode.Cgt_un : ILOpCode.Cgt);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
      case IrBinaryOp.GreaterOrEqual:
        this._il.OpCode(unsigned || isFloat ? ILOpCode.Clt_un : ILOpCode.Clt);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
    }
  }

  private void EmitConvert(IrConvert convert) {
    var src = convert.Operand.Type.Integral;
    var dst = convert.Type.Integral;
    this.EmitExpr(convert.Operand);
    if (src == dst)
      return;

    if (dst.IsFloat) {
      if (src.IsInteger && !src.IsSigned)
        this._il.OpCode(ILOpCode.Conv_r_un);
      this._il.OpCode(dst == IrType.F32 ? ILOpCode.Conv_r4 : ILOpCode.Conv_r8);
      return;
    }

    if (src.IsFloat && convert.Contract == IrConversion.Saturate) {
      this.EmitFloatToIntSaturate(src, dst);
      return;
    }

    var srcUnsigned = src.IsInteger && !src.IsSigned;
    if (convert.Contract == IrConversion.Trap) {
      this._il.OpCode((dst.Kind, srcUnsigned) switch {
        (IrTypeKind.I8, true) => ILOpCode.Conv_ovf_i1_un,
        (IrTypeKind.I8, false) => ILOpCode.Conv_ovf_i1,
        (IrTypeKind.U8, true) => ILOpCode.Conv_ovf_u1_un,
        (IrTypeKind.U8, false) => ILOpCode.Conv_ovf_u1,
        (IrTypeKind.I16, true) => ILOpCode.Conv_ovf_i2_un,
        (IrTypeKind.I16, false) => ILOpCode.Conv_ovf_i2,
        (IrTypeKind.U16, true) => ILOpCode.Conv_ovf_u2_un,
        (IrTypeKind.U16, false) => ILOpCode.Conv_ovf_u2,
        (IrTypeKind.I32, true) => ILOpCode.Conv_ovf_i4_un,
        (IrTypeKind.I32, false) => ILOpCode.Conv_ovf_i4,
        (IrTypeKind.U32, true) => ILOpCode.Conv_ovf_u4_un,
        (IrTypeKind.U32, false) => ILOpCode.Conv_ovf_u4,
        (IrTypeKind.I64, true) => ILOpCode.Conv_ovf_i8_un,
        (IrTypeKind.I64, false) => ILOpCode.Conv_ovf_i8,
        (IrTypeKind.U64, true) => ILOpCode.Conv_ovf_u8_un,
        _ => ILOpCode.Conv_ovf_u8
      });
      return;
    }

    switch (dst.Kind) {
      case IrTypeKind.I8:
        this._il.OpCode(ILOpCode.Conv_i1);
        break;
      case IrTypeKind.U8:
        this._il.OpCode(ILOpCode.Conv_u1);
        break;
      case IrTypeKind.I16:
        this._il.OpCode(ILOpCode.Conv_i2);
        break;
      case IrTypeKind.U16:
        this._il.OpCode(ILOpCode.Conv_u2);
        break;
      case IrTypeKind.I32:
        this._il.OpCode(ILOpCode.Conv_i4);
        break;
      case IrTypeKind.U32:
        this._il.OpCode(ILOpCode.Conv_u4);
        break;
      case IrTypeKind.I64 or IrTypeKind.U64:
        if (!src.IsLong)
          this._il.OpCode(srcUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
        break;
    }
  }

  private void EmitFloatToIntSaturate(IrType src, IrType dst) {
    if (src == IrType.F32)
      this._il.OpCode(ILOpCode.Conv_r8);

    var valSlot = this.AllocTemp(IrType.F64);
    this._il.StoreLocal(valSlot);

    var nanLabel = this._il.DefineLabel();
    var minLabel = this._il.DefineLabel();
    var maxLabel = this._il.DefineLabel();
    var endLabel = this._il.DefineLabel();

    var minBound = (double)IrOperators.Minimum(dst);
    var maxExclusive = dst.Kind switch {
      IrTypeKind.I64 => 9223372036854775808.0,
      IrTypeKind.U64 => 18446744073709551616.0,
      _ => (double)IrOperators.Maximum(dst)
    };
    this._il.LoadLocal(valSlot);
    this._il.LoadLocal(valSlot);
    this._il.Branch(ILOpCode.Bne_un, nanLabel);

    this._il.LoadLocal(valSlot);
    this._il.LoadConstantR8(minBound);
    this._il.Branch(ILOpCode.Ble, minLabel);

    this._il.LoadLocal(valSlot);
    this._il.LoadConstantR8(maxExclusive);
    this._il.Branch(ILOpCode.Bge, maxLabel);

    this._il.LoadLocal(valSlot);
    this._il.OpCode(dst.Kind switch {
      IrTypeKind.I8 => ILOpCode.Conv_i1,
      IrTypeKind.U8 => ILOpCode.Conv_u1,
      IrTypeKind.I16 => ILOpCode.Conv_i2,
      IrTypeKind.U16 => ILOpCode.Conv_u2,
      IrTypeKind.I32 => ILOpCode.Conv_i4,
      IrTypeKind.U32 => ILOpCode.Conv_u4,
      IrTypeKind.I64 => ILOpCode.Conv_i8,
      _ => ILOpCode.Conv_u8
    });
    this._il.Branch(ILOpCode.Br, endLabel);

    this._il.MarkLabel(nanLabel);
    this.EmitConstValue(dst, 0);
    this._il.Branch(ILOpCode.Br, endLabel);

    this._il.MarkLabel(minLabel);
    this.EmitConstValue(dst, (long)IrOperators.Minimum(dst));
    this._il.Branch(ILOpCode.Br, endLabel);

    this._il.MarkLabel(maxLabel);
    this.EmitConstValue(dst, unchecked((long)(ulong)IrOperators.Maximum(dst)));

    this._il.MarkLabel(endLabel);
  }

  private void EmitStep(IrStep step) {
    var type = step.Type;
    var oldSlot = this.AllocTemp(type);
    var newSlot = this.AllocTemp(type);

    if (step.Target is IrLocalRef localRef) {
      this.EmitLoadLocalOrParam(localRef.Local);
      this._il.StoreLocal(oldSlot);
      this.EmitStepCompute(type, oldSlot, step.Increment, step.Trap);
      this._il.StoreLocal(newSlot);
      this._il.LoadLocal(newSlot);
      this.EmitStoreLocalOrParam(localRef.Local);
    } else {
      var arrayLoad = (IrArrayLoad)step.Target;
      var arrSlot = this.AllocTemp(arrayLoad.Array.Type);
      var idxSlot = this.AllocTemp(IrType.I32);
      this.EmitExpr(arrayLoad.Array);
      this._il.StoreLocal(arrSlot);
      this.EmitExpr(arrayLoad.Index);
      this._il.StoreLocal(idxSlot);

      if (arrayLoad.Array.Type.IsView) {
        var slice = this._ctx.SliceStructs[type];
        this.EmitViewIndexCheck(arrSlot, idxSlot, slice);
        this._il.LoadLocalAddress(arrSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(slice.ArrayField);
        this._il.LoadLocalAddress(arrSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(slice.OffsetField);
        this._il.LoadLocal(idxSlot);
        this._il.OpCode(ILOpCode.Add);
        this.EmitLoadElement(type);
        this._il.StoreLocal(oldSlot);

        this.EmitStepCompute(type, oldSlot, step.Increment, step.Trap);
        this._il.StoreLocal(newSlot);

        this._il.LoadLocalAddress(arrSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(slice.ArrayField);
        this._il.LoadLocalAddress(arrSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(slice.OffsetField);
        this._il.LoadLocal(idxSlot);
        this._il.OpCode(ILOpCode.Add);
        this._il.LoadLocal(newSlot);
        this.EmitStoreElement(type);
      } else {
        this._il.LoadLocal(arrSlot);
        this._il.LoadLocal(idxSlot);
        this.EmitLoadElement(type);
        this._il.StoreLocal(oldSlot);

        this.EmitStepCompute(type, oldSlot, step.Increment, step.Trap);
        this._il.StoreLocal(newSlot);

        this._il.LoadLocal(arrSlot);
        this._il.LoadLocal(idxSlot);
        this._il.LoadLocal(newSlot);
        this.EmitStoreElement(type);
      }
    }

    this._il.LoadLocal(step.Postfix ? oldSlot : newSlot);
  }

  private void EmitStepCompute(IrType type, int oldSlot, bool increment, bool trap) {
    this._il.LoadLocal(oldSlot);
    if (type == IrType.F32) {
      this._il.LoadConstantR4(1.0f);
      this._il.OpCode(increment ? ILOpCode.Add : ILOpCode.Sub);
      this._il.OpCode(ILOpCode.Conv_r4);
      return;
    }

    if (type == IrType.F64) {
      this._il.LoadConstantR8(1.0);
      this._il.OpCode(increment ? ILOpCode.Add : ILOpCode.Sub);
      return;
    }

    var unsigned = !type.IsSigned;
    if (type.IsLong)
      this._il.LoadConstantI8(1);
    else
      this._il.LoadConstantI4(1);

    if (trap) {
      this._il.OpCode((increment, unsigned) switch {
        (true, true) => ILOpCode.Add_ovf_un,
        (true, false) => ILOpCode.Add_ovf,
        (false, true) => ILOpCode.Sub_ovf_un,
        (false, false) => ILOpCode.Sub_ovf
      });
    } else {
      this._il.OpCode(increment ? ILOpCode.Add : ILOpCode.Sub);
    }

    if (type.Bits < 32) {
      this._il.OpCode((type.Kind, trap) switch {
        (IrTypeKind.I8, true) => ILOpCode.Conv_ovf_i1,
        (IrTypeKind.I8, false) => ILOpCode.Conv_i1,
        (IrTypeKind.U8, true) => ILOpCode.Conv_ovf_u1_un,
        (IrTypeKind.U8, false) => ILOpCode.Conv_u1,
        (IrTypeKind.I16, true) => ILOpCode.Conv_ovf_i2,
        (IrTypeKind.I16, false) => ILOpCode.Conv_i2,
        (IrTypeKind.U16, true) => ILOpCode.Conv_ovf_u2_un,
        _ => ILOpCode.Conv_u2
      });
    }
  }

  private void EmitStructEquals(IrStructEquals equals) {
    var structType = equals.Left.Type.StructType!;
    var leftSlot = this.AllocTemp(equals.Left.Type);
    var rightSlot = this.AllocTemp(equals.Right.Type);
    this.EmitExpr(equals.Left);
    this._il.StoreLocal(leftSlot);
    this.EmitExpr(equals.Right);
    this._il.StoreLocal(rightSlot);

    var falseLabel = this._il.DefineLabel();
    var endLabel = this._il.DefineLabel();
    this.EmitStructFieldsEqual(structType, () => this._il.LoadLocalAddress(leftSlot), () => this._il.LoadLocalAddress(rightSlot), falseLabel);
    this._il.LoadConstantI4(1);
    this._il.Branch(ILOpCode.Br, endLabel);
    this._il.MarkLabel(falseLabel);
    this._il.LoadConstantI4(0);
    this._il.MarkLabel(endLabel);
  }

  private void EmitStructFieldsEqual(IrStructType structType, Action loadLeftAddr, Action loadRightAddr, LabelHandle falseLabel) {
    foreach (var field in structType.Fields) {
      var token = this._ctx.FieldHandles[field];
      if (field.Type.IsStruct) {
        this.EmitStructFieldsEqual(
          field.Type.StructType!,
          () => { loadLeftAddr(); this._il.OpCode(ILOpCode.Ldflda); this._il.Token(token); },
          () => { loadRightAddr(); this._il.OpCode(ILOpCode.Ldflda); this._il.Token(token); },
          falseLabel);
      } else if (field.Type == IrType.F32) {
        loadLeftAddr();
        this._il.OpCode(ILOpCode.Ldflda);
        this._il.Token(token);
        loadRightAddr();
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(token);
        this._il.Call(this._ctx.GetInstanceMethodRef("System", "Single", "Equals", IrType.Bool, IrType.F32));
        this._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else if (field.Type == IrType.F64) {
        loadLeftAddr();
        this._il.OpCode(ILOpCode.Ldflda);
        this._il.Token(token);
        loadRightAddr();
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(token);
        this._il.Call(this._ctx.GetInstanceMethodRef("System", "Double", "Equals", IrType.Bool, IrType.F64));
        this._il.Branch(ILOpCode.Brfalse, falseLabel);
      } else {
        loadLeftAddr();
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(token);
        loadRightAddr();
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(token);
        this._il.Branch(ILOpCode.Bne_un, falseLabel);
      }
    }
  }

  private void EmitCoalesce(IrCoalesce coalesce) {
    var endLabel = this._il.DefineLabel();
    var hasValLabel = this._il.DefineLabel();
    var slot = this.AllocTemp(coalesce.Value.Type);
    this.EmitExpr(coalesce.Value);
    this._il.StoreLocal(slot);
    this._il.LoadLocalAddress(slot);
    this._il.Call(this._ctx.GetNullableHasValue(coalesce.Value.Type.Underlying!));
    this._il.Branch(ILOpCode.Brtrue, hasValLabel);
    this.EmitExpr(coalesce.Fallback);
    this._il.Branch(ILOpCode.Br, endLabel);
    this._il.MarkLabel(hasValLabel);
    if (coalesce.Type.IsOptional) {
      this._il.LoadLocal(slot);
    } else {
      this._il.LoadLocalAddress(slot);
      this._il.Call(this._ctx.GetNullableValue(coalesce.Value.Type.Underlying!));
    }
    this._il.MarkLabel(endLabel);
  }

  private void EmitIntrinsic(IrIntrinsic intrinsic) {
    if (this.TryEmitViewOrPointerIntrinsic(intrinsic))
      return;

    var args = intrinsic.Arguments;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.ArrayOf: {
        var elemType = intrinsic.Type.Element!;
        this._il.LoadConstantI4(args.Count);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(elemType));
        for (var i = 0; i < args.Count; i++) {
          this._il.OpCode(ILOpCode.Dup);
          this._il.LoadConstantI4(i);
          this.EmitExpr(args[i]);
          this.EmitStoreElement(elemType);
        }
        break;
      }

      case IrIntrinsicOp.ArrayCopy:
        this.EmitArrayCopy(args);
        break;

      case IrIntrinsicOp.ArrayClear: {
        var arrSlot = this.AllocTemp(args[0].Type);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(arrSlot);
        this._il.LoadLocal(arrSlot);
        this._il.LoadConstantI4(0);
        this._il.LoadLocal(arrSlot);
        this._il.OpCode(ILOpCode.Ldlen);
        this._il.OpCode(ILOpCode.Conv_i4);
        this._il.Call(this._ctx.GetMemberRef(
          "stat:System.Array::Clear(Array,int,int)",
          this._ctx.GetBclType("System", "Array"),
          "Clear",
          sig => sig.Parameters(3, r => r.Void(), p => {
            p.AddParameter().Type().Type(this._ctx.GetBclType("System", "Array"), isValueType: false);
            p.AddParameter().Type().Int32();
            p.AddParameter().Type().Int32();
          })));
        break;
      }

      case IrIntrinsicOp.ArrayFill: {
        var elemType = args[0].Type.Element!;
        var arrSlot = this.AllocTemp(args[0].Type);
        var valSlot = this.AllocTemp(elemType);
        var idxSlot = this.AllocTemp(IrType.I32);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(arrSlot);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(valSlot);
        this._il.LoadConstantI4(0);
        this._il.StoreLocal(idxSlot);

        var condLabel = this._il.DefineLabel();
        var doneLabel = this._il.DefineLabel();
        this._il.MarkLabel(condLabel);
        this._il.LoadLocal(idxSlot);
        this._il.LoadLocal(arrSlot);
        this._il.OpCode(ILOpCode.Ldlen);
        this._il.OpCode(ILOpCode.Conv_i4);
        this._il.Branch(ILOpCode.Bge, doneLabel);

        this._il.LoadLocal(arrSlot);
        this._il.LoadLocal(idxSlot);
        this._il.LoadLocal(valSlot);
        this.EmitStoreElement(elemType);

        this._il.LoadLocal(idxSlot);
        this._il.LoadConstantI4(1);
        this._il.OpCode(ILOpCode.Add);
        this._il.StoreLocal(idxSlot);
        this._il.Branch(ILOpCode.Br, condLabel);
        this._il.MarkLabel(doneLabel);
        break;
      }

      case IrIntrinsicOp.Abs: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        if (t.IsInteger && !t.IsSigned)
          break;
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "Abs", t, t));
        break;
      }

      case IrIntrinsicOp.Min or IrIntrinsicOp.Max: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, intrinsic.Op.ToString(), t, t, t));
        break;
      }

      case IrIntrinsicOp.Sign: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        if (t.IsInteger && !t.IsSigned) {
          if (t.IsLong)
            this._il.LoadConstantI8(0);
          else
            this._il.LoadConstantI4(0);
          this._il.OpCode(ILOpCode.Cgt_un);
          break;
        }
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "Sign", IrType.I32, t));
        break;
      }

      case IrIntrinsicOp.Sqrt or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, intrinsic.Op.ToString(), t, t));
        break;
      }

      case IrIntrinsicOp.RoundEven: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "Round", t, t));
        break;
      }

      case IrIntrinsicOp.RoundAway: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        this._il.LoadConstantI4(1); // MidpointRounding.AwayFromZero
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetMemberRef(
          $"stat:System.{mathType}::Round({t},MidpointRounding)",
          this._ctx.GetBclType("System", mathType),
          "Round",
          sig => sig.Parameters(2, r => this._ctx.EncodeType(r.Type(), t), p => {
            this._ctx.EncodeType(p.AddParameter().Type(), t);
            p.AddParameter().Type().Type(this._ctx.GetBclType("System", "MidpointRounding"), isValueType: true);
          })));
        break;
      }

      case IrIntrinsicOp.CopySign: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "CopySign", t, t, t));
        break;
      }

      case IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity or IrIntrinsicOp.IsFinite
        or IrIntrinsicOp.IsNormal or IrIntrinsicOp.IsSubnormal or IrIntrinsicOp.IsNegative: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var floatType = t == IrType.F32 ? "Single" : "Double";
        this._il.Call(this._ctx.GetStaticMethodRef("System", floatType, intrinsic.Op.ToString(), IrType.Bool, t));
        break;
      }

      case IrIntrinsicOp.FloatBits: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var method = t == IrType.F32 ? "SingleToInt32Bits" : "DoubleToInt64Bits";
        this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", method, intrinsic.Type, t));
        break;
      }

      case IrIntrinsicOp.BitsFloat: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var method = t == IrType.I32 ? "Int32BitsToSingle" : "Int64BitsToDouble";
        this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", method, intrinsic.Type, t));
        break;
      }

      case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight: {
        var isLong = args[0].Type.IsLong;
        var bitArgType = isLong ? IrType.U64 : IrType.U32;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.Call(this._ctx.GetStaticMethodRef("System.Numerics", "BitOperations", intrinsic.Op.ToString(), bitArgType, bitArgType, IrType.I32));
        break;
      }

      case IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount: {
        var isLong = args[0].Type.IsLong;
        var bitArgType = isLong ? IrType.U64 : IrType.U32;
        this.EmitExpr(args[0]);
        this._il.Call(this._ctx.GetStaticMethodRef("System.Numerics", "BitOperations", intrinsic.Op.ToString(), IrType.I32, bitArgType));
        break;
      }

      case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig:
        if (args[0].Type.IsView)
          this.EmitEndianLoadOnView(intrinsic.Type, args[0], args[1], intrinsic.Op == IrIntrinsicOp.LoadLittle);
        else
          this.EmitEndianLoad(intrinsic.Type, args[0], args[1], intrinsic.Op == IrIntrinsicOp.LoadLittle);
        break;

      case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig:
        if (args[0].Type.IsView)
          this.EmitEndianStoreOnView(args[0], args[1], args[2], intrinsic.Op == IrIntrinsicOp.StoreLittle);
        else
          this.EmitEndianStore(args[0], args[1], args[2], intrinsic.Op == IrIntrinsicOp.StoreLittle);
        break;

      case IrIntrinsicOp.StrLength:
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32));
        break;

      case IrIntrinsicOp.StrCodeUnitAt:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef(
          "inst:System.String::get_Chars(int)",
          this._ctx.GetBclType("System", "String"),
          "get_Chars",
          sig => sig.Parameters(1, r => r.Type().Char(), p => p.AddParameter().Type().Int32())));
        break;

      case IrIntrinsicOp.StrConcat:
        this.EmitExpr(args[0]);
        for (var i = 1; i < args.Count; i++) {
          this.EmitExpr(args[i]);
          this._il.Call(this._ctx.GetStaticMethodRef("System", "String", "Concat", IrType.String, IrType.String, IrType.String));
        }
        break;

      case IrIntrinsicOp.StrEquals:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.LoadConstantI4(4); // StringComparison.Ordinal
        this._il.Call(this.GetStringComparisonStaticRef("Equals", IrType.Bool));
        break;

      case IrIntrinsicOp.StrCompareOrdinal:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.Call(this._ctx.GetStaticMethodRef("System", "String", "CompareOrdinal", IrType.I32, IrType.String, IrType.String));
        this._il.Call(this._ctx.GetStaticMethodRef("System", "Math", "Sign", IrType.I32, IrType.I32));
        break;

      case IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith or IrIntrinsicOp.StrContains: {
        var name = intrinsic.Op switch {
          IrIntrinsicOp.StrStartsWith => "StartsWith",
          IrIntrinsicOp.StrEndsWith => "EndsWith",
          _ => "Contains"
        };
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        if (args[1].Type == IrType.U16) {
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this.GetStringCharInstanceRef(name, IrType.Bool, withStartIndex: false));
        } else {
          this._il.LoadConstantI4(4); // StringComparison.Ordinal
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this.GetStringOrdinalInstanceRef(name, IrType.Bool, withStartIndex: false));
        }
        break;
      }

      case IrIntrinsicOp.StrIndexOf: {
        var withStart = args.Count == 3;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        if (withStart)
          this.EmitExpr(args[2]);
        if (args[1].Type == IrType.U16) {
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this.GetStringCharInstanceRef("IndexOf", IrType.I32, withStart));
        } else {
          this._il.LoadConstantI4(4); // StringComparison.Ordinal
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this.GetStringOrdinalInstanceRef("IndexOf", IrType.I32, withStart));
        }
        break;
      }

      case IrIntrinsicOp.StrSubstring:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        if (args.Count == 3) {
          this.EmitExpr(args[2]);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32, IrType.I32));
        } else {
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32));
        }
        break;

      case IrIntrinsicOp.StrJoin:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.Call(this._ctx.GetStaticMethodRef("System", "String", "Join", IrType.String, IrType.String, IrType.Array(IrType.String)));
        break;

      case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma:
        this.EmitFormatDotNet(args[0]);
        break;
    }
  }

  private void EmitForeignCall(IrForeignCall call) {
    var foreign = this._ctx.ForeignByName[call.Function];
    for (var i = 0; i < call.Arguments.Count; i++) {
      var arg = call.Arguments[i];
      var marshal = foreign.Parameters[i].Marshal;
      this.EmitExpr(arg);
      if (marshal.Kind == IrMarshalKind.ByRef) {
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ldelema);
        this._il.Token(this._ctx.GetTypeToken(arg.Type.Element!));
      } else if (marshal.Kind == IrMarshalKind.Handle || marshal.NativeSize) {
        this._il.OpCode(arg.Type.IsSigned ? ILOpCode.Conv_i : ILOpCode.Conv_u);
      }
    }

    this._il.Call(this._ctx.ForeignHandles[call.Function]);
    if (foreign.ReturnType != IrType.Void && (foreign.Result.Kind == IrMarshalKind.Handle || foreign.Result.NativeSize))
      this._il.OpCode(foreign.ReturnType.IsSigned ? ILOpCode.Conv_i8 : ILOpCode.Conv_u8);
  }

  private void EmitFormatDotNet(IrExpr value) {
    var type = value.Type.Integral;
    var slot = this.AllocTemp(type);
    this.EmitExpr(value);
    this._il.StoreLocal(slot);
    this._il.LoadLocalAddress(slot);
    this._il.Call(this._ctx.GetMemberRef(
      "stat:System.Globalization.CultureInfo::get_InvariantCulture()",
      this._ctx.GetBclType("System.Globalization", "CultureInfo"),
      "get_InvariantCulture",
      sig => sig.Parameters(0, r => r.Type().Type(this._ctx.GetBclType("System.Globalization", "CultureInfo"), isValueType: false), _ => { })));
    var bclName = type.Kind switch {
      IrTypeKind.I8 => "SByte",
      IrTypeKind.U8 => "Byte",
      IrTypeKind.I16 => "Int16",
      IrTypeKind.U16 => "UInt16",
      IrTypeKind.I32 => "Int32",
      IrTypeKind.U32 => "UInt32",
      IrTypeKind.I64 => "Int64",
      IrTypeKind.U64 => "UInt64",
      IrTypeKind.F32 => "Single",
      _ => "Double"
    };
    this._il.Call(this._ctx.GetMemberRef(
      $"inst:System.{bclName}::ToString(IFormatProvider)",
      this._ctx.GetBclType("System", bclName),
      "ToString",
      sig => sig.Parameters(1, r => r.Type().String(), p =>
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "IFormatProvider"), isValueType: false))));
  }

  private void EmitArrayCopy(IReadOnlyList<IrExpr> args) {
    var srcSlot = this.AllocTemp(args[0].Type);
    var srcIdxSlot = this.AllocTemp(IrType.I32);
    var dstSlot = this.AllocTemp(args[2].Type);
    var dstIdxSlot = this.AllocTemp(IrType.I32);
    var lenSlot = this.AllocTemp(IrType.I32);

    this.EmitExpr(args[0]);
    this._il.StoreLocal(srcSlot);
    this.EmitExpr(args[1]);
    this._il.StoreLocal(srcIdxSlot);
    this.EmitExpr(args[2]);
    this._il.StoreLocal(dstSlot);
    this.EmitExpr(args[3]);
    this._il.StoreLocal(dstIdxSlot);
    this.EmitExpr(args[4]);
    this._il.StoreLocal(lenSlot);

    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();

    this._il.LoadLocal(srcIdxSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(dstIdxSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(lenSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);

    this._il.LoadLocal(srcIdxSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.LoadLocal(lenSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadLocal(srcSlot);
    this._il.OpCode(ILOpCode.Ldlen);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.Branch(ILOpCode.Bgt, failLabel);

    this._il.LoadLocal(dstIdxSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.LoadLocal(lenSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadLocal(dstSlot);
    this._il.OpCode(ILOpCode.Ldlen);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.Branch(ILOpCode.Ble, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);

    this._il.MarkLabel(okLabel);
    this._il.LoadLocal(srcSlot);
    this._il.LoadLocal(srcIdxSlot);
    this._il.LoadLocal(dstSlot);
    this._il.LoadLocal(dstIdxSlot);
    this._il.LoadLocal(lenSlot);
    this._il.Call(this.GetArrayCopyRef());
  }

  private void EmitEndianLoad(IrType resultType, IrExpr arrayExpr, IrExpr indexExpr, bool littleEndian) {
    var size = resultType.Size;
    var arrSlot = this.AllocTemp(arrayExpr.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(arrayExpr);
    this._il.StoreLocal(arrSlot);
    this.EmitExpr(indexExpr);
    this._il.StoreLocal(idxSlot);
    this.EmitEndianBoundsCheck(arrSlot, idxSlot, size);
    this.EmitEndianLoadFromSlots(resultType, arrSlot, idxSlot, littleEndian);
  }

  private void EmitEndianLoadFromSlots(IrType resultType, int arrSlot, int idxSlot, bool littleEndian) {
    var size = resultType.Size;
    var is64 = size == 8;
    for (var i = 0; i < size; i++) {
      var byteOffset = littleEndian ? i : size - 1 - i;
      this._il.LoadLocal(arrSlot);
      this._il.LoadLocal(idxSlot);
      if (byteOffset > 0) {
        this._il.LoadConstantI4(byteOffset);
        this._il.OpCode(ILOpCode.Add);
      }
      this._il.OpCode(ILOpCode.Ldelem_u1);
      if (is64)
        this._il.OpCode(ILOpCode.Conv_u8);
      if (i > 0) {
        this._il.LoadConstantI4(i * 8);
        this._il.OpCode(ILOpCode.Shl);
        this._il.OpCode(ILOpCode.Or);
      }
    }

    if (resultType == IrType.I16)
      this._il.OpCode(ILOpCode.Conv_i2);
    else if (resultType == IrType.F32)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "Int32BitsToSingle", IrType.F32, IrType.I32));
    else if (resultType == IrType.F64)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "Int64BitsToDouble", IrType.F64, IrType.I64));
  }

  private void EmitEndianStore(IrExpr arrayExpr, IrExpr indexExpr, IrExpr valueExpr, bool littleEndian) {
    var valueType = valueExpr.Type;
    var size = valueType.Size;
    var is64 = size == 8;
    var arrSlot = this.AllocTemp(arrayExpr.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    var bitsType = is64 ? IrType.U64 : IrType.U32;
    var bitsSlot = this.AllocTemp(bitsType);

    this.EmitExpr(arrayExpr);
    this._il.StoreLocal(arrSlot);
    this.EmitExpr(indexExpr);
    this._il.StoreLocal(idxSlot);
    this.EmitExpr(valueExpr);
    if (valueType == IrType.F32)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "SingleToInt32Bits", IrType.I32, IrType.F32));
    else if (valueType == IrType.F64)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "DoubleToInt64Bits", IrType.I64, IrType.F64));
    this._il.StoreLocal(bitsSlot);

    this.EmitEndianBoundsCheck(arrSlot, idxSlot, size);
    this.EmitEndianStoreFromSlots(arrSlot, idxSlot, bitsSlot, size, littleEndian);
  }

  private void EmitEndianStoreFromSlots(int arrSlot, int idxSlot, int bitsSlot, int size, bool littleEndian) {
    for (var i = 0; i < size; i++) {
      var byteOffset = littleEndian ? i : size - 1 - i;
      this._il.LoadLocal(arrSlot);
      this._il.LoadLocal(idxSlot);
      if (byteOffset > 0) {
        this._il.LoadConstantI4(byteOffset);
        this._il.OpCode(ILOpCode.Add);
      }
      this._il.LoadLocal(bitsSlot);
      if (i > 0) {
        this._il.LoadConstantI4(i * 8);
        this._il.OpCode(ILOpCode.Shr_un);
      }
      this._il.OpCode(ILOpCode.Conv_u1);
      this._il.OpCode(ILOpCode.Stelem_i1);
    }
  }

  private void EmitEndianBoundsCheck(int arrSlot, int idxSlot, int size) {
    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();
    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocal(arrSlot);
    this._il.OpCode(ILOpCode.Ldlen);
    this._il.OpCode(ILOpCode.Conv_i4);
    this._il.LoadConstantI4(size);
    this._il.OpCode(ILOpCode.Sub);
    this._il.Branch(ILOpCode.Ble, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(okLabel);
  }

  private MemberReferenceHandle GetStringComparisonStaticRef(string methodName, IrType returnType)
    => this._ctx.GetMemberRef(
      $"stat:System.String::{methodName}(str,str,StringComparison)",
      this._ctx.GetBclType("System", "String"),
      methodName,
      sig => sig.Parameters(3, r => this._ctx.EncodeType(r.Type(), returnType), p => {
        p.AddParameter().Type().String();
        p.AddParameter().Type().String();
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "StringComparison"), isValueType: true);
      }));

  private MemberReferenceHandle GetStringOrdinalInstanceRef(string methodName, IrType returnType, bool withStartIndex)
    => this._ctx.GetMemberRef(
      $"inst:System.String::{methodName}(str,{(withStartIndex ? "int," : "")}StringComparison)",
      this._ctx.GetBclType("System", "String"),
      methodName,
      sig => sig.Parameters(withStartIndex ? 3 : 2, r => this._ctx.EncodeType(r.Type(), returnType), p => {
        p.AddParameter().Type().String();
        if (withStartIndex)
          p.AddParameter().Type().Int32();
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "StringComparison"), isValueType: true);
      }));

  private MemberReferenceHandle GetStringCharInstanceRef(string methodName, IrType returnType, bool withStartIndex)
    => this._ctx.GetMemberRef(
      $"inst:System.String::{methodName}(char{(withStartIndex ? ",int" : "")})",
      this._ctx.GetBclType("System", "String"),
      methodName,
      sig => sig.Parameters(withStartIndex ? 2 : 1, r => this._ctx.EncodeType(r.Type(), returnType), p => {
        p.AddParameter().Type().Char();
        if (withStartIndex)
          p.AddParameter().Type().Int32();
      }));

  private void EmitAddress(IrExpr place) {
    switch (place) {
      case IrLocalRef localRef:
        if (this._outerCaptureFields.ContainsKey(localRef.Local) || this._capturedLocalSlots.ContainsKey(localRef.Local)) {
          this.EmitLoadCaptureCell(localRef.Local);
          this._il.OpCode(ILOpCode.Ldflda);
          this._il.Token(this._ctx.CellTypes[localRef.Local.Type].ValueField);
        } else if (this._addressedLocalSlots.TryGetValue(localRef.Local, out var addrSlot)) {
          this._il.LoadLocal(addrSlot);
          this._il.LoadConstantI4(0);
          this._il.OpCode(ILOpCode.Ldelema);
          this._il.Token(this._ctx.GetTypeToken(localRef.Local.Type));
        } else if (this._paramSlots.TryGetValue(localRef.Local, out var paramIndex)) {
          this._il.LoadArgumentAddress(paramIndex);
        } else {
          this._il.LoadLocalAddress(this._localSlots[localRef.Local]);
        }
        break;
      case IrArrayLoad arrayLoad:
        this.EmitExpr(arrayLoad.Array);
        this.EmitExpr(arrayLoad.Index);
        this._il.OpCode(ILOpCode.Ldelema);
        this._il.Token(this._ctx.GetTypeToken(arrayLoad.Type));
        break;
      case IrFieldLoad fieldLoad:
        if (fieldLoad.Target.Type.IsObject)
          this.EmitExpr(fieldLoad.Target);
        else
          this.EmitAddress(fieldLoad.Target);
        this._il.OpCode(ILOpCode.Ldflda);
        this._il.Token(this._ctx.FieldHandles[fieldLoad.Field]);
        break;
    }
  }

  private void EmitLoadLocalOrParam(IrLocal local) {
    if (this._outerCaptureFields.ContainsKey(local) || this._capturedLocalSlots.ContainsKey(local)) {
      this.EmitLoadCaptureCell(local);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(this._ctx.CellTypes[local.Type].ValueField);
    } else if (this._addressedLocalSlots.TryGetValue(local, out var addrSlot)) {
      this._il.LoadLocal(addrSlot);
      this._il.LoadConstantI4(0);
      this.EmitLoadElement(local.Type);
    } else if (this._paramSlots.TryGetValue(local, out var paramIndex)) {
      this._il.LoadArgument(paramIndex);
    } else {
      this._il.LoadLocal(this._localSlots[local]);
    }
  }

  private void EmitStoreLocalOrParam(IrLocal local) {
    if (this._outerCaptureFields.ContainsKey(local) || this._capturedLocalSlots.ContainsKey(local)) {
      var valSlot = this.AllocTemp(local.Type);
      this._il.StoreLocal(valSlot);
      this.EmitLoadCaptureCell(local);
      this._il.LoadLocal(valSlot);
      this._il.OpCode(ILOpCode.Stfld);
      this._il.Token(this._ctx.CellTypes[local.Type].ValueField);
    } else if (this._addressedLocalSlots.TryGetValue(local, out var addrSlot)) {
      var valSlot = this.AllocTemp(local.Type);
      this._il.StoreLocal(valSlot);
      this._il.LoadLocal(addrSlot);
      this._il.LoadConstantI4(0);
      this._il.LoadLocal(valSlot);
      this.EmitStoreElement(local.Type);
    } else if (this._paramSlots.TryGetValue(local, out var paramIndex)) {
      this._il.StoreArgument(paramIndex);
    } else {
      this._il.StoreLocal(this._localSlots[local]);
    }
  }

  private void EmitConstValue(IrType type, long value) {
    switch (type.Kind) {
      case IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32:
        this._il.LoadConstantI4(unchecked((int)value));
        break;
      case IrTypeKind.I64 or IrTypeKind.U64:
        this._il.LoadConstantI8(value);
        break;
      case IrTypeKind.F32:
        this._il.LoadConstantR4(BitConverter.Int32BitsToSingle(unchecked((int)value)));
        break;
      case IrTypeKind.F64:
        this._il.LoadConstantR8(BitConverter.Int64BitsToDouble(value));
        break;
    }
  }

  private static bool IsClrValueType(IrType type)
    => type.IsStruct || type.IsOptional || type.IsView || type.IsMemory || type.IsPointer;

  private static bool IsClrReferenceType(IrType type)
    => type.IsString || type.IsArray || type.IsObject || type.IsFunction || type.IsCollection || type.IsLazySequence || type.IsAsync;

  private void EmitDefaultValue(IrType type) {
    if (IsClrValueType(type)) {
      var slot = this.AllocTemp(type);
      this._il.LoadLocalAddress(slot);
      this._il.OpCode(ILOpCode.Initobj);
      this._il.Token(this._ctx.GetTypeToken(type));
      this._il.LoadLocal(slot);
    } else if (IsClrReferenceType(type)) {
      this._il.OpCode(ILOpCode.Ldnull);
    } else {
      this.EmitConstValue(type, 0);
    }
  }

  private void EmitStringLiteral(string value) {
    var handle = this._ctx.Metadata.GetOrAddUserString(value);
    this._il.LoadString(handle);
  }

  private void EmitLoadElement(IrType elementType) {
    if (IsClrValueType(elementType)) {
      this._il.OpCode(ILOpCode.Ldelem);
      this._il.Token(this._ctx.GetTypeToken(elementType));
      return;
    }

    switch (elementType.Kind) {
      case IrTypeKind.Bool or IrTypeKind.U8:
        this._il.OpCode(ILOpCode.Ldelem_u1);
        break;
      case IrTypeKind.I8:
        this._il.OpCode(ILOpCode.Ldelem_i1);
        break;
      case IrTypeKind.I16:
        this._il.OpCode(ILOpCode.Ldelem_i2);
        break;
      case IrTypeKind.U16:
        this._il.OpCode(ILOpCode.Ldelem_u2);
        break;
      case IrTypeKind.I32:
        this._il.OpCode(ILOpCode.Ldelem_i4);
        break;
      case IrTypeKind.U32:
        this._il.OpCode(ILOpCode.Ldelem_u4);
        break;
      case IrTypeKind.I64 or IrTypeKind.U64:
        this._il.OpCode(ILOpCode.Ldelem_i8);
        break;
      case IrTypeKind.F32:
        this._il.OpCode(ILOpCode.Ldelem_r4);
        break;
      case IrTypeKind.F64:
        this._il.OpCode(ILOpCode.Ldelem_r8);
        break;
      default:
        this._il.OpCode(ILOpCode.Ldelem_ref);
        break;
    }
  }

  private void EmitStoreElement(IrType elementType) {
    if (IsClrValueType(elementType)) {
      this._il.OpCode(ILOpCode.Stelem);
      this._il.Token(this._ctx.GetTypeToken(elementType));
      return;
    }

    switch (elementType.Kind) {
      case IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8:
        this._il.OpCode(ILOpCode.Stelem_i1);
        break;
      case IrTypeKind.I16 or IrTypeKind.U16:
        this._il.OpCode(ILOpCode.Stelem_i2);
        break;
      case IrTypeKind.I32 or IrTypeKind.U32:
        this._il.OpCode(ILOpCode.Stelem_i4);
        break;
      case IrTypeKind.I64 or IrTypeKind.U64:
        this._il.OpCode(ILOpCode.Stelem_i8);
        break;
      case IrTypeKind.F32:
        this._il.OpCode(ILOpCode.Stelem_r4);
        break;
      case IrTypeKind.F64:
        this._il.OpCode(ILOpCode.Stelem_r8);
        break;
      default:
        this._il.OpCode(ILOpCode.Stelem_ref);
        break;
    }
  }

}
