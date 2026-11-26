using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript.Lowering;

internal sealed class VBScriptContracts {

  private static readonly Dictionary<string, IrType> _primitives = new(StringComparer.OrdinalIgnoreCase) {
    ["i8"] = IrType.I8,
    ["u8"] = IrType.U8,
    ["i16"] = IrType.I16,
    ["u16"] = IrType.U16,
    ["i32"] = IrType.I32,
    ["u32"] = IrType.U32,
    ["i64"] = IrType.I64,
    ["u64"] = IrType.U64,
    ["int"] = IrType.I32,
    ["integer"] = IrType.I32,
    ["long"] = IrType.I32,
    ["f32"] = IrType.F32,
    ["f64"] = IrType.F64,
    ["single"] = IrType.F32,
    ["double"] = IrType.F64,
    ["bool"] = IrType.Bool,
    ["boolean"] = IrType.Bool,
    ["str"] = IrType.String,
    ["string"] = IrType.String,
    ["void"] = IrType.Void
  };

  private readonly Dictionary<string, IrType> _types = new(StringComparer.OrdinalIgnoreCase);

  public static VBScriptContracts Parse(
    string file,
    string? text,
    Func<string, IrType?> classResolver,
    List<Diagnostic> diagnostics) {
    var result = new VBScriptContracts();
    if (string.IsNullOrWhiteSpace(text))
      return result;

    var lines = text.Split('\n');
    for (var i = 0; i < lines.Length; ++i) {
      var line = lines[i].Trim();
      if (line.Length == 0 || line.StartsWith('\'') || line.StartsWith('#'))
        continue;

      var colon = line.IndexOf(':');
      if (colon <= 0) {
        AddError(file, i + 1, $"malformed contract '{line}'", diagnostics);
        continue;
      }

      var key = line[..colon].Trim();
      var spec = line[(colon + 1)..].Trim();
      var parsed = ParseType(spec, classResolver);
      if (parsed is null) {
        AddError(file, i + 1, $"unknown contract type '{spec}'", diagnostics);
        continue;
      }

      if (!result._types.TryAdd(key, parsed))
        AddError(file, i + 1, $"duplicate contract '{key}'", diagnostics);
    }

    return result;
  }

  public void AddDocComments(
    string file,
    string procKey,
    IReadOnlyList<string> docComments,
    int line,
    Func<string, IrType?> classResolver,
    List<Diagnostic> diagnostics) {
    foreach (var raw in docComments) {
      var trimmed = raw.Trim();
      if (trimmed.StartsWith("@return", StringComparison.OrdinalIgnoreCase)) {
        var rest = trimmed[7..].Trim();
        if (rest.StartsWith(':'))
          rest = rest[1..].Trim();
        var type = ParseType(rest, classResolver);
        if (type is null)
          AddError(file, line, $"unknown '@return' type '{rest}'", diagnostics);
        else
          this._types[$"{procKey}.return"] = type;
        continue;
      }

      if (trimmed.StartsWith("@param", StringComparison.OrdinalIgnoreCase)) {
        var rest = trimmed[6..].Trim();
        var sep = rest.IndexOf(':');
        string pName;
        string pSpec;
        if (sep > 0) {
          pName = rest[..sep].Trim();
          pSpec = rest[(sep + 1)..].Trim();
        } else {
          var space = rest.IndexOfAny([' ', '\t']);
          if (space <= 0) {
            AddError(file, line, $"malformed '@param' annotation '{trimmed}'", diagnostics);
            continue;
          }
          pName = rest[..space].Trim();
          pSpec = rest[(space + 1)..].Trim();
        }

        var type = ParseType(pSpec, classResolver);
        if (type is null)
          AddError(file, line, $"unknown '@param' type '{pSpec}' for '{pName}'", diagnostics);
        else
          this._types[$"{procKey}.{pName}"] = type;
      }
    }
  }

  public bool TryGet(string key, out IrType type) => this._types.TryGetValue(key, out type!);

  public static IrType? ParseType(string spec, Func<string, IrType?> classResolver) {
    spec = spec.Trim();
    if (spec.Length == 0)
      return null;

    if (spec.EndsWith("[]", StringComparison.Ordinal)) {
      var inner = ParseType(spec[..^2], classResolver);
      return inner is not null && inner != IrType.Void ? IrType.Array(inner) : null;
    }

    if (spec.StartsWith("seq<", StringComparison.OrdinalIgnoreCase) && spec.EndsWith('>')) {
      var inner = ParseType(spec[4..^1], classResolver);
      return inner is not null && inner != IrType.Void ? IrType.Array(inner) : null;
    }

    if (spec.EndsWith('?')) {
      var inner = ParseType(spec[..^1], classResolver);
      return inner is not null && inner.IsOptionalValue ? IrType.Optional(inner) : inner;
    }

    if (_primitives.TryGetValue(spec, out var primitive))
      return primitive;

    return classResolver(spec);
  }

  private static void AddError(string file, int line, string message, List<Diagnostic> diagnostics)
    => diagnostics.Add(Diagnostic.Create(VBScriptFrontend.InvalidContract, new(file, line, 1, line, 1), message));

}
