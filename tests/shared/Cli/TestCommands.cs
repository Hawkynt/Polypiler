using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Cli.Support;

/// <summary>
/// The test-only commands of the command-line foundation, shared as source by the unit tests and the
/// acceptance tests: <c>echo</c> and <c>demand</c>, registered beside nothing else.
/// </summary>
public static class TestCommands {

  /// <summary>An info of the test command; it carries no attribute, so the registry test does not collect it.</summary>
  public static readonly DiagnosticDescriptor TestInfo = new("PLY0098", DiagnosticSeverity.Info, null, "test info", "test-only");

  /// <summary>A warning of the test command.</summary>
  public static readonly DiagnosticDescriptor TestWarning = new("PLY0098", DiagnosticSeverity.Warning, null, "test warning", "test-only");

  /// <summary>An error of the test command, exit code 1.</summary>
  public static readonly DiagnosticDescriptor TestError = new("PLY0098", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "test error", "test-only");

  /// <summary>A registry with a new <see cref="EchoCommand"/> and a new <see cref="DemandCommand"/>.</summary>
  public static (CommandRegistry Registry, EchoCommand Echo, DemandCommand Demand) Registry(params DiagnosticDescriptor[] reports) {
    var echo = new EchoCommand(reports);
    var demand = new DemandCommand();
    var registry = new CommandRegistry();
    registry.Add(echo);
    registry.Add(demand);
    return (registry, echo, demand);
  }

}

/// <summary>
/// <c>echo &lt;input&gt;</c> with <c>--output</c>, <c>--force</c>, <c>--export</c>, <c>--mode a|b</c>,
/// <c>--note</c> and the exclusive pair <c>--left</c> / <c>--right</c>: checks the input and the
/// output directory, prints <c>echo &lt;input&gt;</c> and reports the diagnostics it was made with.
/// </summary>
/// <param name="reports">The diagnostics reported after the line is printed.</param>
public sealed class EchoCommand(IReadOnlyList<DiagnosticDescriptor> reports) : ICliCommand {

  private int _executions;

  /// <summary>How often the command ran.</summary>
  public int Executions => Volatile.Read(ref this._executions);

  /// <summary>The values of the last run: the input, the list of <c>--export</c>, <c>--mode</c> and <c>--note</c>.</summary>
  public (string Input, string[]? Export, string? Mode, string? Note) Last { get; private set; }

  /// <inheritdoc/>
  public string Name => "echo";

  /// <inheritdoc/>
  public string Description => "print the input";

  /// <inheritdoc/>
  public void Configure(Command command) {
    command.Arguments.Add(new Argument<string>("input") { Description = "the input" });
    command.Options.Add(CommonOptions.Output);
    command.Options.Add(CommonOptions.Force);
    command.Options.Add(CommonOptions.Export);
    command.Options.Add(OptionRules.AcceptOnly(new Option<string>("--mode") { Description = "the mode" }, "a", "b"));
    command.Options.Add(new Option<string>("--note"));
    var left = new Option<bool>("--left") { Description = "left" };
    var right = new Option<bool>("--right") { Description = "right" };
    command.Options.Add(left);
    command.Options.Add(right);
    OptionRules.Exclusive(command, left, right);
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    Interlocked.Increment(ref this._executions);
    var input = parse.GetValue<string>("input")!;
    this.Last = (input, parse.GetValue<string[]>("--export"), parse.GetValue<string>("--mode"), parse.GetValue<string>("--note"));
    if (!CliPaths.RequireInput(input, context))
      return 0;

    if (parse.GetValue<string>("--output") is { } output && !CliPaths.PrepareOutputDirectory(output, parse.GetValue<bool>("--force"), context))
      return 0;

    context.Output.WriteLine("echo " + input);
    foreach (var report in reports)
      context.Report(Diagnostic.Create(report, null));

    return 0;
  }

}

/// <summary><c>demand --key &lt;value&gt;</c>: no operand and one required option; prints <c>demand &lt;key&gt;</c>.</summary>
public sealed class DemandCommand : ICliCommand {

  /// <inheritdoc/>
  public string Name => "demand";

  /// <inheritdoc/>
  public string Description => "print the key";

  /// <inheritdoc/>
  public void Configure(Command command) => command.Options.Add(new Option<string>("--key") { Required = true });

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    context.Output.WriteLine("demand " + parse.GetValue<string>("--key"));
    return 0;
  }

}
