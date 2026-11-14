using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Ci;

/// <summary>
/// The diagnostics of <c>polypiler-tooling ci</c>. Each one is an error with the exit code
/// <see cref="ExitCode.EnvironmentError"/>; a file that cannot be read is <c>PLY0004</c>, as for the
/// toolchain lock.
/// </summary>
public static class CiDiagnostics {

  /// <summary><c>PLY0075</c>: a CI layer definition that is invalid or inconsistent with the workflow.</summary>
  public const string InconsistentCode = "PLY0075";

  /// <summary><c>PLY0076</c>: a test or a category that no layer runs.</summary>
  public const string UnrunCode = "PLY0076";

  /// <summary>The registered diagnostic of <see cref="InconsistentCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Inconsistent = new(InconsistentCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "ci.layer");

  /// <summary>The registered diagnostic of <see cref="UnrunCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unrun = new(UnrunCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "ci.unrun");

  /// <summary>A <c>PLY0075</c> diagnostic.</summary>
  /// <param name="message">What is wrong.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic InconsistentWith(string message) => Diagnostic.Create(Inconsistent, null, message);

  /// <summary>A <c>PLY0076</c> diagnostic.</summary>
  /// <param name="message">What no layer runs.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic UnrunWith(string message) => Diagnostic.Create(Unrun, null, message);

  /// <summary>A <c>PLY0004</c> diagnostic for a file the command needs and cannot read.</summary>
  /// <param name="path">The file, as the command line or the repository names it.</param>
  /// <param name="reason">Why it cannot be read.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic Unreadable(string path, string reason) => Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, $"'{path}' cannot be read: {reason}");

  /// <summary>The line a diagnostic is reported as: <c>polypiler-tooling: error &lt;code&gt;: &lt;message&gt;</c>.</summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <returns>The line, without a line break.</returns>
  public static string Render(Diagnostic diagnostic) => $"{Program.Name}: error {diagnostic.Code}: {diagnostic.Message}";

}
