using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// A module of iterator functions built by hand (#1327), for what the fixtures do not reach: every
/// kind of statement an iterator suspends inside, every way a consumer leaves a loop, nested closing,
/// arrays as sequences and the element types. Every public function takes nothing and returns an
/// <c>i32</c> or an <c>i64</c>, so the interpreter, the generated C# and the generated JavaScript can be
/// compared call by call. A trace records the order of events as <c>t[0] = t[0] * 10 + code</c>.
/// </summary>
internal static class SequenceModules {

  private static readonly IrType Ints = IrType.Sequence(IrType.I32, true);
  private static readonly IrType IntsOnce = IrType.Sequence(IrType.I32, false);
  private static readonly IrType Trace = IrType.Array(IrType.I32);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrBinary Plus(IrExpr left, IrExpr right) => new(IrBinaryOp.Add, left, right);

  private static IrBinary Times(IrExpr left, IrExpr right) => new(IrBinaryOp.Multiply, left, right);

  private static IrBlock B(params IrStmt[] statements) => new(statements);

  /// <summary><c>t[0] = t[0] * 10 + code</c>.</summary>
  private static IrStore Step(IrLocal trace, long code)
    => new(R(trace), I(0), Plus(Times(new IrArrayLoad(R(trace), I(0)), I(10)), I(code)));

  /// <summary><c>t[0] = t[0] * 10 + value</c>.</summary>
  private static IrStore StepBy(IrLocal trace, IrExpr value)
    => new(R(trace), I(0), Plus(Times(new IrArrayLoad(R(trace), I(0)), I(10)), value));

  private static IrAssign Add(IrLocal sum, IrExpr value) => new(sum, Plus(R(sum), value));

  private static IrDeclare NewTrace(IrLocal trace) => new(trace, new IrNewArray(Trace, I(1)));

  private static IrReturn TraceValue(IrLocal trace) => new(new IrArrayLoad(R(trace), I(0)));

  /// <summary>The names of the public functions of <see cref="Module"/>, which take nothing and return an <c>i32</c>.</summary>
  public static readonly string[] Functions = [
    "Lazy", "EarlyBreak", "Full", "ReturnInside", "ThrowInside", "ThrowInBody", "Twice", "TwiceOnce", "ResumeOnce", "NestedClose",
    "NestedFull", "Loops", "Switched", "Empty", "Delegated", "FromArray", "ArrayLoop", "Floats", "Truths", "Objects", "Doubled",
    "DoubledClose", "Counted", "FibSum", "Closure", "Repeated", "Picked", "Listed", "ListedBreak", "Mapped", "Modified",
    "PassWhile", "PassSequence", "PassFor", "PassList"
  ];

  /// <summary>The calls every implementation is compared on.</summary>
  public static IEnumerable<PrototypeCall> Calls() => Functions.Select(name => new PrototypeCall(name, [])).Append(new PrototypeCall("Longs", []));

