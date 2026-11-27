using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// The cycles each primitive costs on one CPU, from a <c>spec/permutations/costs/*.yaml</c> document
/// (<c>spec.costs</c>: <c>{primitive, cycles}</c>). A primitive without an entry is unavailable there:
/// asking for its cost is <c>PLY0811</c>.
/// </summary>
public sealed class PrimitiveCostTable {

  private readonly Dictionary<string, long> _cycles;

  /// <summary>Creates a table.</summary>
  /// <param name="name">The table's name, such as <c>bitperm-modern</c>.</param>
  /// <param name="cycles">The cycles of each available primitive.</param>
  public PrimitiveCostTable(string name, IReadOnlyDictionary<string, long> cycles) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(cycles);
    this.Name = name;
    this._cycles = new(cycles, StringComparer.Ordinal);
  }

  /// <summary>The table's name: its document's file name without the extension.</summary>
  public string Name { get; }

  /// <summary>The primitives the table has entries for.</summary>
  public IReadOnlyCollection<string> Primitives => this._cycles.Keys;

  /// <summary>Loads a cost document.</summary>
  /// <param name="path">The document.</param>
  /// <returns>The table, named after the file.</returns>
  /// <exception cref="FileNotFoundException">There is no such file.</exception>
  /// <exception cref="InvalidDataException">The document has no list of costs, or an entry without a primitive or a whole number of cycles.</exception>
  public static PrimitiveCostTable Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    using var reader = File.OpenText(path);
    var stream = new YamlStream();
    stream.Load(reader);
    var name = Path.GetFileNameWithoutExtension(path);
    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
      || !root.Children.TryGetValue(new YamlScalarNode("spec"), out var spec) || spec is not YamlMappingNode body
      || !body.Children.TryGetValue(new YamlScalarNode("costs"), out var costs) || costs is not YamlSequenceNode entries)
      throw new InvalidDataException($"cost table '{name}' has no list spec.costs");

    var cycles = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var entry in entries) {
      if (entry is not YamlMappingNode fields || Scalar(fields, "primitive") is not { } primitive
        || !long.TryParse(Scalar(fields, "cycles"), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        throw new InvalidDataException($"cost table '{name}': an entry needs a primitive and a whole number of cycles");

      cycles[primitive] = count;
    }

    return new PrimitiveCostTable(name, cycles);
  }

  /// <summary>Whether the table has an entry for a primitive.</summary>
  /// <param name="primitive">The primitive.</param>
  /// <returns><see langword="true"/> when it is available.</returns>
  public bool Has(string primitive) => this._cycles.ContainsKey(primitive);

  /// <summary>The cycles of a primitive.</summary>
  /// <param name="primitive">The primitive.</param>
  /// <returns>The cycles.</returns>
  /// <exception cref="PermutationModelException">PLY0811: the table has no entry for it.</exception>
  public long Cycles(string primitive) => this._cycles.TryGetValue(primitive, out var cycles)
    ? cycles
    : throw new PermutationModelException(Diagnostic.Create(NetworkDiagnostics.Unavailable, null, primitive, this.Name));

  private static string? Scalar(YamlMappingNode node, string key)
    => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

}
