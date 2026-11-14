using Polypiler.Cli;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// The commands a scenario runs <c>polypiler</c> with, when not the real ones: one instance per
/// scenario, empty unless a step of the scenario sets <see cref="Registry"/>.
/// </summary>
public sealed class TestCommandSet {

  /// <summary>
  /// The registry <see cref="PolypilerRunner"/> runs every command line of the scenario with, in
  /// process and in the scenario's workspace; <see langword="null"/> for the real commands.
  /// </summary>
  public CommandRegistry? Registry { get; set; }

}
