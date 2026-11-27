using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Kotlin;
using Polypiler.Core.Backends.Kotlin.Passes;
using Polypiler.Core.Backends.Kotlin.Printing;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Kotlin;

/// <summary>
/// Every source of the prototype fixtures, C# and JavaScript, translated to Kotlin on the oldest and
/// the newest profile: the reader reads the printed text back into a tree that prints the same text,
/// a second translation prints the same text, and no helper is left that nothing names. A source the
/// backend refuses is refused with a code of its own.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class KotlinParseBackTests {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  private static IEnumerable<TestCaseData> Cases()
    => from source in GoldenOutputs.All
       from profile in new[] { "kotlin2.0", "kotlin2.4" }
       select new TestCaseData(source, profile).SetName($"KotlinParseBack({source}, {profile})");

  [TestCaseSource(nameof(Cases))]
  public void EverySource_ParsesBackToTheTextItWasPrintedFrom(string source, string profile) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = File.ReadAllText(path);
    var module = source.EndsWith(".cs", System.StringComparison.Ordinal)
      ? CSharpFrontend.Import(Path.GetFileName(path), text).Module
      : JavaScriptFrontend.Import(Path.GetFileName(path), text).Module;
    if (module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var translated = KotlinFixtures.Translate(module, KotlinProfile.Parse(profile));
    if (translated.Source is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY16(5[01]|58|66|74|82): "));
      return;
    }

    var read = KotlinSyntaxReader.Read(translated.Source);
    Assert.That(KotlinPrinter.Print(read), Is.EqualTo(translated.Source));
    Assert.That(KotlinPrinter.Print(read), Is.EqualTo(KotlinPrinter.Print(translated.Tree!)));
    var names = read.Declarations.SelectMany(KotlinTargetPasses.Names).ToList();
    Assert.That(translated.Tree!.Declarations.OfType<KtFunction>().Where(function => function.IsHelper && !names.Contains(function.Name)).Select(function => function.Name), Is.Empty, "helpers nothing names");
    Assert.That(KotlinFixtures.Translate(module, KotlinProfile.Parse(profile)).Source, Is.EqualTo(translated.Source), "a second translation prints differently");
  }

}
