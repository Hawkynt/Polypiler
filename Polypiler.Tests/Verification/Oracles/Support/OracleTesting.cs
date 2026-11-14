using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Verification.Oracles.Support;

/// <summary>What the oracle tests share: the Adler-32 fixtures, real node runs and hand-made oracles.</summary>
internal static class OracleTesting {

  /// <summary>The toolchain the fixture programs run on.</summary>
  public const string Node = "node";

  /// <summary>The fixtures of the framework.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "verify", "oracle");

  /// <summary>The published Adler-32 vectors.</summary>
  public static string AdlerVectors { get; } = Path.Combine(FixtureFolder, "adler32.vectors.json");

  /// <summary>A fixture file by name.</summary>
  public static string Fixture(string name) => Path.Combine(FixtureFolder, name);

  /// <summary>A program of the fixtures run by node in the sandbox of this host, trusted: it is part of the repository.</summary>
  public static ToolchainProgram NodeProgram(string path, RunLimits? limits = null)
    => new(new CrossLanguageRunner(RunnerTesting.Host, RunnerTesting.Toolchains), Node, path, limits ?? RunLimits.Default, TrustLevel.Trusted);

  /// <summary>Vectors with the given names and empty inputs.</summary>
  public static TestVector[] Named(params string[] names) => [.. names.Select(name => new TestVector(name, [], null))];

  /// <summary>An f64 as its eight little-endian bytes.</summary>
  public static byte[] F64(ulong bits) {
    var bytes = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
    return bytes;
  }

  /// <summary>An f32 as its four little-endian bytes.</summary>
  public static byte[] F32(uint bits) {
    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, bits);
    return bytes;
  }

  /// <summary>Compares one reference output with one actual output.</summary>
  public static VectorOutcome CompareOne(byte[] reference, byte[] actual, ExactnessPolicy policy, string name = "v")
    => DifferentialComparer.Compare(Named(name), [reference], [actual], policy).Outcomes[0];

  /// <summary>An oracle whose answer is given.</summary>
  public sealed class FixedOracle(OracleResult result, string producer = "test", string? referenceFile = null) : IVerificationOracle {

    public string Id => "fixed";

    public OracleKind Kind => OracleKind.PublishedVectors;

    public string Producer => producer;

    public string? ReferenceFile => referenceFile;

    public Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) => Task.FromResult(result);

  }

  /// <summary>A runner that answers every plan with the next of the given results and records the plans.</summary>
  public sealed class ScriptedRunner(params RunResult[] results) : ICrossLanguageRunner {

    private int _next;

    public List<RunPlan> Plans { get; } = [];

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plans.Add(plan);
      return Task.FromResult(results[Math.Min(this._next++, results.Length - 1)]);
    }

  }

  /// <summary>A run that ended with the given outcome, exit code and output.</summary>
  public static RunResult Run(RunOutcome outcome, int exitCode, string stdout = "", string stderr = "", params Polypiler.Core.Diagnostic[] diagnostics)
    => new(outcome, exitCode, System.Text.Encoding.UTF8.GetBytes(stdout), System.Text.Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

}
