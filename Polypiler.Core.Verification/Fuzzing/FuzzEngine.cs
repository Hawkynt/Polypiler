using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>A finding and where its record went.</summary>
/// <param name="Finding">The finding.</param>
/// <param name="BinaryPath">The minimized input, <c>&lt;artifacts&gt;/fuzz/&lt;target&gt;/&lt;id&gt;.bin</c>.</param>
/// <param name="JsonPath">The record beside it, <c>&lt;id&gt;.json</c>.</param>
/// <param name="WriteError">Why the record could not be written; <see langword="null"/> when it was.</param>
public sealed record FuzzFindingRecord(Finding Finding, string BinaryPath, string JsonPath, string? WriteError);

/// <summary>What a fuzz run did.</summary>
/// <param name="Target">The ID of the target.</param>
/// <param name="Seed">The seed of the run.</param>
/// <param name="Executed">How many inputs were executed, minimization not counted.</param>
/// <param name="Records">The de-duplicated findings in the order they were first seen, with their records.</param>
/// <param name="WorkerStarts">How many times the executor started the code that runs the target.</param>
/// <param name="Elapsed">How long the run took.</param>
public sealed record FuzzReport(string Target, ulong Seed, long Executed, IReadOnlyList<FuzzFindingRecord> Records, int WorkerStarts, TimeSpan Elapsed) {

  /// <summary>The findings in the order they were first seen.</summary>
  public IReadOnlyList<Finding> Findings => [.. this.Records.Select(record => record.Finding)];

  /// <summary>Executed inputs per second; recorded, not gated.</summary>
  public double ExecutionsPerSecond => this.Elapsed > TimeSpan.Zero ? this.Executed / this.Elapsed.TotalSeconds : 0;

}

/// <summary>
/// The deterministic fuzzing engine. Iteration <c>i &lt; n</c> (n entries in the corpus) executes entry
/// <c>i</c> unchanged; iteration <c>i ≥ n</c> mutates entry <c>(i − n) mod n</c> with
/// <see cref="Mutators.Mutate"/> drawing from <c>SeededRandom(seed + i)</c>. Every input is cut to
/// <see cref="FuzzBudget.MaxInputLength"/>. The first input with a new signature becomes a finding: it
/// is minimized with the same executor and recorded under <c>&lt;artifacts&gt;/fuzz/&lt;target&gt;/</c>.
/// </summary>
/// <param name="executors">Creates the executor of a run; it is disposed at the end of the run.</param>
/// <param name="artifactsDirectory">The directory the records go below.</param>
/// <param name="engine">The name of the engine the records carry, e.g. <c>inprocess</c> or <c>afl</c>.</param>
public sealed class FuzzEngine(Func<IFuzzTarget, IFuzzExecutor> executors, string artifactsDirectory, string engine = FuzzEngine.InProcess) {

  /// <summary>The name of the deterministic engine.</summary>
  public const string InProcess = "inprocess";

  /// <summary>The name of the engine.</summary>
  public string Engine => engine;

