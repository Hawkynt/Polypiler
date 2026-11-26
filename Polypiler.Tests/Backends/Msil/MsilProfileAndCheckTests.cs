using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>Tests for <see cref="MsilProfile"/>, <see cref="MsilEmitOptions"/>, diagnostics, and <see cref="MsilEmitter.Check"/>.</summary>
[TestFixture]
public sealed class MsilProfileAndCheckTests {

  [Test]
  public void Profile_DefaultsAndProperties() {
    Assert.That(MsilProfile.Default, Is.EqualTo(MsilProfile.Net10));
    Assert.That(MsilProfile.Net10.Name, Is.EqualTo("msil/net10"));
    Assert.That(MsilProfile.Net10.TargetFramework, Is.EqualTo("net10.0"));
    Assert.That(MsilProfile.Net10.OutputKind, Is.EqualTo(MsilOutputKind.Library));
    Assert.That(MsilProfile.Net10.ToString(), Is.EqualTo("msil/net10"));
    Assert.That(MsilProfile.Net8.Name, Is.EqualTo("msil/net8"));
    Assert.That(MsilProfile.Net8.TargetFramework, Is.EqualTo("net8.0"));
    Assert.That(MsilProfile.Names, Is.EqualTo(new[] { "msil/net10", "msil/net8" }));
    Assert.That(MsilProfile.OutputKindNames, Is.EqualTo(new[] { "library", "exe" }));
    Assert.That(MsilEmitter.Runtime, Is.EqualTo(NativeAccuracy.DotNet));
    Assert.That(MsilEmitter.UnsupportedConstruct.Code, Is.EqualTo(MsilEmitter.UnsupportedConstructCode));
    Assert.That(MsilEmitter.UnsupportedType.Code, Is.EqualTo(MsilEmitter.UnsupportedTypeCode));
    Assert.That(MsilEmitter.UnsupportedIntrinsic.Code, Is.EqualTo(MsilEmitter.UnsupportedIntrinsicCode));
  }

  [TestCase(null, null, "msil/net10", "net10.0", MsilOutputKind.Library)]
  [TestCase("msil/net10", "library", "msil/net10", "net10.0", MsilOutputKind.Library)]
  [TestCase("msil/net10", "exe", "msil/net10", "net10.0", MsilOutputKind.ConsoleExe)]
  [TestCase("msil/net8", null, "msil/net8", "net8.0", MsilOutputKind.Library)]
  [TestCase("msil/net8", "exe", "msil/net8", "net8.0", MsilOutputKind.ConsoleExe)]
  public void Profile_ParseAndTryParse_Valid(string? name, string? outputKind, string expectedName, string expectedTfm, MsilOutputKind expectedKind) {
    Assert.That(MsilProfile.TryParse(name, outputKind, out var parsed, out var error), Is.True);
    Assert.That(error, Is.Null);
    Assert.That(parsed!.Name, Is.EqualTo(expectedName));
    Assert.That(parsed.TargetFramework, Is.EqualTo(expectedTfm));
    Assert.That(parsed.OutputKind, Is.EqualTo(expectedKind));
    Assert.That(MsilProfile.Parse(name, outputKind), Is.EqualTo(parsed));
  }

  [TestCase("msil/net6", null, "unknown MSIL profile 'msil/net6'")]
  [TestCase("msil/net10", "winexe", "unknown MSIL output kind 'winexe'")]
  public void Profile_ParseAndTryParse_Invalid(string? name, string? outputKind, string expectedError) {
    Assert.That(MsilProfile.TryParse(name, outputKind, out var parsed, out var error), Is.False);
    Assert.That(parsed, Is.Null);
    Assert.That(error, Does.Contain(expectedError));
    Assert.That(() => MsilProfile.Parse(name, outputKind), Throws.ArgumentException.With.Message.Contains(expectedError));
  }

  [Test]
  public void Check_ReportsVerifierFailures() {
    var badFn = new IrFunction("f", true, IrType.I32, [], [], Block(new IrReturn(B(true))));
    var module = new IrModule("Bad", [badFn]);
    var diagnostics = MsilEmitter.Check(module);
    Assert.That(diagnostics, Is.Not.Empty);
    Assert.That(() => MsilEmitter.Emit(module), Throws.ArgumentException);
  }

