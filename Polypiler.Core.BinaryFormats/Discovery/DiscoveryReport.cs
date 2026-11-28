using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polypiler.Core.MachineSemantics;

namespace Polypiler.Core.BinaryFormats.Discovery;

/// <summary>
/// A discovery result as canonical JSON, the form its fixtures state by hand: the functions by entry
/// with their confidence, evidence, completeness and blocks, and the byte map as ranges per class
/// (undecoded bytes are the rest). Addresses are written as <see cref="MachineSyntax.Number"/> writes them.
/// </summary>
public static class DiscoveryReport {

  /// <summary>The result as JSON.</summary>
  /// <param name="result">The result.</param>
  /// <returns>The JSON object.</returns>
  public static JsonObject Write(DiscoveryResult result) {
    ArgumentNullException.ThrowIfNull(result);
    var functions = new JsonArray();
    foreach (var function in result.Functions) {
      var blocks = new JsonArray();
      foreach (var block in function.Blocks) {
        var written = new JsonObject {
          ["start"] = MachineSyntax.Number(block.Start),
          ["end"] = MachineSyntax.Number(block.End),
          ["successors"] = new JsonArray([.. block.Successors.Select(successor => (JsonNode)MachineSyntax.Number(successor))])
        };
        if (block.Unresolved)
          written["unresolved"] = true;
        if (block.TailCall is { } tail)
          written["tailCall"] = MachineSyntax.Number(tail);
        if (!block.ExternalTargets.IsEmpty)
          written["external"] = new JsonArray([.. block.ExternalTargets.Select(target => (JsonNode)MachineSyntax.Number(target))]);
        if (block.Invalid is { } invalid)
          written["invalid"] = MachineSyntax.Number(invalid);
        blocks.Add(written);
      }

      var entry = new JsonObject {
        ["entry"] = MachineSyntax.Number(function.Entry),
        ["confidence"] = function.Confidence.ToString(),
        ["evidence"] = function.Evidence,
        ["complete"] = function.Complete,
        ["blocks"] = blocks
      };
      if (function.Name is { } name)
        entry["name"] = name;
      functions.Add(entry);
    }

    var bytes = new JsonObject();
    foreach (var kind in new[] { ByteClass.Code, ByteClass.Data, ByteClass.Conflict })
      bytes[kind.ToString().ToLowerInvariant()] = new JsonArray([.. Ranges(result.Bytes.Classified.Where(pair => pair.Value == kind).Select(pair => pair.Key)).Select(range => (JsonNode)range)]);

    return new JsonObject {
      ["functions"] = functions,
      ["bytes"] = bytes,
      ["diagnostics"] = new JsonArray([.. result.Diagnostics.Select(diagnostic => (JsonNode)diagnostic.Code)])
    };
  }

  /// <summary>The result as indented JSON text.</summary>
  /// <param name="result">The result.</param>
  public static string Text(DiscoveryResult result) => Write(result).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

  /// <summary>Ascending addresses as ranges <c>first..last</c>, a single address as itself.</summary>
  private static IEnumerable<string> Ranges(IEnumerable<ulong> addresses) {
    ulong? first = null;
    ulong last = 0;
    foreach (var address in addresses) {
      if (first is not null && address == last + 1) {
        last = address;
        continue;
      }

      if (first is { } open)
        yield return Range(open, last);
      (first, last) = (address, address);
    }

    if (first is { } final)
      yield return Range(final, last);
  }

  private static string Range(ulong first, ulong last) => first == last ? MachineSyntax.Number(first) : $"{MachineSyntax.Number(first)}..{MachineSyntax.Number(last)}";

}
