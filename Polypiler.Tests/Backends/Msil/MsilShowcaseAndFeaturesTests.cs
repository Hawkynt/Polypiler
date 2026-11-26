using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>
/// End-to-end live execution, disassembly, and edge-case tests covering all 10 showcase files
/// (object model, generics, closures, collections, iterators, exceptions/using, async/await,
/// spans, pointers, and bit permutations) across both <c>msil/net10</c> and <c>msil/net8</c>.
/// </summary>
[TestFixture]
public sealed class MsilShowcaseAndFeaturesTests {

  private static string ShowcasePath(string fileName) {
    var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (dir is not null) {
      var candidate = Path.Combine(dir.FullName, "docs", "showcase", fileName);
      if (File.Exists(candidate))
        return candidate;
      dir = dir.Parent;
    }
    throw new FileNotFoundException($"Could not locate showcase file '{fileName}'.");
  }

  private static IrModule ImportShowcase(string fileName) {
    var path = ShowcasePath(fileName);
    var source = File.ReadAllText(path);
    var imported = CSharpFrontend.Import(fileName, source);
    Assert.That(imported.Diagnostics, Is.Empty, $"Frontend diagnostics for {fileName}: {string.Join("; ", imported.Diagnostics.Select(d => d.Message))}");
    return Legalize(imported.Module!);
  }

  private static void EmitDisassembleAndVerify(IrModule module, Action<Assembly> verify, params MsilProfile[] profiles) {
    var targets = profiles.Length > 0 ? profiles : [MsilProfile.Net10, MsilProfile.Net8];
    foreach (var profile in targets) {
      var diagnostics = MsilEmitter.Check(module, profile);
      Assert.That(diagnostics, Is.Empty, $"Check failed for {module.Name} ({profile}): {string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");

      var files = MsilEmitter.Emit(module, profile);
      Assert.That(files, Has.Count.EqualTo(1));
      var bytes = files[0].BinaryContent!;
      Assert.That(bytes, Is.Not.Null.And.Length.GreaterThan(0));

      var il = MsilDisassembler.Disassemble(bytes);
      Assert.That(il, Does.Contain(".assembly"));

      WithLoadedAssembly(module, verify, profile);
    }
  }

