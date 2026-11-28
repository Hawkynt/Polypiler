using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// A module the source imports that cannot be read: a source CPython does not parse or does not
/// compile. Either is a refusal at the imported module, with the syntax error's code.
/// </summary>
[TestFixture]
public sealed class ImportedModuleFailureTests {

  private const string Main = "import helper\n\ndef f(n: int) -> int:\n    return helper.g(n)\n";

  /// <summary>A module whose source CPython parses but does not compile is refused with CPython's error.</summary>
  [Test]
  public void ModuleThatDoesNotCompile_IsRefused() {
    var result = PythonSnippet.Import(Main, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", "def g(x: int) -> int:\n    return x\nreturn 1\n") : null });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.EqualTo("helper.py(3,1): error PLY1800: SyntaxError: 'return' outside function"));
  }

  /// <summary>A module whose source CPython does not parse is refused with CPython's syntax error, where CPython puts it.</summary>
  [Test]
  public void ModuleThatDoesNotParse_IsRefusedWithItsSyntaxError() {
    const string helper = "def g(x: int) -> int:\n    return (x\n";
    var result = PythonSnippet.Import(Main, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", helper) : null });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.StartsWith("helper.py(2,12): error PLY1800: SyntaxError: "));
  }

}
