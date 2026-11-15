using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// A variable a closure captures holds no single value for the rules (#1329): the closure may write it
/// whenever it is called - here by a LINQ operation, which no call of the function names - so no rule
/// may take its initializer for it.
/// </summary>
[TestFixture]
internal sealed class CapturedVariableRuleTests {

  private const string Source = "using System.Linq;\n";

  private static IrModule Imported(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", Source + PrototypeFixture.Wrap(methods));
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  [Test]
  public void CapturedVariable_IsNotStable() {
    var function = Imported("public static int F(int[] a) { var calls = 0; var n = a.Select(x => { ++calls; return x; }).Count(); return calls * 100 + n * 10; }").Functions.Single();
    var context = new RuleContext(function, new RuleTarget(false));
    Assert.That(context.IsStable(function.Locals.Single(local => local.Name == "calls")), Is.False);
    Assert.That(context.Definition(function.Locals.Single(local => local.Name == "calls")), Is.Null);
  }

  /// <summary>The sum of shifted copies does not fold the captured count away: the optimized module computes what the imported one does.</summary>
  [Test]
  public void OptimizedModule_KeepsTheCapturedCount() {
    var module = Imported("public static int F(int[] a) { var calls = 0; var n = a.Select(x => { ++calls; return x; }).Count(); return calls * 100 + n * 10 + n; }");
    var optimized = Optimizer.Run(module, new OptimizerOptions()).Module;
    Assert.That(IrInterpreter.Call(optimized, "F", [new[] { 1, 2, 3 }]), Is.EqualTo(333));
  }

}
