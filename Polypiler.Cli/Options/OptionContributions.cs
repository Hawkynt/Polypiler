using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;

namespace Polypiler.Cli.Options;

/// <summary>
/// The options and identifiers the areas contribute to the commands of one registry (ADR 0004,
/// "Adding a command or an option later"). <see cref="Add(ICommandOptionContributor)"/> checks the
/// naming, collision and declaration rules and refuses a contributor that breaks one, naming both
/// owners of a collision; the foundation configures the contributed options onto their commands,
/// reads them before a command runs, lists them in its help and records them in its canonical
/// command line. Registration order changes none of these.
/// </summary>
public sealed class OptionContributions {

  /// <summary>The names no contributor may take: the help option, <c>--version</c> and the two global options.</summary>
  private static readonly string[] _reserved = ["--help", "--version", CommandLineParser.DiagnosticsName, CommandLineParser.QuietName];

  private readonly CommandRegistry _registry;
  private readonly List<ICommandOptionContributor> _contributors = [];
  private readonly List<RegisteredOption> _registered = [];
  private readonly List<RegisteredIdentifier> _identifiers = [];

  /// <summary>Creates the contributions to the commands of a registry, with no contributor.</summary>
  /// <param name="registry">The commands whose own options a contributed option must not repeat.</param>
  /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
  public OptionContributions(CommandRegistry registry) {
    ArgumentNullException.ThrowIfNull(registry);
    this._registry = registry;
  }

  /// <summary>Every contributed identifier, ordered by name (ordinal).</summary>
  public IReadOnlyList<RegisteredIdentifier> Identifiers
    => [.. this._identifiers.OrderBy(identifier => identifier.Identifier.Name, StringComparer.Ordinal).ThenBy(identifier => identifier.Identifier.Direction)];

  /// <summary>
  /// Registers an option contributor. Every option it declares has a name of the grammar that is no
  /// global option, a list and only a list is a set or a sequence, and on each of its commands it
  /// configures exactly the options it declares, each of the declared kind; no option collides with
  /// an option the command has or another contributor added to it.
  /// </summary>
  /// <param name="contributor">The contributor.</param>
  /// <exception cref="ArgumentNullException"><paramref name="contributor"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidOperationException">A rule is broken; nothing is registered.</exception>
  public void Add(ICommandOptionContributor contributor) {
    ArgumentNullException.ThrowIfNull(contributor);
    var owner = contributor.Owner;
    var declared = Declarations(contributor);
    var added = new List<RegisteredOption>();
    foreach (var commandName in contributor.Commands.Distinct(StringComparer.Ordinal)) {
      var scratch = new Command(commandName);
      contributor.Configure(commandName, scratch);
      CheckConfigured(owner, commandName, declared, scratch.Options);
      var own = this._registry.Find(commandName) is { } command ? CommandLineParser.Build(command).Command.Options.Select(option => option.Name).ToHashSet(StringComparer.Ordinal) : [];
      foreach (var declaration in contributor.Options) {
        if (own.Contains(declaration.Name))
          throw new InvalidOperationException($"option '{declaration.Name}' of command '{commandName}' is an option of the command itself (second registration: {owner})");
        if (this._registered.FirstOrDefault(registered => registered.Command == commandName && registered.Option.Name == declaration.Name) is { } existing)
          throw new InvalidOperationException($"option '{declaration.Name}' of command '{commandName}' is already registered by {existing.Owner} (second registration: {owner})");

        added.Add(new RegisteredOption(commandName, declaration, owner));
      }
    }

    this._contributors.Add(contributor);
    this._registered.AddRange(added);
  }

  /// <summary>Registers an identifier contributor: every identifier is a word of the grammar with a direction, and none is registered twice for one direction.</summary>
  /// <param name="contributor">The contributor.</param>
  /// <exception cref="ArgumentNullException"><paramref name="contributor"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidOperationException">A rule is broken; nothing is registered.</exception>
  public void Add(IIdentifierContributor contributor) {
    ArgumentNullException.ThrowIfNull(contributor);
    var added = new List<RegisteredIdentifier>();
    foreach (var identifier in contributor.Identifiers) {
      if (!CommandRegistry.IsWord(identifier.Name))
        throw new InvalidOperationException($"'{identifier.Name}' of {contributor.Owner} is not an identifier: lower-case words of letters and digits joined by single hyphens");
      if (identifier.Direction is not (IdentifierDirection.From or IdentifierDirection.To or IdentifierDirection.Both))
        throw new InvalidOperationException($"identifier '{identifier.Name}' of {contributor.Owner} has no direction");

      var clash = this._identifiers.Concat(added).FirstOrDefault(registered => registered.Identifier.Name == identifier.Name && (registered.Identifier.Direction & identifier.Direction) != 0);
      if (clash is not null)
        throw new InvalidOperationException($"identifier '{identifier.Name}' for {Directions(clash.Identifier.Direction & identifier.Direction)} is already registered by {clash.Owner} (second registration: {contributor.Owner})");

      added.Add(new RegisteredIdentifier(identifier, contributor.Owner));
    }

    this._identifiers.AddRange(added);
  }

