using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The filter of <c>dotnet test</c>, evaluated for a test known by its categories.</summary>
[TestFixture]
public sealed class TestFilterTests {

  private static bool Selects(string filter, params string[] categories) => TestFilter.Parse(filter).Selects(categories);

  [TestCase("TestCategory=Slow", new[] { "Slow" }, true)]
  [TestCase("TestCategory=Slow", new[] { "slow" }, true)]
  [TestCase("TestCategory=Slow", new[] { "SlowToo" }, false)]
  [TestCase("TestCategory!=Slow", new[] { "Slow" }, false)]
  [TestCase("TestCategory!=Slow", new string[0], true)]
  [TestCase("TestCategory~Layer.", new[] { "Layer.rust" }, true)]
  [TestCase("TestCategory!~Layer.", new[] { "Layer.rust" }, false)]
  [TestCase("TestCategory!~Layer.", new[] { "Slow" }, true)]
  [TestCase("testcategory = Slow", new[] { "Slow" }, true)]
  public void Condition_OnCategories(string filter, string[] categories, bool selected) => Assert.That(Selects(filter, categories), Is.EqualTo(selected));

  [TestCase("FullyQualifiedName=A.B", false)]
  [TestCase("FullyQualifiedName~A", false)]
  [TestCase("Name!=A", true)]
  [TestCase("ClassName!~A", true)]
  [TestCase("Polypiler.Tests", false)]
  public void Condition_OnAnotherProperty_IsUnknown(string filter, bool selected) => Assert.That(Selects(filter, "Slow"), Is.EqualTo(selected));

  [TestCase("TestCategory!~Layer.|TestCategory=Layer.core", new[] { "Slow" }, true)]
  [TestCase("TestCategory!~Layer.|TestCategory=Layer.core", new[] { "Slow", "Layer.core" }, true)]
  [TestCase("TestCategory!~Layer.|TestCategory=Layer.core", new[] { "Slow", "Layer.rust" }, false)]
  [TestCase("TestCategory=A|TestCategory=B&TestCategory=C", new[] { "A" }, true)]
  [TestCase("(TestCategory=A|TestCategory=B)&TestCategory=C", new[] { "A" }, false)]
  [TestCase("(TestCategory=A|TestCategory=B)&TestCategory=C", new[] { "B", "C" }, true)]
  [TestCase("(TestCategory!~Layer.|TestCategory=Layer.core)&TestCategory!=Performance", new[] { "Performance" }, false)]
  public void Expression_AndBindsTighterThanOr(string filter, string[] categories, bool selected) => Assert.That(Selects(filter, categories), Is.EqualTo(selected));

  [Test]
  public void Escape_TakesTheNextCharacterAsItIs() => Assert.That(Selects(@"TestCategory=a\|b", "a|b"), Is.True);

  [TestCase("")]
  [TestCase("TestCategory=")]
  [TestCase("(TestCategory=A")]
  [TestCase("TestCategory!Slow")]
  [TestCase("TestCategory=A)")]
  [TestCase("TestCategory=A|")]
  public void Parse_Malformed_IsAFormatException(string filter) => Assert.Throws<FormatException>(() => TestFilter.Parse(filter));

  [Test]
  public void InCommands_NoDotnetTest_GivesNoFilter() => Assert.That(TestFilter.InCommands("dotnet build x\necho test"), Is.Empty);

  [Test]
  public void InCommands_WithoutFilter_SelectsEverything()
    => Assert.That(TestFilter.InCommands("dotnet test Demo.slnx -c Release").Single().Selects(["Layer.rust"]), Is.True);

  [TestCase("dotnet test Demo.slnx --filter \"TestCategory=Slow\"")]
  [TestCase("dotnet test Demo.slnx --filter 'TestCategory=Slow'")]
  [TestCase("dotnet test Demo.slnx --filter TestCategory=Slow")]
  [TestCase("dotnet test Demo.slnx --filter=\"TestCategory=Slow\"")]
  [TestCase("dotnet test Demo.slnx --filter:TestCategory=Slow")]
  [TestCase("dotnet test Demo.slnx \\\n  --filter \"TestCategory=Slow\"")]
  [TestCase("dotnet test Demo.slnx \\\r\n\t--filter \"TestCategory=Slow\"")]
  public void InCommands_FilterSpellings(string run) {
    var filter = TestFilter.InCommands(run).Single();
    Assert.That((filter.Selects(["Slow"]), filter.Selects(["Fast"])), Is.EqualTo((true, false)));
  }

  [Test]
  public void InCommands_FilterWithoutValue_SelectsEverything()
    => Assert.That(TestFilter.InCommands("dotnet test --filter").Single().Selects([]), Is.True);

  [TestCase("dotnet build && dotnet test --filter \"TestCategory=Slow\"")]
  [TestCase("dotnet build; dotnet test --filter \"TestCategory=Slow\"")]
  [TestCase("echo | dotnet test --filter \"TestCategory=Slow\"")]
  [TestCase("dotnet build || dotnet test --filter \"TestCategory=Slow\"")]
  [TestCase("dotnet build\n\ndotnet test --filter \"TestCategory=Slow\"")]
  public void InCommands_SplitsCommands(string run) => Assert.That(TestFilter.InCommands(run).Single().Selects(["Slow"]), Is.True);

  [Test]
  public void InCommands_SeveralTests_GiveSeveralFilters()
    => Assert.That(TestFilter.InCommands("dotnet test --filter \"TestCategory=A\"\ndotnet test --filter \"TestCategory=B\"").Select(filter => filter.Selects(["B"])), Is.EqualTo(new[] { false, true }));

  [Test]
  public void InCommands_MalformedFilter_IsAFormatException()
    => Assert.Throws<FormatException>(() => TestFilter.InCommands("dotnet test --filter \"(TestCategory=A\""));

  [Test]
  public void InCommands_TrailingBackslash_IsPartOfTheWord()
    => Assert.That(TestFilter.InCommands("dotnet test --filter TestCategory=A\\").Single().Selects(["A\\"]), Is.True);

}
