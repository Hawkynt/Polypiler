using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Starts the real <c>polypiler</c> executable and the real <c>node</c>, or, where the process is not
/// what a test is about, does the same work without starting one: a translation through the entry
/// point the executable calls, and a module run by a <see cref="NodeWorker"/> that stays alive.
/// </summary>
internal static class Toolchain {

  /// <summary>
  /// What the scripts that call a module, generated or original, have in common: <c>outcomes(module,
  /// calls, zero, mode)</c> runs each call and gives its outcome in the notation
  /// <see cref="PrototypeFixture.Outcome"/> uses. A function is taken from the exports of an ES module,
  /// or from the object a CommonJS file such as Cipher's <c>OpCodes.js</c> exports as a whole. Only a
  /// <c>RangeError</c> is an outcome; anything else the module throws is thrown on.
  /// <para>
  /// An argument is a JSON number or truth value, or an object: <c>{t:"n"}</c> a 64-bit integer in
  /// decimal that becomes a <c>BigInt</c>, <c>{t:"f"}</c> the 64 bits of a double in hexadecimal,
  /// <c>{t:"a"}</c> a typed array of the element kind <c>k</c>. A number that is not an integer is
  /// printed as its bits, so that two results are equal exactly when they are the same double. With
  /// <c>zero</c> <c>exact</c> that includes the negative zero; otherwise -0 is printed as the 0 it
  /// is to code that computes in integers. An <c>Array</c> is printed like a typed array, which is
  /// what it is once imported. Any other object but a pair of halves is a struct, printed as the
  /// values of its keys in order between braces; a pair of halves among them is read as unsigned.
  /// </para>
  /// <para>
  /// The <c>mode</c> says what the module is written for. <c>native</c>: 64-bit integers are
  /// <c>BigInt</c>s and arrays typed arrays. <c>halves</c>: a 64-bit integer is an object
  /// <c>{ hi, lo }</c> and an array of them a plain array. <c>plain</c>: that, and every other array
  /// is a plain array as well. A pair of halves that is not two unsigned 32-bit integers is an
  /// error; it is printed as the number it stands for, signed or unsigned as the kind of the result
  /// or of the array says.
  /// </para>
  /// </summary>
  internal const string Codec = """
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
    const isArray = value => ArrayBuffer.isView(value) || Array.isArray(value);
    const isPair = value => Object.keys(value).length === 2 && 'hi' in value && 'lo' in value;
    const outcomes = (module, calls, zero, mode) => {
      const decode = value => value === null || typeof value !== 'object' ? value
        : value.t === 'n' ? (mode === 'native' ? BigInt(value.v) : pair(BigInt(value.v)))
        : value.t === 'f' ? (bits.setBigUint64(0, BigInt('0x' + value.v)), bits.getFloat64(0))
        : mode === 'plain' || (mode === 'halves' && wide(value.k)) ? value.v.map(decode)
        : arrays[value.k].from(value.v.map(decode));
      const showNumber = value => {
        if (Number.isSafeInteger(value) && !(zero === 'exact' && Object.is(value, -0))) return String(value);
        if (value !== value) return 'NaN';
        bits.setFloat64(0, value);
        return 'f:' + bits.getBigUint64(0).toString(16).padStart(16, '0');
      };
      const show = (value, kind) => value === undefined ? 'void'
        : isArray(value) ? `[${Array.from(value, element => show(element, kind)).join(',')}]`
        : typeof value === 'number' ? showNumber(value)
        : typeof value === 'object' && isPair(value) ? unpair(value, kind)
        : typeof value === 'object' ? `{${Object.values(value).map(field => show(field)).join(',')}}`
        : String(value);
      const lines = [];
      for (const [name, encoded, returns] of calls) {
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
      return lines;
    };
    """;

  /// <summary>
  /// The script that calls a module in a <c>node</c> of its own: its arguments are the module, the JSON
  /// file of the calls, <c>zero</c> and <c>mode</c> of <see cref="Codec"/>, and, for a script that
  /// declares a global object rather than a module, the name of that object, which holds the
  /// functions. It prints one outcome per line; anything the module throws but a <c>RangeError</c>
  /// ends it with an error.
  /// </summary>
  private const string Driver = """
    import { readFileSync } from 'node:fs';
    import { fileURLToPath } from 'node:url';
    import { runInThisContext } from 'node:vm';

    """ + Codec + """

    const [modulePath, callsPath, zero, mode, globalName] = process.argv.slice(2);
    let module;
    if (globalName) {
      runInThisContext(readFileSync(fileURLToPath(modulePath), 'utf8'));
      module = globalThis[globalName];
    } else {
      module = await import(modulePath);
    }
    process.stdout.write(outcomes(module, JSON.parse(readFileSync(callsPath, 'utf8')), zero, mode).join('\n'));
    """;

