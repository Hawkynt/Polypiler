using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Batch;

[TestFixture]
public sealed class BatchRuntimeShowcaseTests {

  private static readonly string[] _forbiddenEngines = ["cscript", "wscript", "powershell", "pwsh", "mshta", ".vbs"];

  private static string ShowcasePath(string fileName)
    => Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "showcase", fileName);

  private static IrModule ImportShowcase(string fileName) {
    var path = ShowcasePath(fileName);
    var result = CSharpFrontend.Import(fileName, File.ReadAllText(path));
    Assert.That(result.Diagnostics, Is.Empty, $"Frontend failed for {fileName}");
    return result.Module!;
  }

  private static string EmitValid(IrModule module) {
    var checks = BatchEmitter.Check(module);
    Assert.That(checks, Is.Empty, () => string.Join("\n", checks.Select(d => d.Message)));
    var files = BatchEmitter.Emit(module);
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Path, Is.EqualTo("kernel.bat"));
    var text = files[0].Content;
    Assert.That(text, Does.EndWith("\r\n"));
    Assert.That(Encoding.UTF8.GetBytes(text).All(b => b < 128), Is.True, "Emitted batch must be pure ASCII.");
    var lower = text.ToLowerInvariant();
    foreach (var forbidden in _forbiddenEngines)
      Assert.That(lower, Does.Not.Contain(forbidden), $"Forbidden external engine '{forbidden}' found.");
    foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
      Assert.That(line.Length, Is.LessThanOrEqualTo(1024), $"Line exceeded 1024 bytes: {line}");
    return text;
  }

  private static string EmitCSharp(string source) {
    var imported = CSharpFrontend.Import("Test.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty, () => string.Join("\n", imported.Diagnostics.Select(d => d.Message)));
    return EmitValid(imported.Module!);
  }

  [TestCase("01-operators-and-checked.cs", "IntegerOverflow")]
  [TestCase("02-recursion.cs", "_SP")]
  [TestCase("03-overloading.cs", "call :fn_")]
  [TestCase("04-structs-and-records.cs", "__ply_copy_s")]
  [TestCase("05-classes-and-properties.cs", "__ply_req_c")]
  [TestCase("06-statics-and-module-state.cs", ":__ply_init")]
  [TestCase("07-inheritance-and-interfaces.cs", ":__ply_i_")]
  [TestCase("08-generics.cs", ":__ply_add64")]
  [TestCase("09-lambdas-and-closures.cs", "lam_")]
  [TestCase("10-callbacks-and-delegates.cs", "_O_!_HID!___fn=")]
  [TestCase("11-enums-and-optionals.cs", "__PLY_NONE__")]
  [TestCase("12-collections.cs", ":__ply_map_find")]
  [TestCase("13-iterators-and-yield.cs", "seq_")]
  [TestCase("14-linq.cs", "lam_")]
  [TestCase("15-exceptions-and-using.cs", "_EX_ACTIVE")]
  [TestCase("16-tasks-and-async.cs", "start \"\" /B cmd /c")]
  [TestCase("17-stdio.cs", "echo(")]
  [TestCase("18-spans-and-pointers.cs", ":__ply_pext32")]
  public void EmitsAllEighteenShowcaseFilesAsPureBatch(string fileName, string expectedMarker) {
    var module = ImportShowcase(fileName);
    var script = EmitValid(module);
    Assert.That(script, Does.Contain(expectedMarker));
  }

  [Test]
  public void RefusesNativeInteropShowcaseWithPly2850() {
    var module = ImportShowcase("19-native-interop.cs");
    var diagnostics = BatchEmitter.Check(module);
    Assert.That(diagnostics, Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));
  }

  [Test]
  public void EmitsIdiomaticSetlocalEndlocalAndShiftForManyParameters() {
    const string source = """
      public static class ManyParams {
        public static int Sum11(int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10, int a11)
          => a1 + a2 + a3 + a4 + a5 + a6 + a7 + a8 + a9 + a10 + a11;
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain("setlocal EnableExtensions EnableDelayedExpansion")
      .And.Contain("shift")
      .And.Contain("endlocal")
      .And.Contain("set \"_RET=%_RET%\""));
  }

  [Test]
  public void EmitsAllSixtyFourBitOperationsAndUnsignedConversions() {
    const string source = """
      using System;
      using System.Numerics;
      public static class WideOps {
        public static ulong Compute(long a, ulong b, uint c, short d, sbyte e, byte f, ushort g) {
          long s1 = checked(a + 1L);
          long s2 = checked(s1 - 2L);
          long s3 = checked(s2 * 3L);
          long s4 = checked(-s3);
          long d1 = s4 / 2L;
          long r1 = s4 % 3L;
          long sh = (d1 << 2) ^ (r1 >> 1) ^ (a >>> 3) ^ (~a) ^ (a & 7L) ^ (a | 8L);
          bool cmp = sh < a && sh <= a && sh > -100L && sh >= -100L && sh != 0L && sh == sh;
          ulong u1 = checked(b + 1UL);
          ulong u2 = checked(u1 - 1UL);
          ulong u3 = checked(u2 * 2UL);
          ulong ud = u3 / 3UL;
          ulong ur = u3 % 3UL;
          ulong us = (ud << 1) | (ur >> 1) | (b >>> 2);
          bool ucmp = us < b || us <= b || us > 0UL || us >= 0UL;
          uint uc1 = checked(c + 1u);
          uint uc2 = checked(uc1 - 1u);
          uint uc3 = checked(uc2 * 2u);
          uint ucd = uc3 / 3u;
          uint ucr = uc3 % 3u;
          bool u32cmp = ucd < ucr && ucd <= ucr && ucd > 0u && ucd >= 0u;
          int narrow = checked((int)a) + checked((short)a) + checked((sbyte)c) + (int)checked((uint)d) + (int)e + f + g;
          ulong conv = checked((ulong)a);
          ulong rot = BitOperations.RotateLeft(b, 5) + BitOperations.RotateRight(b, 3);
          uint rot32 = BitOperations.RotateRight(c, 4);
          return (cmp && ucmp && u32cmp ? conv : us) + (ulong)narrow + rot + rot32;
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_add64")
      .And.Contain(":__ply_sub64")
      .And.Contain(":__ply_mul64")
      .And.Contain(":__ply_sdiv64")
      .And.Contain(":__ply_udiv64")
      .And.Contain(":__ply_shl64")
      .And.Contain(":__ply_shr64")
      .And.Contain(":__ply_ushr64")
      .And.Contain(":__ply_bit64")
      .And.Contain(":__ply_cmp_i64")
      .And.Contain(":__ply_cmp_u64")
      .And.Contain(":__ply_udiv32")
      .And.Contain(":__ply_parse_u64")
      .And.Contain(":__ply_fmt_u64"));
  }

  [Test]
  public void EmitsCheckedInt32StepAndMathIntrinsics() {
    const string source = """
      using System;
      using System.Numerics;
      using System.Runtime.Intrinsics.X86;
      public static class IntIntrinsics {
        public static int Run(int x, int y, uint u, long lx, long ly) {
          int a = checked(x - y);
          int b = checked(a * y);
          int c = checked(-b);
          checked { c++; c--; ++c; --c; }
          int[] arr = [1, 2, 3];
          arr[0]++;
          --arr[1];
          long[] larr = [10L, 20L];
          larr[0]++;
          Array.Fill(arr, 5);
          Array.Clear(arr);
          int[] dst = new int[3];
          Array.Copy(arr, 0, dst, 0, 3);
          int m1 = Math.Abs(x) + Math.Min(x, y) + Math.Max(x, y) + Math.Sign(x) + Math.Clamp(x, -10, 10);
          long m2 = Math.Abs(lx) + Math.Min(lx, ly) + Math.Max(lx, ly) + Math.Sign(lx) + Math.Clamp(lx, -10L, 10L);
          int bits = BitOperations.PopCount(u) + BitOperations.LeadingZeroCount(u) + BitOperations.TrailingZeroCount(u);
          uint pd = Bmi2.ParallelBitDeposit(u, 0xFFu);
          return c + m1 + (int)m2 + bits + (int)pd + dst[0];
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_mulchk32")
      .And.Contain(":__ply_pdep32")
      .And.Contain(":__ply_popcnt32")
      .And.Contain(":__ply_clz32")
      .And.Contain(":__ply_ctz32"));
  }

  [Test]
  public void EmitsStructsNestedCopiesRecordEqualityVirtualBaseAndCasts() {
    const string source = """
      public readonly record struct Inner(int V);
      public struct MutableOuter { public Inner In; public int Tag; }
      public readonly record struct OuterRec(Inner In, int Tag);
      public record RecA(int X, int Y);
      public record RecB(RecA Nested, Inner Box);
      public class Animal {
        public virtual int Sound() => 1;
        public int CallSelf() => this.Sound();
      }
      public class Dog : Animal {
        public override int Sound() => base.Sound() + 2;
      }
      public static class ObjectShowcase {
        public static int Run(int x) {
          MutableOuter[] arr = new MutableOuter[2];
          arr[0] = new MutableOuter { In = new Inner(x), Tag = 7 };
          arr[0].Tag = x + 1;
          var o1 = new OuterRec(new Inner(x), 7);
          var o2 = new OuterRec(new Inner(x), 7);
          bool eq = o1 == o2;
          var r1 = new RecB(new RecA(1, 2), new Inner(2));
          var r2 = new RecB(new RecA(1, 2), new Inner(2));
          bool req = r1 == r2;
          Animal a = new Dog();
          Dog? d = a as Dog;
          Dog d2 = (Dog)a;
          bool same = object.ReferenceEquals(d, d2);
          System.Func<int> bound = a.Sound;
          return a.CallSelf() + d2.Sound() + bound() + arr[0].Tag + (eq && req && same ? 1 : 0);
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_copy_s")
      .And.Contain(":__ply_eq_s")
      .And.Contain(":__ply_req_c")
      .And.Contain(":__ply_v_")
      .And.Contain("InvalidCast"));
  }

  [Test]
  public void EmitsAllCollectionsIteratorsLinqAndExceptions() {
    const string source = """
      using System;
      using System.Collections.Generic;
      using System.Linq;

      public static class FullFeatures {
        private static IEnumerable<int> YieldChain(int[] items) {
          try {
            foreach (var x in items) {
              if (x < 0) yield break;
              yield return x;
            }
          } finally {
          }
        }

        private static IEnumerable<int> YieldWrap(int[] items) {
          foreach (var x in YieldChain(items))
            yield return x * 2;
        }

        public static int Run(int[] items, string needle) {
          var list = new List<int>();
          list.Add(1);
          list.Insert(0, 2);
          list[1] = 3;
          bool has3 = list.Contains(3);
          int idx3 = list.IndexOf(3);
          list.RemoveAt(0);
          int[] arrCopy = list.ToArray();
          list.Clear();

          var map = new Dictionary<string, int>();
          map.Add("a", 1);
          map["b"] = 2;
          map["a"] = 10;
          bool hasA = map.ContainsKey("a");
          int valA = map["a"];
          bool remB = map.Remove("b");
          int sumMap = 0;
          foreach (var kv in map)
            sumMap += kv.Value;
          map.Clear();

          var set = new HashSet<string>();
          set.Add("x");
          bool hasX = set.Contains("x");
          bool remX = set.Remove("x");
          int setCount = set.Count;
          set.Clear();

          int seqSum = 0;
          foreach (var v in YieldWrap(items)) {
            if (v > 10) break;
            if (v == 2) continue;
            seqSum += v;
          }

          int linq = Enumerable.Range(1, 5).OrderByDescending(x => x).ToArray()[0];
          int strOps = needle.Length + needle.IndexOf("a", StringComparison.Ordinal) + (needle.Contains("a", StringComparison.Ordinal) ? 1 : 0) + (needle.StartsWith("a", StringComparison.Ordinal) ? 1 : 0);
          string sub = needle.Substring(0, 1);
          int ch = sub[0];

          try {
            if (valA < 0)
              throw new InvalidOperationException("bad");
          } catch (InvalidOperationException) {
            throw;
          } finally {
            seqSum += 1;
          }

          return seqSum + linq + strOps + ch + arrCopy.Length + idx3 + sumMap + setCount
            + (has3 && hasA && remB && hasX && remX ? 1 : 0);
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_list_find")
      .And.Contain(":__ply_map_find")
      .And.Contain(":__ply_map_alloc")
      .And.Contain(":__ply_map_rem")
      .And.Contain(":__ply_stablesort")
      .And.Contain(":__ply_substr")
      .And.Contain(":__ply_stridx")
      .And.Contain(":__ply_strcode"));
  }

  [Test]
  public void EmitsViewsPointersEnumsAsyncAndCraftedIrPaths() {
    const string source = """
      using System;
      using System.Threading.Tasks;

      public enum Color { Red = 1, Green = 2, Blue = 4 }

      public static class LowLevelAndAsync {
        public static async Task CompletedHelper() {
          await Task.CompletedTask;
        }

        private static async Task<int> SingleAsync(int x) => await Task.FromResult(x + 1);

        public static async Task<int> RunAsync(int x) {
          await CompletedHelper();
          int[] single = await Task.WhenAll(SingleAsync(x));
          return single[0];
        }

        public static unsafe int PointerAndSpanOps(int[] data) {
          Span<int> s = data.AsSpan();
          Span<int> sl = s.Slice(1, 2);
          sl[0] = 9;
          sl.Fill(4);
          int[] back = sl.ToArray();
          Span<int> buf = stackalloc int[2];
          sl.CopyTo(buf);
          bool tried = sl.TryCopyTo(buf);
          bool eq = sl.SequenceEqual(buf);

          int local = 5;
          int* pl = &local;
          *pl = 7;
          int* pstk = stackalloc int[2];
          pstk[0] = 3;
          int* p1 = pstk + 1;
          long diff = p1 - pstk;
          bool pcmp = pstk < p1 && pstk <= p1 && p1 > pstk && p1 >= pstk && pstk != p1 && pstk == pstk;
          void* vp = (void*)pstk;
          int* backPtr = (int*)vp;

          fixed (int* pinned = &data[0]) {
            *pinned = backPtr[0];
          }

          string cName = Color.Red.ToString();
          bool cDef = Enum.IsDefined(typeof(Color), Color.Green);
          Color parsed = Enum.Parse<Color>("Red", true);
          return local + (int)diff + back.Length + (int)parsed + cName.Length + (tried && eq && pcmp && cDef ? 1 : 0);
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_enum_str_Color")
      .And.Contain(":__ply_enum_def_Color")
      .And.Contain(":__ply_enum_parse_Color"));

    // Crafted IR covering remaining specialized nodes: PerIteration for-loop, IrYieldFrom, IrArraySequence,
    // IrThrowExpression, IrModuleConst of strings, IrModule.Initializer, IrCollectionOp.ListOf/ListFromArray/
    // ListRemoveLast/MapTryAdd, MapOrder.Insertion, EnumerationMode.Live, HighWord/JoinWords, FormatBoolEcma/DotNet,
    // EnumHasFlag on i64, GenericEquals/GenericCompare on u32/u64/bool/i8, IrNull pointer, IrSwitch on i64.
    var pIter = new IrLocal("i", IrType.I32);
    var capLam = IrLambda.Of([], IrType.I32, [], new([new IrReturn(new IrLocalRef(pIter))]));
    var strConst = IrModuleConst.OfStrings("Tags", IrType.Array(IrType.String), ["a", "b"]);
    var singleStrConst = IrModuleConst.OfStrings("Title", IrType.String, ["hi"]);
    var globalArr = new IrGlobal("GArr", IrType.Array(IrType.I32), new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 2)));
    var globalInit = new IrGlobal("GVal", IrType.I32, new IrConst(IrType.I32, 7));
    var listLocal = new IrLocal("ls", IrType.List(IrType.I32));
    var mapLocal = new IrLocal("mp", IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion));
    var elemLocal = new IrLocal("el", IrType.I32);
    var valLocal = new IrLocal("vl", IrType.I32);
    var seqParam = new IrLocal("arr", IrType.Array(IrType.I32));
    var flagEnum = new IrEnumType("BigFlags", IrType.I64, [new("A", 1), new("B", 2)], true);
    var initFn = new IrFunction("__init", false, IrType.Void, [], [], new([new IrGlobalAssign(globalInit, new IrConst(IrType.I32, 9))]));
    var iterFn = new IrFunction("Forward", false, IrType.Sequence(IrType.I32, true), [seqParam], [],
      new([new IrYieldFrom(new IrArraySequence(new IrLocalRef(seqParam)))]));
    var mainFn = new IrFunction("Exercise", true, IrType.String, [], [pIter, listLocal, mapLocal, elemLocal, valLocal], new([
      new IrDeclare(pIter, new IrConst(IrType.I32, 0)),
      new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(pIter), new IrConst(IrType.I32, 2)),
        new([new IrExprStmt(new IrInvoke(IrType.I32, capLam, []))]),
        new([new IrAssign(pIter, new IrBinary(IrBinaryOp.Add, new IrLocalRef(pIter), new IrConst(IrType.I32, 1)))])) { PerIteration = [pIter] },
      new IrDeclare(listLocal, new IrCollection(IrCollectionOp.ListOf, IrType.List(IrType.I32), [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [new IrConst(IrType.I32, 3)])])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [new IrLocalRef(listLocal)])),
      new IrForEachCollection(new IrLocalRef(listLocal), EnumerationMode.Live, elemLocal, null, new([new IrBreak()])),
      new IrDeclare(mapLocal, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion), [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [new IrLocalRef(mapLocal), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])),
      new IrForEachCollection(new IrLocalRef(mapLocal), EnumerationMode.Live, elemLocal, valLocal, new([new IrContinue()])),
      new IrForEachSequence(elemLocal, new IrCall("Forward", IrType.Sequence(IrType.I32, true), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [new IrConst(IrType.I32, 5)])]),
        new([new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(elemLocal), new IrConst(IrType.I32, 5)), new([new IrReturn(new IrConstRef(singleStrConst))]), null)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [new IrConst(IrType.U32, 1), new IrConst(IrType.U32, 2)])])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [new IrConst(IrType.Bool, 1)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [new IrConst(IrType.Bool, 0)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [new IrConst(flagEnum.Type, 3), new IrConst(flagEnum.Type, 1)])),
      new IrExprStmt(new IrGenericEquals(new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 1))),
      new IrExprStmt(new IrGenericCompare(new IrConst(IrType.U32, 1), new IrConst(IrType.U32, 2))),
      new IrExprStmt(new IrGenericCompare(new IrConst(IrType.U64, 1), new IrConst(IrType.U64, 2))),
      new IrExprStmt(new IrGenericCompare(new IrConst(IrType.Bool, 0), new IrConst(IrType.Bool, 1))),
      new IrExprStmt(new IrGenericCompare(new IrConst(IrType.I8, 1), new IrConst(IrType.I8, 2))),
      new IrExprStmt(new IrIsNull(new IrNull(IrType.String))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerEqual, IrType.Bool, [new IrNull(IrType.Pointer(IrType.I32)), new IrNull(IrType.Pointer(IrType.I32))])),
      new IrExprStmt(new IrConditional(new IrConst(IrType.Bool, 1), new IrConst(IrType.I32, 1), new IrThrowExpression(IrType.I32, new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), [])))),
      new IrSwitch(new IrConst(IrType.I64, 1), [new IrSwitchSection([1], false, new([new IrBreak()]))]),
      new IrReturn(new IrArrayLoad(new IrConstRef(strConst), new IrConst(IrType.I32, 0)))
    ]));
    var crafted = new IrModule("Crafted", [initFn, iterFn, mainFn], [strConst, singleStrConst]) {
      Types = [flagEnum],
      Globals = [globalArr, globalInit],
      Initializer = "__init"
    };
    var craftedScript = EmitValid(crafted);
    Assert.That(craftedScript, Does.Contain(":__ply_arrseq").And.Contain(":__ply_init"));
  }

  [Test]
  public void CoversRemainingRuntimeAndBranchPaths() {
    // 1. Captured parameters, address-taken parameters, break/continue/return through nested try/finally,
    //    nested struct field stores/loads, struct arrays, span reads, unchecked i64 sub/mul, checked conversions,
    //    FormatDotNet on u32/i64/u64, StrEquals/2-arg Substring/3-arg IndexOf, ViewOf 1-arg & 2-arg, Pin 1-arg on array,
    //    EnumToString on u32/i64/u64, static & interface bound methods.
    const string source = """
      using System;
      public enum U32Flag : uint { A = 1u }
      public enum I64Flag : long { B = 2L }
      public enum U64Flag : ulong { C = 4UL }
      public interface IAct { int Go(); }
      public sealed class Actor : IAct {
        public int Go() => 7;
        public static int Stat() => 9;
      }
      public struct Leaf { public int K; }
      public struct Node { public Leaf L; }
      public sealed class Holder { public Leaf Saved; public Holder() { } }
      public static class ExtraPaths {
        public static unsafe int All(int capParam, int addrParam, int[] data, string a, string b, uint u32v, long i64v, ulong u64v, int? opt) {
          Func<int> f = () => capParam + 1;
          int* pAddr = &addrParam;
          *pAddr = 3;

          Node n = new Node { L = new Leaf { K = 1 } };
          n.L.K = 2;
          Leaf extracted = n.L;
          var h = new Holder();
          h.Saved = extracted;
          Leaf[] larr = new Leaf[1];
          Leaf fromArr = larr[0];

          long sub64 = i64v - 5L;
          long mul64 = sub64 * 3L;
          ulong convU64 = checked((ulong)capParam);
          short convI16 = checked((short)capParam);
          byte b8 = unchecked((byte)capParam);
          sbyte sb8 = unchecked((sbyte)capParam);
          ushort u16 = unchecked((ushort)capParam);

          Span<int> sFull = data.AsSpan();
          Span<int> sTail = data.AsSpan(1);
          Span<int> sSlice = sFull.Slice(0);
          int spanRead = sSlice[0] + sTail[0];
          fixed (int* pArr = data) {
            spanRead += *pArr;
          }

          string s1 = u32v.ToString() + i64v.ToString() + u64v.ToString();
          bool seq = string.Equals(a, b, StringComparison.Ordinal);
          string sub2 = a.Substring(1);
          int idx3 = a.IndexOf(b, 0, StringComparison.Ordinal);
          string e1 = U32Flag.A.ToString();
          string e2 = I64Flag.B.ToString();
          string e3 = U64Flag.C.ToString();

          IAct act = new Actor();
          Func<int> dIface = act.Go;
          Func<int> dStat = Actor.Stat;
          int uw = opt.Value;

          for (int i = 0; i < 3; ++i) {
            try {
              if (i == 0) continue;
              if (i == 1) break;
              try {
                return 99;
              } finally {
                spanRead += 1;
              }
            } finally {
              spanRead += 2;
            }
          }

          return f() + *pAddr + fromArr.K + h.Saved.K + (int)mul64 + (int)convU64 + convI16 + b8 + sb8 + u16
            + spanRead + s1.Length + sub2.Length + idx3 + e1.Length + e2.Length + e3.Length + dIface() + dStat() + uw + (seq ? 1 : 0);
        }
      }
      """;
    var script = EmitCSharp(source);
    Assert.That(script, Does.Contain(":__ply_enum_str_U32Flag")
      .And.Contain(":__ply_enum_str_I64Flag")
      .And.Contain(":__ply_enum_str_U64Flag")
      .And.Contain(":__ply_streq"));

    // 2. Crafted IR for IrStep (local & array, i32 & i64, prefix & postfix, trap & wrap),
    //    IrBind, IrCollectionOp.MapTryGet/ListRemoveLast on object, ViewOf on null array,
    //    record class with string field equality, and over-budget ArrayOf/cumulative NewArray.
    var recStr = new IrClassType("RecNum") { IsRecord = true };
    var fNum = new IrField("Num", IrType.I32, true);
    recStr.Define([fNum]);
    recStr.Inherit(null, []);
    recStr.DefineMembers([], [], [], []);

    var locI32 = new IrLocal("li", IrType.I32);
    var locI64 = new IrLocal("ll", IrType.I64);
    var locArr = new IrLocal("ai", IrType.Array(IrType.I32));
    var locLArr = new IrLocal("al", IrType.Array(IrType.I64));
    var locMapObj = new IrLocal("mo", IrType.Map(IrType.I32, IrType.Object(recStr), MapOrder.SlotReuse));
    var locListObj = new IrLocal("lo", IrType.List(IrType.Object(recStr)));
    var stepFn = new IrFunction("Steps", true, IrType.I32, [], [locI32, locI64, locArr, locLArr, locMapObj, locListObj], new([
      new IrDeclare(locI32, new IrConst(IrType.I32, 1)),
      new IrDeclare(locI64, new IrConst(IrType.I64, 1)),
      new IrDeclare(locArr, new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 2))),
      new IrDeclare(locLArr, new IrNewArray(IrType.Array(IrType.I64), new IrConst(IrType.I64.Bits == 64 ? IrType.I32 : IrType.I32, 2))),
      new IrExprStmt(new IrBind(locI32, new IrConst(IrType.I32, 2))),
      new IrExprStmt(new IrStep(new IrLocalRef(locI32), true, true, false)),
      new IrExprStmt(new IrStep(new IrLocalRef(locI32), false, false, true)),
      new IrExprStmt(new IrStep(new IrLocalRef(locI64), true, false, false)),
      new IrExprStmt(new IrStep(new IrLocalRef(locI64), false, true, true)),
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(locArr), new IrConst(IrType.I32, 0)), true, false, true)),
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(locArr), new IrConst(IrType.I32, 0)), false, true, false)),
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(locLArr), new IrConst(IrType.I32, 0)), true, true, false)),
      new IrDeclare(locMapObj, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.I32, IrType.Object(recStr), MapOrder.SlotReuse), [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapTryGet, IrType.Object(recStr), [new IrLocalRef(locMapObj), new IrConst(IrType.I32, 1)])),
      new IrDeclare(locListObj, new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.Object(recStr)), [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Object(recStr), [new IrLocalRef(locListObj)])),
      new IrExprStmt(new IrStructEquals(new IrNull(IrType.Object(recStr)), new IrNull(IrType.Object(recStr)))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [new IrNull(IrType.Array(IrType.I32))])),
      new IrReturn(new IrLocalRef(locI32))
    ]));
    var stepMod = new IrModule("StepMod", [stepFn]) { Types = [recStr] };
    EmitValid(stepMod);

    // Budget refusals: cumulative NewArray > 512 and ArrayOf > 512.
    var overCum = new IrModule("OverCum", [new IrFunction("Run", true, IrType.I32, [], [], new([
      new IrExprStmt(new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 300))),
      new IrExprStmt(new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 300))),
      new IrReturn(new IrConst(IrType.I32, 0))
    ]))]);
    Assert.That(BatchEmitter.Check(overCum), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var overArrOf = new IrModule("OverArrOf", [new IrFunction("Run", true, IrType.I32, [], [], new([
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), Enumerable.Repeat<IrExpr>(new IrConst(IrType.I32, 1), BatchEmitter.CmdArrayStorageBudget + 1).ToList())),
      new IrReturn(new IrConst(IrType.I32, 0))
    ]))]);
    Assert.That(BatchEmitter.Check(overArrOf), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    // 3. Uninitialized captured/address-taken/default locals, struct return, and return inside try-without-finally.
    var pairSt = new IrStructType("PairDef", [new IrField("A", IrType.I32, false)]);
    var cOpt = new IrLocal("cOpt", IrType.Optional(IrType.I32));
    var cStr = new IrLocal("cStr", IrType.String);
    var cLong = new IrLocal("cLong", IrType.I64);
    var cStruct = new IrLocal("cStruct", IrType.Struct(pairSt));
    var cInt = new IrLocal("cInt", IrType.I32);
    var cStructInit = new IrLocal("cStructInit", IrType.Struct(pairSt));
    var aInt = new IrLocal("aInt", IrType.I32);
    var plainSt = new IrLocal("plainSt", IrType.Struct(pairSt));
    var lam = IrLambda.Of([], IrType.I32, [], new([
      new IrAssign(cInt, new IrConst(IrType.I32, 5)),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrLocalRef(cInt),
        new IrBinary(IrBinaryOp.Add,
          new IrCoalesce(new IrLocalRef(cOpt), new IrConst(IrType.I32, 1)),
          new IrBinary(IrBinaryOp.Add,
            new IrConditional(new IrIsNull(new IrLocalRef(cStr)), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0)),
            new IrBinary(IrBinaryOp.Add,
              new IrConvert(IrType.I32, new IrLocalRef(cLong)),
              new IrBinary(IrBinaryOp.Add,
                new IrFieldLoad(new IrLocalRef(cStruct), pairSt.Fields[0]),
                new IrFieldLoad(new IrLocalRef(cStructInit), pairSt.Fields[0])))))))
    ]));
    var defFn = new IrFunction("Defaults", true, IrType.Struct(pairSt), [],
      [cOpt, cStr, cLong, cStruct, cInt, cStructInit, aInt, plainSt], new([
        new IrDeclare(cOpt, null),
        new IrDeclare(cStr, null),
        new IrDeclare(cLong, null),
        new IrDeclare(cStruct, null),
        new IrDeclare(cInt, null),
        new IrDeclare(cStructInit, new IrStructNew(IrType.Struct(pairSt), [new IrConst(IrType.I32, 7)])),
        new IrDeclare(aInt, null),
        new IrAssign(aInt, new IrConst(IrType.I32, 9)),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [new IrLocalRef(aInt)])),
        new IrDeclare(plainSt, null),
        new IrExprStmt(new IrInvoke(IrType.I32, lam, [])),
        new IrTry(new([new IrReturn(new IrLocalRef(plainSt))]),
          [new IrCatch(IrExceptionClass.Exception, null, null, new([new IrReturn(new IrLocalRef(cStructInit))]))],
          null),
        new IrReturn(new IrLocalRef(plainSt))
      ]));
    EmitValid(new IrModule("DefMod", [defFn]) { Types = [pairSt] });
  }

}
