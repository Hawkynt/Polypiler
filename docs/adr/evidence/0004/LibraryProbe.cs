using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Linq;

var root = new Command("polypiler");
var quiet = new Option<bool>("--quiet") { Recursive = true, Arity = ArgumentArity.Zero };
root.Options.Add(quiet);
root.Options.Add(new HelpOption("--help", "-h"));

var input = new Argument<string>("input");
var from = new Option<string>("--from") { Required = true };
var output = new Option<string>("--output") { Required = true };
var force = new Option<bool>("--force") { Arity = ArgumentArity.Zero };
var opt = new Option<string>("--opt") { DefaultValueFactory = _ => "balanced" };
opt.AcceptOnlyFromAmong("none", "balanced", "speed");
var export = new Option<string[]>("--export") { AllowMultipleArgumentsPerToken = false };
var limit = new Option<int>("--limit");
var translate = new Command("translate") { input, from, output, force, opt, export, limit };
translate.SetAction(p => { Console.WriteLine("  ACTION RAN"); return 0; });
root.Subcommands.Add(translate);

var config = new ParserConfiguration { EnablePosixBundling = false, ResponseFileTokenReplacer = null };

foreach (var line in File.ReadAllLines(args[0])) {
  var argv = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
  var parse = root.Parse(argv, config);
  Console.WriteLine($"> {line}");
  Console.WriteLine($"  command={parse.CommandResult.Command.Name} action={parse.Action?.GetType().Name}");
  foreach (var e in parse.Errors)
    Console.WriteLine($"  error[{e.SymbolResult?.GetType().Name}]: {e.Message}");
  if (parse.UnmatchedTokens.Count > 0) Console.WriteLine($"  unmatched: {string.Join(" | ", parse.UnmatchedTokens)}");
  foreach (var o in new Option[] { from, output, force, opt, export, limit, quiet }) {
    var r = parse.GetResult(o);
    if (r != null) Console.WriteLine($"  {o.Name}: identifiers={r.IdentifierTokenCount} implicit={r.Implicit} tokens=[{string.Join(" | ", r.Tokens.Select(t => t.Value))}]");
  }
  var ar = parse.GetResult(input);
  if (ar != null) Console.WriteLine($"  <input>: [{string.Join(" | ", ar.Tokens.Select(t => t.Value))}]");
  using var o1 = new StringWriter();
  using var e1 = new StringWriter();
  var exit = parse.Invoke(new InvocationConfiguration { Output = o1, Error = e1 });
  Console.WriteLine($"  invoke exit={exit} out={o1.ToString().Length} chars, err: {e1.ToString().Split('\n')[0]}");
}
var rc = new RootCommand("x");
foreach (var o in rc.Options) Console.WriteLine($"RootCommand option: {o.Name} aliases=[{string.Join(", ", o.Aliases)}] {o.GetType().Name}");
foreach (var d in rc.Directives) Console.WriteLine($"RootCommand directive: {d.Name} {d.GetType().Name}");
var plain = new Command("x");
Console.WriteLine($"plain Command options: {plain.Options.Count}");
var p2 = rc.Parse(new[] { "@nofile.rsp" });
foreach (var e in p2.Errors) Console.WriteLine("default response-file handling: " + e.Message);
var help = typeof(Command).Assembly.GetType("System.CommandLine.Help.HelpBuilder");
Console.WriteLine($"HelpBuilder: {(help == null ? "absent" : help.IsPublic ? "public" : "internal")}");
Console.WriteLine($"ParserConfiguration settings: {string.Join(", ", typeof(ParserConfiguration).GetProperties().Select(p => p.Name))}");
Console.WriteLine($"ParseError members: {string.Join(", ", typeof(System.CommandLine.Parsing.ParseError).GetProperties().Select(p => p.Name))}");
