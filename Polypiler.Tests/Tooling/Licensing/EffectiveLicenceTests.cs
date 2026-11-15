using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="EffectiveLicence"/> and <see cref="LicenceClasses"/>: the label of every class alone and
/// in pairs, every strong and weak identifier, <c>AND</c>, <c>OR</c> and <c>WITH</c>, the permissive
/// joins, the empty and one-member closures, order, the members that cannot be part of an output, and
/// <see cref="EffectiveLicence.Understates"/>. The expected labels are derived by hand from the rule
/// table of the issue (GPL-3.0 §5, LGPL-3.0 §4).
/// </summary>
[TestFixture]
public sealed class EffectiveLicenceTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private static LicensedSource[] Closure(params string[] expressions) => [.. expressions.Select((expression, index) => new LicensedSource($"m{index}", expression))];

  private static (EffectiveLicenceResult? Result, string[] Messages) Compute(IEnumerable<LicensedSource> closure, LicencePolicy? policy = null) {
    var bag = new DiagnosticBag();
    var result = EffectiveLicence.Compute(closure, policy ?? _shipped, bag);
    return (result, [.. bag.Items.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")]);
  }

  private static string Label(params string[] expressions) {
    var (result, messages) = Compute(Closure(expressions));
    Assert.That(messages, Is.Empty);
    return result!.Licence;
  }

  // Scenario Outline: The effective licence follows from the closure.
  [TestCase(new[] { "MIT" }, "MIT")]
  [TestCase(new[] { "MIT", "Apache-2.0" }, "Apache-2.0 AND MIT")]
  [TestCase(new[] { "MIT", "LGPL-3.0-or-later" }, "LGPL-3.0-or-later")]
  [TestCase(new[] { "LGPL-2.0-or-later", "LGPL-3.0-or-later" }, "LGPL-3.0-or-later")]
  [TestCase(new[] { "LGPL-3.0-or-later", "GPL-3.0-only" }, "GPL-3.0-or-later")]
  [TestCase(new[] { "MIT", "GPL-2.0-or-later" }, "GPL-3.0-or-later")]
  [TestCase(new[] { "MIT OR GPL-3.0-only" }, "MIT")]
  public void TheEffectiveLicence_FollowsFromTheClosure(string[] closure, string effective) => Assert.That(Label(closure), Is.EqualTo(effective));

  // Test design 3: each class alone and each pair of classes; the label is that of the stronger class.
  [TestCase(new[] { "ISC" }, "ISC", LicenceClass.Permissive)]
  [TestCase(new[] { "LGPL-2.1-or-later" }, "LGPL-3.0-or-later", LicenceClass.WeakCopyleft)]
  [TestCase(new[] { "GPL-3.0-or-later" }, "GPL-3.0-or-later", LicenceClass.StrongCopyleft)]
  [TestCase(new[] { "Zlib", "LGPL-2.0-only" }, "LGPL-3.0-or-later", LicenceClass.WeakCopyleft)]
  [TestCase(new[] { "BSD-3-Clause", "GPL-3.0-only" }, "GPL-3.0-or-later", LicenceClass.StrongCopyleft)]
  [TestCase(new[] { "LGPL-3.0-only", "GPL-2.0-or-later" }, "GPL-3.0-or-later", LicenceClass.StrongCopyleft)]
  public void EachClassAndEachPairOfClasses_GiveTheLabelOfTheStrongerClass(string[] closure, string label, LicenceClass licenceClass)
    => Assert.That(Compute(Closure(closure)).Result, Is.EqualTo(new EffectiveLicenceResult(label, licenceClass, [.. Closure(closure).Where(member => LicenceClasses.Family(member.Expression) == licenceClass).Select(member => member.Origin)]))
      .Using<EffectiveLicenceResult>((left, right) => left.Licence == right.Licence && left.Class == right.Class && left.Because.SequenceEqual(right.Because)));

  // Test design 4: each strong-copyleft and each weak-copyleft identifier as the only member.
  [TestCaseSource(nameof(StrongAndWeak))]
  public void EachCopyleftIdentifier_AloneGivesTheLabelOfItsClass(string identifier, string label) => Assert.That(Label(identifier), Is.EqualTo(label));

  private static IEnumerable<TestCaseData> StrongAndWeak()
    => LicenceClasses.StrongCopyleft.Select(identifier => new TestCaseData(identifier, "GPL-3.0-or-later"))
      .Concat(LicenceClasses.WeakCopyleft.Select(identifier => new TestCaseData(identifier, "LGPL-3.0-or-later")));

  [Test]
  public void TheListsOfTheClasses_AreAllowedByTheShippedPolicy_AndAreThreeAndSix()
    => Assert.That((LicenceClasses.StrongCopyleft.Count, LicenceClasses.WeakCopyleft.Count, LicenceClasses.StrongCopyleft.Concat(LicenceClasses.WeakCopyleft).All(_shipped.Allowed.Contains)), Is.EqualTo((3, 6, true)));

  // Test design 5: OR is resolved to its weakest allowed operand, AND to its strongest.
  [TestCase("MIT OR GPL-3.0-only", "MIT")]
  [TestCase("MIT AND GPL-3.0-only", "GPL-3.0-or-later")]
  [TestCase("AGPL-3.0-only OR MIT", "MIT")]
  [TestCase("GPL-3.0-only OR LGPL-3.0-only", "LGPL-3.0-or-later")]
  [TestCase("MIT OR Apache-2.0", "MIT")]
  [TestCase("Apache-2.0 OR MIT", "Apache-2.0")]
  [TestCase("(MIT AND ISC) OR GPL-3.0-only", "ISC AND MIT")]
  [TestCase("MIT AND (Zlib OR GPL-3.0-only)", "MIT AND Zlib")]
  public void OrResolvesToItsWeakestAllowedOperand_AndAndToItsStrongest(string expression, string label) => Assert.That(Label(expression), Is.EqualTo(label));

  [Test]
  public void With_KeepsTheClassOfItsLicence_WhenThePolicyAcceptsTheException() {
    var policy = LicencePolicy.Parse(LicencePolicyTests.Policy(allowed: "[MIT, GPL-2.0-or-later]", exceptions: "[Classpath-exception-2.0]"), "p.yaml",
      LicencePolicyTests.Decisions + "\n| `GPL-2.0-or-later` | allowed | 2026-11-13 | test | T |\n");
    var (result, messages) = Compute(Closure("GPL-2.0-or-later WITH Classpath-exception-2.0", "MIT"), policy);
    Assert.That((result?.Licence, result?.Class, messages), Is.EqualTo(("GPL-3.0-or-later", LicenceClass.StrongCopyleft, Array.Empty<string>())));
  }

  [Test]
  public void With_AnExceptionThePolicyDoesNotAccept_IsDenied()
    => Assert.That(Compute(Closure("GPL-3.0-only WITH Classpath-exception-2.0")), Is.EqualTo(((EffectiveLicenceResult?)null,
      new[] { "PLY0024: 'm0': licence 'GPL-3.0-only WITH Classpath-exception-2.0' is denied and cannot be part of an output" })));

  // Test design 9: a closure of one member, and of none.
  [Test]
  public void AClosureOfOneMember_IsItsOwnLicence() => Assert.That(Compute([new("only.c", "BSD-2-Clause")]).Result, Is.EqualTo(new EffectiveLicenceResult("BSD-2-Clause", LicenceClass.Permissive, ["only.c"]))
    .Using<EffectiveLicenceResult>((left, right) => left.Licence == right.Licence && left.Class == right.Class && left.Because.SequenceEqual(right.Because)));

  [Test]
  public void AnEmptyClosure_IsAnInvalidOperation()
    => Assert.That(() => EffectiveLicence.Compute([], _shipped, new DiagnosticBag()), Throws.InvalidOperationException.With.Message.Contains("empty closure"));

  // Test design 10: permissive closures of one, two and three distinct identifiers, and a duplicate.
  [TestCase(new[] { "MIT" }, "MIT")]
  [TestCase(new[] { "MIT", "ISC" }, "ISC AND MIT")]
  [TestCase(new[] { "Zlib", "MIT", "ISC" }, "ISC AND MIT AND Zlib")]
  [TestCase(new[] { "MIT", "MIT" }, "MIT")]
  public void PermissiveIdentifiers_AreJoinedDistinctInOrdinalOrder(string[] closure, string label) => Assert.That(Label(closure), Is.EqualTo(label));

  // Test design 11: the same members in two orders give the same label and the same Because.
  [Test]
  public void TheOrderOfTheClosure_ChangesNothing() {
    LicensedSource[] members = [new("z.c", "GPL-3.0-only"), new("a.c", "MIT"), new("m.c", "GPL-2.0-or-later"), new("b.c", "LGPL-3.0-only")];
    var forward = Compute(members).Result!;
    var backward = Compute(members.Reverse()).Result!;
    Assert.That((forward.Licence, forward.Because, backward.Licence, backward.Because),
      Is.EqualTo(("GPL-3.0-or-later", new[] { "m.c", "z.c" }, "GPL-3.0-or-later", new[] { "m.c", "z.c" })));
  }

  [Test]
  public void Because_NamesEveryPermissiveMember_OfAPermissiveClosure_Once()
    => Assert.That(Compute([new("b.c", "MIT"), new("a.c", "ISC"), new("b.c", "MIT")]).Result!.Because, Is.EqualTo(new[] { "a.c", "b.c" }));

  [Test]
  public void ALargerClosure_NeverWeakensTheLabel() {
    string[] members = ["MIT", "Apache-2.0", "LGPL-2.1-or-later", "ISC", "GPL-3.0-only", "MIT", "LGPL-3.0-only"];
    var classes = Enumerable.Range(1, members.Length).Select(count => Compute(Closure(members[..count])).Result!.Class).ToList();
    Assert.That(classes, Is.Ordered);
  }

  // Test design 15: a reference-only or denied member gives no licence; every one is reported.
  [Test]
  public void ReferenceOnlyAndDeniedMembers_GiveNoLicence_AndAreEachReported()
    => Assert.That(Compute([new("old.c", "GPL-2.0-only"), new("ok.c", "MIT"), new("closed.c", "Proprietary-Foo")]), Is.EqualTo(((EffectiveLicenceResult?)null, new[] {
      "PLY0024: 'old.c': licence 'GPL-2.0-only' is reference-only and cannot be part of an output",
      "PLY0024: 'closed.c': licence 'Proprietary-Foo' is denied and cannot be part of an output"
    })));

  [Test]
  public void AnOrOfOnlyReferenceOnlyOperands_IsReferenceOnly()
    => Assert.That(Compute(Closure("GPL-2.0-only OR AGPL-3.0-only")).Messages, Is.EqualTo(new[] { "PLY0024: 'm0': licence 'GPL-2.0-only OR AGPL-3.0-only' is reference-only and cannot be part of an output" }));

  [Test]
  public void AnAndWithADeniedOperandOnEitherSide_IsDenied() {
    Assert.That(Compute(Closure("Proprietary-Foo AND MIT")).Result, Is.Null);
    Assert.That(Compute(Closure("MIT AND Proprietary-Foo")).Result, Is.Null);
  }

  // Test design 16: an expression that does not parse leaves the licence undetermined.
  [Test]
  public void AnExpressionThatDoesNotParse_IsPly0029()
    => Assert.That(Compute([new("x.c", "MIT AND")]), Is.EqualTo(((EffectiveLicenceResult?)null,
      new[] { "PLY0029: 'x.c': no licence could be determined: 'MIT AND' is not an SPDX expression: expected a licence after 'AND' but the expression ends" })));

  [Test]
  public void TheArguments_MustNotBeNull() {
    Assert.That(() => EffectiveLicence.Compute(null!, _shipped, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => EffectiveLicence.Compute([], null!, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => EffectiveLicence.Compute([], _shipped, null!), Throws.ArgumentNullException);
  }

  // Test design 6: a stated label equal to, stronger than or weaker than the effective one.
  [TestCase("LGPL-3.0-or-later", false)]
  [TestCase("GPL-3.0-or-later", false)]
  [TestCase("MIT", true)]
  [TestCase("NOASSERTION", true)]
  [TestCase("MIT AND", true)]
  [TestCase("LGPL-2.1-or-later OR GPL-3.0-only", false)]
  [TestCase("MIT OR LGPL-3.0-only", true)]
  public void Understates_WhenTheStatedClassIsWeakerOrNone(string stated, bool understates)
    => Assert.That(EffectiveLicence.Understates(stated, "LGPL-3.0-or-later", _shipped), Is.EqualTo(understates));

  [Test]
  public void Understates_RefusesAnEffectiveLicenceNoOutputCanBeUnder()
    => Assert.That(() => EffectiveLicence.Understates("MIT", "GPL-2.0-only", _shipped), Throws.ArgumentException.With.Message.Contains("'GPL-2.0-only' is no licence an output can be under"));

  [TestCase("MIT", LicenceClass.Permissive)]
  [TestCase("GPL-3.0-only AND MIT", LicenceClass.StrongCopyleft)]
  [TestCase("GPL-2.0-only", null)]
  [TestCase("(", null)]
  public void ClassOf_AnExpression(string expression, LicenceClass? licenceClass) => Assert.That(EffectiveLicence.ClassOf(expression, _shipped), Is.EqualTo(licenceClass));

  [TestCase("GPL-2.0-only", LicenceClass.StrongCopyleft)]
  [TestCase("GPL-2.0+", LicenceClass.StrongCopyleft)]
  [TestCase("AGPL-3.0-or-later", LicenceClass.StrongCopyleft)]
  [TestCase("LGPL-2.1-only", LicenceClass.WeakCopyleft)]
  [TestCase("LGPL-3.0-or-later", LicenceClass.WeakCopyleft)]
  [TestCase("MIT", LicenceClass.Permissive)]
  [TestCase("EUPL-1.2", LicenceClass.Permissive)]
  public void Family_ClassifiesByTheListsAndThePrefixes(string identifier, LicenceClass licenceClass) => Assert.That(LicenceClasses.Family(identifier), Is.EqualTo(licenceClass));

  [Test]
  public void Of_HasNoClassForAnIdentifierThePolicyDoesNotAllow()
    => Assert.That((LicenceClasses.Of("GPL-2.0-only", _shipped), LicenceClasses.Of("EUPL-1.2", _shipped), LicenceClasses.Of("Zlib", _shipped)), Is.EqualTo(((LicenceClass?)null, (LicenceClass?)null, (LicenceClass?)LicenceClass.Permissive)));

  [TestCase(LicenceClass.Permissive, "permissive")]
  [TestCase(LicenceClass.WeakCopyleft, "weak-copyleft")]
  [TestCase(LicenceClass.StrongCopyleft, "strong-copyleft")]
  public void Name_OfAClass(LicenceClass licenceClass, string name) => Assert.That(LicenceClasses.Name(licenceClass), Is.EqualTo(name));

}
