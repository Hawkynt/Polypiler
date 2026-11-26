using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live <c>g++ -Wall -Wextra -Werror</c> tests across C++17, C++20, and C++23 for closures/lambdas
/// (<c>09-lambdas-and-closures.cs</c>), collections (<c>12-collections.cs</c>), iterators/yield
/// (<c>13-iterators-and-yield.cs</c>), async tasks (<c>16-tasks-and-async.cs</c>), and spans,
/// pointers, and BMI2 bit permutations (<c>18-spans-and-pointers.cs</c>).
/// </summary>
[TestFixture]
public sealed class CppClosuresCollectionsAsyncViewsTests {

  [Test]
  public void Showcase09LambdasAndClosures_CompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("09-lambdas-and-closures.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty, profile.Name);

    var expected = (int)Eval(mod, "Run", 10, 5)!;
    Assert.That(expected, Is.EqualTo(45));

    var driver = """
      #include "closures_showcase.hpp"
      #include <cassert>
      using namespace closures_showcase;
      int main() {
        assert(run(10, 5) == 45);
        assert(run(0, 3) == 9);
        assert(run(-4, 10) == 18);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void LambdasFunctionRefsBoundMethodsAndPerIterationCaptures_RunAcrossAllStandards() {
    var src = """
      using System;
      using System.Collections.Generic;

      public class Adder {
        public int BaseVal;
        public Adder(int b) { this.BaseVal = b; }
        public virtual int Add(int x) => this.BaseVal + x;
        public static int DoubleIt(int x) => x * 2;
      }

      public class SubAdder : Adder {
        public SubAdder(int b) : base(b) { }
        public override int Add(int x) => base.Add(x) + 100;
      }

      public static class ClosureSuite {
        private static int Square(int x) => x * x;

        public static int RunAll(int seed) {
          Func<int, int> sq = Square;
          var a = new Adder(seed);
          Func<int, int> inst = a.Add;
          Func<int, int> stat = Adder.DoubleIt;
          var sub = new SubAdder(seed);
          Func<int, int> subDel = sub.Add;

          var list = new List<Func<int>>();
          for (int i = 0; i < 4; ++i) {
            int captured = i;
            list.Add(() => captured * 10);
          }

          int loopSum = 0;
          foreach (var fn in list)
            loopSum += fn();

          Action bumpSeed = () => { seed += 1; };
          bumpSeed();

          Func<int, int, int> nested = (p, unused) => {
            Func<int> inner = () => p + seed;
            return inner();
          };

          return sq(3) + inst(4) + stat(5) + subDel(2) + loopSum + nested(7, 99);
        }
      }
      """;
    var mod = ImportCSharp(src);
    var expected = (int)Eval(mod, "RunAll", 10)!;

    var driver = $$"""
      #include "closure_suite.hpp"
      #include <cassert>
      using namespace closure_suite;
      int main() {
        assert(run_all(10) == {{expected}});
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void NullDelegateInvoke_TrapsWithRuntimeError() {
    var fnType = IrType.Function(new IrFunctionType([IrType.I32], IrType.I32));
    var f = L("f", fnType);
    var mod = new IrModule("NullInvoke", [
      Function("callNull", IrType.I32, [],
        new IrDeclare(f, new IrNull(fnType)),
        new IrReturn(new IrInvoke(IrType.I32, R(f), [I(5)])))
    ]);
    var driver = """
      #include "null_invoke.hpp"
      #include <cassert>
      #include <stdexcept>
      int main() {
        bool caught = false;
        try {
          (void)null_invoke::call_null();
        } catch (const std::runtime_error&) {
          caught = true;
        }
        assert(caught);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void Showcase12Collections_CompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("12-collections.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty, profile.Name);

    var r1 = (int)Eval(mod, "Run", new[] { 1, -2, 1, 3, -2, 5 })!;
    var r2 = (int)Eval(mod, "Run", Array.Empty<int>())!;
    var r3 = (int)Eval(mod, "Run", new[] { -1, -2, -3 })!;

    var driver = $$"""
      #include "collections_showcase.hpp"
      #include <cassert>
      #include <vector>
      using namespace collections_showcase;
      int main() {
        assert(run(std::vector<std::int32_t>{1, -2, 1, 3, -2, 5}) == {{r1}});
        assert(run(std::vector<std::int32_t>{}) == {{r2}});
        assert(run(std::vector<std::int32_t>{-1, -2, -3}) == {{r3}});
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void MapsSetsSlotReuseInsertionAndEdgeCases_RunAcrossAllStandards() {
    var slotMapType = IrType.Map(IrType.I32, IrType.I32, MapOrder.SlotReuse);
    var insMapType = IrType.Map(IrType.F64, IrType.I32, MapOrder.Insertion);
    var slotSetType = IrType.Set(IrType.I32, MapOrder.SlotReuse);
    var insSetType = IrType.Set(IrType.I32, MapOrder.Insertion);

    var sm = L("sm", slotMapType);
    var im = L("im", insMapType);
    var ss = L("ss", slotSetType);
    var iset = L("iset", insSetType);
    var k = L("k", IrType.I32);
    var v = L("v", IrType.I32);
    var acc = L("acc", IrType.I32);
    var sk = L("sk", IrType.I32);

    // SlotReuse order spec: add 1,2,3; remove 1,3; add 4,5,6 => enumerates 5,2,4,6!
    var mod = new IrModule("MapSetSuite", [
      Function("slotReuseOrder", IrType.I32, [],
        new IrDeclare(sm, new IrCollection(IrCollectionOp.MapNew, slotMapType, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(sm), I(1), I(10)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(sm), I(2), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(sm), I(3), I(30)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(sm), I(1)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(sm), I(3)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(sm), I(2), I(99)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(sm), I(4), I(40)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(sm), I(5), I(50)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(sm), I(6), I(60)])),
        new IrDeclare(acc, I(0)),
        new IrForEachCollection(R(sm), EnumerationMode.Checked, k, v, Block(
          new IrAssign(acc, Bin(IrBinaryOp.Add, Bin(IrBinaryOp.Multiply, R(acc), I(10)), R(k))))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapClear, IrType.Void, [R(sm)])),
        new IrReturn(Bin(IrBinaryOp.Add, R(acc), new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(sm)])))),

      Function("floatMapAndSets", IrType.I32, [],
        new IrDeclare(im, new IrCollection(IrCollectionOp.MapNew, insMapType, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(im), F64(double.NaN), I(7)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(im), F64(-0.0), I(3)])),
        new IrDeclare(ss, new IrCollection(IrCollectionOp.SetNew, slotSetType, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(ss), I(1)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(ss), I(2)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(ss), I(1)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(ss), I(3)])),
        new IrDeclare(iset, new IrCollection(IrCollectionOp.SetNew, insSetType, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(iset), I(10)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(iset), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(iset), I(10)])),
        new IrDeclare(acc, Bin(IrBinaryOp.Add,
          new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(im), F64(double.NaN)]),
          new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(im), F64(0.0)]))),
        new IrForEachCollection(R(ss), EnumerationMode.Checked, sk, null, Block(
          new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(sk))))),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetClear, IrType.Void, [R(ss)])),
        new IrReturn(Bin(IrBinaryOp.Add, R(acc), new IrConditional(
          new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [R(iset), I(20)]),
          new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(iset)]),
          I(0))))),

      Function("mapArgMut", IrType.Bool, [L("m", slotMapType)],
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(L("m", slotMapType)), I(9), I(90)])),
        new IrReturn(new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [R(L("m", slotMapType)), I(9)])))
    ]);

    Assert.That(Eval(mod, "slotReuseOrder"), Is.EqualTo(5246));
    Assert.That(Eval(mod, "floatMapAndSets"), Is.EqualTo(16));

    var driver = """
      #include "map_set_suite.hpp"
      #include <cassert>
      using namespace map_set_suite;
      int main() {
        assert(slot_reuse_order() == 5246);
        assert(float_map_and_sets() == 16);
        PlyMap<std::int32_t, std::int32_t, true> m{};
        assert(map_arg_mut(m) == true);
        assert(m.count() == 1 && m.get(9) == 90);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void Showcase13IteratorsAndYield_CompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("13-iterators-and-yield.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty, profile.Name);

    Assert.That(Eval(mod, "SumEvens", 10), Is.EqualTo(30));
    Assert.That(Eval(mod, "SumEvens", 200), Is.EqualTo(2550));

    var driver = """
      #include "iterators_showcase.hpp"
      #include <cassert>
      using namespace iterators_showcase;
      int main() {
        assert(sum_evens(10) == 30);
        assert(sum_evens(200) == 2550);
        assert(sum_evens(-1) == 0);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void IteratorsRestartabilityYieldFromAndArraySequence_RunAcrossAllStandards() {
    var seqI32 = IrType.Sequence(IrType.I32, true);
    var n = L("n", IrType.I32);
    var arr = L("arr", IrType.Array(IrType.I32));
    var s = L("s", seqI32);
    var x = L("x", IrType.I32);
    var sum = L("sum", IrType.I32);

    var mod = new IrModule("IterSuite", [
      Function("countDown", seqI32, [n],
        new IrWhile(Bin(IrBinaryOp.Greater, R(n), I(0)), Block(
          new IrYield(R(n)),
          new IrAssign(n, Bin(IrBinaryOp.Subtract, R(n), I(1)))))),
      Function("chain", seqI32, [arr],
        new IrYieldFrom(new IrArraySequence(R(arr))),
        new IrYieldFrom(new IrCall("countDown", seqI32, [I(2)])),
        new IrYieldBreak()),
      Function("sumTwice", IrType.I32, [arr],
        new IrDeclare(s, new IrCall("chain", seqI32, [R(arr)])),
        new IrDeclare(sum, I(0)),
        new IrForEachSequence(x, R(s), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))))),
        new IrForEachSequence(x, R(s), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))))),
        new IrReturn(R(sum)))
    ]);

    var expected = (int)Eval(mod, "sumTwice", new[] { 10, 20 })!;
    Assert.That(expected, Is.EqualTo(66));

    var driver = """
      #include "iter_suite.hpp"
      #include <cassert>
      #include <vector>
      using namespace iter_suite;
      int main() {
        assert(sum_twice(std::vector<std::int32_t>{10, 20}) == 66);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void Showcase16TasksAndAsync_CompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("16-tasks-and-async.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty, profile.Name);

    var driver = """
      #include "tasks_showcase.hpp"
      #include <cassert>
      using namespace tasks_showcase;
      int main() {
        assert(combine_async(3, 4).await() == 27);
        assert(combine_async(0, 0).await() == 2);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void AsyncVoidCompletedAndFaultPropagation_RunAcrossAllStandards() {
    var x = L("x", IrType.I32);
    var fail = Function("failAsync", false, IrType.Async(IrType.I32), [x],
      new IrIf(Bin(IrBinaryOp.Less, R(x), I(0)),
        Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.ArgumentException), IrExceptionClass.ArgumentException.Constructors[1], [new IrStrConst("neg")]))),
        null),
      new IrReturn(Bin(IrBinaryOp.Multiply, R(x), I(2))));
    var noop = Function("noopAsync", true, IrType.Async(IrType.Void), [],
      new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []))));
    var bothVoid = Function("bothVoid", true, IrType.Async(IrType.Void), [],
      new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [
        new IrCall("noopAsync", IrType.Async(IrType.Void), []),
        new IrCall("noopAsync", IrType.Async(IrType.Void), [])
      ]))),
      new IrReturn(null));
    var checkFault = Function("checkFault", true, IrType.Async(IrType.I32), [x],
      new IrReturn(new IrAwait(new IrCall("failAsync", IrType.Async(IrType.I32), [R(x)]))));

    var mod = new IrModule("AsyncSuite", [fail, noop, bothVoid, checkFault]);
    var driver = """
      #include "async_suite.hpp"
      #include <cassert>
      #include <stdexcept>
      using namespace async_suite;
      int main() {
        noop_async().await();
        both_void().await();
        assert(check_fault(5).await() == 10);
        bool caught = false;
        try {
          (void)check_fault(-1).await();
        } catch (const std::invalid_argument&) {
          caught = true;
        }
        assert(caught);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void Showcase18SpansAndPointers_CompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("18-spans-and-pointers.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty, profile.Name);

    var pextVal = (uint)Eval(mod, "ExtractBits", 0xF0A5_1234u, 0x0FF0_00FFu)!;
    var sumVal = (int)Eval(mod, "SumSlice", new[] { 10, 20, 30, 40, 50 }, 1, 3)!;
    Assert.That(sumVal, Is.EqualTo(90));

    var driver = $$"""
      #include "low_level_showcase.hpp"
      #include <cassert>
      #include <vector>
      using namespace low_level_showcase;
      int main() {
        assert(extract_bits(0xF0A51234u, 0x0FF000FFu) == {{pextVal}}u);
        assert(extract_bits(0xFFFFFFFFu, 0x55555555u) == 0xFFFFu);
        assert(sum_slice(std::vector<std::int32_t>{10, 20, 30, 40, 50}, 1, 3) == 90);
        assert(sum_slice(std::vector<std::int32_t>{10, 20, 30}, 0, 0) == 0);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void SpansPointersMemoryAndBitDeposit_AllOperationsRunAcrossAllStandards() {
    var spanI32 = IrType.Span(IrType.I32);
    var roSpanI32 = IrType.ReadOnlySpan(IrType.I32);
    var memI32 = IrType.Memory(IrType.I32);
    var roMemI32 = IrType.ReadOnlyMemory(IrType.I32);
    var ptrI32 = IrType.Pointer(IrType.I32);
    var ptrU8 = IrType.Pointer(IrType.U8);

    var src = L("src", IrType.U32);
    var mask = L("mask", IrType.U32);
    var data = L("data", IrType.Array(IrType.I32));
    var sp = L("sp", spanI32);
    var sub = L("sub", spanI32);
    var stack = L("stack", spanI32);
    var ro = L("ro", roSpanI32);
    var mem = L("mem", memI32);
    var rmem = L("rmem", roMemI32);
    var bytes = L("bytes", IrType.Span(IrType.U8));
    var localVal = L("localVal", IrType.I32);
    var p = L("p", ptrI32);
    var p2 = L("p2", ptrI32);
    var q = L("q", ptrI32);
    var q2 = L("q2", ptrI32);
    var spPtr = L("spPtr", ptrI32);
    var bp = L("bp", ptrU8);
    var bp2 = L("bp2", ptrU8);
    var outArr = L("outArr", IrType.Array(IrType.I32));
    var ok = L("ok", IrType.Bool);
    var eq = L("eq", IrType.Bool);
    var diff = L("diff", IrType.I64);

    var mod = new IrModule("ViewsPtrs", [
      Function("pdep32", IrType.U32, [src, mask],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U32, [R(src), R(mask)]))),

      Function("spanOps", IrType.I32, [data],
        new IrDeclare(sp, new IrIntrinsic(IrIntrinsicOp.ViewOf, spanI32, [R(data)])),
        new IrDeclare(sub, new IrIntrinsic(IrIntrinsicOp.Slice, spanI32, [R(sp), I(1), I(2)])),
        new IrDeclare(stack, new IrIntrinsic(IrIntrinsicOp.StackAlloc, spanI32, [I(2)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [R(stack), I(7)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [R(sub), R(stack)])),
        new IrDeclare(ok, new IrIntrinsic(IrIntrinsicOp.ViewTryCopy, IrType.Bool, [R(sp), R(stack)])),
        new IrDeclare(ro, new IrIntrinsic(IrIntrinsicOp.ViewOf, roSpanI32, [R(stack)])),
        new IrDeclare(eq, new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(sub), R(ro)])),
        new IrDeclare(mem, new IrIntrinsic(IrIntrinsicOp.ViewOf, memI32, [R(data), I(1)])),
        new IrDeclare(rmem, new IrIntrinsic(IrIntrinsicOp.ViewOf, roMemI32, [R(mem)])),
        new IrDeclare(outArr, new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(IrType.I32), [R(rmem)])),
        new IrDeclare(bytes, new IrIntrinsic(IrIntrinsicOp.Reinterpret, IrType.Span(IrType.U8), [R(stack)])),
        new IrStore(R(stack), I(0), I(99)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrArrayLoad(R(stack), I(0)),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(bytes)]),
            Bin(IrBinaryOp.Add,
              new IrArrayLength(R(outArr)),
              new IrConditional(Bin(IrBinaryOp.LogicalAnd, new IrUnary(IrUnaryOp.LogicalNot, R(ok)), R(eq)), I(1), I(0))))))),

      Function("pointerOps", IrType.I32, [data],
        new IrDeclare(localVal, I(11)),
        new IrDeclare(p, new IrIntrinsic(IrIntrinsicOp.AddressOf, ptrI32, [R(localVal)])),
        new IrDeclare(p2, R(p)),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(p), I(22)])),
        new IrDeclare(spPtr, new IrIntrinsic(IrIntrinsicOp.StackAlloc, ptrI32, [I(3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(spPtr), I(5)])),
        new IrDeclare(q, new IrIntrinsic(IrIntrinsicOp.Pin, ptrI32, [R(data), I(1)])),
        new IrDeclare(q2, new IrIntrinsic(IrIntrinsicOp.PointerAdd, ptrI32, [R(q), I(1)])),
        new IrDeclare(diff, new IrIntrinsic(IrIntrinsicOp.PointerDifference, IrType.I64, [
          new IrIntrinsic(IrIntrinsicOp.PointerAdd, ptrI32, [R(q), I(2)]),
          R(q)
        ])),
        new IrDeclare(bp, new IrIntrinsic(IrIntrinsicOp.PointerCast, ptrU8, [R(p)])),
        new IrDeclare(bp2, R(bp)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(p)]),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(spPtr)]),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.I32, R(diff)),
              new IrConditional(
                Bin(IrBinaryOp.LogicalAnd,
                  new IrIntrinsic(IrIntrinsicOp.PointerEqual, IrType.Bool, [R(p), R(p2)]),
                  Bin(IrBinaryOp.LogicalAnd,
                    new IrIntrinsic(IrIntrinsicOp.PointerNotEqual, IrType.Bool, [R(p), R(q)]),
                    Bin(IrBinaryOp.LogicalAnd,
                      new IrIntrinsic(IrIntrinsicOp.PointerLess, IrType.Bool, [R(q), R(q2)]),
                      Bin(IrBinaryOp.LogicalAnd,
                        new IrIntrinsic(IrIntrinsicOp.PointerLessOrEqual, IrType.Bool, [R(q), R(q2)]),
                        Bin(IrBinaryOp.LogicalAnd,
                          new IrIntrinsic(IrIntrinsicOp.PointerGreater, IrType.Bool, [R(q2), R(q)]),
                          new IrIntrinsic(IrIntrinsicOp.PointerGreaterOrEqual, IrType.Bool, [R(bp), R(bp2)])))))),
                I(1), I(0)))))))
    ]);

    var pdepExpected = (uint)Eval(mod, "pdep32", 0x0000_00A5u, 0x00FF_0F0Fu)!;
    var spanExpected = (int)Eval(mod, "spanOps", new[] { 10, 20, 30, 40 })!;
    var ptrExpected = (int)Eval(mod, "pointerOps", new[] { 10, 20, 30, 40 })!;

    var driver = $$"""
      #include "views_ptrs.hpp"
      #include <cassert>
      #include <vector>
      using namespace views_ptrs;
      int main() {
        assert(pdep32(0x000000A5u, 0x00FF0F0Fu) == {{pdepExpected}}u);
        std::vector<std::int32_t> v{10, 20, 30, 40};
        assert(span_ops(v) == {{spanExpected}});
        assert(pointer_ops(v) == {{ptrExpected}});
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void CapturedLoopVarsCtorParamsObjectMapsAndGenericEquals_RunAcrossAllStandards() {
    var box = new IrClassType("Box");
    var valField = new IrField("Val", IrType.I32, false);
    box.Define([valField]);
    box.Inherit(null, []);
    var boxType = IrType.Object(box);
    var ctorThis = L("this", boxType);
    var ctorInit = L("init", IrType.I32);
    var ctor = new IrConstructor("Box..ctor");
    var getFn = new IrMethod("Get", "Box.Get", false);
    box.DefineMembers([ctor], [getFn], [], []);

    var boxCtorFn = Function("Box..ctor", false, IrType.Void, [ctorThis, ctorInit],
      new IrDeclare(L("f", IrType.Function(new IrFunctionType([], IrType.I32))),
        IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(ctorInit))))),
      new IrFieldStore(R(ctorThis), valField, new IrInvoke(IrType.I32, R(L("f", IrType.Function(new IrFunctionType([], IrType.I32)))), [])),
      new IrReturn(null));
    var getThis = L("this", boxType);
    var boxGetFn = Function("Box.Get", false, IrType.I32, [getThis],
      new IrReturn(new IrFieldLoad(R(getThis), valField)));

    var fnI32 = IrType.Function(new IrFunctionType([], IrType.I32));
    var fnList = IrType.List(fnI32);
    var objList = IrType.List(boxType);
    var objMap = IrType.Map(IrType.I32, boxType, MapOrder.Insertion);

    var funcs = L("funcs", fnList);
    var i = L("i", IrType.I32);
    var nums = L("nums", IrType.List(IrType.I32));
    var el = L("el", IrType.I32);
    var mp = L("mp", IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion));
    var mk = L("mk", IrType.I32);
    var mv = L("mv", IrType.I32);
    var sqEl = L("sqEl", IrType.I32);
    var fn = L("fn", fnI32);
    var total = L("total", IrType.I32);
    var ol = L("ol", objList);
    var popped = L("popped", boxType);
    var om = L("om", objMap);
    var found = L("found", boxType);
    var miss = L("miss", boxType);
    var fa = L("fa", IrType.F64);
    var fb = L("fb", IrType.F64);

    var mod = new IrModule("EdgeClosures", [
      boxCtorFn,
      boxGetFn,
      Function("runEdges", IrType.I32, [],
        new IrDeclare(funcs, new IrCollection(IrCollectionOp.ListNew, fnList, [])),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), I(3)),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1)))),
          Block(new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [
            R(funcs), IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(i))))
          ])))) { PerIteration = [i] },
        new IrDeclare(nums, new IrCollection(IrCollectionOp.ListOf, IrType.List(IrType.I32), [I(10)])),
        new IrForEachCollection(R(nums), EnumerationMode.Live, el, null, Block(
          new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [
            R(funcs), IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(el))))
          ])))),
        new IrDeclare(mp, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mp), I(2), I(20)])),
        new IrForEachCollection(R(mp), EnumerationMode.Live, mk, mv, Block(
          new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [
            R(funcs), IrLambda.Of([], IrType.I32, [], Block(new IrReturn(Bin(IrBinaryOp.Add, R(mk), R(mv)))))
          ])))),
        new IrForEachSequence(sqEl, new IrArraySequence(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(5)])), Block(
          new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [
            R(funcs), IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(sqEl))))
          ])))),
        new IrDeclare(total, I(0)),
        new IrForEachCollection(R(funcs), EnumerationMode.Live, fn, null, Block(
          new IrAssign(total, Bin(IrBinaryOp.Add, R(total), new IrInvoke(IrType.I32, R(fn), []))))),
        new IrDeclare(ol, new IrCollection(IrCollectionOp.ListNew, objList, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(ol), new IrNew(boxType, ctor, [I(7)])])),
        new IrDeclare(popped, new IrCollection(IrCollectionOp.ListRemoveLast, boxType, [R(ol)])),
        new IrDeclare(om, new IrCollection(IrCollectionOp.MapNew, objMap, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(om), I(1), R(popped)])),
        new IrDeclare(found, new IrCollection(IrCollectionOp.MapTryGet, boxType, [R(om), I(1)])),
        new IrDeclare(miss, new IrCollection(IrCollectionOp.MapTryGet, boxType, [R(om), I(9)])),
        new IrReturn(Bin(IrBinaryOp.Add, R(total), Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, getFn, R(found), []),
          new IrConditional(new IrIsNull(R(miss)), I(1), I(0)))))),
      Function("genEq", IrType.Bool, [fa, fb],
        new IrReturn(Bin(IrBinaryOp.LogicalAnd,
          new IrGenericEquals(R(fa), R(fb)),
          new IrGenericEquals(I(3), I(3)))))
    ]) { Types = [box] };

    var expected = (int)Eval(mod, "runEdges")!;
    Assert.That(Eval(mod, "genEq", double.NaN, double.NaN), Is.EqualTo(true));
    Assert.That(Eval(mod, "genEq", 1.0, 2.0), Is.EqualTo(false));

    var driver = $$"""
      #include "edge_closures.hpp"
      #include <cassert>
      #include <limits>
      using namespace edge_closures;
      int main() {
        assert(run_edges() == {{expected}});
        const double nan = std::numeric_limits<double>::quiet_NaN();
        assert(gen_eq(nan, nan) == true);
        assert(gen_eq(1.0, 2.0) == false);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

}