  /// <summary>Every contributed option of every command, ordered by command and then by option name (ordinal).</summary>
  public IReadOnlyList<RegisteredOption> Describe()
    => [.. this._registered.OrderBy(registered => registered.Command, StringComparer.Ordinal).ThenBy(registered => registered.Option.Name, StringComparer.Ordinal)];

  /// <summary>Whether an identifier was contributed for a direction; matched exactly (ordinal, case-sensitive).</summary>
  /// <param name="name">The identifier as typed.</param>
  /// <param name="direction">Where it stands: <see cref="IdentifierDirection.From"/> or <see cref="IdentifierDirection.To"/>.</param>
  /// <returns>Whether it is accepted there.</returns>
  public bool IsKnownIdentifier(string name, IdentifierDirection direction)
    => this._identifiers.Any(registered => registered.Identifier.Name == name && (registered.Identifier.Direction & direction) == direction);

  /// <summary>Adds the contributed options of a command to its symbol, after the command's own.</summary>
  /// <param name="commandName">The name of the command.</param>
  /// <param name="command">The symbol of the command.</param>
  /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
  public void ConfigureFor(string commandName, Command command) {
    ArgumentNullException.ThrowIfNull(command);
    foreach (var contributor in this.For(commandName)) {
      contributor.Configure(commandName, command);
      var repeatOnly = contributor.Options.Where(option => option.Kind == OptionKind.RepeatOnly).Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
      foreach (var option in command.Options.Where(option => repeatOnly.Contains(option.Name)))
        OptionRules.MarkRepeatOnly(option);
    }
  }

  /// <summary>Reads the contributed options of a command from a command line the grammar accepted.</summary>
  /// <param name="commandName">The name of the command.</param>
  /// <param name="parse">The parsed command line.</param>
  /// <param name="context">The context of the run.</param>
  /// <returns>The values, or <see langword="null"/> when a contributor refused the command line, after reporting why.</returns>
  public OptionBag? ReadFor(string commandName, ParseResult parse, CliContext context) {
    var bag = new OptionBag();
    foreach (var contributor in this.For(commandName))
      if (!contributor.Read(commandName, parse, bag, context))
        return null;

    return bag;
  }

  /// <summary>
  /// The contributed part of a command's canonical command line (ADR 0004, "Recorded command line"):
  /// every recorded option, ordered by name (ordinal), as <c>--name value</c> with the value in its
  /// normal spelling; a flag as <c>--name</c>; a list once, comma-joined, a set sorted (ordinal) with
  /// duplicates removed; a repeat-only option once per value, the values sorted (ordinal).
  /// </summary>
  /// <param name="commandName">The name of the command.</param>
  /// <param name="bag">The values <see cref="ReadFor"/> read.</param>
  /// <returns>The words.</returns>
  /// <exception cref="InvalidOperationException">A contributor yields a word that is no option it declares, or an option without its value.</exception>
  public IReadOnlyList<string> CanonicalArgumentsFor(string commandName, OptionBag bag) {
    var rendered = new List<(string Name, IEnumerable<string> Words)>();
    foreach (var contributor in this.For(commandName)) {
      var declared = contributor.Options.ToDictionary(option => option.Name, StringComparer.Ordinal);
      var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
      var words = contributor.CanonicalArguments(bag).ToList();
      for (var index = 0; index < words.Count; ++index) {
        if (!declared.TryGetValue(words[index], out var declaration))
          throw new InvalidOperationException($"{contributor.Owner} yields '{words[index]}' as a canonical argument, which is no option it declares");

        var list = values.TryGetValue(declaration.Name, out var known) ? known : values[declaration.Name] = [];
        if (declaration.Kind == OptionKind.Flag)
          continue;
        if (index + 1 == words.Count)
          throw new InvalidOperationException($"{contributor.Owner} yields the option '{declaration.Name}' without a value as a canonical argument");

        list.Add(words[++index]);
      }

      rendered.AddRange(values.Where(value => declared[value.Key].Recorded).Select(value => (value.Key, Render(declared[value.Key], value.Value))));
    }

    return [.. rendered.OrderBy(option => option.Name, StringComparer.Ordinal).SelectMany(option => option.Words)];
  }

