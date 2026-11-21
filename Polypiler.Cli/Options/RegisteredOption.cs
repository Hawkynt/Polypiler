namespace Polypiler.Cli.Options;

/// <summary>One contributed option of one command, as <see cref="OptionContributions.Describe"/> lists it.</summary>
/// <param name="Command">The command.</param>
/// <param name="Option">The declaration.</param>
/// <param name="Owner">The owner of the contributor.</param>
public sealed record RegisteredOption(string Command, ContributedOption Option, string Owner);

/// <summary>One contributed identifier, as <see cref="OptionContributions.Identifiers"/> lists it.</summary>
/// <param name="Identifier">The identifier.</param>
/// <param name="Owner">The owner of the contributor.</param>
public sealed record RegisteredIdentifier(ContributedIdentifier Identifier, string Owner);
