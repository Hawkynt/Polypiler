using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

/// <summary>Identifies a distinct bound-method adapter class to synthesize.</summary>
internal readonly record struct BoundMethodKey(IrMethod Method, IrMethodCallKind Kind, IrFunctionType Signature);

/// <summary>
/// Pre-scans an <see cref="IrModule"/> to discover every class/interface member function and every
/// synthesized helper type (closure cells, function interfaces, lambda/adapter classes, iterator
/// sequences, slice structs, pointer struct, and bit-permutation helpers) needed during assembly emission.
/// </summary>
internal sealed class MsilSynthesisPlan {

  public HashSet<string> MemberFunctions { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, (IrClassType Owner, IrConstructor Ctor)> ClassConstructorsByFn { get; } = new(StringComparer.Ordinal);

  public Dictionary<IrMethod, IrClassType> ClassMethodOwners { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrMethod, IrInterfaceType> InterfaceMethodOwners { get; } = new(ReferenceEqualityComparer.Instance);

  public HashSet<IrLocal> CapturedLocals { get; } = new(ReferenceEqualityComparer.Instance);

  public HashSet<IrLocal> AddressedLocals { get; } = new(ReferenceEqualityComparer.Instance);

  public List<IrType> CellTypes { get; } = [];

  public List<IrFunctionType> FunctionTypes { get; } = [];

  public List<IrLambda> Lambdas { get; } = [];

  public List<(string Function, IrFunctionType Signature)> FunctionRefs { get; } = [];

  public List<BoundMethodKey> BoundMethods { get; } = [];

  public List<IrType> SequenceElementTypes { get; } = [];

  public List<IrFunction> IteratorFunctions { get; } = [];

  public List<IrType> ArraySequenceElementTypes { get; } = [];

  public List<IrType> SliceElementTypes { get; } = [];

  public bool NeedsPointerStruct { get; private set; }

  public List<IrType> PointerLoadTypes { get; } = [];

  public List<IrType> PointerStoreTypes { get; } = [];

  public List<IrClassType> RecordEqualsClasses { get; } = [];

  public bool NeedsPext32 { get; private set; }

  public bool NeedsPext64 { get; private set; }

  public bool NeedsPdep32 { get; private set; }

  public bool NeedsPdep64 { get; private set; }

  private readonly HashSet<IrType> _cellTypeSet = [];
  private readonly HashSet<IrFunctionType> _fnTypeSet = [];
  private readonly HashSet<(string, IrFunctionType)> _fnRefSet = [];
  private readonly HashSet<BoundMethodKey> _boundMethodSet = [];
  private readonly HashSet<IrType> _seqElemSet = [];
  private readonly HashSet<IrType> _arrSeqElemSet = [];
  private readonly HashSet<IrType> _sliceElemSet = [];
  private readonly HashSet<IrType> _ptrLoadSet = [];
  private readonly HashSet<IrType> _ptrStoreSet = [];
  private readonly HashSet<IrClassType> _recordEqSet = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrLambda> _lambdaSet = new(ReferenceEqualityComparer.Instance);

  /// <summary>Analyzes <paramref name="module"/> and returns its deterministic synthesis plan.</summary>
  public static MsilSynthesisPlan Analyze(IrModule module) {
    var plan = new MsilSynthesisPlan();

    foreach (var type in module.Types) {
      switch (type) {
        case IrStructType structure:
          foreach (var field in structure.Fields)
            plan.RecordType(field.Type);
          break;
        case IrInterfaceType iface:
          foreach (var method in iface.Methods) {
            plan.MemberFunctions.Add(method.Function);
            plan.InterfaceMethodOwners[method] = iface;
          }
          break;
        case IrClassType cls:
          foreach (var field in cls.Fields)
            plan.RecordType(field.Type);
          foreach (var ctor in cls.Constructors) {
            plan.MemberFunctions.Add(ctor.Function);
            plan.ClassConstructorsByFn[ctor.Function] = (cls, ctor);
          }
          foreach (var method in cls.Methods) {
            plan.MemberFunctions.Add(method.Function);
            plan.ClassMethodOwners[method] = cls;
          }
          break;
      }
    }

    foreach (var constant in module.Constants)
      plan.RecordType(constant.Type);

    foreach (var global in module.Globals) {
      plan.RecordType(global.Type);
      if (global.Initializer is not null)
        plan.VisitExpr(global.Initializer);
    }

    foreach (var foreign in module.ForeignFunctions) {
      plan.RecordType(foreign.ReturnType);
      foreach (var p in foreign.Parameters)
        plan.RecordType(p.Type);
    }

    foreach (var fn in module.Functions) {
      plan.RecordType(fn.ReturnType);
      foreach (var p in fn.Parameters)
        plan.RecordType(p.Type);
      foreach (var l in fn.Locals)
        plan.RecordType(l.Type);

      if (fn.ReturnType.IsLazySequence && !IsAbstractSignature(module, fn))
        plan.IteratorFunctions.Add(fn);

      plan.VisitStmt(fn.Body);
    }

    // Record cell types in deterministic order.
    foreach (var local in plan.CapturedLocals)
      plan.AddCellType(local.Type);

    return plan;
  }

  private static bool IsAbstractSignature(IrModule module, IrFunction fn) {
    foreach (var iface in module.Types.OfType<IrInterfaceType>())
      if (iface.Methods.Any(m => m.Function == fn.Name))
        return true;
    foreach (var cls in module.Types.OfType<IrClassType>())
      if (cls.Methods.Any(m => m.Function == fn.Name && m.Dispatch == IrDispatch.Abstract))
        return true;
    return false;
  }

  private void AddCellType(IrType type) {
    this.RecordType(type);
    if (this._cellTypeSet.Add(type))
      this.CellTypes.Add(type);
  }

  private void AddRecordEquals(IrClassType cls) {
    if (!this._recordEqSet.Add(cls))
      return;

    this.RecordEqualsClasses.Add(cls);
    foreach (var field in cls.AllFields) {
      if (field.Type.ClassType is { IsRecord: true } nested)
        this.AddRecordEquals(nested);
    }
  }

  private void RecordType(IrType type) {
    switch (type.Kind) {
      case IrTypeKind.Array:
        this.RecordType(type.Element!);
        break;
      case IrTypeKind.Optional:
        this.RecordType(type.Underlying!);
        break;
      case IrTypeKind.List or IrTypeKind.Set:
        this.RecordType(type.Element!);
        break;
      case IrTypeKind.Map:
        this.RecordType(type.Key!);
        this.RecordType(type.Element!);
        break;
      case IrTypeKind.Async:
        this.RecordType(type.Element!);
        break;
      case IrTypeKind.Function: {
        var sig = type.Signature!;
        this.RecordType(sig.Result);
        foreach (var p in sig.Parameters)
          this.RecordType(p);
        if (this._fnTypeSet.Add(sig))
          this.FunctionTypes.Add(sig);
        break;
      }
      case IrTypeKind.Sequence: {
        var elem = type.Element!;
        this.RecordType(elem);
        if (this._seqElemSet.Add(elem))
          this.SequenceElementTypes.Add(elem);
        break;
      }
      case IrTypeKind.Span or IrTypeKind.ReadOnlySpan or IrTypeKind.Memory or IrTypeKind.ReadOnlyMemory: {
        var elem = type.Element!;
        this.RecordType(elem);
        if (this._sliceElemSet.Add(elem))
          this.SliceElementTypes.Add(elem);
        break;
      }
      case IrTypeKind.Pointer:
        this.NeedsPointerStruct = true;
        this.RecordType(type.Element!);
        break;
    }
  }

  private void VisitStmt(IrStmt stmt) {
    switch (stmt) {
      case IrBlock block:
        foreach (var child in block.Statements)
          this.VisitStmt(child);
        break;
      case IrDeclare declare:
        this.RecordType(declare.Local.Type);
        if (declare.Initializer is not null)
          this.VisitExpr(declare.Initializer);
        break;
      case IrAssign assign:
        this.VisitExpr(assign.Value);
        break;
      case IrGlobalAssign globalAssign:
        this.VisitExpr(globalAssign.Value);
        break;
      case IrStore store:
        this.VisitExpr(store.Array);
        this.VisitExpr(store.Index);
        this.VisitExpr(store.Value);
        break;
      case IrFieldStore fieldStore:
        this.VisitExpr(fieldStore.Target);
        this.VisitExpr(fieldStore.Value);
        break;
      case IrExprStmt exprStmt:
        this.VisitExpr(exprStmt.Expression);
        break;
      case IrWriteLine write:
        this.VisitExpr(write.Value);
        break;
      case IrIf conditional:
        this.VisitExpr(conditional.Condition);
        this.VisitStmt(conditional.Then);
        if (conditional.Else is not null)
          this.VisitStmt(conditional.Else);
        break;
      case IrWhile loop:
        this.VisitExpr(loop.Condition);
        this.VisitStmt(loop.Body);
        break;
      case IrDoWhile loop:
        this.VisitStmt(loop.Body);
        this.VisitExpr(loop.Condition);
        break;
      case IrFor loop:
        this.VisitExpr(loop.Condition);
        this.VisitStmt(loop.Body);
        this.VisitStmt(loop.Step);
        break;
      case IrSwitch selection:
        this.VisitExpr(selection.Value);
        foreach (var section in selection.Sections)
          this.VisitStmt(section.Body);
        break;
      case IrReturn ret:
        if (ret.Value is not null)
          this.VisitExpr(ret.Value);
        break;
      case IrThrow throwing:
        this.VisitExpr(throwing.Exception);
        break;
      case IrTry trying:
        this.VisitStmt(trying.Body);
        foreach (var handler in trying.Catches) {
          if (handler.Filter is not null)
            this.VisitExpr(handler.Filter);
          this.VisitStmt(handler.Body);
        }
        if (trying.Finally is not null)
          this.VisitStmt(trying.Finally);
        break;
      case IrUsing scope:
        this.RecordType(scope.Resource.Type);
        this.VisitExpr(scope.Initializer);
        this.VisitStmt(scope.Body);
        break;
      case IrForEachCollection fe:
        this.VisitExpr(fe.Collection);
        if (fe.Element is not null)
          this.RecordType(fe.Element.Type);
        if (fe.Value is not null)
          this.RecordType(fe.Value.Type);
        this.VisitStmt(fe.Body);
        break;
      case IrYield yielded:
        this.VisitExpr(yielded.Value);
        break;
      case IrYieldFrom delegated:
        this.VisitExpr(delegated.Sequence);
        break;
      case IrForEachSequence feSeq:
        this.RecordType(feSeq.Element.Type);
        this.VisitExpr(feSeq.Sequence);
        this.VisitStmt(feSeq.Body);
        break;
    }
  }

  private void VisitExpr(IrExpr expr) {
    this.RecordType(expr.Type);
    switch (expr) {
      case IrUnary unary:
        this.VisitExpr(unary.Operand);
        break;
      case IrBinary binary:
        this.VisitExpr(binary.Left);
        this.VisitExpr(binary.Right);
        break;
      case IrConvert convert:
        this.VisitExpr(convert.Operand);
        break;
      case IrConditional conditional:
        this.VisitExpr(conditional.Condition);
        this.VisitExpr(conditional.WhenTrue);
        this.VisitExpr(conditional.WhenFalse);
        break;
      case IrArrayLoad load:
        this.VisitExpr(load.Array);
        this.VisitExpr(load.Index);
        break;
      case IrArrayLength length:
        this.VisitExpr(length.Array);
        break;
      case IrNewArray create:
        this.VisitExpr(create.Length);
        break;
      case IrCall call:
        foreach (var arg in call.Arguments)
          this.VisitExpr(arg);
        break;
      case IrForeignCall call:
        foreach (var arg in call.Arguments)
          this.VisitExpr(arg);
        break;
      case IrStep step:
        this.VisitExpr(step.Target);
        break;
      case IrStructNew create:
        foreach (var arg in create.Arguments)
          this.VisitExpr(arg);
        break;
      case IrFieldLoad fieldLoad:
        this.VisitExpr(fieldLoad.Target);
        break;
      case IrStructEquals equals:
        this.VisitExpr(equals.Left);
        this.VisitExpr(equals.Right);
        if (equals.Left.Type.ClassType is { IsRecord: true } recCls)
          this.AddRecordEquals(recCls);
        break;
      case IrNew create:
        foreach (var arg in create.Arguments)
          this.VisitExpr(arg);
        break;
      case IrMethodCall call:
        if (call.Target is not null)
          this.VisitExpr(call.Target);
        foreach (var arg in call.Arguments)
          this.VisitExpr(arg);
        break;
      case IrReferenceEquals eq:
        this.VisitExpr(eq.Left);
        this.VisitExpr(eq.Right);
        break;
      case IrTypeTest test:
        this.VisitExpr(test.Value);
        this.RecordType(test.Target);
        break;
      case IrCast cast:
        this.VisitExpr(cast.Value);
        break;
      case IrAsCast asCast:
        this.VisitExpr(asCast.Value);
        break;
      case IrGenericEquals geq:
        this.VisitExpr(geq.Left);
        this.VisitExpr(geq.Right);
        if (geq.Left.Type.ClassType is { IsRecord: true } gRecCls)
          this.AddRecordEquals(gRecCls);
        break;
      case IrGenericCompare gcmp:
        this.VisitExpr(gcmp.Left);
        this.VisitExpr(gcmp.Right);
        break;
      case IrSome some:
        this.VisitExpr(some.Value);
        break;
      case IrIsNone isNone:
        this.VisitExpr(isNone.Value);
        break;
      case IrUnwrap unwrap:
        this.VisitExpr(unwrap.Value);
        break;
      case IrCoalesce coalesce:
        this.VisitExpr(coalesce.Value);
        this.VisitExpr(coalesce.Fallback);
        break;
      case IrIsNull isNull:
        this.VisitExpr(isNull.Value);
        break;
      case IrBind bind:
        this.VisitExpr(bind.Value);
        break;
      case IrThrowExpression throwExpr:
        this.VisitExpr(throwExpr.Exception);
        break;
      case IrLambda lambda:
        if (this._lambdaSet.Add(lambda))
          this.Lambdas.Add(lambda);
        foreach (var cap in lambda.Captures)
          this.CapturedLocals.Add(cap);
        foreach (var p in lambda.Parameters)
          this.RecordType(p.Type);
        foreach (var l in lambda.Locals)
          this.RecordType(l.Type);
        this.VisitStmt(lambda.Body);
        break;
      case IrFunctionRef fnRef: {
        var key = (fnRef.Function, fnRef.Type.Signature!);
        if (this._fnRefSet.Add(key))
          this.FunctionRefs.Add(key);
        break;
      }
      case IrBoundMethod bound: {
        if (bound.Receiver is not null)
          this.VisitExpr(bound.Receiver);
        var key = new BoundMethodKey(bound.Method, bound.Kind, bound.Type.Signature!);
        if (this._boundMethodSet.Add(key))
          this.BoundMethods.Add(key);
        break;
      }
      case IrInvoke invoke:
        this.VisitExpr(invoke.Callee);
        foreach (var arg in invoke.Arguments)
          this.VisitExpr(arg);
        break;
      case IrCollection collection:
        foreach (var arg in collection.Arguments)
          this.VisitExpr(arg);
        if (collection.Op is IrCollectionOp.ListContains or IrCollectionOp.ListIndexOfEquals
            && collection.Arguments[0].Type.Element?.ClassType is { IsRecord: true } listRecCls)
          this.AddRecordEquals(listRecCls);
        break;
      case IrArraySequence arrSeq: {
        this.VisitExpr(arrSeq.Array);
        var elem = arrSeq.Type.Element!;
        if (this._arrSeqElemSet.Add(elem))
          this.ArraySequenceElementTypes.Add(elem);
        break;
      }
      case IrAwait awaited:
        this.VisitExpr(awaited.Operand);
        break;
      case IrAsync asyncOp:
        foreach (var arg in asyncOp.Arguments)
          this.VisitExpr(arg);
        break;
      case IrIntrinsic intrinsic:
        this.VisitIntrinsic(intrinsic);
        break;
    }
  }

  private void VisitIntrinsic(IrIntrinsic intrinsic) {
    foreach (var arg in intrinsic.Arguments)
      this.VisitExpr(arg);

    switch (intrinsic.Op) {
      case IrIntrinsicOp.AddressOf when intrinsic.Arguments is [IrLocalRef localRef]:
        this.AddressedLocals.Add(localRef.Local);
        break;
      case IrIntrinsicOp.PointerLoad:
        if (this._ptrLoadSet.Add(intrinsic.Type))
          this.PointerLoadTypes.Add(intrinsic.Type);
        break;
      case IrIntrinsicOp.PointerStore: {
        var valType = intrinsic.Arguments[1].Type;
        if (this._ptrStoreSet.Add(valType))
          this.PointerStoreTypes.Add(valType);
        break;
      }
      case IrIntrinsicOp.ParallelBitExtract:
        if (intrinsic.Type.IsLong)
          this.NeedsPext64 = true;
        else
          this.NeedsPext32 = true;
        break;
      case IrIntrinsicOp.ParallelBitDeposit:
        if (intrinsic.Type.IsLong)
          this.NeedsPdep64 = true;
        else
          this.NeedsPdep32 = true;
        break;
    }
  }

}
