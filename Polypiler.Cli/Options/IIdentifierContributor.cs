using System;
using System.Collections.Generic;

namespace Polypiler.Cli.Options;

/// <summary>Where an identifier is accepted: after <c>--from</c>, after <c>--to</c>, or after both.</summary>
[Flags]
public enum IdentifierDirection {

  /// <summary>As the source: after <c>--from</c>.</summary>
  From = 1,

  /// <summary>As the target: after <c>--to</c>.</summary>
  To = 2,

  /// <summary>After both.</summary>
  Both = From | To

}

/// <summary>An identifier for <c>--from</c> or <c>--to</c> that is no language of <c>spec/languages/</c>: a format, a CPU, <c>bitmapping</c>.</summary>
/// <param name="Name">The identifier, matched exactly (ordinal, case-sensitive).</param>
/// <param name="Direction">Where it is accepted.</param>
/// <param name="Description">What it names.</param>
public sealed record ContributedIdentifier(string Name, IdentifierDirection Direction, string Description);

/// <summary>An area's identifiers for <c>--from</c> and <c>--to</c>, registered in <see cref="Program.CreateContributions(CommandRegistry)"/>.</summary>
public interface IIdentifierContributor {

  /// <summary>The owner: the backlog ID or issue of the area.</summary>
  string Owner { get; }

  /// <summary>The identifiers.</summary>
  IReadOnlyList<ContributedIdentifier> Identifiers { get; }

}
