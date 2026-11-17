using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using Polypiler.Cli.Options;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The help texts of <c>polypiler</c>, written by the foundation rather than by the library's help
/// builder: the root help lists the commands, the help of a command its operands and options.
/// </summary>
internal static class CliHelp {

  /// <summary>
  /// The root help: <c>polypiler &lt;version&gt;</c>, the usage line, then one line per registered
  /// command, <c>  &lt;name&gt;  &lt;description&gt;</c>, the names padded to one width.
  /// </summary>
  internal static void Root(TextWriter output, CommandRegistry registry) {
    output.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");
    output.WriteLine($"usage: {ProductInfo.Name} <command> [options]");
    var commands = registry.Commands;
    var width = commands.Count == 0 ? 0 : commands.Max(command => command.Name.Length);
    foreach (var command in commands)
      output.WriteLine($"  {command.Name.PadRight(width)}  {command.Description}");
  }

  /// <summary>
  /// The help of one command: its usage, its description, its operands, its own options, the options
  /// contributed to it under <c>Options from &lt;owner&gt;:</c>, one group per owner in owner order, each
  /// in name order (ordinal), and the global options.
  /// </summary>
  internal static void Command(TextWriter output, ICliCommand command, OptionContributions contributions) {
    var (root, symbol) = CommandLineParser.Build(command, contributions);
    var groups = contributions.HelpGroups(command.Name);
    var contributed = groups.SelectMany(group => group.Names).ToHashSet(StringComparer.Ordinal);
    var operands = string.Concat(symbol.Arguments.Select(operand => " " + Operand(operand)));
    output.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");
    output.WriteLine($"usage: {ProductInfo.Name} {command.Name}{operands} [options]");
    output.WriteLine(command.Description);
    Section(output, "arguments", symbol.Arguments.Select(operand => (Operand(operand), operand.Description)));
    Section(output, "options", symbol.Options.Where(option => !contributed.Contains(option.Name)).Select(option => (Syntax(option), Described(option))));
    foreach (var (owner, names) in groups)
      Section(output, $"Options from {owner}", names.Select(name => symbol.Options.First(option => option.Name == name)).Select(option => (Syntax(option), Described(option))));
    Section(output, "global options", root.Options.Select(option => (option.Name == "--help" ? "-h, --help" : Syntax(option), option.Name == "--help" ? "show this help" : option.Description)));
  }

  /// <summary>An operand as the usage line writes it: <c>&lt;name&gt;</c>, in brackets when it may be left out, with <c>...</c> when it may repeat.</summary>
  private static string Operand(Argument operand) {
    var text = $"<{operand.Name}>" + (operand.Arity.MaximumNumberOfValues > 1 ? "..." : "");
    return operand.Arity.MinimumNumberOfValues == 0 ? $"[{text}]" : text;
  }

  /// <summary>An option as it is written: the name, and for a value its allowed values or its help name; a list with its repetition.</summary>
  private static string Syntax(Option option) {
    var kind = CommandLineParser.KindOf(option);
    if (kind == OptionKind.Flag)
      return option.Name;

    var value = OptionRules.Allowed(option) is { } allowed ? string.Join('|', allowed) : option.HelpName ?? "value";
    return kind == OptionKind.List ? $"{option.Name} <{value}>[,<{value}>...]" : $"{option.Name} <{value}>";
  }

  private static string? Described(Option option) => option.Required ? $"{option.Description} (required)".TrimStart() : option.Description;

  /// <summary>A titled list of rows, the first column padded to one width; nothing for no rows.</summary>
  private static void Section(TextWriter output, string title, IEnumerable<(string Syntax, string? Description)> rows) {
    var list = rows.ToList();
    if (list.Count == 0)
      return;

    output.WriteLine();
    output.WriteLine(title + ":");
    var width = list.Max(row => row.Syntax.Length);
    foreach (var (syntax, description) in list)
      output.WriteLine(string.IsNullOrEmpty(description) ? $"  {syntax}" : $"  {syntax.PadRight(width)}  {description}");
  }

}
