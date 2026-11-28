using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.BigInt;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Every operation and conversion of the arbitrary-precision integer, emitted as JavaScript at the
/// levels with <c>BigInt</c> and run by node on the operands of <see cref="BigIntCases"/>: every call
/// has the interpreter's outcome, a double by its bits, a trap by the error class of the IR's exception class.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Category("Prototype")]
[Layer("core")]
public sealed class BigIntJavaScriptTests {

  private const string Runner = """
    import { readFileSync } from 'node:fs';
    const request = JSON.parse(readFileSync('calls.json', 'utf8'));
    const module = await import('./' + request.module);
    const view = new DataView(new ArrayBuffer(8));
    const value = argument => {
      if ('big' in argument) return BigInt(argument.big);
      if ('float' in argument) { view.setBigUint64(0, BigInt.asUintN(64, BigInt(argument.float))); return view.getFloat64(0); }
      return argument.int;
    };
    const lines = [];
    for (const [name, args, floating] of request.calls) {
      try {
        const result = module[name](...args.map(value));
        if (floating) { view.setFloat64(0, result); lines.push('f ' + view.getBigUint64(0).toString(16).padStart(16, '0')); }
        else lines.push(String(result));
      } catch (error) {
        lines.push('raise ' + error.name);
      }
    }
    process.stdout.write(lines.join('\n') + '\n');
    """;

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-bigint-js-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  [TestCase("es2020"), TestCase("es2022")]
  public void NodeOnTheEmittedJavaScript_AgreesWithTheInterpreter(string profile)
    => this.Run(BigIntCases.Module(), [.. BigIntCases.Calls()], profile);

  /// <summary>
  /// The hashes and generators of O0062, imported and specialized: the
  /// narrowed arithmetic of 32 bits on numbers, of 64 bits on BigInts cut to 64 bits, the rest exact.
  /// </summary>
  [Test]
  public void NodeOnTheSpecializedHashes_AgreesWithTheInterpreter() {
    var source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "o0062", "hashes.py")).Replace("\r\n", "\n", StringComparison.Ordinal);
    var module = PyIntSpecialization.Run(PythonFixture.Module(PythonFixture.Import("hashes.py", source))).Module;
    BigInteger[] operands = [0, 1, -1, 255, 4294967295, 0x0123456789ABCDEF, BigInteger.One << 64, (BigInteger.One << 70) + 5, -(BigInteger.One << 65) + 3];
    var calls = new List<(string Function, object[] Arguments)>();
    foreach (var name in new[] { "fnv1a64", "fnv1a32", "splitmix64", "xorshift64", "xorshift32_sum", "shifted_low", "power" })
      calls.AddRange(operands.Where(operand => name != "power" || (operand >= 0 && operand < 200)).Select(operand => (name, new object[] { operand })));

    calls.Add(("checksum", []));
    foreach (var (a, b) in new (BigInteger, BigInteger)[] { (BigInteger.One << 40, 2), (7, -2), (-7, 2), (5, 0), (BigInteger.One << 80, 7) }) {
      calls.Add(("unmasked_product", [a, b]));
      calls.Add(("quotient_low", [a, b]));
    }

    this.Run(module, calls, "es2022");
    var text = JavaScriptEmitter.Emit(JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate), JavaScriptProfile.Default, FloatMode.Accurate).Single().Content;
    Assert.That(text, Does.Contain("Math.imul(h, 16777619) >>> 0"), "fnv1a32 multiplies on numbers");
  }

  private void Run(IrModule module, IReadOnlyList<(string Function, object[] Arguments)> calls, string profile) {
    var level = JavaScriptProfile.Parse(profile);
    var legalized = JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, level, FloatMode.Accurate);
    var file = JavaScriptEmitter.Emit(legalized, level, FloatMode.Accurate).Single();
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    var returns = module.Functions.ToDictionary(function => function.Name, function => function.ReturnType);
    var request = new JsonObject {
      ["module"] = file.Path,
      ["calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonArray(call.Function, new JsonArray([.. call.Arguments.Select(Argument)]), returns[call.Function] == IrType.F64))])
    };
    File.WriteAllText(Path.Combine(this._directory, "calls.json"), request.ToJsonString());
    File.WriteAllText(Path.Combine(this._directory, "runner.mjs"), Runner);
    var (exitCode, output) = Toolchain.RunNode(this._directory, "runner.mjs");
    Assert.That(exitCode, Is.Zero, output);

    var outcomes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
    Assert.That(outcomes, Has.Count.EqualTo(calls.Count));
    var mismatches = calls.Select((call, index) => (call, Node: outcomes[index], Expected: Expected(module, call.Function, call.Arguments)))
      .Where(row => row.Node != row.Expected)
      .Select(row => $"{row.call.Function}({string.Join(", ", row.call.Arguments.Select(BigIntCases.Show))}): node {row.Node}, interpreter {row.Expected}")
      .ToList();
    TestContext.Out.WriteLine($"{profile}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{file.Content}");
  }

  /// <summary>The interpreter's outcome as node prints it: a double by its bits, an exception by the error class of its IR class.</summary>
  private static string Expected(IrModule module, string function, object[] arguments) {
    try {
      var result = IrInterpreter.Call(module, function, arguments);
      return result switch {
        double number => "f " + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
        bool truth => truth ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => result!.ToString()!
      };
    } catch (IrException exception) {
      return "raise " + exception.Class.Name.Replace("Exception", "Error", StringComparison.Ordinal);
    }
  }

  private static JsonNode Argument(object value) => value switch {
    BigInteger big => new JsonObject { ["big"] = big.ToString(CultureInfo.InvariantCulture) },
    double number => new JsonObject { ["float"] = "0x" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture) },
    ulong large => new JsonObject { ["big"] = large.ToString(CultureInfo.InvariantCulture) },
    _ => new JsonObject { ["int"] = Convert.ToInt32(value, CultureInfo.InvariantCulture) }
  };

}
