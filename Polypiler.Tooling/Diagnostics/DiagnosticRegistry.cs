using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Polypiler.Core;

namespace Polypiler.Tooling.Diagnostics;

/// <summary>A range of codes an area allocates from.</summary>
/// <param name="First">The first code of the range.</param>
/// <param name="Last">The last code of the range, inclusive.</param>
/// <param name="Area">The area that owns the range.</param>
public sealed record DiagnosticBlock(string First, string Last, string Area) {

  /// <summary>Whether a code lies in the range.</summary>
  /// <param name="code">A well-formed code.</param>
  public bool Contains(string code) => string.CompareOrdinal(this.First, code) <= 0 && string.CompareOrdinal(code, this.Last) <= 0;

  /// <summary>The block as <c>PLY0001-PLY0099</c>.</summary>
  public override string ToString() => $"{this.First}-{this.Last}";

}

/// <summary>One code of the registry.</summary>
/// <param name="Code">The code.</param>
/// <param name="Severity">Its severity.</param>
/// <param name="ExitCode">The exit code of an error; <see langword="null"/> for a warning or an info.</param>
/// <param name="Title">A few words naming the diagnostic.</param>
/// <param name="Message">The message, with <c>&lt;placeholders&gt;</c>.</param>
/// <param name="Rule">The rule ID the descriptor carries, or <see langword="null"/> while none is fixed.</param>
/// <param name="Area">The area of the block the code lies in.</param>
/// <param name="Owner">The backlog ID of the issue that registered the code.</param>
/// <param name="Surfaces">Where the diagnostic is seen, or <see langword="null"/>.</param>
/// <param name="Strict">Whether <c>--strict</c> turns the warning into an error.</param>
/// <param name="Reserved">Whether the code is allocated but not yet declared by anything.</param>
/// <param name="Retired">Whether the code is no longer emitted.</param>
public sealed record DiagnosticEntry(
  string Code,
  DiagnosticSeverity Severity,
  ExitCode? ExitCode,
  string Title,
  string Message,
  string? Rule,
  string Area,
  string Owner,
  string? Surfaces,
  bool Strict,
  bool Reserved,
  bool Retired);

/// <summary>
/// The diagnostics registry <c>spec/diagnostics.yaml</c> as <see cref="DiagnosticRegistryValidator"/>
/// read it: its blocks, its codes and what is wrong with it.
/// </summary>
public sealed class DiagnosticRegistry {

  /// <summary>The registry's path relative to the repository root.</summary>
  public const string RepositoryPath = "spec/diagnostics.yaml";

  private readonly Dictionary<string, DiagnosticEntry> _byCode;

  /// <summary>Creates a registry from what was read.</summary>
  /// <param name="file">The file as it was named.</param>
  /// <param name="blocks">The blocks, in file order.</param>
  /// <param name="codes">The codes that could be read, in file order.</param>
  /// <param name="problems">What is wrong with the file, each a <c>PLY0045</c> diagnostic.</param>
  public DiagnosticRegistry(string file, IReadOnlyList<DiagnosticBlock> blocks, IReadOnlyList<DiagnosticEntry> codes, IReadOnlyList<Diagnostic> problems) {
    this.File = file;
    this.Blocks = blocks;
    this.Codes = codes;
    this.Problems = problems;
    this._byCode = new(StringComparer.Ordinal);
    foreach (var entry in codes)
      this._byCode.TryAdd(entry.Code, entry);
  }

  /// <summary>The file as it was named.</summary>
  public string File { get; }

  /// <summary>The blocks, in file order.</summary>
  public IReadOnlyList<DiagnosticBlock> Blocks { get; }

  /// <summary>The codes, in file order.</summary>
  public IReadOnlyList<DiagnosticEntry> Codes { get; }

  /// <summary>What is wrong with the file; empty when it is valid.</summary>
  public IReadOnlyList<Diagnostic> Problems { get; }

  /// <summary>The entry of a code, or <see langword="null"/> when the registry has none.</summary>
  /// <param name="code">The code.</param>
  public DiagnosticEntry? Find(string code) => this._byCode.GetValueOrDefault(code);

  /// <summary>
  /// Compares the registry with the descriptors the code declares: every declared code must be
  /// registered with the same severity, exit code and rule and be neither reserved nor retired, and
  /// every registered code that is neither reserved nor retired must be declared.
  /// </summary>
  /// <param name="declared">The descriptors, as <see cref="Declared"/> finds them.</param>
  /// <returns>One line per difference, in code order; empty when the two agree.</returns>
  public IReadOnlyList<string> Compare(IEnumerable<DiagnosticDescriptor> declared) {
    var problems = new List<string>();
    var byCode = declared.GroupBy(descriptor => descriptor.Code, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Distinct().ToList(), StringComparer.Ordinal);
    foreach (var code in byCode.Keys.Union(this._byCode.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
      var entry = this.Find(code);
      if (!byCode.TryGetValue(code, out var descriptors)) {
        if (entry is { Reserved: false, Retired: false })
          problems.Add($"{code} is registered but nothing declares it; mark it 'reserved' or 'retired'");
        continue;
      }

      if (descriptors.Count > 1) {
        problems.Add($"{code} is declared {descriptors.Count} times with different severity, exit code, message or rule");
        continue;
      }

      var descriptor = descriptors[0];
      if (entry is null) {
        problems.Add($"{code} is declared in code but not registered in {this.File}");
        continue;
      }

      if (entry.Reserved)
        problems.Add($"{code} is declared in code but registered as reserved");
      if (entry.Retired)
        problems.Add($"{code} is declared in code but registered as retired");
      if (entry.Severity != descriptor.Severity)
        problems.Add($"{code}: severity {Name(descriptor.Severity)} in code, {Name(entry.Severity)} in the registry");
      if (entry.ExitCode != descriptor.ExitCode)
        problems.Add($"{code}: exit code {Name(descriptor.ExitCode)} in code, {Name(entry.ExitCode)} in the registry");
      if (entry.Rule != descriptor.Rule)
        problems.Add($"{code}: rule '{descriptor.Rule}' in code, '{entry.Rule}' in the registry");
    }

    return problems;
  }

  /// <summary>
  /// Every descriptor the assemblies declare: the value of each <see langword="static"/> field of type
  /// <see cref="DiagnosticDescriptor"/> marked <see cref="DiagnosticAttribute"/>, public or not.
  /// </summary>
  /// <param name="assemblies">The assemblies to search.</param>
  /// <returns>The descriptors, in the order found.</returns>
  public static IReadOnlyList<DiagnosticDescriptor> Declared(IEnumerable<Assembly> assemblies)
    => [.. assemblies
      .SelectMany(assembly => assembly.GetTypes())
      .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
      .Where(field => field.FieldType == typeof(DiagnosticDescriptor) && field.IsDefined(typeof(DiagnosticAttribute), false))
      .Select(field => (DiagnosticDescriptor)field.GetValue(null)!)];

  private static string Name(DiagnosticSeverity severity) => DiagnosticRenderer.SeverityName(severity);

  private static string Name(ExitCode? exitCode) => exitCode?.ToString() ?? "none";

}
