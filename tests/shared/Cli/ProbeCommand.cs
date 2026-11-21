using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Threading;
using Polypiler.Cli;
using Polypiler.Cli.Options;
using Polypiler.Core;

namespace Polypiler.Tests.Cli.Support;

/// <summary>
/// <c>probe &lt;input&gt;</c> with its own <c>--opt &lt;level&gt;</c> (default <c>balanced</c>),
/// <c>--from</c> and <c>--to</c>: the test-only command of the option contributions (issue #178). It
/// accepts after <c>--from</c> and <c>--to</c> only an identifier contributed for that direction,
/// keeps the bag its contributors read and its canonical arguments, and prints <c>probe &lt;input&gt;</c>.
/// </summary>
public sealed class ProbeCommand : ICliCommand {

  /// <summary>The refusal of an identifier nobody contributed, with the code and text PRD §10 reserves for it; test-only.</summary>
  public static readonly DiagnosticDescriptor UnknownLanguage = new("PLY0006", DiagnosticSeverity.Error, ExitCode.UsageError, "unknown language '{0}'", "test-only");

  private int _executions;

  /// <summary>The contributions whose identifiers <c>--from</c> and <c>--to</c> accept.</summary>
  public OptionContributions? Contributions { get; set; }

  /// <summary>How often the command ran.</summary>
  public int Executions => Volatile.Read(ref this._executions);

  /// <summary>The bag of the last run.</summary>
  public OptionBag? LastBag { get; private set; }

  /// <summary>The options of the last run as the canonical command line writes them: <c>--opt</c>, then the contributed ones.</summary>
  public IReadOnlyList<string> LastCanonical { get; private set; } = [];

  /// <inheritdoc/>
  public string Name => "probe";

  /// <inheritdoc/>
  public string Description => "show what the contributed options read";

  /// <inheritdoc/>
  public void Configure(Command command) {
    command.Arguments.Add(new Argument<string>("input") { Description = "the input" });
    command.Options.Add(new Option<string>("--opt") { Description = "the optimization level", HelpName = "level", DefaultValueFactory = _ => "balanced" });
    command.Options.Add(CommonOptions.From);
    command.Options.Add(CommonOptions.To);
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    Interlocked.Increment(ref this._executions);
    foreach (var (option, direction) in new[] { ("--from", IdentifierDirection.From), ("--to", IdentifierDirection.To) })
      if (parse.GetValue<string>(option) is { } identifier && this.Contributions?.IsKnownIdentifier(identifier, direction) != true) {
        context.Report(Diagnostic.Create(UnknownLanguage, null, identifier));
        return 0;
      }

    this.LastBag = context.Contributed;
    this.LastCanonical = ["--opt", parse.GetValue<string>("--opt")!, .. context.ContributedArguments];
    context.Output.WriteLine("probe " + parse.GetValue<string>("input"));
    return 0;
  }

  /// <summary>A registry of <c>probe</c>, <c>echo</c> and <c>translate</c>, with the contributors registered in the order given.</summary>
  public static (CommandRegistry Registry, OptionContributions Contributions, ProbeCommand Probe) Setup(params object[] contributors) {
    var probe = new ProbeCommand();
    var registry = new CommandRegistry();
    registry.Add(probe);
    registry.Add(new EchoCommand([]));
    registry.Add(new TranslateCommand());
    var contributions = new OptionContributions(registry);
    foreach (var contributor in contributors)
      if (contributor is ICommandOptionContributor options)
        contributions.Add(options);
      else
        contributions.Add((IIdentifierContributor)contributor);

    probe.Contributions = contributions;
    return (registry, contributions, probe);
  }

}

/// <summary>One option of a <see cref="DemoContributor"/>: its declaration and, for a single value, its default.</summary>
/// <param name="Declaration">The declaration.</param>
/// <param name="Default">The default of a single-valued option, or <see langword="null"/>.</param>
public sealed record DemoOption(ContributedOption Declaration, string? Default = null);

/// <summary>
/// A test-only contributor of options: it configures each declared option by its kind, reads a flag
/// into <see cref="FlagKey"/>, a single value into <see cref="ValueKey"/> and a list into
/// <see cref="ListKey"/>, refuses one value of one option with <c>PLY9001</c>, and yields every value
/// it read as a canonical argument.
/// </summary>
/// <param name="owner">The owner.</param>
/// <param name="commands">The commands.</param>
/// <param name="options">The options.</param>
public sealed class DemoContributor(string owner, IReadOnlyList<string> commands, params DemoOption[] options) : ICommandOptionContributor {

  /// <summary>The error of a value the contributor refuses, exit code 2; test-only.</summary>
  public static readonly DiagnosticDescriptor Refused = new("PLY9001", DiagnosticSeverity.Error, ExitCode.UsageError, "'{1}' is not a valid value for '{0}'", "test-only");

  /// <inheritdoc/>
  public string Owner => owner;

  /// <inheritdoc/>
  public IReadOnlyList<string> Commands => commands;

