using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra.Ci;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Runs calls of a generated Python module on the interpreter <c>tooling/toolchains.lock</c> pins,
/// through the cross-language runner and the sandbox of this host, in isolated mode without bytecode
/// files (<see cref="PythonCommands"/>). The driver imports the module from its file and calls each
/// function directly, and runs the coroutine an <c>async def</c> returns with <c>asyncio.run</c>; it prints one outcome per call in the notation of
/// <see cref="PrototypeFixture.Outcome"/>, so that an outcome compares with the C# compiler's and the
/// interpreter's as text. A trap is the built-in exception of the trap table of
/// <c>docs/reference/exceptions.md</c>, read back by its class: Python's own messages are no part of
/// the contract. Any other exception ends the driver with an error, which fails the test.
/// </summary>
internal static class PythonDriver {

  /// <summary>
  /// The driver: its arguments are the module and the JSON file of the calls. An argument is a JSON
  /// number or truth value, or an object: <c>{t:"n"}</c> an integer in decimal, <c>{t:"f"}</c> the
  /// 64 bits of a double in hexadecimal, <c>{t:"a"}</c> a list of the element kind <c>k</c>.
  /// </summary>
  private const string Script = """
    import asyncio
    import importlib.util
    import inspect
    import json
    import struct
    import sys

    sys.setrecursionlimit(20000)
    spec = importlib.util.spec_from_file_location("generated", sys.argv[1])
    module = importlib.util.module_from_spec(spec)
    sys.modules["generated"] = module
    spec.loader.exec_module(module)
    with open(sys.argv[2], encoding="utf-8") as source:
        calls = json.load(source)


    def decode(value):
        if not isinstance(value, dict):
            return value
        if value["t"] == "n":
            return int(value["v"])
        if value["t"] == "f":
            return struct.unpack("<d", struct.pack("<Q", int(value["v"], 16)))[0]
        return [decode(element) for element in value["v"]]


    def show(value):
        if value is None:
            return "void"
        if value is True:
            return "true"
        if value is False:
            return "false"
        if isinstance(value, str):
            return value
        if isinstance(value, (list, tuple)) or type(value).__name__ == "_PlyView":
            return "[" + ",".join(show(element) for element in value) + "]"
        if isinstance(value, float):
            if value != value:
                return "NaN"
            if value.is_integer() and abs(value) <= 9007199254740991 and not (value == 0 and struct.pack("<d", value)[7] & 0x80):
                return str(int(value))
            return "f:" + format(struct.unpack("<Q", struct.pack("<d", value))[0], "016x")
        if isinstance(value, int):
            return str(int(value))
        if hasattr(value, "__dict__"):
            return "{" + ",".join(show(field) for field in vars(value).values()) + "}"
        raise TypeError("a result of the type " + type(value).__name__)


    def trap(error):
        kinds = [kind.__name__ for kind in type(error).__mro__]
        text = str(error.args[0]) if error.args else ""
        if "ZeroDivisionError" in kinds:
            return "divide by zero"
        if "OverflowError" in kinds:
            return "integer overflow"
        if "IndexError" in kinds:
            return "index out of range"
        if "ArgumentOutOfRangeError" in kinds:
            return "argument out of range"
        if "InvalidCastError" in kinds:
            return "invalid cast"
        if "KeyError" in kinds:
            return "key not found"
        if "FormatError" in kinds:
            return "invalid format"
        if "ValueError" in kinds:
            return text if text in ("duplicate key", "unknown name") else "index out of range"
        if "RuntimeError" in kinds and "RecursionError" not in kinds:
            return "collection modified" if text == "collection modified" or "changed size during iteration" in text else "missing value"
        if "AttributeError" in kinds or "TypeError" in kinds:
            return "null reference"
        if "ArithmeticError" in kinds:
            return "invalid operation"
        return None


    lines = []
    for name, encoded, returns in calls:
        values = [decode(value) for value in encoded]
        try:
            result = getattr(module, name)(*values)
            if inspect.iscoroutine(result):
                result = asyncio.run(result)
        except Exception as error:
            kind = trap(error)
            if kind is None and type(error).__module__ != "generated":
                raise
            lines.append("trap: " + kind if kind is not None else "exception: " + type(error).__name__)
            continue
        shown = [show(result)] + [show(value) for value in values if isinstance(value, list)]
        lines.append(" | ".join(shown))
    sys.stdout.write(json.dumps(lines))
    """;

