using System;
using System.Collections.Generic;
using System.IO;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// An obligation as a text file (<c>*.obligation</c>): one <c>key: value</c> per line, blank lines
/// and lines starting with <c>#</c> ignored.
/// <code>
/// rule: rotate-from-shifts
/// inputs: x: u&lt;w&gt;, n: u&lt;w&gt;
/// precondition: %p1 = UGe.u&lt;w&gt; %n, 1; %p2 = ULe.u&lt;w&gt; %n, 7; %p = And.bool %p1, %p2
/// before: %a = Shl.u&lt;w&gt;[strict] %x, %n; %s = Sub.u&lt;w&gt; 8, %n; %b = LShr.u&lt;w&gt;[strict] %x, %s; %r = Or.u&lt;w&gt; %a, %b
/// after: %r = RotateLeft.u&lt;w&gt; %x, %n
/// result: r
/// </code>
/// <c>rule</c>, <c>before</c> and <c>after</c> are required; <c>inputs</c> may be empty, a
/// <c>precondition</c> may be given any number of times, and <c>result</c> defaults to <c>r</c>.
/// </summary>
public static class ObligationFile {

  /// <summary>The extension of an obligation file.</summary>
  public const string Extension = ".obligation";

  /// <summary>Reads an obligation file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The obligation.</returns>
  /// <exception cref="InvalidDataException">The text is not an obligation; the message names the line.</exception>
  /// <exception cref="ArgumentException">The obligation's fragments do not fit together (<see cref="ProofObligation"/>).</exception>
  public static ProofObligation Load(string path) => Parse(File.ReadAllText(path));

  /// <summary>Reads the text of an obligation file.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The obligation.</returns>
  /// <exception cref="InvalidDataException">The text is not an obligation; the message names the line.</exception>
  /// <exception cref="ArgumentException">The obligation's fragments do not fit together (<see cref="ProofObligation"/>).</exception>
  public static ProofObligation Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var preconditions = new List<string>();
    var lines = text.Split('\n');
    for (var index = 0; index < lines.Length; ++index) {
      var line = lines[index].Trim();
      if (line.Length == 0 || line.StartsWith('#'))
        continue;

      var colon = line.IndexOf(':', StringComparison.Ordinal);
      if (colon < 0)
        throw new InvalidDataException($"line {index + 1}: '{line}' is no 'key: value'");

      var key = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      if (key == "precondition")
        preconditions.Add(value);
      else if (key is not ("rule" or "inputs" or "before" or "after" or "result"))
        throw new InvalidDataException($"line {index + 1}: unknown key '{key}'");
      else if (!values.TryAdd(key, value))
        throw new InvalidDataException($"line {index + 1}: '{key}' is given twice");
    }

    string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"the obligation has no '{key}'");
    return new(Required("rule"), Required("before"), Required("after"), Inputs(values.GetValueOrDefault("inputs", "")), preconditions, values.GetValueOrDefault("result", "r"));
  }

  /// <summary>Reads a list of inputs: <c>name: type</c>, separated by commas.</summary>
  /// <param name="text">The list, such as <c>x: u&lt;w&gt;, n: u&lt;w&gt;</c>; empty for none.</param>
  /// <returns>The inputs, in order.</returns>
  /// <exception cref="InvalidDataException">An entry is no <c>name: type</c>.</exception>
  public static IReadOnlyList<ProofInput> Inputs(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var inputs = new List<ProofInput>();
    foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var parts = entry.Split(':', StringSplitOptions.TrimEntries);
      if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        throw new InvalidDataException($"'{entry}' is no input 'name: type'");

      inputs.Add(new(parts[0], parts[1]));
    }

    return inputs;
  }

}
