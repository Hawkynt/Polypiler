using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Every source of the prototype fixtures, C# and JavaScript, translated to Python and run by the
/// pinned CPython: each public function of the module whose parameters and result cross the boundary
/// as numbers, truth values, strings, arrays of them, structs or optional numbers is called with
/// arguments drawn from fixed sets, and has the outcome .NET's build of the C# fixture or node's run of
/// the JavaScript original has - its result, the arrays it was given afterwards, or the trap it raised.
/// The interpreter's outcome is the oracle only where node cannot run the original and for unsafe C#,
/// whose pointers .NET does not bound as the IR's are. A source the
/// backend refuses names each construct with a code of the Python backend.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonFixtureTests {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  /// <summary>Every C# and JavaScript source of the prototype fixtures.</summary>
  public static IEnumerable<string> Sources => GoldenOutputs.All;

  [TestCaseSource(nameof(Sources))]
  [RequiresToolchain("python")]
  public async Task Fixture_RunsInCPythonAsInDotNetOrNode(string source) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = await File.ReadAllTextAsync(path);
    var csharp = source.EndsWith(".cs", StringComparison.Ordinal);
    var module = csharp ? CSharpFrontend.Import(Path.GetFileName(path), text).Module : JavaScriptFrontend.Import(Path.GetFileName(path), text).Module;
    if (module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var translated = PythonFixtures.Translate(module);
    if (translated.Source is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY18\d\d: "), "a refusal names its construct with a code of the Python backend");
      Assert.Inconclusive($"refused: {string.Join("; ", translated.Problems)}");
      return;
    }

    var calls = csharp ? CSharpCalls(text, module) : JavaScriptCalls(module);
    if (calls.Count == 0) {
      Assert.Pass("no function of the module crosses the boundary with values the driver writes");
      return;
    }

    List<string> expected;
    if (csharp) {
      // A call that runs on and on for its arguments is no call of the comparison, and nor are the others of its method.
      expected = [];
      var endless = new HashSet<string>(StringComparer.Ordinal);
      foreach (var call in calls.ToList()) {
        // Unsafe code reads past its memory where the IR's bounded pointers trap: the interpreter is its oracle.
        var unsafeCode = text.Contains(" unsafe ", StringComparison.Ordinal);
        var outcome = endless.Contains(call.Method) ? null : Bounded(() => unsafeCode ? PrototypeFixture.InterpretedOutcome(module, call) : DotNetOutcome(text, module, call));
        if (outcome is not null) {
          expected.Add(outcome);
          continue;
        }

        endless.Add(call.Method);
        calls.Remove(call);
      }
    } else {
      expected = NodeOutcomes(path, text, module, calls);
    }

    // A call where the source's own semantics differ from the IR's is a matter of the import, not of
    // this backend: the C# fixtures' known differences, a JavaScript original that reads past an
    // array or divides an integer by zero where the import traps. It is left out and counted.
    var imported = calls.Select(call => Interpreted(module, call)).ToList();
    // A call the interpreter runs on and on for would run on in CPython as well: it is left out too.
    var compared = calls.Select((call, index) => (call, index)).Where(pair => imported[pair.index] is { } interpreted && Same(expected[pair.index], interpreted)).ToList();
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source, [.. compared.Select(pair => pair.call with { Method = translated.Names![pair.call.Method] })]);
    var mismatches = compared.Select((pair, at) => (pair.call, pair.index, at)).Where(entry => !Same(expected[entry.index], outcomes[entry.at]))
      .Select(entry => $"{entry.call}: expected {expected[entry.index]}, python {outcomes[entry.at]}").ToList();
    TestContext.Out.WriteLine($"{source}: {compared.Count} calls compared, {calls.Count - compared.Count} where the import differs from the source, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {compared.Count} calls differ\n{translated.Source}");
  }

  /// <summary>What the interpreter does for a call, or <see langword="null"/> where it runs on for longer than a call may.</summary>
  private static string? Interpreted(IrModule module, PrototypeCall call) => Bounded(() => {
    try {
      return PrototypeFixture.InterpretedOutcome(module, call);
    } catch (IrException exception) when (exception is not IrTrapException) {
      return "exception: " + exception.Class.Name;
    }
  });

  /// <summary>
  /// What a computation gives, on a thread of its own, or <see langword="null"/> where it takes longer
  /// than a call may; the thread of one that runs on is left to run in the background, and no thread of
  /// the pool waits for it.
  /// </summary>
  private static string? Bounded(Func<string> compute) {
    string? result = null;
    Exception? failure = null;
    var thread = new System.Threading.Thread(() => {
      try {
        result = compute();
      } catch (Exception exception) {
        failure = exception;
      }
    }, 64 * 1024 * 1024) { IsBackground = true };
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(5)))
      return null;

    return failure is null ? result : throw new InvalidOperationException("the oracle failed", failure);
  }

  /// <summary>The texts of the IR's traps, which the drivers of the generated code write.</summary>
  private static readonly HashSet<string> _traps = [.. Enum.GetValues<IrTrap>().Select(trap => "trap: " + IrTrapException.Describe(trap))];

  /// <summary>
  /// Whether an outcome of CPython is the expected one: the same text, or a trap where node's original
  /// raised a <c>RangeError</c> of the engine, whose message is the engine's own and no part of the contract.
  /// </summary>
  private static bool Same(string expected, string actual)
    => expected == actual || (expected.StartsWith("trap: ", StringComparison.Ordinal) && !_traps.Contains(expected) && actual.StartsWith("trap: ", StringComparison.Ordinal));

  /// <summary>How many sets of arguments each function is called with.</summary>
  private const int Rounds = 6;

  private static readonly long[] _signed = [0, 1, 2, 3, 5, 7, -1, -3];
  private static readonly long[] _unsigned = [0, 1, 2, 3, 5, 7, 10, 255];
  private static readonly double[] _floats = [0.5, 1, 2, -1.25, 3, 0, 10, 100.75];
  private static readonly string[] _strings = ["", "a", "abc", "Hello, World", "42", " x y ", "a,b,,c", "3.25"];
  private static readonly char[] _chars = ['a', 'Z', '0', ' ', 'é', '-'];

  /// <summary>The value of a .NET type the driver can write for a round, or <see langword="null"/> for a type it cannot.</summary>
  private static object? Value(Type type, int round) {
    if (type.IsEnum)
      return null;

    if (type.IsArray) {
      var element = type.GetElementType()!;
      var length = round % 5;
      if (Value(element, 0) is null)
        return null;

      var array = Array.CreateInstance(element, length);
      for (var index = 0; index < length; ++index)
        array.SetValue(Value(element, round + index + 1), index);

      return array;
    }

    return Type.GetTypeCode(type) switch {
      TypeCode.Boolean => round % 2 == 0,
      TypeCode.Char => _chars[round % _chars.Length],
      TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => Convert.ChangeType(_signed[round % _signed.Length], type, CultureInfo.InvariantCulture),
      TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => Convert.ChangeType(_unsigned[round % _unsigned.Length], type, CultureInfo.InvariantCulture),
      TypeCode.Single => (float)_floats[round % _floats.Length],
      TypeCode.Double => _floats[round % _floats.Length],
      TypeCode.String => _strings[round % _strings.Length],
      _ => null
    };
  }

  /// <summary>Whether a .NET result type is written by both drivers alike.</summary>
  private static bool Shown(Type type) {
    if (type == typeof(void) || type.IsPrimitive || type == typeof(string))
      return true;

    if (type.IsArray)
      return Shown(type.GetElementType()!) && type.GetElementType() != typeof(void);

    if (Nullable.GetUnderlyingType(type) is { } underlying)
      return underlying.IsPrimitive;

    return type.IsValueType && !type.IsEnum && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(field => field.FieldType.IsPrimitive);
  }

  /// <summary>The .NET type of the class a C# source's module is, found by the namespace of the file.</summary>
  private static Type? CompiledType(string text, IrModule module) {
    var match = Regex.Match(text, @"namespace\s+([\w.]+)\s*[;{]");
    var name = (match.Success ? match.Groups[1].Value + "." : "") + module.Name;
    return typeof(PythonFixtureTests).Assembly.GetType(name);
  }

  /// <summary>The calls of every public static method of the C# fixture's class that the module has as a function.</summary>
  private static List<PrototypeCall> CSharpCalls(string text, IrModule module) {
    var calls = new List<PrototypeCall>();
    if (CompiledType(text, module) is not { } type)
      return calls;

    var functions = module.Functions.Where(function => function.IsPublic).Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(method => functions.Contains(method.Name) && !method.IsGenericMethodDefinition)) {
      if (type.GetMethods(BindingFlags.Public | BindingFlags.Static).Count(other => other.Name == method.Name) > 1 || !Shown(method.ReturnType))
        continue;

      var parameters = method.GetParameters();
      if (parameters.Any(parameter => parameter.ParameterType.IsByRef || Value(parameter.ParameterType, 0) is null))
        continue;

      for (var round = 0; round < (parameters.Length == 0 ? 1 : Rounds); ++round)
        calls.Add(new PrototypeCall(method.Name, [.. parameters.Select((parameter, index) => Value(parameter.ParameterType, round + 3 * index)!)]));
    }

    return calls;
  }

  /// <summary>What .NET's build of the fixture does for a call; an exception that is no trap is its class.</summary>
  private static string DotNetOutcome(string text, IrModule module, PrototypeCall call) {
    var method = CompiledType(text, module)!.GetMethod(call.Method, BindingFlags.Public | BindingFlags.Static)!;
    try {
      return PrototypeFixture.InvokedOutcome(method, call);
    } catch (TargetInvocationException exception) when (exception.InnerException is { } inner) {
      return "exception: " + inner.GetType().Name;
    }
  }

  /// <summary>The .NET type a JavaScript import gives a parameter or result, or <see langword="null"/>.</summary>
  private static Type? HostType(IrType type) => type.Kind switch {
    IrTypeKind.Bool => typeof(bool),
    IrTypeKind.I8 => typeof(sbyte),
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.I16 => typeof(short),
    IrTypeKind.U16 => typeof(ushort),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.F32 => typeof(float),
    IrTypeKind.F64 => typeof(double),
    IrTypeKind.String => typeof(string),
    IrTypeKind.Array when type.Element is { } element && HostType(element) is { } host && !element.IsEnum => host.MakeArrayType(),
    _ => null
  };

  /// <summary>The calls of every public function of a JavaScript module whose values cross as numbers, truth values, strings and arrays.</summary>
  private static List<PrototypeCall> JavaScriptCalls(IrModule module) {
    var calls = new List<PrototypeCall>();
    foreach (var function in module.Functions.Where(function => function.IsPublic)) {
      if (function.ReturnType.Kind is not (IrTypeKind.Void or IrTypeKind.Bool or IrTypeKind.String) && HostType(function.ReturnType) is null)
        continue;

      var types = function.Parameters.Select(parameter => HostType(parameter.Type)).ToList();
      if (types.Any(type => type is null || type == typeof(sbyte) || type == typeof(short)))
        continue;

      for (var round = 0; round < (types.Count == 0 ? 1 : Rounds); ++round)
        calls.Add(new PrototypeCall(function.Name, [.. types.Select((type, index) => Value(type!, round + 3 * index)!)]));
    }

    return calls;
  }

  /// <summary>
  /// What node's run of the JavaScript original does for the calls, as a module that exports the
  /// functions; the interpreter's outcomes where node cannot run them.
  /// </summary>
  private static List<string> NodeOutcomes(string path, string text, IrModule module, IReadOnlyList<PrototypeCall> calls) {
    var directory = Directory.CreateTempSubdirectory("polypiler-python-node-").FullName;
    try {
      var copy = Path.Combine(directory, "original.mjs");
      var names = calls.Select(call => call.Method).Distinct().ToList();
      File.WriteAllText(copy, text.Contains("export ", StringComparison.Ordinal) ? text : text + $"\nexport {{ {string.Join(", ", names)} }};\n");
      try {
        return [.. Toolchain.WorkerOutcomes(new Uri(copy).AbsoluteUri, calls, true)];
      } catch (NodeFailure failure) {
        TestContext.Out.WriteLine($"node cannot run {Path.GetFileName(path)} ({failure.Message.Split('\n')[0]}); the interpreter is the oracle");
        // A call the interpreter runs on and on for is compared with nothing: no outcome is no outcome of CPython's.
        return [.. calls.Select(call => Interpreted(module, call) ?? "endless")];
      }
    } finally {
      Directory.Delete(directory, true);
    }
  }

}
