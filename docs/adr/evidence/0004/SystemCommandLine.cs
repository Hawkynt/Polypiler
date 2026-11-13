using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Linq;

namespace Polypiler.Evidence.Adr0004;

internal static class Program {
  internal const int Success = 0;
  internal const int UsageError = 2;

  private static readonly ParserConfiguration _configuration = new() { EnablePosixBundling = false, ResponseFileTokenReplacer = null };

  internal static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "--selftest") return Checks.RunAll(Run, Console.Out);
    return Run(args, Console.Out, Console.Error);
  }

  internal static int Run(string[] args, TextWriter output, TextWriter error) {
    if (args.Length == 0 || args is ["help"]) return RootHelp(output);
    if (args[0] == "help") args = [args[1], "--help"];
    if (Grammar.HelpRequested(args, out var wanted)) {
      if (wanted == null) return RootHelp(output);
      if (Grammar.Find(wanted) == null) return Fail(error, "PLY0002", $"unknown command '{wanted}'; run 'polypiler --help'");
      var (helpRoot, _) = Build();
      return helpRoot.Parse([wanted, "--help"], _configuration).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }
    if (args is ["--version"]) {
      output.WriteLine(Grammar.Version);
      return Success;
    }

    var (root, symbols) = Build();
    var parse = root.Parse(args, _configuration);
    var cmd = Grammar.Find(parse.CommandResult.Command.Name);
    if (cmd == null) {
      var first = parse.UnmatchedTokens.FirstOrDefault();
      return first == null || IsOptionLike(first)
        ? Fail(error, "PLY0003", first == null ? "no command given" : $"unknown option '{NameOf(first)}'")
        : Fail(error, "PLY0002", $"unknown command '{first}'; run 'polypiler --help'");
    }

    // Strictness layer: what the grammar refuses and the library accepts, judged on the raw arguments.
    var end = Array.IndexOf(args, "--");
    var raw = end < 0 ? args : args[..end];
    var known = Grammar.Globals.Concat(cmd.Options).ToDictionary(o => o.Name);
    var emptyValues = new Dictionary<string, int>();
    for (var i = 0; i < raw.Length; ++i) {
      var token = raw[i];
      if (!IsOptionLike(token)) continue;
      var name = NameOf(token);
      if (!known.TryGetValue(name, out var option) || token.Length > name.Length && token[name.Length] == ':')
        return Fail(error, "PLY0003", $"unknown option '{(option == null ? name : token)}'");
      if (option.Kind == Kind.Flag) {
        if (token.Length > name.Length) return Fail(error, "PLY0003", $"option '{name}' does not take a value");
      } else if (token.Length == name.Length + 1) {
        emptyValues[name] = emptyValues.GetValueOrDefault(name) + 1;
      } else if (token.Length == name.Length) {
        if (i + 1 >= args.Length || args[i + 1] == "--" || IsOptionLike(args[i + 1]))
          return Fail(error, "PLY0003", $"option '{name}' requires a value");
        ++i;
      }
    }

    if (parse.UnmatchedTokens.Count > 0) {
      var stray = parse.UnmatchedTokens[0];
      return Fail(error, "PLY0003", IsOptionLike(stray) ? $"unknown option '{NameOf(stray)}'" : $"unexpected argument '{stray}'");
    }

    var positionals = parse.GetResult(symbols.Positionals[cmd.Name])?.Tokens.Select(t => t.Value).ToList() ?? [];
    var values = new Dictionary<string, List<string>>();
    foreach (var option in Grammar.Globals.Concat(cmd.Options)) {
      var result = parse.GetResult(symbols.Options[(option.Group == Group.Global ? "" : cmd.Name, option.Name)]);
      if (result == null || result.Implicit) continue;
      var list = new List<string>();
      if (emptyValues.TryGetValue(option.Name, out var empties)) {
        if (option.Kind == Kind.List) return Fail(error, "PLY0003", $"option '{option.Name}' has an empty list element");
        list.AddRange(Enumerable.Repeat("", empties));
      }
      if (option.Kind == Kind.Flag)
        list.AddRange(Enumerable.Repeat("", result.IdentifierTokenCount));
      else
        foreach (var token in result.Tokens) {
          if (option.Kind != Kind.List) {
            list.Add(token.Value);
            continue;
          }
          var items = Grammar.SplitList(token.Value);
          if (items == null) return Fail(error, "PLY0003", $"option '{option.Name}' has an empty list element");
          list.AddRange(items);
        }
      values[option.Name] = list;
    }

    var problem = Grammar.Validate(cmd, positionals, values);
    if (problem != null) return Fail(error, "PLY0003", problem);
    output.WriteLine(Grammar.Canonical(cmd, positionals, values));
    return Success;
  }

  private static bool IsOptionLike(string token) => token.Length > 1 && token[0] == '-';

  private static string NameOf(string token) {
    var split = token.IndexOfAny(['=', ':']);
    return split < 0 ? token : token[..split];
  }

  private sealed record Symbols(Dictionary<string, Argument<string[]>> Positionals, Dictionary<(string, string), Option> Options);

  private static (Command, Symbols) Build() {
    var symbols = new Symbols([], []);
    var root = new Command("polypiler");
    root.Options.Add(new HelpOption("--help", "-h"));
    foreach (var option in Grammar.Globals) {
      var symbol = Create(option);
      symbol.Recursive = true;
      root.Options.Add(symbol);
      symbols.Options[("", option.Name)] = symbol;
    }
    foreach (var cmd in Grammar.Commands) {
      var command = new Command(cmd.Name);
      var positionals = new Argument<string[]>(cmd.Positionals.Length == 0 ? "none" : string.Join("-and-", cmd.Positionals)) { Arity = ArgumentArity.ZeroOrMore };
      command.Arguments.Add(positionals);
      symbols.Positionals[cmd.Name] = positionals;
      foreach (var option in cmd.Options) {
        var symbol = Create(option);
        command.Options.Add(symbol);
        symbols.Options[(cmd.Name, option.Name)] = symbol;
      }
      root.Subcommands.Add(command);
    }
    return (root, symbols);
  }

  private static Option Create(Opt option) => option.Kind == Kind.Flag
    ? new Option<bool>(option.Name) { Arity = ArgumentArity.Zero }
    : new Option<string[]>(option.Name) { Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false };

  private static int RootHelp(TextWriter output) {
    output.WriteLine($"polypiler {Grammar.Version}");
    output.WriteLine("usage: polypiler <command> [options]");
    foreach (var c in Grammar.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
      output.WriteLine($"  {c.Name}");
    return Success;
  }

  private static int Fail(TextWriter error, string code, string message) {
    error.WriteLine($"polypiler: error {code}: {message}");
    return UsageError;
  }
}
