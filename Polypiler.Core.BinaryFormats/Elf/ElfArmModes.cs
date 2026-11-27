using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LibObjectFile.Elf;

namespace Polypiler.Core.BinaryFormats.Elf;

/// <summary>Turns AAELF mapping symbols into nonoverlapping ranges within their sections.</summary>
internal static class ElfArmModes {

  internal static ImmutableArray<ImageModeRange> Ranges(IEnumerable<ElfSymbol> symbols) {
    var groups = symbols.Where(symbol => symbol.SectionLink.Section is { } section && !section.Flags.HasFlag(ElfSectionFlags.Tls))
      .Select(symbol => (Symbol: symbol, Mode: Kind(symbol.Name.Value)))
      .Where(mapping => mapping.Mode is not null)
      .GroupBy(mapping => mapping.Symbol.SectionLink.Section!);
    var ranges = ImmutableArray.CreateBuilder<ImageModeRange>();
    foreach (var group in groups) {
      var section = group.Key;
      var sorted = group.OrderBy(mapping => mapping.Symbol.Value).ToArray();
      for (var index = 0; index < sorted.Length; ++index) {
        var start = sorted[index].Symbol.Value;
        if (start < section.VirtualAddress || start - section.VirtualAddress > section.Size)
          throw new ElfCorruption($"ARM mapping symbol at {BinaryDiagnostics.Hex(start)} is outside section '{section.Name.Value}'");
        var remaining = section.Size - (start - section.VirtualAddress);
        var length = index + 1 < sorted.Length ? sorted[index + 1].Symbol.Value - start : remaining;
        if (length > remaining)
          throw new ElfCorruption($"ARM mapping range at {BinaryDiagnostics.Hex(start)} extends outside section '{section.Name.Value}'");
        if (length > 0)
          ranges.Add(new ImageModeRange(start, length, sorted[index].Mode!, section.Name.Value));
      }
    }
    return ranges.ToImmutable();
  }

  private static string? Kind(string name) {
    var prefix = name.AsSpan();
    if (prefix.Length < 2 || prefix[0] != '$' || (prefix.Length > 2 && prefix[2] != '.'))
      return null;
    return prefix[1] switch {
      'a' => "arm",
      't' => "thumb",
      'd' => "data",
      'x' => "a64",
      _ => null
    };
  }

}
