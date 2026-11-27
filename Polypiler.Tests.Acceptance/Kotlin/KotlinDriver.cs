using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra.Ci;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Kotlin;

/// <summary>A Kotlin file the driver calls: its text, its package and the IR module it was written from.</summary>
/// <param name="Source">The text of the file.</param>
/// <param name="Package">Its package.</param>
/// <param name="Module">The module as it was emitted, whose public functions the driver calls.</param>
/// <param name="Names">The Kotlin name of every function by its IR name.</param>
internal sealed record KotlinUnit(string Source, string Package, IrModule Module, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// Compiles generated Kotlin with the pinned <c>kotlinc</c> and calls its functions on the pinned JDK,
/// through the cross-language runner and the sandbox of this host. Every unit gets a driver in its
/// package that reads one call per line - the function, then its arguments, an integer in decimal, a
/// floating-point number as the 64 bits of a double in hexadecimal, an array as its elements between
/// brackets - calls the function directly and prints one outcome per call in the notation of
/// <see cref="PrototypeFixture.Outcome"/>, so that an outcome compares with the C# compiler's and the
/// interpreter's as text. A trap is the JVM exception of the trap table of
/// <c>docs/reference/exceptions.md</c> ("Kotlin"), read back by its class and, for the
/// <c>ArithmeticException</c> the JVM throws itself, its message; any other exception ends the driver,
/// which fails the test. Many units compile in one run of the compiler, each in a folder named after
/// its package, and the jar is run once per batch of calls.
/// </summary>
internal static class KotlinDriver {

  /// <summary>The source of the driver of a unit: its package, the helpers that read and show values, and one branch per public function.</summary>
  public static string Source(KotlinUnit unit) {
    var text = new StringBuilder();
    text.Append("package ").Append(unit.Package).Append("\n\n");
    text.Append(Support);
    text.Append("private fun call(p: List<String>): String = when (p[0]) {\n");
    foreach (var function in unit.Module.Functions.Where(function => function.IsPublic)) {
      var name = unit.Names[function.Name];
      text.Append("    \"").Append(function.Name).Append("\" -> {\n");
      for (var index = 0; index < function.Parameters.Count; ++index)
        text.Append(CultureInfo.InvariantCulture, $"        val a{index} = {Parse(function.Parameters[index].Type, $"p[{index + 1}]")}\n");

      var arguments = string.Join(", ", Enumerable.Range(0, function.Parameters.Count).Select(index => "a" + index.ToString(CultureInfo.InvariantCulture)));
      var shown = new List<string>();
      if (function.ReturnType == IrType.Void) {
        text.Append("        ").Append(name).Append('(').Append(arguments).Append(")\n");
        shown.Add("\"void\"");
      } else {
        text.Append("        val r = ").Append(name).Append('(').Append(arguments).Append(")\n");
        shown.Add(Show(function.ReturnType, "r"));
      }

      for (var index = 0; index < function.Parameters.Count; ++index)
        if (function.Parameters[index].Type.IsArray)
          shown.Add(Show(function.Parameters[index].Type, "a" + index.ToString(CultureInfo.InvariantCulture)));

      text.Append("        ").Append(string.Join(" + \" | \" + ", shown)).Append('\n');
      text.Append("    }\n");
    }

    text.Append("    else -> throw IllegalStateException(\"no function \" + p[0])\n}\n");
    return text.ToString();
  }

  private const string Support = """
    private fun showDouble(d: Double): String {
        if (d.isNaN()) return "NaN"
        if (Math.abs(d) <= 9007199254740991.0 && kotlin.math.truncate(d) == d && !(d == 0.0 && 1.0 / d < 0.0)) return d.toLong().toString()
        return "f:" + java.lang.String.format("%016x", d.toRawBits())
    }

    private fun double(text: String): Double = Double.fromBits(java.lang.Long.parseUnsignedLong(text, 16))

    private fun items(text: String): List<String> {
        val inner = text.substring(1, text.length - 1)
        return if (inner.isEmpty()) emptyList() else inner.split(',')
    }

    private fun trap(e: Throwable): String? {
        val name = e.javaClass.simpleName
        return when {
            name == "DivideByZeroException" -> "divide by zero"
            name == "OverflowException" -> "integer overflow"
            name == "ArgumentOutOfRangeException" -> "argument out of range"
            name == "FormatException" -> "invalid format"
            e is ArithmeticException && (e.message == null || e.message == "/ by zero") -> "divide by zero"
            e is ArithmeticException && (e.message == "integer overflow" || e.message == "long overflow") -> "integer overflow"
            e is ArithmeticException -> "invalid operation"
            e is NegativeArraySizeException -> "integer overflow"
            e is IndexOutOfBoundsException -> "index out of range"
            e is NullPointerException -> "null reference"
            e is ClassCastException -> "invalid cast"
            e is java.util.ConcurrentModificationException -> "collection modified"
            e is NoSuchElementException -> "key not found"
            e is IllegalStateException -> if (e.message == "collection modified") "collection modified" else "missing value"
            e is IllegalArgumentException && (e.message == "duplicate key" || e.message == "unknown name") -> e.message
            e is IllegalArgumentException -> "argument out of range"
            else -> null
        }
    }

    fun main(args: Array<String>) {
        val out = StringBuilder()
        for (line in java.io.File(args[0]).readLines()) {
            if (line.isEmpty()) continue
            val outcome = try {
                call(line.split(' '))
            } catch (e: Throwable) {
                "trap: " + (trap(e) ?: throw e)
            }
            out.append(outcome).append('\n')
        }
        print(out)
    }


    """;

  /// <summary>The Kotlin expression that reads a value of a type from its text.</summary>
  private static string Parse(IrType type, string text) => type switch {
    { IsArray: true, Element: { } element } => $"items({text}).map {{ {Parse(element, "it")} }}.{ArrayFactory(element)}()",
    { Kind: IrTypeKind.Bool } => $"{text}.toBoolean()",
    { Kind: IrTypeKind.F64 } => $"double({text})",
    { Kind: IrTypeKind.F32 } => $"double({text}).toFloat()",
    _ when type.Integral.IsInteger => $"{text}.to{Scalar(type.Integral)}()",
    _ => throw new ArgumentException($"the driver reads no {type}", nameof(type))
  };

  /// <summary>The conversion of a list of elements to the primitive array the backend holds them in.</summary>
  private static string ArrayFactory(IrType element) => element.Integral.Kind switch {
    IrTypeKind.U8 => "map { it.toByte() }.toByteArray",
    IrTypeKind.U16 => "map { it.toShort() }.toShortArray",
    IrTypeKind.U32 => "map { it.toInt() }.toIntArray",
    IrTypeKind.U64 => "map { it.toLong() }.toLongArray",
    _ => "to" + Scalar(element.Integral) + "Array"
  };

  private static string Scalar(IrType type) => type.Kind switch {
    IrTypeKind.Bool => "Boolean",
    IrTypeKind.I8 => "Byte",
    IrTypeKind.U8 => "UByte",
    IrTypeKind.I16 => "Short",
    IrTypeKind.U16 => "UShort",
    IrTypeKind.I32 => "Int",
    IrTypeKind.U32 => "UInt",
    IrTypeKind.I64 => "Long",
    IrTypeKind.U64 => "ULong",
    IrTypeKind.F32 => "Float",
    _ => "Double"
  };

  /// <summary>The Kotlin expression that shows a value of a type as <see cref="PrototypeFixture.Show(object?)"/> does.</summary>
  private static string Show(IrType type, string value) => type switch {
    { IsArray: true, Element: { } element } => $"\"[\" + {value}.joinToString(\",\") {{ {Show(element, Element(element, "it"))} }} + \"]\"",
    { Kind: IrTypeKind.F64 } => $"showDouble({value})",
    { Kind: IrTypeKind.F32 } => $"showDouble({value}.toDouble())",
    { IsStruct: true, Declaration: IrStructType structure } => "\"{\" + " + string.Join(" + \",\" + ", structure.Fields.Select(field => Show(field.Type, $"{value}.{Polypiler.Core.Backends.Kotlin.KotlinNaming.Camel(field.Name)}"))) + " + \"}\"",
    _ => $"{value}.toString()"
  };

  /// <summary>An element of a primitive array as the value of its IR type: an unsigned one from the signed type the array holds.</summary>
  private static string Element(IrType element, string value) => element.Integral.IsInteger && !element.Integral.IsSigned ? $"{value}.to{Scalar(element.Integral)}()" : value;

  /// <summary>A call as the driver reads it: the function and its arguments.</summary>
  public static string Line(PrototypeCall call) => string.Join(' ', call.Arguments.Select(Encode).Prepend(call.Method));

  private static string Encode(object argument) => argument switch {
    Array array => "[" + string.Join(",", array.Cast<object>().Select(Encode)) + "]",
    bool truth => truth ? "true" : "false",
    float or double => BitConverter.DoubleToInt64Bits(System.Convert.ToDouble(argument, CultureInfo.InvariantCulture)).ToString("x16", CultureInfo.InvariantCulture),
    char unit => ((int)unit).ToString(CultureInfo.InvariantCulture),
    _ => System.Convert.ToString(argument, CultureInfo.InvariantCulture)!
  };

  private static readonly Lazy<JvmCommands> _commands = new(() => new JvmCommands(Toolchains));

  /// <summary>The commands of the Kotlin/Java layer over the repository lock.</summary>
  public static JvmCommands Commands => _commands.Value;

  /// <summary>
  /// Compiles units, each with its driver, into one runnable jar with warnings as errors; fails the
  /// test with the compiler's messages when it rejects them.
  /// </summary>
  /// <param name="units">The units, each in a package of its own.</param>
  /// <param name="options">Further options of <c>kotlinc</c>, such as a profile's.</param>
  /// <returns>The bytes of the jar.</returns>
  public static async Task<ReadOnlyMemory<byte>> BuildAsync(IReadOnlyList<KotlinUnit> units, IReadOnlyList<string>? options = null) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var unit in units) {
      files[$"{unit.Package}/Unit.kt"] = Encoding.UTF8.GetBytes(unit.Source);
      files[$"{unit.Package}/PolypilerDriver.kt"] = Encoding.UTF8.GetBytes(Source(unit));
    }

    var compile = new ProcessSpec(JvmCommands.Kotlinc, [.. files.Keys.Order(StringComparer.Ordinal), "-Werror", "-include-runtime", .. options ?? [], "-d", JvmCommands.Jar]);
    if (Commands.JdkHome is { } home)
      compile = compile with { Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["JAVA_HOME"] = home } };

    var limits = new RunLimits(TimeSpan.FromMinutes(10), RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes);
    var plan = new RunPlan(files, [compile], new ProcessSpec(JvmCommands.Java, "-version"), limits, TrustLevel.Trusted) { Collect = [JvmCommands.Jar] };
    var result = await Commands.RunAsync(plan, Host);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed),
      () => "kotlinc failed: " + string.Join("\n", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}\n{diagnostic.Cause}")) + "\n" + string.Join("\n----\n", units.Select(unit => unit.Source)));
    return result.Collected[JvmCommands.Jar];
  }

  /// <summary>Runs calls of a unit in a jar <see cref="BuildAsync"/> made and returns one outcome per call.</summary>
  /// <param name="jar">The jar.</param>
  /// <param name="package">The package of the unit.</param>
  /// <param name="calls">The calls, by the IR names of the functions.</param>
  /// <returns>The outcomes, in the order of the calls.</returns>
  public static async Task<IReadOnlyList<string>> OutcomesAsync(ReadOnlyMemory<byte> jar, string package, IReadOnlyList<PrototypeCall> calls) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      [JvmCommands.Jar] = jar,
      ["calls.txt"] = Encoding.UTF8.GetBytes(string.Concat(calls.Select(call => Line(call) + "\n")))
    };
    // The driver of every unit is the file class of its PolypilerDriver.kt, in the unit's package.
    var execute = new ProcessSpec(JvmCommands.Java, "-Xss64m", "-cp", JvmCommands.Jar, package + ".PolypilerDriverKt", "calls.txt");
    var limits = new RunLimits(TimeSpan.FromMinutes(5), RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes);
    var result = await Commands.RunAsync(new RunPlan(files, [], execute, limits, TrustLevel.Trusted), Host);
    var stdout = Encoding.UTF8.GetString(result.Stdout.Span);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)),
      $"java failed: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}"))}\n{Encoding.UTF8.GetString(result.Stderr.Span)}");
    var outcomes = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That(outcomes, Has.Length.EqualTo(calls.Count), $"the driver printed {outcomes.Length} outcomes for {calls.Count} calls");
    return outcomes;
  }

}
