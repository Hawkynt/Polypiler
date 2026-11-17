using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The diagnostics of the pipeline options <c>--opt</c>, <c>--disable-opt</c>, <c>--assume</c> and
/// <c>--strict</c> (ADR 0004, <c>docs/cli/pipeline-options.md</c>), with the texts they take.
/// </summary>
public static class PipelineOptionDiagnostics {

  /// <summary>The rule every diagnostic of the pipeline options enforces.</summary>
  public const string Rule = "adr-0004.pipeline-options";

  /// <summary><c>PLY0007</c>: the text of an <c>--assume</c> is no assumption; the text and the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvalidAssumption = new("PLY0007", DiagnosticSeverity.Error, ExitCode.UsageError, "'{0}' is not a valid assumption: {1}", Rule);

  /// <summary><c>PLY0008</c>: an item of <c>--disable-opt</c> is no optimization the pipeline has; the message is <see cref="NotAnId"/> or <see cref="UnknownId"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownOptimization = new("PLY0008", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", Rule);

  /// <summary><c>PLY0009</c>: an assumption is accepted without proof; <c>--strict</c> turns it into <c>PLY0010</c>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TrustedAssumption = new("PLY0009", DiagnosticSeverity.Warning, null, "assumption '{0}' is trusted without proof", Rule);

  /// <summary><c>PLY0010</c>: a warning that <c>--strict</c> refuses; the original code and message.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor RefusedByStrict = new("PLY0010", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "strict mode: {0} {1}", Rule);

  /// <summary><c>PLY0012</c>: the value of <c>--opt</c> is no optimization level.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownLevel = new("PLY0012", DiagnosticSeverity.Error, ExitCode.UsageError, "'{0}' is not an optimization level; expected one of: none, balanced, speed, size, memory", Rule);

  /// <summary>An item of <c>--disable-opt</c> that is not <c>O</c> and four digits.</summary>
  public const string NotAnId = "'{0}' is not an optimization ID (expected O followed by four digits)";

  /// <summary>An optimization ID the pipeline does not have.</summary>
  public const string UnknownId = "unknown optimization '{0}'";

  /// <summary>An assumption in the contract form <c>&lt;target&gt;: &lt;contract&gt;</c> where no contract parser is registered.</summary>
  public const string ContractsUnavailable = "contract assumptions are not available in this build";

}
