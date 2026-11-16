using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>A source a test writes itself, imported from its recorded tree as <c>snippet.py</c>.</summary>
internal static class PythonSnippet {

  /// <summary>Imports a snippet.</summary>
  public static PythonImportResult Import(string source, PythonImportOptions? options = null) => PythonFixture.Import("snippet.py", source, options);

  /// <summary>Imports a snippet that must import, and checks the IR.</summary>
  public static IrModule Module(string source, PythonImportOptions? options = null) {
    var module = PythonFixture.Module(Import(source, options));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
  }

  /// <summary>What the interpreter does for a call of a function of a snippet.</summary>
  public static string Run(string source, string function, params object[] arguments) => PythonOutcome.Interpreted(Module(source), new PythonCall(function, arguments));

  /// <summary>Every statement of a statement, itself first, those of its blocks included.</summary>
  public static System.Collections.Generic.IEnumerable<IrStmt> Statements(IrStmt statement)
    => (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement)).SelectMany(Statements).Prepend(statement);

  /// <summary>The diagnostics of a snippet that must be refused, as text with code and position.</summary>
  public static string[] Refusals(string source, PythonImportOptions? options = null) {
    var result = Import(source, options);
    Assert.That(result.Module, Is.Null, "the snippet imported");
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.ToString())];
  }

}
