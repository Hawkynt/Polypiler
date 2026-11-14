using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>Finds the fuzz targets an assembly declares.</summary>
public static class FuzzTargets {

  /// <summary>
  /// Every public, non-abstract <see cref="IFuzzTarget"/> class of the assembly that has a public
  /// parameterless constructor, one instance each, ordered by <see cref="IFuzzTarget.Id"/>.
  /// </summary>
  public static IReadOnlyList<IFuzzTarget> Discover(Assembly assembly) {
    ArgumentNullException.ThrowIfNull(assembly);
    Type?[] types;
    try {
      types = assembly.GetTypes();
    } catch (ReflectionTypeLoadException exception) {
      // Types whose dependencies are not present cannot be targets the caller can run anyway.
      types = exception.Types;
    }

    return [.. types
      .OfType<Type>()
      .Where(type => type is { IsClass: true, IsAbstract: false, IsVisible: true } && typeof(IFuzzTarget).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
      .Select(type => (IFuzzTarget)Activator.CreateInstance(type)!)
      .OrderBy(target => target.Id, StringComparer.Ordinal)];
  }

}

/// <summary>A regression input that shows a finding again.</summary>
/// <param name="File">The regression file.</param>
/// <param name="Target">The ID of its target.</param>
/// <param name="Outcome">What the input showed; <see langword="null"/> when no target has that ID.</param>
public sealed record FuzzRegressionFailure(string File, string Target, FuzzOutcome? Outcome) {

  /// <summary>The file and what it showed.</summary>
  public override string ToString()
    => this.Outcome is { } outcome ? $"{this.File}: {outcome.Kind} '{outcome.Signature}'" : $"{this.File}: no fuzz target '{this.Target}' is registered";

}

/// <summary>Replays the inputs of fixed findings, <c>&lt;fixtures&gt;/&lt;target&gt;/regressions/*.bin</c>.</summary>
public static class FuzzRegressions {

  /// <summary>Runs every regression input against its target and returns those that still show a finding.</summary>
  /// <param name="fixturesDirectory">The directory of the targets' corpora, e.g. <c>tests/fixtures/fuzz</c>.</param>
  /// <param name="targets">The targets, by ID.</param>
  /// <param name="executors">Creates the executor of a target.</param>
  /// <param name="budget">The limits of one input.</param>
  /// <param name="only">Replays only this target's inputs; all targets' when <see langword="null"/>.</param>
  /// <returns>Every input that still fails, ordered by file; empty when all pass.</returns>
  public static IReadOnlyList<FuzzRegressionFailure> Replay(string fixturesDirectory, IReadOnlyDictionary<string, IFuzzTarget> targets, Func<IFuzzTarget, IFuzzExecutor> executors, FuzzBudget budget, string? only = null) {
    ArgumentNullException.ThrowIfNull(fixturesDirectory);
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(executors);
    ArgumentNullException.ThrowIfNull(budget);
    var failures = new List<FuzzRegressionFailure>();
    if (!Directory.Exists(fixturesDirectory))
      return failures;

    foreach (var directory in Directory.GetDirectories(fixturesDirectory).Order(StringComparer.Ordinal)) {
      var id = Path.GetFileName(directory);
      var regressions = Path.Combine(directory, FuzzCorpus.RegressionsFolder);
      if ((only != null && id != only) || !Directory.Exists(regressions))
        continue;

      var files = Directory.GetFiles(regressions, "*.bin").Order(StringComparer.Ordinal).ToList();
      if (files.Count == 0)
        continue;

      if (!targets.TryGetValue(id, out var target)) {
        failures.AddRange(files.Select(file => new FuzzRegressionFailure(file, id, null)));
        continue;
      }

      using var executor = executors(target);
      foreach (var file in files) {
        var input = File.ReadAllBytes(file);
        var outcome = executor.Execute(input.Length > budget.MaxInputLength ? input[..budget.MaxInputLength] : input, budget);
        if (outcome.IsFinding)
          failures.Add(new(file, id, outcome));
      }
    }

    return failures;
  }

}
