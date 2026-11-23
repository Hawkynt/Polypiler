using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Analysis.Scev;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Analysis.Scev;

/// <summary>
/// The iteration-trace oracle of #350: seeded loops run in the interpreter, which records, at the start of
/// every pass, a value the analysis has a closed form for and, at the end, how many passes ran. Every
/// recurrence must give the recorded value of every pass, modulo 2^w; every no-wrap flag must hold for the
/// recorded passes; an exact or symbolic count must equal, and a bound must not be exceeded by, the passes
/// of every run that ends.
/// </summary>
[TestFixture]
public sealed class ScalarEvolutionSoundnessTests {

  private const int Longest = 2048;

  private static readonly IrType[] _types = [IrType.I32, IrType.U32, IrType.I64, IrType.U64];
  private static readonly IrBinaryOp[] _tests = [IrBinaryOp.Less, IrBinaryOp.LessOrEqual, IrBinaryOp.Greater, IrBinaryOp.GreaterOrEqual, IrBinaryOp.NotEqual];
  private static readonly long[] _steps = [1, 2, 3, 7, -1, -2, -3, -5, 100, -100, 0x40000000];

  /// <summary>One random loop: <c>while (i op bound) { c++; trace[c] = (i64)(a·i + b + j); [if (i == stop) break;] j += step2; i += step; }</c>.</summary>
  private sealed record Case(IrModule Module, IrStmt Loop, IrExpr Traced, IrType Type, bool Symbolic, long Bound, string Text);

  private static IrConst Const(IrType type, long value) => new(type, IrOperators.Wrap(type, value));

  private static Case Make(Random random) {
    var type = _types[random.Next(_types.Length)];
    var op = _tests[random.Next(_tests.Length)];
    var step = _steps[random.Next(_steps.Length)];
    var second = _steps[random.Next(_steps.Length)];
    var start = random.Next(4) == 0 ? (random.Next(2) == 0 ? (long)IrOperators.Maximum(type) - random.Next(400) : (long)IrOperators.Minimum(type) + random.Next(400)) : random.Next(-300, 300);
    // A bound a few steps away, give or take, so that most loops end within the trace.
    var bound = unchecked(start + step * random.Next(-3, 60) + random.Next(-2, 3));
    var symbolic = random.Next(3) == 0;
    if (symbolic && random.Next(2) == 0) {
      // The canonical shape of a symbolic count: from 0 by 1 below an unsigned bound.
      (type, start, step) = (random.Next(2) == 0 ? IrType.U32 : IrType.U64, 0, 1);
      op = random.Next(2) == 0 ? IrBinaryOp.Less : IrBinaryOp.NotEqual;
      bound = random.Next(0, 80);
    }

    var breaks = random.Next(5) == 0;
    var (factor, offset) = (random.Next(-4, 5), random.Next(-50, 50));

    var trace = new IrLocal("trace", IrType.Array(IrType.I64));
    var n = new IrLocal("n", type);
    var i = new IrLocal("i", type);
    var j = new IrLocal("j", type);
    var c = new IrLocal("c", IrType.I32);
    IrExpr I() => new IrLocalRef(i);
    IrExpr Step(IrLocal local, long amount) => amount < 0
      ? new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(local), Const(type, -amount))
      : new IrBinary(IrBinaryOp.Add, new IrLocalRef(local), Const(type, amount));

