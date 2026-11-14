using System;
using NUnit.Framework;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="SpdxExpression"/>: the grammar of the SPDX specification's annex on licence expressions
/// (precedence <c>WITH</c> over <c>AND</c> over <c>OR</c>, parentheses) and the evaluation of a status
/// over it, against a hand-derived table.
/// </summary>
[TestFixture]
public sealed class SpdxExpressionTests {

  /// <summary>A small policy: MIT, Apache-2.0, ISC and GPL-3.0-only allowed, AGPL-3.0-only and GPL-2.0-only reference-only.</summary>
  private static LicenceStatus Status(string licence) => licence switch {
    "MIT" or "Apache-2.0" or "ISC" or "GPL-3.0-only" => LicenceStatus.Allowed,
    "AGPL-3.0-only" or "GPL-2.0-only" => LicenceStatus.ReferenceOnly,
    _ => LicenceStatus.Denied
  };

  private static LicenceStatus Evaluate(string text, params string[] exceptions)
    => SpdxExpression.Parse(text).Evaluate(Status, exception => Array.IndexOf(exceptions, exception) >= 0);

  [TestCase("MIT", LicenceStatus.Allowed)]
  [TestCase("MIT AND GPL-3.0-only", LicenceStatus.Allowed)]
  [TestCase("MIT OR AGPL-3.0-only", LicenceStatus.Allowed)]
  [TestCase("MIT AND AGPL-3.0-only", LicenceStatus.ReferenceOnly)]
  [TestCase("(MIT OR Apache-2.0) AND ISC", LicenceStatus.Allowed)]
  [TestCase("MIT OR AGPL-3.0-only AND ISC", LicenceStatus.Allowed)]
  [TestCase("Proprietary-Foo OR AGPL-3.0-only AND ISC", LicenceStatus.ReferenceOnly)]
  [TestCase("(Proprietary-Foo OR AGPL-3.0-only) AND Proprietary-Bar", LicenceStatus.Denied)]
  [TestCase("GPL-2.0-only", LicenceStatus.ReferenceOnly)]
  [TestCase("Proprietary-Foo", LicenceStatus.Denied)]
  [TestCase("GPL-2.0+", LicenceStatus.Denied)]
  [TestCase("DocumentRef-spdx-tool-1.2:LicenseRef-MIT-Style-2", LicenceStatus.Denied)]
  [TestCase("  MIT\tAND\n(ISC)  ", LicenceStatus.Allowed)]
  public void Evaluate_TakesTheBestOperandOfOrAndTheWorstOfAnd(string text, LicenceStatus expected)
    => Assert.That(Evaluate(text), Is.EqualTo(expected));

  [Test]
  public void With_IsTheLicencesStatus_OnlyWhenTheExceptionIsAccepted() {
    Assert.That(Evaluate("GPL-2.0-only WITH Classpath-exception-2.0"), Is.EqualTo(LicenceStatus.Denied));
    Assert.That(Evaluate("GPL-2.0-only WITH Classpath-exception-2.0", "Classpath-exception-2.0"), Is.EqualTo(LicenceStatus.ReferenceOnly));
    Assert.That(Evaluate("MIT OR GPL-2.0-only WITH Classpath-exception-2.0"), Is.EqualTo(LicenceStatus.Allowed));
  }

  [Test]
  public void Parse_BuildsTheTreeByPrecedence() {
    var expression = SpdxExpression.Parse("MIT OR GPL-2.0-only WITH Classpath-exception-2.0 AND ISC");
    Assert.That(expression, Is.EqualTo(new SpdxOr(new SpdxLicence("MIT"), new SpdxAnd(new SpdxWith(new SpdxLicence("GPL-2.0-only"), "Classpath-exception-2.0"), new SpdxLicence("ISC")))));
    Assert.That(expression.Licences, Is.EqualTo(new[] { "MIT", "GPL-2.0-only", "ISC" }));
    Assert.That(expression.ToString(), Is.EqualTo("MIT OR GPL-2.0-only WITH Classpath-exception-2.0 AND ISC"));
    Assert.That(SpdxExpression.Parse("(MIT OR ISC) AND (Apache-2.0)").ToString(), Is.EqualTo("(MIT OR ISC) AND Apache-2.0"));
  }

  [TestCase("", "the expression is empty")]
  [TestCase("   ", "the expression is empty")]
  [TestCase("MIT OR", "expected a licence after 'OR' but the expression ends")]
  [TestCase("(MIT", "missing ')'")]
  [TestCase("AND MIT", "expected a licence but found 'AND'")]
  [TestCase("MIT)", "unexpected ')'")]
  [TestCase("MIT ISC", "unexpected 'ISC'")]
  [TestCase("(MIT ISC)", "expected ')' but found 'ISC'")]
  [TestCase("MIT WITH", "expected an exception after 'WITH' but the expression ends")]
  [TestCase("MIT WITH OR", "expected an exception after 'WITH' but found 'OR'")]
  [TestCase("(MIT OR ISC) WITH Foo-exception", "'WITH' must follow a licence identifier, not a parenthesized expression")]
  [TestCase("MIT/ISC", "'MIT/ISC' is not an SPDX identifier")]
  [TestCase("mit and isc", "unexpected 'and'")]
  public void Parse_RefusesWhatIsNotAnExpression(string text, string message)
    => Assert.That(() => SpdxExpression.Parse(text), Throws.TypeOf<FormatException>().With.Message.EqualTo(message));

}
