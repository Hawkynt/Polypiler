using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>A temporary directory laid out as a checkout: <c>tests/fixtures/fuzz/&lt;target&gt;/corpus/</c> and <c>artifacts/</c>.</summary>
public sealed class FuzzWorkspace : IDisposable {

  /// <summary>Creates an empty workspace.</summary>
  public FuzzWorkspace() => this.Root = Directory.CreateTempSubdirectory("polypiler-fuzz-").FullName;

  /// <summary>The workspace directory.</summary>
  public string Root { get; }

  /// <summary>The directory of every target's corpus.</summary>
  public string Fixtures => Path.Combine(this.Root, "tests", "fixtures", "fuzz");

  /// <summary>The directory the records go below.</summary>
  public string Artifacts => Path.Combine(this.Root, "artifacts");

  /// <summary>The directory of a target, holding <c>corpus/</c> and <c>regressions/</c>.</summary>
  public string TargetDirectory(string target) => Path.Combine(this.Fixtures, target);

  /// <summary>Writes corpus entries of a target and returns the target's directory.</summary>
  public string Corpus(string target, params (string Name, byte[] Content)[] entries) => this.Files(target, FuzzCorpus.CorpusFolder, entries);

  /// <summary>Writes regression inputs of a target and returns the target's directory.</summary>
  public string Regressions(string target, params (string Name, byte[] Content)[] entries) => this.Files(target, FuzzCorpus.RegressionsFolder, entries);

  private string Files(string target, string folder, IEnumerable<(string Name, byte[] Content)> entries) {
    var directory = Path.Combine(this.TargetDirectory(target), folder);
    Directory.CreateDirectory(directory);
    foreach (var (name, content) in entries)
      File.WriteAllBytes(Path.Combine(directory, name), content);
    return this.TargetDirectory(target);
  }

  /// <summary>An engine that runs targets in process and records into the workspace.</summary>
  public FuzzEngine InProcessEngine() => new(target => new InProcessExecutor(target), this.Artifacts);

  /// <summary>Removes the workspace.</summary>
  public void Dispose() => Directory.Delete(this.Root, true);

}
