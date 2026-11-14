using System;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// The diagnostics of <c>polypiler-tooling spec validate</c> (block <c>PLY0040</c>–<c>PLY0047</c> of
/// <c>spec/diagnostics.yaml</c>), and the helpers a family uses to place one at a value of a document.
/// Every one is an error with the exit code <see cref="ExitCode.EnvironmentError"/>.
/// </summary>
public static class SpecDiagnostics {

  /// <summary><c>PLY0040</c>: <c>&lt;json pointer&gt;: &lt;schema keyword message&gt;</c>, for the metadata schema and a family's.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SchemaViolation = new("PLY0040", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}: {1}", "spec.schema");

  /// <summary><c>PLY0041</c>: a metadata rule the schema cannot state.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MetadataRule = new("PLY0041", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "spec.metadata");

  /// <summary><c>PLY0042</c>: <c>duplicate spec id '&lt;id&gt;', first defined in &lt;path&gt;</c>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DuplicateId = new("PLY0042", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "duplicate spec id '{0}', first defined in {1}", "spec.unique-id");

  /// <summary><c>PLY0043</c>: a test path or a spec ID that leads nowhere.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BrokenReference = new("PLY0043", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "spec.reference");

  /// <summary><c>PLY0044</c>: the lifecycle keys contradict each other.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Lifecycle = new("PLY0044", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "spec.lifecycle");

  /// <summary><c>PLY0046</c>: <c>&lt;reason&gt; (rule: &lt;rule&gt;)</c>, a rule of a family that its schema cannot state.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor FamilyRule = new("PLY0046", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0} (rule: {1})", "spec.family");

  /// <summary><c>PLY0047</c>: a file, a schema or a spec root that cannot be read, or a family nothing knows.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unreadable = new("PLY0047", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "spec.readable");

  /// <summary>A diagnostic at the value a JSON pointer names in a document.</summary>
  /// <param name="descriptor">The registered code.</param>
  /// <param name="document">The document.</param>
  /// <param name="pointer">The JSON pointer of the value.</param>
  /// <param name="arguments">The values of the placeholders of the descriptor's message.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic At(DiagnosticDescriptor descriptor, SpecDocument document, string pointer, params object[] arguments) {
    ArgumentNullException.ThrowIfNull(document);
    return Diagnostic.Create(descriptor, document.RangeOf(pointer), arguments);
  }

  /// <summary>A <see cref="FamilyRule"/> diagnostic, <c>PLY0046: &lt;reason&gt; (rule: &lt;rule&gt;)</c>, at a value of a document.</summary>
  /// <param name="document">The document.</param>
  /// <param name="pointer">The JSON pointer of the offending value.</param>
  /// <param name="reason">What is wrong.</param>
  /// <param name="rule">The name of the family's rule, for example <c>layout</c>.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic Rule(SpecDocument document, string pointer, string reason, string rule) => At(FamilyRule, document, pointer, reason, rule);

}
