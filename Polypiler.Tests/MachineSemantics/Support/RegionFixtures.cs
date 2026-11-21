using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.MachineSemantics.Support;

/// <summary>The class a fixture expects for one access, written by hand in its <c># expect</c> comment.</summary>
/// <param name="Class">The class.</param>
/// <param name="Channels">The channels, in order.</param>
/// <param name="Warns">Whether the access draws <c>PLY4010</c>.</param>
internal sealed record ExpectedAccess(MemoryClass Class, ImmutableArray<string> Channels, bool Warns);

/// <summary>A MachineIR fixture of <c>tests/fixtures/native/mach-regions</c>: the module, its map, its facts and what each access expects.</summary>
/// <param name="Module">The parsed module.</param>
/// <param name="Map">The map its <c># map</c> line names, or <see langword="null"/> for <c>none</c>.</param>
/// <param name="Facts">The facts its <c># fact</c> lines give, for its one function.</param>
/// <param name="Expected">The <c># expect</c> comments, in order.</param>
internal sealed record RegionFixture(MachineModule Module, AddressMap? Map, IProvenanceFacts Facts, ImmutableArray<ExpectedAccess> Expected);

/// <summary>Reads the fixtures of the memory-region classifier.</summary>
internal static class RegionFixtures {

  /// <summary>The fixture directory.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-regions");

  private static readonly MachineProfileCatalog _catalog = new([Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "machine")]);

  /// <summary>Parses MachineIR text against the profiles of <c>spec/machine</c>.</summary>
  public static MachineModule Parse(string text) => MachineSyntax.Parse(text, _catalog.Find);

  /// <summary>Reads a memory-map file, <c>{"cpu", "regions": [{"name", "start", "end", "kind", "executable"?}]}</c>, as an <see cref="AddressMap"/> of the 32-bit test machine.</summary>
  public static AddressMap Map(string file) {
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, file)));
    static ulong Address(JsonElement element) => ulong.Parse(element.GetString()![2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    var ranges = json.RootElement.GetProperty("regions").EnumerateArray().Select(region => new AddressRange(
      region.GetProperty("name").GetString()!, Address(region.GetProperty("start")), Address(region.GetProperty("end")),
      Enum.Parse<AddressKind>(region.GetProperty("kind").GetString()!, ignoreCase: true),
      region.TryGetProperty("executable", out var executable) && executable.GetBoolean()));
    return new(ranges, 32, json.RootElement.GetProperty("cpu").GetString()!);
  }

  /// <summary>The <c>.mir</c> fixtures.</summary>
  public static IEnumerable<string> Files() => System.IO.Directory.EnumerateFiles(Directory, "*.mir").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

  /// <summary>Loads a <c>.mir</c> fixture with its comments.</summary>
  public static RegionFixture Load(string file) {
    var text = File.ReadAllText(Path.Combine(Directory, file));
    var module = Parse(text);
    var function = module.Functions.Single().Name;
    AddressMap? map = null;
    var facts = new ProvenanceFactTable();
    var expected = ImmutableArray.CreateBuilder<ExpectedAccess>();
    foreach (var line in text.Split('\n')) {
      var comment = line.IndexOf('#', StringComparison.Ordinal);
      if (comment < 0)
        continue;
      var words = line[(comment + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      switch (words.FirstOrDefault()) {
        case "map":
          map = words[1] == "none" ? null : Map(words[1]);
          break;
        case "fact":
          var value = words[1].TrimStart('%');
          facts = words[2] switch {
            "range" => facts.WithRange(function, value, new(Number(words[3]), Number(words[4]))),
            "private" => facts.WithProvenance(function, value, new PrivateAllocation(words[3])),
            "global" => facts.WithProvenance(function, value, new GlobalAllocation(words[3])),
            "caller" => facts.WithProvenance(function, value, new CallerBuffer(words[3], words[4])),
            "runtime" => facts.WithProvenance(function, value, new RuntimeStorage(words[3])),
            _ => throw new InvalidDataException($"{file}: unknown fact '{words[2]}'")
          };
          break;
        case "expect":
          expected.Add(new(Enum.Parse<MemoryClass>(words[1]), [.. words[2].Split(',')], words.Length > 3 && words[3] == "PLY4010"));
          break;
      }
    }

    return new(module, map, facts, expected.ToImmutable());
  }

  /// <summary>Parses a <c>0x</c> hexadecimal or decimal number.</summary>
  public static ulong Number(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? ulong.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
    : ulong.Parse(text, CultureInfo.InvariantCulture);

}
