using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The wide-integer legalization O0075 in <c>node</c> (#1358). A 64-bit add with a constant high word
/// is specialised at ECMAScript 5 until no 64-bit helper is called, and calls the library's add
/// without the inlining. Every operator, conversion and step of 64 bits, on the edges 0, ±1, 2^31,
/// 2^32, 2^63 − 1 and −2^63 against each other and on random pairs (fixed seed), has in <c>node</c>
/// the outcome of the interpreter - the value, or the same trap.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[Category("Prototype")]
public sealed class WideIntegerScenarioTests {

  private const string Constant = "public static long F(long x) => x + 0x100000000L;";

  private static readonly long[] _edges = [
    0, 1, -1, 2, int.MaxValue, int.MinValue, (long)int.MaxValue + 1, uint.MaxValue, (long)uint.MaxValue + 1, -0x1_0000_0000, long.MaxValue, long.MinValue, 10, -10
  ];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>The source translated for ECMAScript 5 with the options given; the text of the module.</summary>
  private string Translate(string methods, params string[] options) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(methods));
    var output = Path.Combine(this._directory, "out");
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", output, .. options], standard, error);
    Assert.That((exitCode, error.ToString()), Is.EqualTo(((int)ExitCode.Success, "")));
    return File.ReadAllText(Path.Combine(output, "Snippet.cjs"));
  }

  // Scenario: A 64-bit add with a constant high word is specialised at ES5
  [TestCase(new string[0], false)]
  [TestCase(new[] { "--disable-opt", "O0006" }, true)]
  public void ConstantHighWord_IsSpecialisedAndComputesTheSum(string[] options, bool calls) {
    var module = this.Translate(Constant, options);
    var called = Regex.Matches(module, @"\b([A-Z]\w*64)\(").Select(match => match.Groups[1].Value).Distinct().ToList();
    Assert.That(called, calls ? Is.EqualTo(new[] { "Add64" }) : Is.Empty, module);

    File.WriteAllText(Path.Combine(this._directory, "out", "call.cjs"),
      "const m = require(\"./Snippet.cjs\");\nconst r = m.F({ hi: 0, lo: 1 });\nconsole.log(String(r.hi * 4294967296 + r.lo));\n");
    var (code, printed) = Toolchain.RunNode(Path.Combine(this._directory, "out"), "call.cjs");
    Assert.That((code, printed.Trim()), Is.EqualTo((0, "4294967297")));
  }

  /// <summary>Test design 2: every operation on 64 bits, with the passes and without the inlining, in <c>node</c> against the interpreter.</summary>
  [TestCase(false)]
  [TestCase(true)]
  public void EveryOperation_ComputesWhatTheInterpreterComputes(bool withoutInlining) {
    var (methods, calls) = Operations();
    this.Translate(methods, withoutInlining ? ["--disable-opt", "O0006"] : []);
    var imported = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(imported, call)).ToList();
    var node = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), "Snippet.cjs", calls, false, "plain");
    var mismatches = Enumerable.Range(0, calls.Count).Where(index => node[index] != expected[index])
      .Select(index => $"{calls[index]}: node {node[index]}, the interpreter {expected[index]}").ToList();
    TestContext.Out.WriteLine($"{calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>The methods of every operation and the calls of each: the edges against each other and random pairs.</summary>
  private static (string Methods, List<PrototypeCall> Calls) Operations() {
    var random = new Random(1358);
    var methods = new StringBuilder();
    var calls = new List<PrototypeCall>();
    var count = 0;
    void Method(string returns, string parameters, string body, Type? host, IEnumerable<object[]> lists) {
      var name = "M" + count++;
      methods.Append($"public static {returns} {name}({parameters}) => {body}; ");
      calls.AddRange(lists.Select(list => new PrototypeCall(name, list) { Returns = host }));
    }

    foreach (var (type, host) in new[] { ("long", typeof(long)), ("ulong", typeof(ulong)) }) {
      IEnumerable<object> Values() => _edges.Concat(Enumerable.Range(0, 24).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)))
        .Select(value => host == typeof(long) ? value : (object)unchecked((ulong)value)).ToList();
      List<object[]> Pairs() {
        var values = Values().ToList();
        return [.. from x in values.Take(_edges.Length) from y in values.Take(_edges.Length) select new[] { x, y },
          .. Enumerable.Range(0, 120).Select(_ => new[] { values[random.Next(values.Count)], values[random.Next(values.Count)] })];
      }

      foreach (var op in new[] { "+", "-", "*", "/", "%", "&", "|", "^" })
        Method(type, $"{type} a, {type} b", $"a {op} b", host, Pairs());

      foreach (var op in new[] { "+", "-", "*" })
        Method(type, $"{type} a, {type} b", $"checked(a {op} b)", host, Pairs());

      foreach (var op in new[] { "==", "!=", "<", "<=", ">", ">=" })
        Method("bool", $"{type} a, {type} b", $"a {op} b", null, Pairs());

      foreach (var op in new[] { "<<", ">>", ">>>" })
        Method(type, $"{type} a, int n", $"a {op} n", host, Values().SelectMany(value => new[] { 0, 1, 31, 32, 33, 63, 64, 65, -1 }.Select(shift => new[] { value, (object)shift })));

      Method(type, $"{type} a", "~a", host, Values().Select(value => new[] { value }));
      Method("int", $"{type} a, {type} b", "a.CompareTo(b)", null, Pairs());
      Method("double", $"{type} a", "a", null, Values().Select(value => new[] { value }));
      Method("float", $"{type} a", "a", null, Values().Select(value => new[] { value }));
      foreach (var narrow in new[] { "int", "uint", "short", "byte" }) {
        Method(narrow, $"{type} a", $"({narrow})a", null, Values().Select(value => new[] { value }));
        Method(narrow, $"{type} a", $"checked(({narrow})a)", null, Values().Select(value => new[] { value }));
      }

      foreach (var (narrow, values) in new[] { ("int", new object[] { 0, 1, -1, int.MaxValue, int.MinValue }), ("uint", [0u, 1u, uint.MaxValue]), ("short", [(short)-5, (short)7]) }) {
        Method(type, $"{narrow} a", $"unchecked(({type})a)", host, values.Select(value => new[] { value }));
        Method(type, $"{narrow} a", $"checked(({type})a)", host, values.Select(value => new[] { value }));
      }

      object[] numbers = [0.0, -0.5, 1.5, -1.5, 4294967296.5, -4294967296.5, 9.2233720368547758e18, -9.2233720368547758e18, 1.8446744073709552e19, double.NaN, 1e300, -1e300, 123456789012345.75];
      Method(type, "double a", $"({type})a", host, numbers.Select(value => new[] { value }));
      Method(type, "double a", $"checked(({type})a)", host, numbers.Select(value => new[] { value }));
      Method(type, $"{type} a", "a++ + a", host, Values().Select(value => new[] { value }));
      Method(type, $"{type} a", "checked(--a)", host, Values().Select(value => new[] { value }));
      Method(type, $"{type}[] x, int i", "checked(x[i]++) + x[0]", host,
        Values().Select(value => new object[] { host == typeof(long) ? new[] { (long)value, 5L } : new[] { (ulong)value, 5UL }, random.Next(-1, 3) }));
    }

    Method("long", "long a", "-a", typeof(long), _edges.Select(value => new object[] { value }));
    Method("long", "long a", "checked(-a)", typeof(long), _edges.Select(value => new object[] { value }));
    Method("long", "ulong a", "checked((long)a)", typeof(long), _edges.Select(value => new object[] { unchecked((ulong)value) }));
    Method("ulong", "long a", "checked((ulong)a)", typeof(ulong), _edges.Select(value => new object[] { value }));
    return (methods.ToString(), calls);
  }

}