  [Test]
  public void Check_ReportsUnsupportedTypesConstructsAndIntrinsics() {
    // Unsupported type: BigInt parameter
    var bigIntParam = L("x", IrType.BigInt);
    var bigIntMod = Returning(IrType.BigInt, [bigIntParam], R(bigIntParam));
    Assert.That(MsilEmitter.Check(bigIntMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedTypeCode));

    // Unsupported type: user class declaration
    var cls = new IrClassType("UserObj");
    cls.Define([new IrField("x", IrType.I32, false)]);
    cls.Inherit(null, []);
    var ctor = new IrConstructor("UserObj..ctor");
    cls.DefineMembers([ctor], [], [], []);
    var self = L("this", IrType.Object(cls));
    var ctorFn = Function("UserObj..ctor", false, IrType.Void, [self], new IrReturn(null));
    var makeFn = Function("Make", IrType.Object(cls), [], new IrReturn(new IrNew(IrType.Object(cls), ctor, [])));
    var clsMod = new IrModule("ClsMod", [ctorFn, makeFn]) { Types = [cls] };
    var clsProblems = MsilEmitter.Check(clsMod);
    Assert.That(clsProblems.Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedTypeCode));
    Assert.That(clsProblems.Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedConstructCode));

    // Unsupported intrinsic: ByteSwap (unlegalized)
    var p = L("n", IrType.I32);
    var swapMod = Returning(IrType.I32, [p], new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I32, [R(p)]));
    Assert.That(MsilEmitter.Check(swapMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedIntrinsicCode));
    Assert.That(() => MsilEmitter.Emit(swapMod), Throws.ArgumentException.With.Message.Contains("ByteSwap"));

    // Unsupported intrinsic: RegexTest
    var s = L("s", IrType.String);
    var rxMod = Returning(IrType.Bool, [s], new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [R(s), new IrStrConst("a"), new IrStrConst("")]));
    Assert.That(MsilEmitter.Check(rxMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedIntrinsicCode));

    // Unsupported intrinsic: FormatEcma on f64
    var dParam = L("d", IrType.F64);
    var ecmaFloatMod = Returning(IrType.String, [dParam], new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [R(dParam)]));
    Assert.That(MsilEmitter.Check(ecmaFloatMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedIntrinsicCode));

    // Unsupported construct: native-size by-reference foreign parameter
    var nativeByRefMod = new IrModule("NBR", [Function("f", IrType.Void, [], new IrReturn(null))]) {
      ForeignFunctions = [
        new IrForeignFunction("ext", true, "lib", IrType.Void, [
          new IrForeignParameter("p", IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.InOut) { NativeSize = true })
        ])
      ]
    };
    Assert.That(MsilEmitter.Check(nativeByRefMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedConstructCode));

    // Unsupported construct: IrFor with PerIteration
    var idx = L("i", IrType.I32);
    var perIterFor = new IrFor(Bin(IrBinaryOp.Less, R(idx), I(3)), Block(), Block(new IrAssign(idx, Bin(IrBinaryOp.Add, R(idx), I(1))))) {
      PerIteration = [idx]
    };
    var perIterMod = new IrModule("PI", [Function("f", IrType.Void, [], new IrDeclare(idx, I(0)), perIterFor, new IrReturn(null))]);
    Assert.That(MsilEmitter.Check(perIterMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedConstructCode));

    // Unsupported construct: List collection expression
    var listLocal = L("xs", IrType.List(IrType.I32));
    var listMod = new IrModule("Lst", [Function("f", IrType.I32, [],
      new IrDeclare(listLocal, new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.I32), [])),
      new IrReturn(new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(listLocal)])))]);
    Assert.That(MsilEmitter.Check(listMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedConstructCode));

    // Unsupported statement: IrForEachCollection
    var elemLocal = L("e", IrType.I32);
    var listParam = L("xs", IrType.List(IrType.I32));
    var feMod = new IrModule("FE", [new IrFunction("f", true, IrType.Void, [listParam], [elemLocal],
      Block(new IrForEachCollection(R(listParam), EnumerationMode.Checked, elemLocal, null, Block()), new IrReturn(null)))]);
    Assert.That(MsilEmitter.Check(feMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedConstructCode));

    // Unsupported catch class (custom exception class)
    var customEx = new IrClassType("CustomEx");
    customEx.Define([]);
    customEx.Inherit(IrExceptionClass.Exception, []);
    customEx.DefineMembers([], [], [], []);
    var catchMod = new IrModule("CE", [Function("f", IrType.Void, [],
      new IrTry(Block(), [new IrCatch(customEx, null, null, Block())], null), new IrReturn(null))]) {
      Types = [customEx]
    };
    Assert.That(MsilEmitter.Check(catchMod).Select(d => d.Code), Does.Contain(MsilEmitter.UnsupportedTypeCode));
  }

  [Test]
  public void EmitTarget_RespectsOptionsSanitizationAndNet8Profile() {
    var x = L("x", IrType.I32);
    var mod = new IrModule("9-my.mod", [Function("Id", IrType.I32, [x], new IrReturn(R(x)))]);

    var emission = MsilEmitter.EmitTarget(mod, MsilProfile.Net8, new MsilEmitOptions {
      AssemblyName = "Custom-Asm",
      OutputKind = MsilOutputKind.ConsoleExe
    });
    Assert.That(emission.Applications, Is.Empty);
    Assert.That(emission.Files, Has.Count.EqualTo(2));
    Assert.That(emission.Files[0].Path, Is.EqualTo("Custom_Asm.dll"));
    Assert.That(emission.Files[0].BinaryContent, Is.Not.Null.And.Length.GreaterThan(0));
    Assert.That(emission.Files[1].Path, Is.EqualTo("Custom_Asm.runtimeconfig.json"));
    Assert.That(emission.Files[1].Content, Does.Contain("\"net8.0\"").And.Contain("\"8.0.0\""));

    // Also check null argument guards.
    Assert.That(() => MsilEmitter.Check(null!), Throws.ArgumentNullException);
    Assert.That(() => MsilEmitter.Emit(null!), Throws.ArgumentNullException);
    Assert.That(() => MsilEmitter.Legalize(null!, () => mod), Throws.ArgumentNullException);
    Assert.That(() => MsilEmitter.Legalize(mod, null!), Throws.ArgumentNullException);
  }

}
