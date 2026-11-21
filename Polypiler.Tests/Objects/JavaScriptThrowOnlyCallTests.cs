using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// A call of a method whose every implementation only throws, used as a value (#1389), as
/// <c>IBlockCipherInstance.Result</c> of the Cipher framework computes
/// <c>this.isInverse ? this.DecryptBlock(block) : this.EncryptBlock(block)</c>: the call has no value,
/// so the local, the return or the argument that takes it lowers as the call followed by a throw that
/// is never reached, and the rest of its block is dead. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptThrowOnlyCallTests {

  private const string Classes = """
    class Base {
      constructor() { this.inverse = false; }
      encrypt(b) { throw new Error('encrypt not implemented'); }
      decrypt(b) { throw new Error('decrypt not implemented'); }
      result(b) {
        const out = [b];
        for (let n = 0; n < 1; n++) {
          const processed = this.inverse ? this.decrypt(b) : this.encrypt(b);
          for (let i = 0; i < processed.length; i++) out.push(processed[i]);
        }
        return out.length + out[0];
      }
    }
    class Derived extends Base {
      result(b) { return b * 2 + (this.inverse ? 1 : 0); }
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.Single().Message;

  /// <summary>The module in C#, compiled; every JavaScript profile of the round trip is valid JavaScript.</summary>
  private static GeneratedCSharp Both(IrModule module) {
    foreach (var profile in new[] { JavaScriptProfile.Default, JavaScriptProfile.Parse("es5", "cjs") }) {
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }

    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    return GeneratedCSharp.Compile(text, false);
  }

  /// <summary>Asserts that a function throws the exception the method that only throws throws, in the interpreter and in C#.</summary>
  private static void ThrowsAsNode(IrModule module, GeneratedCSharp compiled, string function, string message) {
    Assert.That(() => IrInterpreter.Call(module, function, []), Throws.Exception.With.Message.Contains(message));
    Assert.That(() => compiled.Method(function).Invoke(null, []), Throws.InstanceOf<TargetInvocationException>().With.InnerException.Message.EqualTo(message));
  }

  /// <summary>Scenario: a base Result that no object runs - the derived Result computes what node computes (41), the base one throws.</summary>
  [Test]
  public void BaseResultThatNoObjectRuns_Translates() {
    var module = Import(Classes + """
      export function scenario() { const d = new Derived(); d.inverse = true; return d.result(20); }
      export function plain() { return new Base().result(3); }
      export function inverse() { const b = new Base(); b.inverse = true; return b.result(3); }
      """);
    var compiled = Both(module);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(41));
    Assert.That(compiled.Method("scenario").Invoke(null, []), Is.EqualTo(41));
    ThrowsAsNode(module, compiled, "plain", "encrypt not implemented");
    ThrowsAsNode(module, compiled, "inverse", "decrypt not implemented");
  }

  /// <summary>Test design 1 and 3: one override that returns gives the call its type, and the base method runs on it.</summary>
  [Test]
  public void AnOverrideThatReturns_GivesTheCallItsType() {
    var module = Import(Classes + """
      class Real extends Base {
        encrypt(b) { return [b, b + 1]; }
        decrypt(b) { return [b - 1]; }
      }
      export function forward() { return new Real().result(5); }
      export function backward() { const r = new Real(); r.inverse = true; return r.result(5); }
      """);
    var compiled = Both(module);
    Assert.That(IrInterpreter.Call(module, "forward", []), Is.EqualTo(8));
    Assert.That(compiled.Method("forward").Invoke(null, []), Is.EqualTo(8));
    Assert.That(IrInterpreter.Call(module, "backward", []), Is.EqualTo(7));
    Assert.That(compiled.Method("backward").Invoke(null, []), Is.EqualTo(7));
  }

  /// <summary>Test design 2: a local, a return, an argument and an assignment take a call of no value; the rest of the block is never run.</summary>
  [TestCase("const v = new Base().encrypt(1); count = v + 1;")]
  [TestCase("return new Base().encrypt(1);")]
  [TestCase("const b = new Base(); list.push(count + 1, b.encrypt(1)); count = 3;")]
  [TestCase("let v = count; v = new Base().encrypt(1); count = v;")]
  [TestCase("const v = count > 0 ? fail() : new Base().encrypt(2);")]
  [TestCase("take(count, new Base().encrypt(count)); count = 2;")]
  [TestCase("made().push(count, new Base().encrypt(1)); count = 2;")]
  public void EveryPlaceThatTakesNoValue_Throws(string body) {
    var module = Import(Classes + $$"""
      let count = 0;
      const list = [];
      function fail() { throw new Error('fail'); }
      function take(a, b) { return a + b; }
      function made() { list.push(5); return list; }
      export function f() { {{body}} return count + list.length; }
      export function g() { list.push(1); return take(1, 2) + made().length; }
      """);
    ThrowsAsNode(module, Both(module), "f", "encrypt not implemented");
    var f = module.Functions.Single(function => function.Name == "f");
    Assert.That(f.Body.Statements[^1], Is.InstanceOf<IrThrow>(), "the statements after the call that only throws are dead");
  }

  /// <summary>Test design 3: a conditional both of whose calls only throw, as a statement of its own, needs no throw of its own.</summary>
  [Test]
  public void CallThatOnlyThrows_AsAStatement_StaysACall() {
    var module = Import(Classes + "export function f() { new Base().encrypt(1); return 1; }");
    Assert.That(module.Functions.Single(function => function.Name == "f").Body.Statements[^1], Is.InstanceOf<IrReturn>());
    ThrowsAsNode(module, Both(module), "f", "encrypt not implemented");
  }

  /// <summary>Scenario: a value used where a method may return - an operand, or one branch of a conditional whose other has a value - is refused naming the call.</summary>
  [TestCase("return 1 + new Base().encrypt(1);", "'the value of new Base().encrypt(1), a call that only throws' is not supported yet")]
  [TestCase("return new Base().encrypt(1) * 2;", "'the value of new Base().encrypt(1), a call that only throws' is not supported yet")]
  [TestCase("return count > 0 ? 1 : new Base().encrypt(1);", "'the value of new Base().encrypt(1), a call that only throws' is not supported yet")]
  [TestCase("return [new Base().encrypt(1)];", "'the value of new Base().encrypt(1), a call that only throws' is not supported yet")]
  [TestCase("return 1 + Math.max(1, new Base().encrypt(2));", "'the value of new Base().encrypt(2), a call that only throws' is not supported yet")]
  [TestCase("const a = 1, b = new Base().encrypt(1); return a;", "'the value of new Base().encrypt(1), a call that only throws' is not supported yet")]
  [TestCase("return count > 0 ? new Base().decrypt(1) : 1;", "'the value of new Base().decrypt(1), a call that only throws' is not supported yet")]
  [TestCase("Math.max(...[1], new Base().encrypt(1)); return 1;", "'a spread argument' is not supported yet")]
  public void ValueUsedWhereTheMethodMayReturn_IsRefusedNamingTheCall(string body, string message)
    => Assert.That(Refusal(Classes + $$"""
      let count = 0;
      export function f() { {{body}} }
      """), Is.EqualTo(message));

}
