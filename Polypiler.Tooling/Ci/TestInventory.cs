using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Ci;

/// <summary>One test method and what the audit needs of it.</summary>
/// <param name="FullName">The namespace, the class and the method, dot-separated.</param>
/// <param name="Categories">Its NUnit categories, its own and those of its class, the classes it derives from and its assembly.</param>
/// <param name="Toolchains">The keys of its <c>RequiresToolchain</c> attributes, its own and its class's.</param>
/// <param name="IsAcceptance">Whether it belongs to the acceptance project, whose assembly is in the category <see cref="AcceptanceCategory"/>.</param>
public sealed record TestEntry(string FullName, IReadOnlyList<string> Categories, IReadOnlyList<string> Toolchains, bool IsAcceptance) {

  /// <summary>The category of the acceptance project's assembly.</summary>
  public const string AcceptanceCategory = "Acceptance";

  /// <summary>The prefix of the category that names a test's layer.</summary>
  public const string LayerPrefix = "Layer.";

  /// <summary>The <c>Layer.&lt;name&gt;</c> categories of the test.</summary>
  public IReadOnlyList<string> Layers => [.. this.Categories.Where(category => category.StartsWith(LayerPrefix, StringComparison.Ordinal))];

}

/// <summary>
/// The tests of a repository, read by reflection from the built test assemblies without running any
/// code of them, or, for a tree that has no assemblies, from a listing.
/// </summary>
public static partial class TestInventory {

  /// <summary>
  /// The listing a tree may hold instead of built assemblies, at its root: <c>tests</c>, a list of
  /// <c>{name, categories, toolchains?}</c>, where a test of the category <c>Acceptance</c> stands for
  /// one of the acceptance project. The audit fixtures use it.
  /// </summary>
  public const string ListingFile = "test-listing.yaml";

  private static readonly HashSet<string> _testAttributes = new(StringComparer.Ordinal) {
    "NUnit.Framework.TestAttribute", "NUnit.Framework.TestCaseAttribute", "NUnit.Framework.TestCaseSourceAttribute", "NUnit.Framework.TheoryAttribute"
  };

  private const string CategoryAttribute = "NUnit.Framework.CategoryAttribute";

  /// <summary>The tests of a repository: those of its listing if it has one, else those of the Release builds of its test projects.</summary>
  /// <param name="root">The repository root.</param>
  /// <returns>The tests, sorted by full name.</returns>
  /// <exception cref="CiEnvironmentException">The listing is not valid, or a test project has no Release build (<c>PLY0004</c>).</exception>
  public static IReadOnlyList<TestEntry> Load(string root) {
    ArgumentNullException.ThrowIfNull(root);
    var listing = Path.Combine(root, ListingFile);
    return File.Exists(listing) ? FromListing(File.ReadAllText(listing), ListingFile) : FromAssemblies(Assemblies(root));
  }

  /// <summary>
  /// The Release builds of the test projects of a repository: for every <c>*/*.csproj</c> that sets
  /// <c>IsTestProject</c>, <c>bin/Release/&lt;framework&gt;/&lt;project&gt;.dll</c>.
  /// </summary>
  /// <param name="root">The repository root.</param>
  /// <returns>The assemblies, sorted.</returns>
  /// <exception cref="CiEnvironmentException">A test project has no Release build (<c>PLY0004</c>).</exception>
  public static IReadOnlyList<string> Assemblies(string root) {
    ArgumentNullException.ThrowIfNull(root);
    var assemblies = new List<string>();
    foreach (var project in Directory.GetDirectories(root).SelectMany(directory => Directory.GetFiles(directory, "*.csproj")).Order(StringComparer.Ordinal)) {
      if (!TestProject().IsMatch(File.ReadAllText(project)))
        continue;

      var name = Path.GetFileNameWithoutExtension(project);
      var bin = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release");
      var built = Directory.Exists(bin) ? Directory.GetDirectories(bin).Select(framework => Path.Combine(framework, name + ".dll")).Where(File.Exists).Order(StringComparer.Ordinal).ToArray() : [];
      if (built.Length == 0)
        throw new CiEnvironmentException(CiDiagnostics.Unreadable($"{Path.GetFileName(Path.GetDirectoryName(project))}/bin/Release/<framework>/{name}.dll", "the test project is not built; run 'dotnet build -c Release' first"));

      assemblies.Add(built[^1]);
    }

    return assemblies;
  }

