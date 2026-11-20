using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.IR;
using static Polypiler.Tests.Analysis.AnalysisIr;

namespace Polypiler.Tests.Analysis;

/// <summary>The observed run of the interpreter that the soundness checker watches: every point reached, with the values there.</summary>
[TestFixture]
public sealed class IrObserverTests {

  private sealed class Recorder(Func<IrObservation, object?> read) : IIrObserver {

    public List<(IrBlock Block, int Index, object? Value)> Seen { get; } = [];

    public void Reached(IrBlock block, int index, IrObservation state) => this.Seen.Add((block, index, read(state)));

  }

  /// <summary>Each statement and the end of each block that runs through is reported, in order, with the value there.</summary>
  [Test]
  public void EveryPointReached_IsReported() {
    var then = Block(new IrAssign(Y, K(2)));
    var function = Function([X], new IrDeclare(Y, K(1)), new IrIf(new IrConst(IrType.Bool, 1), then, null), new IrReturn(R(Y)));
    var recorder = new Recorder(state => state.Value(R(Y)));
    Assert.That(IrInterpreter.Call(Module(function), "f", [0u], recorder), Is.EqualTo(2u));
    Assert.That(recorder.Seen, Is.EqualTo(new (IrBlock, int, object?)[] {
      (function.Body, 0, null), (function.Body, 1, new IrConst(IrType.U32, 1)), (then, 0, new IrConst(IrType.U32, 1)),
      (then, 1, new IrConst(IrType.U32, 2)), (function.Body, 2, new IrConst(IrType.U32, 2))
    }));
  }

  /// <summary>Values are integers, truth values and pointers; anything else, a trap and a missing variable are no value.</summary>
  [Test]
  public void Values_AreObservedByType() {
    var pointer = new IrLocal("p", IrType.Pointer(IrType.U32));
    var empty = new IrLocal("n", IrType.Pointer(IrType.U32));
    var function = Function([X],
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.U32), [new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Pointer(IrType.U32), [new IrConst(IrType.I32, 4)]), new IrConst(IrType.I32, 1)])),
      new IrDeclare(empty, new IrNull(IrType.Pointer(IrType.U32))),
      new IrReturn(R(X)));
    var values = new List<object?>();
    var recorder = new Recorder(state => {
      if (values.Count == 0) {
        values.Add(state.Value(Op(IrBinaryOp.Equal, R(X), K(3))));
        values.Add(state.Value(new IrConst(IrType.U32, 9)));
        values.Add(state.Value(new IrStrConst("s")));
        values.Add(state.Value(Op(IrBinaryOp.Divide, K(1), K(0))));
        values.Add(state.Value(R(pointer)));
      }

      return null;
    });
    IrInterpreter.Call(Module(function), "f", [3u], recorder);
    var end = new Recorder(state => (state.Value(R(pointer)), state.Value(R(empty))));
    IrInterpreter.Call(Module(function), "f", [3u], end);
    Assert.That(values, Is.EqualTo(new object?[] { new IrConst(IrType.Bool, 1), new IrConst(IrType.U32, 9), null, null, null }));
    var (moved, none) = ((object?, object?))end.Seen[2].Value!;
    Assert.That(moved, Is.TypeOf<IrObservedPointer>().And.Property("Offset").EqualTo(4).And.Property("Bytes").EqualTo(16).And.Property("IsNull").False);
    Assert.That(none, Is.TypeOf<IrObservedPointer>().And.Property("IsNull").True.And.Property("Bytes").EqualTo(0));
    Assert.That(() => IrInterpreter.Call(Module(function), "f", [3u], new Recorder(state => state.Value(null!))), Throws.ArgumentNullException);
  }

  /// <summary>An observed call takes a module without globals, an observer and a budget of steps.</summary>
  [Test]
  public void ObservedCalls_CheckTheirArguments() {
    var function = Function([X], new IrWhile(new IrConst(IrType.Bool, 1), Block(new IrAssign(X, R(X)))), new IrReturn(R(X)));
    var recorder = new Recorder(_ => null);
    Assert.That(() => IrInterpreter.Call(Module(function), "f", [1u], recorder, 50), Throws.TypeOf<IrBudgetException>());
    Assert.That(() => IrInterpreter.Call(null!, "f", [], recorder), Throws.ArgumentNullException);
    Assert.That(() => IrInterpreter.Call(Module(function), "f", [], null!), Throws.ArgumentNullException);
    var global = new IrGlobal("g", IrType.U32, null);
    Assert.That(() => IrInterpreter.Call(Module(function) with { Globals = [global] }, "f", [1u], recorder), Throws.ArgumentException.With.Message.Contains("globals"));
  }

}
