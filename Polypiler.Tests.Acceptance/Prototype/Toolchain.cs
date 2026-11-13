using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>Starts the real <c>polypiler</c> executable and the real <c>node</c>.</summary>
internal static class Toolchain {

  /// <summary>
  /// The script that calls a module, generated or original. It reads the calls from a JSON file, runs
  /// each one and prints its outcome in the notation <see cref="PrototypeFixture.Outcome"/> uses, one
  /// per line. A function is taken from the exports of an ES module, or from the object a CommonJS
  /// file such as Cipher's <c>OpCodes.js</c> exports as a whole. Only a <c>RangeError</c> is an
  /// outcome; anything else the module throws ends the script with an error.
  /// <para>
  /// An argument is a JSON number or truth value, or an object: <c>{t:"n"}</c> a 64-bit integer in
  /// decimal that becomes a <c>BigInt</c>, <c>{t:"f"}</c> the 64 bits of a double in hexadecimal,
  /// <c>{t:"a"}</c> a typed array of the element kind <c>k</c>. A number that is not an integer is
  /// printed as its bits, so that two results are equal exactly when they are the same double. With
  /// the argument <c>exact</c> that includes the negative zero; without it -0 is printed as the 0 it
  /// is to code that computes in integers. An <c>Array</c> is printed like a typed array, which is
  /// what it is once imported.
  /// </para>
  /// <para>
  /// The fourth argument says what the module is written for. <c>native</c>: 64-bit integers are
  /// <c>BigInt</c>s and arrays typed arrays. <c>halves</c>: a 64-bit integer is an object
  /// <c>{ hi, lo }</c> and an array of them a plain array. <c>plain</c>: that, and every other array
  /// is a plain array as well. A pair of halves that is not two unsigned 32-bit integers ends the
  /// script with an error; it is printed as the number it stands for, signed or unsigned as the
  /// kind of the result or of the array says. With a fifth argument the module is a script that
  /// declares a global object of that name, which holds the functions.
  /// </para>
  /// </summary>
  private const string Driver = """
    import { readFileSync } from 'node:fs';
    import { fileURLToPath } from 'node:url';
    import { runInThisContext } from 'node:vm';
    const [modulePath, callsPath, zero, mode, globalName] = process.argv.slice(2);
    let module;
    if (globalName) {
      runInThisContext(readFileSync(fileURLToPath(modulePath), 'utf8'));
      module = globalThis[globalName];
    } else {
      module = await import(modulePath);
    }
    const arrays = {
      i8: Int8Array, u8: Uint8Array, i16: Int16Array, u16: Uint16Array, i32: Int32Array, u32: Uint32Array,
      i64: BigInt64Array, u64: BigUint64Array, f32: Float32Array, f64: Float64Array
    };
    const bits = new DataView(new ArrayBuffer(8));
    const wide = kind => kind === 'i64' || kind === 'u64';
    const pair = big => ({ hi: Number(BigInt.asUintN(64, big) >> 32n), lo: Number(BigInt.asUintN(32, big)) });
    const half = value => Number.isInteger(value) && value >= 0 && value <= 0xFFFFFFFF && !Object.is(value, -0);
    const unpair = (value, kind) => {
      if (!half(value.hi) || !half(value.lo) || Object.keys(value).length !== 2) throw new Error('not a pair of halves: ' + JSON.stringify(value));
      const big = (BigInt(value.hi) << 32n) | BigInt(value.lo);
      return String(kind === 'i64' ? BigInt.asIntN(64, big) : big);
    };
    const decode = value => value === null || typeof value !== 'object' ? value
      : value.t === 'n' ? (mode === 'native' ? BigInt(value.v) : pair(BigInt(value.v)))
      : value.t === 'f' ? (bits.setBigUint64(0, BigInt('0x' + value.v)), bits.getFloat64(0))
      : mode === 'plain' || (mode === 'halves' && wide(value.k)) ? value.v.map(decode)
      : arrays[value.k].from(value.v.map(decode));
    const isArray = value => ArrayBuffer.isView(value) || Array.isArray(value);
    const showNumber = value => {
      if (Number.isSafeInteger(value) && !(zero === 'exact' && Object.is(value, -0))) return String(value);
      if (value !== value) return 'NaN';
      bits.setFloat64(0, value);
      return 'f:' + bits.getBigUint64(0).toString(16).padStart(16, '0');
    };
    const show = (value, kind) => value === undefined ? 'void'
      : isArray(value) ? `[${Array.from(value, element => show(element, kind)).join(',')}]`
      : typeof value === 'number' ? showNumber(value)
      : typeof value === 'object' ? unpair(value, kind)
      : String(value);
    const lines = [];
    for (const [name, encoded, returns] of JSON.parse(readFileSync(callsPath, 'utf8'))) {
      const values = encoded.map(decode);
      try {
        const result = (name in module ? module : module.default)[name](...values);
        const shown = [show(result, returns)];
        values.forEach((value, index) => { if (isArray(value)) shown.push(show(value, encoded[index].k)); });
        lines.push(shown.join(' | '));
      } catch (error) {
        if (!(error instanceof RangeError)) throw error;
        lines.push('trap: ' + error.message);
      }
    }
    process.stdout.write(lines.join('\n'));
    """;

