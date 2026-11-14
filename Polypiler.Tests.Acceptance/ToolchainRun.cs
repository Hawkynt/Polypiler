using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Acceptance;

/// <summary>Ends what the tests of the assembly share for the run: the node workers and the scenario workspaces.</summary>
[SetUpFixture]
public sealed class ToolchainRun {

  /// <summary>Stops every node worker the tests started and checks that no scenario left its workspace behind.</summary>
  [OneTimeTearDown]
  public void EndTheRun() {
    Toolchain.StopWorkers();
    var left = ScenarioWorkspace.Created.Where(Directory.Exists).ToList();
    Assert.That(left, Is.Empty, $"{left.Count} scenario workspaces were left behind");
  }

}
