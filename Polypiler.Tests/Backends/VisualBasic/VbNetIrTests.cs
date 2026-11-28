using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// IR the frontends rarely give the Visual Basic .NET backend as it stands - expressions evaluated for
/// their effect, a step of a variable as a statement, an assignment as a value, a call the module does
/// not define - emitted without the optimizer, compiled in memory and called against the interpreter.
/// </summary>
[TestFixture]
public sealed class VbNetIrTests {

  private static string Emitted(IrModule module, VisualBasicProfile? profile = null)
    => VisualBasicEmitter.Emit(module, profile ?? VisualBasicProfile.VbNetLatest).Single(file => file.Path.EndsWith(".vb", System.StringComparison.Ordinal)).Content;

  private static void Agrees(IrModule module, VisualBasicProfile profile, params object[][] calls) {
    var source = Emitted(module, profile);
    var procedure = VbNetFixtures.Procedure(VbNetFixtures.Load(source, profile, profile.RemoveIntegerChecks), "F");
    foreach (var arguments in calls) {
      var call = new PrototypeCall("F", arguments);
      Assert.That(PrototypeFixture.InvokedOutcome(procedure, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), source);
    }
  }

  private static IrModule Steps(IrType type) {
    var n = new IrLocal("n", type);
    var x = new IrLocal("x", type);
    IrExpr Ref(IrLocal local) => new IrLocalRef(local);
    return new IrModule("M", [new IrFunction("F", true, type, [n], [x], new IrBlock([
      new IrDeclare(x, Ref(n)),
      new IrExprStmt(new IrStep(Ref(x), true, true, false)),
      new IrExprStmt(new IrStep(Ref(x), false, false, false)),
      new IrExprStmt(new IrStep(Ref(x), true, false, !type.IsFloat)),
      new IrExprStmt(new IrBind(x, new IrBinary(IrBinaryOp.Add, Ref(x), Ref(n)))),
      new IrExprStmt(new IrBinary(IrBinaryOp.Multiply, Ref(x), Ref(n))),
      new IrReturn(Ref(x))
    ]))]);
  }

  [Test]
  public void Effects_AreStatementsOfTheirOwn() {
    foreach (var removed in new[] { true, false }) {
      var profile = VisualBasicProfile.VbNetLatest with { RemoveIntegerChecks = removed };
      Agrees(Steps(IrType.I32), profile, [5], [-1], [1 << 30]);
      Agrees(Steps(IrType.F64), profile, [2.5], [-0.5]);
      Agrees(Steps(IrType.U64), profile, [7UL]);
    }
  }

  [Test]
  public void StepStatement_OfAFloatOrWithChecksRemoved_IsACompoundAssignment() {
    Assert.That(Emitted(Steps(IrType.F64)), Does.Contain("x += 1.0"));
    Assert.That(Emitted(Steps(IrType.I32)), Does.Contain("x += 1").And.Not.Contain("PlyStepWrap"));
    Assert.That(Emitted(Steps(IrType.I32), VisualBasicProfile.VbNetLatest with { RemoveIntegerChecks = false }), Does.Contain("PlyStepWrap(x, True, True)"));
  }

  [Test]
  public void NegationOfSignedTypes_WrapsWithChecksOn() {
    foreach (var type in new[] { IrType.I32, IrType.I64 }) {
      var n = new IrLocal("n", type);
      var module = new IrModule("M", [new IrFunction("F", true, type, [n], [], new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, new IrLocalRef(n)))]))]);
      var profile = VisualBasicProfile.VbNetLatest with { RemoveIntegerChecks = false };
      Assert.That(Emitted(module, profile), Does.Contain("PlyNegWrap("));
      object[][] calls = type == IrType.I32 ? [[0], [int.MinValue], [int.MaxValue]] : [[0L], [long.MinValue], [7L]];
      Agrees(module, profile, calls);
    }
  }

  [Test]
  public void ConstructorOfAClassOfTheModule_IsRefusedNotACrash() {
    var source = System.IO.File.ReadAllText(System.IO.Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "showcase", "15-exceptions-and-using.cs"));
    var translation = VbNetFixtures.Translate(PrototypeFixture.Import("Showcase.cs", source));
    Assert.That(translation.Problems, Has.Some.Contains("PLY2656"));
    Assert.That(translation.Problems, Has.Some.Contains("which the module does not define"));
  }

  [Test]
  public void CallOfAFunctionTheModuleDoesNotDefine_IsIllTyped() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrCall("Missing", IrType.I32, []))]))]);
    Assert.That(() => Emitted(module), Throws.ArgumentException.With.Message.Contains("'Missing' does not exist"));
  }

}
