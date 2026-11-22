using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

/// <summary>
/// Parses Perl source contract lines (grammar: <c>Pkg::f.a: i32</c>, <c>seq&lt;u8&gt;</c>,
/// <c>nullable(i32)</c>, <c>record{ width: i32, pixels: seq&lt;i32&gt; }</c>, <c>context: scalar|list</c>).
/// </summary>
internal sealed class PerlContracts {

  private readonly Dictionary<string, IrType> _types = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _contexts = new(StringComparer.Ordinal);
  private readonly List<IrTypeDeclaration> _declarations = [];
  private readonly List<string> _recorded = [];

  public IReadOnlyList<string> Recorded => this._recorded;
  public IReadOnlyList<IrTypeDeclaration> Declarations => this._declarations;

  public static PerlContracts Parse(string text, Func<string, Exception> fail, Func<string, IrType?>? namedTypeResolver = null) {
    var result = new PerlContracts();
    var recordIndex = 0;
    foreach (var raw in text.Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith('#'))
        continue;

      result._recorded.Add(line);
      var colon = FindTopLevelColon(line);
      if (colon <= 0)
        throw fail($"malformed contract '{line}'");

      var key = NormalizeKey(line[..colon].Trim());
      var value = line[(colon + 1)..].Trim();
      if (key.EndsWith(".context", StringComparison.Ordinal)) {
        if (value is not ("scalar" or "list" or "void"))
          throw fail($"unknown contract context '{value}' in '{line}'");
        result._contexts[key[..^8]] = value;
        continue;
      }

      var type = result.ParseType(value, ref recordIndex, fail, namedTypeResolver);
      if (!result._types.TryAdd(key, type))
        throw fail($"duplicate contract '{key}'");
    }
    return result;
  }

  public bool TryGetType(string package, string function, string member, out IrType type) {
    var qualified = $"{package}::{function}.{member}";
    if (this._types.TryGetValue(qualified, out type!))
      return true;
    var bare = $"{function}.{member}";
    return this._types.TryGetValue(bare, out type!);
  }

  public string ContextOf(string package, string function)
    => this._contexts.TryGetValue($"{package}::{function}", out var q) ? q
      : this._contexts.TryGetValue(function, out var b) ? b
      : "scalar";

  private static readonly Dictionary<string, IrType> _primitiveTypes = new(StringComparer.Ordinal) {
    ["i8"] = IrType.I8, ["u8"] = IrType.U8, ["i16"] = IrType.I16, ["u16"] = IrType.U16,
    ["i32"] = IrType.I32, ["u32"] = IrType.U32, ["i64"] = IrType.I64, ["int"] = IrType.I64,
    ["u64"] = IrType.U64, ["f32"] = IrType.F32, ["f64"] = IrType.F64, ["num"] = IrType.F64,
    ["bool"] = IrType.Bool, ["str"] = IrType.String, ["string"] = IrType.String, ["void"] = IrType.Void
  };

  private IrType ParseType(string spec, ref int recordIndex, Func<string, Exception> fail, Func<string, IrType?>? namedTypeResolver) {
    spec = spec.Trim();
    if (_primitiveTypes.TryGetValue(spec, out var prim))
      return prim;
    if (namedTypeResolver?.Invoke(spec) is { } named)
      return named;
    return this.ParseCompoundType(spec, ref recordIndex, fail, namedTypeResolver);
  }

  private IrType ParseCompoundType(string spec, ref int recordIndex, Func<string, Exception> fail, Func<string, IrType?>? namedTypeResolver) {
    if (spec.StartsWith("seq<", StringComparison.Ordinal) && spec.EndsWith('>'))
      return IrType.Array(this.ParseType(spec[4..^1], ref recordIndex, fail, namedTypeResolver));

    if (spec.StartsWith("list<", StringComparison.Ordinal) && spec.EndsWith('>'))
      return IrType.List(this.ParseType(spec[5..^1], ref recordIndex, fail, namedTypeResolver));

    if (spec.StartsWith("nullable(", StringComparison.Ordinal) && spec.EndsWith(')')) {
      var inner = this.ParseType(spec[9..^1], ref recordIndex, fail, namedTypeResolver);
      return inner.IsOptionalValue ? IrType.Optional(inner) : inner;
    }

    if (spec.StartsWith("optional<", StringComparison.Ordinal) && spec.EndsWith('>')) {
      var inner = this.ParseType(spec[9..^1], ref recordIndex, fail, namedTypeResolver);
      return inner.IsOptionalValue ? IrType.Optional(inner) : inner;
    }

    if (spec.StartsWith("map<", StringComparison.Ordinal) && spec.EndsWith('>')) {
      var parts = SplitTopLevel(spec[4..^1], ',');
      if (parts.Count == 2)
        return IrType.Map(this.ParseType(parts[0], ref recordIndex, fail, namedTypeResolver), this.ParseType(parts[1], ref recordIndex, fail, namedTypeResolver), MapOrder.Insertion);
    }

    if (spec.StartsWith("record{", StringComparison.Ordinal) && spec.EndsWith('}')) {
      var body = spec[7..^1].Trim();
      var fields = new List<IrField>();
      foreach (var item in SplitTopLevel(body, ',')) {
        var colon = FindTopLevelColon(item);
        if (colon <= 0)
          throw fail($"malformed record field '{item}'");
        var fname = item[..colon].Trim();
        var ftype = this.ParseType(item[(colon + 1)..], ref recordIndex, fail, namedTypeResolver);
        fields.Add(new(fname, ftype, false));
      }
      var shapeKey = string.Join("|", fields.Select(f => $"{f.Name}:{f.Type}"));
      var existing = this._declarations.OfType<IrClassType>().FirstOrDefault(c =>
        string.Join("|", c.Fields.Select(f => $"{f.Name}:{f.Type}")) == shapeKey);
      if (existing is not null)
        return IrType.Object(existing);
      var cls = new IrClassType($"PerlRecord{++recordIndex}");
      cls.Define(fields);
      cls.DefineMembers([], [], [], []);
      this._declarations.Add(cls);
      return IrType.Object(cls);
    }

    throw fail($"unsupported contract type '{spec}'");
  }

  private static string NormalizeKey(string key)
    => key.StartsWith("main::", StringComparison.Ordinal) ? key[6..] : key;

  private static int DepthDelta(char c)
    => "<{(".Contains(c) ? 1 : ">})".Contains(c) ? -1 : 0;

  private static int FindTopLevelColon(string text) {
    var depth = 0;
    for (var i = 0; i < text.Length; ++i) {
      var c = text[i];
      depth += DepthDelta(c);
      if (c == ':' && depth == 0) {
        if (i + 1 < text.Length && text[i + 1] == ':') {
          ++i;
          continue;
        }
        return i;
      }
    }
    return -1;
  }

  private static List<string> SplitTopLevel(string text, char separator) {
    var parts = new List<string>();
    var depth = 0;
    var start = 0;
    for (var i = 0; i <= text.Length; ++i) {
      if (i < text.Length) {
        var c = text[i];
        depth += DepthDelta(c);
        if (c != separator || depth != 0)
          continue;
      }
      var part = text[start..i].Trim();
      if (part.Length > 0)
        parts.Add(part);
      start = i + 1;
    }
    return parts;
  }

}
