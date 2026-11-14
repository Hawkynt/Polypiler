using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>One call of a fixture method: the unit every comparison works on.</summary>
/// <param name="Method">The name of the method.</param>
/// <param name="Arguments">The arguments; arrays are copied before every run.</param>
internal sealed record PrototypeCall(string Method, object[] Arguments) {

  /// <summary>
  /// The type the method returns, where a comparison needs it: JavaScript below ECMAScript 2020 holds
  /// a 64-bit integer as two halves, which read as a number only when its signedness is known.
  /// </summary>
  public Type? Returns { get; init; }

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

  /// <summary>
  /// The fixtures of the first prototype: <c>int</c>, <c>uint</c> and <c>byte</c> with <c>if</c>,
  /// <c>while</c> and <c>for</c>. Every backend writes these; the wide ones need what only some do.
  /// </summary>
  public static readonly string[] First = ["Adler32", "Arrays", "Crc32", "Fnv1a", "Numbers", "Operators", "Statements"];

  /// <summary>The names of all fixtures, which are also the names of their classes.</summary>
  public static readonly string[] Names = [
    .. First,
    "wide/BinarySearch", "wide/BubbleSort", "wide/Conversions", "wide/Crc16", "wide/Crc32Table", "wide/DigitSum", "wide/Fir",
    "wide/Fletcher16", "wide/Fnv1a64", "wide/Horner", "wide/Library", "wide/OperatorsWide", "wide/RunLength", "wide/SplitMix64",
    "wide/StatementsWide", "wide/Tables", "wide/Xorshift64"
  ];

  /// <summary>The name of the class a fixture declares, which is the name of its file without the folder.</summary>
  public static string ClassOf(string fixture) => fixture[(fixture.LastIndexOf('/') + 1)..];

  /// <summary>The root of the repository.</summary>
  public static string RepositoryRoot { get; } = Root();

  /// <summary>The path of a fixture's source file.</summary>
  public static string PathOf(string fixture)
    => Path.Combine(RepositoryRoot, "tests", "fixtures", "prototype", fixture.Replace('/', Path.DirectorySeparatorChar) + ".cs");

  /// <summary>The text of a fixture's source file.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture));

  /// <summary>A fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string fixture) => Import(ClassOf(fixture) + ".cs", SourceOf(fixture));

  /// <summary>Wraps method declarations into the one static class a source file consists of, on one line.</summary>
  public static string Wrap(string methods) => $"public static class Snippet {{ {methods} }}";

  /// <summary>Imports a source and fails with its diagnostics when it is refused.</summary>
  public static IrModule Import(string path, string source) {
    var result = CSharpFrontend.Import(path, source);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The method of a fixture as the C# compiler built it.</summary>
  public static MethodInfo Compiled(string fixture, string method) {
    return CompiledType(fixture).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new MissingMethodException(fixture, method);
  }

  /// <summary>The class of a fixture as the C# compiler built it.</summary>
  public static Type CompiledType(string fixture)
    => typeof(PrototypeFixture).Assembly.GetType("Polypiler.Fixtures.Prototype." + ClassOf(fixture), true)!;

  /// <summary>The calls with the return type of the method each one calls, as the C# compiler built it.</summary>
  public static IReadOnlyList<PrototypeCall> Typed(string fixture, IEnumerable<PrototypeCall> calls) {
    var returns = new Dictionary<string, Type>();
    return [.. calls.Select(call => {
      if (!returns.TryGetValue(call.Method, out var type))
        returns.Add(call.Method, type = Compiled(fixture, call.Method).ReturnType);

      return call with { Returns = type };
    })];
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

  /// <summary>
  /// A value as an outcome writes it; JavaScript's <c>String()</c> writes integers the same way. A
  /// <c>char</c> is the number of its code unit, which is what it is in the IR and in JavaScript; a
  /// struct is the values of its fields between braces.
  /// </summary>
  public static string Show(object? value) => value switch {
    null => "void",
    bool truth => truth ? "true" : "false",
    char unit => ((int)unit).ToString(CultureInfo.InvariantCulture),
    float single => Show((double)single),
    double number => Show(number),
    // A struct: the interpreter hands one over as the array of its fields, .NET as a value of a struct type.
    object?[] fields => $"{{{string.Join(",", fields.Select(Show))}}}",
    Array array when Elements(array) is { } shown => shown,
    Array array => $"[{string.Join(",", array.Cast<object>().Select(Show))}]",
    _ when value.GetType() is { IsValueType: true, IsPrimitive: false } type && type != typeof(decimal)
      => $"{{{string.Join(",", type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(field => field.MetadataToken).Select(field => Show(field.GetValue(value))))}}}",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
  };

  /// <summary>
  /// An array of a primitive type as <see cref="Show(object)"/> writes it, element by element, without
  /// boxing every element: the equivalence tests write millions of them. The type is asked exactly,
  /// because a <c>uint[]</c> also passes for an <c>int[]</c>. <see langword="null"/> for any other array.
  /// </summary>
  private static string? Elements(Array array) {
    var type = array.GetType();
    return type == typeof(byte[]) ? Joined((byte[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(sbyte[]) ? Joined((sbyte[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(short[]) ? Joined((short[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(ushort[]) ? Joined((ushort[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(int[]) ? Joined((int[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(uint[]) ? Joined((uint[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(long[]) ? Joined((long[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(ulong[]) ? Joined((ulong[])array, value => value.ToString(CultureInfo.InvariantCulture))
      : type == typeof(char[]) ? Joined((char[])array, value => ((int)value).ToString(CultureInfo.InvariantCulture))
      : type == typeof(bool[]) ? Joined((bool[])array, value => value ? "true" : "false")
      : type == typeof(float[]) ? Joined((float[])array, value => Show((double)value))
      : type == typeof(double[]) ? Joined((double[])array, Show)
      : null;
  }

  private static string Joined<T>(T[] values, Func<T, string> show) {
    var text = new StringBuilder("[");
    for (var index = 0; index < values.Length; ++index) {
      if (index > 0)
        text.Append(',');

      text.Append(show(values[index]));
    }

    return text.Append(']').ToString();
  }

  /// <summary>
  /// A floating-point number by its bits, so that two outcomes are equal exactly when the numbers are
  /// the same: <c>NaN</c> for every NaN, the decimal digits for an integer a double holds exactly
  /// (which is how JavaScript cannot tell it from an integer result), otherwise <c>f:</c> and the 64
  /// bits in hexadecimal. A binary32 number is widened first, which changes nothing about it.
  /// </summary>
  public static string Show(double number) {
    if (double.IsNaN(number))
      return "NaN";

    return Math.Abs(number) <= 9007199254740991 && Math.Truncate(number) == number && !(number == 0 && double.IsNegative(number))
      ? ((long)number).ToString(CultureInfo.InvariantCulture)
      : "f:" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The trap a .NET exception corresponds to, or <see langword="null"/> for any other exception.
  /// <c>Array.Copy</c> reports a range outside an array as an <see cref="ArgumentException"/>; the
  /// fixtures have no other source of one, and in the IR it is the bounds trap. <c>Math.Sign</c> of a
  /// NaN throws the <see cref="ArithmeticException"/> itself, not one of its two subclasses.
  /// </summary>
  private static IrTrap? TrapOf(Exception? exception) => exception switch {
    DivideByZeroException => IrTrap.DivideByZero,
    IndexOutOfRangeException or ArgumentException => IrTrap.IndexOutOfRange,
    OverflowException => IrTrap.IntegerOverflow,
    ArithmeticException => IrTrap.InvalidOperation,
    _ => null
  };

  private static string Root([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".."));

}
