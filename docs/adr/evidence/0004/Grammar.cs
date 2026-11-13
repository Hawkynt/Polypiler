using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Evidence.Adr0004;

internal enum Kind { Flag, Single, List, Repeat }
internal enum Group { Global, Own, Pipeline, Contributed }

internal sealed record Opt(string Name, Kind Kind, Group Group, bool Required = false, string[]? Allowed = null,
  string? Default = null, bool Set = false, bool Recorded = true);

internal sealed record Cmd(string Name, string[] Positionals, Opt[] Options);

internal static class Grammar {
  internal const string Version = "0.1.0";

  internal static readonly Opt[] Globals = [
    new("--diagnostics", Kind.Single, Group.Global, Allowed: ["text", "json"], Default: "text", Recorded: false),
    new("--quiet", Kind.Flag, Group.Global, Recorded: false),
  ];

  private static readonly Opt[] _pipeline = [
    new("--opt", Kind.Single, Group.Pipeline, Allowed: ["none", "balanced", "speed", "size", "memory"], Default: "balanced"),
    new("--disable-opt", Kind.List, Group.Pipeline, Set: true),
    new("--assume", Kind.Repeat, Group.Pipeline, Set: true),
    new("--strict", Kind.Flag, Group.Pipeline),
  ];

  private static Opt Own(string name, Kind kind = Kind.Single, bool required = false, string[]? allowed = null,
    string? @default = null, bool set = false, bool recorded = true)
    => new(name, kind, Group.Own, required, allowed, @default, set, recorded);

  private static Opt Ext(string name, Kind kind = Kind.Single, bool set = false) => new(name, kind, Group.Contributed, Set: set);

  private static readonly string[] _format = ["text", "json"];

  internal static readonly Cmd[] Commands = [
    new("translate", ["input"], [
      Own("--from", required: true), Own("--source-profile"), Own("--to", required: true), Own("--target-profile"),
      Own("--export", Kind.List, set: true), Own("--verify", Kind.Flag), Own("--vectors"),
      Own("--output", required: true, recorded: false), Own("--force", Kind.Flag, recorded: false),
      .. _pipeline,
      Ext("--cpu"), Ext("--entry", Kind.List, set: true), Ext("--load-address"), Ext("--memory-map"), Ext("--verify-emulator"),
      Ext("--strategy"), Ext("--native-provider"), Ext("--target-triple"), Ext("--llvm-version"),
    ]),
    new("recover", ["image"], [
      Own("--from", required: true), Own("--to", required: true), Own("--target-profile"),
      Own("--verify", Kind.Flag), Own("--vectors"),
      Own("--output", required: true, recorded: false), Own("--force", Kind.Flag, recorded: false),
      .. _pipeline,
      Ext("--cpu"), Ext("--entry", Kind.List, set: true), Ext("--load-address"), Ext("--memory-map"), Ext("--verify-emulator"),
    ]),
    new("inspect", ["input"], [
      Own("--from", required: true), Own("--source-profile"), Own("--export", Kind.List, set: true),
      Own("--show", Kind.List, allowed: ["types", "ranges", "effects", "patterns", "ir", "proofs"], @default: "ir", set: true),
      Own("--format", allowed: _format, @default: "text"), Own("--output", recorded: false),
      .. _pipeline,
    ]),
    new("verify", ["directory"], [Own("--against"), Own("--no-update", Kind.Flag), Own("--evidence")]),
    new("capabilities", [], [
      Own("--from"), Own("--to"), Own("--source-profile"), Own("--target-profile"), Own("--all", Kind.Flag),
      Own("--format", allowed: _format, @default: "text"), Own("--report"),
    ]),
    new("search", ["query"], [
      Own("--language"), Own("--limit", @default: "20"), Own("--format", allowed: _format, @default: "text"), Own("--catalog"),
    ]),
    new("generate", ["algorithm-id"], [
      Own("--to", required: true), Own("--target-profile"), Own("--variant"), Own("--verify", Kind.Flag),
      Own("--output", required: true, recorded: false), Own("--force", Kind.Flag, recorded: false), Own("--catalog"),
      .. _pipeline,
    ]),
    new("package", ["directory"], [
      Own("--ecosystem", required: true), Own("--name"), Own("--package-version", @default: "0.1.0"),
      Own("--output", required: true, recorded: false), Own("--force", Kind.Flag, recorded: false),
      Own("--allow-unverified", Kind.Flag), Own("--no-publish", Kind.Flag), Own("--publish", Kind.Flag),
    ]),
  ];

