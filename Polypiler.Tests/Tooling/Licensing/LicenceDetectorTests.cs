using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="LicenceTexts"/> and <see cref="LicenceDetector"/>: fingerprints, the shipped reference
/// texts, header comment styles and the 30-line boundary, licence file names, the nearest licence file
/// and how a declared licence selects the variant of a shared text.
/// </summary>
[TestFixture]
public sealed class LicenceDetectorTests {

  /// <summary>The shipped reference texts.</summary>
  public static LicenceTexts ShippedTexts { get; } = LicenceTexts.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "licensing", LicencePolicy.TextsFolder));

  /// <summary>The repository's own licence file: the LGPL-3.0 text as gnu.org publishes it.</summary>
  public static string Lgpl3Text { get; } = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "LICENSE"));

  /// <summary>The MIT text with a real holder in place of the SPDX placeholder.</summary>
  public static string MitText { get; } = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "licenses", "weights", "LICENSE"));

  private static readonly LicencePolicy _policy = LicencePolicy.Parse(
    LicencePolicyTests.Policy(overrides: "[{ glob: 'legacy/*.asm', licence: BSD-3-Clause, reason: 'reviewed', reviewer: owner }]"), "p.yaml", LicencePolicyTests.Decisions);

  private static readonly string _bom = ((char)0xFEFF).ToString();

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-licence-detector-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private LicenceDetection Detect(string path, string? declared = null, LicenceTexts? texts = null)
    => new LicenceDetector(_policy, texts ?? ShippedTexts).Detect(this._root, Path.Combine(this._root, path), path, declared);

  [Test]
  public void Fingerprint_IgnoresLineEndsWrappingQuotesNoticesAndAByteOrderMark() {
    var lf = LicenceTexts.Fingerprint(Lgpl3Text);
    Assert.That(LicenceTexts.Fingerprint(Lgpl3Text.Replace("\n", "\r\n")), Is.EqualTo(lf));
    Assert.That(LicenceTexts.Fingerprint(_bom + "  " + Lgpl3Text.Replace(" ", "  ").Replace("\n", " ") + "\n\n"), Is.Not.EqualTo(lf), "a notice line joined into the text is not dropped");
    Assert.That(LicenceTexts.Fingerprint(_bom + Lgpl3Text.Replace(" the ", "\nthe ").Replace("  ", " ")), Is.EqualTo(lf));
    Assert.That(LicenceTexts.Fingerprint("A “quoted” ‘word’"), Is.EqualTo(LicenceTexts.Fingerprint("A \"quoted\" 'word'")));
    foreach (var notice in (string[])["Copyright (c) 2020 A", "Copyright © B", "Copyright 1999 C", "(c) 2001 D", "© E", "  COPYRIGHT (C) F"])
      Assert.That(LicenceTexts.Fingerprint($"{notice}\nPermission granted."), Is.EqualTo(LicenceTexts.Fingerprint("Permission granted.")), notice);
    Assert.That(LicenceTexts.Fingerprint("copyright on the Program\nPermission granted."), Is.Not.EqualTo(LicenceTexts.Fingerprint("Permission granted.")));
  }

  [Test]
  public void ShippedTexts_KnowTheRepositoryLicenceAsTheLgpl3Family_AndARealMitLicence() {
    Assert.That(ShippedTexts.Identify(Lgpl3Text), Is.EqualTo(new[] { "LGPL-3.0-only", "LGPL-3.0-or-later" }));
    Assert.That(ShippedTexts.Identify(MitText), Is.EqualTo(new[] { "MIT" }));
    Assert.That(ShippedTexts.Identify("Do what you like."), Is.Null);
    Assert.That(ShippedTexts.Count, Is.GreaterThanOrEqualTo(14));
    Assert.That(LicenceTexts.Load(Path.Combine(this._root, "none")).Count, Is.Zero);
  }

  [TestCase(new[] { "MIT" }, "MIT")]
  [TestCase(new[] { "LGPL-3.0-only", "LGPL-3.0-or-later" }, "LGPL-3.0")]
  [TestCase(new[] { "AGPL-3.0-only", "GPL-3.0-only" }, "AGPL-3.0-only | GPL-3.0-only")]
  [TestCase(new[] { "X", "X-only" }, "X")]
  public void Family_NamesTheSharedPrefixOfTheVariants(string[] licences, string expected)
    => Assert.That(LicenceTexts.Family(licences), Is.EqualTo(expected));

  [TestCase("// SPDX-License-Identifier: MIT")]
  [TestCase("# SPDX-License-Identifier: MIT")]
  [TestCase("' SPDX-License-Identifier: MIT")]
  [TestCase("; SPDX-License-Identifier: MIT")]
  [TestCase("/* SPDX-License-Identifier: MIT */")]
  [TestCase("REM SPDX-License-Identifier: MIT")]
  [TestCase("<!-- SPDX-License-Identifier: MIT -->")]
  [TestCase("// SPDX-License-Identifier:MIT\r")]
  [TestCase(" * SPDX-License-Identifier: MIT")]
  [TestCase("-- SPDX-License-Identifier: MIT")]
  [TestCase("rem SPDX-License-Identifier: MIT")]
  [TestCase("(* SPDX-License-Identifier: MIT *)")]
  [TestCase("SPDX-License-Identifier: MIT")]
  public void Header_IsFoundInEveryCommentStyle(string line)
    => Assert.That(LicenceDetector.Header($"first\n{line}\nrest"), Is.EqualTo("MIT"));

  [TestCase("/// an <c>SPDX-License-Identifier: &lt;expression&gt;</c> line")]
  [TestCase("  [TestCase(\"// SPDX-License-Identifier: MIT\")]")]
  [TestCase("var tag = \"SPDX-License-Identifier: MIT\";")]
  [TestCase("// spdx-license-identifier: MIT")]
  public void LineThatOnlyMentionsTheTag_IsNoHeader(string line)
    => Assert.That(LicenceDetector.Header($"{line}\n"), Is.Null);

  [Test]
  public void Header_IsFoundOnLine30_ButNotOnLine31() {
    var filler = string.Concat(Enumerable.Repeat("x\n", 29));
    Assert.That(LicenceDetector.Header(filler + "// SPDX-License-Identifier: MIT\n"), Is.EqualTo("MIT"));
    Assert.That(LicenceDetector.Header(filler + "x\n// SPDX-License-Identifier: MIT\n"), Is.Null);

    Scratch.Write(this._root, "LICENSE", MitText);
    Scratch.Write(this._root, "late.c", filler + "x\n// SPDX-License-Identifier: GPL-3.0-only\n");
    Assert.That(this.Detect("late.c"), Is.EqualTo(new LicenceDetection("MIT", LicenceSource.LicenceFile)).Using<LicenceDetection>(Same));
  }

  [Test]
  public void HeaderOverridesOverrideAndLicenceFile_InThatOrder() {
    Scratch.Write(this._root, "LICENSE", MitText);
    Scratch.Write(this._root, "legacy/a.asm", "; SPDX-License-Identifier: ISC\n");
    Scratch.Write(this._root, "legacy/b.asm", "; no header\n");
    Assert.That(this.Detect("legacy/a.asm"), Is.EqualTo(new LicenceDetection("ISC", LicenceSource.Header)).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("legacy/b.asm"), Is.EqualTo(new LicenceDetection("BSD-3-Clause", LicenceSource.Override)).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("LICENSE"), Is.EqualTo(new LicenceDetection("MIT", LicenceSource.LicenceFile)).Using<LicenceDetection>(Same));
  }

  [TestCase("LICENSE")]
  [TestCase("LICENSE.txt")]
  [TestCase("LICENSE.md")]
  [TestCase("COPYING")]
  [TestCase("COPYING.LESSER")]
  [TestCase("license")]
  public void EveryLicenceFileName_IsRecognized(string name) {
    Scratch.Write(this._root, name, MitText);
    Scratch.Write(this._root, "a/b.c", "");
    Assert.That(this.Detect("a/b.c"), Is.EqualTo(new LicenceDetection("MIT", LicenceSource.LicenceFile)).Using<LicenceDetection>(Same));
  }

  [Test]
  public void CopyingLesser_IsPreferredOverCopying() {
    Scratch.Write(this._root, "COPYING", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "licenses", "gpl-nested", "sub", "LICENSE")));
    Scratch.Write(this._root, "COPYING.LESSER", Lgpl3Text);
    Scratch.Write(this._root, "x.c", "");
    Assert.That(this.Detect("x.c", "LGPL-3.0-or-later").Licence, Is.EqualTo("LGPL-3.0-or-later"));
  }

  [Test]
  public void NearestLicenceFile_Wins() {
    Scratch.Write(this._root, "LICENSE", Lgpl3Text);
    Scratch.Write(this._root, "sub/LICENSE", MitText);
    Scratch.Write(this._root, "sub/x.c", "");
    Scratch.Write(this._root, "y.c", "");
    Assert.That(this.Detect("sub/x.c"), Is.EqualTo(new LicenceDetection("MIT", LicenceSource.LicenceFile)).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("y.c", "LGPL-3.0-or-later"), Is.EqualTo(new LicenceDetection("LGPL-3.0-or-later", LicenceSource.LicenceFile)).Using<LicenceDetection>(Same));
  }

  [Test]
  public void SharedText_NeedsADeclarationOfItsFamily() {
    Scratch.Write(this._root, "LICENSE", Lgpl3Text);
    Scratch.Write(this._root, "x.c", "");
    Assert.That(this.Detect("x.c"), Is.EqualTo(new LicenceDetection("LGPL-3.0", LicenceSource.LicenceFile,
      "'LICENSE' holds the LGPL-3.0 text and nothing declares which of LGPL-3.0-only, LGPL-3.0-or-later applies")).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("x.c", "LGPL-3.0-only").Licence, Is.EqualTo("LGPL-3.0-only"));
    Assert.That(this.Detect("x.c", "LGPL-3.0-or-later AND LGPL-2.0-or-later").Licence, Is.EqualTo("LGPL-3.0-or-later"));
    Assert.That(this.Detect("x.c", "MIT"), Is.EqualTo(new LicenceDetection("LGPL-3.0", LicenceSource.LicenceFile,
      Disagreement: "declared licence 'MIT' disagrees with detected 'LGPL-3.0'")).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("x.c", "MIT OR").Disagreement, Is.EqualTo("declared licence 'MIT OR' disagrees with detected 'LGPL-3.0'"));
  }

  [Test]
  public void UnknownTextUnreadableFileAndNoLicenceFile_AreUndetermined() {
    Scratch.Write(this._root, "known/LICENSE", "Do what you like.");
    Scratch.Write(this._root, "known/x.c", "");
    Scratch.Write(this._root, "orphan.py", "");
    Assert.That(this.Detect("known/x.c").Undetermined, Is.EqualTo("the text of 'known/LICENSE' is not a known licence text"));
    Assert.That(this.Detect("known/x.c").Undetermined, Is.EqualTo("the text of 'known/LICENSE' is not a known licence text"), "cached");
    Assert.That(this.Detect("orphan.py"), Is.EqualTo(new LicenceDetection(null, null)).Using<LicenceDetection>(Same));
    Assert.That(this.Detect("missing.c").Undetermined, Does.StartWith("the file cannot be read: "));
  }

  [Test]
  public void UnreadableLicenceFile_IsUndetermined_AndAFolderNamedLicenseIsNone() {
    Directory.CreateDirectory(Path.Combine(this._root, "LICENSE"));
    var copying = Scratch.Write(this._root, "COPYING", MitText);
    Scratch.Write(this._root, "x.c", "");
    using (new FileStream(copying, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      Assert.That(this.Detect("x.c").Undetermined, Does.StartWith("'COPYING' cannot be read: "));
  }

  [Test]
  public void ReferenceTextNamedByNoIdentifier_IsUndetermined() {
    var texts = new LicenceTexts([("not an id", "Do what you like.")]);
    Scratch.Write(this._root, "LICENSE", "Do what you like.");
    Scratch.Write(this._root, "x.c", "");
    Assert.That(this.Detect("x.c", texts: texts).Undetermined, Is.EqualTo("'not an id' is not an SPDX expression: unexpected 'an'"));
  }

  [Test]
  public void BadHeaderExpression_IsUndetermined_WithTheHeaderAsLicence() {
    Scratch.Write(this._root, "x.c", "// SPDX-License-Identifier: MIT OR\n");
    Assert.That(this.Detect("x.c"), Is.EqualTo(new LicenceDetection("MIT OR", LicenceSource.Header,
      "'MIT OR' is not an SPDX expression: expected a licence after 'OR' but the expression ends")).Using<LicenceDetection>(Same));
  }

  [Test]
  public void IsLicenceFile_IgnoresCase() {
    Assert.That(LicenceDetector.IsLicenceFile("copying.lesser"), Is.True);
    Assert.That(LicenceDetector.IsLicenceFile("LICENSE.rst"), Is.False);
  }

  /// <summary>Compares detections without their parsed expression.</summary>
  private static bool Same(LicenceDetection left, LicenceDetection right)
    => (left with { Expression = null }) == (right with { Expression = null });

}
