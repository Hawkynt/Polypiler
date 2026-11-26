using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The <c>targets</c> command: lists every supported target backend with its dialect profiles as JSON,
/// or executes a batch of <c>translate</c> invocations inside a single warm process via <c>--run-batch</c>.
/// </summary>
public sealed class TargetsCommand : ICliCommand {

  private const string RunBatchOption = "--run-batch";

  private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

  /// <inheritdoc/>
  public string Name => "targets";

  /// <inheritdoc/>
  public string Description => "list supported target backends and dialect profiles as JSON, or run a translation batch";

  /// <inheritdoc/>
  public void Configure(Command command) {
    ArgumentNullException.ThrowIfNull(command);
    command.Options.Add(new Option<string>(RunBatchOption) {
      Description = "JSON file describing a batch of translate invocations to run in this process",
      HelpName = "file"
    });
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    ArgumentNullException.ThrowIfNull(parse);
    ArgumentNullException.ThrowIfNull(context);
    var option = (Option<string>)parse.CommandResult.Command.Options.First(o => o.Name == RunBatchOption);
    if (parse.GetResult(option) is not { Implicit: false }) {
      var targets = TranslateCommand.SupportedTargets()
        .Select(entry => new TargetEntry(entry.Backend, entry.Profiles))
        .ToArray();
      context.Output.WriteLine(JsonSerializer.Serialize(targets, _json));
      return (int)ExitCode.Success;
    }

    var file = parse.GetValue(option)!;
    if (!CliPaths.RequireInput(file, context))
      return (int)ExitCode.EnvironmentError;

    BatchJob[]? jobs;
    try {
      jobs = JsonSerializer.Deserialize<BatchJob[]>(File.ReadAllText(context.Resolve(file)), _json);
    } catch (JsonException exception) {
      context.Report(Diagnostic.Create(CliDiagnostics.CommandLine, null, $"invalid batch file '{file}': {exception.Message}"));
      return (int)ExitCode.UsageError;
    }

    if (jobs is null) {
      context.Report(Diagnostic.Create(CliDiagnostics.CommandLine, null, $"batch file '{file}' contains no job list"));
      return (int)ExitCode.UsageError;
    }

    var results = new BatchJobResult[jobs.Length];
    Parallel.For(0, jobs.Length, index => {
      var job = jobs[index];
      using var stdout = new StringWriter();
      using var stderr = new StringWriter();
      var exitCode = TranslateCommand.Run(job.Args, stdout, stderr);
      results[index] = new(job.Id, exitCode, stderr.ToString());
    });

    context.Output.WriteLine(JsonSerializer.Serialize(results, _json));
    return (int)ExitCode.Success;
  }

  private sealed record TargetEntry(string Backend, IReadOnlyList<string> Profiles);

  private sealed record BatchJob(string Id, string[] Args);

  private sealed record BatchJobResult(string Id, int ExitCode, string Stderr);

}