  /// <summary>When help is requested before "--": the first token that is neither an option nor a global option's value.</summary>
  internal static bool HelpRequested(string[] args, out string? command) {
    command = null;
    var end = Array.IndexOf(args, "--");
    var head = end < 0 ? args : args[..end];
    if (!head.Any(a => a is "--help" or "-h")) return false;
    for (var i = 0; i < head.Length; ++i) {
      if (head[i].Length > 1 && head[i][0] == '-') {
        if (Globals.Any(g => g.Kind != Kind.Flag && g.Name == head[i])) ++i;
        continue;
      }
      command = head[i];
      break;
    }
    return true;
  }

  internal static Cmd? Find(string name) => Commands.FirstOrDefault(c => c.Name == name);

  /// <summary>Renders the canonical recorded command line from the parsed values.</summary>
  internal static string Canonical(Cmd cmd, IReadOnlyList<string> positionals, IReadOnlyDictionary<string, List<string>> values) {
    var parts = new List<string> { cmd.Name };
    parts.AddRange(positionals.Select(p => Quote(p.Replace('\\', '/'))));
    var recorded = cmd.Options.Where(o => o.Recorded).ToArray();
    var ordered = recorded.Where(o => o.Group == Group.Own).OrderBy(o => o.Name, StringComparer.Ordinal)
      .Concat(recorded.Where(o => o.Group == Group.Pipeline))
      .Concat(recorded.Where(o => o.Group == Group.Contributed).OrderBy(o => o.Name, StringComparer.Ordinal));
    foreach (var o in ordered) {
      values.TryGetValue(o.Name, out var given);
      switch (o.Kind) {
        case Kind.Flag:
          if (given != null) parts.Add(o.Name);
          break;
        case Kind.Single:
          var v = given?[0] ?? o.Default;
          if (v != null) { parts.Add(o.Name); parts.Add(Quote(v)); }
          break;
        case Kind.List:
          var items = given ?? (o.Default == null ? null : new List<string> { o.Default });
          if (items == null) break;
          IEnumerable<string> l = o.Set ? items.Distinct().OrderBy(x => x, StringComparer.Ordinal) : items;
          parts.Add(o.Name); parts.Add(Quote(string.Join(',', l)));
          break;
        case Kind.Repeat:
          if (given == null) break;
          foreach (var r in given.Distinct().OrderBy(x => x, StringComparer.Ordinal)) { parts.Add(o.Name); parts.Add(Quote(r)); }
          break;
      }
    }
    return string.Join(' ', parts);
  }

  internal static string Quote(string s) {
    if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "_./:=,+@%-".Contains(c))) return s;
    return "\"" + string.Concat(s.Select(c => c is '\\' or '"' or '$' or '`' ? "\\" + c : c.ToString())) + "\"";
  }

  /// <summary>Splits a list value at commas; null when an element is empty.</summary>
  internal static string[]? SplitList(string value) {
    var items = value.Split(',');
    return items.Any(i => i.Length == 0) ? null : items;
  }

  /// <summary>Checks collected values against the table; returns the first error or null.</summary>
  internal static string? Validate(Cmd cmd, IReadOnlyList<string> positionals, IReadOnlyDictionary<string, List<string>> values) {
    if (positionals.Count < cmd.Positionals.Length) return $"missing argument <{cmd.Positionals[positionals.Count]}>";
    if (positionals.Count > cmd.Positionals.Length) return $"unexpected argument '{positionals[cmd.Positionals.Length]}'";
    foreach (var o in cmd.Options.Concat(Globals)) {
      if (!values.TryGetValue(o.Name, out var given)) {
        if (o.Required) return $"option '{o.Name}' is required";
        continue;
      }
      if (o.Kind is Kind.Single or Kind.Flag && given.Count > 1) return $"option '{o.Name}' given more than once";
      if (o.Allowed != null)
        foreach (var g in given)
          if (!o.Allowed.Contains(g)) return $"'{g}' is not a valid value for '{o.Name}'; expected one of: {string.Join(", ", o.Allowed)}";
    }
    return null;
  }
}
