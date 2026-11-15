using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// A module the source imports that cannot be read: a locator that gives no tree and an interpreter
/// that does not run the helper, or a tree of a source CPython does not parse. Either is a refusal at
/// the imported module, with the syntax error's code.
/// </summary>
[TestFixture]
public sealed class ImportedModuleFailureTests {

  private const string Main = "import helper\n\ndef f(n: int) -> int:\n    return helper.g(n)\n";

  /// <summary>A module the locator gives without a tree is parsed by the interpreter; one that does not run the helper leaves nothing to parse.</summary>
  [Test]
  public void ModuleWithoutTree_AndAFailingInterpreter_IsRefused() {
    var result = PythonSnippet.Import(Main, new PythonImportOptions {
      Interpreter = "dotnet",
      Modules = name => name == "helper" ? ("helper.py", "def g(x: int) -> int:\n    return x\n", null) : null
    });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.EqualTo("helper.py(1,1): error PLY1800: the Python helper could not parse helper.py"));
  }

  /// <summary>A module whose source CPython does not parse is refused with CPython's syntax error, where CPython puts it.</summary>
  [Test]
  public void ModuleThatDoesNotParse_IsRefusedWithItsSyntaxError() {
    const string helper = "def g(x: int) -> int:\n    return (x\n";
    var result = PythonSnippet.Import(Main, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", helper, PythonFixture.Dump("helper.py", helper)) : null });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.StartsWith("helper.py(2,12): error PLY1800: SyntaxError: "));
  }

}
