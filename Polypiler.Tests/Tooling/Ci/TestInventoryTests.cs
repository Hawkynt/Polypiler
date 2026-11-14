using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The tests of a repository, read from built assemblies by their metadata or from a listing.</summary>
[TestFixture]
public sealed class TestInventoryTests {

  /// <summary>Test classes of every shape NUnit runs, and some it does not.</summary>
  private const string Source = """
    using System;
    using NUnit.Framework;

    [assembly: Category("Acceptance")]

    namespace Demo;

    public sealed class LayerAttribute(string name) : CategoryAttribute("Layer." + name);

    public sealed class Fast : CategoryAttribute { }

    public sealed class QuickAttribute : CategoryAttribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequiresToolchainAttribute(string key) : Attribute { public string Key { get; } = key; }

    public sealed class MyTestAttribute : TestAttribute { }

    [Category("Base")]
    public abstract class BaseTests {
      [Test] public void Inherited() { }
    }

    [Layer("core"), RequiresToolchain("node")]
    public sealed class Translations : BaseTests {
      [Test, Category("Slow"), RequiresToolchain("rust")] public void Translates() { }
      [TestCase(1)] public void Cases(int value) { }
      [TestCaseSource(nameof(Values))] public void Sourced(int value) { }
      [Theory] public void Theory(int value) { }
      [MyTest, Fast, Quick] public void Custom() { }
      public void NotATest() { }
      public static int[] Values => [1];
    }

    public static class StaticTests {
      [Test] public static void Static() { }
    }

    public class Generic<T> {
      [Test] public void Open() { }
    }
    """;

  private string _folder = null!;

  [SetUp]
  public void CreateFolder() => this._folder = Path.Combine(Path.GetTempPath(), "polypiler-inventory-" + Guid.NewGuid().ToString("N"));

  [TearDown]
  public void DeleteFolder() {
    if (Directory.Exists(this._folder))
      Directory.Delete(this._folder, true);
  }

  private string Compile() {
    Directory.CreateDirectory(this._folder);
    var nunit = typeof(TestAttribute).Assembly.Location;
    File.Copy(nunit, Path.Combine(this._folder, Path.GetFileName(nunit)));
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
      .Append(nunit)
      .Select(path => MetadataReference.CreateFromFile(path));
    var compilation = CSharpCompilation.Create("Demo.Tests", [CSharpSyntaxTree.ParseText(Source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var path = Path.Combine(this._folder, "Demo.Tests.dll");
    var result = compilation.Emit(path);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), Is.Empty);
    return path;
  }

  [Test]
  public void FromAssemblies_ReadsTestsCategoriesAndToolchains() {
    var tests = TestInventory.FromAssemblies([this.Compile()]).ToDictionary(test => test.FullName);
    Assert.That(tests.Keys, Is.EqualTo(new[] {
      "Demo.StaticTests.Static", "Demo.Translations.Cases", "Demo.Translations.Custom", "Demo.Translations.Inherited",
      "Demo.Translations.Sourced", "Demo.Translations.Theory", "Demo.Translations.Translates"
    }));
    Assert.That(tests["Demo.Translations.Translates"].Categories, Is.EqualTo(new[] { "Slow", "Layer.core", "Base", "Acceptance" }));
    Assert.That(tests["Demo.Translations.Translates"].Toolchains, Is.EqualTo(new[] { "rust", "node" }));
    Assert.That(tests["Demo.Translations.Custom"].Categories, Is.EqualTo(new[] { "Fast", "Quick", "Layer.core", "Base", "Acceptance" }));
    Assert.That(tests["Demo.StaticTests.Static"].Categories, Is.EqualTo(new[] { "Acceptance" }));
    Assert.That(tests.Values.All(test => test.IsAcceptance), Is.True);
    Assert.That(tests["Demo.Translations.Translates"].Layers, Is.EqualTo(new[] { "Layer.core" }));
  }

  [Test]
  public void Load_WithoutListing_ReadsTheReleaseBuildOfEveryTestProject() {
    var assembly = this.Compile();
    var project = Path.Combine(this._folder, "Demo.Tests");
    Directory.CreateDirectory(Path.Combine(project, "bin", "Release", "net10.0"));
    File.WriteAllText(Path.Combine(project, "Demo.Tests.csproj"), "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
    Directory.CreateDirectory(Path.Combine(this._folder, "Demo"));
    File.WriteAllText(Path.Combine(this._folder, "Demo", "Demo.csproj"), "<Project />");
    foreach (var file in Directory.GetFiles(this._folder, "*.dll"))
      File.Copy(file, Path.Combine(project, "bin", "Release", "net10.0", Path.GetFileName(file)));
    File.Delete(assembly);
    Assert.That(TestInventory.Assemblies(this._folder), Is.EqualTo(new[] { Path.Combine(project, "bin", "Release", "net10.0", "Demo.Tests.dll") }));
    Assert.That(TestInventory.Load(this._folder), Has.Count.EqualTo(7));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Assemblies_TestProjectNotBuilt_IsAnEnvironmentError(bool withBin) {
    var project = Path.Combine(this._folder, "Demo.Tests");
    Directory.CreateDirectory(withBin ? Path.Combine(project, "bin", "Release", "net10.0") : project);
    File.WriteAllText(Path.Combine(project, "Demo.Tests.csproj"), "<Project><PropertyGroup><IsTestProject> true </IsTestProject></PropertyGroup></Project>");
    var exception = Assert.Throws<CiEnvironmentException>(() => TestInventory.Assemblies(this._folder))!;
    Assert.That(exception.Diagnostic.Message, Is.EqualTo("'Demo.Tests/bin/Release/<framework>/Demo.Tests.dll' cannot be read: the test project is not built; run 'dotnet build -c Release' first"));
  }

  [Test]
  public void Load_Listing_IsSortedAndKnowsTheAcceptanceTests() {
    Directory.CreateDirectory(this._folder);
    File.WriteAllText(Path.Combine(this._folder, TestInventory.ListingFile), "tests:\n  - { name: B, categories: [Acceptance, Layer.core], toolchains: [node] }\n  - { name: A, categories: [] }\n");
    var tests = TestInventory.Load(this._folder);
    Assert.That(tests.Select(test => (test.FullName, test.IsAcceptance, test.Toolchains.Count)), Is.EqualTo(new[] { ("A", false, 0), ("B", true, 1) }));
  }

  [TestCase("tests: [", "not valid YAML")]
  [TestCase("other: []\n", "expected a list 'tests'")]
  [TestCase("tests:\n  - 1\n", "every test needs")]
  [TestCase("tests:\n  - { categories: [] }\n", "every test needs")]
  [TestCase("tests:\n  - { name: A }\n", "every test needs")]
  [TestCase("tests:\n  - { name: A, categories: [1] }\n", "every test needs")]
  [TestCase("tests:\n  - { name: A, categories: [], toolchains: x }\n", "every test needs")]
  public void FromListing_Invalid_IsAnEnvironmentError(string text, string reason) {
    var exception = Assert.Throws<CiEnvironmentException>(() => TestInventory.FromListing(text, "test-listing.yaml"))!;
    Assert.That(exception.Diagnostic.Message, Does.StartWith("'test-listing.yaml' cannot be read: " + reason));
  }

}
