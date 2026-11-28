using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The Python fixtures imported, emitted as JavaScript and run by
/// <c>node</c>: every call has the outcome CPython recorded for the original source, as the unit tests
/// hold the interpreter and the emitted C# to it. An <c>int</c> is a <c>BigInt</c> there, a
/// <c>float</c> a number, written as Python's <c>repr</c> writes it, an exception by the Python class
/// its error class stands for.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Category("Prototype")]
[Layer("core")]
public sealed class PythonToJavaScriptTests {

  private const string Runner = """
    import { readFileSync } from 'node:fs';
    const request = JSON.parse(readFileSync('calls.json', 'utf8'));
    const module = await import('./' + request.module);
    const view = new DataView(new ArrayBuffer(8));
    const value = argument => {
      if ('int' in argument) return BigInt(argument.int);
      if ('float' in argument) { view.setBigUint64(0, BigInt.asUintN(64, BigInt(argument.float))); return view.getFloat64(0); }
      return argument.bool;
    };
    const repr = x => {
      if (Number.isNaN(x)) return 'nan';
      if (x === Infinity) return 'inf';
      if (x === -Infinity) return '-inf';
      if (x === 0) return Object.is(x, -0) ? '-0.0' : '0.0';
      const sign = x < 0 ? '-' : '';
      const [mantissa, exponent] = Math.abs(x).toExponential().split('e');
      const digits = mantissa.replace('.', '');
      const point = Number(exponent) + 1;
      if (point > -4 && point <= 16) {
        if (point <= 0) return sign + '0.' + '0'.repeat(-point) + digits;
        if (point >= digits.length) return sign + digits + '0'.repeat(point - digits.length) + '.0';
        return sign + digits.slice(0, point) + '.' + digits.slice(point);
      }
      const power = point - 1;
      return sign + (digits.length === 1 ? digits : digits[0] + '.' + digits.slice(1)) + 'e' + (power < 0 ? '-' : '+') + String(Math.abs(power)).padStart(2, '0');
    };
    const show = result => result === undefined ? 'None'
      : typeof result === 'boolean' ? 'bool ' + (result ? 'True' : 'False')
      : typeof result === 'bigint' ? 'int ' + result
      : 'float ' + repr(result);
    const lines = [];
    for (const [name, args] of request.calls) {
      try {
        lines.push(show(module[name](...args.map(value))));
      } catch (error) {
        lines.push('raise ' + error.name);
      }
    }
    process.stdout.write(lines.join('\n') + '\n');
    """;

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-python-js-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static string[] Fixtures => PythonFixture.Names;

  [TestCaseSource(nameof(Fixtures))]
  public void NodeOnTheEmittedJavaScript_AgreesWithCPython(string fixture) {
    var module = PythonFixture.Import(fixture);
    // As translate does: the operations the profile lacks bound to the portable implementations and the helpers.
    var legalized = JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate);
    var file = JavaScriptEmitter.Emit(legalized, JavaScriptProfile.Default, FloatMode.Accurate).Single();
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    var calls = PythonCases.For(fixture, module);
    var request = new JsonObject {
      ["module"] = file.Path,
      ["calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonArray(call.Function, new JsonArray([.. call.Arguments.Select(Argument)])))])
    };
    File.WriteAllText(Path.Combine(this._directory, "calls.json"), request.ToJsonString());
    File.WriteAllText(Path.Combine(this._directory, "runner.mjs"), Runner);
    var (exitCode, output) = Toolchain.RunNode(this._directory, "runner.mjs");
    Assert.That(exitCode, Is.Zero, output);

    var outcomes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))
      .Select(line => line.StartsWith("raise ", StringComparison.Ordinal) ? "raise " + PythonOutcome.Exception(line[6..]) : line).ToList();
    var expected = PythonFixture.RecordedLines(fixture).Select(line => PythonOutcome.Expected(line[(line.IndexOf(" => ", StringComparison.Ordinal) + 4)..])).ToList();
    Assert.That(outcomes, Has.Count.EqualTo(calls.Count));
    var mismatches = calls.Select((call, index) => (call, Node: outcomes[index], Expected: expected[index]))
      .Where(row => row.Node != row.Expected)
      .Select(row => $"{row.call}: node {row.Node}, CPython {row.Expected}")
      .ToList();
    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  private static JsonNode Argument(object value) => value switch {
    bool truth => new JsonObject { ["bool"] = truth },
    long integer => new JsonObject { ["int"] = integer.ToString(CultureInfo.InvariantCulture) },
    double number => new JsonObject { ["float"] = "0x" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture) },
    _ => throw new ArgumentException($"{value} is no Python value of the import", nameof(value))
  };

}
