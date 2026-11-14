using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>How far one metric of one benchmark variant may regress.</summary>
/// <param name="Id">The benchmark.</param>
/// <param name="Variant">The variant.</param>
/// <param name="Metric">The metric, for example <c>wallNs</c> or an extra one.</param>
/// <param name="MaxRegressionPercent">The largest allowed increase of the median over the baseline, in percent.</param>
public sealed record Budget(string Id, string Variant, string Metric, decimal MaxRegressionPercent) {

  /// <summary>
  /// Whether a value regresses beyond the budget: <c>current &gt; baseline · (1 + max / 100)</c>,
  /// computed exactly; a value on the limit passes.
  /// </summary>
  public bool IsExceeded(long baseline, long current) => current * 100m > baseline * (100m + this.MaxRegressionPercent);

}

/// <summary>
/// The performance budgets, a file such as <c>tooling/bench/budgets.yaml</c> whose numbers an ADR sets
/// (ADR-PERF-BUDGETS). A metric without an entry is recorded, never gated.
/// </summary>
/// <param name="File">The file the budgets came from, as the messages name it.</param>
/// <param name="MaxRelativeMad">The largest MAD / median of the wall time a compared result may have; <see langword="null"/> for no limit.</param>
/// <param name="Budgets">The entries.</param>
public sealed record BudgetFile(string File, decimal? MaxRelativeMad, IReadOnlyList<Budget> Budgets) {

  /// <summary>The version of the budget format.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The budget of a metric of a variant; <see langword="null"/> when there is none.</summary>
  public Budget? Find(string id, string variant, string metric)
    => this.Budgets.FirstOrDefault(budget => budget.Id == id && budget.Variant == variant && budget.Metric == metric);

  /// <summary>Reads a budget file.</summary>
  /// <param name="path">The file.</param>
  /// <exception cref="BenchmarkFileException">The file cannot be read or does not follow the format.</exception>
  public static BudgetFile Load(string path) {
    string text;
    try {
      text = System.IO.File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new BenchmarkFileException(path, 0, 0, $"cannot be read: {exception.Message}");
    }

    return Parse(text, path);
  }

  /// <summary>Reads the text of a budget file: <c>{ schemaVersion: 1, maxRelativeMad?, budgets: [ { id, variant, metric, maxRegressionPercent } ] }</c>.</summary>
  /// <param name="text">The text.</param>
  /// <param name="file">The file's name, for the messages.</param>
  /// <exception cref="BenchmarkFileException">The text does not follow the format.</exception>
  public static BudgetFile Parse(string text, string file) {
    var reader = new YamlReader(file);
    var root = reader.Root(text);
    var fields = reader.Properties(root, "budget file", "schemaVersion", "maxRelativeMad", "budgets");
    reader.Integer(reader.Required(fields, root, "schemaVersion"), "schemaVersion", SchemaVersion, SchemaVersion);
    var budgets = new List<Budget>();
    foreach (var entry in reader.Mappings(reader.Required(fields, root, "budgets"), "budgets")) {
      var properties = reader.Properties(entry, "budget", "id", "variant", "metric", "maxRegressionPercent");
      var budget = new Budget(
        reader.Text(reader.Required(properties, entry, "id"), "id"),
        reader.Text(reader.Required(properties, entry, "variant"), "variant"),
        reader.Text(reader.Required(properties, entry, "metric"), "metric"),
        reader.Number(reader.Required(properties, entry, "maxRegressionPercent"), "maxRegressionPercent"));
      if (budgets.Any(other => (other.Id, other.Variant, other.Metric) == (budget.Id, budget.Variant, budget.Metric)))
        throw reader.Error(entry, $"the budget of '{budget.Id}/{budget.Variant}' {budget.Metric} is given more than once");
      budgets.Add(budget);
    }

    return new(file, fields.TryGetValue("maxRelativeMad", out var mad) ? reader.Number(mad, "maxRelativeMad") : null, budgets);
  }

}
