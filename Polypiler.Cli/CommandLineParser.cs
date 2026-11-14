using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>How an option takes its value (ADR 0004, "Kinds"), read from its value type.</summary>
internal enum OptionKind {

  /// <summary>An <see cref="Option{T}"/> of <see cref="bool"/>: no value.</summary>
  Flag,

  /// <summary>Any other option but a list: one value, once.</summary>
  Single,

  /// <summary>An <see cref="Option{T}"/> of <see cref="T:string[]"/>: a comma list, repeatable.</summary>
  List

}

/// <summary>A command line the grammar accepted: the command, the library's parse of it and the two global options.</summary>
/// <param name="Command">The command to run.</param>
/// <param name="Parse">The parse, without errors.</param>
/// <param name="Format">The value of <c>--diagnostics</c>.</param>
/// <param name="Quiet">Whether <c>--quiet</c> was given.</param>
internal sealed record ParsedCommandLine(ICliCommand Command, ParseResult Parse, DiagnosticsFormat Format, bool Quiet);

/// <summary>
/// The parser of ADR 0004. Every decision is taken on the raw arguments: the command, every option
/// with its kind, value and count, the operands, the required and the exclusive options. Only a
/// command line that passed is handed to System.CommandLine, with the settings of the ADR, for the
/// typed values the command reads; no message of the library is ever shown.
/// </summary>
internal sealed class CommandLineParser {

  /// <summary>The name of the global option that selects the format of the diagnostics.</summary>
  internal const string DiagnosticsName = "--diagnostics";

  /// <summary>The name of the global option that drops infos and warnings.</summary>
  internal const string QuietName = "--quiet";

  private static readonly ParserConfiguration _configuration = new() { EnablePosixBundling = false, ResponseFileTokenReplacer = null };

  private readonly CommandRegistry _registry;

  /// <summary>Creates a parser for the commands of a registry.</summary>
  internal CommandLineParser(CommandRegistry registry) => this._registry = registry;

  /// <summary>The two global options, new for every command tree: <c>--diagnostics text|json</c> and <c>--quiet</c>.</summary>
  internal static Option[] Globals() => [
    OptionRules.AcceptOnly(new Option<string>(DiagnosticsName) { Description = "the format of the diagnostics on standard error", HelpName = "format", DefaultValueFactory = _ => "text", Recursive = true }, "text", "json"),
    new Option<bool>(QuietName) { Description = "leave infos and warnings off standard error", Recursive = true }
  ];

  /// <summary>The kind of an option.</summary>
  internal static OptionKind KindOf(Option option)
    => option.ValueType == typeof(bool) ? OptionKind.Flag : option.ValueType == typeof(string[]) ? OptionKind.List : OptionKind.Single;

  /// <summary>Whether an argument is an option by rule 1 of the grammar: two or more characters, the first a <c>-</c>.</summary>
  internal static bool IsOptionLike(string argument) => argument.Length > 1 && argument[0] == '-';

  /// <summary>
  /// The command a request for help names: the first argument before the first <c>--</c> that is
  /// neither an option nor the separate value of <c>--diagnostics</c>; <see langword="null"/> when
  /// there is none.
  /// </summary>
  internal static string? CommandWord(IReadOnlyList<string> arguments) {
    for (var index = 0; index < arguments.Count && arguments[index] != "--"; ++index) {
      var argument = arguments[index];
      if (argument == DiagnosticsName)
        ++index;
      else if (!IsOptionLike(argument))
        return argument;
    }

    return null;
  }

  /// <summary>Whether <c>--help</c> or <c>-h</c> stands before the first <c>--</c>.</summary>
  internal static bool AsksForHelp(IReadOnlyList<string> arguments)
    => arguments.TakeWhile(argument => argument != "--").Any(argument => argument is "--help" or "-h");

