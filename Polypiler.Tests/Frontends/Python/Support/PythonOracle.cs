using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// CPython running a fixture's original source on calls, through <c>oracle.py</c>: the oracle every
/// recording of the Python fixtures comes from. The interpreter is the one <c>POLYPILER_PYTHON</c>
/// names, else <c>python3</c>; it runs with <c>-I -B</c>, as the cross-language runner starts it.
/// </summary>
internal static class PythonOracle {

  /// <summary>The interpreter the oracle runs.</summary>
  public static string Interpreter => Environment.GetEnvironmentVariable("POLYPILER_PYTHON") is { Length: > 0 } named ? named : "python3";

  /// <summary>What CPython does for every call of a module, one outcome per call.</summary>
  /// <param name="module">The path of the Python source.</param>
  /// <param name="calls">The calls.</param>
  public static IReadOnlyList<string> Outcomes(string module, IReadOnlyList<PythonCall> calls) {
    var request = new JsonObject {
      ["module"] = module,
      ["calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonArray(call.Function, new JsonArray([.. call.Arguments.Select(Argument)])))])
    };
    var start = new ProcessStartInfo(Interpreter) {
      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      StandardOutputEncoding = Encoding.UTF8
    };
    foreach (var argument in new[] { "-I", "-B", PythonFixture.Oracle })
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
      input.Write(request.ToJsonString());

    Task.WaitAll(output, errors);
    process.WaitForExit();
    if (process.ExitCode != 0)
      throw new InvalidOperationException($"the oracle failed with {process.ExitCode}: {errors.Result}");

    return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
  }

  private static JsonNode Argument(object value) => value switch {
    bool truth => new JsonObject { ["bool"] = truth },
    long integer => new JsonObject { ["int"] = integer.ToString(CultureInfo.InvariantCulture) },
    double number => new JsonObject { ["float"] = Hex(number) },
    _ => throw new ArgumentException($"{value} is no Python value of the import", nameof(value))
  };

  /// <summary>A float as <c>float.hex()</c> writes it, which <c>float.fromhex()</c> reads back exactly.</summary>
  public static string Hex(double number) {
    if (double.IsNaN(number))
      return "nan";

    if (double.IsInfinity(number))
      return number > 0 ? "inf" : "-inf";

    var bits = BitConverter.DoubleToInt64Bits(number);
    var sign = bits < 0 ? "-" : "";
    var exponent = (int)((bits >> 52) & 0x7FF);
    var fraction = bits & 0xFFFFFFFFFFFFFL;
    return exponent == 0
      ? $"{sign}0x0.{fraction:x13}p-1022"
      : $"{sign}0x1.{fraction:x13}p{exponent - 1023:+0;-0}";
  }

}