  /// <inheritdoc/>
  public IReadOnlyList<ContributedOption> Options => [.. options.Select(option => option.Declaration)];

  /// <summary>The option and value the contributor refuses, or <see langword="null"/>.</summary>
  public (string Option, string Value)? RefusedValue { get; init; }

  /// <summary>Whether <see cref="Read"/> returns <see langword="false"/> without reporting anything, a defect of a contributor.</summary>
  public bool FailsSilently { get; init; }

  /// <summary>Configures in place of the declared options, or <see langword="null"/> to configure those.</summary>
  public Action<string, Command>? ConfigureInstead { get; init; }

  /// <summary>The words <see cref="CanonicalArguments"/> yields in place of the values read, or <see langword="null"/>.</summary>
  public IReadOnlyList<string>? CanonicalInstead { get; init; }

  /// <summary>The key of a flag.</summary>
  public static OptionKey<bool> FlagKey(string option) => new(option[2..]);

  /// <summary>The key of a single value.</summary>
  public static OptionKey<string> ValueKey(string option) => new(option[2..]);

  /// <summary>The key of a list or repeat-only option.</summary>
  public static OptionKey<string[]> ListKey(string option) => new(option[2..]);

  /// <summary>The usual demo contributor: <c>--demo-level &lt;n&gt;</c> and <c>--demo-flag</c>, refusing <c>--demo-level 0</c>.</summary>
  public static DemoContributor Level(string owner, params string[] commands) => new(owner, commands,
    new DemoOption(new ContributedOption("--demo-level", OptionKind.Single, ListOrder.None, true, ContributedOption.Identity)),
    new DemoOption(new ContributedOption("--demo-flag", OptionKind.Flag, ListOrder.None, true, ContributedOption.Identity))) {
    RefusedValue = ("--demo-level", "0")
  };

  /// <summary>An address written as <c>0x</c> and four upper-case hexadecimal digits, whether typed in decimal or hexadecimal.</summary>
  public static string Address(string value)
    => "0x" + (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(value, CultureInfo.InvariantCulture)).ToString("X4", CultureInfo.InvariantCulture);

  /// <inheritdoc/>
  public void Configure(string commandName, Command command) {
    if (this.ConfigureInstead is { } configure) {
      configure(commandName, command);
      return;
    }

    foreach (var (declaration, value) in options)
      command.Options.Add(declaration.Kind switch {
        OptionKind.Flag => new Option<bool>(declaration.Name) { Description = "demo flag" },
        OptionKind.Single when value is not null => new Option<string>(declaration.Name) { Description = "demo value", HelpName = "n", DefaultValueFactory = _ => value },
        OptionKind.Single => new Option<string>(declaration.Name) { Description = "demo value", HelpName = "n" },
        _ => new Option<string[]>(declaration.Name) { Description = "demo list", HelpName = "item" }
      });
  }

  /// <inheritdoc/>
  public bool Read(string commandName, ParseResult parse, OptionBag bag, CliContext context) {
    if (this.FailsSilently)
      return false;

    foreach (var declaration in this.Options)
      switch (declaration.Kind) {
        case OptionKind.Flag:
          if (parse.GetValue<bool>(declaration.Name))
            bag.Set(FlagKey(declaration.Name), true);
          break;
        case OptionKind.Single:
          if (parse.GetValue<string>(declaration.Name) is not { } value)
            break;
          if (this.RefusedValue == (declaration.Name, value)) {
            context.Report(Diagnostic.Create(Refused, null, declaration.Name, value));
            return false;
          }

          bag.Set(ValueKey(declaration.Name), value);
          break;
        default:
          if (parse.GetValue<string[]>(declaration.Name) is { Length: > 0 } values)
            bag.Set(ListKey(declaration.Name), values);
          break;
      }

    return true;
  }

  /// <inheritdoc/>
  public IEnumerable<string> CanonicalArguments(OptionBag bag) {
    if (this.CanonicalInstead is { } instead)
      return instead;

    var words = new List<string>();
    foreach (var declaration in this.Options)
      if (declaration.Kind == OptionKind.Flag && bag.GetOrDefault(FlagKey(declaration.Name), false))
        words.Add(declaration.Name);
      else if (declaration.Kind == OptionKind.Single && bag.TryGet(ValueKey(declaration.Name), out var value))
        words.AddRange([declaration.Name, value]);
      else if (bag.TryGet(ListKey(declaration.Name), out var values))
        words.AddRange(values.SelectMany(item => new[] { declaration.Name, item }));

    return words;
  }

}

/// <summary>A test-only contributor of identifiers.</summary>
/// <param name="owner">The owner.</param>
/// <param name="identifiers">The identifiers.</param>
public sealed class DemoIdentifiers(string owner, params ContributedIdentifier[] identifiers) : IIdentifierContributor {

  /// <inheritdoc/>
  public string Owner => owner;

  /// <inheritdoc/>
  public IReadOnlyList<ContributedIdentifier> Identifiers => identifiers;

}
