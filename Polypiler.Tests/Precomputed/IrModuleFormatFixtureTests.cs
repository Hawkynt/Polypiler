using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.Precomputed;

/// <summary>
/// Every fixture of <c>tests/fixtures/prototype</c> that a frontend imports - classes and dispatch,
/// optional values, generics, exceptions, resource scopes, collections, function values, views,
/// enums, numbers as text and the rest - reads back from <see cref="IrModuleFormat"/> as the same graph.
/// </summary>
[TestFixture]
public sealed class IrModuleFormatFixtureTests {

  private static readonly string _root = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  /// <summary>Every C# and JavaScript file of the fixtures, by its path below the folder.</summary>
  private static IEnumerable<TestCaseData> Fixtures()
    => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
      .Where(path => Path.GetExtension(path) is ".cs" or ".js" or ".mjs")
      .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
      .Order(StringComparer.Ordinal)
      .Select(fixture => new TestCaseData(fixture).SetName($"The fixture {fixture} reads back as the same graph"));

  /// <summary>A fixture imported by the frontend of its language, or <see langword="null"/> when the frontend refuses it.</summary>
  private static IrModule? Imported(string fixture) {
    var path = Path.Combine(_root, fixture);
    var source = File.ReadAllText(path);
    var name = Path.GetFileName(path);
    return fixture.EndsWith(".cs", StringComparison.Ordinal) ? CSharpFrontend.Import(name, source).Module : JavaScriptFrontend.Import(name, source).Module;
  }

  [TestCaseSource(nameof(Fixtures))]
  public void AnImportedFixture_ReadsBackAsTheSameGraph(string fixture) {
    var module = Imported(fixture);
    if (module is null)
      Assert.Pass("The frontend refuses the fixture: there is no module to write.");

    var bytes = IrModuleFormat.Write(module!);
    var read = IrModuleFormat.Read(bytes);
    Assert.That(IrGraph.Difference(module, read), Is.Null);
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes), "the module read writes other bytes");
  }

  /// <summary>The features the sweep is meant to reach, each found in at least one imported fixture.</summary>
  [TestCase("generics/Search.cs", typeof(IrGenericEquals), TestName = "A generic equality comes from a fixture")]
  [TestCase("exceptions/Classes.cs", typeof(IrTry), TestName = "A try statement comes from a fixture")]
  [TestCase("resources/Scopes.cs", typeof(IrUsing), TestName = "A resource scope comes from a fixture")]
  [TestCase("collections/Collections.cs", typeof(IrForEachCollection), TestName = "A loop over a collection comes from a fixture")]
  [TestCase("functions/Closures.cs", typeof(IrLambda), TestName = "A lambda comes from a fixture")]
  [TestCase("optional/Find.cs", typeof(IrSome), TestName = "An optional value comes from a fixture")]
  [TestCase("dispatch/Shapes.cs", typeof(IrMethodCall), TestName = "A method call comes from a fixture")]
  public void TheSweep_ReachesTheFeature(string fixture, Type node) {
    var module = Imported(fixture)!;
    var nodes = module.Functions.SelectMany(function => IrTree.Nodes(function.Body).Cast<object>().Concat(IrClosures.Within(function.Body)));
    Assert.That(nodes.Any(node.IsInstanceOfType), Is.True);
  }

}
