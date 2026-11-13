using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Evidence.Adr0004;

internal static class Program {
  internal const int Success = 0;
  internal const int UsageError = 2;

  internal static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "--selftest") return Checks.RunAll(Run, Console.Out);
    return Run(args, Console.Out, Console.Error);
  }

  internal static int Run(string[] args, TextWriter output, TextWriter error) {
    if (args.Length == 0 || args is ["help"]) return RootHelp(output);
    if (args[0] == "help") args = [args[1], "--help"];
    if (Grammar.HelpRequested(args, out var wanted)) {
      if (wanted == null) return RootHelp(output);
      var described = Grammar.Find(wanted);
      return described == null ? Fail(error, "PLY0002", $"unknown command '{wanted}'; run 'polypiler --help'") : CommandHelp(described, output);
    }
    if (args is ["--version"]) {
      output.WriteLine(Grammar.Version);
      return Success;
    }

    Cmd? cmd = null;
    var positionals = new List<string>();
    var values = new Dictionary<string, List<string>>();
    var literal = false;
    for (var i = 0; i < args.Length; ++i) {
      var token = args[i];
      if (!literal && token == "--") {
        literal = true;
        continue;
      }
      if (!literal && token.Length > 1 && token[0] == '-') {
        var split = token.IndexOf('=');
        var name = split < 0 ? token : token[..split];
        var attached = split < 0 ? null : token[(split + 1)..];
        var option = Grammar.Globals.Concat(cmd?.Options ?? []).FirstOrDefault(o => o.Name == name);
        if (option == null) return Fail(error, "PLY0003", $"unknown option '{name}'");
        if (!values.TryGetValue(name, out var list)) values[name] = list = [];
        if (option.Kind == Kind.Flag) {
          if (attached != null) return Fail(error, "PLY0003", $"option '{name}' does not take a value");
          list.Add("");
          continue;
        }
        if (attached == null) {
          if (i + 1 >= args.Length || args[i + 1] == "--" || args[i + 1].Length > 1 && args[i + 1][0] == '-')
            return Fail(error, "PLY0003", $"option '{name}' requires a value");
          attached = args[++i];
        }
        if (option.Kind == Kind.List) {
          var items = Grammar.SplitList(attached);
          if (items == null) return Fail(error, "PLY0003", $"option '{name}' has an empty list element");
          list.AddRange(items);
        } else
          list.Add(attached);
        continue;
      }
      if (cmd == null) {
        cmd = Grammar.Find(token);
        if (cmd == null) return Fail(error, "PLY0002", $"unknown command '{token}'; run 'polypiler --help'");
      } else
        positionals.Add(token);
    }

    if (cmd == null) return Fail(error, "PLY0003", "no command given");
    var problem = Grammar.Validate(cmd, positionals, values);
    if (problem != null) return Fail(error, "PLY0003", problem);
    output.WriteLine(Grammar.Canonical(cmd, positionals, values));
    return Success;
  }

  private static int RootHelp(TextWriter output) {
    output.WriteLine($"polypiler {Grammar.Version}");
    output.WriteLine("usage: polypiler <command> [options]");
    foreach (var c in Grammar.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
      output.WriteLine($"  {c.Name}");
    return Success;
  }

  private static int CommandHelp(Cmd cmd, TextWriter output) {
    output.WriteLine($"usage: polypiler {cmd.Name} {string.Join(' ', cmd.Positionals.Select(p => $"<{p}>"))} [options]");
    foreach (var o in cmd.Options)
      output.WriteLine($"  {o.Name}{(o.Kind == Kind.Flag ? "" : " <value>")}");
    return Success;
  }

  private static int Fail(TextWriter error, string code, string message) {
    error.WriteLine($"polypiler: error {code}: {message}");
    return UsageError;
  }
}
