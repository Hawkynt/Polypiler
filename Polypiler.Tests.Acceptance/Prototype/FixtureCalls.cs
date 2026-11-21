using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// One source of the prototype fixtures, imported, with the calls of every public function whose
/// parameters and result cross the boundary as numbers, truth values, strings, arrays of them,
/// structs or optional numbers, and for each call what the original runtime does and what the IR
/// interpreter does.
/// </summary>
/// <param name="Source">The source, as <see cref="GoldenOutputs.All"/> has it.</param>
/// <param name="Text">The text of the source.</param>
/// <param name="Module">The imported module, or <see langword="null"/> when the frontend refuses the source.</param>
/// <param name="Caveats">The known differences the JavaScript import announced; empty for C#.</param>
/// <param name="Calls">The calls, without those of a method the original runs on and on for.</param>
/// <param name="Expected">What .NET's build of the C# fixture or node's run of the JavaScript original does for each call.</param>
/// <param name="Interpreted">What the IR interpreter does for each call, or <see langword="null"/> where it runs on for longer than a call may.</param>
internal sealed record FixtureRun(
  string Source, string Text, IrModule? Module, IReadOnlyList<string> Caveats,
  IReadOnlyList<PrototypeCall> Calls, IReadOnlyList<string> Expected, IReadOnlyList<string?> Interpreted) {

  /// <summary>The outcome of a call that runs on for longer than a call may.</summary>
  public const string Endless = "endless";

  /// <summary>Whether the source is C#.</summary>
  public bool IsCSharp => this.Source.EndsWith(".cs", StringComparison.Ordinal);

  /// <summary>The indexes of the calls whose interpreted outcome is the original's.</summary>
  public IEnumerable<int> Agreeing => Enumerable.Range(0, this.Calls.Count).Where(index => FixtureCalls.Same(this.Expected[index], this.Interpreted[index] ?? Endless));

  /// <summary>The calls whose interpreted outcome is not the original's, as a test failure shows them.</summary>
  public IEnumerable<string> Differences => Enumerable.Range(0, this.Calls.Count).Where(index => !this.Agreeing.Contains(index))
    .Select(index => $"{this.Source}: {this.Calls[index]}: original {this.Expected[index]}, interpreted {this.Interpreted[index] ?? Endless}");

}

/// <summary>
/// The calls the differential tests of the prototype fixtures make, and their oracles: .NET for C#,
/// node for JavaScript. The interpreter is the oracle only where node cannot run the original, for
/// unsafe C#, whose pointers .NET does not bound as the IR's are, and for C# that calls native code.
/// </summary>
internal static class FixtureCalls {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  /// <summary>How many sets of arguments each function is called with.</summary>
  private const int Rounds = 6;

  private static readonly long[] _signed = [0, 1, 2, 3, 5, 7, -1, -3];
  private static readonly long[] _unsigned = [0, 1, 2, 3, 5, 7, 10, 255];
  private static readonly double[] _floats = [0.5, 1, 2, -1.25, 3, 0, 10, 100.75];
  private static readonly string[] _strings = ["", "a", "abc", "Hello, World", "42", " x y ", "a,b,,c", "3.25"];
  private static readonly char[] _chars = ['a', 'Z', '0', ' ', 'é', '-'];

  /// <summary>Imports a source and runs its calls in the original runtime and in the interpreter.</summary>
  public static FixtureRun Run(string source) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = File.ReadAllText(path);
    var csharp = source.EndsWith(".cs", StringComparison.Ordinal);
    IrModule? module;
    IReadOnlyList<string> caveats = [];
    if (csharp) {
      module = CSharpFrontend.Import(Path.GetFileName(path), text).Module;
    } else {
      var imported = JavaScriptFrontend.Import(Path.GetFileName(path), text);
      module = imported.Module;
      caveats = [.. imported.Caveats.Select(caveat => caveat.Id)];
    }

    if (module is null)
      return new(source, text, null, caveats, [], [], []);

    var calls = csharp ? CSharpCalls(text, module) : JavaScriptCalls(module);
    List<string> expected;
    if (csharp) {
      // A call that runs on and on for its arguments is no call of the comparison, and nor are the others of its method.
      expected = [];
      var endless = new HashSet<string>(StringComparer.Ordinal);
      // Unsafe code reads past its memory where the IR's bounded pointers trap, and native code called
      // outside its contract ends the process (libc's div by zero raises SIGFPE): the interpreter is their oracle.
      var unsafeCode = text.Contains(" unsafe ", StringComparison.Ordinal) || text.Contains(" extern ", StringComparison.Ordinal);
      // The calls run in one instance of the module, as node's run of a fresh copy of the original does:
      // the static fields of the class start from their initial values again.
      var oracle = unsafeCode ? Instance(module) : null;
      if (!unsafeCode)
        CompiledType(text, module)?.TypeInitializer?.Invoke(null, null);

      foreach (var call in calls.ToList()) {
        var outcome = endless.Contains(call.Method) ? null : Bounded(() => unsafeCode ? InterpretedOutcome(module, oracle, call) : DotNetOutcome(text, module, call));
        if (outcome is not null) {
          expected.Add(outcome);
          continue;
        }

        endless.Add(call.Method);
        calls.Remove(call);
      }
    } else if (calls.Count > 0) {
      expected = NodeOutcomes(path, text, module, calls);
    } else {
      expected = [];
    }