  /// <summary>Whether the arguments select JSON diagnostics: <c>--diagnostics json</c> or <c>--diagnostics=json</c> before the first <c>--</c>.</summary>
  internal static DiagnosticsFormat RequestedFormat(IReadOnlyList<string> arguments) {
    for (var index = 0; index < arguments.Count && arguments[index] != "--"; ++index)
      if (arguments[index] == DiagnosticsName + "=json" || (arguments[index] == DiagnosticsName && index + 1 < arguments.Count && arguments[index + 1] == "json"))
        return DiagnosticsFormat.Json;

    return DiagnosticsFormat.Text;
  }

  /// <summary>
  /// The tree of one command under the plain root command <c>polypiler</c>, with the help option
  /// <c>--help</c>/<c>-h</c> and the global options; every option normalised to its kind.
  /// </summary>
  /// <param name="command">The registered command.</param>
  /// <returns>The root and the command under it.</returns>
  /// <exception cref="InvalidOperationException">The command declares an option a global option or another of its options already has.</exception>
  internal static (Command Root, Command Command) Build(ICliCommand command) {
    var root = new Command(ProductInfo.Name);
    root.Options.Add(new HelpOption("--help", "-h"));
    foreach (var global in Globals())
      root.Options.Add(global);

    var symbol = new Command(command.Name, command.Description);
    command.Configure(symbol);
    var names = new HashSet<string>(root.Options.Select(option => option.Name), StringComparer.Ordinal);
    foreach (var option in symbol.Options) {
      if (!names.Add(option.Name))
        throw new InvalidOperationException($"the command '{command.Name}' declares the option '{option.Name}' twice or as a global option");

      Normalize(option);
    }

    root.Subcommands.Add(symbol);
    return (root, symbol);
  }

  /// <summary>Gives an option the arity of its kind; a list splits its values at commas.</summary>
  private static void Normalize(Option option) {
    switch (KindOf(option)) {
      case OptionKind.Flag:
        option.Arity = ArgumentArity.Zero;
        break;
      case OptionKind.List:
        option.Arity = ArgumentArity.OneOrMore;
        option.AllowMultipleArgumentsPerToken = false;
        ((Option<string[]>)option).CustomParser = result => [.. result.Tokens.SelectMany(token => token.Value.Split(','))];
        break;
      default:
        option.Arity = ArgumentArity.ExactlyOne;
        break;
    }
  }

  /// <summary>Parses a command line that asks for no help and is not <c>--version</c>.</summary>
  /// <param name="arguments">The arguments, without the program name.</param>
  /// <param name="refusal">The one diagnostic of a refused command line.</param>
  /// <returns>The accepted command line, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
  internal ParsedCommandLine? Parse(IReadOnlyList<string> arguments, out Diagnostic? refusal) {
    refusal = this.Scan(arguments, out var command, out var root);
    if (refusal is not null)
      return null;

    var parse = root!.Parse([.. Attached(arguments)], _configuration);
    if (parse.Errors.Count > 0) {
      refusal = Diagnostic.Create(DiagnosticDescriptor.InternalError, null, "System.CommandLine refused a command line the grammar accepts");
      return null;
    }

    var format = parse.GetValue<string>(DiagnosticsName) == "json" ? DiagnosticsFormat.Json : DiagnosticsFormat.Text;
    return new ParsedCommandLine(command!, parse, format, parse.GetValue<bool>(QuietName));
  }

  /// <summary>The arguments with every <c>--name=</c> before the first <c>--</c> written as <c>--name ""</c>, which the library takes as the empty value.</summary>
  private static IEnumerable<string> Attached(IReadOnlyList<string> arguments) {
    var options = true;
    foreach (var argument in arguments) {
      options &= argument != "--";
      if (options && IsOptionLike(argument) && argument.IndexOf('=') == argument.Length - 1) {
        yield return argument[..^1];
        yield return "";
      } else {
        yield return argument;
      }
    }
  }

