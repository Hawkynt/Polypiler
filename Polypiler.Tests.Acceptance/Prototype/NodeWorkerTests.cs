using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The <see cref="NodeWorker"/>: it computes what a <c>node</c> started for the calls computes, and a
/// module that crashes it, hangs it, answers outside its protocol or throws fails only its own
/// request - the worker is replaced and the next request passes.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class NodeWorkerTests {

  private static readonly PrototypeCall[] _calls = [new("f", [20]), new("f", [-3])];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Writes a module of the test's own and returns its URL.</summary>
  private string Module(string name, string text) {
    var path = Path.Combine(this._directory, name);
    File.WriteAllText(path, text);
    return new Uri(path).AbsoluteUri;
  }

  private string Good() => this.Module("good-" + Guid.NewGuid().ToString("N") + ".mjs", "export function f(x) { return x * 2 + 1; }\n");

  private static IReadOnlyList<string> Run(NodeWorker worker, string module)
    => worker.Outcomes(module, [.. _calls.Select(call => new object?[] { call.Method, call.Arguments, null })], "integer", "native");

  [Test]
  public void Module_ComputesItsOutcomes_RequestAfterRequest() {
    using var worker = NodeWorker.Start();
    Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
    Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
    Assert.That(worker.IsAlive, Is.True);
  }

  /// <summary>Each request loads the module afresh: what a module keeps between calls starts over.</summary>
  [TestCase("counter.mjs", "let n = 0; export function f(x) { return ++n; }\n")]
  [TestCase("counter.cjs", "let n = 0; module.exports = { f: x => ++n };\n")]
  public void EveryRequest_GetsAFreshInstanceOfTheModule(string name, string text) {
    using var worker = NodeWorker.Start();
    var module = this.Module(name, text);
    Assert.That(Run(worker, module), Is.EqualTo(new[] { "1", "2" }));
    Assert.That(Run(worker, module), Is.EqualTo(new[] { "1", "2" }));
  }

  /// <summary>A RangeError is an outcome, as it is to the driver.</summary>
  [Test]
  public void RangeError_IsATrap() {
    using var worker = NodeWorker.Start();
    var module = this.Module("trap.mjs", "export function f(x) { if (x < 0) throw new RangeError('negative'); return x; }\n");
    Assert.That(Run(worker, module), Is.EqualTo(new[] { "20", "trap: negative" }));
  }

  /// <summary>Scenario of the issue: a crashing worker fails only its own test, with the exit code, and the next one passes.</summary>
  [Test]
  public void CrashingModule_FailsWithTheExitCode_AndTheNextRequestPasses() {
    var crash = this.Module("exit.mjs", "export function f(x) { process.exit(3); }\n");
    Assert.That(() => Toolchain.WorkerOutcomes(crash, _calls), Throws.TypeOf<NodeFailure>().With.Message.Contains("exited with code 3"));
    Assert.That(Toolchain.WorkerOutcomes(this.Good(), _calls), Is.EqualTo(new[] { "41", "-5" }));
  }

  /// <summary>A module that never answers fails after the timeout; the worker is stopped and replaced.</summary>
  [Test]
  public void EndlessModule_FailsAfterTheTimeout_AndTheWorkerIsStopped() {
    using var worker = NodeWorker.Start(TimeSpan.FromSeconds(2));
    var endless = this.Module("endless.mjs", "export function f(x) { for (;;) {} }\n");
    Assert.That(() => Run(worker, endless), Throws.TypeOf<NodeFailure>().With.Message.Contains("did not answer within 2 s"));
    Assert.That(worker.IsAlive, Is.False);
    Assert.That(() => Run(worker, this.Good()), Throws.TypeOf<NodeFailure>().With.Message.Contains("stopped"));
  }

  /// <summary>
  /// The answer reaches the caller while every thread of the pool is taken and more work waits for one,
  /// as in a sweep of many targets beside other tests: the worker's pipes are served by threads of
  /// their own, so the answer does not queue for a thread the pool adds only slowly.
  /// </summary>
  [Test, NonParallelizable]
  public void AnAnswer_DoesNotWaitForTheThreadPool() {
    using var worker = NodeWorker.Start(TimeSpan.FromSeconds(5));
    Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
    // Not disposed: blockers the pool has not started yet still wait on it after the test.
    var release = new ManualResetEventSlim();
    ThreadPool.GetMinThreads(out var minimum, out _);
    for (var blocker = Math.Max(minimum, ThreadPool.ThreadCount) + 4 * Environment.ProcessorCount + 64; blocker > 0; --blocker)
      ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(), null);
    try {
      Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
    } finally {
      release.Set();
    }
  }

  /// <summary>Just inside the timeout a slow module still answers.</summary>
  [Test]
  public void SlowModule_WithinTheTimeout_Answers() {
    using var worker = NodeWorker.Start(TimeSpan.FromSeconds(30));
    var slow = this.Module("slow.mjs", "export function f(x) { const end = Date.now() + 200; while (Date.now() < end) {} return x; }\n");
    Assert.That(Run(worker, slow), Is.EqualTo(new[] { "20", "-3" }));
  }

  /// <summary>An exception at load or a call that throws something other than a RangeError fails the request, not the worker.</summary>
  [TestCase("throw new Error('boom at load');\nexport function f(x) { return x; }\n", "boom at load")]
  [TestCase("export function f(x) { throw new TypeError('boom in a call'); }\n", "boom in a call")]
  [TestCase("export function g(x) { return x; }\n", "TypeError")]
  public void ThrowingModule_FailsWithTheCause_AndTheWorkerGoesOn(string text, string cause) {
    using var worker = NodeWorker.Start();
    var module = this.Module("throws-" + Guid.NewGuid().ToString("N") + ".mjs", text);
    Assert.That(() => Run(worker, module), Throws.TypeOf<NodeFailure>().With.Message.Contains(cause));
    Assert.That(worker.IsAlive, Is.True);
    Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
  }

  /// <summary>Output of the module's own fails the request, as it would add a line to the driver's outcomes.</summary>
  [Test]
  public void ModuleWritingToStandardOutput_Fails_AndTheWorkerGoesOn() {
    using var worker = NodeWorker.Start();
    var chatty = this.Module("chatty.mjs", "console.log('hello');\nexport function f(x) { return x; }\n");
    Assert.That(() => Run(worker, chatty), Throws.TypeOf<NodeFailure>().With.Message.Contains("hello"));
    Assert.That(Run(worker, this.Good()), Is.EqualTo(new[] { "41", "-5" }));
  }

  /// <summary>An answer that is the marker and no protocol stops the worker.</summary>
  [TestCase("{broken")]
  [TestCase("{\"ok\":true}")]
  public void MalformedAnswer_FailsTheRequest_AndStopsTheWorker(string answer) {
    using var worker = NodeWorker.Start();
    var forged = this.Module("forged.mjs", $"process.stdout.write({System.Text.Json.JsonSerializer.Serialize(NodeWorker.Marker + answer + "\n")});\nexport function f(x) {{ return x; }}\n");
    Assert.That(() => Run(worker, forged), Throws.TypeOf<NodeFailure>().With.Message.Contains("outside the protocol"));
    Assert.That(worker.IsAlive, Is.False);
  }

  /// <summary>
  /// Test design 3: the worker and a <c>node</c> started for the calls compute the same outcomes, for
  /// every fixture in every profile the worker runs.
  /// </summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Worker_AgreesWithAFreshNode(string fixture, ProfileCase profile) {
    var calls = PrototypeFixture.Typed(fixture, PrototypeCases.For(fixture));
    var file = Directory.CreateDirectory(Path.Combine(this._directory, "out"));
    var (exitCode, output) = Toolchain.Translate(this._directory, ["translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "javascript", .. profile.Arguments, "--output", "out"]);
    Assert.That(exitCode, Is.Zero, output);
    var module = PrototypeFixture.ClassOf(fixture) + profile.Parsed.Extension;
    var fresh = Toolchain.FreshNodeOutcomes(file.FullName, module, calls, true, profile.Mode);
    Assert.That(Toolchain.NodeOutcomes(file.FullName, module, calls, true, profile.Mode), Is.EqualTo(fresh));
  }

  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in PrototypeFixture.Names
       from profile in ProfileCase.All
       where profile.Global is null
       select new TestCaseData(fixture, profile);

  /// <summary>The worker leaves a script with a global object to a node of its own.</summary>
  [Test]
  public void ScriptWithAGlobal_RunsInANodeOfItsOwn() {
    var profile = new ProfileCase("es5", "iife", false, "Fnv");
    var (exitCode, output) = Toolchain.Translate(this._directory, ["translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "javascript", .. profile.Arguments, "--output", "out"]);
    Assert.That(exitCode, Is.Zero, output);
    var call = new PrototypeCall("Compute", [PrototypeCases.Text("Wikipedia")]);
    Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), "Adler32" + profile.Parsed.Extension, [call], false, profile.Mode, profile.Global).Single(),
      Is.EqualTo(PrototypeFixture.CompiledOutcome("Adler32", call)));
  }

}
