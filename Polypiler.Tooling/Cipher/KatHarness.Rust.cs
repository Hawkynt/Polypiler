using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Tooling.Cipher;

public sealed partial class KatHarness {

  /// <summary>
  /// Writes the Rust test crate around the translation (<see cref="KatRustCrate"/>), runs
  /// <c>cargo test</c> on it offline and reads which vectors passed. A translation the tests cannot
  /// reach - no registry, no name of an algorithm, a file the reader does not take - is translated and
  /// not run; a test crate that does not build names the compiler's first error.
  /// </summary>
  private KatTargetResult RunRust(string folder, KatTarget target, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    IReadOnlyList<string> ids;
    try {
      ids = KatRustCrate.Write(folder, checkable);
    } catch (Exception exception) when (exception is KatRustException or IOException or FormatException or UnauthorizedAccessException) {
      return new(target.Name, KatStage.Translated, $"the tests cannot reach the translation: {exception.Message}", 0, []);
    }

    var run = runner.Run(["cargo", "test", "--offline", "--no-fail-fast", "--manifest-path", Path.Combine(folder, "Cargo.toml"), "--test", "kat"], TestTimeout);
    var outcomes = KatRustCrate.ReadTests(run.StandardOutput);
    if (outcomes.Count == 0)
      return new(target.Name, KatStage.BuildFailed, run.TimedOut ? "the build and the tests timed out" : KatRustCrate.FirstError(run.StandardError + "\n" + run.StandardOutput), 0, []);

    var passed = 0;
    var failures = new List<string>();
    foreach (var (id, index) in ids.Select((id, index) => (id, index))) {
      if (!outcomes.TryGetValue(KatRustCrate.TestName(index), out var outcome))
        failures.Add($"{id}: did not run");
      else if (outcome.Passed)
        ++passed;
      else
        failures.Add($"{id}: {outcome.Message}");
    }

    return new(target.Name, KatStage.Ran, null, passed, failures);
  }

}