  /// <summary>Checks the arguments against the grammar, in the order they stand; the first violation is the refusal.</summary>
  private Diagnostic? Scan(IReadOnlyList<string> arguments, out ICliCommand? command, out Command? root) {
    command = null;
    root = null;
    Command? symbol = null;
    var known = Globals().ToDictionary(option => option.Name, StringComparer.Ordinal);
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    long operands = 0, maximum = 0;
    var rest = false;
    for (var index = 0; index < arguments.Count; ++index) {
      var argument = arguments[index];
      if (!rest && argument == "--") {
        if (symbol is null)
          return Grammar(CliDiagnostics.NoCommand);

        rest = true;
        continue;
      }

      if (!rest && IsOptionLike(argument)) {
        var equals = argument.IndexOf('=');
        var name = equals < 0 ? argument : argument[..equals];
        if (!known.TryGetValue(name, out var option))
          return Grammar(CliDiagnostics.UnknownOption, name);

        var kind = KindOf(option);
        string? value = null;
        if (kind == OptionKind.Flag) {
          if (equals >= 0)
            return Grammar(CliDiagnostics.TakesNoValue, name);
        } else if (equals >= 0) {
          value = argument[(equals + 1)..];
        } else if (index + 1 == arguments.Count || arguments[index + 1] == "--" || IsOptionLike(arguments[index + 1])) {
          return Grammar(CliDiagnostics.RequiresValue, name);
        } else {
          value = arguments[++index];
        }

        counts[name] = counts.GetValueOrDefault(name) + 1;
        if (kind != OptionKind.List && counts[name] > 1)
          return Grammar(CliDiagnostics.GivenTwice, name);

        if (value is not null && ValueProblem(option, name, kind, value) is { } problem)
          return problem;

        continue;
      }

      if (symbol is null) {
        command = this._registry.Find(argument);
        if (command is null)
          return Diagnostic.Create(CliDiagnostics.UnknownCommand, null, argument);

        (root, symbol) = Build(command);
        foreach (var option in symbol.Options)
          known.Add(option.Name, option);

        maximum = symbol.Arguments.Sum(operand => (long)operand.Arity.MaximumNumberOfValues);
        continue;
      }

      if (++operands > maximum)
        return Grammar(CliDiagnostics.UnexpectedArgument, argument);
    }

    if (symbol is null)
      return Grammar(CliDiagnostics.NoCommand);

    long minimum = 0;
    foreach (var operand in symbol.Arguments)
      if ((minimum += operand.Arity.MinimumNumberOfValues) > operands)
        return Grammar(CliDiagnostics.MissingArgument, operand.Name);

    foreach (var option in symbol.Options)
      if (option.Required && !counts.ContainsKey(option.Name))
        return Grammar(CliDiagnostics.Required, option.Name);

    foreach (var (first, second) in OptionRules.ExclusivePairs(symbol))
      if (counts.ContainsKey(first.Name) && counts.ContainsKey(second.Name))
        return Diagnostic.Create(CliDiagnostics.ExclusiveOptions, null, first.Name, second.Name);

    return null;
  }

  /// <summary>What is wrong with the value of an option: an empty list item, or a value outside the allowed set.</summary>
  private static Diagnostic? ValueProblem(Option option, string name, OptionKind kind, string value) {
    var items = kind == OptionKind.List ? value.Split(',') : [value];
    if (kind == OptionKind.List && items.Any(item => item.Length == 0))
      return Grammar(CliDiagnostics.EmptyListElement, name);

    if (OptionRules.Allowed(option) is not { } allowed)
      return null;

    var outside = items.FirstOrDefault(item => !allowed.Contains(item, StringComparer.Ordinal));
    return outside is null ? null : Grammar(CliDiagnostics.NotAllowed, name, outside, string.Join(", ", allowed));
  }

  /// <summary>A <c>PLY0003</c> refusal with one of the texts of <see cref="CliDiagnostics"/>.</summary>
  internal static Diagnostic Grammar(string message, params object[] arguments)
    => Diagnostic.Create(CliDiagnostics.CommandLine, null, string.Format(CultureInfo.InvariantCulture, message, arguments));

}