  /// <summary>The contributed options of a command, grouped by owner (ordinal), each group in name order: what its help lists.</summary>
  internal IReadOnlyList<(string Owner, IReadOnlyList<string> Names)> HelpGroups(string commandName)
    => [.. this._registered.Where(registered => registered.Command == commandName)
      .GroupBy(registered => registered.Owner, StringComparer.Ordinal)
      .OrderBy(group => group.Key, StringComparer.Ordinal)
      .Select(group => (group.Key, (IReadOnlyList<string>)[.. group.Select(registered => registered.Option.Name).Order(StringComparer.Ordinal)]))];

  /// <summary>The contributors of a command, ordered by owner (ordinal) so that registration order changes nothing.</summary>
  private IEnumerable<ICommandOptionContributor> For(string commandName)
    => this._contributors.Where(contributor => contributor.Commands.Contains(commandName, StringComparer.Ordinal)).OrderBy(contributor => contributor.Owner, StringComparer.Ordinal);

  /// <summary>One option's words: the declaration fixes how the values are written.</summary>
  private static IEnumerable<string> Render(ContributedOption declaration, List<string> values) {
    var spelled = values.Select(declaration.NormalSpelling);
    return declaration.Kind switch {
      OptionKind.Flag => [declaration.Name],
      OptionKind.Single => [declaration.Name, spelled.Last()],
      OptionKind.List => [declaration.Name, string.Join(',', declaration.Order == ListOrder.Set
        ? values.SelectMany(value => value.Split(',')).Select(declaration.NormalSpelling).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        : values.SelectMany(value => value.Split(',')).Select(declaration.NormalSpelling))],
      _ => spelled.Order(StringComparer.Ordinal).SelectMany(value => new[] { declaration.Name, value })
    };
  }

  /// <summary>The declarations of a contributor by name, each checked against the naming and declaration rules.</summary>
  private static Dictionary<string, ContributedOption> Declarations(ICommandOptionContributor contributor) {
    var declared = new Dictionary<string, ContributedOption>(StringComparer.Ordinal);
    foreach (var option in contributor.Options) {
      if (!option.Name.StartsWith("--", StringComparison.Ordinal) || !CommandRegistry.IsWord(option.Name[2..]))
        throw new InvalidOperationException($"'{option.Name}' of {contributor.Owner} is not an option name: '--' followed by lower-case words of letters and digits joined by single hyphens");
      if (_reserved.Contains(option.Name, StringComparer.Ordinal))
        throw new InvalidOperationException($"option '{option.Name}' of {contributor.Owner} is a global option, which no contributor can add");
      if ((option.Kind == OptionKind.List) != (option.Order != ListOrder.None))
        throw new InvalidOperationException($"option '{option.Name}' of {contributor.Owner} is a {option.Kind} declared with the order {option.Order}; a list, and only a list, is a Set or a Sequence");
      if (!declared.TryAdd(option.Name, option))
        throw new InvalidOperationException($"option '{option.Name}' is declared twice by {contributor.Owner}");
    }

    return declared;
  }

  /// <summary>Checks that a contributor configured on a command exactly the options it declares, each once and of the declared kind.</summary>
  private static void CheckConfigured(string owner, string commandName, Dictionary<string, ContributedOption> declared, IList<Option> configured) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var option in configured) {
      if (!declared.TryGetValue(option.Name, out var declaration))
        throw new InvalidOperationException($"option '{option.Name}' that {owner} configures on '{commandName}' has no declaration");
      if (!seen.Add(option.Name))
        throw new InvalidOperationException($"option '{option.Name}' is configured twice on '{commandName}' by {owner}");

      var expected = declaration.Kind == OptionKind.RepeatOnly ? OptionKind.List : declaration.Kind;
      if (CommandLineParser.BaseKindOf(option) != expected)
        throw new InvalidOperationException($"option '{option.Name}' of {owner} is declared a {declaration.Kind} but configured as a {CommandLineParser.BaseKindOf(option)}");
    }

    if (declared.Keys.FirstOrDefault(name => !seen.Contains(name)) is { } missing)
      throw new InvalidOperationException($"option '{missing}' that {owner} declares is not configured on '{commandName}'");
  }

  /// <summary>The options a direction stands for, as the messages name them.</summary>
  private static string Directions(IdentifierDirection direction) => direction switch {
    IdentifierDirection.From => "--from",
    IdentifierDirection.To => "--to",
    _ => "--from and --to"
  };

}
