using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Objects;

/// <summary>
/// A method a base class leaves to its derived classes and whose body only throws (#1365), as
/// <c>Result()</c> of the Cipher framework's instance interface: its result is what the overrides
/// return, not <c>undefined</c>.
/// </summary>
[TestFixture]
public sealed class JavaScriptAbstractMethodTests {

  [Test]
  public void MethodThatOnlyThrows_TakesTheResultOfItsOverrides() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      class Base {
        result() { throw new Error('not implemented'); }
      }
      class Derived extends Base {
        result() { return 7; }
      }
      export function f() { const made = new Derived(); return made.result(); }
      export function g() { return new Base().result(); }
      """);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    var module = result.Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Types.OfType<IrClassType>().Select(type => type.Methods.Single().Function).Select(name => module.Functions.Single(function => function.Name == name).ReturnType),
      Is.All.EqualTo(IrType.I32));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(7));
    Assert.That(() => IrInterpreter.Call(module, "g", []), Throws.Exception);
  }

  [Test]
  public void FunctionThatOnlyThrows_ReturnsNothing() {
    var result = JavaScriptFrontend.Import("m.mjs", "function fail() { throw new Error('no'); } export function f(a) { if (a > 1) fail(); return 1; }");
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(result.Module!.Functions.Single(function => function.Name == "fail").ReturnType, Is.EqualTo(IrType.Void));
  }

}
