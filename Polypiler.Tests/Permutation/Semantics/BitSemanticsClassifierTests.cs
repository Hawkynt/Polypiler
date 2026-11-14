using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// Classification of mappings (issue #267, test design 1, 2, 4 and 5): the class and the explanation of
/// every mapping in <c>tests/fixtures/permutation/hierarchy/mappings.json</c>, worked out by hand, and
/// the class of all 44 BitPerm fixtures against the hand-made list in <c>bitperm-classes.json</c>.
/// </summary>
[TestFixture]
public sealed class BitSemanticsClassifierTests {

  private static readonly string _hierarchy = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "permutation", "hierarchy");
  private static readonly string _bitPerm = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm", "mappings");

  /// <summary>The cases of <c>mappings.json</c>: mapping text, class, explanation.</summary>
  public static IEnumerable<TestCaseData> Cases() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_hierarchy, "mappings.json")));
    foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray()) {
      var mapping = item.GetProperty("mapping").GetString()!;
      yield return new TestCaseData(mapping, Enum.Parse<BitSemanticClass>(item.GetProperty("class").GetString()!), item.GetProperty("explanation").GetString()!)
        .SetName($"Classify({mapping})");
    }
  }

  [TestCaseSource(nameof(Cases))]
  public void Mapping_HasItsHandWorkedClassAndExplanation(string text, BitSemanticClass expected, string explanation) {
    var mapping = BitMapping.Parse(text);
    Assert.Multiple(() => {
      Assert.That(BitSemanticsClassifier.Classify(mapping), Is.EqualTo(expected));
      Assert.That(BitSemanticsClassifier.Explain(mapping), Is.EqualTo(explanation));
      Assert.That(BitMapping.Parse(text), Is.EqualTo(mapping), "classification changes nothing");
    });
  }

  /// <summary>The H1 reason of every non-permutation case is the reason <see cref="BitPermutation.TryFrom"/> reports.</summary>
  [TestCaseSource(nameof(Cases))]
  public void TryFrom_ReportsTheExplanationOfEveryRouting(string text, BitSemanticClass expected, string explanation) {
    var diagnostics = new DiagnosticBag();
    var accepted = BitPermutation.TryFrom(BitMapping.Parse(text), diagnostics, out var permutation);
    Assert.Multiple(() => {
      Assert.That(accepted, Is.EqualTo(expected == BitSemanticClass.Permutation));
      Assert.That(permutation is not null, Is.EqualTo(accepted));
      Assert.That(diagnostics.Items.Select(item => $"{item.Code}: {item.Message}"),
        Is.EqualTo(accepted ? Array.Empty<string>() : [$"PLY0805: mapping is declared BitPermutation but {explanation}"]));
    });
  }

  /// <summary>Test design 4: the 16 permutations among the 44 fixtures are exactly those listed by hand, and the class agrees with <see cref="BitMapping.IsBijective"/>.</summary>
  [Test]
  public void BitPermFixtures_ClassifyAsTheHandMadeListSays() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_hierarchy, "bitperm-classes.json")));
    var listed = document.RootElement.GetProperty("permutations").EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal);
    var classes = Directory.GetFiles(_bitPerm, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => {
      using var stream = File.OpenRead(path);
      var mapping = BitPermState.Read(stream);
      var kind = BitSemanticsClassifier.Classify(mapping);
      Assert.That(kind, Is.EqualTo(mapping.IsBijective ? BitSemanticClass.Permutation : BitSemanticClass.Routing), path);
      return kind;
    });
    TestContext.Out.WriteLine(string.Join(Environment.NewLine, classes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}")));
    Assert.Multiple(() => {
      Assert.That(classes, Has.Count.EqualTo(44));
      Assert.That(listed, Has.Count.EqualTo(16));
      Assert.That(classes.Where(pair => pair.Value == BitSemanticClass.Permutation).Select(pair => pair.Key), Is.EquivalentTo(listed));
    });
  }

  [Test]
  public void Classifier_RefusesNull() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => BitSemanticsClassifier.Classify((BitMapping)null!));
      Assert.Throws<ArgumentNullException>(() => BitSemanticsClassifier.Classify((BitBooleanTransform)null!));
      Assert.Throws<ArgumentNullException>(() => BitSemanticsClassifier.Explain((BitMapping)null!));
      Assert.Throws<ArgumentNullException>(() => BitSemanticsClassifier.Explain((BitBooleanTransform)null!));
    });
  }

  /// <summary>PRD §20.2: a network is not a semantic object; only the three classes implement the interface.</summary>
  [Test]
  public void OnlyTheThreeClasses_AreSemantics() {
    var implementers = typeof(IBitSemantics).Assembly.GetTypes().Where(type => type != typeof(IBitSemantics) && typeof(IBitSemantics).IsAssignableFrom(type));
    Assert.That(implementers, Is.EquivalentTo(new[] { typeof(BitPermutation), typeof(BitRouting), typeof(BitBooleanTransform) }));
  }

  /// <summary>The issue forbids an inverse on a routing; only a permutation may ever get one.</summary>
  [Test]
  public void Routing_HasNoInverse() {
    Assert.That(typeof(BitRouting).GetMembers().Select(member => member.Name), Has.None.Contains("Invers"));
  }

}
