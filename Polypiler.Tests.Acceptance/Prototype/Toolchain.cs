using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>Starts the real <c>polypiler</c> executable and the real <c>node</c>.</summary>
internal static class Toolchain {

  /// <summary>
  /// The script that calls a generated module. It reads the calls from a JSON file, runs each one and
  /// prints its outcome in the notation <see cref="PrototypeFixture.Outcome"/> uses, one per line. Only
  /// a <c>RangeError</c> is an outcome; anything else the module throws ends the script with an error.
  /// </summary>
  private const string Driver = """
    import { readFileSync } from 'node:fs';
    const [modulePath, callsPath] = process.argv.slice(2);
    const module = await import(modulePath);
    const arrays = { u8: Uint8Array, i32: Int32Array, u32: Uint32Array };
    const show = value => value === undefined ? 'void'
      : ArrayBuffer.isView(value) ? `[${Array.from(value).join(',')}]`
      : String(value);
    const lines = [];
    for (const [name, encoded] of JSON.parse(readFileSync(callsPath, 'utf8'))) {
      const values = encoded.map(value => value !== null && typeof value === 'object' ? arrays[value.t].from(value.v) : value);
      try {
        const result = module[name](...values);
        lines.push([result, ...values.filter(ArrayBuffer.isView)].map(show).join(' | '));
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
  public static IReadOnlyList<string> NodeOutcomes(string directory, string module, IReadOnlyList<PrototypeCall> calls) {
    var driver = Path.Combine(directory, "driver.mjs");
    var input = Path.Combine(directory, "calls.json");
    File.WriteAllText(driver, Driver);
    File.WriteAllText(input, JsonSerializer.Serialize(calls.Select(call => new object[] { call.Method, call.Arguments.Select(Encode).ToArray() })));
    var (exitCode, output) = Node(directory, driver, new Uri(Path.Combine(directory, module)).AbsoluteUri, input);
    Assert.That(exitCode, Is.Zero, $"node failed on {module}: {output}");
    var outcomes = output.Split('\n');
    Assert.That(outcomes, Has.Length.EqualTo(calls.Count), $"node printed {outcomes.Length} outcomes for {calls.Count} calls");
    return outcomes;
  }

  private static (int ExitCode, string Output) Node(string workingDirectory, params string[] arguments) {
    try {
      return Run("node", workingDirectory, arguments);
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to run the generated JavaScript but could not be started: {exception.Message}");
      throw;
    }
  }

  /// <summary>
  /// An argument as the driver reads it. The element type is asked of the array itself: a pattern
  /// such as <c>is int[]</c> also matches a <c>uint[]</c>.
  /// </summary>
  private static object Encode(object argument) {
    if (argument is not Array array)
      return argument;

    var element = array.GetType().GetElementType();
    var tag = element == typeof(byte) ? "u8" : element == typeof(int) ? "i32" : "u32";
    return new { t = tag, v = array.Cast<object>().ToArray() };
  }

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
