using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Ci;

/// <summary>One step of a job: its <c>id</c>, its <c>name</c> and the text of its <c>run</c>, empty for a step that <c>uses</c> an action.</summary>
/// <param name="Id">The step's <c>id</c>, if it has one.</param>
/// <param name="Name">The step's <c>name</c>, if it has one.</param>
/// <param name="Run">The shell text the step runs.</param>
public sealed record WorkflowStep(string? Id, string? Name, string Run) {

  /// <summary>Whether a category file names this step: by its <c>id</c> or by its <c>name</c>.</summary>
  /// <param name="name">The name the category file gives.</param>
  /// <returns><see langword="true"/> for the step's id or name.</returns>
  public bool IsNamed(string name) => this.Id == name || this.Name == name;

}

/// <summary>One job of a workflow and its steps in order; a job that calls a reusable workflow has none.</summary>
/// <param name="Id">The job's key under <c>jobs</c>.</param>
/// <param name="Steps">The steps.</param>
public sealed record WorkflowJob(string Id, IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// The jobs and steps of a GitHub Actions workflow, as far as <c>polypiler-tooling ci audit</c> reads
/// them: job keys, step ids and names and the <c>run</c> texts.
/// </summary>
public sealed class WorkflowFile {

  /// <summary>The workflow the layer jobs live in, relative to the repository root.</summary>
  public const string RepositoryPath = ".github/workflows/ci.yml";

  private readonly Dictionary<string, WorkflowJob> _jobs;

  private WorkflowFile(IEnumerable<WorkflowJob> jobs) => this._jobs = jobs.ToDictionary(job => job.Id, StringComparer.Ordinal);

  /// <summary>The jobs in the order of the file.</summary>
  public IReadOnlyCollection<WorkflowJob> Jobs => this._jobs.Values;

  /// <summary>The job with a key, if there is one.</summary>
  /// <param name="id">The job's key.</param>
  /// <returns>The job or <see langword="null"/>.</returns>
  public WorkflowJob? Job(string id) => this._jobs.GetValueOrDefault(id);

  /// <summary>Reads a workflow file.</summary>
  /// <param name="path">The file.</param>
  /// <param name="name">The name messages give it.</param>
  /// <returns>The workflow.</returns>
  /// <exception cref="CiEnvironmentException">The file does not exist, cannot be read or is not a YAML mapping with a mapping of jobs (<c>PLY0004</c>).</exception>
  public static WorkflowFile Load(string path, string name) {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(name, "the file does not exist"));

    return Parse(File.ReadAllText(path), name);
  }

  /// <summary>Reads the text of a workflow.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="name">The name messages give the file.</param>
  /// <returns>The workflow.</returns>
  /// <exception cref="CiEnvironmentException">The text is not YAML, or not a mapping with a mapping of jobs (<c>PLY0004</c>).</exception>
  public static WorkflowFile Parse(string text, string name) {
    ArgumentNullException.ThrowIfNull(text);
    JsonNode? document;
    try {
      document = YamlToJson.Read(text, out _);
    } catch (YamlToJsonException exception) {
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(name, exception.Message));
    }

    if (document is not JsonObject root || root["jobs"] is not JsonObject jobs)
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(name, "the workflow has no mapping 'jobs'"));

    return new(jobs.Select(job => new WorkflowJob(job.Key, Steps(job.Value))));
  }

  private static WorkflowStep[] Steps(JsonNode? job)
    => job is JsonObject mapping && mapping["steps"] is JsonArray steps
      ? [.. steps.OfType<JsonObject>().Select(step => new WorkflowStep(Text(step["id"]), Text(step["name"]), Text(step["run"]) ?? ""))]
      : [];

  private static string? Text(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

}

/// <summary>A file <c>polypiler-tooling ci</c> needs cannot be read; the command ends with its diagnostic.</summary>
/// <param name="diagnostic">The <c>PLY0004</c> diagnostic.</param>
public sealed class CiEnvironmentException(Polypiler.Core.Diagnostic diagnostic) : Exception(diagnostic.Message) {

  /// <summary>The diagnostic.</summary>
  public Polypiler.Core.Diagnostic Diagnostic { get; } = diagnostic;

}
