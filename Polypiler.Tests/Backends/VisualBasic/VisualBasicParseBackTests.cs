using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Printing;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// Every C# source of the prototype fixtures translated to Visual Basic in every profile: the reader
/// reads the printed text back into a tree of the same shape, the tree prints the same text again, a
/// second translation prints the same text, and a source the backend refuses is refused with a code of
/// its own range.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class VisualBasicParseBackTests {

  private static IEnumerable<TestCaseData> Cases()
    => from fixture in PrototypeFixture.Names
       from profile in VisualBasicProfile.Names
       select new TestCaseData(fixture, profile).SetName($"VisualBasicParseBack({fixture}, {profile})");

  [TestCaseSource(nameof(Cases))]
  public void EveryFixture_ParsesBackToTheTreeItWasPrintedFrom(string fixture, string profile) {
    var module = PrototypeFixture.Imported(fixture);
    var translation = VisualBasicFixtures.Translate(module, VisualBasicProfile.Parse(profile));
    if (translation.Source is null) {
      Assert.That(translation.Problems, Is.Not.Empty.And.All.Match(@"^error PLY265[5-8]: "));
      return;
    }

    var read = VbSyntaxReader.ReadModule(translation.Source);
    Assert.That(VbSyntaxRewriter.Shape(read), Is.EqualTo(VbSyntaxRewriter.Shape(translation.Tree!)), translation.Source);
    Assert.That(VbPrinter.Print(read), Is.EqualTo(translation.Source));
    Assert.That(VisualBasicFixtures.Translate(module, VisualBasicProfile.Parse(profile)).Source, Is.EqualTo(translation.Source));
  }

}

/// <summary>The fast slice of <see cref="VisualBasicParseBackTests"/>: the first prototype fixtures in the default profile.</summary>
[TestFixture]
public sealed class VisualBasicParseBackSliceTests {

  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.First))]
  public void Fixture_ParsesBackToTheTreeItWasPrintedFrom(string fixture) {
    var translation = VisualBasicFixtures.Translate(PrototypeFixture.Imported(fixture));
    Assert.That(translation.Source, Is.Not.Null, string.Join("; ", translation.Problems));
    var read = VbSyntaxReader.ReadModule(translation.Source!);
    Assert.That(VbSyntaxRewriter.Shape(read), Is.EqualTo(VbSyntaxRewriter.Shape(translation.Tree!)));
  }

  [TestCase("Numeric.bas")]
  [TestCase("Records.bas")]
  [TestCase("ClassicProbe.bas")]
  public void ClassicFixture_ImportsAndParsesBack(string file) {
    var source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic", file));
    var imported = Polypiler.Core.Frontends.VisualBasic.VisualBasicFrontend.Import(file, source);
    if (file == "ClassicProbe.bas") {
      // The probe asks LibreOffice what the frontend does not import: TypeName, CStr of a Double, CLng of a String.
      Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Code), Is.Not.Empty.And.All.EqualTo("PLY2646"));
      return;
    }

    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var translation = VisualBasicFixtures.Translate(imported.Module!);
    Assert.That(translation.Source, Is.Not.Null, string.Join("; ", translation.Problems));
    Assert.That(VbSyntaxRewriter.Shape(VbSyntaxReader.ReadModule(translation.Source!)), Is.EqualTo(VbSyntaxRewriter.Shape(translation.Tree!)));
  }

  [Test]
  public void CSharpFixtures_AlsoImportThroughTheCSharpFrontend()
    => Assert.That(CSharpFrontend.Import("Adler32.cs", PrototypeFixture.SourceOf("Adler32")).Module, Is.Not.Null);

}
