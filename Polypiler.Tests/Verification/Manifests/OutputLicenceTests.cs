using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Tests.Tooling.Licensing;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>
/// <see cref="OutputLicence"/>: the licence <c>translate</c> states for an output made of its input and
/// MIT helpers, for each class of input licence, without helpers and without a known input licence,
/// and held against the effective licence <c>licenses scan</c> computes with the shipped policy, which
/// it may never understate.
/// </summary>
[TestFixture]
public sealed class OutputLicenceTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  // Test design 1: the input licence MIT, LGPL, GPL and unknown, with MIT helpers.
  [TestCase("MIT", "MIT")]
  [TestCase("LGPL-2.1-only", "LGPL-3.0-or-later")]
  [TestCase("LGPL-3.0-or-later", "LGPL-3.0-or-later")]
  [TestCase("GPL-3.0-only", "GPL-3.0-or-later")]
  [TestCase("AGPL-3.0-or-later", "GPL-3.0-or-later")]
  [TestCase(null, "NOASSERTION")]
  [TestCase("", "NOASSERTION")]
  [TestCase(" NOASSERTION ", "NOASSERTION")]
  public void TheInputsClass_DecidesTheLabel(string? input, string expected) => Assert.That(OutputLicence.Of(input, ["MIT"]), Is.EqualTo(expected));

  // Test design 3: without a helper the output is under the input's licence as it is written.
  [TestCase("MIT")]
  [TestCase("GPL-2.0-or-later")]
  [TestCase("MIT OR Apache-2.0")]
  public void WithoutHelpers_TheLicenceIsTheInputs(string input) => Assert.That(OutputLicence.Of($" {input} ", []), Is.EqualTo(input));

  [TestCase("Apache-2.0", new[] { "MIT" }, "Apache-2.0 AND MIT")]
  [TestCase("Apache-2.0 AND MIT", new[] { "MIT AND BSD-2-Clause", "MIT" }, "Apache-2.0 AND BSD-2-Clause AND MIT")]
  [TestCase("LicenseRef-Proprietary", new[] { "MIT" }, "LicenseRef-Proprietary AND MIT")]
  [TestCase("MIT OR Apache-2.0", new[] { "MIT" }, "(MIT OR Apache-2.0) AND MIT")]
  [TestCase("(MIT AND ISC)", new[] { "MIT" }, "ISC AND MIT")]
  [TestCase("Apache-2.0 WITH LLVM-exception", new[] { "MIT" }, "(Apache-2.0 WITH LLVM-exception) AND MIT")]
  [TestCase("MIT", new[] { "LGPL-3.0-only" }, "LGPL-3.0-or-later")]
  [TestCase("LGPL-2.1-only", new[] { "GPL-3.0-or-later" }, "GPL-3.0-or-later")]
  [TestCase("MIT OR GPL-3.0-only", new[] { "MIT" }, "GPL-3.0-or-later")]
  [TestCase("GPL-2.0-only WITH Classpath-exception-2.0", new[] { "MIT" }, "GPL-3.0-or-later")]
  public void PermissiveLicences_AreJoined_AndCopyleftGivesItsLabel(string input, string[] helpers, string expected)
    => Assert.That(OutputLicence.Of(input, helpers), Is.EqualTo(expected));

  // The label never understates what the scan computes from the same closure with the shipped policy.
  [TestCase("MIT", new[] { "MIT" })]
  [TestCase("Apache-2.0", new[] { "MIT", "MIT AND BSD-2-Clause" })]
  [TestCase("LGPL-2.1-only", new[] { "MIT" })]
  [TestCase("LGPL-2.1-or-later", new[] { "MIT" })]
  [TestCase("GPL-3.0-only", new[] { "MIT" })]
  [TestCase("MIT OR Apache-2.0", new[] { "MIT" })]
  [TestCase("MIT OR GPL-3.0-only", new[] { "MIT" })]
  public void TheLabel_DoesNotUnderstateTheEffectiveLicence(string input, string[] helpers) {
    var bag = new DiagnosticBag();
    var effective = EffectiveLicence.Compute([new("input", input), .. helpers.Select((helper, index) => new LicensedSource($"helper{index}", helper))], _shipped, bag);
    Assert.That(bag.Items, Is.Empty);
    var stated = OutputLicence.Of(input, helpers);
    Assert.That(EffectiveLicence.Understates(stated, effective!.Licence, _shipped), Is.False, $"{stated} against {effective.Licence}");
  }

  [Test]
  public void Of_RefusesNullHelpers() => Assert.That(() => OutputLicence.Of("MIT", null!), Throws.ArgumentNullException);

  [TestCase("// SPDX-License-Identifier: MIT\nusing System;\n", "MIT")]
  [TestCase("using System;\n//SPDX-License-Identifier:   Apache-2.0 OR MIT  \r\n", "Apache-2.0 OR MIT")]
  [TestCase("/* SPDX-License-Identifier: LGPL-2.1-only */\nvar a = 1;\n", "LGPL-2.1-only")]
  [TestCase(" * SPDX-License-Identifier: BSD-2-Clause\n", "BSD-2-Clause")]
  [TestCase("# SPDX-License-Identifier: GPL-3.0-only\n", "GPL-3.0-only")]
  [TestCase("SPDX-License-Identifier: ISC\n", "ISC")]
  [TestCase("var s = \"SPDX-License-Identifier: MIT\";\n", null)]
  [TestCase("public static class A { }\n", null)]
  [TestCase("", null)]
  public void Header_ReadsTheLicenceOfASource_AsTheScannerDoes(string source, string? expected) {
    Assert.That(OutputLicence.Header(source), Is.EqualTo(expected));
    Assert.That(LicenceDetector.Header(source), Is.EqualTo(expected));
  }

  [TestCase(29, "MIT")]
  [TestCase(30, null)]
  public void Header_IsLookedForInTheFirstThirtyLines_AsTheScannerDoes(int before, string? expected) {
    var source = string.Concat(Enumerable.Repeat("// x\n", before)) + "// SPDX-License-Identifier: MIT\n";
    Assert.That((OutputLicence.Header(source), LicenceDetector.Header(source)), Is.EqualTo((expected, expected)));
  }

  [Test]
  public void Header_RefusesNull() => Assert.That(() => OutputLicence.Header(null!), Throws.ArgumentNullException);

}