  /// <summary>The module: private iterator functions and the public functions that consume them.</summary>
  public static IrModule Module() {
    var functions = new List<IrFunction>();
    IrFunction Public(string name, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
      => new(name, true, IrType.I32, [], [.. locals], B(statements));

    IrFunction Iterator(string name, IrType type, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
      => new(name, false, type, [.. parameters], [.. locals], B(statements));

    IrCall Call(string name, IrType type, params IrExpr[] arguments) => new(name, type, arguments);

    // Traced(t): 7, then 1, 2, 3 before each value, 9 in the finally block, 6 after it.
    foreach (var (name, type) in new[] { ("Traced", Ints), ("TracedOnce", IntsOnce) }) {
      var t = new IrLocal("t", Trace);
      functions.Add(Iterator(name, type, [t], [],
        Step(t, 7),
        new IrTry(B(Step(t, 1), new IrYield(I(1)), Step(t, 2), new IrYield(I(2)), Step(t, 3), new IrYield(I(3))), [], B(Step(t, 9))),
        Step(t, 6)));
    }

    // Up(n): 1 … n.
    foreach (var (name, type) in new[] { ("Up", Ints), ("UpOnce", IntsOnce) }) {
      var n = new IrLocal("n", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      functions.Add(Iterator(name, type, [n], [i],
        new IrDeclare(i, I(1)),
        new IrWhile(new IrBinary(IrBinaryOp.LessOrEqual, R(i), R(n)), B(new IrYield(R(i)), new IrAssign(i, Plus(R(i), I(1)))))));
    }

    // Lazy: nothing of the body runs before the first value is asked for - 5 before 7.
    {
      var t = new IrLocal("t", Trace);
      var s = new IrLocal("s", Ints);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Lazy", [t, s, v],
        NewTrace(t),
        new IrDeclare(s, Call("Traced", Ints, R(t))),
        Step(t, 5),
        new IrForEachSequence(v, R(s), B(Step(t, 8), new IrBreak())),
        TraceValue(t)));
    }

    // EarlyBreak: 5 7 1 8 9 - the call makes the sequence, the break closes it.
    {
      var t = new IrLocal("t", Trace);
      var s = new IrLocal("s", IntsOnce);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("EarlyBreak", [t, s, v],
        NewTrace(t),
        new IrDeclare(s, Call("TracedOnce", IntsOnce, R(t))),
        Step(t, 5),
        new IrForEachSequence(v, R(s), B(Step(t, 8), new IrBreak())),
        TraceValue(t)));
    }

    // Full: 7 1 8 2 8 3 8 9 6.
    {
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Full", [t, v], NewTrace(t), new IrForEachSequence(v, Call("Traced", Ints, R(t)), B(Step(t, 8))), TraceValue(t)));
    }

    // ReturnInside: a return from inside the loop closes it - 7 1 2 9, then 4 after the call.
    {
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(new IrFunction("Consume", false, IrType.I32, [t], [v], B(
        new IrForEachSequence(v, Call("Traced", Ints, R(t)), B(new IrIf(new IrBinary(IrBinaryOp.Equal, R(v), I(2)), B(new IrReturn(R(v))), null))),
        new IrReturn(I(0)))));
      var u = new IrLocal("t", Trace);
      var r = new IrLocal("r", IrType.I32);
      functions.Add(Public("ReturnInside", [u, r], NewTrace(u), new IrDeclare(r, Call("Consume", IrType.I32, R(u))), StepBy(u, R(r)), Step(u, 4), TraceValue(u)));
    }

    // ThrowInside: an exception in the consumer's body closes the loop before the catch - 7 1 9 4.
    var exception = IrType.Object(IrExceptionClass.InvalidOperationException);
    var thrown = new IrThrow(new IrNew(exception, IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 0), []));
    {
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("ThrowInside", [t, v],
        NewTrace(t),
        new IrTry(B(new IrForEachSequence(v, Call("Traced", Ints, R(t)), B(thrown))), [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, B(Step(t, 4)))], null),
        TraceValue(t)));
    }

    // ThrowInBody: the iterator throws after its first value; the value is consumed first - 1 8 9 4.
    {
      var t = new IrLocal("t", Trace);
      functions.Add(Iterator("Thrower", Ints, [t], [], new IrTry(B(Step(t, 1), new IrYield(I(1)), thrown), [], B(Step(t, 9)))));
      var u = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("ThrowInBody", [u, v],
        NewTrace(u),
        new IrTry(B(new IrForEachSequence(v, Call("Thrower", Ints, R(u)), B(Step(u, 8)))), [new IrCatch(IrExceptionClass.Exception, null, null, B(Step(u, 4)))], null),
        TraceValue(u)));
    }

    // Twice and TwiceOnce: a restartable sequence runs again (6), a single-pass one does not (3).
    foreach (var (name, type, iterator) in new[] { ("Twice", Ints, "Up"), ("TwiceOnce", IntsOnce, "UpOnce") }) {
      var s = new IrLocal("s", type);
      var sum = new IrLocal("sum", IrType.I32);
      var v = new IrLocal("v", IrType.I32);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public(name, [s, sum, v, w],
        new IrDeclare(s, Call(iterator, type, I(2))),
        new IrDeclare(sum, I(0)),
        new IrForEachSequence(v, R(s), B(Add(sum, R(v)))),
        new IrForEachSequence(w, R(s), B(Add(sum, R(w)))),
        new IrReturn(R(sum))));
    }

    // ResumeOnce: a single-pass loop left by a break is closed - the second loop gets nothing; 1 then 0 more.
    {
      var s = new IrLocal("s", IntsOnce);
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("ResumeOnce", [s, t, v, w],
        NewTrace(t),
        new IrDeclare(s, Call("TracedOnce", IntsOnce, R(t))),
        new IrForEachSequence(v, R(s), B(StepBy(t, R(v)), new IrBreak())),
        new IrForEachSequence(w, R(s), B(StepBy(t, R(w)))),
        TraceValue(t)));
    }

    // Inner and Outer: closing the outer cursor inside the inner one closes the inner one first.
    {
      var t = new IrLocal("t", Trace);
      functions.Add(Iterator("Inner", Ints, [t], [], new IrTry(B(new IrYield(I(1)), new IrYield(I(2))), [], B(Step(t, 8)))));
      var u = new IrLocal("t", Trace);
      functions.Add(Iterator("Outer", Ints, [u], [], new IrTry(B(new IrYieldFrom(Call("Inner", Ints, R(u))), Step(u, 3), new IrYield(I(3))), [], B(Step(u, 9)))));
      var w = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("NestedClose", [w, v], NewTrace(w), new IrForEachSequence(v, Call("Outer", Ints, R(w)), B(StepBy(w, R(v)), new IrBreak())), TraceValue(w)));
      var x = new IrLocal("t", Trace);
      var y = new IrLocal("v", IrType.I32);
      functions.Add(Public("NestedFull", [x, y], NewTrace(x), new IrForEachSequence(y, Call("Outer", Ints, R(x)), B(StepBy(x, R(y)))), TraceValue(x)));
    }

    // Loops: a for with continue and break, a do-while, a nested while - all suspending.
    {
      var n = new IrLocal("n", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      var j = new IrLocal("j", IrType.I32);
      functions.Add(Iterator("Looped", Ints, [n], [i, j],
        new IrDeclare(i, I(0)),
        new IrFor(new IrBinary(IrBinaryOp.Less, R(i), R(n)), B(
          new IrIf(new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.Remainder, R(i), I(2)), I(1)), B(new IrContinue()), null),
          new IrIf(new IrBinary(IrBinaryOp.Greater, R(i), I(6)), B(new IrBreak()), null),
          new IrYield(R(i))), B(new IrAssign(i, Plus(R(i), I(1))))),
        new IrDeclare(j, I(3)),
        new IrDoWhile(B(new IrYield(R(j)), new IrAssign(j, Plus(R(j), I(-1)))), new IrBinary(IrBinaryOp.Greater, R(j), I(0))),
        new IrWhile(new IrConst(IrType.Bool, 1), B(new IrYield(I(9)), new IrBreak()))));
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Loops", [t, v], NewTrace(t), new IrForEachSequence(v, Call("Looped", Ints, I(20)), B(StepBy(t, R(v)))), TraceValue(t)));
    }

    // Switched: a yield in the sections of a switch.
    {
      var n = new IrLocal("n", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      functions.Add(Iterator("Switching", Ints, [n], [i],
        new IrDeclare(i, I(0)),
        new IrWhile(new IrBinary(IrBinaryOp.Less, R(i), R(n)), B(
          new IrSwitch(R(i), [
            new IrSwitchSection([0], false, B(new IrYield(I(5)))),
            new IrSwitchSection([1], false, B(new IrBreak())),
            new IrSwitchSection([], true, B(new IrYield(R(i)), new IrYield(I(0))))
          ]),
          new IrAssign(i, Plus(R(i), I(1)))))));
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Switched", [t, v], NewTrace(t), new IrForEachSequence(v, Call("Switching", Ints, I(4)), B(StepBy(t, R(v)))), TraceValue(t)));
    }

    // Picked: a switch over a 64-bit value without a default section, whose sections yield.
    {
      var w = new IrLocal("w", IrType.I64);
      functions.Add(Iterator("Pick", Ints, [w], [],
        new IrSwitch(R(w), [
          new IrSwitchSection([1], false, B(new IrYield(I(7)))),
          new IrSwitchSection([1L << 40], false, B(new IrYield(I(8)), new IrBreak()))
        ]),
        new IrYield(I(0))));
      var t = new IrLocal("t", Trace);
      var a = new IrLocal("a", IrType.I32);
      var b = new IrLocal("b", IrType.I32);
      var c = new IrLocal("c", IrType.I32);
      functions.Add(Public("Picked", [t, a, b, c], NewTrace(t),
        new IrForEachSequence(a, Call("Pick", Ints, new IrConst(IrType.I64, 1)), B(StepBy(t, R(a)))),
        new IrForEachSequence(b, Call("Pick", Ints, new IrConst(IrType.I64, 1L << 40)), B(StepBy(t, R(b)))),
        new IrForEachSequence(c, Call("Pick", Ints, new IrConst(IrType.I64, 3)), B(StepBy(t, R(c)))),
        TraceValue(t)));
    }

    // Listed, ListedBreak, Mapped, Modified: a yield inside a loop over a list and over a map.
    {
      var ints = IrType.List(IrType.I32);
      var l = new IrLocal("l", ints);
      var x = new IrLocal("x", IrType.I32);
      IrCollection Op(IrCollectionOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);
      functions.Add(Iterator("FromList", Ints, [l], [x],
        new IrForEachCollection(R(l), EnumerationMode.Checked, x, null, B(new IrYield(Times(R(x), I(2)))))));
      functions.Add(Iterator("FromListLive", Ints, [l], [x],
        new IrForEachCollection(R(l), EnumerationMode.Live, x, null, B(
          new IrIf(new IrBinary(IrBinaryOp.Less, R(x), I(3)), B(new IrExprStmt(Op(IrCollectionOp.ListAdd, IrType.Void, R(l), Plus(R(x), I(10))))), null),
          new IrIf(new IrBinary(IrBinaryOp.Equal, R(x), I(2)), B(new IrContinue()), null),
          new IrYield(R(x))))));
      IrStmt[] Filled(IrLocal list) => [
        new IrDeclare(list, Op(IrCollectionOp.ListNew, ints)),
        new IrExprStmt(Op(IrCollectionOp.ListAdd, IrType.Void, R(list), I(1))),
        new IrExprStmt(Op(IrCollectionOp.ListAdd, IrType.Void, R(list), I(2))),
        new IrExprStmt(Op(IrCollectionOp.ListAdd, IrType.Void, R(list), I(3)))
      ];
      var a = new IrLocal("a", ints);
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Listed", [a, t, v], [.. Filled(a), NewTrace(t),
        new IrForEachSequence(v, Call("FromList", Ints, R(a)), B(StepBy(t, R(v)))),
        new IrForEachSequence(v, Call("FromListLive", Ints, R(a)), B(StepBy(t, new IrBinary(IrBinaryOp.Remainder, R(v), I(10))))),
        TraceValue(t)]));
      var b = new IrLocal("b", ints);
      var u = new IrLocal("t", Trace);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("ListedBreak", [b, u, w], [.. Filled(b), NewTrace(u),
        new IrForEachSequence(w, Call("FromList", Ints, R(b)), B(StepBy(u, R(w)), new IrBreak())),
        TraceValue(u)]));
      // A change of the list while the iterator is suspended inside its checked loop traps at its next step.
      var c = new IrLocal("c", ints);
      var y = new IrLocal("y", IrType.I32);
      var s = new IrLocal("s", IrType.I32);
      functions.Add(Public("Modified", [c, y, s], [.. Filled(c), new IrDeclare(s, I(0)),
        new IrTry(B(new IrForEachSequence(y, Call("FromList", Ints, R(c)), B(new IrAssign(s, Plus(R(s), R(y))), new IrExprStmt(Op(IrCollectionOp.ListAdd, IrType.Void, R(c), I(9)))))),
          [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, B(new IrAssign(s, Plus(R(s), I(100)))))], null),
        new IrReturn(R(s))]));

      var map = IrType.Map(IrType.I32, IrType.I32, MapOrder.SlotReuse);
      var m = new IrLocal("m", map);
      var k = new IrLocal("k", IrType.I32);
      var entry = new IrLocal("value", IrType.I32);
      functions.Add(Iterator("FromMap", Ints, [m], [k, entry],
        new IrForEachCollection(R(m), EnumerationMode.Checked, k, entry, B(new IrYield(Plus(Times(R(k), I(10)), R(entry)))))));
      var n = new IrLocal("n", map);
      var z = new IrLocal("z", IrType.I32);
      var r = new IrLocal("t", Trace);
      IrStmt Put(long key, long put) => new IrExprStmt(Op(IrCollectionOp.MapPut, IrType.Void, R(n), I(key), I(put)));
      functions.Add(Public("Mapped", [n, z, r], new IrDeclare(n, Op(IrCollectionOp.MapNew, map)), Put(1, 2), Put(3, 4), Put(5, 6),
        new IrExprStmt(Op(IrCollectionOp.MapRemove, IrType.Bool, R(n), I(3))), NewTrace(r),
        new IrForEachSequence(z, Call("FromMap", Ints, R(n)), B(new IrStore(R(r), I(0), Plus(Times(new IrArrayLoad(R(r), I(0)), I(100)), R(z))))),
        TraceValue(r)));
    }

    // PassWhile, PassSequence, PassFor, PassList: a closure made in a pass of a loop that suspends keeps the variables of that pass.
    {
      var function = IrType.Function(IrType.I32);
      var functionsOf = IrType.Sequence(function, true);
      IrLambda Reads(IrLocal local) => IrLambda.Of([], IrType.I32, [], B(new IrReturn(Times(R(local), I(10)))));
      IrFunction Summed(string name, string iterator) {
        var f = new IrLocal("f", function);
        var sum = new IrLocal("sum", IrType.I32);
        return Public(name, [f, sum], new IrDeclare(sum, I(0)),
          new IrForEachSequence(f, Call(iterator, functionsOf), B(new IrAssign(sum, Plus(R(sum), new IrInvoke(IrType.I32, R(f), []))))), new IrReturn(R(sum)));
      }

      // Every closure is called after the iterator ended: a shared variable would give the last value three times.
      IrFunction Deferred(string name, string iterator) {
        var f = new IrLocal("f", function);
        var all = new IrLocal("all", IrType.Array(function));
        var at = new IrLocal("at", IrType.I32);
        var sum = new IrLocal("sum", IrType.I32);
        var g = new IrLocal("g", function);
        return Public(name, [f, all, at, sum, g],
          new IrDeclare(all, new IrNewArray(IrType.Array(function), I(3))),
          new IrDeclare(at, I(0)),
          new IrForEachSequence(f, Call(iterator, functionsOf), B(new IrStore(R(all), R(at), R(f)), new IrAssign(at, Plus(R(at), I(1))))),
          new IrDeclare(sum, I(0)),
          new IrForEachSequence(g, new IrArraySequence(R(all)), B(new IrAssign(sum, Plus(R(sum), new IrInvoke(IrType.I32, R(g), []))))),
          new IrReturn(R(sum)));
      }

      var i = new IrLocal("i", IrType.I32);
      var k = new IrLocal("k", IrType.I32);
      functions.Add(Iterator("MadeInWhile", functionsOf, [], [i, k],
        new IrDeclare(i, I(0)),
        new IrWhile(new IrBinary(IrBinaryOp.Less, R(i), I(3)), B(new IrDeclare(k, R(i)), new IrYield(Reads(k)), new IrAssign(i, Plus(R(i), I(1)))))));
      functions.Add(Deferred("PassWhile", "MadeInWhile"));
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Iterator("MadeInSequence", functionsOf, [], [v], new IrForEachSequence(v, Call("Up", Ints, I(3)), B(new IrYield(Reads(v))))));
      functions.Add(Deferred("PassSequence", "MadeInSequence"));
      var j = new IrLocal("j", IrType.I32);
      functions.Add(Iterator("MadeInFor", functionsOf, [], [j],
        new IrDeclare(j, I(0)),
        new IrFor(new IrBinary(IrBinaryOp.Less, R(j), I(3)), B(new IrYield(Reads(j))), B(new IrAssign(j, Plus(R(j), I(1))))) { PerIteration = [j] }));
      functions.Add(Deferred("PassFor", "MadeInFor"));
      var e = new IrLocal("e", IrType.I32);
      var list = new IrLocal("list", IrType.List(IrType.I32));
      functions.Add(Iterator("MadeInList", functionsOf, [], [list, e],
        new IrDeclare(list, new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.I32), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(4)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(5)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(6)])),
        new IrForEachCollection(R(list), EnumerationMode.Live, e, null, B(new IrYield(Reads(e))))));
      functions.Add(Deferred("PassList", "MadeInList"));
      functions.Add(Summed("PassSummed", "MadeInWhile"));
    }

    // Empty: a yield break first - the loop's body never runs.
    {
      var n = new IrLocal("n", IrType.I32);
      functions.Add(Iterator("Gate", Ints, [n], [],
        new IrIf(new IrBinary(IrBinaryOp.Less, R(n), I(0)), B(new IrYieldBreak()), null),
        new IrYield(Plus(R(n), I(1)))));
      var sum = new IrLocal("sum", IrType.I32);
      var v = new IrLocal("v", IrType.I32);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("Empty", [sum, v, w],
        new IrDeclare(sum, I(0)),
        new IrForEachSequence(v, Call("Gate", Ints, I(-1)), B(Add(sum, I(100)))),
        new IrForEachSequence(w, Call("Gate", Ints, I(4)), B(Add(sum, R(w)))),
        new IrReturn(R(sum))));
    }

    // Delegated and FromArray: yield from a sequence and from an array.
    var digits = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(4), I(5), I(6)]);
    {
      functions.Add(Iterator("Chained", Ints, [], [], new IrYieldFrom(Call("Up", Ints, I(2))), new IrYieldFrom(new IrArraySequence(digits)), new IrYield(I(7))));
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Delegated", [t, v], NewTrace(t), new IrForEachSequence(v, Call("Chained", Ints), B(StepBy(t, R(v)))), TraceValue(t)));
      var a = new IrLocal("a", IrType.Array(IrType.I32));
      var u = new IrLocal("u", Trace);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("FromArray", [a, u, w],
        new IrDeclare(a, digits),
        NewTrace(u),
        new IrForEachSequence(w, new IrArraySequence(R(a)), B(StepBy(u, R(w)), new IrStore(R(a), I(2), I(9)))),
        TraceValue(u)));
      var x = new IrLocal("x", IrType.I32);
      var z = new IrLocal("z", IrType.I32);
      var sum = new IrLocal("sum", IrType.I32);
      functions.Add(Iterator("Passed", Ints, [], [], new IrYieldFrom(new IrArraySequence(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2)])))));
      functions.Add(Public("ArrayLoop", [sum, x, z],
        new IrDeclare(sum, I(0)),
        new IrForEachSequence(x, Call("Passed", Ints), B(Add(sum, R(x)))),
        new IrForEachSequence(z, Call("Passed", Ints), B(Add(sum, Times(R(z), I(10))))),
        new IrReturn(R(sum))));
    }

    // The element types: i64, f64, bool and a class.
    {
      var longs = IrType.Sequence(IrType.I64, true);
      functions.Add(Iterator("Wide", longs, [], [], new IrYield(new IrConst(IrType.I64, 1L << 40)), new IrYield(new IrConst(IrType.I64, -3))));
      var sum = new IrLocal("sum", IrType.I64);
      var v = new IrLocal("v", IrType.I64);
      functions.Add(new IrFunction("Longs", true, IrType.I64, [], [sum, v], B(
        new IrDeclare(sum, new IrConst(IrType.I64, 0)),
        new IrForEachSequence(v, Call("Wide", longs), B(new IrAssign(sum, new IrBinary(IrBinaryOp.Add, R(sum), R(v))))),
        new IrReturn(R(sum)))));

      var doubles = IrType.Sequence(IrType.F64, true);
      IrConst F(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));
      functions.Add(Iterator("Halves", doubles, [], [], new IrYield(F(0.5)), new IrYield(F(-2.25))));
      var total = new IrLocal("total", IrType.F64);
      var d = new IrLocal("d", IrType.F64);
      functions.Add(Public("Floats", [total, d],
        new IrDeclare(total, F(1)),
        new IrForEachSequence(d, Call("Halves", doubles), B(new IrAssign(total, new IrBinary(IrBinaryOp.Multiply, R(total), R(d))))),
        new IrReturn(new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Multiply, R(total), F(-100)), IrConversion.Saturate))));

      var truths = IrType.Sequence(IrType.Bool, false);
      functions.Add(Iterator("Flags", truths, [], [], new IrYield(new IrConst(IrType.Bool, 1)), new IrYield(new IrConst(IrType.Bool, 0)), new IrYield(new IrConst(IrType.Bool, 1))));
      var t = new IrLocal("t", Trace);
      var b = new IrLocal("b", IrType.Bool);
      functions.Add(Public("Truths", [t, b], NewTrace(t), new IrForEachSequence(b, Call("Flags", truths), B(StepBy(t, new IrConditional(R(b), I(1), I(2))))), TraceValue(t)));
    }

    var box = new IrClassType("Box");
    var value = new IrField("Value", IrType.I32, false);
    box.Define([value]);
    var make = new IrConstructor("Box..ctor");
    box.DefineMembers([make], [], [], []);
    {
      var self = new IrLocal("this", IrType.Object(box));
      var start = new IrLocal("start", IrType.I32);
      functions.Add(new IrFunction("Box..ctor", false, IrType.Void, [self, start], [], B(new IrFieldStore(R(self), value, R(start)))));
      var boxes = IrType.Sequence(IrType.Object(box), true);
      functions.Add(Iterator("Boxes", boxes, [], [], new IrYield(new IrNew(IrType.Object(box), make, [I(3)])), new IrYield(new IrNew(IrType.Object(box), make, [I(4)]))));
      var t = new IrLocal("t", Trace);
      var o = new IrLocal("o", IrType.Object(box));
      functions.Add(Public("Objects", [t, o], NewTrace(t), new IrForEachSequence(o, Call("Boxes", boxes), B(StepBy(t, new IrFieldLoad(R(o), value)))), TraceValue(t)));
    }

    // Doubled: an iterator that enumerates another inside a loop of its own; closing it closes the inner one.
    {
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Iterator("Twofold", Ints, [t], [v],
        new IrTry(B(new IrForEachSequence(v, Call("Inner", Ints, R(t)), B(new IrYield(Times(R(v), I(2)))))), [], B(Step(t, 5)))));
      var u = new IrLocal("t", Trace);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("Doubled", [u, w], NewTrace(u), new IrForEachSequence(w, Call("Twofold", Ints, R(u)), B(StepBy(u, R(w)))), TraceValue(u)));
      var x = new IrLocal("t", Trace);
      var y = new IrLocal("w", IrType.I32);
      functions.Add(Public("DoubledClose", [x, y], NewTrace(x),
        new IrForEachSequence(y, Call("Twofold", Ints, R(x)), B(StepBy(x, R(y)), new IrIf(new IrBinary(IrBinaryOp.Equal, R(y), I(2)), B(new IrBreak()), null))),
        TraceValue(x)));
    }

    // Counted: a parameter changed by the body; a restartable sequence starts from the argument again.
    {
      var n = new IrLocal("n", IrType.I32);
      functions.Add(Iterator("Down", Ints, [n], [],
        new IrWhile(new IrBinary(IrBinaryOp.Greater, R(n), I(0)), B(new IrYield(R(n)), new IrAssign(n, Plus(R(n), I(-1)))))));
      var s = new IrLocal("s", Ints);
      var t = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      var w = new IrLocal("w", IrType.I32);
      functions.Add(Public("Counted", [s, t, v, w],
        NewTrace(t),
        new IrDeclare(s, Call("Down", Ints, I(2))),
        new IrForEachSequence(v, R(s), B(StepBy(t, R(v)))),
        new IrForEachSequence(w, R(s), B(StepBy(t, R(w)))),
        TraceValue(t)));
    }

    // FibSum: an infinite single-pass sequence with int32 wrap-around, left after 47 values.
    {
      var a = new IrLocal("a", IrType.I32);
      var b = new IrLocal("b", IrType.I32);
      var c = new IrLocal("c", IrType.I32);
      functions.Add(Iterator("Fib", IntsOnce, [], [a, b, c],
        new IrDeclare(a, I(0)),
        new IrDeclare(b, I(1)),
        new IrWhile(new IrConst(IrType.Bool, 1), B(new IrYield(R(a)), new IrDeclare(c, Plus(R(a), R(b))), new IrAssign(a, R(b)), new IrAssign(b, R(c))))));
      var sum = new IrLocal("sum", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("FibSum", [sum, i, v],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrForEachSequence(v, Call("Fib", IntsOnce), B(
          new IrIf(new IrBinary(IrBinaryOp.Equal, R(i), I(47)), B(new IrBreak()), null),
          Add(sum, R(v)),
          new IrAssign(i, Plus(R(i), I(1))))),
        new IrReturn(R(sum))));
    }

    // Closure: a lambda made inside an iterator captures a local of it, and sees it change across a yield.
    {
      var k = new IrLocal("k", IrType.I32);
      var f = new IrLocal("f", IrType.Function(IrType.I32));
      functions.Add(Iterator("Captured", Ints, [], [k, f],
        new IrDeclare(k, I(1)),
        new IrDeclare(f, new IrLambda(IrType.Function(IrType.I32), [k], [], [], B(new IrReturn(Times(R(k), I(10)))))),
        new IrYield(new IrInvoke(IrType.I32, R(f), [])),
        new IrAssign(k, I(2)),
        new IrYield(new IrInvoke(IrType.I32, R(f), []))));
      var sum = new IrLocal("sum", IrType.I32);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Closure", [sum, v], new IrDeclare(sum, I(0)), new IrForEachSequence(v, Call("Captured", Ints), B(Add(sum, R(v)))), new IrReturn(R(sum))));
    }

    // Repeated: a try with a finally inside a loop, entered and left on every pass; nested try blocks.
    {
      var t = new IrLocal("t", Trace);
      var i = new IrLocal("i", IrType.I32);
      functions.Add(Iterator("Rounds", Ints, [t], [i],
        new IrDeclare(i, I(1)),
        new IrTry(B(
          new IrWhile(new IrBinary(IrBinaryOp.LessOrEqual, R(i), I(3)), B(
            new IrTry(B(new IrYield(R(i))), [], B(Step(t, 8))),
            new IrAssign(i, Plus(R(i), I(1)))))), [], B(Step(t, 9)))));
      var u = new IrLocal("t", Trace);
      var v = new IrLocal("v", IrType.I32);
      functions.Add(Public("Repeated", [u, v], NewTrace(u),
        new IrForEachSequence(v, Call("Rounds", Ints, R(u)), B(StepBy(u, R(v)), new IrIf(new IrBinary(IrBinaryOp.Equal, R(v), I(2)), B(new IrBreak()), null))),
        TraceValue(u)));
    }

    return new IrModule("Sequences", functions) { Types = [box] };
  }

}
