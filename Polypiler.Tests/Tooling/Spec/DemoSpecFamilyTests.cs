using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary><see cref="DemoSpecFamily"/>, the worked example of a family: its rule and its references, run through the whole engine.</summary>
[TestFixture]
public sealed class DemoSpecFamilyTests {

  private static string[] Lines(string body, params string[] extraDocuments) {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document(("spec", body)));
    foreach (var id in extraDocuments)
      repository.Document($"demo/{id}.yaml", SpecText.Document(("id", $"DEMO.{id}")));
    return repository.Lines();
  }

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<DemoSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("demo", "spec/schemas/demo.schema.json")));
  }

  [Test]
  public void EmptyPartList_IsValid() => Assert.That(Lines("{parts: []}"), Is.Empty);

  [Test]
  public void PartNamedTwice_IsReportedAtTheSecond()
    => Assert.That(Lines("\n  parts:\n    - name: gear\n    - name: spring\n    - name: gear"), Is.EqualTo(new[] { "demo/widget.yaml(16,13): error PLY0046: part 'gear' is defined twice (rule: unique-part)" }));

  [Test]
  public void UsesOfAnExistingDocument_IsValid() => Assert.That(Lines("{parts: [{name: gear, uses: DEMO.spring}]}", "spring"), Is.Empty);

  [Test]
  public void UsesOfAMissingDocument_IsPLY0043AtTheValue()
    => Assert.That(Lines("\n  parts:\n    - name: gear\n      uses: DEMO.spring"), Is.EqualTo(new[] { "demo/widget.yaml(15,13): error PLY0043: unknown spec id 'DEMO.spring'" }));

  [Test]
  public void PartWithoutName_IsASchemaViolation()
    => Assert.That(Lines("{parts: [{uses: DEMO.widget}]}").Single(), Does.StartWith("demo/widget.yaml(12,16): error PLY0040: /spec/parts/0: Required properties"));

  [Test]
  public void Rule_FormatsReasonAndRule() {
    var document = SpecDocument.Parse("demo/widget.yaml", SpecText.Document());
    Assert.That(DiagnosticRenderer.ToText(SpecDiagnostics.Rule(document, "/title", "too plain", "style")), Is.EqualTo("demo/widget.yaml(2,8): error PLY0046: too plain (rule: style)"));
  }

}
