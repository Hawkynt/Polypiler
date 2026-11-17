using System.Collections.Generic;
using System.CommandLine;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Cli.Support;

/// <summary>
/// <c>options</c>: the test-only command of the pipeline options, shared as source by the unit tests
/// and the acceptance tests. It attaches the four options through <see cref="GlobalOptionSet"/>, knows
/// the optimizations <c>O0001</c> and <c>O0100</c>, prints the canonical rendering of what it read and
/// reports the diagnostics it was made with afterwards.
/// </summary>
/// <param name="reports">The diagnostics reported after the line is printed.</param>
public sealed class OptionsCommand(params DiagnosticDescriptor[] reports) : ICliCommand {

  /// <summary>The optimizations the command's pipeline has.</summary>
  public static IReadOnlyList<string> Optimizations { get; } = ["O0001", "O0100"];

  /// <summary>The options of the last run that read them, or <see langword="null"/>.</summary>
  public GlobalOptions? Last { get; private set; }

  /// <inheritdoc/>
  public string Name => "options";

  /// <inheritdoc/>
  public string Description => "print the pipeline options";

  /// <inheritdoc/>
  public void Configure(Command command) => GlobalOptionSet.AddTo(command);

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    this.Last = GlobalOptionSet.Read(parse, context, Optimizations);
    if (this.Last is null)
      return 0;

    context.Output.WriteLine(this.Last.ToCanonicalArguments());
    foreach (var report in reports)
      context.Report(Diagnostic.Create(report, null));

    return 0;
  }

  /// <summary>A registry with this command beside the test commands <c>echo</c> and <c>demand</c>, which do not attach the pipeline options.</summary>
  /// <param name="reports">The diagnostics the command reports after its line.</param>
  /// <returns>The registry and the command.</returns>
  public static (CommandRegistry Registry, OptionsCommand Options) Registry(params DiagnosticDescriptor[] reports) {
    var (registry, _, _) = TestCommands.Registry();
    var options = new OptionsCommand(reports);
    registry.Add(options);
    return (registry, options);
  }

}