    var traced = new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Const(type, factor), I()), Const(type, offset)), new IrLocalRef(j));
    var body = new List<IrStmt> {
      new IrAssign(c, new IrBinary(IrBinaryOp.Add, new IrLocalRef(c), new IrConst(IrType.I32, 1))),
      new IrStore(new IrLocalRef(trace), new IrLocalRef(c), new IrConvert(IrType.I64, traced))
    };
    if (breaks)
      body.Add(new IrIf(new IrBinary(IrBinaryOp.Equal, I(), Const(type, unchecked(start + step * random.Next(0, 8)))), new IrBlock([new IrBreak()]), null));
    body.Add(new IrAssign(j, Step(j, second)));
    body.Add(new IrAssign(i, Step(i, step)));

    IrExpr limit = symbolic ? new IrLocalRef(n) : Const(type, bound);
    var loop = new IrWhile(new IrBinary(op, I(), limit), new IrBlock(body));
    var statements = new List<IrStmt> {
      new IrDeclare(i, Const(type, start)),
      new IrDeclare(j, Const(type, random.Next(-10, 10))),
      new IrDeclare(c, new IrConst(IrType.I32, 0)),
      loop,
      new IrStore(new IrLocalRef(trace), new IrConst(IrType.I32, 0), new IrConvert(IrType.I64, new IrLocalRef(c))),
      new IrReturn(null)
    };
    var function = new IrFunction("F", true, IrType.Void, [trace, n], [i, j, c], new IrBlock(statements));
    var text = $"{type} i = {start}; while (i {op} {(symbolic ? "n" : bound.ToString(System.Globalization.CultureInfo.InvariantCulture))}) {{ trace a·i+b+j with a={factor} b={offset}; {(breaks ? "maybe break; " : "")}j += {second}; i += {step}; }}";
    return new(new IrModule("Scev", [function], []), loop, traced, type, symbolic, bound, text);
  }

  private static object Host(IrType type, long bits) => type.Kind switch {
    IrTypeKind.I32 => unchecked((int)bits),
    IrTypeKind.U32 => unchecked((uint)bits),
    IrTypeKind.I64 => bits,
    _ => unchecked((ulong)bits)
  };

  /// <summary>The value of a recurrence with constant parts in pass k, as the interpreter stores it widened to an i64.</summary>
  private static long Closed(Evolution.AddRec recurrence, long pass) {
    var type = recurrence.Type;
    var start = ((Evolution.Invariant)recurrence.Start).Value as IrConst;
    var step = ((Evolution.Invariant)recurrence.Step).Value as IrConst;
    var value = IrOperators.Wrap(type, unchecked(start!.Value + pass * step!.Value));
    return type.IsSigned || type.Bits == 64 ? value : value & (type.Bits == 32 ? 0xFFFFFFFFL : 0xFFFF);
  }

  private static BigInteger Unsigned(long bits, IrType type) => (BigInteger)(ulong)bits & ((BigInteger.One << type.Bits) - 1);

  private static BigInteger Signed(long bits, IrType type) {
    var value = Unsigned(bits, type);
    return value >= BigInteger.One << (type.Bits - 1) ? value - (BigInteger.One << type.Bits) : value;
  }

  [Test, Category("Slow")]
  public void TwoThousandSeededLoops_AgreeWithTheirIterationTraces() {
    var random = new Random(315);
    var (recurrences, exact, symbolic, bounded, unknown, ran) = (0, 0, 0, 0, 0, 0);
    for (var sample = 0; sample < 2000; ++sample) {
      var loop = Make(random);
      var analysis = new ScalarEvolution(loop.Module.Functions[0]);
      var evolution = analysis.Of(loop.Traced, loop.Loop);
      var count = analysis.TripCountOf(loop.Loop);
      var bound = loop.Symbolic ? unchecked(loop.Bound + random.Next(-3, 3)) : loop.Bound;
      var trace = new long[Longest + 1];
      long? passes;
      try {
        IrInterpreter.Call(loop.Module, "F", [trace, Host(loop.Type, bound)], 200_000);
        passes = trace[0];
        ++ran;
      } catch (Exception exception) when (exception is IrBudgetException or IrTrapException or IrException) {
        // Ran past the trace or the budget: no count may claim fewer passes than were recorded.
        passes = null;
      }

      var recorded = passes ?? Longest;
      var context = $"loop {sample}: {loop.Text}, n = {bound}: {evolution}, {count}";
      if (evolution is Evolution.AddRec { Start: Evolution.Invariant { Value: IrConst }, Step: Evolution.Invariant { Value: IrConst } } recurrence) {
        ++recurrences;
        var first = Unsigned(Closed(recurrence, 0), recurrence.Type);
        var delta = Signed(((IrConst)((Evolution.Invariant)recurrence.Step).Value).Value, recurrence.Type);
        var firstSigned = Signed(Closed(recurrence, 0), recurrence.Type);
        for (var pass = 0L; pass < recorded; ++pass) {
          Assert.That(trace[pass + 1], Is.EqualTo(Closed(recurrence, pass)), $"{context}, pass {pass}");
          if (recurrence.NoUnsignedWrap)
            Assert.That(Unsigned(trace[pass + 1], recurrence.Type), Is.EqualTo(first + pass * delta), $"{context}: unsigned wrap in pass {pass}");
          if (recurrence.NoSignedWrap)
            Assert.That(Signed(trace[pass + 1], recurrence.Type), Is.EqualTo(firstSigned + pass * delta), $"{context}: signed wrap in pass {pass}");
        }
      }

      switch (count) {
        case TripCount.Exact counted:
          ++exact;
          // A count longer than the trace cannot be watched to its end, only past the trace.
          if (counted.Count < Longest)
            Assert.That(passes, Is.EqualTo((long?)(long)counted.Count), context);
          else
            Assert.That(passes, Is.Null, context);
          break;
        case TripCount.Symbolic counted:
          ++symbolic;
          Assert.That(counted.Value, Is.InstanceOf<IrLocalRef>(), context);
          var expected = IrOperators.Number(loop.Type, IrOperators.Wrap(loop.Type, bound)) + (Int128)counted.Offset;
          if (expected < Longest)
            Assert.That(passes, Is.EqualTo((long?)(long)expected), context);
          else
            Assert.That(passes, Is.Null, context);
          break;
        case TripCount.Max max:
          ++bounded;
          Assert.That(passes is null && max.Bound < Longest, Is.False, $"{context}: a bound below the trace did not end the loop");
          if (passes is { } finished)
            Assert.That((BigInteger)finished, Is.LessThanOrEqualTo(max.Bound), context);
          break;
        default:
          ++unknown;
          break;
      }
    }

    TestContext.Out.WriteLine($"seed 315, 2000 loops: {ran} ended within the trace; {recurrences} closed forms checked pass by pass; counts {exact} exact, {symbolic} symbolic, {bounded} bounded, {unknown} unknown; 0 violations.");
    Assert.That(exact + symbolic + bounded, Is.GreaterThan(500), "the generator must produce countable loops");
    Assert.That(recurrences, Is.GreaterThan(1500));
  }
}