    var instance = Instance(module);
    return new(source, text, module, caveats, calls, expected, [.. calls.Select(call => Interpreted(module, call, instance))]);
  }

  /// <summary>An instance of a module with globals, whose state the calls made in it share; <see langword="null"/> for a module without.</summary>
  private static IrModuleInstance? Instance(IrModule module) => module.Globals.Count == 0 ? null : IrInterpreter.Instantiate(module);

  /// <summary>
  /// What the interpreter does for a call, in an instance of the module when one is given, or
  /// <see langword="null"/> where it runs on for longer than a call may.
  /// </summary>
  public static string? Interpreted(IrModule module, PrototypeCall call, IrModuleInstance? instance = null) => Bounded(() => InterpretedOutcome(module, instance, call));

  /// <summary>What the interpreter does for a call; an exception of the program that is no trap is its class.</summary>
  private static string InterpretedOutcome(IrModule module, IrModuleInstance? instance, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return PrototypeFixture.Outcome(instance is null ? IrInterpreter.Call(module, call.Method, arguments) : instance.Call(call.Method, arguments), arguments);
    } catch (IrTrapException exception) {
      return PrototypeFixture.Trap(exception.Trap);
    } catch (IrException exception) {
      return "exception: " + exception.Class.Name;
    }
  }

  /// <summary>
  /// What a computation gives, on a thread of its own, or <see langword="null"/> where it takes longer
  /// than a call may; the thread of one that runs on is left to run in the background, and no thread of
  /// the pool waits for it.
  /// </summary>
  public static string? Bounded(Func<string> compute) {
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

  /// <summary>The name of the exception class each trap's object is of, by the trap's text.</summary>
  private static readonly Dictionary<string, string> _trapClasses = Enum.GetValues<IrTrap>()
    .ToDictionary(trap => "trap: " + IrTrapException.Describe(trap), trap => "exception: " + IrExceptionClass.Of(trap).Name, StringComparer.Ordinal);

  /// <summary>
  /// Whether an outcome is the expected one: the same text; a trap where node's original raised a
  /// <c>RangeError</c> of the engine, whose message is the engine's own and no part of the contract; or
  /// an exception the program throws where .NET's build threw one of the same class, which it reports
  /// as the trap of that class.
  /// </summary>
  public static bool Same(string expected, string actual)
    => expected == actual
      || (expected.StartsWith("trap: ", StringComparison.Ordinal) && !_traps.Contains(expected) && actual.StartsWith("trap: ", StringComparison.Ordinal))
      || (_trapClasses.TryGetValue(expected, out var thrown) && thrown == actual);

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

  /// <summary>Whether a .NET result type is written by every driver alike.</summary>
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
    return typeof(FixtureCalls).Assembly.GetType(name);
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

  /// <summary>
  /// What .NET's build of the fixture does for a call; an exception that is no trap is its class. A
  /// view's traps are told apart from the bounds trap of an array in the fixtures of views, as
  /// <see cref="SpanFixtures"/> tells them apart.
  /// </summary>
  private static string DotNetOutcome(string text, IrModule module, PrototypeCall call) {
    var method = CompiledType(text, module)!.GetMethod(call.Method, BindingFlags.Public | BindingFlags.Static)!;
    try {
      return text.Contains("Span<", StringComparison.Ordinal) || text.Contains("Memory<", StringComparison.Ordinal)
        ? SpanFixtures.Outcome(method, call)
        : PrototypeFixture.InvokedOutcome(method, call);
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
    IrTypeKind.I64 => typeof(long),
    IrTypeKind.U64 => typeof(ulong),
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
    var directory = Directory.CreateTempSubdirectory("polypiler-fixture-node-").FullName;
    try {
      var copy = Path.Combine(directory, "original.mjs");
      var names = calls.Select(call => call.Method).Distinct().ToList();
      File.WriteAllText(copy, text.Contains("export ", StringComparison.Ordinal) ? text : text + $"\nexport {{ {string.Join(", ", names)} }};\n");
      try {
        return [.. Toolchain.WorkerOutcomes(new Uri(copy).AbsoluteUri, calls, true)];
      } catch (NodeFailure failure) {
        TestContext.Out.WriteLine($"node cannot run {Path.GetFileName(path)} ({failure.Message.Split('\n')[0]}); the interpreter is the oracle");
        // A call the interpreter runs on and on for is compared with nothing: no outcome is no outcome of another runtime's.
        return [.. calls.Select(call => Interpreted(module, call) ?? FixtureRun.Endless)];
      }
    } finally {
      Directory.Delete(directory, true);
    }
  }

}
