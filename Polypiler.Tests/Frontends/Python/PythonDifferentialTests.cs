using System.Collections.Concurrent;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The imported Python fixtures against CPython: every call of every fixture - the boundary values of
/// each operator and built-in, random operands (seed 1800), every statement form - has the outcome
/// CPython recorded for the original source, in the interpreter and in the C# emitted from the IR and
/// built by the C# compiler. Where the import states a difference (an <c>int</c> beyond 64 bits, a
/// complex power) the expected outcome is the stated one (<see cref="PythonOutcome.Expected"/>).
/// The acceptance tests of the Python layer run CPython again and hold the recordings to it.
/// </summary>
[TestFixture]
public sealed class PythonDifferentialTests {

  private static readonly ConcurrentDictionary<string, IrModule> _modules = new();

  private static IrModule Module(string fixture) => _modules.GetOrAdd(fixture, PythonFixture.Import);

  private static string[] Fixtures => PythonFixture.Names;

  [TestCaseSource(nameof(Fixtures))]
  public void Fixture_ImportsIntoIrTheVerifierAccepts(string fixture)
    => Assert.That(IrVerifier.Verify(Module(fixture)), Is.Empty);

  [TestCaseSource(nameof(Fixtures))]
  public void Recording_BelongsToTheCallsOfTheFixture(string fixture) {
    var calls = PythonCases.For(fixture, Module(fixture));
    var recorded = PythonFixture.RecordedLines(fixture);
    Assert.That(recorded.Select(line => line[..line.IndexOf(" => ", System.StringComparison.Ordinal)]), Is.EqualTo(calls.Select(call => call.ToString())));
  }

  [TestCaseSource(nameof(Fixtures))]
  public void EveryPublicFunction_IsCalled(string fixture) {
    var called = PythonCases.For(fixture, Module(fixture)).Select(call => call.Function).ToHashSet();
    Assert.That(Module(fixture).Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.SubsetOf(called));
  }

  [TestCaseSource(nameof(Fixtures))]
  public void Interpreter_AgreesWithCPython(string fixture) {
    var module = Module(fixture);
    var calls = PythonCases.For(fixture, module);
    var expected = PythonFixture.RecordedLines(fixture).Select(line => PythonOutcome.Expected(line[(line.IndexOf(" => ", System.StringComparison.Ordinal) + 4)..])).ToList();
    var mismatches = calls.Select((call, index) => (call, Interpreted: PythonOutcome.Interpreted(module, call), Expected: expected[index]))
      .Where(row => row.Interpreted != row.Expected)
      .Select(row => $"{row.call}: interpreter {row.Interpreted}, CPython {row.Expected}")
      .ToList();
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  [TestCaseSource(nameof(Fixtures))]
  public void EmittedCSharp_AgreesWithCPython(string fixture) {
    var module = Module(fixture);
    var source = LegalizedCSharp.Emit(module).Single().Content;
    var build = GeneratedCSharp.Compile(source, false);
    var calls = PythonCases.For(fixture, module);
    var expected = PythonFixture.RecordedLines(fixture).Select(line => PythonOutcome.Expected(line[(line.IndexOf(" => ", System.StringComparison.Ordinal) + 4)..])).ToList();
    var mismatches = calls.Select((call, index) => (call, Compiled: PythonOutcome.Invoked(build.Method(call.Function), call), Expected: expected[index]))
      .Where(row => row.Compiled != row.Expected)
      .Select(row => $"{row.call}: C# {row.Compiled}, CPython {row.Expected}")
      .ToList();
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

}
