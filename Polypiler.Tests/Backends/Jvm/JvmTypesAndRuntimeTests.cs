using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

[TestFixture]
public sealed class JvmTypesAndRuntimeTests {

  private static string ShowcaseDir {
    get {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "showcase")))
        dir = dir.Parent;
      return Path.Combine(dir!.FullName, "docs", "showcase");
    }
  }

  private static object? Eval(IrModule mod, string name, params object[] args) => IrInterpreter.Call(mod, name, args);

  private static (IrModule Module, IReadOnlyList<GeneratedFile> Files) CompileShowcase(string fileName, JvmProfile? profile = null) {
    var path = Path.Combine(ShowcaseDir, fileName);
    var source = File.ReadAllText(path);
    var imported = CSharpFrontend.Import(fileName, source);
    Assert.That(imported.Diagnostics, Is.Empty, $"Import failed for {fileName}");
    var legalized = JvmEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, profile ?? JvmProfile.Default);
    var effectiveProfile = profile ?? JvmProfile.Default;
    var check = JvmEmitter.Check(legalized, effectiveProfile);
    Assert.That(check, Is.Empty, $"Check failed for {fileName} ({effectiveProfile}): {string.Join("; ", check.Select(d => $"{d.Code}: {d.Message}"))}");
    var files = JvmEmitter.Emit(legalized, effectiveProfile);
    Assert.That(files, Has.Count.EqualTo(1));
    var disasm = JvmDisassembler.DisassembleJar(files[0].GetBytes());
    Assert.That(disasm, Is.Not.Empty);
    return (legalized, files);
  }

  [TestCase("03-overloading.cs")]
  [TestCase("04-structs-and-records.cs")]
  [TestCase("05-classes-and-properties.cs")]
  [TestCase("07-inheritance-and-interfaces.cs")]
  [TestCase("08-generics.cs")]
  [TestCase("09-lambdas-and-closures.cs")]
  [TestCase("12-collections.cs")]
  [TestCase("13-iterators-and-yield.cs")]
  [TestCase("15-exceptions-and-using.cs")]
  [TestCase("16-tasks-and-async.cs")]
  [TestCase("18-spans-and-pointers.cs")]
  [TestCase("19-native-interop.cs")]
  public void All12ShowcaseFiles_EmitAndDisassembleAcrossAllJvmProfiles(string fileName) {
    foreach (var profile in new[] { JvmProfile.Java8, JvmProfile.Java17, JvmProfile.Java21 })
      CompileShowcase(fileName, profile);
  }

  [Test]
  public void Showcase03_Overloading_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("03-overloading.cs");
    var expected = (int)Eval(mod, "Run", 10, 3, 4)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(OverloadingShowcase.Run(10, 3, 4));
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(expected));
  }

  [Test]
  public void Showcase04_StructsAndRecords_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("04-structs-and-records.cs");
    var expectedEqTrue = (bool)Eval(mod, "AreEqual", 3, 4)!;
    var expectedCopy = (int)Eval(mod, "CopyValue", 5)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          Vec2 a = new Vec2(2, 3);
          Vec2 b = new Vec2(4, 5);
          System.out.println(StructsAndRecordsShowcase.Dot(a, b));
          System.out.println(StructsAndRecordsShowcase.AreEqual(3, 4));
          System.out.println(StructsAndRecordsShowcase.CopyValue(5));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(int.Parse(lines[0]), Is.EqualTo(23));
    Assert.That(bool.Parse(lines[1]), Is.EqualTo(expectedEqTrue));
    Assert.That(int.Parse(lines[2]), Is.EqualTo(expectedCopy));
  }

  [Test]
  public void Showcase05_ClassesAndProperties_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("05-classes-and-properties.cs");
    var expected = (int)Eval(mod, "Measure", 4, 7)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ClassesShowcase.Measure(4, 7));
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(expected));
  }

  [Test]
  public void Showcase07_InheritanceAndInterfaces_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("07-inheritance-and-interfaces.cs");
    var expRect = (int)Eval(mod, "Measure", true, 3, 5)!;
    var expSquare = (int)Eval(mod, "Measure", false, 4, 9)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(InheritanceShowcase.Measure(true, 3, 5));
          System.out.println(InheritanceShowcase.Measure(false, 4, 9));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(int.Parse(lines[0]), Is.EqualTo(expRect));
    Assert.That(int.Parse(lines[1]), Is.EqualTo(expSquare));
  }

  [Test]
  public void Showcase08_Generics_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("08-generics.cs");
    var e1 = (long)Eval(mod, "Run", 150, -100L)!;
    var e2 = (long)Eval(mod, "Run", 42, 25L)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(GenericsShowcase.Run(150, -100L));
          System.out.println(GenericsShowcase.Run(42, 25L));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(long.Parse(lines[0]), Is.EqualTo(e1));
    Assert.That(long.Parse(lines[1]), Is.EqualTo(e2));
  }

  [Test]
  public void Showcase09_LambdasAndClosures_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("09-lambdas-and-closures.cs");
    var expected = (int)Eval(mod, "Run", 10, 5)!;
    Assert.That(expected, Is.EqualTo(45));
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ClosuresShowcase.Run(10, 5));
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(45));
  }

  [Test]
  public void Showcase12_Collections_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("12-collections.cs");
    int[] data = [3, -1, 3, 0, -2, 5];
    var expected = (int)Eval(mod, "Run", data)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(CollectionsShowcase.Run(new int[] { 3, -1, 3, 0, -2, 5 }));
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(expected));
  }

  [Test]
  public void Showcase13_IteratorsAndYield_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("13-iterators-and-yield.cs");
    var e10 = (int)Eval(mod, "SumEvens", 10)!;
    var e200 = (int)Eval(mod, "SumEvens", 200)!;
    Assert.That(e10, Is.EqualTo(30));
    Assert.That(e200, Is.EqualTo(2550));
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(IteratorsShowcase.SumEvens(10));
          System.out.println(IteratorsShowcase.SumEvens(200));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(int.Parse(lines[0]), Is.EqualTo(30));
    Assert.That(int.Parse(lines[1]), Is.EqualTo(2550));
  }

  [Test]
  public void Showcase15_ExceptionsAndUsing_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("15-exceptions-and-using.cs");
    var ePos = (int)Eval(mod, "Evaluate", 5)!;
    var eNeg = (int)Eval(mod, "Evaluate", -3)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ExceptionsAndUsingShowcase.Evaluate(5));
          System.out.println(ExceptionsAndUsingShowcase.Evaluate(-3));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(int.Parse(lines[0]), Is.EqualTo(ePos));
    Assert.That(int.Parse(lines[1]), Is.EqualTo(eNeg));
  }

  [Test]
  public void Showcase16_TasksAndAsync_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("16-tasks-and-async.cs");
    var expected = (int)Eval(mod, "CombineAsync", 3, 4)!;
    Assert.That(expected, Is.EqualTo(27));
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          Object res = TasksShowcase.CombineAsync(3, 4).join();
          System.out.println(res);
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(27));
  }

  [Test]
  public void Showcase18_SpansAndPointers_MatchesIrInterpreterLive() {
    var (mod, files) = CompileShowcase("18-spans-and-pointers.cs");
    var ePext = (uint)Eval(mod, "ExtractBits", 0xABCD1234u, 0x0F0F0F0Fu)!;
    var eSum = (int)Eval(mod, "SumSlice", new[] { 10, 20, 30, 40, 50 }, 1, 3)!;
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(Integer.toUnsignedString(LowLevelShowcase.ExtractBits(0xABCD1234, 0x0F0F0F0F)));
          System.out.println(LowLevelShowcase.SumSlice(new int[] { 10, 20, 30, 40, 50 }, 1, 3));
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(uint.Parse(lines[0]), Is.EqualTo(ePext));
    Assert.That(int.Parse(lines[1]), Is.EqualTo(eSum));
  }

  [Test]
  public void Showcase19_NativeInterop_VerifiesCleanlyUnderJavaVerifyAll() {
    var (_, files) = CompileShowcase("19-native-interop.cs");
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) throws Exception {
          Class<?> cls = Class.forName("NativeInteropShowcase");
          NativePoint pt = new NativePoint(3, 4);
          System.out.println(cls.getSimpleName() + ":" + pt.X + ":" + pt.Y);
        }
      }
      """);
    Assert.That(output, Is.EqualTo("NativeInteropShowcase:3:4"));
  }

  [Test]
  public void Structs_Nested_Arrays_Floats_AndValueSemantics_ExecuteCorrectly() {
    var inner = new IrStructType("Inner", [new IrField("V", IrType.F64, false), new IrField("Tag", IrType.U8, false)]) { IsEquatable = true };
    var outer = new IrStructType("Outer", [new IrField("In", IrType.Struct(inner), false), new IrField("F", IrType.F32, false), new IrField("L", IrType.I64, false)]) { IsEquatable = true };
    var arrStruct = new IrStructType("Pair", [new IrField("A", IrType.I32, false), new IrField("B", IrType.I32, false)]);

    var p = L("p", IrType.Struct(arrStruct));
    var arr = L("arr", IrType.Array(IrType.Struct(arrStruct)));
    var o1 = L("o1", IrType.Struct(outer));
    var o2 = L("o2", IrType.Struct(outer));
    var optS = L("os", IrType.Optional(IrType.Struct(arrStruct)));
    var p2 = L("p2", IrType.Struct(arrStruct));

    var mod = new IrModule("StructDeep", [
      Function("MutateParam", IrType.I32, [p],
        new IrFieldStore(R(p), arrStruct.Fields[0], I(99)),
        new IrReturn(new IrFieldLoad(R(p), arrStruct.Fields[0]))),
      Function("TestParamIsolation", IrType.I32, [],
        new IrDeclare(p, null),
        new IrFieldStore(R(p), arrStruct.Fields[0], I(12)),
        new IrExprStmt(new IrCall("MutateParam", IrType.I32, [R(p)])),
        new IrReturn(new IrFieldLoad(R(p), arrStruct.Fields[0]))),
      Function("StructArrayAndGlobal", IrType.I32, [],
        new IrDeclare(arr, new IrNewArray(IrType.Array(IrType.Struct(arrStruct)), I(2))),
        new IrFieldStore(new IrArrayLoad(R(arr), I(0)), arrStruct.Fields[0], I(40)),
        new IrDeclare(p2, new IrStructNew(IrType.Struct(arrStruct), [I(7), I(8)])),
        new IrDeclare(optS, new IrSome(R(p2))),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrFieldLoad(new IrArrayLoad(R(arr), I(0)), arrStruct.Fields[0]),
          new IrFieldLoad(new IrUnwrap(R(optS)), arrStruct.Fields[0])))),
      Function("NestedEquality", IrType.Bool, [],
        new IrDeclare(o1, new IrStructNew(IrType.Struct(outer), [new IrStructNew(IrType.Struct(inner), [F64(double.NaN), C(IrType.U8, 200)]), F32(-0.0f), I64(5)])),
        new IrDeclare(o2, new IrStructNew(IrType.Struct(outer), [new IrStructNew(IrType.Struct(inner), [F64(double.NaN), C(IrType.U8, 200)]), F32(0.0f), I64(5)])),
        new IrFieldStore(new IrFieldLoad(R(o1), outer.Fields[0]), inner.Fields[1], C(IrType.U8, 200)),
        new IrReturn(new IrStructEquals(R(o1), R(o2))))
    ]) {
      Types = [inner, outer, arrStruct]
    };

    var files = JvmEmitter.Emit(mod, JvmProfile.Java21, new JvmEmitOptions { PackageFormat = JvmPackageFormat.Class });
    Assert.That(files.Count, Is.EqualTo(4));
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(StructDeep.TestParamIsolation());
          System.out.println(StructDeep.StructArrayAndGlobal());
          System.out.println(StructDeep.NestedEquality());
        }
      }
      """);
    Assert.That(output, Is.EqualTo("12\n47\ntrue"));
  }

  [Test]
  public void ObjectModel_BaseCalls_CtorChaining_Casts_GenericCompare_AndFilterFallthrough_ExecuteCorrectly() {
    var iface = new IrInterfaceType("IBase");
    var mIface = new IrMethod("Val", "IBase.Val", false) { Dispatch = IrDispatch.Abstract };
    iface.DefineMembers([mIface], [], []);

    var baseCls = new IrClassType("Base");
    var fX = new IrField("X", IrType.I32, false);
    baseCls.Define([fX]);
    baseCls.Inherit(null, [iface]);
    var bCtor = new IrConstructor("Base..ctor");
    var bVal = new IrMethod("Val", "Base.Val", false) { Dispatch = IrDispatch.Virtual, Implements = [mIface] };
    var bStat = new IrMethod("Stat", "Base.Stat", true);
    baseCls.DefineMembers([bCtor], [bVal, bStat], [], []);

    var subCls = new IrClassType("Sub") { IsSealed = true };
    var fY = new IrField("Y", IrType.I32, false);
    subCls.Define([fY]);
    subCls.Inherit(baseCls, []);
    var sCtor1 = new IrConstructor("Sub..ctor1");
    var sCtor2 = new IrConstructor("Sub..ctor2");
    var sVal = new IrMethod("Val", "Sub.Val", false) { Dispatch = IrDispatch.Override, Overrides = bVal, IsSealedOverride = true };
    subCls.DefineMembers([sCtor1, sCtor2], [sVal], [], []);

    var exCls = new IrClassType("MyEx") { IsSealed = true };
    exCls.Define([]);
    exCls.Inherit(IrExceptionClass.Exception, []);
    var exCtor0 = new IrConstructor("MyEx..ctor0");
    var exCtor2 = new IrConstructor("MyEx..ctor2");
    exCls.DefineMembers([exCtor0, exCtor2], [], [], []);

    var bThis = L("this", IrType.Object(baseCls));
    var sThis = L("this", IrType.Object(subCls));
    var eThis = L("this", IrType.Object(exCls));
    var px = L("x", IrType.I32);
    var py = L("y", IrType.I32);
    var msg = L("m", IrType.String);
    var innerEx = L("inner", IrType.Object(IrExceptionClass.Exception));
    var obj = L("o", IrType.Object(baseCls));
    var e1 = L("e1", IrType.Object(exCls));
    var e2 = L("e2", IrType.Object(IrExceptionClass.Exception));

    var mod = new IrModule("ObjDeep", [
      Function("IBase.Val", false, IrType.I32, [L("this", IrType.Object(iface))]),
      Function("Base..ctor", false, IrType.Void, [bThis, px], new IrFieldStore(R(bThis), fX, R(px))),
      Function("Base.Val", false, IrType.I32, [bThis], new IrReturn(new IrFieldLoad(R(bThis), fX))),
      Function("Base.Stat", false, IrType.I32, [px], new IrReturn(Bin(IrBinaryOp.Add, R(px), I(100)))),
      Function("Sub..ctor2", false, IrType.Void, [sThis, px, py],
        new IrExprStmt(new IrCall("Base..ctor", IrType.Void, [R(sThis), R(px)])),
        new IrFieldStore(R(sThis), fY, R(py))),
      Function("Sub..ctor1", false, IrType.Void, [sThis, px],
        new IrExprStmt(new IrCall("Sub..ctor2", IrType.Void, [R(sThis), R(px), I(5)]))),
      Function("Sub.Val", false, IrType.I32, [sThis],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, bVal, R(sThis), []) { Kind = IrMethodCallKind.Base },
          new IrFieldLoad(R(sThis), fY)))),
      Function("MyEx..ctor0", false, IrType.Void, [eThis],
        new IrExprStmt(new IrCall("Exception..ctor()", IrType.Void, [R(eThis)]))),
      Function("MyEx..ctor2", false, IrType.Void, [eThis, msg, innerEx],
        new IrExprStmt(new IrCall("Exception..ctor(str,Exception)", IrType.Void, [R(eThis), R(msg), R(innerEx)]))),
      Function("TestObjects", IrType.I32, [],
        new IrDeclare(obj, new IrNew(IrType.Object(subCls), sCtor1, [I(10)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, bVal, R(obj), []) { Kind = IrMethodCallKind.Virtual },
          Bin(IrBinaryOp.Add,
            new IrMethodCall(IrType.I32, bStat, null, [I(1)]),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrReferenceEquals(new IrAsCast(IrType.Object(subCls), R(obj)), new IrCast(IrType.Object(subCls), R(obj))), I(10), I(0)),
              new IrConditional(new IrReferenceEquals(new IrAsCast(IrType.Object(subCls), new IrNew(IrType.Object(baseCls), bCtor, [I(1)])), new IrNull(IrType.Object(subCls))), I(20), I(0))))))),
      Function("TestFilterFallthrough", IrType.I32, [px],
        new IrTry(
          Block(new IrThrow(new IrNew(IrType.Object(exCls), exCtor0, []))),
          [
            new IrCatch(exCls, e1, Bin(IrBinaryOp.Equal, R(px), I(1)), Block(new IrReturn(I(111)))),
            new IrCatch(IrExceptionClass.Exception, e2, null, Block(new IrReturn(I(222))))
          ],
          null),
        new IrReturn(I(0))),
      Function("TestGenericCompare", IrType.I32, [],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrGenericCompare(B(false), B(true)),
          Bin(IrBinaryOp.Add,
            new IrGenericCompare(C(IrType.U8, 10), C(IrType.U8, 3)),
            Bin(IrBinaryOp.Add,
              new IrGenericCompare(U32(uint.MaxValue), U32(1)),
              Bin(IrBinaryOp.Add,
                new IrGenericCompare(U64(1), U64(ulong.MaxValue)),
                Bin(IrBinaryOp.Add,
                  new IrGenericCompare(F64(double.NaN), F64(0.0)),
                  Bin(IrBinaryOp.Add,
                    new IrGenericCompare(F64(0.0), F64(double.NaN)),
                    Bin(IrBinaryOp.Add,
                      new IrGenericCompare(F32(float.NaN), F32(float.NaN)),
                      new IrConditional(new IrGenericEquals(F64(double.NaN), F64(double.NaN)), I(100), I(0)))))))))))
    ]) {
      Types = [iface, baseCls, subCls, exCls]
    };

    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ObjDeep.TestObjects());
          System.out.println(ObjDeep.TestFilterFallthrough(1));
          System.out.println(ObjDeep.TestFilterFallthrough(2));
          System.out.println(ObjDeep.TestGenericCompare());
        }
      }
      """);
    // TestObjects: 15 + 101 + 10 + 20 = 146
    // TestGenericCompare: -1 + 7 + 1 + (-1) + (-1) + 1 + 0 + 100 = 106
    Assert.That(output, Is.EqualTo("146\n111\n222\n106"));
  }

  [Test]
  public void Closures_FunctionRef_BoundMethod_AndPerIterationLoopCaptures_ExecuteCorrectly() {
    var cls = new IrClassType("Adder") { IsSealed = true };
    var fBase = new IrField("Base", IrType.I32, false);
    cls.Define([fBase]);
    cls.Inherit(null, []);
    var ctor = new IrConstructor("Adder..ctor");
    var mAdd = new IrMethod("Add", "Adder.Add", false);
    cls.DefineMembers([ctor], [mAdd], [], []);

    var cThis = L("this", IrType.Object(cls));
    var px = L("x", IrType.I32);
    var fType = IrType.Function(IrType.I32, IrType.I32);
    var fnRef = L("fnRef", fType);
    var fnBound = L("fnBound", fType);
    var fnArr = L("fns", IrType.Array(IrType.Function(IrType.I32)));
    var i = L("i", IrType.I32);

    var loopLam = IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(i))));

    var mod = new IrModule("ClosureDeep", [
      Function("Adder..ctor", false, IrType.Void, [cThis, px], new IrFieldStore(R(cThis), fBase, R(px))),
      Function("Adder.Add", false, IrType.I32, [cThis, px], new IrReturn(Bin(IrBinaryOp.Add, new IrFieldLoad(R(cThis), fBase), R(px)))),
      Function("DoubleIt", false, IrType.I32, [px], new IrReturn(Bin(IrBinaryOp.Multiply, R(px), I(2)))),
      Function("RunAll", IrType.I32, [],
        new IrDeclare(fnRef, new IrFunctionRef(fType, "DoubleIt")),
        new IrDeclare(fnBound, new IrBoundMethod(fType, new IrNew(IrType.Object(cls), ctor, [I(10)]), mAdd)),
        new IrDeclare(fnArr, new IrNewArray(IrType.Array(IrType.Function(IrType.I32)), I(3))),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), I(3)),
          Block(new IrStore(R(fnArr), R(i), loopLam)),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))) { PerIteration = [i] },
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrInvoke(IrType.I32, R(fnRef), [I(5)]),
          Bin(IrBinaryOp.Add,
            new IrInvoke(IrType.I32, R(fnBound), [I(7)]),
            Bin(IrBinaryOp.Add,
              new IrInvoke(IrType.I32, new IrArrayLoad(R(fnArr), I(0)), []),
              new IrInvoke(IrType.I32, new IrArrayLoad(R(fnArr), I(2)), []))))))
    ]) {
      Types = [cls]
    };

    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ClosureDeep.RunAll());
        }
      }
      """);
    // 10 + 17 + 0 + 2 = 29
    Assert.That(output, Is.EqualTo("29"));
  }

  [Test]
  public void Collections_AllOperationsAndForEach_ExecuteCorrectly() {
    var listT = IrType.List(IrType.I32);
    var mapT = IrType.Map(IrType.String, IrType.I32, MapOrder.Insertion);
    var setT = IrType.Set(IrType.I32, MapOrder.Insertion);
    var list = L("l", listT);
    var map = L("m", mapT);
    var set = L("s", setT);
    var sum = L("sum", IrType.I32);
    var v = L("v", IrType.I32);
    var k = L("k", IrType.String);
    var mv = L("mv", IrType.I32);
    var popped = L("pop", IrType.Optional(IrType.I32));
    var arr = L("a", IrType.Array(IrType.I32));

    var mod = new IrModule("ColDeep", [
      Function("ExerciseAll", IrType.I32, [],
        new IrDeclare(list, new IrCollection(IrCollectionOp.ListOf, listT, [I(10), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [R(list), I(1), I(15)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [R(list), I(0), I(5)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [R(list), I(1)])),
        new IrDeclare(popped, new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(list)])),
        new IrDeclare(arr, new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), [R(list)])),
        new IrDeclare(sum, new IrUnwrap(R(popped))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrArrayLoad(R(arr), I(0)))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(list), I(5)]))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(list), I(5)]), I(1), I(0)))),
        new IrForEachCollection(R(list), EnumerationMode.Checked, v, null, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(v))))),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [R(list)])),
        new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, mapT, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(map), new IrStrConst("a"), I(3)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(map), new IrStrConst("b"), I(4)])),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(map), new IrStrConst("a")]))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(map)]))),
        new IrForEachCollection(R(map), EnumerationMode.Checked, k, mv, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(mv))))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(map), new IrStrConst("a")])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapClear, IrType.Void, [R(map)])),
        new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, setT, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(set), I(7)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(set), I(8)])),
        new IrForEachCollection(R(set), EnumerationMode.Checked, v, null, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(v))))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(set)]))),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(set), I(7)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetClear, IrType.Void, [R(set)])),
        new IrReturn(R(sum)))
    ]);

    var expected = (int)Eval(mod, "ExerciseAll")!;
    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ColDeep.ExerciseAll());
        }
      }
      """);
    Assert.That(int.Parse(output), Is.EqualTo(expected));
  }

  [Test]
  public void Iterators_YieldFrom_ArraySequence_Restartability_AndSinglePass_ExecuteCorrectly() {
    var seqT = IrType.Sequence(IrType.I32, true);
    var singleT = IrType.Sequence(IrType.I32, false);
    var arr = L("a", IrType.Array(IrType.I32));
    var seq = L("s", seqT);
    var sng = L("sg", singleT);
    var elem = L("e", IrType.I32);
    var sum = L("sum", IrType.I32);

    var mod = new IrModule("IterDeep", [
      Function("Wrap", seqT, [arr],
        new IrYield(I(1)),
        new IrYieldFrom(new IrArraySequence(R(arr))),
        new IrYield(I(9))),
      Function("Once", singleT, [],
        new IrYield(I(100)),
        new IrYieldBreak()),
      Function("RunIter", IrType.I32, [arr],
        new IrDeclare(seq, new IrCall("Wrap", seqT, [R(arr)])),
        new IrDeclare(sng, new IrCall("Once", singleT, [])),
        new IrDeclare(sum, I(0)),
        new IrForEachSequence(elem, R(seq), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))))),
        new IrForEachSequence(elem, R(seq), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))))),
        new IrForEachSequence(elem, R(sng), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))))),
        new IrForEachSequence(elem, R(sng), Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))))),
        new IrReturn(R(sum)))
    ]);

    int[] input = [2, 3];
    var expected = (int)Eval(mod, "RunIter", input)!;
    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(IterDeep.RunIter(new int[] { 2, 3 }));
        }
      }
      """);
    // (1+2+3+9)*2 + 100 + 0 = 130
    Assert.That(int.Parse(output), Is.EqualTo(expected));
  }

  [Test]
  public void Async_Completed_VoidWhenAll_AndFaultUnwrapping_ExecuteCorrectly() {
    var flag = L("flag", IrType.Bool);
    var mod = new IrModule("AsyncDeep", [
      Function("NoOp", IrType.Async(IrType.Void), [],
        new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []))),
        new IrReturn(null)),
      Function("MayFail", IrType.Async(IrType.I32), [flag],
        new IrIf(R(flag), Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("boom")]))), null),
        new IrReturn(I(42))),
      Function("TestAsync", IrType.Async(IrType.I32), [flag],
        new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [new IrCall("NoOp", IrType.Async(IrType.Void), [])]))),
        new IrTry(
          Block(new IrReturn(new IrAwait(new IrCall("MayFail", IrType.Async(IrType.I32), [R(flag)])))),
          [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, Block(new IrReturn(I(-7))))],
          null),
        new IrReturn(I(0)))
    ]);

    var eOk = (int)Eval(mod, "TestAsync", false)!;
    var eFail = (int)Eval(mod, "TestAsync", true)!;
    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(AsyncDeep.TestAsync(false).join());
          System.out.println(AsyncDeep.TestAsync(true).join());
        }
      }
      """);
    Assert.That(output, Is.EqualTo($"{eOk}\n{eFail}"));
  }

  [Test]
  public void Spans_Pointers_EndianOnView_AndBitPermutations_ExecuteCorrectly() {
    var data = L("data", IrType.Array(IrType.I32));
    var bytes = L("bytes", IrType.Array(IrType.U8));
    var s1 = L("s1", IrType.Span(IrType.I32));
    var s2 = L("s2", IrType.Span(IrType.I32));
    var sb = L("sb", IrType.Span(IrType.U8));
    var localX = L("x", IrType.I32);
    var p = L("p", IrType.Pointer(IrType.I32));
    var q = L("q", IrType.Pointer(IrType.I32));
    var pv = L("pv", IrType.Pointer(IrType.Void));
    var u64a = L("a", IrType.U64);
    var u64b = L("b", IrType.U64);
    var u8a = L("ba", IrType.U8);
    var u8b = L("bb", IrType.U8);

    var mod = new IrModule("LowDeep", [
      Function("SpanOps", IrType.I32, [data],
        new IrDeclare(s1, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I32), [I(3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [R(s1), I(9)])),
        new IrDeclare(s2, new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(IrType.I32), [new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(data)]), I(1), I(3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [R(s2), R(s1)])),
        new IrStore(R(s1), I(0), I(100)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrArrayLoad(R(s1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(IrType.I32), [R(s1)]), I(1)),
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(s1), R(s2)]), I(1000), I(0)))))),
      Function("ViewEndian", IrType.I32, [bytes],
        new IrDeclare(sb, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U8), [R(bytes), I(2)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(sb), I(0), I(0x11223344)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I32, [R(sb), I(0)]))),
      Function("PointerOps", IrType.I32, [data],
        new IrDeclare(localX, I(5)),
        new IrDeclare(p, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(localX)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(p), I(77)])),
        new IrDeclare(pv, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.Void), [R(p)])),
        new IrDeclare(q, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(data), I(1)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          R(localX),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.I32), [R(pv)])]),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerDifference, IrType.I64, [new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.I32), [R(q), I(2)]), R(q)])),
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerLess, IrType.Bool, [R(q), new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.I32), [R(q), I(1)])]), I(10), I(0))))))),
      Function("Pdep64", IrType.U64, [u64a, u64b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U64, [R(u64a), R(u64b)]))),
      Function("Pext64", IrType.U64, [u64a, u64b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitExtract, IrType.U64, [R(u64a), R(u64b)]))),
      Function("PdepU8", IrType.U8, [u8a, u8b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U8, [R(u8a), R(u8b)])))
    ]);

    var arrData = new[] { 1, 20, 30, 40, 50 };
    var byteBuf = new byte[8];
    var eSpan = (int)Eval(mod, "SpanOps", arrData)!;
    var eEndian = (int)Eval(mod, "ViewEndian", byteBuf)!;
    var ePtr = (int)Eval(mod, "PointerOps", arrData)!;
    var ePdep64 = (ulong)Eval(mod, "Pdep64", 0xFUL, 0x0101010100000000UL)!;
    var ePext64 = (ulong)Eval(mod, "Pext64", 0x0101010100000000UL, 0x0101010100000000UL)!;
    var ePdepU8 = (byte)Eval(mod, "PdepU8", (byte)0b11, (byte)0b10100000)!;

    var files = JvmEmitter.Emit(mod);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(LowDeep.SpanOps(new int[] { 1, 20, 30, 40, 50 }));
          System.out.println(LowDeep.ViewEndian(new byte[8]));
          System.out.println(LowDeep.PointerOps(new int[] { 1, 20, 30, 40, 50 }));
          System.out.println(Long.toUnsignedString(LowDeep.Pdep64(0xFL, 0x0101010100000000L)));
          System.out.println(Long.toUnsignedString(LowDeep.Pext64(0x0101010100000000L, 0x0101010100000000L)));
          System.out.println(LowDeep.PdepU8((byte)3, (byte)0xA0) & 0xFF);
        }
      }
      """);
    var lines = output.Split('\n');
    Assert.That(int.Parse(lines[0]), Is.EqualTo(eSpan));
    Assert.That(int.Parse(lines[1]), Is.EqualTo(eEndian));
    Assert.That(int.Parse(lines[2]), Is.EqualTo(ePtr));
    Assert.That(ulong.Parse(lines[3]), Is.EqualTo(ePdep64));
    Assert.That(ulong.Parse(lines[4]), Is.EqualTo(ePext64));
    Assert.That(byte.Parse(lines[5]), Is.EqualTo(ePdepU8));
  }

  [Test]
  public void EdgeCases_Captures_Records_Exceptions_Collections_Iterators_Pointers_ExecuteCorrectly() {
    var pairSt = new IrStructType("Pair", [new IrField("A", IrType.I32, false)]);
    var iface = new IrInterfaceType("IGet");
    var mIface = new IrMethod("Get", "IGet.Get", false) { Dispatch = IrDispatch.Abstract };
    iface.DefineMembers([mIface], [], []);

    // Name collides with module name "EdgeMod" to test $Type suffixing, plus 0 constructors.
    var collideCls = new IrClassType("EdgeMod") { IsSealed = true };
    collideCls.Define([]);
    collideCls.Inherit(null, []);
    var mCollideStat = new IrMethod("Ping", "EdgeMod.Ping", true);
    collideCls.DefineMembers([], [mCollideStat], [], []);

    // Record class with i32, nested record, non-record object ref, struct field, Memory<int>, u8 field.
    var innerRec = new IrClassType("InnerRec") { IsSealed = true, IsRecord = true };
    var fLabel = new IrField("Label", IrType.I32, true);
    innerRec.Define([fLabel]);
    innerRec.Inherit(null, []);
    var irCtor = new IrConstructor("InnerRec..ctor");
    innerRec.DefineMembers([irCtor], [], [], []);

    var outerRec = new IrClassType("OuterRec") { IsSealed = true, IsRecord = true };
    var fInner = new IrField("In", IrType.Object(innerRec), true);
    var fRef = new IrField("Ref", IrType.Object(collideCls), true);
    var fSt = new IrField("St", IrType.Struct(pairSt), false);
    var fByte = new IrField("Tag", IrType.U8, false);
    outerRec.Define([fInner, fRef, fSt, fByte]);
    outerRec.Inherit(null, [iface]);
    var orCtor = new IrConstructor("OuterRec..ctor");
    var mGet = new IrMethod("Get", "OuterRec.Get", false) { Dispatch = IrDispatch.Virtual, Implements = [mIface] };
    var mGetByte = new IrMethod("GetByte", "OuterRec.GetByte", false);
    outerRec.DefineMembers([orCtor], [mGet, mGetByte], [], []);

    // Derived class for bound Base method + Memory<int> field + FormatException subclass with 2-arg ctor (initCause path).
    var baseHolder = new IrClassType("BaseH");
    var fMem = new IrField("Mem", IrType.Memory(IrType.I32), false);
    baseHolder.Define([fMem]);
    baseHolder.Inherit(null, []);
    var bhCtor = new IrConstructor("BaseH..ctor");
    var bhM = new IrMethod("Num", "BaseH.Num", false) { Dispatch = IrDispatch.Virtual };
    baseHolder.DefineMembers([bhCtor], [bhM], [], []);

    var subHolder = new IrClassType("SubH") { IsSealed = true };
    subHolder.Define([]);
    subHolder.Inherit(baseHolder, []);
    var shCtor = new IrConstructor("SubH..ctor");
    var shM = new IrMethod("Num", "SubH.Num", false) { Dispatch = IrDispatch.Override, Overrides = bhM };
    var shBoundBase = new IrMethod("CallBaseBound", "SubH.CallBaseBound", false);
    subHolder.DefineMembers([shCtor], [shM, shBoundBase], [], []);

    var fmtEx = new IrClassType("FmtEx") { IsSealed = true };
    fmtEx.Define([]);
    fmtEx.Inherit(IrExceptionClass.FormatException, []);
    var fmtCtor = new IrConstructor("FmtEx..ctor2");
    fmtEx.DefineMembers([fmtCtor], [], [], []);

    var irThis = L("this", IrType.Object(innerRec));
    var orThis = L("this", IrType.Object(outerRec));
    var bhThis = L("this", IrType.Object(baseHolder));
    var shThis = L("this", IrType.Object(subHolder));
    var feThis = L("this", IrType.Object(fmtEx));
    var sParam = L("s", IrType.I32);
    var innerParam = L("in", IrType.Object(innerRec));
    var refParam = L("rf", IrType.Object(collideCls));
    var exMsg = L("m", IrType.String);
    var exCause = L("c", IrType.Object(IrExceptionClass.Exception));

    var capParam = L("cp", IrType.I32);
    var addrParam = L("ap", IrType.I32);
    var capSt = L("cs", IrType.Struct(pairSt));
    var uninitAddr = L("ua", IrType.I32);
    var pLocal = L("pl", IrType.Pointer(IrType.I32));
    var pLocal2 = L("pl2", IrType.Pointer(IrType.I32));
    var pArr = L("pa", IrType.Pointer(IrType.I32));
    var pNull = L("pn", IrType.Pointer(IrType.I32));
    var u8Fn = L("u8f", IrType.Function(IrType.U8));
    var stFn = L("stf", IrType.Function(IrType.I32));
    var ifFn = L("iff", IrType.Function(IrType.I32));
    var or1 = L("or1", IrType.Object(outerRec));
    var or2 = L("or2", IrType.Object(outerRec));
    var finCount = L("fc", IrType.I32);
    var strArr = L("sa", IrType.Array(IrType.String));
    var iArr = L("ia", IrType.Array(IrType.I32));
    var stArr = L("sta", IrType.Array(IrType.Struct(pairSt)));
    var u8Arr = L("ba", IrType.Array(IrType.U8));
    var listSt = L("lst", IrType.List(IrType.Struct(pairSt)));
    var listI = L("li", IrType.List(IrType.I32));
    var mapD = L("md", IrType.Map(IrType.F64, IrType.Struct(pairSt), MapOrder.Insertion));
    var setF = L("sf", IrType.Set(IrType.F32, MapOrder.Insertion));
    var seqSt = L("sq", IrType.Sequence(IrType.Struct(pairSt), true));
    var seqU8 = L("sqb", IrType.Sequence(IrType.U8, true));
    var stEl = L("se", IrType.Struct(pairSt));
    var bEl = L("be", IrType.U8);
    var sp1 = L("sp1", IrType.Span(IrType.I32));
    var sp2 = L("sp2", IrType.Span(IrType.I32));
    var bParam = L("bp", IrType.U8);
    var stParam = L("stp", IrType.Struct(pairSt));

    var capLam = IrLambda.Of([], IrType.I32, [], Block(
      new IrAssign(capParam, Bin(IrBinaryOp.Add, R(capParam), I(1))),
      new IrFieldStore(R(capSt), pairSt.Fields[0], I(7)),
      new IrReturn(Bin(IrBinaryOp.Add, R(capParam), new IrFieldLoad(R(capSt), pairSt.Fields[0])))));
    var u8Lam = IrLambda.Of([], IrType.U8, [], Block(new IrReturn(C(IrType.U8, 200))));
    var baseFnType = IrType.Function(IrType.I32);

    var mod = new IrModule("EdgeMod", [
      Function("IGet.Get", false, IrType.I32, [L("this", IrType.Object(iface))]),
      Function("EdgeMod.Ping", false, IrType.I32, [], new IrReturn(I(55))),
      Function("InnerRec..ctor", false, IrType.Void, [irThis, sParam], new IrFieldStore(R(irThis), fLabel, R(sParam))),
      Function("OuterRec..ctor", false, IrType.Void, [orThis, innerParam, refParam],
        new IrFieldStore(R(orThis), fInner, R(innerParam)),
        new IrFieldStore(R(orThis), fRef, R(refParam)),
        new IrFieldStore(R(orThis), fByte, C(IrType.U8, 200))),
      Function("OuterRec.Get", false, IrType.I32, [orThis], new IrReturn(I(0))),
      Function("OuterRec.GetByte", false, IrType.U8, [orThis], new IrReturn(new IrFieldLoad(R(orThis), fByte))),
      Function("BaseH..ctor", false, IrType.Void, [bhThis]),
      Function("BaseH.Num", false, IrType.I32, [bhThis], new IrReturn(Bin(IrBinaryOp.Add, I(11), new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [new IrFieldLoad(R(bhThis), fMem)])))),
      Function("SubH..ctor", false, IrType.Void, [shThis], new IrExprStmt(new IrCall("BaseH..ctor", IrType.Void, [R(shThis)]))),
      Function("SubH.Num", false, IrType.I32, [shThis], new IrReturn(I(99))),
      Function("SubH.CallBaseBound", false, IrType.I32, [shThis],
        new IrReturn(new IrInvoke(IrType.I32, new IrBoundMethod(baseFnType, new IrNew(IrType.Object(baseHolder), bhCtor, []), bhM) { Kind = IrMethodCallKind.Virtual }, []))),
      Function("FmtEx..ctor2", false, IrType.Void, [feThis, exMsg, exCause],
        new IrExprStmt(new IrCall("FormatException..ctor(str,Exception)", IrType.Void, [R(feThis), R(exMsg), R(exCause)]))),
      Function("CallNativeU8", IrType.U8, [], new IrReturn(new IrForeignCall("nat_u8", IrType.U8, []))),
      Function("IterStructAndU8", IrType.Sequence(IrType.Struct(pairSt), true), [bParam, stParam],
        new IrYield(R(stParam))),
      Function("AsyncWithFinally", IrType.Async(IrType.I32), [],
        new IrDeclare(finCount, I(0)),
        new IrTry(
          Block(new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.FromResult, IrType.Async(IrType.I32), [I(33)])))),
          [],
          Block(new IrAssign(finCount, Bin(IrBinaryOp.Add, R(finCount), I(1))))),
        new IrReturn(I(0))),
      Function("UnmatchedFilterFinally", IrType.I32, [],
        new IrDeclare(finCount, I(0)),
        new IrTry(
          Block(new IrTry(
            Block(new IrThrow(new IrNew(IrType.Object(fmtEx), fmtCtor, [new IrStrConst("bad"), new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), [])]))),
            [new IrCatch(fmtEx, null, B(false), Block(new IrReturn(I(-1))))],
            Block(new IrAssign(finCount, I(5))))),
          [new IrCatch(IrExceptionClass.FormatException, null, null, Block(new IrReturn(R(finCount))))],
          null),
        new IrReturn(I(0))),
      Function("ExerciseEdgeCases", IrType.I32, [capParam, addrParam],
        new IrDeclare(capSt, null),
        new IrDeclare(uninitAddr, null),
        new IrDeclare(pLocal, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(addrParam)])),
        new IrDeclare(pLocal2, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(uninitAddr)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(pLocal), I(4)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(pLocal2), I(6)])),
        new IrDeclare(u8Fn, u8Lam),
        new IrDeclare(stFn, new IrBoundMethod(baseFnType, null, mCollideStat)),
        new IrDeclare(or1, new IrNew(IrType.Object(outerRec), orCtor, [new IrNew(IrType.Object(innerRec), irCtor, [I(7)]), new IrNull(IrType.Object(collideCls))])),
        new IrDeclare(or2, new IrNew(IrType.Object(outerRec), orCtor, [new IrNew(IrType.Object(innerRec), irCtor, [I(7)]), new IrNull(IrType.Object(collideCls))])),
        new IrFieldStore(new IrFieldLoad(R(or1), fSt), pairSt.Fields[0], I(2)),
        new IrFieldStore(new IrFieldLoad(R(or2), fSt), pairSt.Fields[0], I(2)),
        new IrDeclare(ifFn, new IrBoundMethod(baseFnType, R(or1), mIface) { Kind = IrMethodCallKind.Interface }),
        // String array allocation & element access
        new IrDeclare(strArr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [new IrStrConst("x"), new IrStrConst("y")])),
        // Collections: ListFromArray, ListGet, ListCopyToArray(struct), Map<f64,struct> with -0/+0, Set<f32> with -0/+0, SetContains, MapContainsKey, empty ListRemoveLast
        new IrDeclare(iArr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(3), I(4)])),
        new IrDeclare(listI, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [R(iArr)])),
        new IrDeclare(stArr, new IrNewArray(IrType.Array(IrType.Struct(pairSt)), I(1))),
        new IrDeclare(listSt, new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.Struct(pairSt)), [R(stArr)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.Struct(pairSt)), [R(listSt)])),
        new IrDeclare(mapD, new IrCollection(IrCollectionOp.MapNew, IrType.Map(IrType.F64, IrType.Struct(pairSt), MapOrder.Insertion), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(mapD), F64(-0.0), new IrStructNew(IrType.Struct(pairSt), [I(8)])])),
        new IrDeclare(setF, new IrCollection(IrCollectionOp.SetNew, IrType.Set(IrType.F32, MapOrder.Insertion), [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(setF), F32(-0.0f)])),
        // Iterators with u8 & struct
        new IrDeclare(u8Arr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [C(IrType.U8, 200)])),
        new IrDeclare(seqU8, new IrArraySequence(R(u8Arr))),
        new IrDeclare(seqSt, new IrCall("IterStructAndU8", IrType.Sequence(IrType.Struct(pairSt), true), [C(IrType.U8, 200), new IrStructNew(IrType.Struct(pairSt), [I(9)])])),
        new IrForEachSequence(bEl, R(seqU8), Block()),
        new IrForEachSequence(stEl, R(seqSt), Block()),
        // Spans & Pointers: ViewTryCopy true/false, Reinterpret, StackAlloc(ptr), Pin(1-arg), PointerCompare all ops, PointerAdd on null
        new IrDeclare(sp1, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(iArr)])),
        new IrDeclare(sp2, new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(IrType.I32), [R(sp1), I(1)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewTryCopy, IrType.Bool, [R(sp1), R(sp2)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewTryCopy, IrType.Bool, [R(sp2), R(sp1)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.Reinterpret, IrType.Span(IrType.I32), [R(sp1)])),
        new IrDeclare(pArr, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Pointer(IrType.I32), [I(2)])),
        new IrDeclare(pNull, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [new IrNewArray(IrType.Array(IrType.I32), I(0))])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.I32), [R(pNull), I(1)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrInvoke(IrType.I32, capLam, []),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrInvoke(IrType.U8, R(u8Fn), [])),
            Bin(IrBinaryOp.Add,
              new IrInvoke(IrType.I32, R(stFn), []),
              Bin(IrBinaryOp.Add,
                new IrInvoke(IrType.I32, R(ifFn), []),
                Bin(IrBinaryOp.Add,
                  new IrMethodCall(IrType.I32, shBoundBase, new IrNew(IrType.Object(subHolder), shCtor, []), []),
                  Bin(IrBinaryOp.Add,
                    new IrConvert(IrType.I32, new IrMethodCall(IrType.U8, mGetByte, R(or1), [])),
                    Bin(IrBinaryOp.Add,
                      new IrConditional(new IrStructEquals(R(or1), R(or2)), I(1), I(0)),
                      Bin(IrBinaryOp.Add,
                        new IrCollection(IrCollectionOp.ListGet, IrType.I32, [R(listI), I(0)]),
                        Bin(IrBinaryOp.Add,
                          new IrFieldLoad(new IrCollection(IrCollectionOp.MapGet, IrType.Struct(pairSt), [R(mapD), F64(0.0)]), pairSt.Fields[0]),
                          Bin(IrBinaryOp.Add,
                            new IrConditional(new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [R(mapD), F64(0.0)]), I(1), I(0)),
                            Bin(IrBinaryOp.Add,
                              new IrConditional(new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [R(setF), F32(0.0f)]), I(1), I(0)),
                              Bin(IrBinaryOp.Add,
                                new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerEqual, IrType.Bool, [R(pLocal), R(pLocal2)]), I(100), I(0)),
                                Bin(IrBinaryOp.Add,
                                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerNotEqual, IrType.Bool, [R(pArr), R(pNull)]), I(1), I(0)),
                                  Bin(IrBinaryOp.Add,
                                    new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerLessOrEqual, IrType.Bool, [R(pArr), R(pArr)]), I(1), I(0)),
                                    Bin(IrBinaryOp.Add,
                                      new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerGreater, IrType.Bool, [R(pArr), R(pArr)]), I(100), I(0)),
                                      new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerGreaterOrEqual, IrType.Bool, [R(pArr), R(pArr)]), I(1), I(0)))))))))))))))))))
    ]) {
      Types = [pairSt, iface, collideCls, innerRec, outerRec, baseHolder, subHolder, fmtEx],
      ForeignFunctions = [new IrForeignFunction("nat_u8", true, "lib", IrType.U8, [])]
    };

    var files = JvmEmitter.Emit(mod);
    var disasm = JvmDisassembler.DisassembleJar(files[0].GetBytes());
    Assert.That(disasm, Does.Contain("EdgeMod$Type"));
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(EdgeMod.AsyncWithFinally().join());
          System.out.println(EdgeMod.UnmatchedFilterFinally());
          System.out.println(EdgeMod.ExerciseEdgeCases(10, 1));
        }
      }
      """);
    // AsyncWithFinally: 33
    // UnmatchedFilterFinally: 5
    // ExerciseEdgeCases: (11+7=18) + 200 + 55 + 0 + 11 + 200 + 1 + 3 + 8 + 1 + 1 + 0 + 1 + 1 + 0 + 1 = 501
    Assert.That(output, Is.EqualTo("33\n5\n501"));
  }

}
