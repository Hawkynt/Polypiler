using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Permutation.Model;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Corpus;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-model.feature</c>. A mapping text is parsed by the
/// model in process. The route maps BitPerm derives are computed by <c>tooling/bitperm/routemap.mjs</c>
/// in a real <c>node</c>, from the pinned page that the real <c>polypiler-tooling corpus import</c>
/// puts into the scenario's workspace.
/// </summary>
[Binding, Scope(Feature = "Pure bit-mapping model")]
public sealed partial class PermModelSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Repository = "Hawkynt/Hawkynt.github.io";
  private const string Pin = "b3a8a8e92cbca83475986391c3cac56f0b185d29";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _text = "";
  private BitMapping? _mapping;
  private PermutationModelException? _refusal;
  private string _directory = "";
  private string[] _files = [];
  private readonly Dictionary<string, BitMapping> _loaded = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Exception> _failed = new(StringComparer.Ordinal);
  private Dictionary<string, int[]>? _derived;

  private static string RepositoryFile(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>Remembers a mapping text.</summary>
  [Given("^the mapping text " + StepText.Quoted + "$")]
  public void MappingText(string text) => this._text = text;

  /// <summary>Parses the text, keeping the mapping or the refusal.</summary>
  [When("^it is parsed$")]
  public void Parse() {
    try {
      this._mapping = BitMapping.Parse(this._text);
    } catch (PermutationModelException exception) {
      this._refusal = exception;
    }
  }

  private BitMapping Mapping => this._mapping ?? throw new AssertionException($"'{this._text}' was refused: {this._refusal?.Message}");

  /// <summary>Checks a list of facts, <c>Name is value</c> joined by commas and a final <c>and</c>; a value is a Boolean, a decimal or a hexadecimal number.</summary>
  [Then(@"^(\w+ is (?:true|false|0x[0-9A-Fa-f]+|[0-9]+)(?:(?:, | and )\w+ is (?:true|false|0x[0-9A-Fa-f]+|[0-9]+))*)$")]
  public void Facts(string facts) {
    var mapping = this.Mapping;
    Assert.Multiple(() => {
      foreach (Match fact in Fact().Matches(facts)) {
        var (name, value) = (fact.Groups["name"].Value, fact.Groups["value"].Value);
        object actual = name switch {
          nameof(BitMapping.IsSquare) => mapping.IsSquare,
          nameof(BitMapping.IsInjective) => mapping.IsInjective,
          nameof(BitMapping.IsBijective) => mapping.IsBijective,
          nameof(BitMapping.HasDuplication) => mapping.HasDuplication,
          nameof(BitMapping.HasInversion) => mapping.HasInversion,
          nameof(BitMapping.HasConstants) => mapping.HasConstants,
          nameof(BitMapping.HasDontCares) => mapping.HasDontCares,
          nameof(BitMapping.InputWidth) => new BigInteger(mapping.InputWidth),
          nameof(BitMapping.OutputWidth) => new BigInteger(mapping.OutputWidth),
          nameof(BitMapping.RequiredOutputMask) => mapping.RequiredOutputMask,
          nameof(BitMapping.InvertMask) => mapping.InvertMask,
          nameof(BitMapping.OnesMask) => mapping.OnesMask,
          nameof(BitMapping.ZerosMask) => mapping.ZerosMask,
          nameof(BitMapping.UsedInputMask) => mapping.UsedInputMask,
          nameof(BitMapping.DroppedInputMask) => mapping.DroppedInputMask,
          _ => throw new AssertionException($"unknown fact '{name}'")
        };
        object expected = value switch {
          "true" => true,
          "false" => false,
          _ when value.StartsWith("0x", StringComparison.Ordinal) => BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
          _ => BigInteger.Parse(value, CultureInfo.InvariantCulture)
        };
        Assert.That(actual, Is.EqualTo(expected), name);
      }
    });
  }

  [GeneratedRegex(@"(?<name>\w+) is (?<value>\w+)")]
  private static partial Regex Fact();

  /// <summary>Counts the readers of one input bit.</summary>
  [Then(@"^UseCount of input bit (\d+) is (\d+)$")]
  public void UseCount(int bit, int count) => Assert.That(this.Mapping.UseCount(bit), Is.EqualTo(count));

  /// <summary>Compares with a factory of a given width.</summary>
  [Then(@"^it equals BitMapping\.(Identity|Reverse|SwapHalves)\((\d+)\)$")]
  public void EqualsFactory(string factory, int width) {
    var expected = factory switch {
      "Identity" => BitMapping.Identity(width),
      "Reverse" => BitMapping.Reverse(width),
      _ => BitMapping.SwapHalves(width)
    };
    Assert.That(this.Mapping, Is.EqualTo(expected));
  }

  /// <summary>Checks the refusal as <c>CODE: message</c>.</summary>
  [Then("^the diagnostic is " + StepText.Quoted + "$")]
  public void Diagnostic(string text) {
    Assert.That(this._refusal, Is.Not.Null, $"'{this._text}' was accepted as {this._mapping}");
    Assert.That($"{this._refusal!.Diagnostic.Code}: {this._refusal.Diagnostic.Message}", Is.EqualTo(text));
  }

  /// <summary>Checks that the text gave no mapping.</summary>
  [Then("^no mapping is produced$")]
  public void NoMapping() => Assert.That(this._mapping, Is.Null);

  /// <summary>Checks that BitPerm's view is refused.</summary>
  [Then("^ToRouteMap throws InvalidOperationException$")]
  public void RouteMapThrows() {
    var mapping = this.Mapping;
    Assert.Throws<InvalidOperationException>(() => mapping.ToRouteMap());
  }

  /// <summary>Lists the mapping files of a repository folder and checks their number.</summary>
  [Given("^the " + @"(\d+)" + " files in " + StepText.Quoted + "$")]
  public void Files(int count, string directory) {
    this._directory = RepositoryFile(directory);
    this._files = [.. Directory.GetFiles(this._directory, "*.json").Order(StringComparer.Ordinal)];
    Assert.That(this._files, Has.Length.EqualTo(count));
  }

  /// <summary>Reads every file, keeping each mapping or its failure.</summary>
  [When("^each is read with BitPermState.Read$")]
  public void ReadEach() {
    foreach (var file in this._files) {
      var name = Path.GetFileNameWithoutExtension(file);
      try {
        using var stream = File.OpenRead(file);
        this._loaded[name] = BitPermState.Read(stream);
      } catch (PermutationModelException exception) {
        this._failed[name] = exception;
      }
    }
  }

  /// <summary>Checks that every file loaded.</summary>
  [Then("^each loads without a diagnostic$")]
  public void EachLoads() {
    Assert.That(this._failed.Select(failure => $"{failure.Key}: {failure.Value.Message}"), Is.Empty);
    Assert.That(this._loaded, Has.Count.EqualTo(this._files.Length));
  }

  /// <summary>Derives the route maps with the page's own code and compares each with the model's.</summary>
  [Then("^ToRouteMap of each equals the route map BitPerm derives from the same file under node$")]
  public void RouteMapsAgree() {
    var derived = this.Derived();
    Assert.That(derived.Keys.Order(StringComparer.Ordinal), Is.EqualTo(this._loaded.Keys.Order(StringComparer.Ordinal)));
    var differences = this._loaded.Where(loaded => !loaded.Value.ToRouteMap().SequenceEqual(derived[loaded.Key])).Select(loaded => loaded.Key);
    Assert.That(differences, Is.Empty);
  }

  /// <summary>Compares the captured route maps the unit tests use with the ones the page derives.</summary>
  [Then("^" + StepText.Quoted + " holds the same route maps$")]
  public void CapturedAgree(string path) {
    using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(path)));
    var captured = document.RootElement.GetProperty("routeMaps").EnumerateObject().ToDictionary(map => map.Name, map => map.Value.EnumerateArray().Select(index => index.GetInt32()).ToArray());
    var derived = this.Derived();
    Assert.That(captured.Keys.Order(StringComparer.Ordinal), Is.EqualTo(derived.Keys.Order(StringComparer.Ordinal)));
    Assert.That(captured.Where(map => !map.Value.SequenceEqual(derived[map.Key])).Select(map => map.Key), Is.Empty);
  }

  /// <summary>Runs <c>tooling/bitperm/routemap.mjs</c> on the pinned page once per scenario.</summary>
  private Dictionary<string, int[]> Derived() {
    if (this._derived is not null)
      return this._derived;

    var result = this.Node(RepositoryFile("tooling/bitperm/routemap.mjs"), "--page", this.Page(), "--mappings", this._directory);
    Assert.That(result.ExitCode, Is.Zero, result.Stderr);
    using var document = JsonDocument.Parse(result.Stdout);
    this._derived = document.RootElement.GetProperty("routeMaps").EnumerateObject()
      .ToDictionary(map => map.Name, map => map.Value.EnumerateArray().Select(index => index.GetInt32()).ToArray(), StringComparer.Ordinal);
    return this._derived;
  }

  /// <summary>Imports the page into the workspace with the repository's manifest and lock.</summary>
  private string Page() {
    workspace.Copy(RepositoryFile(CorpusManifest.RepositoryPath), CorpusManifest.RepositoryPath);
    workspace.Copy(RepositoryFile(CorpusLock.RepositoryPath), CorpusLock.RepositoryPath);
    var import = Toolchain.Tooling(workspace.Path, "corpus", "import", "--fixture", "bitperm.page", "--source", CorpusSteps.Checkout);
    Assert.That(import.ExitCode, Is.Zero, import.Output);
    var page = CorpusImporter.FilePath(workspace.Path, Repository, Pin, "BitPerm/index.html");
    Assert.That(File.Exists(page), Is.True, page);
    return page;
  }

  /// <summary>Runs <c>node</c> in the workspace and logs what it did.</summary>
  private CommandResult Node(params string[] arguments) {
    var start = new ProcessStartInfo("node") {
      WorkingDirectory = workspace.Path,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    try {
      using var process = Process.Start(start)!;
      var error = process.StandardError.ReadToEndAsync();
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return log.Add(new CommandResult(process.ExitCode, output, error.Result));
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to derive BitPerm's route maps but could not be started: {exception.Message}");
      throw;
    }
  }

}
