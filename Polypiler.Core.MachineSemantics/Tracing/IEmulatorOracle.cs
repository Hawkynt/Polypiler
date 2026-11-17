using System.Collections.Immutable;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>
/// An emulator of a real CPU that runs a machine-code program on the vectors of a machine vector file
/// and reports what each run did (PRD §21.4). Every emulator oracle of the native and retro areas
/// implements this one port.
/// </summary>
public interface IEmulatorOracle {

  /// <summary>The machine profile the emulator runs, for example <c>x86-32</c>.</summary>
  string Isa { get; }

  /// <summary>Runs the program once per vector.</summary>
  /// <param name="vectors">The vectors and what the fixture observes.</param>
  /// <param name="program">The program.</param>
  /// <param name="sink">Receives what went wrong.</param>
  /// <returns>One trace per vector, in vector order.</returns>
  ImmutableArray<ObservableTrace> Run(MachineVectorFile vectors, EmulatorProgram program, IDiagnosticSink sink);

}
