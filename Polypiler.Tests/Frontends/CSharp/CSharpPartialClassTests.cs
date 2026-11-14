using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.CSharp;

/// <summary>One static class whose partial parts several files declare.</summary>
[TestFixture]
public sealed class CSharpPartialClassTests {

  private static ImportResult Import(params (string Path, string Text)[] files)
    => CSharpFrontend.Import(files.Select(file => new CSharpSource(file.Path, file.Text)).ToList());

  [Test]
  public void Parts_BecomeOneModule_WhoseMethodsReachEachOther() {
    var result = Import(
      ("A.cs", "namespace N;\npublic static partial class C { public static int F(int x) => G(x) + K; }"),
      ("B.cs", "namespace N;\npublic static partial class C { private const int K = 2; private static readonly int[] _t = [1, 2]; internal static int G(int x) => x * _t[1]; }"));
    Assert.That(result.Diagnostics, Is.Empty);
    var module = result.Module!;
    Assert.That((module.Name, module.Functions.Select(function => function.Name)), Is.EqualTo(("C", new[] { "F", "G" })));
    Assert.That(module.Constants.Select(constant => constant.Name), Is.EquivalentTo(new[] { "K", "_t" }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [5]), Is.EqualTo(12));
  }

  [Test]
  public void OneFile_IsImportedAsBefore() {
    var result = Import(("A.cs", "public static class C { public static int F() => 1; }"));
    Assert.That(result.Module!.Functions, Has.Count.EqualTo(1));
  }

  [Test]
  public void PartsOfDifferentClasses_AreRefusedInTheFileOfTheSecond() {
    var result = Import(
      ("A.cs", "public static partial class C { public static int F() => 1; }"),
      ("B.cs", "public static partial class D { public static int G() => 2; }"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.EqualTo(new[] {
      new Diagnostic("PLY1002", DiagnosticSeverity.Error, "'files that declare different classes' is not supported yet", "B.cs", 1, 1)
    }));
  }

  [Test]
  public void AMethodDeclaredInTwoParts_IsOverloading() {
    var result = Import(
      ("A.cs", "public static partial class C { public static int F() => 1; }"),
      ("B.cs", "public static partial class C { public static int F(int x) => x; }"));
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.File)),
      Is.EqualTo(new[] { ("'method overloading' is not supported yet", "B.cs") }));
  }

  [Test]
  public void CompilerError_NamesTheFileItStandsIn() {
    var result = Import(
      ("A.cs", "public static partial class C { public static int F() => 1; }"),
      ("B.cs", "public static partial class C { public static int G() => undefined; }"));
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.File)), Is.EqualTo(new[] { ("PLY1000", "B.cs") }));
  }

  [Test]
  public void AFileOfTheWrongShape_IsRefusedAndTheOthersStillRead() {
    var result = Import(
      ("A.cs", "public static partial class C { public static int F() => 1; }"),
      ("B.cs", "public partial class E { }"));
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.File)),
      Is.EqualTo(new[] { ("'a class that is not static' is not supported yet", "B.cs") }));
  }

  [Test]
  public void FusedMultiplyAdd_BecomesItsIntrinsic() {
    var result = Import(("A.cs", "public static class C { public static double F(double a) => System.Math.FusedMultiplyAdd(a, a, a); "
      + "public static float G(float a) => System.MathF.FusedMultiplyAdd(a, a, a); }"));
    var module = result.Module!;
    Assert.That(module.Functions.Select(function => ((IrIntrinsic)((IrReturn)function.Body.Statements[0]).Value!).Op),
      Is.All.EqualTo(IrIntrinsicOp.FusedMultiplyAdd));
    Assert.That(IrInterpreter.Call(module, "F", [3.0]), Is.EqualTo(12.0));
    Assert.That(IrInterpreter.Call(module, "G", [2.0f]), Is.EqualTo(6.0f));
  }

}