  /// <summary>
  /// Runs calls through a generated module and returns one outcome per call. Ends the test unless the
  /// interpreter is at its pinned version, as <see cref="Infra.RequiresToolchainAttribute"/> does.
  /// </summary>
  /// <param name="source">The text of the module.</param>
  /// <param name="calls">The calls; each method name is the Python name to call.</param>
  /// <returns>The outcomes, in the order of the calls.</returns>
  public static async Task<IReadOnlyList<string>> OutcomesAsync(string source, IReadOnlyList<PrototypeCall> calls) {
    var command = new ProcessSpec(PythonCommands.Toolchain, [.. PythonCommands.Isolation, "driver.py", "generated.py", "calls.json"]);
    PyPerlSandbox.Require(command);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      ["driver.py"] = Encoding.UTF8.GetBytes(Script),
      ["generated.py"] = Encoding.UTF8.GetBytes(source),
      ["calls.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Encoded(calls)))
    };
    var plan = new RunPlan(files, [], command, new RunLimits(TimeSpan.FromMinutes(5), RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes), TrustLevel.Trusted);
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(plan);
    var stdout = PyPerlSandbox.Text(result.Stdout);
    var stderr = PyPerlSandbox.Text(result.Stderr);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)),
      $"python failed: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}"))}\n{stderr}");
    var outcomes = calls.Count == 0 ? [] : JsonSerializer.Deserialize<string[]>(stdout)!;
    Assert.That(outcomes, Has.Length.EqualTo(calls.Count), $"python printed {outcomes.Length} outcomes for {calls.Count} calls");
    return outcomes;
  }

  /// <summary>Runs a script of the test's own on the pinned interpreter and returns its exit code and both streams.</summary>
  /// <param name="files">The files of the working directory, by name.</param>
  /// <param name="arguments">The arguments after the isolation options, the script first.</param>
  /// <returns>The exit code, standard output and standard error.</returns>
  public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IReadOnlyDictionary<string, string> files, params string[] arguments) {
    var command = new ProcessSpec(PythonCommands.Toolchain, [.. PythonCommands.Isolation, .. arguments]);
    PyPerlSandbox.Require(command);
    var plan = new RunPlan(files.ToDictionary(file => file.Key, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Value), StringComparer.Ordinal), [], command, RunLimits.Default, TrustLevel.Trusted);
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(plan);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return (result.ExitCode, PyPerlSandbox.Text(result.Stdout), PyPerlSandbox.Text(result.Stderr));
  }

  /// <summary>The calls as the driver reads them.</summary>
  private static object?[][] Encoded(IReadOnlyList<PrototypeCall> calls) => [.. calls.Select(call => new object?[] {
    call.Method, call.Arguments.Select(Encode).ToArray(), null
  })];

  private static object Encode(object argument) => argument switch {
    Array array => new { t = "a", v = array.Cast<object>().Select(Encode).ToArray() },
    long or ulong or int or uint or short or ushort or sbyte or byte => new { t = "n", v = Convert.ToString(argument, CultureInfo.InvariantCulture) },
    float or double => new { t = "f", v = BitConverter.DoubleToInt64Bits(Convert.ToDouble(argument, CultureInfo.InvariantCulture)).ToString("x16", CultureInfo.InvariantCulture) },
    char unit => new { t = "n", v = ((int)unit).ToString(CultureInfo.InvariantCulture) },
    System.Numerics.BigInteger big => new { t = "n", v = big.ToString(CultureInfo.InvariantCulture) },
    _ => argument
  };

}
