using System;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The diagnostics of MachineIR, its profiles and its verifier, as <c>spec/diagnostics.yaml</c>
/// registers them in the machine block <c>PLY4000</c>–<c>PLY4099</c>.
/// </summary>
public static class MachineDiagnostics {

  /// <summary><c>PLY4000</c>: a register or flag the profile does not declare. Arguments: the name, the profile.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownStorage = new("PLY4000", DiagnosticSeverity.Error, ExitCode.InvalidSource, "'{0}' is not a register or flag of machine profile '{1}'", "machine.ir.storage");

  /// <summary><c>PLY4001</c>: an access whose type is not as wide as its register. Arguments: the instruction, its type, the register, its width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor WidthMismatch = new("PLY4001", DiagnosticSeverity.Error, ExitCode.InvalidSource, "'{0}' has type {1} but register '{2}' is {3} bits wide", "machine.ir.width");

  /// <summary><c>PLY4002</c>: a flag read that only an undefining instruction reaches. Arguments: the flag, where it is read, where it was undefined.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UndefinedFlagRead = new("PLY4002", DiagnosticSeverity.Error, ExitCode.InvalidSource, "flag '{0}' is read at {1} but is undefined after {2}", "machine.ir.undefined-flag");

  /// <summary>
  /// <c>PLY4003</c>: machine operations without a profile (arguments: the function, the module), or,
  /// through <see cref="InvalidProfile"/>, a profile file that breaks a rule.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoProfile = new("PLY4003", DiagnosticSeverity.Error, ExitCode.InvalidSource, "'@{0}' uses machine operations but module '{1}' declares no machine profile", "machine.ir.profile");

  /// <summary><c>PLY4004</c>: a sub-register access left in a function normalized to root registers. Arguments: the instruction, the function.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor AfterNormalization = new("PLY4004", DiagnosticSeverity.Error, ExitCode.InvalidSource, "'{0}' is not allowed in '@{1}' after normalization to root registers", "machine.ir.normalized");

  /// <summary><c>PLY4005</c>: MachineIR that is not well formed. Arguments: the function, the problem.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Malformed = new("PLY4005", DiagnosticSeverity.Error, ExitCode.InvalidSource, "'@{0}' is not well-formed MachineIR: {1}", "machine.ir.well-formed");

  /// <summary><c>PLY4006</c>: MachineIR text that does not parse. Arguments: the problem.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Syntax = new("PLY4006", DiagnosticSeverity.Error, ExitCode.InvalidSource, "MachineIR text is not valid: {0}", "machine.ir.syntax");

  /// <summary>The second form of <c>PLY4003</c>: a profile file that breaks a rule.</summary>
  /// <param name="profile">The profile's ID, or the file when the ID is not known yet.</param>
  /// <param name="reason">The broken rule.</param>
  /// <param name="file">The file, or <see langword="null"/>.</param>
  /// <returns>The diagnostic.</returns>
  public static Diagnostic InvalidProfile(string profile, string reason, string? file = null) {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(reason);
    return NoProfile.At($"machine profile '{profile}': {reason}", file);
  }

}

/// <summary>An input to MachineIR that cannot be accepted, with the diagnostic that says why.</summary>
/// <param name="diagnostic">The diagnostic.</param>
public sealed class MachineException(Diagnostic diagnostic) : Exception(diagnostic?.ToString()) {

  /// <summary>The diagnostic.</summary>
  public Diagnostic Diagnostic { get; } = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));

}
