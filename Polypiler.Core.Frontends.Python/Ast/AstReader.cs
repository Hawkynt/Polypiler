using System.Text.Json;

namespace Polypiler.Core.Frontends.Python.Ast;

/// <summary>What the helper printed for a source: the tree, or why there is none.</summary>
/// <param name="Module">The <c>Module</c> node, or <see langword="null"/> when the source was refused.</param>
/// <param name="Python">The version of the interpreter that parsed it, such as <c>3.13.1</c>; empty when unknown.</param>
/// <param name="Error">The refusal: python's exception class and message, and the position.</param>
internal sealed record ParsedSource(PyNode? Module, string Python, (string Message, int Line, int Column)? Error);

/// <summary>Reads the JSON of the helper's wire format into the node model.</summary>
internal static class AstReader {

  /// <summary>The version of the wire format this reader understands.</summary>
  public const int Format = 1;

  /// <summary>Reads one printed dump.</summary>
  /// <param name="json">The text the helper wrote.</param>
  /// <returns>The tree, or the error the dump reports, or a mismatch of the format.</returns>
  public static ParsedSource Read(string json) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(json);
    } catch (JsonException exception) {
      return new(null, "", ($"the Python helper wrote no tree: {exception.Message}", 1, 1));
    }

    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != Format)
      return new(null, "", ($"the Python helper speaks another wire format than {Format}", 1, 1));

    var python = root.TryGetProperty("python", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString()! : "";
    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) {
      var node = new PyNode(error);
      return new(null, python, ($"{node.Text("type")}: {node.Text("msg")}", node.Line, node.Column));
    }

    return root.TryGetProperty("module", out var module) && module.ValueKind == JsonValueKind.Object
      ? new(new PyNode(module), python, null)
      : new(null, python, ("the Python helper wrote neither a tree nor an error", 1, 1));
  }

}
