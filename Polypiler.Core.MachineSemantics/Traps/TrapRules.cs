using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.MachineSemantics.Traps;

/// <summary>
/// The rules of <see cref="MachineOpcode.MachineTrap"/>: a vector inside the profile's interrupt vectors
/// and present where the kind needs one (<c>PLY4025</c>), registers of a service contract declared by
/// the profile (<c>PLY4000</c>), and the warning <c>PLY4026</c> for a software interrupt without a
/// contract, whose effect is then the conservative summary of <see cref="MachineEffects"/>.
/// </summary>
public static class TrapRules {

  /// <summary><c>PLY4025</c>: a vector outside the profile's range (arguments: vector, last vector, profile), or a missing one (through <see cref="DiagnosticDescriptor.At"/>).</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BadVector = new("PLY4025", DiagnosticSeverity.Error, ExitCode.InvalidSource, "interrupt vector {0} is outside 0..{1} for machine profile '{2}'", "machine.trap.vector");

  /// <summary><c>PLY4026</c>: a software interrupt without a service contract. Arguments: the vector, where.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoContract = new("PLY4026", DiagnosticSeverity.Warning, null, "software interrupt {0} at {1} has no service contract; all registers and unknown memory are assumed read and written", "machine.trap.contract");

  /// <summary>Checks one trap.</summary>
  /// <param name="trap">The trap's descriptor.</param>
  /// <param name="profile">The machine profile.</param>
  /// <param name="where">Where the trap stands, for the diagnostics.</param>
  /// <returns>What is wrong or worth a warning.</returns>
  public static IEnumerable<Diagnostic> Check(TrapDescriptor trap, IsaProfile profile, string where) {
    System.ArgumentNullException.ThrowIfNull(trap);
    System.ArgumentNullException.ThrowIfNull(profile);
    if (trap.Vector is { } vector) {
      if (vector < 0 || vector >= profile.InterruptVectors)
        yield return Diagnostic.Create(BadVector, null, vector, profile.InterruptVectors is { } count ? count - 1 : "unbounded", profile.Id);
    } else if (trap.Kind == MachineTrapKind.SoftwareInterrupt) {
      yield return BadVector.At($"trap kind '{TrapDescriptor.Name(trap.Kind)}' requires a vector");
    }

    if (trap.Service is { } service) {
      foreach (var register in service.RegistersRead.AddRange(service.RegistersWritten))
        if (profile.Register(register) is null)
          yield return Diagnostic.Create(MachineDiagnostics.UnknownStorage, null, register, profile.Id);
    } else if (trap.Kind == MachineTrapKind.SoftwareInterrupt && trap.Resumes && trap.Vector is { } number) {
      yield return Diagnostic.Create(NoContract, null, MachineSyntax.Number((ulong)number), where);
    }
  }

}

/// <summary>Links the arithmetic faults of a CPU to the trap kinds of the canonical IR.</summary>
public static class TrapLabels {

  /// <summary>
  /// The canonical trap of a machine trap kind: <c>divide-error</c> is <see cref="IrTrap.DivideByZero"/>,
  /// <c>overflow-trap</c> is <see cref="IrTrap.IntegerOverflow"/>; every other kind has none and stays a
  /// <see cref="MachineOpcode.MachineTrap"/>. A lifter emits a trapping division (<c>UDiv</c>, <c>SDiv</c>)
  /// for an instruction that faults only for some operands, and a <c>MachineTrap</c> only for one that
  /// always does.
  /// </summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The canonical trap, or <see langword="null"/>.</returns>
  public static IrTrap? For(MachineTrapKind kind) => kind switch {
    MachineTrapKind.DivideError => IrTrap.DivideByZero,
    MachineTrapKind.OverflowTrap => IrTrap.IntegerOverflow,
    _ => null
  };

}
