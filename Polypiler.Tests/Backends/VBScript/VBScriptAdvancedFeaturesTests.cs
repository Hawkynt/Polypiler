using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.VBScript.VBScriptTestHost;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>
/// Live <c>wine cscript</c> tests for VBScript 5.8 emission of lambdas &amp; closures, pure-VBScript
/// collections, iterators &amp; lazy sequences, async/await tasks, spans/memory, bounded pointers,
/// and BMI2 bit permutations (covering all 5 PLY2651 showcase files plus edge cases).
/// </summary>
[TestFixture]
public sealed class VBScriptAdvancedFeaturesTests {

  [Test]
  public void Showcase09LambdasAndClosures_AndShowcase10Callbacks_RunLive() {
    var mod09 = ImportShowcase("09-lambdas-and-closures.cs");
    Assert.That(VBScriptEmitter.Check(mod09), Is.Empty);
    var vbs09 = LegalizedEmit(mod09);
    var lines09 = RunLines(vbs09, """
      WScript.Echo Run(10, 5)
      WScript.Echo Run(0, 3)
      WScript.Echo Run(-4, 10)
      """);
    Assert.That(lines09, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod09, "Run", [10, 5])),
      FormatExpected(IrInterpreter.Call(mod09, "Run", [0, 3])),
      FormatExpected(IrInterpreter.Call(mod09, "Run", [-4, 10]))
    }));
    Assert.That(lines09[0], Is.EqualTo("45"));

    var mod10 = ImportShowcase("10-callbacks-and-delegates.cs");
    Assert.That(VBScriptEmitter.Check(mod10), Is.Empty);
    var vbs10 = LegalizedEmit(mod10);
    var lines10 = RunLines(vbs10, "WScript.Echo Run(Array(1, 2, 3), 2)");
    Assert.That(lines10, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod10, "Run", [new[] { 1, 2, 3 }, 2]))
    }));
  }

  [Test]
  public void Closures_NestedCaptures_PerIteration_BoundMethods_VoidActions_AndNullInvoke_RunLive() {
    var mod = ImportCSharpRaw("""
      using System;

      public sealed class Accumulator {
        private int _total;
        public Accumulator(int initial) { this._total = initial; }
        public int Add(int x) { this._total += x; return this._total; }
        public void Reset() { this._total = 0; }
        public static int Triple(int x) => x * 3;
      }

      public static class ClosureEdges {
        public static int NestedAndParamCapture(int seed) {
          Func<int, Func<int, int>> make = a => {
            int acc = seed + a;
            return b => {
              acc += b;
              return acc;
            };
          };
          var f = make(10);
          int r1 = f(2);
          int r2 = f(3);
          return r1 + r2;
        }

        public static int BoundAndVoidAction(int start) {
          var acc = new Accumulator(start);
          Func<int, int> adder = acc.Add;
          Action reset = acc.Reset;
          Func<int, int> triple = Accumulator.Triple;
          int a = adder(5);
          reset();
          int b = adder(2);
          return a + b + triple(4);
        }

        public static int CapturedArrayAndNullDelegate() {
          int[] buf = new int[] { 1, 2 };
          Action bump = () => { buf[0] = buf[0] + 10; };
          bump();
          Func<int, int>? missing = null;
          int caught = 0;
          try {
            caught = missing!(1);
          } catch (NullReferenceException) {
            caught = 7;
          }
          return buf[0] + caught;
        }
      }
      """);

    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = LegalizedEmit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo NestedAndParamCapture(5)
      WScript.Echo BoundAndVoidAction(10)
      WScript.Echo CapturedArrayAndNullDelegate()
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "NestedAndParamCapture", [5])),
      FormatExpected(IrInterpreter.Call(mod, "BoundAndVoidAction", [10])),
      FormatExpected(IrInterpreter.Call(mod, "CapturedArrayAndNullDelegate", []))
    }));

    // Also test IrFor.PerIteration capture semantics and static IrBoundMethod/IrFunctionRef void directly via IR.
    var i = L("i", IrType.I32);
    var fns = L("fns", IrType.List(IrType.Function(IrType.I32)));
    var fnItem = L("fn", IrType.Function(IrType.I32));
    var sum = L("sum", IrType.I32);
    var perIterMod = new IrModule("PerIter", [
      Function("voidStatic", false, IrType.Void, [i]),
      Function("testPerIter", IrType.I32, [],
        new IrDeclare(fns, new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.Function(IrType.I32)), [])),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), I(3)),
          Block(new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [
            R(fns),
            IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(i))))
          ]))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))) { PerIteration = [i] },
        new IrDeclare(sum, I(0)),
        new IrForEachCollection(R(fns), EnumerationMode.Checked, fnItem, null,
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrInvoke(IrType.I32, R(fnItem), []))))),
        new IrExprStmt(new IrInvoke(IrType.Void, new IrFunctionRef(IrType.Function(IrType.Void, IrType.I32), "voidStatic"), [I(1)])),
        new IrReturn(R(sum)))
    ]);
    Assert.That(VBScriptEmitter.Check(perIterMod), Is.Empty);
    var perIterVbs = Emit(perIterMod);
    Assert.That(RunLines(perIterVbs, "WScript.Echo testPerIter()"), Is.EqualTo(new[] { "3" }));
  }

  [Test]
  public void Showcase12Collections_TranslatesAndRunsLive() {
    var mod = ImportShowcase("12-collections.cs");
    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = LegalizedEmit(mod);
    Assert.That(vbs, Does.Not.Contain("CreateObject"), "Collections must be 100% pure VBScript without COM dependencies.");

    var lines = RunLines(vbs, """
      WScript.Echo Run(Array(1, -2, 1, 3, -2, 0))
      WScript.Echo Run(Array())
      WScript.Echo Run(Array(-5, -5, -1))
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "Run", [new[] { 1, -2, 1, 3, -2, 0 }])),
      FormatExpected(IrInterpreter.Call(mod, "Run", [Array.Empty<int>()])),
      FormatExpected(IrInterpreter.Call(mod, "Run", [new[] { -5, -5, -1 }]))
    }));
  }

  [Test]
  public void Collections_AllListMapSetOps_AndForEachModes_RunLive() {
    var st = new IrStructType("Pair");
    var sf = new IrField("X", IrType.I32, false);
    st.Define([sf]);
    var stT = IrType.Struct(st);

    var listI = L("li", IrType.List(IrType.I32));
    var listS = L("ls", IrType.List(stT));
    var mapI = L("mi", IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse));
    var mapS = L("ms", IrType.Map(IrType.I32, stT, MapOrder.Insertion));
    var setI = L("si", IrType.Set(IrType.I32, MapOrder.SlotReuse));
    var arrI = L("ai", IrType.Array(IrType.I32));
    var el = L("el", IrType.I32);
    var kvK = L("k", IrType.String);
    var kvV = L("v", IrType.I32);
    var acc = L("acc", IrType.I32);
    var popped = L("pop", IrType.Optional(IrType.I32));
    var optSt = L("optSt", IrType.Optional(stT));

    var mod = new IrModule("ColOps", [
      Function("exerciseAll", IrType.I32, [],
        new IrDeclare(listI, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10), I(20), I(30)])])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [R(listI), I(1), I(25)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [R(listI), I(1), I(15)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [R(listI), I(0)])),
        new IrDeclare(popped, new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(listI)])),
        new IrDeclare(arrI, new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), [R(listI)])),
        new IrDeclare(acc, Bin(IrBinaryOp.Add,
          new IrUnwrap(R(popped)),
          Bin(IrBinaryOp.Add,
            new IrCollection(IrCollectionOp.ListGet, IrType.I32, [R(listI), I(0)]),
            Bin(IrBinaryOp.Add,
              new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(listI), I(25)]),
              Bin(IrBinaryOp.Add,
                new IrCollection(IrCollectionOp.ListIndexOfStrict, IrType.I32, [R(listI), I(25)]),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(listI), I(15)]), I(1), I(0)),
                  Bin(IrBinaryOp.Add,
                    new IrConditional(new IrCollection(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, [R(listI), I(15)]), I(1), I(0)),
                    new IrArrayLength(R(arrI))))))))),
        new IrForEachCollection(R(listI), EnumerationMode.Live, el, null,
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(el), I(15)), Block(new IrContinue()), null),
            new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(el))),
            new IrBreak())),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [R(listI)])),
        // ListOf with structs
        new IrDeclare(listS, new IrCollection(IrCollectionOp.ListOf, IrType.List(stT), [new IrStructNew(stT, [I(4)]), new IrStructNew(stT, [I(9)])])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(listS), new IrStructNew(stT, [I(9)])]), I(5), I(0)))),
        // Map operations (SlotReuse + Insertion + struct value TryGet)
        new IrDeclare(mapI, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mapI), new IrStrConst("a"), I(1)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(mapI), new IrStrConst("b"), I(2)])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(mapI), new IrStrConst("b"), I(99)]), I(0), I(1)))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [R(mapI), new IrStrConst("a")]),
            new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(mapI), new IrStrConst("a")]), I(0)))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(mapI), new IrStrConst("a")]), I(1), I(0)))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mapI), new IrStrConst("c"), I(3)])),
        new IrForEachCollection(R(mapI), EnumerationMode.Checked, kvK, kvV,
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(kvK)]), R(kvV)))))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(mapI)]))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapClear, IrType.Void, [R(mapI)])),
        new IrDeclare(mapS, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.I32, stT, MapOrder.Insertion), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mapS), I(1), new IrStructNew(stT, [I(11)])])),
        new IrDeclare(optSt, new IrCollection(IrCollectionOp.MapTryGet, IrType.Optional(stT), [R(mapS), I(1)])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), new IrFieldLoad(new IrUnwrap(R(optSt)), sf))),
        // Set operations
        new IrDeclare(setI, new IrCollection(IrCollectionOp.SetNew, IrType.Set(IrType.I32, MapOrder.SlotReuse), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(setI), I(7)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(setI), I(8)])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [R(setI), I(7)]), I(1), I(0)))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConditional(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(setI), I(7)]), I(1), I(0)))),
        new IrForEachCollection(R(setI), EnumerationMode.Checked, el, null,
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(el))))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(setI)]))),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetClear, IrType.Void, [R(setI)])),
        new IrReturn(R(acc)))
    ]) { Types = [st] };

    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = Emit(mod);
    var lines = RunLines(vbs, "WScript.Echo exerciseAll()");
    Assert.That(lines, Is.EqualTo(new[] { FormatExpected(IrInterpreter.Call(mod, "exerciseAll", [])) }));
  }

  [Test]
  public void Showcase13IteratorsAndYield_AndShowcase14Linq_AndSinglePassYieldFrom_RunLive() {
    var mod13 = ImportShowcase("13-iterators-and-yield.cs");
    Assert.That(VBScriptEmitter.Check(mod13), Is.Empty);
    var vbs13 = LegalizedEmit(mod13);
    var lines13 = RunLines(vbs13, """
      WScript.Echo SumEvens(10)
      WScript.Echo SumEvens(200)
      WScript.Echo SumEvens(0)
      """);
    Assert.That(lines13, Is.EqualTo(new[] { "30", "2550", "0" }));
    Assert.That(lines13[0], Is.EqualTo(FormatExpected(IrInterpreter.Call(mod13, "SumEvens", [10]))));
    Assert.That(lines13[1], Is.EqualTo(FormatExpected(IrInterpreter.Call(mod13, "SumEvens", [200]))));

    var mod14 = ImportShowcase("14-linq.cs");
    var vbs14 = LegalizedEmit(mod14);
    var lines14 = RunLines(vbs14, "WScript.Echo TopSquaredSum(Array(-3, 2, 0, 3, 4, 5), 2)");
    Assert.That(lines14, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod14, "TopSquaredSum", [new[] { -3, 2, 0, 3, 4, 5 }, 2]))
    }));

    // Test restartability, yield from, IrArraySequence, try/finally with yield break, single-pass sequence, and break/continue.
    var n = L("n", IrType.I32);
    var x = L("x", IrType.I32);
    var seq = L("s", IrType.Sequence(IrType.I32, true));
    var spSeq = L("sp", IrType.Sequence(IrType.I32, false));
    var sum = L("sum", IrType.I32);
    var gLog = new IrGlobal("Log", IrType.I32, I(0));
    var seqMod = new IrModule("IterEdges", [
      Function("gen", false, IrType.Sequence(IrType.I32, true), [n],
        new IrTry(
          Block(
            new IrYield(R(n)),
            new IrYieldFrom(new IrArraySequence(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10), I(20)]))),
            new IrYieldBreak()),
          [],
          Block(new IrGlobalAssign(gLog, Bin(IrBinaryOp.Add, new IrGlobalRef(gLog), I(1)))))),
      Function("once", false, IrType.Sequence(IrType.I32, false), [],
        new IrYield(I(5)),
        new IrYield(I(6))),
      Function("runIter", IrType.I32, [],
        new IrDeclare(seq, new IrCall("gen", IrType.Sequence(IrType.I32, true), [I(1)])),
        new IrDeclare(sum, I(0)),
        // Enumerate restartable sequence TWICE with continue and break.
        new IrForEachSequence(x, R(seq), Block(
          new IrIf(Bin(IrBinaryOp.Equal, R(x), I(10)), Block(new IrContinue()), null),
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))))),
        new IrForEachSequence(x, R(seq), Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))),
          new IrIf(Bin(IrBinaryOp.Equal, R(x), I(10)), Block(new IrBreak()), null))),
        // Enumerate single-pass sequence twice: second pass yields nothing!
        new IrDeclare(spSeq, new IrCall("once", IrType.Sequence(IrType.I32, false), [])),
        new IrForEachSequence(x, R(spSeq), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))))),
        new IrForEachSequence(x, R(spSeq), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(x))))),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum), new IrGlobalRef(gLog))))
    ]) { Globals = [gLog] };

    Assert.That(VBScriptEmitter.Check(seqMod), Is.Empty);
    var seqVbs = Emit(seqMod);
    // First pass: 1 + 20 = 21 (log=1). Second pass: 1 + 10 = 11 (log=2). Once: 5 + 6 = 11. Total: 21 + 11 + 11 + 2 = 45.
    Assert.That(RunLines(seqVbs, "WScript.Echo runIter()"), Is.EqualTo(new[] { "45" }));
  }

  [Test]
  public void Showcase16TasksAndAsync_AndAsyncFaultVoidTryFinally_RunLive() {
    var mod16 = ImportShowcase("16-tasks-and-async.cs");
    Assert.That(VBScriptEmitter.Check(mod16), Is.Empty);
    var vbs16 = LegalizedEmit(mod16);
    var lines16 = RunLines(vbs16, """
      WScript.Echo ply_task_await(CombineAsync(3, 4))
      WScript.Echo ply_task_await(CombineAsync(0, 5))
      """);
    Assert.That(lines16, Is.EqualTo(new[] { "27", "27" }));
    Assert.That(lines16[0], Is.EqualTo(FormatExpected(IrInterpreter.Call(mod16, "CombineAsync", [3, 4]))));

    // Async void, CompletedTask, fault capture & propagation through await and WhenAll, and return inside try/finally!
    var edgeMod = ImportCSharpRaw("""
      using System;
      using System.Threading.Tasks;

      public static class AsyncEdges {
        private static int _cleanupCount = 0;

        private static async Task DoneAsync() {
          await Task.CompletedTask;
        }

        private static async Task<int> OkAsync(int v) {
          return await Task.FromResult(v);
        }

        private static async Task<int> FailAsync() {
          await Task.CompletedTask;
          throw new InvalidOperationException("boom");
        }

        private static async Task<int> ReturnInTryAsync(int x) {
          try {
            return await Task.FromResult(x + 1);
          } finally {
            _cleanupCount += 1;
          }
        }

        public static async Task<int> RunEdges() {
          _cleanupCount = 0;
          await DoneAsync();
          int ok = await ReturnInTryAsync(41);
          int caught = 0;
          try {
            int[] xs = await Task.WhenAll(OkAsync(1), FailAsync());
            caught = xs[0];
          } catch (InvalidOperationException) {
            caught = 100;
          }
          return ok + caught + _cleanupCount;
        }
      }
      """);

    Assert.That(VBScriptEmitter.Check(edgeMod), Is.Empty);
    var edgeVbs = LegalizedEmit(edgeMod);
    var edgeLines = RunLines(edgeVbs, "WScript.Echo ply_task_await(RunEdges())");
    Assert.That(edgeLines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(edgeMod, "RunEdges", []))
    }));
  }

  [Test]
  public void Showcase18SpansAndPointers_AndAllSpanPointerBitPermOps_RunLive() {
    var mod18 = ImportShowcase("18-spans-and-pointers.cs");
    Assert.That(VBScriptEmitter.Check(mod18), Is.Empty);
    var vbs18 = LegalizedEmit(mod18);
    var lines18 = RunLines(vbs18, """
      WScript.Echo ExtractBits(&H0F0F0F0F, &H00FF00FF)
      WScript.Echo ExtractBits(4294967295.0, &H55555555)
      WScript.Echo SumSlice(Array(10, 20, 30, 40, 50), 1, 3)
      WScript.Echo SumSlice(Array(10, 20, 30), 0, 0)
      """);
    Assert.That(lines18, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod18, "ExtractBits", [0x0F0F0F0Fu, 0x00FF00FFu])),
      FormatExpected(IrInterpreter.Call(mod18, "ExtractBits", [0xFFFFFFFFu, 0x55555555u])),
      FormatExpected(IrInterpreter.Call(mod18, "SumSlice", [new[] { 10, 20, 30, 40, 50 }, 1, 3])),
      FormatExpected(IrInterpreter.Call(mod18, "SumSlice", [new[] { 10, 20, 30 }, 0, 0]))
    }));

    // Exercise Span/Memory/Pointer mutation, AddressOf, StackAlloc, Slice, CopyTo, TryCopyTo, Fill,
    // SequenceEqual, ToArray, Reinterpret, endian-on-view, PointerAdd/Diff/Cmp/Cast, and PEXT/PDEP (8/16/32/64-bit).
    var edgeMod = ImportCSharpRaw("""
      using System;
      using System.Buffers.Binary;
      using System.Runtime.InteropServices;
      using System.Runtime.Intrinsics.X86;

      public static class LowLevelEdges {
        public static unsafe int MutateArrayThroughSpanAndPtr(int[] data) {
          Span<int> s = data.AsSpan(1, 3);
          s[0] = 100;
          s.Slice(1, 1).Fill(200);
          fixed (int* p = data) {
            *(p + 3) = 300;
            int* p1 = p + 1;
            int* p3 = p + 3;
            int diff = (int)(p3 - p1);
            bool cmp = (p1 < p3) && (p1 <= p3) && (p3 > p1) && (p3 >= p1) && (p1 != p3) && (p1 == p + 1);
            if (!cmp)
              return -1;
            return data[1] + data[2] + data[3] + diff;
          }
        }

        public static unsafe int AddressOfAndStackAlloc(int x) {
          int* px = &x;
          *px = *px + 10;
          int* sbuf = stackalloc int[3];
          sbuf[0] = x;
          sbuf[1] = 2;
          sbuf[2] = 3;
          Span<int> ss = stackalloc int[3];
          ss.Fill(5);
          Span<int> dst = new int[3];
          ss.CopyTo(dst);
          bool eq = ss.SequenceEqual(dst);
          bool failCopy = ss.TryCopyTo(dst.Slice(0, 2));
          int[] arr = dst.ToArray();
          return x + sbuf[1] + sbuf[2] + arr[0] + (eq ? 1 : 0) + (failCopy ? 100 : 0);
        }

        public static int ReinterpretAndEndianView() {
          int[] ints = new int[] { 0x04030201 };
          ReadOnlySpan<int> roInts = ints;
          ReadOnlySpan<byte> roBytes = MemoryMarshal.AsBytes(roInts);
          int b0 = roBytes[0];
          int b3 = roBytes[3];
          byte[] buf = new byte[4];
          Span<byte> bytes = buf.AsSpan();
          BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x1234);
          ushort readBack = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
          return b0 + b3 + readBack;
        }

        public static int BitPerms() {
          uint pext32 = Bmi2.ParallelBitExtract(0xF0F0u, 0x0FF0u);
          uint pdep32 = Bmi2.ParallelBitDeposit(0x00ABu, 0x0F0Fu);
          ulong pext64 = Bmi2.X64.ParallelBitExtract(0x0000_0001_0000_000FuL, 0x0000_0001_0000_000FuL);
          ulong pdep64 = Bmi2.X64.ParallelBitDeposit(0x1FuL, 0x0000_0001_0000_000FuL);
          return (int)pext32 + (int)pdep32 + (int)pext64 + (int)(pdep64 & 0xFFFFuL);
        }
      }
      """);

    Assert.That(VBScriptEmitter.Check(edgeMod), Is.Empty);
    var edgeVbs = LegalizedEmit(edgeMod);
    var edgeLines = RunLines(edgeVbs, """
      Dim d
      d = Array(1, 2, 3, 4, 5)
      WScript.Echo MutateArrayThroughSpanAndPtr(d)
      WScript.Echo d(1)
      WScript.Echo d(2)
      WScript.Echo d(3)
      WScript.Echo AddressOfAndStackAlloc(7)
      WScript.Echo ReinterpretAndEndianView()
      WScript.Echo BitPerms()
      """);
    Assert.That(edgeLines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(edgeMod, "MutateArrayThroughSpanAndPtr", [new[] { 1, 2, 3, 4, 5 }])),
      "100",
      "200",
      "300",
      FormatExpected(IrInterpreter.Call(edgeMod, "AddressOfAndStackAlloc", [7])),
      FormatExpected(IrInterpreter.Call(edgeMod, "ReinterpretAndEndianView", [])),
      FormatExpected(IrInterpreter.Call(edgeMod, "BitPerms", []))
    }));
  }

  [Test]
  public void AdditionalCoverage_NumericSpanTypes_VirtualBoundMethods_AsyncVoid_AndSteps_RunLive() {
    var st = new IrStructType("BoxVal");
    var sf = new IrField("V", IrType.I32, false);
    st.Define([sf]);
    var stT = IrType.Struct(st);

    var recCls = new IrClassType("RecObj") { IsRecord = true };
    var rf = new IrField("Id", IrType.I32, false);
    recCls.Define([rf]);
    recCls.Inherit(null, []);
    var recCtor = new IrConstructor("RecObj_ctor");
    var recGet = new IrMethod("GetId", "RecObj_GetId", false) { Dispatch = IrDispatch.Virtual };
    var recStatic = new IrMethod("Tag", "RecObj_Tag", true);
    recCls.DefineMembers([recCtor], [recGet, recStatic], [], []);

    var thisR = L("this", IrType.Object(recCls));
    var idP = L("id", IrType.I32);
    var arrP = L("arr", IrType.Array(IrType.I32));
    var a1 = L("a1", IrType.I32);
    var a2 = L("a2", IrType.I32);
    var locAddr = L("la", IrType.I32);
    var pLoc = L("pl", IrType.Pointer(IrType.I32));
    var pVoid = L("pv", IrType.Pointer(IrType.Void));
    var pIdx = L("pi", IrType.Pointer(IrType.I32));
    var pRaw = L("pr", IrType.Pointer(IrType.I32));
    var locArr = L("larr", IrType.Array(IrType.I32));
    var spDef = L("spDef", IrType.Span(IrType.I32));
    var spView = L("spView", IrType.Span(IrType.I32));
    var spSlice = L("spSlice", IrType.Span(IrType.I32));
    var spI8 = L("spI8", IrType.Span(IrType.I8));
    var spI16 = L("spI16", IrType.Span(IrType.I16));
    var spU16 = L("spU16", IrType.Span(IrType.U16));
    var spU32 = L("spU32", IrType.Span(IrType.U32));
    var spI64 = L("spI64", IrType.Span(IrType.I64));
    var spU64 = L("spU64", IrType.Span(IrType.U64));
    var spF32 = L("spF32", IrType.Span(IrType.F32));
    var spF64 = L("spF64", IrType.Span(IrType.F64));
    var spBytes = L("spBytes", IrType.Span(IrType.U8));
    var listRec = L("lrec", IrType.List(IrType.Object(recCls)));
    var listSt = L("lst", IrType.List(stT));
    var r1 = L("r1", IrType.Object(recCls));
    var fnVirt = L("fv", IrType.Function(IrType.I32));
    var fnStat = L("fs", IrType.Function(IrType.I32));
    var fnLam = L("fl", IrType.Function(IrType.Function(IrType.I32, IrType.I32), IrType.I32));
    var fnArr = L("fa", IrType.Function(IrType.I32, IrType.Array(IrType.I32)));
    var stParam = L("stp", stT);
    var arrIterP = L("aip", IrType.Array(IrType.I32));
    var itEl = L("itel", IrType.I32);
    var mapKV = L("mkv", IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion));
    var mapOnlyVal = L("mov", IrType.I32);
    var acc = L("acc", IrType.I32);

    var mod = new IrModule("AdvCov", [
      Function("RecObj_ctor", false, IrType.Void, [thisR, idP],
        new IrFieldStore(R(thisR), rf, R(idP))),
      Function("RecObj_GetId", false, IrType.I32, [thisR],
        new IrReturn(new IrFieldLoad(R(thisR), rf))),
      Function("RecObj_Tag", false, IrType.I32, [],
        new IrReturn(I(7))),
      Function("sumArr", false, IrType.I32, [arrP],
        new IrReturn(new IrArrayLoad(R(arrP), I(0)))),
      Function("asyncVoidEarly", false, IrType.Async(IrType.Void), [],
        new IrReturn(null)),
      Function("asyncWhenAllVoid", IrType.Async(IrType.I32), [],
        new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [
          new IrCall("asyncVoidEarly", IrType.Async(IrType.Void), []),
          new IrCall("asyncVoidEarly", IrType.Async(IrType.Void), [])
        ]))),
        new IrReturn(I(12))),
      Function("iterStructAndArr", false, IrType.Sequence(IrType.I32, true), [stParam, arrIterP],
        new IrYield(new IrFieldLoad(R(stParam), sf)),
        new IrYield(new IrArrayLoad(R(arrIterP), I(0)))),
      Function("coverAll", IrType.I32, [],
        // Uninitialized default Span<int> (then assigned an empty span before reading length in IrInterpreter)
        new IrDeclare(spDef, null),
        new IrAssign(spDef, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I32), [I(0)])),
        // Local AddressOf + IrAssign + IrStep on addr-boxed local + PointerCast to void* and back
        new IrDeclare(locAddr, I(5)),
        new IrDeclare(pLoc, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(locAddr)])),
        new IrDeclare(pVoid, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.Void), [R(pLoc)])),
        new IrAssign(locAddr, I(8)),
        new IrExprStmt(new IrStep(R(locAddr), true, false, false)),
        // Local array boxed by ViewOf + reassign + length + step + ViewOf on span + ViewOf on non-local array + Pin with index + Pin on non-local array
        new IrDeclare(locArr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10), I(20), I(30)])),
        new IrDeclare(spView, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(locArr)])),
        new IrDeclare(spSlice, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(spView), I(1), I(2)])),
        new IrExprStmt(new IrStep(new IrArrayLoad(R(spSlice), I(0)), true, false, false)),
        new IrExprStmt(new IrStep(new IrArrayLoad(R(locArr), I(0)), true, false, false)),
        new IrStore(R(locArr), I(2), I(35)),
        new IrDeclare(pIdx, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(locArr), I(1)])),
        new IrDeclare(pRaw, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(3), I(4)])])),
        new IrDeclare(acc, Bin(IrBinaryOp.Add,
          new IrArrayLength(R(locArr)),
          Bin(IrBinaryOp.Add,
            new IrCall("sumArr", IrType.I32, [R(locArr)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(pIdx)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(pRaw)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2)])])]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spDef)]),
                    new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.I32), [R(pVoid)])])))))))),
        new IrAssign(locArr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1)])),
        // All numeric span kinds (I8, I16, U16, U32, I64, U64, F32, F64) + BigEndian Load/Store on byte view
        new IrDeclare(spI8, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I8), [I(1)])),
        new IrDeclare(spI16, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I16), [I(1)])),
        new IrDeclare(spU16, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.U16), [I(1)])),
        new IrDeclare(spU32, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.U32), [I(1)])),
        new IrDeclare(spI64, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I64), [I(1)])),
        new IrDeclare(spU64, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.U64), [I(1)])),
        new IrDeclare(spF32, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.F32), [I(1)])),
        new IrDeclare(spF64, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.F64), [I(1)])),
        new IrDeclare(spBytes, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.U8), [I(4)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(spBytes), I(0), C(IrType.U16, 0x0102)])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.U16, [R(spBytes), I(0)])))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spI8)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spI16)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spU16)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spU32)]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spI64)]),
                    Bin(IrBinaryOp.Add,
                      new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spU64)]),
                      Bin(IrBinaryOp.Add,
                        new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spF32)]),
                        new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(spF64)])))))))))),
        // Virtual & static IrBoundMethod, lambda capturing its own param, lambda with array param
        new IrDeclare(r1, new IrNew(IrType.Object(recCls), recCtor, [I(13)])),
        new IrDeclare(fnVirt, new IrBoundMethod(IrType.Function(IrType.I32), R(r1), recGet) { Kind = IrMethodCallKind.Virtual }),
        new IrDeclare(fnStat, new IrBoundMethod(IrType.Function(IrType.I32), null, recStatic)),
        new IrDeclare(fnLam, IrLambda.Of([a1], IrType.Function(IrType.I32, IrType.I32), [],
          Block(new IrReturn(IrLambda.Of([a2], IrType.I32, [], Block(new IrReturn(Bin(IrBinaryOp.Add, R(a1), R(a2))))))))),
        new IrDeclare(fnArr, IrLambda.Of([arrP], IrType.I32, [], Block(new IrReturn(new IrArrayLoad(R(arrP), I(0)))))),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          Bin(IrBinaryOp.Add,
            new IrInvoke(IrType.I32, R(fnVirt), []),
            Bin(IrBinaryOp.Add,
              new IrInvoke(IrType.I32, R(fnStat), []),
              Bin(IrBinaryOp.Add,
                new IrInvoke(IrType.I32, new IrInvoke(IrType.Function(IrType.I32, IrType.I32), R(fnLam), [I(4)]), [I(5)]),
                new IrInvoke(IrType.I32, R(fnArr), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(6)])])))))),
        // Record list Contains, struct list IndexOfStrict / IncludesSameValueZero / CopyToArray / RemoveLast(object)
        new IrDeclare(listRec, new IrCollection(IrCollectionOp.ListOf, IrType.List(IrType.Object(recCls)), [R(r1)])),
        new IrDeclare(listSt, new IrCollection(IrCollectionOp.ListOf, IrType.List(stT), [new IrStructNew(stT, [I(2)])])),
        new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(listRec), new IrNew(IrType.Object(recCls), recCtor, [I(13)])]), I(1), I(0)),
            Bin(IrBinaryOp.Add,
              new IrCollection(IrCollectionOp.ListIndexOfStrict, IrType.I32, [R(listSt), new IrStructNew(stT, [I(2)])]),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrCollection(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, [R(listSt), new IrStructNew(stT, [I(2)])]), I(1), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrArrayLength(new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(stT), [R(listSt)])),
                  new IrFieldLoad(new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Object(recCls), [R(listRec)]), rf))))))),
        // Map foreach with Value only (Element == null)
        new IrDeclare(mapKV, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mapKV), I(1), I(9)])),
        new IrForEachCollection(R(mapKV), EnumerationMode.Live, null, mapOnlyVal,
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(mapOnlyVal))))),
        // Iterator with struct & array parameters + IrArraySequence of structs
        new IrForEachSequence(itEl, new IrCall("iterStructAndArr", IrType.Sequence(IrType.I32, true), [
          new IrStructNew(stT, [I(3)]),
          new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(4)])
        ]), Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(itEl))))),
        new IrForEachSequence(stParam, new IrArraySequence(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(stT), [new IrStructNew(stT, [I(5)])])),
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), new IrFieldLoad(R(stParam), sf))))),
        new IrReturn(R(acc)))
    ]) { Types = [st, recCls] };

    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo coverAll()
      WScript.Echo ply_task_await(asyncWhenAllVoid())
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "coverAll", [])),
      FormatExpected(IrInterpreter.Call(mod, "asyncWhenAllVoid", []))
    }));
  }

}