  /// <summary>Reads the tests of assemblies by their metadata.</summary>
  /// <param name="paths">The assemblies; each one's folder must hold what it references.</param>
  /// <returns>The tests, sorted by full name.</returns>
  public static IReadOnlyList<TestEntry> FromAssemblies(IEnumerable<string> paths) {
    ArgumentNullException.ThrowIfNull(paths);
    var tests = new List<TestEntry>();
    foreach (var path in paths) {
      var references = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, "*.dll")
        .Concat(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First());
      using var context = new MetadataLoadContext(new PathAssemblyResolver(references));
      var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
      var assemblyCategories = Categories(assembly.GetCustomAttributesData()).ToList();
      var acceptance = assemblyCategories.Contains(TestEntry.AcceptanceCategory);
      foreach (var type in assembly.GetTypes().Where(type => type is { IsClass: true } && (!type.IsAbstract || type.IsSealed) && !type.IsGenericTypeDefinition))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy).Where(IsTest)) {
          var lineage = Lineage(type).ToList();
          var categories = Categories(method.GetCustomAttributesData()).Concat(lineage.SelectMany(ancestor => Categories(ancestor.GetCustomAttributesData()))).Concat(assemblyCategories);
          var toolchains = Toolchains(method.GetCustomAttributesData()).Concat(lineage.SelectMany(ancestor => Toolchains(ancestor.GetCustomAttributesData())));
          tests.Add(new($"{type.FullName}.{method.Name}", [.. categories.Distinct(StringComparer.Ordinal)], [.. toolchains.Distinct(StringComparer.Ordinal)], acceptance));
        }
    }

    return [.. tests.DistinctBy(test => test.FullName).OrderBy(test => test.FullName, StringComparer.Ordinal)];
  }

  /// <summary>Reads a listing.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name messages give the file.</param>
  /// <returns>The tests, sorted by full name.</returns>
  /// <exception cref="CiEnvironmentException">The text is not a valid listing (<c>PLY0004</c>).</exception>
  public static IReadOnlyList<TestEntry> FromListing(string text, string file) {
    ArgumentNullException.ThrowIfNull(text);
    JsonNode? document;
    try {
      document = YamlToJson.Read(text, out _);
    } catch (YamlToJsonException exception) {
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(file, exception.Message));
    }

    if (document is not JsonObject root || root["tests"] is not JsonArray entries)
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(file, "expected a list 'tests'"));

    var tests = new List<TestEntry>();
    foreach (var entry in entries) {
      if (entry is not JsonObject test || Text(test["name"]) is not { } name || Texts(test["categories"]) is not { } categories
          || (test.ContainsKey("toolchains") ? Texts(test["toolchains"]) : []) is not { } toolchains)
        throw new CiEnvironmentException(CiDiagnostics.Unreadable(file, "every test needs a 'name' and a list 'categories', and may have a list 'toolchains'"));

      tests.Add(new(name, categories, toolchains, categories.Contains(TestEntry.AcceptanceCategory)));
    }

    return [.. tests.OrderBy(test => test.FullName, StringComparer.Ordinal)];
  }

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  private static string[]? Texts(JsonNode? node) {
    if (node is not JsonArray array)
      return null;

    var texts = array.Select(Text).OfType<string>().ToArray();
    return texts.Length == array.Count ? texts : null;
  }

  private static IEnumerable<Type> Lineage(Type type) {
    for (var current = type; current is not null && current.FullName != "System.Object"; current = current.BaseType)
      yield return current;
  }

  private static bool IsTest(MethodInfo method) => method.GetCustomAttributesData().Any(attribute => Lineage(attribute.AttributeType).Any(type => _testAttributes.Contains(type.FullName!)));

  /// <summary>
  /// The categories attributes give: a <c>CategoryAttribute</c> its argument; one derived from it its
  /// <c>Layer.</c>-prefixed argument when it is a <c>LayerAttribute</c>, its argument when it passes one,
  /// and its name without <c>Attribute</c> when it passes none, as NUnit does.
  /// </summary>
  private static IEnumerable<string> Categories(IEnumerable<CustomAttributeData> attributes) {
    foreach (var attribute in attributes) {
      var type = attribute.AttributeType;
      if (!Lineage(type).Any(ancestor => ancestor.FullName == CategoryAttribute))
        continue;

      var argument = attribute.ConstructorArguments is [{ Value: string text }] ? text : null;
      if (type.Name == "LayerAttribute" && argument is not null)
        yield return TestEntry.LayerPrefix + argument;
      else
        yield return argument ?? (type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name[..^"Attribute".Length] : type.Name);
    }
  }

  private static IEnumerable<string> Toolchains(IEnumerable<CustomAttributeData> attributes)
    => attributes.Where(attribute => attribute.AttributeType.Name == "RequiresToolchainAttribute" && attribute.ConstructorArguments is [{ Value: string }])
      .Select(attribute => (string)attribute.ConstructorArguments[0].Value!);

  [GeneratedRegex(@"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase)]
  private static partial Regex TestProject();

}