  /// <summary>Runs <c>polypiler</c> with the given arguments in a working directory.</summary>
  /// <returns>The exit code and everything the process wrote, standard output first.</returns>
  public static (int ExitCode, string Output) RunPolypiler(string workingDirectory, params string[] arguments) {
    var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "polypiler.exe" : "polypiler");
    Assert.That(File.Exists(executable), Is.True, $"the polypiler executable was not built next to the tests: {executable}");
    return Run(executable, workingDirectory, arguments);
  }

  /// <summary>The version of the <c>node</c> on the path; fails the test when there is none.</summary>
  public static string NodeVersion() => Node(Path.GetTempPath(), "--version").Output.Trim();

  /// <summary>Runs calls through a generated module in <c>node</c> and returns one outcome per call.</summary>
  /// <param name="directory">The directory the driver and the calls are written to.</param>
  /// <param name="module">The module, relative to the directory or absolute.</param>
  /// <param name="calls">The calls, in the order of the outcomes.</param>
  /// <param name="negativeZero">
  /// Whether a result of -0 is told from 0. It is for a module that computes in floating point, where
  /// the two are different results; an integer has one zero, and JavaScript that computes one may
  /// well hold it as -0.
  /// </param>
  /// <param name="mode">
  /// How the module holds its values: <c>native</c>, <c>halves</c> or <c>plain</c>, as the driver
  /// reads it. Anything but <c>native</c> needs <see cref="PrototypeCall.Returns"/> on every call
  /// that returns a 64-bit integer or an array of them.
  /// </param>
  /// <param name="global">The global object of a script that declares one; <see langword="null"/> for a module.</param>
  public static IReadOnlyList<string> NodeOutcomes(
    string directory, string module, IReadOnlyList<PrototypeCall> calls, bool negativeZero = false, string mode = "native", string? global = null) {
    var driver = Path.Combine(directory, "driver.mjs");
    var input = Path.Combine(directory, "calls.json");
    File.WriteAllText(driver, Driver);
    File.WriteAllText(input, JsonSerializer.Serialize(calls.Select(call => new object?[] {
      call.Method, call.Arguments.Select(Encode).ToArray(), call.Returns is null ? null : Kind(call.Returns.GetElementType() ?? call.Returns)
    })));
    string[] arguments = [driver, new Uri(Path.Combine(directory, module)).AbsoluteUri, input, negativeZero ? "exact" : "integer", mode];
    var (exitCode, output) = Node(directory, global is null ? arguments : [.. arguments, global]);
    Assert.That(exitCode, Is.Zero, $"node failed on {module}: {output}");
    var outcomes = output.Split('\n');
    Assert.That(outcomes, Has.Length.EqualTo(calls.Count), $"node printed {outcomes.Length} outcomes for {calls.Count} calls");
    return outcomes;
  }

  /// <summary>Runs a script of the test's own in <c>node</c>.</summary>
  /// <returns>The exit code and everything the process wrote, standard output first.</returns>
  public static (int ExitCode, string Output) RunNode(string workingDirectory, string script) => Node(workingDirectory, script);

  private static (int ExitCode, string Output) Node(string workingDirectory, params string[] arguments) {
    try {
      return Run("node", workingDirectory, arguments);
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to run the generated JavaScript but could not be started: {exception.Message}");
      throw;
    }
  }

  /// <summary>
  /// An argument as the driver reads it. The type is asked of the value itself: a pattern such as
  /// <c>is int[]</c> also matches a <c>uint[]</c>.
  /// </summary>
  private static object Encode(object argument) => argument switch {
    Array array => new { t = "a", k = Kind(array.GetType().GetElementType()!), v = array.Cast<object>().Select(Encode).ToArray() },
    long or ulong => new { t = "n", v = Convert.ToString(argument, CultureInfo.InvariantCulture) },
    float or double => new { t = "f", v = BitConverter.DoubleToInt64Bits(Convert.ToDouble(argument, CultureInfo.InvariantCulture)).ToString("x16", CultureInfo.InvariantCulture) },
    char unit => (int)unit,
    _ => argument
  };

  /// <summary>The element kind of a typed array for a .NET element type.</summary>
  private static string Kind(Type element) => Type.GetTypeCode(element) switch {
    TypeCode.SByte => "i8",
    TypeCode.Byte => "u8",
    TypeCode.Int16 => "i16",
    TypeCode.UInt16 or TypeCode.Char => "u16",
    TypeCode.Int32 => "i32",
    TypeCode.UInt32 => "u32",
    TypeCode.Int64 => "i64",
    TypeCode.UInt64 => "u64",
    TypeCode.Single => "f32",
    _ => "f64"
  };

  private static (int ExitCode, string Output) Run(string executable, string workingDirectory, string[] arguments) {
    var start = new ProcessStartInfo(executable) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output + error.Result);
  }

}