  /// <summary>Stands for the source file in a translation kept for the run.</summary>
  private const string InputMark = "\u0001input\u0001";

  /// <summary>Stands for the output directory in a translation kept for the run.</summary>
  private const string OutputMark = "\u0001output\u0001";

  /// <summary>Stands for the path of the source relative to the output directory, which the manifest of a kept translation records.</summary>
  private const string ManifestInputMark = "\u0001manifest-input\u0001";

  /// <summary>
  /// The manifest of a translation with the path of its input, relative to the output directory,
  /// replaced by <paramref name="to"/> where it stands for <paramref name="from"/>: as a JSON string of
  /// its own and in the recorded command line.
  /// </summary>
  private static byte[] Relocated(byte[] manifest, string from, string to) {
    static string Quoted(string path) => global::Polypiler.Cli.CliContext.Invocation([path])["polypiler ".Length..];
    // The command line first: the mark, quoted there, would otherwise be taken for a JSON string.
    var text = Encoding.UTF8.GetString(manifest)
      .Replace($"polypiler translate {Quoted(from)} ", $"polypiler translate {Quoted(to)} ", StringComparison.Ordinal)
      .Replace($"\"{from}\"", $"\"{to}\"", StringComparison.Ordinal);
    return Encoding.UTF8.GetBytes(text);
  }

  /// <summary>The manifest a translation writes beside its files.</summary>
  private const string ManifestFile = "polypiler.manifest.json";

  /// <summary>Every translation done in process during this run, by <see cref="TranslationKey"/>.</summary>
  private static readonly ConcurrentDictionary<string, Lazy<Translation>> _translations = new(StringComparer.Ordinal);

  /// <summary>Every worker started during this run, so that the end of the run can stop them.</summary>
  private static readonly ConcurrentBag<NodeWorker> _workers = [];

  /// <summary>The worker of the test thread: tests on one thread run one after the other, so they share it.</summary>
  [ThreadStatic]
  private static NodeWorker? _worker;

  /// <summary>Runs <c>polypiler</c> with the given arguments in a working directory.</summary>
  /// <returns>The exit code and everything the process wrote, standard output first.</returns>
  public static (int ExitCode, string Output) RunPolypiler(string workingDirectory, params string[] arguments) {
    var result = Executable(workingDirectory, arguments);
    return (result.ExitCode, result.Output);
  }

