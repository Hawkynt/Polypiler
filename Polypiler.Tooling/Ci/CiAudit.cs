using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Ci;

/// <summary>What one run of <see cref="CiAudit"/> found.</summary>
/// <param name="Layers">One line per valid layer file: its file, job, toolchains and acceptance tests.</param>
/// <param name="Categories">The number of categories of the category file.</param>
/// <param name="Problems">Every <c>PLY0075</c> and <c>PLY0076</c>, in the order the checks ran.</param>
public sealed record CiAuditResult(IReadOnlyList<string> Layers, int Categories, IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// <c>polypiler-tooling ci audit</c>: holds the layer files, the workflow, the toolchain lock, the test
/// categories and the tests to each other, so that nothing counts as passing that no job runs
/// (AGENTS.md, "Tests come in two tiers"), and checks the numbering of the decision records.
/// </summary>
public static class CiAudit {

  /// <summary>The toolchain every layer has, because every layer builds with it.</summary>
  public const string BaseToolchain = "dotnet-sdk";

  /// <summary>Audits a repository.</summary>
  /// <param name="root">The repository root.</param>
  /// <returns>The layers, the number of categories and the problems.</returns>
  /// <exception cref="CiEnvironmentException">A file the audit needs is missing or unreadable (<c>PLY0004</c>).</exception>
  public static CiAuditResult Run(string root) {
    ArgumentNullException.ThrowIfNull(root);
    var (layers, layerProblems) = CiLayer.LoadAll(root);
    var problems = new List<Diagnostic>(layerProblems);
    var workflow = WorkflowFile.Load(Path.Combine(root, WorkflowFile.RepositoryPath), WorkflowFile.RepositoryPath);
    var lockKeys = LockKeys(root);
    var categories = CiCategories.Load(root, problems);
    var tests = TestInventory.Load(root);

    foreach (var layer in layers) {
      if (workflow.Job(layer.Job) is null)
        problems.Add(CiDiagnostics.InconsistentWith($"layer '{layer.Name}' has no job '{layer.Job}' in {WorkflowFile.RepositoryPath}"));
      foreach (var key in layer.Toolchains.Where(key => !lockKeys.Contains(key)))
        problems.Add(CiDiagnostics.InconsistentWith($"layer '{layer.Name}' names unknown toolchain '{key}'"));
    }

    foreach (var job in workflow.Jobs.Where(job => job.Id.StartsWith(CiLayer.JobPrefix, StringComparison.Ordinal)))
      if (!File.Exists(Path.Combine(root, CiLayer.Folder, job.Id[CiLayer.JobPrefix.Length..] + ".yaml")))
        problems.Add(CiDiagnostics.InconsistentWith($"job '{job.Id}' has no layer file"));

    var byName = layers.ToDictionary(layer => layer.Name, StringComparer.Ordinal);
    foreach (var test in tests.Where(test => test.IsAcceptance))
      problems.AddRange(CheckTest(test, byName));

    foreach (var category in categories)
      problems.AddRange(CheckCategory(category, workflow, tests));

    problems.AddRange(AdrNumbering.Check(root));

    var lines = layers.Select(layer => {
      var count = tests.Count(test => test.Layers.Contains(TestEntry.LayerPrefix + layer.Name));
      return $"layer {layer.Name}: {layer.File}, job {layer.Job}, toolchains {string.Join(" ", layer.Toolchains)}, {count} acceptance tests";
    });
    return new([.. lines], categories.Count, problems);
  }

  private static HashSet<string> LockKeys(string root) {
    try {
      return [.. ToolchainLock.Load(Path.Combine(root, ToolchainLock.RepositoryPath)).Entries.Select(entry => entry.Key)];
    } catch (ToolchainLockException exception) {
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(ToolchainLock.RepositoryPath, exception.Diagnostics[0].Message));
    }
  }

  private static IEnumerable<Diagnostic> CheckTest(TestEntry test, IReadOnlyDictionary<string, CiLayer> layers) {
    var named = test.Layers;
    if (named.Count == 0) {
      yield return CiDiagnostics.UnrunWith($"test '{test.FullName}' has no Layer category");
      yield break;
    }

    if (named.Count > 1) {
      yield return CiDiagnostics.UnrunWith($"test '{test.FullName}' has {named.Count} Layer categories: {string.Join(", ", named)}");
      yield break;
    }

    var name = named[0][TestEntry.LayerPrefix.Length..];
    if (!layers.TryGetValue(name, out var layer)) {
      yield return CiDiagnostics.UnrunWith($"test '{test.FullName}' names the layer '{name}', which has no layer file");
      yield break;
    }

    foreach (var key in test.Toolchains.Where(key => key != BaseToolchain && !layer.Toolchains.Contains(key)))
      yield return CiDiagnostics.UnrunWith($"test '{test.FullName}' requires toolchain '{key}', which layer '{name}' does not provide");
  }

  /// <summary>
  /// A category is run when the step its entry names, in the job it names, has a <c>dotnet test</c>
  /// whose filter selects a test of the category: one in no layer, or one in the layer of the job.
  /// </summary>
  private static IEnumerable<Diagnostic> CheckCategory(CiCategory category, WorkflowFile workflow, IReadOnlyList<TestEntry> tests) {
    var reason = Unrun(category, workflow);
    if (reason is not null)
      yield return CiDiagnostics.UnrunWith($"category '{category.Name}' is not run by any step: {reason}");

    if (category.Pending is null && !tests.Any(test => test.Categories.Contains(category.Name)))
      yield return CiDiagnostics.UnrunWith($"category '{category.Name}' has no tests and is not pending");
  }

  private static string? Unrun(CiCategory category, WorkflowFile workflow) {
    if (workflow.Job(category.Job) is not { } job)
      return $"{WorkflowFile.RepositoryPath} has no job '{category.Job}'";

    if (job.Steps.FirstOrDefault(step => step.IsNamed(category.Step)) is not { } step)
      return $"job '{category.Job}' has no step '{category.Step}'";

    IReadOnlyList<TestFilter> filters;
    try {
      filters = TestFilter.InCommands(step.Run);
    } catch (FormatException exception) {
      return $"step '{category.Step}' of job '{category.Job}': {exception.Message}";
    }

    string[][] probes = job.Id.StartsWith(CiLayer.JobPrefix, StringComparison.Ordinal)
      ? [[category.Name], [category.Name, TestEntry.LayerPrefix + job.Id[CiLayer.JobPrefix.Length..]]]
      : [[category.Name]];
    return filters.Any(filter => probes.Any(filter.Selects))
      ? null
      : $"no 'dotnet test' of step '{category.Step}' of job '{category.Job}' selects TestCategory={category.Name}";
  }

}
