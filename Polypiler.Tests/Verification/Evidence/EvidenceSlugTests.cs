using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Evidence;

namespace Polypiler.Tests.Verification.Evidence;

/// <summary><see cref="Slug.From"/>: the folder name of a feature or scenario title.</summary>
[TestFixture]
public sealed class EvidenceSlugTests {

  /// <summary>The examples of the feature, derived by hand from the rule.</summary>
  [TestCase("JavaScript Cipher to C#", "javascript-cipher-to-c")]
  [TestCase("A→B→A round trip (u32)", "a-b-a-round-trip-u32")]
  [TestCase("--opt none vs --opt speed", "opt-none-vs-opt-speed")]
  [TestCase("§", "scenario", TestName = "Test design 6: a title with nothing left is 'scenario'")]
  [TestCase("", "scenario")]
  [TestCase("ÄBC", "bc", TestName = "Letters outside a-z separate")]
  [TestCase("abc", "abc")]
  public void Slug_FollowsTheRule(string title, string slug) => Assert.That(Slug.From(title), Is.EqualTo(slug));

  /// <summary>Test design 7: 80 characters stay, 81 are cut to 80.</summary>
  [TestCase(79, 79)]
  [TestCase(80, 80)]
  [TestCase(81, 80)]
  public void Slug_IsCutTo80Characters(int length, int expected)
    => Assert.That(Slug.From(new string('a', length)), Is.EqualTo(new string('a', expected)));

  /// <summary>Test design 8: a cut that ends on a separator does not leave a trailing <c>-</c>.</summary>
  [Test]
  public void Slug_CutAtASeparator_HasNoTrailingDash()
    => Assert.That(Slug.From(new string('a', 79) + " b"), Is.EqualTo(new string('a', 79)));

  /// <summary>A title is required.</summary>
  [Test]
  public void Slug_OfNull_Throws() => Assert.That(() => Slug.From(null!), Throws.TypeOf<ArgumentNullException>());

  /// <summary>An identity's slugs: the scenario's ends in the example row, the tags lose their <c>@</c>.</summary>
  [Test]
  public void Identity_HasSlugsAndTags() {
    var identity = new ScenarioIdentity("Evidence bundle per BDD scenario", "Titles become stable folder names", ["@translation", "translation", "Slow"], 2);
    Assert.That((identity.FeatureSlug, identity.ScenarioSlug, identity.Row), Is.EqualTo(("evidence-bundle-per-bdd-scenario", "titles-become-stable-folder-names-2", 2)));
    Assert.That(identity.Tags, Is.EqualTo(new[] { "translation", "Slow" }));
    Assert.That(new ScenarioIdentity("f", "A b", []).ScenarioSlug, Is.EqualTo("a-b"));
    Assert.That(() => new ScenarioIdentity("f", "s", [], -1), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ScenarioIdentity(null!, "s", []), Throws.ArgumentNullException);
    Assert.That(() => new ScenarioIdentity("f", null!, []), Throws.ArgumentNullException);
    Assert.That(() => new ScenarioIdentity("f", "s", null!), Throws.ArgumentNullException);
  }

}
