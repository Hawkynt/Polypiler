using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>One call of a fixture method: the unit every comparison works on.</summary>
/// <param name="Method">The name of the method.</param>
/// <param name="Arguments">The arguments; arrays are copied before every run.</param>
internal sealed record PrototypeCall(string Method, object[] Arguments) {

  /// <summary>The arguments with every array copied, so that one run cannot change what the next one sees.</summary>
  public object[] FreshArguments()
    => this.Arguments.Select(argument => argument is Array array ? array.Clone() : argument).ToArray();

  /// <summary>The call as a test failure shows it.</summary>
  public override string ToString() => $"{this.Method}({string.Join(", ", this.Arguments.Select(PrototypeFixture.Show))})";

}

/// <summary>
/// A fixture of <c>tests/fixtures/prototype</c>: its source for Polypiler, and the same source as the
/// C# compiler built it into this test assembly, which is the oracle.
/// </summary>
internal static class PrototypeFixture {

  /// <summary>The names of all fixtures, which are also the names of their classes.</summary>
  public static readonly string[] Names = ["Adler32", "Arrays", "Crc32", "Fnv1a", "Numbers", "Operators", "Statements"];

  /// <summary>The root of the repository.</summary>
  public static string RepositoryRoot { get; } = Root();

  /// <summary>The path of a fixture's source file.</summary>
  public static string PathOf(string fixture) => Path.Combine(RepositoryRoot, "tests", "fixtures", "prototype", fixture + ".cs");

  /// <summary>The text of a fixture's source file.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture));

  /// <summary>Wraps method declarations into the one static class a source file consists of, on one line.</summary>
  public static string Wrap(string methods) => $"public static class Snippet {{ {methods} }}";

  /// <summary>Imports a source and fails with its diagnostics when it is refused.</summary>
  public static IrModule Import(string path, string source) {
    var result = CSharpFrontend.Import(path, source);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The method of a fixture as the C# compiler built it.</summary>
  public static MethodInfo Compiled(string fixture, string method) {
    var type = typeof(PrototypeFixture).Assembly.GetType("Polypiler.Fixtures.Prototype." + fixture, true)!;
    return type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new MissingMethodException(fixture, method);
  }

  /// <summary>What the method compiled by the C# compiler does for a call.</summary>
  public static string CompiledOutcome(string fixture, PrototypeCall call) => InvokedOutcome(Compiled(fixture, call.Method), call);

  /// <summary>What a static method does for a call, whichever compiler run produced the method.</summary>
  public static string InvokedOutcome(MethodInfo method, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return Outcome(method.Invoke(null, arguments), arguments);
    } catch (TargetInvocationException exception) when (TrapOf(exception.InnerException) is { } trap) {
      return Trap(trap);
    }
  }

  /// <summary>What the interpreter does for a call.</summary>
  public static string InterpretedOutcome(IrModule module, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return Outcome(IrInterpreter.Call(module, call.Method, arguments), arguments);
    } catch (IrTrapException exception) {
      return Trap(exception.Trap);
    }
  }

  /// <summary>The outcome of a call that trapped, as every implementation is expected to report it.</summary>
  public static string Trap(IrTrap trap) => "trap: " + IrTrapException.Describe(trap);

  /// <summary>
  /// The outcome of a call that returned: the result, followed by the final contents of every array
  /// argument, because a function may change the arrays it is given.
  /// </summary>
  public static string Outcome(object? result, IEnumerable<object> arguments)
    => string.Join(" | ", arguments.Where(argument => argument is Array).Prepend(result).Select(Show));

  /// <summary>A value as an outcome writes it; JavaScript's <c>String()</c> writes numbers the same way.</summary>
  public static string Show(object? value) => value switch {
    null => "void",
    bool truth => truth ? "true" : "false",
    Array array => $"[{string.Join(",", array.Cast<object>().Select(Show))}]",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
  };

  /// <summary>The trap a .NET exception corresponds to, or <see langword="null"/> for any other exception.</summary>
  private static IrTrap? TrapOf(Exception? exception) => exception switch {
    DivideByZeroException => IrTrap.DivideByZero,
    IndexOutOfRangeException => IrTrap.IndexOutOfRange,
    OverflowException => IrTrap.IntegerOverflow,
    _ => null
  };

  private static string Root([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".."));

}