  /// <summary>Fuzzes a target from its corpus.</summary>
  /// <param name="target">The target.</param>
  /// <param name="corpusDirectory">The target's directory, holding <c>corpus/</c> and <c>regressions/</c>.</param>
  /// <param name="seed">The seed of the mutations.</param>
  /// <param name="iterations">How many inputs to execute; 0 executes none.</param>
  /// <param name="budget">The limits of one input.</param>
  /// <exception cref="FuzzCorpusException">The corpus directory is missing or empty.</exception>
  public FuzzReport Run(IFuzzTarget target, string corpusDirectory, ulong seed, long iterations, FuzzBudget budget) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(budget);
    ArgumentOutOfRangeException.ThrowIfNegative(iterations);
    var corpus = FuzzCorpus.Load(corpusDirectory);
    var contents = corpus.Select(entry => entry.Content).ToArray();
    return this.Execute(target, seed, budget, iterations, iteration => (Input(contents, seed, iteration, budget.MaxInputLength), corpus[EntryOf(contents.Length, iteration)]));
  }

  /// <summary>
  /// Executes inputs found elsewhere — by AFL++, or given to <c>fuzz minimize</c> — and treats them as
  /// a run would: iteration <c>i</c> is input <c>i</c>, cut to the budget's length.
  /// </summary>
  public FuzzReport Confirm(IFuzzTarget target, IReadOnlyList<byte[]> inputs, ulong seed, FuzzBudget budget) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(budget);
    return this.Execute(target, seed, budget, inputs.Count, iteration => (Cut(inputs[(int)iteration], budget.MaxInputLength), null));
  }

  /// <summary>The input of an iteration; depends only on the corpus, the seed and the iteration.</summary>
  /// <param name="corpus">The contents of the corpus entries, in corpus order; at least one.</param>
  /// <param name="seed">The seed of the run.</param>
  /// <param name="iteration">The iteration, from 0.</param>
  /// <param name="maxLength">The longest input; longer ones are cut.</param>
  public static byte[] Input(IReadOnlyList<byte[]> corpus, ulong seed, long iteration, int maxLength) {
    ArgumentNullException.ThrowIfNull(corpus);
    ArgumentOutOfRangeException.ThrowIfZero(corpus.Count);
    ArgumentOutOfRangeException.ThrowIfNegative(iteration);
    var entry = corpus[EntryOf(corpus.Count, iteration)];
    if (iteration < corpus.Count)
      return Cut((byte[])entry.Clone(), maxLength);

    return Cut(Mutators.Mutate(entry, new SeededRandom(seed + (ulong)iteration), corpus), maxLength);
  }

  private static int EntryOf(int count, long iteration) => (int)(iteration < count ? iteration : (iteration - count) % count);

  private static byte[] Cut(byte[] input, int maxLength) => input.Length > maxLength ? input[..maxLength] : input;

  private FuzzReport Execute(IFuzzTarget target, ulong seed, FuzzBudget budget, long iterations, Func<long, (byte[] Input, FuzzCorpusEntry? Entry)> inputOf) {
    var started = Stopwatch.GetTimestamp();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var records = new List<FuzzFindingRecord>();
    using var executor = executors(target);
    for (var iteration = 0L; iteration < iterations; ++iteration) {
      var (input, entry) = inputOf(iteration);
      var outcome = executor.Execute(input, budget);
      if (!outcome.IsFinding || !seen.Add(outcome.Signature))
        continue;

      var minimized = Minimizer.Minimize(input, candidate => executor.Execute(candidate, budget) is { IsFinding: true } again && again.Signature == outcome.Signature);
      var finding = new Finding(target.Id, outcome.Kind!.Value, outcome.Signature, input, minimized, seed, iteration, entry?.Name, entry?.Sha256);
      records.Add(this.Record(finding, entry?.Upstream));
    }

    return new(target.Id, seed, iterations, records, executor.Starts, Stopwatch.GetElapsedTime(started));
  }

  /// <summary>Writes the minimized input and its JSON record; a write that fails is kept as the record's error.</summary>
  private FuzzFindingRecord Record(Finding finding, string? upstream) {
    var directory = Path.Combine(artifactsDirectory, "fuzz", finding.Target);
    var id = FuzzCorpus.Sha256(finding.Minimized)[..16];
    var binary = Path.Combine(directory, id + ".bin");
    var json = Path.Combine(directory, id + ".json");
    try {
      Directory.CreateDirectory(directory);
      File.WriteAllBytes(binary, finding.Minimized);
      File.WriteAllBytes(json, this.Json(finding, upstream));
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(finding, binary, json, $"the output directory '{directory}' cannot be written: {exception.Message}");
    }

    return new(finding, binary, json, null);
  }

  private byte[] Json(Finding finding, string? upstream) {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteString("target", finding.Target);
      writer.WriteString("kind", finding.Kind.ToString());
      writer.WriteString("signature", finding.Signature);
      writer.WriteNumber("seed", finding.Seed);
      writer.WriteNumber("iteration", finding.Iteration);
      writer.WriteString("engine", engine);
      writer.WriteString("originalSha256", FuzzCorpus.Sha256(finding.Input));
      writer.WriteNumber("minimizedLength", finding.Minimized.Length);
      writer.WriteString("corpusEntry", finding.CorpusEntry);
      writer.WriteString("corpusEntrySha256", finding.CorpusEntrySha256);
      writer.WriteString("upstream", upstream);
      writer.WriteEndObject();
    }

    buffer.WriteByte((byte)'\n');
    return buffer.ToArray();
  }

}
