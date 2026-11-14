using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary><see cref="LicenceDeclarations"/>: the last matching entry wins, and every malformed file is a <c>PLY0026</c>.</summary>
[TestFixture]
public sealed class LicenceDeclarationsTests {

  [Test]
  public void LastMatchingEntry_Wins() {
    var declarations = LicenceDeclarations.Parse("schema: 1\ndeclared:\n  - { glob: '**', licence: LGPL-3.0-or-later }\n  - { glob: 'sub/**', licence: GPL-3.0-only }\n", "licences.yaml");
    Assert.That(declarations.For("sub/deep/y.c"), Is.EqualTo("GPL-3.0-only"));
    Assert.That(declarations.For("other/z.c"), Is.EqualTo("LGPL-3.0-or-later"));
    Assert.That(LicenceDeclarations.None.For("x"), Is.Null);
    Assert.That(LicenceDeclarations.Parse("schema: 1\ndeclared: []\n", "d").For("x"), Is.Null);
  }

  [TestCase("declared: [", "not YAML")]
  [TestCase("", "the file must be a mapping")]
  [TestCase("- 1\n", "the file must be a mapping")]
  [TestCase("schema: 1\n", "the file must be a mapping")]
  [TestCase("schema: 2\ndeclared: []\n", "the file must be a mapping")]
  [TestCase("schema: 1\nother: []\n", "the file must be a mapping")]
  [TestCase("schema: 1\ndeclared: {}\n", "the file must be a mapping")]
  [TestCase("schema: 1\ndeclared: [3]\n", "an entry of 'declared' must be a mapping of exactly 'glob' and 'licence'")]
  [TestCase("schema: 1\ndeclared: [{ glob: '**' }]\n", "an entry of 'declared' must be")]
  [TestCase("schema: 1\ndeclared: [{ glob: '**', other: MIT }]\n", "an entry of 'declared' must be")]
  [TestCase("schema: 1\ndeclared: [{ glob: '', licence: MIT }]\n", "an entry of 'declared' must be")]
  [TestCase("schema: 1\ndeclared: [{ other: '**', licence: MIT }]\n", "an entry of 'declared' must be")]
  [TestCase("schema: 1\ndeclared: [{ glob: '**', licence: [MIT] }]\n", "an entry of 'declared' must be")]
  [TestCase("schema: 1\ndeclared: [{ glob: '**', licence: 'MIT OR' }]\n", "'MIT OR' is not an SPDX expression")]
  public void MalformedFile_IsPly0026(string text, string message) {
    var problems = Assert.Throws<CorpusException>(() => LicenceDeclarations.Parse(text, "licences.yaml"))!.Diagnostics;
    Assert.That(problems.Select(problem => problem.Code), Is.All.EqualTo("PLY0026"));
    Assert.That(problems[0].Message, Does.StartWith("corpus manifest invalid: licence declarations: " + message));
  }

  [Test]
  public void Load_OfARootWithoutTheFile_DeclaresNothing_AndAnUnreadableFileIsRefused() {
    var root = Scratch.Create("polypiler-licence-declarations-");
    try {
      Assert.That(LicenceDeclarations.Load(root), Is.SameAs(LicenceDeclarations.None));
      var file = Scratch.Write(root, LicenceDeclarations.FileName, "schema: 1\ndeclared: [{ glob: '**', licence: MIT }]\n");
      Assert.That(LicenceDeclarations.Load(root).For("a"), Is.EqualTo("MIT"));
      using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Assert.That(() => LicenceDeclarations.Load(root), Throws.TypeOf<CorpusException>().With.Message.Contains("licence declarations: cannot read"));
    } finally {
      Scratch.Delete(root);
    }
  }

}
