using NUnit.Framework;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>Ends what the tests of the namespace share for the run: the node workers.</summary>
[SetUpFixture]
public sealed class ToolchainRun {

  /// <summary>Stops every node worker the tests started.</summary>
  [OneTimeTearDown]
  public void StopWorkers() => Toolchain.StopWorkers();

}
