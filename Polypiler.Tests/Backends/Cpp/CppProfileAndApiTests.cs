using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>Tests for C++ profiles, public API surface, legalization, naming edge cases, and diagnostic refusals (PLY1450–PLY1452).</summary>
[TestFixture]
public sealed class CppProfileAndApiTests {

  [Test]
  public void Profile_ParseTryParseNamesAndCapabilities() {
    Assert.That(CppProfile.Default, Is.EqualTo(CppProfile.Cpp20));
    Assert.That(CppProfile.Names, Is.EqualTo(new[] { "cpp/cpp17", "cpp/cpp20", "cpp/cpp23" }));
    Assert.That(CppProfile.Cpp17.ToString(), Is.EqualTo("cpp/cpp17"));
    Assert.That(CppProfile.Cpp20.ToString(), Is.EqualTo("cpp/cpp20"));
    Assert.That(CppProfile.Cpp23.ToString(), Is.EqualTo("cpp/cpp23"));

    Assert.That(CppProfile.Cpp17.HasBitHeader, Is.False);
    Assert.That(CppProfile.Cpp17.HasSpanHeader, Is.False);
    Assert.That(CppProfile.Cpp17.HasByteSwap, Is.False);
    Assert.That(CppProfile.Cpp20.HasBitHeader, Is.True);
    Assert.That(CppProfile.Cpp20.HasSpanHeader, Is.True);
    Assert.That(CppProfile.Cpp20.HasByteSwap, Is.False);
    Assert.That(CppProfile.Cpp23.HasBitHeader, Is.True);
    Assert.That(CppProfile.Cpp23.HasSpanHeader, Is.True);
    Assert.That(CppProfile.Cpp23.HasByteSwap, Is.True);

    Assert.That(CppProfile.Parse(), Is.EqualTo(CppProfile.Cpp20));
    Assert.That(CppProfile.Parse("cpp/cpp17"), Is.EqualTo(CppProfile.Cpp17));
    Assert.That(CppProfile.Parse("cpp/cpp20"), Is.EqualTo(CppProfile.Cpp20));
    Assert.That(CppProfile.Parse("cpp/cpp23"), Is.EqualTo(CppProfile.Cpp23));

    Assert.That(CppProfile.TryParse("cpp/cpp11", out var bad, out var err), Is.False);
    Assert.That(bad, Is.Null);
    Assert.That(err, Does.Contain("cpp/cpp11").And.Contain("cpp/cpp17"));
    Assert.That(() => CppProfile.Parse("cpp/invalid"), Throws.ArgumentException.With.Message.Contains("cpp/invalid"));
  }

  [Test]
  public void Emitter_ConstantsDescriptorsAndNullGuards() {
    Assert.That(CppEmitter.Runtime, Is.EqualTo("gcc"));
    Assert.That(CppEmitter.UnsupportedConstructCode, Is.EqualTo("PLY1450"));
    Assert.That(CppEmitter.UnsupportedTypeCode, Is.EqualTo("PLY1451"));
    Assert.That(CppEmitter.UnsupportedIntrinsicCode, Is.EqualTo("PLY1452"));
    Assert.That(CppEmitter.UnsupportedConstruct.Rule, Is.EqualTo("cpp.backend.construct"));
    Assert.That(CppEmitter.UnsupportedType.Rule, Is.EqualTo("cpp.backend.type"));
    Assert.That(CppEmitter.UnsupportedIntrinsic.Rule, Is.EqualTo("cpp.backend.intrinsic"));

    Assert.That(() => CppEmitter.Check(null!), Throws.ArgumentNullException);
    Assert.That(() => CppEmitter.Emit(null!), Throws.ArgumentNullException);
    Assert.That(() => CppEmitter.EmitTarget(null!), Throws.ArgumentNullException);
    Assert.That(() => CppEmitter.Legalize(null!, () => PrecomputedImplementations.Module), Throws.ArgumentNullException);
    Assert.That(() => CppEmitter.Legalize(new IrModule("M", []), null!), Throws.ArgumentNullException);
  }

  [Test]
  public void CheckAndEmit_InvalidIr_ReportsVerifierDiagnostics() {
    var badFn = Function("bad", IrType.I32, [], new IrReturn(B(true)));
    var badMod = new IrModule("Bad", [badFn]);
    var problems = CppEmitter.Check(badMod);
    Assert.That(problems, Is.Not.Empty);
    Assert.That(() => CppEmitter.Emit(badMod), Throws.ArgumentException.With.Message.Contains("cannot be emitted"));
  }

  [Test]
  public void CheckAndEmit_UnsupportedType_ReportsPLY1451() {
    var x = L("x", IrType.BigInt);
    var fn = Function("big", IrType.BigInt, [x], new IrReturn(R(x)));
    var mod = new IrModule("Big", [fn]);

    var problems = CppEmitter.Check(mod, CppProfile.Cpp20);
    Assert.That(problems.Any(p => p.Code == CppEmitter.UnsupportedTypeCode), Is.True);
    Assert.That(() => CppEmitter.Emit(mod, CppProfile.Cpp20), Throws.ArgumentException);
  }

