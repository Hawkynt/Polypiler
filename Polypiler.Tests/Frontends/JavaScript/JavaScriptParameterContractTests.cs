using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// What a parameter of the Cipher framework says about itself (#1365): the JSDoc spellings of the
/// corpus - <c>byte</c>, <c>int</c>, <c>bool</c>, a class of the file, <c>T|null</c>, an array of
/// arrays or of objects - and a literal default, which a call that leaves the parameter out passes.
/// </summary>
[TestFixture]
public sealed class JavaScriptParameterContractTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0].Message;
  }

  private const string Contracts = """
    export class Box {
      /** @param {int} side */
      constructor(side) { this.side = side; }
    }
    /**
     * @param {Box} box
     * @param {byte[]} data
     * @param {bool} flag
     * @param {Box|null} spare
     * @param {uint8[][]} rows
     * @param {Box[]} boxes
     * @returns {int32}
     */
    export function f(box, data, flag, spare, rows, boxes) {
      return (box.side + data.length + (flag ? 1 : 0) + (spare === null ? 0 : spare.side) + rows.length + boxes.length) | 0;
    }
    """;

  [Test]
  public void JsDocOfTheCorpus_StatesTheTypes() {
    var module = Import(Contracts);
    var box = IrType.Object(module.Types.OfType<IrClassType>().Single());
    Assert.That(module.Functions.Single(function => function.Name == "f").Parameters.Select(parameter => parameter.Type.ToString()), Is.EqualTo(new[] {
      box.ToString(), IrType.Array(IrType.U8).ToString(), IrType.Bool.ToString(), box.ToString(), IrType.List(IrType.Array(IrType.U8)).ToString(), IrType.List(box).ToString()
    }));
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Single().Type, Is.EqualTo(IrType.I32));
  }

  [TestCase("Object")]
  [TestCase("Missing")]
  [TestCase("Missing|null")]
  [TestCase("Error")]
  public void JsDocThatNamesNoTypeOfTheFile_IsRefused(string spelling)
    => Assert.That(Refusal($"/** @param {{{spelling}}} a @returns {{int32}} */ export function g(a) {{ return 1; }}"),
      Is.EqualTo("parameter 'a' of 'g' has no supported JSDoc type"));

  private const string Defaults = """
    class Algo {
      constructor(variant = '32') { this.name = 'A-' + variant; }
      make(inverse = false, rounds = 8) { return inverse ? -rounds : rounds; }
    }
    function g(x = 3, y = -1, z = null) { return (x + y + (z === null ? 0 : 1)) | 0; }
    export function f() {
      return (new Algo().name.length + new Algo('16').make() + new Algo().make(true, 2) + g() + g(5, 1, 7)) | 0;
    }
    """;

  [Test]
  public void DefaultOfAParameter_IsWhatACallThatLeavesItOutPasses() {
    var module = Import(Defaults);
    // 'A-32' 4, make() 8, make(true, 2) -2, g() 2, g(5, 1, 7) 7.
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(19));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(19), text);
  }

  [Test]
  public void DefaultOfAnExportedParameter_StatesItsKind() {
    var module = Import("export function f(a = 'x', n = 2, flag = true) { return flag ? a.length + n : 0; }");
    Assert.That(module.Functions.Single().Parameters.Select(parameter => parameter.Type), Is.EqualTo(new[] { IrType.String, IrType.F64, IrType.Bool }));
    var declared = Import("/** @param {int32} n */ export function f(n = 2) { return n + 1; }");
    Assert.That(declared.Functions.Single().Parameters.Single().Type, Is.EqualTo(IrType.I32));
  }

  [TestCase("function g(x = y) { return x; } const y = 1; export function f() { return g(); }", "'a default parameter value that is no literal' is not supported yet")]
  [TestCase("class K { m(a, b = 1) { return a + b; } } export function f() { return new K().m(); }", "'a call with 0 arguments for 2 parameters' is not supported yet")]
  public void DefaultThatIsNotSupported_IsRefusedByName(string source, string message)
    => Assert.That(Refusal(source), Is.EqualTo(message));

}