  private static object? InvokeMethod(Assembly assembly, IrModule module, string methodName, params object?[] args) {
    var type = assembly.GetType(module.Name) ?? assembly.GetTypes().Last();
    var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null, $"Method '{methodName}' not found on '{type.FullName}'.");
    try {
      return method!.Invoke(null, args);
    } catch (TargetInvocationException ex) when (ex.InnerException is not null) {
      throw ex.InnerException;
    }
  }

  private static object? Interp(IrModule module, string name, params object[] args)
    => IrInterpreter.Call(module, name, args);

  [Test]
  public void Showcase03_Overloading_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("03-overloading.cs");
    var expected = Interp(module, "Run", 10, 3, 4);
    Assert.That(expected, Is.EqualTo(20));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Run", 10, 3, 4), Is.EqualTo(expected));
      Assert.That(InvokeMethod(asm, module, "Run", 0, 5, -2), Is.EqualTo(Interp(module, "Run", 0, 5, -2)));
    });
  }

  [Test]
  public void Showcase05_ClassesAndProperties_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("05-classes-and-properties.cs");
    var expected = Interp(module, "Measure", 4, 7);
    Assert.That(expected, Is.EqualTo(28));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Measure", 4, 7), Is.EqualTo(expected));
      Assert.That(InvokeMethod(asm, module, "Measure", 3, 0), Is.EqualTo(Interp(module, "Measure", 3, 0)));
    });
  }

  [Test]
  public void Showcase07_InheritanceAndInterfaces_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("07-inheritance-and-interfaces.cs");
    var rectExpected = Interp(module, "Measure", true, 3, 4);
    var sqExpected = Interp(module, "Measure", false, 5, 0);
    Assert.That(rectExpected, Is.EqualTo(24));
    Assert.That(sqExpected, Is.EqualTo(26));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Measure", true, 3, 4), Is.EqualTo(rectExpected));
      Assert.That(InvokeMethod(asm, module, "Measure", false, 5, 0), Is.EqualTo(sqExpected));
    });
  }

  [Test]
  public void Showcase08_Generics_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("08-generics.cs");
    var e1 = Interp(module, "Run", 150, -100L);
    var e2 = Interp(module, "Run", -5, 25L);
    var e3 = Interp(module, "Run", 40, 80L);
    Assert.That(e1, Is.EqualTo(50L));
    Assert.That(e2, Is.EqualTo(25L));
    Assert.That(e3, Is.EqualTo(90L));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Run", 150, -100L), Is.EqualTo(e1));
      Assert.That(InvokeMethod(asm, module, "Run", -5, 25L), Is.EqualTo(e2));
      Assert.That(InvokeMethod(asm, module, "Run", 40, 80L), Is.EqualTo(e3));
    });
  }

  [Test]
  public void Showcase09_LambdasAndClosures_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("09-lambdas-and-closures.cs");
    var expected = Interp(module, "Run", 10, 5);
    Assert.That(expected, Is.EqualTo(45));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Run", 10, 5), Is.EqualTo(45));
      Assert.That(InvokeMethod(asm, module, "Run", 3, 2), Is.EqualTo(Interp(module, "Run", 3, 2)));
    });
  }

  [Test]
  public void Showcase12_Collections_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("12-collections.cs");
    int[] sample = [1, -2, 1, 3, 0, -2, 5];
    var expected = Interp(module, "Run", (object)sample);

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Run", (object)sample), Is.EqualTo(expected));
      Assert.That(InvokeMethod(asm, module, "Run", (object)Array.Empty<int>()), Is.EqualTo(Interp(module, "Run", (object)Array.Empty<int>())));
    });
  }

  [Test]
  public void Showcase13_IteratorsAndYield_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("13-iterators-and-yield.cs");
    Assert.That(Interp(module, "SumEvens", 10), Is.EqualTo(30));
    Assert.That(Interp(module, "SumEvens", 200), Is.EqualTo(2550));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "SumEvens", 10), Is.EqualTo(30));
      Assert.That(InvokeMethod(asm, module, "SumEvens", 200), Is.EqualTo(2550));
      Assert.That(InvokeMethod(asm, module, "SumEvens", -1), Is.EqualTo(0));
    });
  }

  [Test]
  public void Showcase15_ExceptionsAndUsing_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("15-exceptions-and-using.cs");
    var posExpected = Interp(module, "Evaluate", 10);
    var negExpected = Interp(module, "Evaluate", -3);
    Assert.That(posExpected, Is.EqualTo(21));
    Assert.That(negExpected, Is.EqualTo(0));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "Evaluate", 10), Is.EqualTo(posExpected));
      Assert.That(InvokeMethod(asm, module, "Evaluate", -3), Is.EqualTo(negExpected));
    });
  }

  [Test]
  public void Showcase16_TasksAndAsync_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("16-tasks-and-async.cs");
    var expected = Interp(module, "CombineAsync", 3, 4);
    Assert.That(expected, Is.EqualTo(27));

    EmitDisassembleAndVerify(module, asm => {
      var task = (Task<int>)InvokeMethod(asm, module, "CombineAsync", 3, 4)!;
      Assert.That(task.GetAwaiter().GetResult(), Is.EqualTo(27));
      var task2 = (Task<int>)InvokeMethod(asm, module, "CombineAsync", 0, 5)!;
      Assert.That(task2.GetAwaiter().GetResult(), Is.EqualTo(Interp(module, "CombineAsync", 0, 5)));
    });
  }

  [Test]
  public void Showcase18_SpansAndPointers_LiveExecutionAndDisassembly() {
    var module = ImportShowcase("18-spans-and-pointers.cs");
    var pextExpected = Interp(module, "ExtractBits", 0xABCD_1234u, 0x0F0F_00FFu);
    int[] data = [10, 20, 30, 40, 50];
    var sumExpected = Interp(module, "SumSlice", data, 1, 3);
    Assert.That(sumExpected, Is.EqualTo(90));

    EmitDisassembleAndVerify(module, asm => {
      Assert.That(InvokeMethod(asm, module, "ExtractBits", 0xABCD_1234u, 0x0F0F_00FFu), Is.EqualTo(pextExpected));
      Assert.That(InvokeMethod(asm, module, "SumSlice", data, 1, 3), Is.EqualTo(sumExpected));
      Assert.That(InvokeMethod(asm, module, "SumSlice", data, 0, 0), Is.EqualTo(0));
    });
  }

  [Test]
  public void ObjectModel_BaseDispatchCastingRecordsAndGenericOps() {
    // Interfaces with base interface, abstract/virtual/override/sealed override/base calls, static method, this-chaining,
    // SkipsBase, Cast, AsCast, TypeTest, ReferenceEquals, RecordEquals (nested struct/record/string/f32/f64/null), GenericEquals/Compare.
    var iBase = new IrInterfaceType("IBase");
    var mBase = new IrMethod("BaseVal", "IBase.BaseVal", false) { Dispatch = IrDispatch.Abstract };
    iBase.DefineMembers([mBase], [], []);

    var iDerived = new IrInterfaceType("IDerived");
    var mExtra = new IrMethod("Extra", "IDerived.Extra", false) { Dispatch = IrDispatch.Abstract };
    iDerived.DefineMembers([mExtra], [], [iBase]);

    var iBaseSelf = L("this", IrType.Object(iBase));
    var iDerivedSelf = L("this", IrType.Object(iDerived));
    var iBaseSig = Function("IBase.BaseVal", false, IrType.I32, [iBaseSelf]);
    var iDerivedSig = Function("IDerived.Extra", false, IrType.I32, [iDerivedSelf]);

    var pairStruct = new IrStructType("Pair", [new IrField("A", IrType.I32, false), new IrField("B", IrType.F64, false)]);

    var innerRec = new IrClassType("InnerRec") { IsRecord = true, IsSealed = true };
    var irVal = new IrField("V", IrType.I32, true);
    innerRec.Define([irVal]);
    innerRec.Inherit(null, []);
    var irCtor = new IrConstructor("InnerRec..ctor");
    innerRec.DefineMembers([irCtor], [], [], []);

    var outerRec = new IrClassType("OuterRec") { IsRecord = true, IsSealed = true };
    var orPair = new IrField("P", IrType.Struct(pairStruct), true);
    var orInner = new IrField("In", IrType.Object(innerRec), true);
    var orF32 = new IrField("F", IrType.F32, true);
    var orF64 = new IrField("D", IrType.F64, true);
    var orTag = new IrField("Tag", IrType.I32, true);
    outerRec.Define([orPair, orInner, orF32, orF64, orTag]);
    outerRec.Inherit(null, []);
    var orCtor = new IrConstructor("OuterRec..ctor");
    outerRec.DefineMembers([orCtor], [], [], []);

    var baseCls = new IrClassType("BaseNode");
    var bVal = new IrField("Val", IrType.I32, false);
    var bPair = new IrField("Pair", IrType.Struct(pairStruct), false);
    baseCls.Define([bVal, bPair]);
    baseCls.Inherit(null, [iBase]);
    var bCtor1 = new IrConstructor("BaseNode..ctor1");
    var bCtor2 = new IrConstructor("BaseNode..ctor2");
    var bVirtual = new IrMethod("BaseVal", "BaseNode.BaseVal", false) { Dispatch = IrDispatch.Virtual, Implements = [mBase] };
    var bStatic = new IrMethod("Double", "BaseNode.Double", true) { Dispatch = IrDispatch.Static };
    baseCls.DefineMembers([bCtor1, bCtor2], [bVirtual, bStatic], [], []);

    var derivedCls = new IrClassType("DerivedNode") { IsSealed = true };
    derivedCls.Define([]);
    derivedCls.Inherit(baseCls, [iDerived]);
    var dCtor = new IrConstructor("DerivedNode..ctor");
    var dOverride = new IrMethod("BaseVal", "DerivedNode.BaseVal", false) { Dispatch = IrDispatch.Override, Overrides = bVirtual, IsSealedOverride = true };
    var dExtra = new IrMethod("Extra", "DerivedNode.Extra", false) { Dispatch = IrDispatch.Instance, Implements = [mExtra] };
    derivedCls.DefineMembers([dCtor], [dOverride, dExtra], [], []);

    var skipCls = new IrClassType("SkipNode") { IsSealed = true };
    skipCls.Define([]);
    skipCls.Inherit(baseCls, []);
    var sCtor = new IrConstructor("SkipNode..ctor") { SkipsBase = true };
    skipCls.DefineMembers([sCtor], [], [], []);

    var irSelf = L("this", IrType.Object(innerRec));
    var irP = L("v", IrType.I32);
    var irCtorFn = Function(irCtor.Function, false, IrType.Void, [irSelf, irP],
      new IrFieldStore(R(irSelf), irVal, R(irP)));

    var orSelf = L("this", IrType.Object(outerRec));
    var orP0 = L("p", IrType.Struct(pairStruct));
    var orP1 = L("in", IrType.Object(innerRec));
    var orP3 = L("f", IrType.F32);
    var orP4 = L("d", IrType.F64);
    var orP5 = L("t", IrType.I32);
    var orCtorFn = Function(orCtor.Function, false, IrType.Void, [orSelf, orP0, orP1, orP3, orP4, orP5],
      new IrFieldStore(R(orSelf), orPair, R(orP0)),
      new IrFieldStore(R(orSelf), orInner, R(orP1)),
      new IrFieldStore(R(orSelf), orF32, R(orP3)),
      new IrFieldStore(R(orSelf), orF64, R(orP4)),
      new IrFieldStore(R(orSelf), orTag, R(orP5)));

    var bSelf1 = L("this", IrType.Object(baseCls));
    var bArg = L("v", IrType.I32);
    var bCtor1Fn = Function(bCtor1.Function, false, IrType.Void, [bSelf1, bArg],
      new IrFieldStore(R(bSelf1), bVal, R(bArg)),
      new IrFieldStore(new IrFieldLoad(R(bSelf1), bPair), pairStruct.Fields[0], R(bArg)));

    var bSelf2 = L("this", IrType.Object(baseCls));
    var bCtor2Fn = Function(bCtor2.Function, false, IrType.Void, [bSelf2],
      new IrExprStmt(new IrCall(bCtor1.Function, IrType.Void, [R(bSelf2), I(7)])));

    var bSelfM = L("this", IrType.Object(baseCls));
    var bVirtualFn = Function(bVirtual.Function, false, IrType.I32, [bSelfM],
      new IrReturn(new IrFieldLoad(R(bSelfM), bVal)));

    var bStatArg = L("x", IrType.I32);
    var bStaticFn = Function(bStatic.Function, false, IrType.I32, [bStatArg],
      new IrReturn(Bin(IrBinaryOp.Multiply, R(bStatArg), I(2))));

    var dSelf = L("this", IrType.Object(derivedCls));
    var dArg = L("v", IrType.I32);
    var dCtorFn = Function(dCtor.Function, false, IrType.Void, [dSelf, dArg],
      new IrExprStmt(new IrCall(bCtor1.Function, IrType.Void, [R(dSelf), R(dArg)])));

    var dSelfO = L("this", IrType.Object(derivedCls));
    var dOverrideFn = Function(dOverride.Function, false, IrType.I32, [dSelfO],
      new IrReturn(Bin(IrBinaryOp.Add,
        new IrMethodCall(IrType.I32, bVirtual, R(dSelfO), []) { Kind = IrMethodCallKind.Base },
        I(100))));

    var dSelfE = L("this", IrType.Object(derivedCls));
    var dExtraFn = Function(dExtra.Function, false, IrType.I32, [dSelfE],
      new IrReturn(new IrMethodCall(IrType.I32, bStatic, null, [I(21)]) { Kind = IrMethodCallKind.Direct }));

    var sSelf = L("this", IrType.Object(skipCls));
    var sCtorFn = Function(sCtor.Function, false, IrType.Void, [sSelf],
      new IrFieldStore(R(sSelf), bVal, I(55)));

    var flag = L("flag", IrType.Bool);
    var node = L("node", IrType.Object(baseCls));
    var asDerived = L("ad", IrType.Object(derivedCls));
    var castObj = L("co", IrType.Object(baseCls));
    var r1 = L("r1", IrType.Object(outerRec));
    var r2 = L("r2", IrType.Object(outerRec));
    var r3 = L("r3", IrType.Object(outerRec));
    var s1 = L("s1", IrType.String);
    var s2 = L("s2", IrType.String);
    var fa = L("fa", IrType.F32);
    var fb = L("fb", IrType.F32);
    var da = L("da", IrType.F64);
    var db = L("db", IrType.F64);
    var b1 = L("b1", IrType.I8);
    var b2 = L("b2", IrType.I8);
    var u1 = L("u1", IrType.U8);
    var u2 = L("u2", IrType.U8);
    var sh1 = L("sh1", IrType.I16);
    var sh2 = L("sh2", IrType.I16);
    var ush1 = L("ush1", IrType.U16);
    var ush2 = L("ush2", IrType.U16);
    var ui1 = L("ui1", IrType.U32);
    var ui2 = L("ui2", IrType.U32);
    var ul1 = L("ul1", IrType.U64);
    var ul2 = L("ul2", IrType.U64);

    var mod = new IrModule("ObjMod", [
      iBaseSig, iDerivedSig, irCtorFn, orCtorFn, bCtor1Fn, bCtor2Fn, bVirtualFn, bStaticFn, dCtorFn, dOverrideFn, dExtraFn, sCtorFn,
      Function("TestHierarchy", IrType.I32, [flag],
        new IrDeclare(node, null),
        new IrIf(R(flag),
          Block(new IrAssign(node, new IrNew(IrType.Object(derivedCls), dCtor, [I(5)]))),
          Block(new IrAssign(node, new IrNew(IrType.Object(baseCls), bCtor2, [])))),
        new IrDeclare(asDerived, new IrAsCast(IrType.Object(derivedCls), R(node))),
        new IrDeclare(castObj, new IrNew(IrType.Object(skipCls), sCtor, [])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, bVirtual, R(node), []) { Kind = IrMethodCallKind.Virtual },
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrReferenceEquals(R(asDerived), new IrNull(IrType.Object(derivedCls))),
              I(0),
              new IrMethodCall(IrType.I32, mExtra, new IrCast(IrType.Object(iDerived), R(node)), []) { Kind = IrMethodCallKind.Interface }),
            new IrFieldLoad(R(castObj), bVal))))),
      Function("BadCast", IrType.Object(derivedCls), [],
        new IrReturn(new IrCast(IrType.Object(derivedCls), new IrNew(IrType.Object(baseCls), bCtor2, [])))),
      Function("TestRecords", IrType.I32, [],
        new IrDeclare(r1, new IrNew(IrType.Object(outerRec), orCtor, [new IrStructNew(IrType.Struct(pairStruct), [I(1), F64(2.0)]), new IrNew(IrType.Object(innerRec), irCtor, [I(9)]), F32(float.NaN), F64(-0.0), I(1)])),
        new IrDeclare(r2, new IrNew(IrType.Object(outerRec), orCtor, [new IrStructNew(IrType.Struct(pairStruct), [I(1), F64(2.0)]), new IrNew(IrType.Object(innerRec), irCtor, [I(9)]), F32(float.NaN), F64(0.0), I(1)])),
        new IrDeclare(r3, new IrNew(IrType.Object(outerRec), orCtor, [new IrStructNew(IrType.Struct(pairStruct), [I(1), F64(2.0)]), new IrNew(IrType.Object(innerRec), irCtor, [I(8)]), F32(float.NaN), F64(0.0), I(1)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(new IrStructEquals(R(r1), R(r2)), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrGenericEquals(R(r1), R(r1)), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrStructEquals(R(r1), new IrNull(IrType.Object(outerRec))), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrStructEquals(new IrNull(IrType.Object(outerRec)), R(r1)), I(8), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrStructEquals(new IrNull(IrType.Object(outerRec)), new IrNull(IrType.Object(outerRec))), I(16), I(0)),
                  new IrConditional(new IrStructEquals(R(r1), R(r3)), I(32), I(0))))))))),
      Function("TestGenericOps", IrType.I32, [s1, s2, fa, fb, da, db, b1, b2, u1, u2, sh1, sh2, ush1, ush2, ui1, ui2, ul1, ul2],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(new IrGenericEquals(R(fa), R(fb)), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrGenericEquals(R(da), R(db)), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrGenericEquals(R(b1), R(b2)), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrGenericCompare(B(false), B(true)),
                Bin(IrBinaryOp.Add,
                  new IrGenericCompare(R(b1), R(b2)),
                  Bin(IrBinaryOp.Add,
                    new IrGenericCompare(R(u1), R(u2)),
                    Bin(IrBinaryOp.Add,
                      new IrGenericCompare(R(sh1), R(sh2)),
                      Bin(IrBinaryOp.Add,
                        new IrGenericCompare(R(ush1), R(ush2)),
                        Bin(IrBinaryOp.Add,
                          new IrGenericCompare(R(ui1), R(ui2)),
                          Bin(IrBinaryOp.Add,
                            new IrGenericCompare(R(ul1), R(ul2)),
                            Bin(IrBinaryOp.Add,
                              new IrGenericCompare(R(fa), R(fb)),
                              new IrGenericCompare(R(da), R(db)))))))))))))))
    ]) {
      Types = [iBase, iDerived, pairStruct, innerRec, outerRec, baseCls, derivedCls, skipCls]
    };

    AssertMatchesInterpreter(mod, "TestHierarchy", true);
    AssertMatchesInterpreter(mod, "TestHierarchy", false);
    Assert.That(() => Invoke(mod, "BadCast"), Throws.TypeOf<InvalidCastException>());
    AssertMatchesInterpreter(mod, "TestRecords");
    AssertMatchesInterpreter(mod, "TestGenericOps", "a", "b", float.NaN, 1.0f, -0.0, 0.0, (sbyte)-2, (sbyte)3, (byte)1, (byte)5, (short)10, (short)4, (ushort)2, (ushort)8, 10u, 20u, 30UL, 10UL);
  }

  [Test]
  public void Closures_CapturedParamNestedPerIterationFnRefAndBoundMethod() {
    var cls = new IrClassType("Adder") { IsSealed = true };
    var fBase = new IrField("Base", IrType.I32, false);
    cls.Define([fBase]);
    cls.Inherit(null, []);
    var ctor = new IrConstructor("Adder..ctor");
    var addM = new IrMethod("Add", "Adder.Add", false) { Dispatch = IrDispatch.Instance };
    var mulM = new IrMethod("Mul", "Adder.Mul", true) { Dispatch = IrDispatch.Static };
    cls.DefineMembers([ctor], [addM, mulM], [], []);

    var selfC = L("this", IrType.Object(cls));
    var seedP = L("seed", IrType.I32);
    var ctorFn = Function(ctor.Function, false, IrType.Void, [selfC, seedP], new IrFieldStore(R(selfC), fBase, R(seedP)));
    var selfA = L("this", IrType.Object(cls));
    var xA = L("x", IrType.I32);
    var addFn = Function(addM.Function, false, IrType.I32, [selfA, xA], new IrReturn(Bin(IrBinaryOp.Add, new IrFieldLoad(R(selfA), fBase), R(xA))));
    var xM = L("x", IrType.I32);
    var mulFn = Function(mulM.Function, false, IrType.I32, [xM], new IrReturn(Bin(IrBinaryOp.Multiply, R(xM), I(3))));

    var topX = L("x", IrType.I32);
    var topFn = Function("PlusTen", IrType.I32, [topX], new IrReturn(Bin(IrBinaryOp.Add, R(topX), I(10))));

    // Captured parameter + nested lambda + IrFor PerIteration + IrFunctionRef + IrBoundMethod (instance + static + null receiver)
    var p = L("p", IrType.I32);
    var fnType = IrType.Function(IrType.I32, IrType.I32);
    var noArgFnType = IrType.Function(IrType.I32);
    var innerArg = L("y", IrType.I32);
    var innerLam = IrLambda.Of([innerArg], IrType.I32, [], Block(
      new IrAssign(p, Bin(IrBinaryOp.Add, R(p), R(innerArg))),
      new IrReturn(R(p))));
    var outerLam = IrLambda.Of([], fnType, [], Block(new IrReturn(innerLam)));
    var f1 = L("f1", IrType.Function(fnType));
    var f2 = L("f2", fnType);

    var i = L("i", IrType.I32);
    var arr = L("arr", IrType.Array(noArgFnType));
    var iterLam = IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(i))));
    var loop = new IrFor(
      Bin(IrBinaryOp.Less, R(i), I(3)),
      Block(new IrStore(R(arr), R(i), iterLam)),
      Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))) {
      PerIteration = [i]
    };

    var fnRefLocal = L("fr", fnType);
    var boundInstLocal = L("bi", fnType);
    var boundStatLocal = L("bs", fnType);
    var objLocal = L("obj", IrType.Object(cls));

    var mod = new IrModule("ClosMod", [
      ctorFn, addFn, mulFn, topFn,
      Function("NestedAndParam", IrType.I32, [p],
        new IrDeclare(f1, outerLam),
        new IrDeclare(f2, new IrInvoke(fnType, R(f1), [])),
        new IrReturn(Bin(IrBinaryOp.Add, new IrInvoke(IrType.I32, R(f2), [I(5)]), R(p)))),
      Function("PerIter", IrType.I32, [],
        new IrDeclare(arr, new IrNewArray(IrType.Array(noArgFnType), I(3))),
        new IrDeclare(i, I(0)),
        loop,
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrInvoke(IrType.I32, new IrArrayLoad(R(arr), I(0)), []),
          Bin(IrBinaryOp.Add,
            new IrInvoke(IrType.I32, new IrArrayLoad(R(arr), I(1)), []),
            new IrInvoke(IrType.I32, new IrArrayLoad(R(arr), I(2)), []))))),
      Function("RefsAndBounds", IrType.I32, [],
        new IrDeclare(objLocal, new IrNew(IrType.Object(cls), ctor, [I(100)])),
        new IrDeclare(fnRefLocal, new IrFunctionRef(fnType, "PlusTen")),
        new IrDeclare(boundInstLocal, new IrBoundMethod(fnType, R(objLocal), addM) { Kind = IrMethodCallKind.Direct }),
        new IrDeclare(boundStatLocal, new IrBoundMethod(fnType, null, mulM) { Kind = IrMethodCallKind.Direct }),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrInvoke(IrType.I32, R(fnRefLocal), [I(5)]),
          Bin(IrBinaryOp.Add,
            new IrInvoke(IrType.I32, R(boundInstLocal), [I(7)]),
            new IrInvoke(IrType.I32, R(boundStatLocal), [I(4)]))))),
      Function("NullBound", fnType, [],
        new IrReturn(new IrBoundMethod(fnType, new IrNull(IrType.Object(cls)), addM) { Kind = IrMethodCallKind.Direct }))
    ]) { Types = [cls] };

    AssertMatchesInterpreter(mod, "NestedAndParam", 10);
    AssertMatchesInterpreter(mod, "PerIter");
    AssertMatchesInterpreter(mod, "RefsAndBounds");
    Assert.That(() => Invoke(mod, "NullBound"), Throws.TypeOf<NullReferenceException>());
  }

  [Test]
  public void Collections_AllListMapSetOpsAndForEachModes() {
    var ptStruct = new IrStructType("Pt", [new IrField("X", IrType.I32, false)]);
    var recCls = new IrClassType("ItemRec") { IsRecord = true, IsSealed = true };
    var recId = new IrField("Id", IrType.I32, true);
    recCls.Define([recId]);
    recCls.Inherit(null, []);
    var recCtor = new IrConstructor("ItemRec..ctor");
    recCls.DefineMembers([recCtor], [], [], []);
    var rSelf = L("this", IrType.Object(recCls));
    var rId = L("id", IrType.I32);
    var recCtorFn = Function(recCtor.Function, false, IrType.Void, [rSelf, rId], new IrFieldStore(R(rSelf), recId, R(rId)));

    var listT = IrType.List(IrType.I32);
    var flistT = IrType.List(IrType.F64);
    var slistT = IrType.List(IrType.String);
    var ptListT = IrType.List(IrType.Struct(ptStruct));
    var recListT = IrType.List(IrType.Object(recCls));
    var mapT = IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse);
    var objMapT = IrType.Map(IrType.I32, IrType.Object(recCls), MapOrder.SlotReuse);
    var setT = IrType.Set(IrType.I32, MapOrder.SlotReuse);

    var xs = L("xs", listT);
    var ys = L("ys", listT);
    var fl = L("fl", flistT);
    var sl = L("sl", slistT);
    var pl = L("pl", ptListT);
    var rl = L("rl", recListT);
    var m = L("m", mapT);
    var om = L("om", objMapT);
    var s = L("s", setT);
    var sum = L("sum", IrType.I32);
    var e = L("e", IrType.I32);
    var k = L("k", IrType.String);
    var v = L("v", IrType.I32);
    var arrP = L("arr", IrType.Array(IrType.I32));
    var popOpt = L("pop", IrType.Optional(IrType.I32));
    var popRec = L("popRec", IrType.Object(recCls));

    var mod = new IrModule("CollMod", [
      recCtorFn,
      Function("ListAllOps", IrType.I32, [arrP],
        new IrDeclare(xs, new IrCollection(IrCollectionOp.ListFromArray, listT, [R(arrP)])),
        new IrDeclare(ys, new IrCollection(IrCollectionOp.ListOf, listT, [I(10), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(xs), I(99)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [R(xs), I(1), I(50)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [R(xs), I(0), I(7)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [R(xs), I(1)])),
        new IrDeclare(popOpt, new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(xs)])),
        new IrDeclare(sum, new IrCoalesce(R(popOpt), I(-1))),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [R(ys)])),
        new IrAssign(popOpt, new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(ys)])),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCoalesce(R(popOpt), I(5)))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListGet, IrType.I32, [R(xs), I(0)]))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(xs), I(7)]), I(10), I(0)))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(xs), I(7)]))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrArrayLength(new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), [R(xs)])))),
        new IrForEachCollection(R(xs), EnumerationMode.Checked, e, null, Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(e))))),
        new IrForEachCollection(R(xs), EnumerationMode.Live, e, null, Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(e))))),
        new IrReturn(R(sum))),
      Function("ListStrictAndRecords", IrType.I32, [],
        new IrDeclare(fl, new IrCollection(IrCollectionOp.ListOf, flistT, [F64(double.NaN), F64(2.5)])),
        new IrDeclare(sl, new IrCollection(IrCollectionOp.ListOf, slistT, [new IrStrConst("a"), new IrStrConst("b")])),
        new IrDeclare(pl, new IrCollection(IrCollectionOp.ListOf, ptListT, [new IrStructNew(IrType.Struct(ptStruct), [I(3)])])),
        new IrDeclare(rl, new IrCollection(IrCollectionOp.ListOf, recListT, [new IrNew(IrType.Object(recCls), recCtor, [I(42)])])),
        new IrDeclare(popRec, new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Object(recCls), [R(rl)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(rl), R(popRec)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrCollection(IrCollectionOp.ListIndexOfStrict, IrType.I32, [R(fl), F64(double.NaN)]),
          Bin(IrBinaryOp.Add,
            new IrCollection(IrCollectionOp.ListIndexOfStrict, IrType.I32, [R(sl), new IrStrConst("b")]),
            Bin(IrBinaryOp.Add,
              new IrCollection(IrCollectionOp.ListIndexOfStrict, IrType.I32, [R(pl), new IrStructNew(IrType.Struct(ptStruct), [I(3)])]),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrCollection(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, [R(fl), F64(double.NaN)]), I(10), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(rl), new IrNew(IrType.Object(recCls), recCtor, [I(42)])]), I(20), I(0)),
                  new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(rl), new IrNew(IrType.Object(recCls), recCtor, [I(42)])])))))))),
      Function("MapAndSetOps", IrType.I32, [],
        new IrDeclare(m, new IrCollection(IrCollectionOp.MapNew, mapT, [])),
        new IrDeclare(om, new IrCollection(IrCollectionOp.MapNew, objMapT, [])),
        new IrDeclare(s, new IrCollection(IrCollectionOp.SetNew, setT, [])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(m), new IrStrConst("a"), I(10)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(m), new IrStrConst("b"), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(m), new IrStrConst("a"), I(99)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(om), I(1), new IrNew(IrType.Object(recCls), recCtor, [I(7)])])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(s), I(3)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(s), I(5)])),
        new IrDeclare(sum, Bin(IrBinaryOp.Add,
          new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(m), new IrStrConst("a")]),
          Bin(IrBinaryOp.Add,
            new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(m)]),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [R(m), new IrStrConst("b")]), I(100), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIsNull(new IrCollection(IrCollectionOp.MapTryGet, IrType.Object(recCls), [R(om), I(99)])), I(1), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrFieldLoad(new IrCollection(IrCollectionOp.MapTryGet, IrType.Object(recCls), [R(om), I(1)]), recId),
                  Bin(IrBinaryOp.Add,
                    new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(s)]),
                    new IrConditional(new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [R(s), I(3)]), I(2), I(0))))))))),
        new IrForEachCollection(R(m), EnumerationMode.Checked, k, v, Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), Bin(IrBinaryOp.Add, R(v), new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(k)])))))),
        new IrForEachCollection(R(s), EnumerationMode.Checked, e, null, Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(e))))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(m), new IrStrConst("a")])),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapClear, IrType.Void, [R(m)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(s), I(3)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.SetClear, IrType.Void, [R(s)])),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum), Bin(IrBinaryOp.Add, new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(m)]), new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(s)]))))),
      Function("ListFromNull", listT, [],
        new IrReturn(new IrCollection(IrCollectionOp.ListFromArray, listT, [new IrNull(IrType.Array(IrType.I32))])))
    ]) { Types = [ptStruct, recCls] };

    AssertMatchesInterpreter(mod, "ListAllOps", new int[] { 1, 2, 3 });
    AssertMatchesInterpreter(mod, "ListStrictAndRecords");
    AssertMatchesInterpreter(mod, "MapAndSetOps");
    Assert.That(() => Invoke(mod, "ListFromNull"), Throws.TypeOf<NullReferenceException>());
  }

  [Test]
  public void IteratorsAndAsync_YieldFromSinglePassArraySeqAndAsyncVoidFault() {
    var seqT = IrType.Sequence(IrType.I32, restartable: true);
    var spSeqT = IrType.Sequence(IrType.I32, restartable: false);
    var n = L("n", IrType.I32);
    var item = L("item", IrType.I32);
    var total = L("total", IrType.I32);
    var seqLocal = L("seq", seqT);
    var spLocal = L("sp", spSeqT);
    var arrP = L("arr", IrType.Array(IrType.I32));
    var capN = L("cn", IrType.I32);
    var capLam = IrLambda.Of([], IrType.I32, [], Block(new IrReturn(R(capN))));
    var failFlag = L("fail", IrType.Bool);

    var finallyCount = new IrGlobal("FinallyCount", IrType.I32, I(0));
    var mod = new IrModule("IterAsyncMod", [
      // Restartable iterator with yield, yield from (IrArraySequence), yield break inside try/finally, and captured parameter.
      Function("Gen", seqT, [capN, arrP],
        new IrYield(new IrInvoke(IrType.I32, capLam, [])),
        new IrYieldFrom(new IrArraySequence(R(arrP))),
        new IrTry(Block(new IrYieldBreak()), [], Block(new IrGlobalAssign(finallyCount, Bin(IrBinaryOp.Add, new IrGlobalRef(finallyCount), I(1)))))),
      // Single-pass iterator
      Function("Once", spSeqT, [n],
        new IrYield(R(n)),
        new IrYield(Bin(IrBinaryOp.Add, R(n), I(1)))),
      Function("RunIters", IrType.I32, [arrP],
        new IrDeclare(seqLocal, new IrCall("Gen", seqT, [I(5), R(arrP)])),
        new IrDeclare(spLocal, new IrCall("Once", spSeqT, [I(10)])),
        new IrDeclare(total, I(0)),
        // Enumerate restartable twice
        new IrForEachSequence(item, R(seqLocal), Block(new IrAssign(total, Bin(IrBinaryOp.Add, R(total), R(item))))),
        new IrForEachSequence(item, R(seqLocal), Block(new IrAssign(total, Bin(IrBinaryOp.Add, R(total), R(item))))),
        // Enumerate single-pass twice (second yields nothing)
        new IrForEachSequence(item, R(spLocal), Block(new IrAssign(total, Bin(IrBinaryOp.Add, R(total), R(item))))),
        new IrForEachSequence(item, R(spLocal), Block(new IrAssign(total, Bin(IrBinaryOp.Add, R(total), R(item))))),
        new IrReturn(Bin(IrBinaryOp.Add, R(total), new IrGlobalRef(finallyCount)))),
      // Async void with CompletedTask, WhenAll(Task[]), and fault propagation
      Function("WorkVoid", IrType.Async(IrType.Void), [failFlag],
        new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []))),
        new IrIf(R(failFlag),
          Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("void-fault")]))),
          null)),
      Function("RunAsyncVoid", IrType.Async(IrType.Void), [failFlag],
        new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [
          new IrCall("WorkVoid", IrType.Async(IrType.Void), [B(false)]),
          new IrCall("WorkVoid", IrType.Async(IrType.Void), [R(failFlag)])
        ]))),
        new IrReturn(null)),
      Function("FaultInt", IrType.Async(IrType.I32), [],
        new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("int-fault")])),
        new IrReturn(I(0)))
    ]) { Globals = [finallyCount] };

    AssertMatchesInterpreter(mod, "RunIters", new int[] { 1, 2 });
    WithLoadedAssembly(mod, asm => {
      var okTask = (Task)InvokeMethod(asm, mod, "RunAsyncVoid", false)!;
      okTask.GetAwaiter().GetResult();
      Assert.That(okTask.IsCompletedSuccessfully, Is.True);

      var faultVoid = (Task)InvokeMethod(asm, mod, "RunAsyncVoid", true)!;
      Assert.That(() => faultVoid.GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("void-fault"));

      var faultInt = (Task<int>)InvokeMethod(asm, mod, "FaultInt")!;
      Assert.That(() => faultInt.GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("int-fault"));
    });
  }

  [Test]
  public void ViewsPointersAndBitPermutations_ComprehensiveAndBounds() {
    var arr = L("arr", IrType.Array(IrType.I32));
    var bArr = L("bArr", IrType.Array(IrType.U8));
    var sp = L("sp", IrType.Span(IrType.I32));
    var sp2 = L("sp2", IrType.Span(IrType.I32));
    var fsp = L("fsp", IrType.Span(IrType.F32));
    var dsp = L("dsp", IrType.Span(IrType.F64));
    var bsp = L("bsp", IrType.Span(IrType.U8));
    var ptr = L("ptr", IrType.Pointer(IrType.I32));
    var ptr2 = L("ptr2", IrType.Pointer(IrType.I32));
    var ptrA = L("ptrA", IrType.Pointer(IrType.I32));
    var ptrB = L("ptrB", IrType.Pointer(IrType.I32));
    var bptr = L("bptr", IrType.Pointer(IrType.U8));
    var fptr = L("fptr", IrType.Pointer(IrType.F32));
    var dptr = L("dptr", IrType.Pointer(IrType.F64));
    var sptr = L("sptr", IrType.Pointer(IrType.I16));
    var i8ptr = L("i8ptr", IrType.Pointer(IrType.I8));
    var localVal = L("lv", IrType.I32);
    var paramVal = L("pv", IrType.I32);
    var x32 = L("x32", IrType.U32);
    var m32 = L("m32", IrType.U32);
    var x64 = L("x64", IrType.U64);
    var m64 = L("m64", IrType.U64);
    var x8 = L("x8", IrType.I8);
    var m8 = L("m8", IrType.I8);
    var xu8 = L("xu8", IrType.U8);
    var mu8 = L("mu8", IrType.U8);
    var x16 = L("x16", IrType.I16);
    var m16 = L("m16", IrType.I16);
    var xu16 = L("xu16", IrType.U16);
    var mu16 = L("mu16", IrType.U16);
    var idx = L("idx", IrType.I32);

    var mod = new IrModule("LowLevelMod", [
      // Views: ViewOf(1/2/3 args, null), Slice(2/3), StackAlloc, ViewFill, ViewCopy, ViewTryCopy, ViewEqual (i32/f32/f64), ViewToArray, IrStore, IrStep, endian load/store on view.
      Function("ViewOps", IrType.I32, [arr, bArr],
        new IrDeclare(sp, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(arr)])),
        new IrDeclare(sp2, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.I32), [I(3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [R(sp2), I(5)])),
        new IrStore(R(sp2), I(1), I(9)),
        new IrExprStmt(new IrStep(new IrArrayLoad(R(sp2), I(1)), Increment: true, Postfix: false, Trap: false)),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(IrType.I32), [R(sp2), I(0), I(2)]), new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(IrType.I32), [R(sp), I(1)])])),
        new IrDeclare(fsp, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.F32), [I(1)])),
        new IrStore(R(fsp), I(0), F32(float.NaN)),
        new IrDeclare(dsp, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(IrType.F64), [I(1)])),
        new IrStore(R(dsp), I(0), F64(1.5)),
        new IrDeclare(bsp, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U8), [R(bArr), I(0), I(8)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(bsp), I(0), I(0x01020304)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(bsp), I(4), F32(2.5f)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(IrType.I32), [R(sp2)]), I(1)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.ViewTryCopy, IrType.Bool, [R(sp2), new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(IrType.I32), [R(sp), I(0), I(1)])]), I(100), I(1)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(fsp), R(fsp)]), I(2), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(dsp), R(dsp)]), I(4), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(sp), R(sp2)]), I(8), I(0)),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I32, [R(bsp), I(0)]),
                    new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F32, [R(bsp), I(4)]), IrConversion.Saturate))))))))),
      // Pointers: AddressOf (local + param), StackAlloc ptr, Pin(arr, idx), PointerAdd, PointerDifference, all 6 comparisons, PointerCast (cross-type Buffer.GetByte/SetByte!).
      Function("PtrOps", IrType.I32, [arr, paramVal],
        new IrDeclare(localVal, I(10)),
        new IrDeclare(ptr, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(localVal)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(ptr), I(33)])),
        new IrDeclare(ptr2, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [R(paramVal)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(ptr2), Bin(IrBinaryOp.Add, R(paramVal), I(7))])),
        new IrDeclare(bptr, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Pointer(IrType.U8), [I(16)])),
        new IrDeclare(fptr, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.F32), [R(bptr)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(fptr), F32(3.5f)])),
        new IrDeclare(dptr, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.F64), [new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.U8), [R(bptr), I(8)])])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(dptr), F64(4.5)])),
        new IrDeclare(sptr, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.I16), [R(bptr)])),
        new IrDeclare(i8ptr, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.I8), [R(bptr)])),
        new IrDeclare(ptrA, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(arr), I(1)])),
        new IrDeclare(ptrB, new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.I32), [R(ptrA), I(2)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          R(localVal),
          Bin(IrBinaryOp.Add,
            R(paramVal),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerDifference, IrType.I64, [R(ptrB), R(ptrA)]), IrConversion.Wrap),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerLess, IrType.Bool, [R(ptrA), R(ptrB)]), I(1), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerLessOrEqual, IrType.Bool, [R(ptrA), R(ptrB)]), I(2), I(0)),
                  Bin(IrBinaryOp.Add,
                    new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerGreater, IrType.Bool, [R(ptrB), R(ptrA)]), I(4), I(0)),
                    Bin(IrBinaryOp.Add,
                      new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerGreaterOrEqual, IrType.Bool, [R(ptrB), R(ptrA)]), I(8), I(0)),
                      Bin(IrBinaryOp.Add,
                        new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerEqual, IrType.Bool, [R(ptrA), R(ptrA)]), I(16), I(0)),
                        Bin(IrBinaryOp.Add,
                          new IrConditional(new IrIntrinsic(IrIntrinsicOp.PointerNotEqual, IrType.Bool, [R(ptrA), R(ptrB)]), I(32), I(0)),
                          Bin(IrBinaryOp.Add,
                            new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.F32, [R(fptr)]), IrConversion.Saturate),
                            Bin(IrBinaryOp.Add,
                              new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.F64, [R(dptr)]), IrConversion.Saturate),
                              Bin(IrBinaryOp.Add,
                                new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I16, [R(sptr)]), IrConversion.Wrap),
                                new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I8, [R(i8ptr)]), IrConversion.Wrap))))))))))))))),
      // Bit permutations: Pext & Pdep on all widths (8/16/32/64)
      Function("Pext32", IrType.U32, [x32, m32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitExtract, IrType.U32, [R(x32), R(m32)]))),
      Function("Pdep32", IrType.U32, [x32, m32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U32, [R(x32), R(m32)]))),
      Function("Pext64", IrType.U64, [x64, m64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitExtract, IrType.U64, [R(x64), R(m64)]))),
      Function("Pdep64", IrType.U64, [x64, m64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U64, [R(x64), R(m64)]))),
      Function("PextI8", IrType.I8, [x8, m8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitExtract, IrType.I8, [R(x8), R(m8)]))),
      Function("PdepU8", IrType.U8, [xu8, mu8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U8, [R(xu8), R(mu8)]))),
      Function("PextI16", IrType.I16, [x16, m16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitExtract, IrType.I16, [R(x16), R(m16)]))),
      Function("PdepU16", IrType.U16, [xu16, mu16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ParallelBitDeposit, IrType.U16, [R(xu16), R(mu16)]))),
      // Bounds / traps
      Function("BadSlice", IrType.Span(IrType.I32), [arr, idx],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(arr), R(idx), I(2)]))),
      Function("BadViewIndex", IrType.I32, [arr, idx],
        new IrReturn(new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I32), [R(arr)]), R(idx)))),
      Function("BadPtrNull", IrType.I32, [],
        new IrDeclare(ptr, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [new IrNewArray(IrType.Array(IrType.I32), I(0))])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(ptr)]))),
      Function("BadPtrOob", IrType.I32, [arr],
        new IrDeclare(ptr, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(arr)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.PointerAdd, IrType.Pointer(IrType.I32), [R(ptr), I(10)])]))),
      Function("BadPtrDiff", IrType.I64, [arr],
        new IrDeclare(ptr, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(arr)])),
        new IrDeclare(ptr2, new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Pointer(IrType.I32), [I(2)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerDifference, IrType.I64, [R(ptr), R(ptr2)])))
    ]);

    AssertMatchesInterpreter(mod, "ViewOps", new int[] { 1, 2, 3, 4 }, new byte[8]);
    AssertMatchesInterpreter(mod, "PtrOps", new int[] { 10, 20, 30, 40 }, 15);
    AssertMatchesInterpreter(mod, "Pext32", 0xDEADBEEFu, 0x0F0F0F0Fu);
    AssertMatchesInterpreter(mod, "Pdep32", 0x0000ABCDu, 0x0F0F0F0Fu);
    AssertMatchesInterpreter(mod, "Pext64", 0x0123456789ABCDEFUL, 0x00FF00FF00FF00FFUL);
    AssertMatchesInterpreter(mod, "Pdep64", 0x0000000012345678UL, 0x00FF00FF00FF00FFUL);
    AssertMatchesInterpreter(mod, "PextI8", (sbyte)-55, (sbyte)0x55);
    AssertMatchesInterpreter(mod, "PdepU8", (byte)0x0A, (byte)0x55);
    AssertMatchesInterpreter(mod, "PextI16", (short)0x1234, (short)0x0F0F);
    AssertMatchesInterpreter(mod, "PdepU16", (ushort)0x00AB, (ushort)0x0F0F);

    Assert.That(() => Invoke(mod, "BadSlice", new int[] { 1, 2 }, 2), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => Invoke(mod, "BadViewIndex", new int[] { 1, 2 }, 5), Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Invoke(mod, "BadPtrNull"), Throws.TypeOf<NullReferenceException>());
    Assert.That(() => Invoke(mod, "BadPtrOob", new int[] { 1, 2 }), Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Invoke(mod, "BadPtrDiff", new int[] { 1, 2 }), Throws.TypeOf<ArithmeticException>());
  }

}