  /// <summary>Runs <c>polypiler</c> with the given arguments in a working directory and keeps its two streams apart.</summary>
  public static CommandResult Executable(string workingDirectory, params string[] arguments) {
    var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "polypiler.exe" : "polypiler");
    Assert.That(File.Exists(executable), Is.True, $"the polypiler executable was not built next to the tests: {executable}");
    return Run(executable, workingDirectory, arguments, _polypilerEnvironment);
  }

  /// <summary>
  /// Runs <c>polypiler-tooling</c> with the given arguments in a working directory, with that directory
  /// first on <c>PATH</c>: its verbs look for files above the working directory and for tools on
  /// <c>PATH</c>, both of which a process has of its own.
  /// </summary>
  public static CommandResult Tooling(string workingDirectory, params string[] arguments) {
    var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "polypiler-tooling.exe" : "polypiler-tooling");
    Assert.That(File.Exists(executable), Is.True, $"the polypiler-tooling executable was not built next to the tests: {executable}");
    return Run(executable, workingDirectory, arguments, new Dictionary<string, string> {
      ["PATH"] = workingDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
    });
  }

  /// <summary>
  /// The runtime settings of the executable under test. A translation is a process of under a second,
  /// most of it in methods the JIT compiles at the first tier (the tests start the build output, which
  /// is not ReadyToRun); instrumenting those for profile-guided optimization pays off only in a process
  /// that runs long enough to use the profile. Measured on the build output, median of 7 runs: a strict
  /// float translation of Dct8 to ES5 takes 0.87 s instead of 0.90 s, Crc32 to JavaScript 0.76 s
  /// instead of 0.81 s, and a translation from JavaScript 0.22 s instead of 0.24 s. It changes how fast
  /// the executable runs, not what it does.
  /// </summary>
  private static readonly Dictionary<string, string> _polypilerEnvironment = new() { ["DOTNET_TieredPGO"] = "0" };

  /// <summary>
  /// Does what <see cref="RunPolypiler"/> does for a <c>translate</c> command line with a source file
  /// and an <c>--output</c>, without starting a process: through <c>Program.Run</c>, which is what the
  /// executable's <c>Main</c> calls, with the paths of the command line taken from the working
  /// directory as the process would take them. The same command line on the same source is translated
  /// once per run; every caller gets the files written into its own output directory and the text the
  /// executable would have written. It is for the tests that need the generated file; what the
  /// executable does as a process is tested with the executable.
  /// </summary>
  /// <param name="workingDirectory">The directory relative paths of the command line start in.</param>
  /// <param name="arguments">The command line, <c>translate</c> first and the source file second.</param>
  /// <returns>The exit code and everything the command wrote, standard output first.</returns>
  public static (int ExitCode, string Output) Translate(string workingDirectory, params string[] arguments) {
    var result = TranslateInProcess(workingDirectory, arguments);
    return (result.ExitCode, result.Output);
  }

  /// <summary>
  /// Whether <see cref="Translate"/> can run a command line in process: <c>translate</c>, a source file
  /// that exists, and an <c>--output</c> with a value.
  /// </summary>
  public static bool CanTranslateInProcess(string workingDirectory, string[] arguments) {
    var output = Array.IndexOf(arguments, "--output") + 1;
    return arguments.Length > 1 && arguments[0] == "translate" && !arguments[1].StartsWith("--", StringComparison.Ordinal) && output > 0 && output < arguments.Length
      && File.Exists(Path.Combine(workingDirectory, arguments[1]));
  }

  /// <summary><see cref="Translate"/> with the two streams kept apart.</summary>
  public static CommandResult TranslateInProcess(string workingDirectory, params string[] arguments) {
    var output = Array.IndexOf(arguments, "--output") + 1;
    Assert.That(arguments.Length > 1 && arguments[0] == "translate" && !arguments[1].StartsWith("--", StringComparison.Ordinal) && output > 0 && output < arguments.Length, Is.True,
      $"a translation in process needs 'translate', a source file and '--output': {string.Join(' ', arguments)}");
    var source = Path.GetFullPath(Path.Combine(workingDirectory, arguments[1]));
    Assert.That(File.Exists(source), Is.True, $"the source file of a translation in process does not exist: {source}");

    var translation = _translations.GetOrAdd(TranslationKey(arguments, File.ReadAllBytes(source)), _ => new(() => Translated(arguments, source))).Value;
    if (translation.Files is { } files) {
      var directory = Path.Combine(workingDirectory, arguments[output]);
      Directory.CreateDirectory(directory);
      var input = global::Polypiler.Cli.TranslateCommand.InputPath(Path.GetFullPath(directory), source);
      foreach (var (path, content) in files)
        File.WriteAllBytes(Path.Combine(directory, path), path == ManifestFile ? Relocated(content, ManifestInputMark, input) : content);
    }

    string Unmarked(string text) => text.Replace(InputMark, arguments[1], StringComparison.Ordinal).Replace(OutputMark, arguments[output], StringComparison.Ordinal);
    return new CommandResult(translation.ExitCode, Unmarked(translation.Stdout), Unmarked(translation.Stderr));
  }

  /// <summary><see cref="Translate"/> where <paramref name="inProcess"/> is set, <see cref="RunPolypiler"/> otherwise.</summary>
  public static (int ExitCode, string Output) Polypiler(bool inProcess, string workingDirectory, params string[] arguments)
    => inProcess ? Translate(workingDirectory, arguments) : RunPolypiler(workingDirectory, arguments);

  /// <summary>
  /// What identifies a translation: the command line as given, except the output directory, and the
  /// bytes of the source file. Two translations with the same key write the same files and the same
  /// text, apart from the name of the output directory.
  /// </summary>
  internal static string TranslationKey(string[] arguments, byte[] source) {
    var output = Array.IndexOf(arguments, "--output") + 1;
    var line = string.Join('\0', arguments.Select((argument, index) => index == output ? OutputMark : argument));
    return Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes(line), 0, .. source]));
  }

  /// <summary>Translates once, into a directory of its own, and keeps what the command wrote with the paths marked.</summary>
  private static Translation Translated(string[] arguments, string source) {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-translation-" + Guid.NewGuid().ToString("N"));
    var output = Array.IndexOf(arguments, "--output") + 1;
    string[] actual = [.. arguments.Select((argument, index) => index == 1 ? source : index == output ? directory : argument)];
    try {
      using var standardOutput = new StringWriter();
      using var standardError = new StringWriter();
      var exitCode = global::Polypiler.Cli.Program.Run(actual, standardOutput, standardError);
      string Marked(string text) => text.Replace(directory, OutputMark, StringComparison.Ordinal).Replace(source, InputMark, StringComparison.Ordinal);
      var input = global::Polypiler.Cli.TranslateCommand.InputPath(directory, source);
      var files = Directory.Exists(directory)
        ? Directory.GetFiles(directory).Order(StringComparer.Ordinal)
          .Select(file => (Path.GetFileName(file), Path.GetFileName(file) == ManifestFile ? Relocated(File.ReadAllBytes(file), input, ManifestInputMark) : File.ReadAllBytes(file)))
          .ToArray()
        : null;
      return new Translation(exitCode, Marked(standardOutput.ToString()), Marked(standardError.ToString()), files);
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, true);
    }
  }

  /// <summary>The version of the <c>node</c> on the path; fails the test when there is none.</summary>
  public static string NodeVersion() => Node(Path.GetTempPath(), "--version").Output.Trim();

  /// <summary>
  /// Runs calls through a module in <c>node</c> and returns one outcome per call: a module in the
  /// <see cref="NodeWorker"/> of the test thread, a script that declares a global object in a
  /// <c>node</c> of its own, as <see cref="FreshNodeOutcomes"/> does.
  /// </summary>
  /// <param name="directory">The directory the module is in, and a fresh <c>node</c> gets its driver and calls written to.</param>
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
    if (global is not null)
      return FreshNodeOutcomes(directory, module, calls, negativeZero, mode, global);

    IReadOnlyList<string> outcomes;
    try {
      outcomes = WorkerOutcomes(new Uri(Path.Combine(directory, module)).AbsoluteUri, calls, negativeZero, mode);
    } catch (NodeFailure failure) {
      Assert.Fail($"node failed on {module}: {failure.Message}");
      throw;
    }

    Assert.That(outcomes, Has.Count.EqualTo(calls.Count), $"node gave {outcomes.Count} outcomes for {calls.Count} calls");
    return outcomes;
  }

  /// <summary>
  /// Runs calls through a module in the <see cref="NodeWorker"/> of the test thread, which is started
  /// on the first call and again after one that ended it.
  /// </summary>
  /// <exception cref="NodeFailure">The module or the worker failed; the message says how.</exception>
  public static IReadOnlyList<string> WorkerOutcomes(string moduleUrl, IReadOnlyList<PrototypeCall> calls, bool negativeZero = false, string mode = "native") {
    if (_worker is not { IsAlive: true }) {
      _worker?.Dispose();
      _worker = NodeWorker.Start();
      _workers.Add(_worker);
    }

    return _worker.Outcomes(moduleUrl, EncodedCalls(calls), negativeZero ? "exact" : "integer", mode);
  }

  /// <summary>Stops every worker of the run.</summary>
  public static void StopWorkers() {
    while (_workers.TryTake(out var worker))
      worker.Dispose();
  }

  /// <summary>
  /// Runs calls through a module, or a script that declares a global object, in a <c>node</c> started
  /// for them, and returns one outcome per call. The parameters are those of <see cref="NodeOutcomes"/>.
  /// </summary>
  public static IReadOnlyList<string> FreshNodeOutcomes(
    string directory, string module, IReadOnlyList<PrototypeCall> calls, bool negativeZero = false, string mode = "native", string? global = null) {
    var driver = Path.Combine(directory, "driver.mjs");
    var input = Path.Combine(directory, "calls.json");
    File.WriteAllText(driver, Driver);
    File.WriteAllText(input, JsonSerializer.Serialize(EncodedCalls(calls)));
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
      var result = Run("node", workingDirectory, arguments);
      return (result.ExitCode, result.Output);
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to run the generated JavaScript but could not be started: {exception.Message}");
      throw;
    }
  }

  /// <summary>The calls as the driver reads them: the name, the arguments and the kind of the result.</summary>
  private static object?[][] EncodedCalls(IReadOnlyList<PrototypeCall> calls) => [.. calls.Select(call => new object?[] {
    call.Method, call.Arguments.Select(Encode).ToArray(), call.Returns is null ? null : Kind(call.Returns.GetElementType() ?? call.Returns)
  })];

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

  private static CommandResult Run(string executable, string workingDirectory, string[] arguments, IReadOnlyDictionary<string, string>? environment = null) {
    var start = new ProcessStartInfo(executable) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    foreach (var (name, value) in environment ?? new Dictionary<string, string>())
      start.Environment[name] = value;

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return new CommandResult(process.ExitCode, output, error.Result);
  }

  /// <summary>A translation as the command did it: its exit code, its two streams with the paths marked, and the files it wrote, if it created the output directory.</summary>
  private sealed record Translation(int ExitCode, string Stdout, string Stderr, (string Path, byte[] Content)[]? Files);

}
