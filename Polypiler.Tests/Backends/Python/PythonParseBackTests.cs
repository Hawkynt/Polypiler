using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Python.Passes;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// Every source of the prototype fixtures, C# and JavaScript, translated to Python in the oldest and
/// the newest profile: the reader reads the printed text back into a tree of the same structure, the
/// text printed from that tree is the same, a second translation prints the same text, and no
/// helper is left that nothing names. A source the backend refuses is refused with a code of its own.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class PythonParseBackTests {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  private static IEnumerable<TestCaseData> Cases()
    => from source in GoldenOutputs.All
       from profile in new[] { "py3.8", "py3.14" }
       select new TestCaseData(source, profile).SetName($"PythonParseBack({source}, {profile})");

  [TestCaseSource(nameof(Cases))]
  public void EverySource_ParsesBackToTheTreeItWasPrintedFrom(string source, string profile) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = File.ReadAllText(path);
    var module = source.EndsWith(".cs", System.StringComparison.Ordinal)
      ? CSharpFrontend.Import(Path.GetFileName(path), text).Module
      : JavaScriptFrontend.Import(Path.GetFileName(path), text).Module;
    if (module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var translated = PythonFixtures.Translate(module, PythonProfile.Parse(profile));
    if (translated.Source is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY18(5[01]|58|74): "));
      return;
    }

    var read = PythonSyntaxReader.ReadModule(translated.Source);
    Assert.That(PythonSyntaxRewriter.Shape(read), Is.EqualTo(PythonSyntaxRewriter.Shape(translated.Tree!)), translated.Source);
    Assert.That(PythonPrinter.Print(read).Split('\n').Where(line => !line.StartsWith('#') && line.Length > 0), Is.EqualTo(translated.Source.Split('\n').Where(line => !line.StartsWith('#') && line.Length > 0)));
    var names = read.Body.SelectMany(PythonTargetPasses.Names).ToList();
    Assert.That(translated.Tree!.Body.OfType<PyFunctionDef>().Where(function => function.IsHelper && names.Count(name => name == function.Name) == 0).Select(function => function.Name), Is.Empty, "helpers nothing names");
    Assert.That(PythonFixtures.Translate(module, PythonProfile.Parse(profile)).Source, Is.EqualTo(translated.Source), "a second translation prints differently");
  }

}

/// <summary>The fast slice of <see cref="PythonParseBackTests"/>: the first prototype fixtures in the default profile.</summary>
[TestFixture]
public sealed class PythonParseBackSliceTests {

  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.First))]
  public void Fixture_ParsesBackToTheTreeItWasPrintedFrom(string fixture) {
    var translated = PythonFixtures.Translate(PrototypeFixture.Imported(fixture));
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    var read = PythonSyntaxReader.ReadModule(translated.Source!);
    Assert.That(PythonSyntaxRewriter.Shape(read), Is.EqualTo(PythonSyntaxRewriter.Shape(translated.Tree!)));
  }

}
