using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>Why a fact holds: the statements it was derived from, for proof references in the opt-report.</summary>
/// <param name="Domain">The name of the domain.</param>
/// <param name="Because">The declarations, assignments and branches the fact rests on, in the order the analysis met them.</param>
/// <param name="Assumed">Whether the fact rests on an assumption the user supplied rather than on the program alone.</param>
public sealed record FactExplanation(string Domain, IReadOnlyList<IrStmt> Because, bool Assumed);

/// <summary>A provider gives up on a query: the facade answers the domain's Unknown.</summary>
public class FactUnavailableException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="message">Why the provider gave up.</param>
  public FactUnavailableException(string message) : base(message) { }

}

/// <summary>An analysis stopped at its budget: every fact it would have answered is Unknown (PLY0301).</summary>
public sealed class FactBudgetException : FactUnavailableException {

  /// <summary>Creates the exception.</summary>
  /// <param name="domain">The name of the domain.</param>
  /// <param name="function">The name of the function analysed.</param>
  public FactBudgetException(string domain, string function) : base($"analysis '{domain}' stopped at its budget in '{function}'") {
    this.Domain = domain;
    this.Function = function;
  }

  /// <summary>The name of the domain.</summary>
  public string Domain { get; }

  /// <summary>The name of the function analysed.</summary>
  public string Function { get; }

}

/// <summary>A query the facade cannot answer at all, or an unsound fact: carries the diagnostic, PLY0300 or PLY0302.</summary>
public sealed class FactException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public FactException(Diagnostic diagnostic) : base(Text(diagnostic)) => this.Diagnostic = diagnostic;

  /// <summary>The diagnostic.</summary>
  public Diagnostic Diagnostic { get; }

  private static string Text(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    return diagnostic.ToString();
  }

}
