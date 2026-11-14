using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Cli;

/// <summary>The commands <c>polypiler</c> knows, by name.</summary>
public sealed class CommandRegistry {

  private readonly SortedDictionary<string, ICliCommand> _commands = new(StringComparer.Ordinal);

  /// <summary>The registered commands, sorted by name (ordinal).</summary>
  public IReadOnlyList<ICliCommand> Commands => [.. this._commands.Values];

  /// <summary>Registers a command.</summary>
  /// <param name="command">The command.</param>
  /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">
  /// The name is no word of the grammar (lower-case words joined by single hyphens), is the reserved
  /// <c>help</c>, or is registered already.
  /// </exception>
  public void Add(ICliCommand command) {
    ArgumentNullException.ThrowIfNull(command);
    if (!IsWord(command.Name))
      throw new ArgumentException($"'{command.Name}' is not a command name: lower-case words joined by single hyphens", nameof(command));
    if (command.Name == "help")
      throw new ArgumentException("'help' is reserved and cannot be a command", nameof(command));
    if (!this._commands.TryAdd(command.Name, command))
      throw new ArgumentException($"a command '{command.Name}' is registered already", nameof(command));
  }

  /// <summary>The command of a name, or <see langword="null"/>; the name is matched ordinally.</summary>
  /// <param name="name">The name as typed.</param>
  public ICliCommand? Find(string name) => this._commands.GetValueOrDefault(name);

  /// <summary>Whether a name is a <c>word</c> of the grammar: a lower-case ASCII letter, then letters and digits, in parts joined by single hyphens.</summary>
  private static bool IsWord(string? name)
    => name is { Length: > 0 } && char.IsAsciiLetterLower(name[0]) && !name.EndsWith('-') && !name.Contains("--", StringComparison.Ordinal)
      && name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

}