  [Test]
  public void CheckAndEmit_UnsupportedConstruct_ReportsPLY1450() {
    // IrLastError expression is refused with PLY1450
    var fn = Function("lastErr", IrType.I32, [], new IrReturn(new IrLastError(false)));
    var mod = new IrModule("LastErr", [fn]);
    var problems = CppEmitter.Check(mod);
    Assert.That(problems.Any(p => p.Code == CppEmitter.UnsupportedConstructCode), Is.True);
    Assert.That(() => CppEmitter.Emit(mod), Throws.ArgumentException);
  }

  [Test]
  public void CheckAndEmit_UnsupportedIntrinsic_ReportsPLY1452() {
    var s = L("s", IrType.String);
    var re = new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [R(s), new IrStrConst("a+"), new IrStrConst("")]);
    var mod = new IrModule("Re", [Function("test", IrType.Bool, [s], new IrReturn(re))]);
    var problems = CppEmitter.Check(mod);
    Assert.That(problems.Any(p => p.Code == CppEmitter.UnsupportedIntrinsicCode), Is.True);
    Assert.That(() => CppEmitter.Emit(mod), Throws.ArgumentException);
  }

  [Test]
  public void NamingAndOptions_ReservedWordsCollisionsAndCustomNamespace() {
    // Module name with punctuation, reserved names, digit-prefixed names, custom namespace override
    var pClass = L("class", IrType.I32);
    var pInt = L("1st", IrType.I32);
    var pUnused = L("___", IrType.I32);
    var loc = L("return", IrType.I32);
    var f1 = Function("template", IrType.I32, [pClass, pInt, pUnused],
      new IrDeclare(loc, Bin(IrBinaryOp.Add, R(pClass), R(pInt))),
      new IrReturn(R(loc)));
    var f2 = Function("Template", IrType.I32, [L("x", IrType.I32)], new IrReturn(I(42)));
    var f3 = Function("___", IrType.I32, [], new IrReturn(I(7)));
    var emptyNameMod = new IrModule("!!!", [f1, f2, f3]);

    var target = CppEmitter.EmitTarget(emptyNameMod, CppProfile.Cpp20, new CppEmitOptions { Namespace = "MyApp.Core::Math" });
    Assert.That(target.Applications, Is.Empty);
    Assert.That(target.Files[0].Path, Is.EqualTo("module.hpp"));
    Assert.That(target.Files[1].Path, Is.EqualTo("module.cpp"));
    Assert.That(target.Files[0].Content, Does.Contain("namespace my_app::core::math {"));
    Assert.That(target.Files[0].Content, Does.Contain("template_("));
    Assert.That(target.Files[0].Content, Does.Contain("template__2("));
    Assert.That(target.Files[0].Content, Does.Contain("fn("));

    // Compile and run to verify zero warnings even with unused params & reserved identifiers!
    var driver = """
      #include "module.hpp"
      #include <cassert>
      int main() {
        assert(my_app::core::math::template_(10, 20, 99) == 30);
        assert(my_app::core::math::template__2(0) == 42);
        assert(my_app::core::math::fn() == 7);
        return 0;
      }
      """;
    CompileAndRun(emptyNameMod, CppProfile.Cpp20, driver, new CppEmitOptions { Namespace = "MyApp.Core::Math" });

    // Also test default namespace fallback on non-alphanumeric module name + Pascal edge cases + 0-ctor class + IrIsNull
    var filesDefault = CppEmitter.Emit(emptyNameMod, CppProfile.Cpp17);
    Assert.That(filesDefault[0].Content, Does.Contain("namespace module_ {"));

    var noCtorCls = new IrClassType("___");
    noCtorCls.Define([]);
    noCtorCls.Inherit(null, []);
    noCtorCls.DefineMembers([], [], [], []);
    var numCls = new IrClassType("123_abc");
    numCls.Define([]);
    numCls.Inherit(null, []);
    numCls.DefineMembers([], [], [], []);
    var objParam = L("o", IrType.Object(noCtorCls));
    var objLocal = L("ol", IrType.Object(numCls));
    var edgeMod = new IrModule("EdgeCls", [
      Function("checkNull", IrType.Bool, [objParam],
        new IrDeclare(objLocal, null),
        new IrReturn(Bin(IrBinaryOp.LogicalOr, new IrIsNull(R(objParam)), new IrIsNull(R(objLocal)))))
    ]) { Types = [noCtorCls, numCls] };
    var edgeFiles = CppEmitter.Emit(edgeMod, CppProfile.Cpp20);
    Assert.That(edgeFiles[0].Content, Does.Contain("class Type").And.Contain("class T123Abc").And.Contain("Type() = default;"));
  }

}
