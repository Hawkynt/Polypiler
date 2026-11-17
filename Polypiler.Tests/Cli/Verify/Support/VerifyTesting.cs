using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Cli.Verify;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Cli.Verify.Support;

/// <summary>
/// A runner that stands in for node: <c>--version</c> prints a version, <c>--check</c> passes, the
/// driver computes Adler-32 of each <c>bytes</c> argument as the fixture module does, and a program
/// computes it of the hexadecimal input and prints it big-endian. <see cref="Answer"/> replaces any of
/// these; every plan is recorded.
/// </summary>
internal sealed class ScriptedNode : ICrossLanguageRunner {

  /// <summary>The plans run, in order.</summary>
  public List<RunPlan> Plans { get; } = [];

  /// <summary>The answer to a plan, or <see langword="null"/> for the default.</summary>
  public Func<RunPlan, RunResult?> Answer { get; set; } = _ => null;

  /// <summary>The plans of a kind: <c>--version</c>, <c>--check</c>, the driver, or a program.</summary>
  public IEnumerable<RunPlan> Of(string first) => this.Plans.Where(plan => plan.Execute.Arguments[0] == first);

  public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
    this.Plans.Add(plan);
    return Task.FromResult(this.Answer(plan) ?? Default(plan));
  }

  private static RunResult Default(RunPlan plan) {
    var arguments = plan.Execute.Arguments;
    var stdin = Encoding.UTF8.GetString(plan.Execute.Stdin.Span);
    return arguments[0] switch {
      "--version" => Completed(0, "v22.1.0\n"),
      "--check" => Completed(0, ""),
      JavaScriptDriver.FileName => Completed(0, string.Join('\n', JsonNode.Parse(stdin)!.AsArray().Select(call => Number(Adler32(Convert.FromHexString(call![0]!["v"]!.GetValue<string>()))))) + "\n"),
      _ => Completed(0, Convert.ToHexString(BigEndian(Adler32(Convert.FromHexString(stdin)))) + "\n")
    };
  }

  /// <summary>A run that completed with an exit code and an output.</summary>
  public static RunResult Completed(int exitCode, string stdout, string stderr = "")
    => new(RunOutcome.Completed, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []);

  /// <summary>A run that ended otherwise, with its diagnostics.</summary>
  public static RunResult Ended(RunOutcome outcome, params Diagnostic[] diagnostics)
    => new(outcome, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

  /// <summary>The driver's line for a number.</summary>
  public static string Number(double value) => $"{{\"t\":\"f\",\"v\":\"{BitConverter.DoubleToUInt64Bits(value):x16}\"}}";

  /// <summary>Adler-32 of RFC 1950.</summary>
  public static uint Adler32(byte[] bytes) {
    uint a = 1;
    uint b = 0;
    foreach (var value in bytes) {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }

    return (b << 16) | a;
  }

  private static byte[] BigEndian(uint value) {
    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    return bytes;
  }

}

/// <summary>A copy of <c>tests/fixtures/cli/verify</c> in a temporary folder, the working directory of the verifications of one test.</summary>
internal sealed class VerifyWorkspace : IDisposable {

  /// <summary>The fixtures in the repository.</summary>
  public static string Fixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "cli", "verify");

  /// <summary>The vector file of the four published vectors, relative to the workspace.</summary>
  public const string Vectors = "vectors/adler32.vectors.json";

  /// <summary>The vector file with the wrong abc vector, relative to the workspace.</summary>
  public const string WrongVectors = "vectors/adler32.wrong.vectors.json";

  /// <summary>Copies the fixtures: the generated directory as <c>copy</c>, the vector files under <c>vectors</c>.</summary>
  public VerifyWorkspace() {
    this.Root = Path.Combine(Path.GetTempPath(), "polypiler-verify-" + Guid.NewGuid().ToString("N"));
    foreach (var file in Directory.EnumerateFiles(Fixtures, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(Fixtures, file);
      var target = Path.Combine(this.Root, relative.StartsWith("adler32-js", StringComparison.Ordinal) ? Path.Combine("copy", relative["adler32-js".Length..].TrimStart(Path.DirectorySeparatorChar)) : relative.Contains(Path.DirectorySeparatorChar) ? relative : Path.Combine("vectors", relative));
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(target, File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal));
    }
  }

  /// <summary>The folder.</summary>
  public string Root { get; }

  /// <summary>The generated directory.</summary>
  public string Copy => Path.Combine(this.Root, "copy");

  /// <summary>Writes a file of the workspace.</summary>
  public string Write(string relative, string text) {
    var path = Path.Combine(this.Root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
  }

  /// <summary>The manifest of the copy.</summary>
  public Manifest Manifest {
    get {
      var bag = new DiagnosticBag();
      return ManifestReader.Read(Path.Combine(this.Copy, Manifest.FileName), bag) ?? throw new InvalidOperationException(string.Join("; ", bag.Items));
    }
    set => ManifestWriter.Write(value, this.Copy);
  }

  /// <summary>The SHA-256 of every file of a directory, by path.</summary>
  public static Dictionary<string, string> Hashes(string directory)
    => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(directory, file), file => ManifestWriter.Sha256(File.ReadAllBytes(file)));

  public void Dispose() {
    if (Directory.Exists(this.Root))
      Directory.Delete(this.Root, true);
  }

}
