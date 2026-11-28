using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>
/// Generates <c>PolypilerDriver.kt</c>, the program that calls one export of a Kotlin unit on input
/// images. It reads one input image per line of its first argument, in hexadecimal, decodes the
/// arguments (the little-endian encoding of <see cref="ImageCodec"/>) into the Kotlin type of each
/// image type (<see cref="KotlinSignatures"/>), calls the export directly - a generated call, no
/// reflection - inside <c>try</c>/<c>catch (Throwable)</c>, and writes one line per call: the output
/// image in hexadecimal, the result followed by every <c>inout</c> array, or <c>!</c>, the trap class
/// of <see cref="KotlinTrapTable"/> and the simple name of the exception. A line it cannot read ends
/// it with exit code 2. The driver stands in the unit's package, so the call needs no import.
/// </summary>
public static class KotlinDriverGenerator {

  /// <summary>The file name of the driver.</summary>
  public const string FileName = "PolypilerDriver.kt";

  /// <summary>The class of the driver's <c>main</c>, as the compiler names the file class, in a package.</summary>
  /// <param name="package">The unit's package, or <see langword="null"/>.</param>
  /// <returns>The qualified class name.</returns>
  public static string MainClass(string? package) => package is null ? "PolypilerDriverKt" : package + ".PolypilerDriverKt";

  /// <summary>The source of the driver.</summary>
  /// <param name="package">The unit's package, or <see langword="null"/> for the default package.</param>
  /// <param name="export">The function called.</param>
  /// <param name="signature">The images it takes and gives.</param>
  /// <returns>The source.</returns>
  /// <exception cref="ArgumentException">The signature has an <c>out</c> parameter, an <c>inout</c> one that is no array, or an image type the driver has no Kotlin type for.</exception>
  public static string Generate(string? package, string export, CallSignature signature) {
    ArgumentNullException.ThrowIfNull(export);
    ArgumentNullException.ThrowIfNull(signature);
    var text = new StringBuilder();
    if (package is not null)
      text.Append("package ").Append(package).Append("\n\n");

    text.Append(Support);
    text.Append("private fun call(input: java.nio.ByteBuffer, output: java.io.ByteArrayOutputStream) {\n");
    var arguments = new List<string>();
    var outputs = new List<(string Name, ImageType Type)>();
    foreach (var parameter in signature.Parameters) {
      if (parameter.Mode == ParameterMode.Out || parameter.Mode == ParameterMode.InOut && parameter.Type.Kind is not (ImageKind.Sequence or ImageKind.Bytes))
        throw new ArgumentException($"The parameter '{parameter.Name}' has mode {parameter.Mode}; the Kotlin driver passes 'in' parameters and 'inout' arrays.", nameof(signature));

      var name = "a" + arguments.Count.ToString(CultureInfo.InvariantCulture);
      text.Append("    val ").Append(name).Append(" = ").Append(Read(parameter.Type)).Append('\n');
      arguments.Add(name);
      if (parameter.Mode == ParameterMode.InOut)
        outputs.Add((name, parameter.Type));
    }

    text.Append("    if (input.hasRemaining()) kotlin.system.exitProcess(2)\n");
    var call = $"{export}({string.Join(", ", arguments)})";
    if (signature.Result is { } result) {
      text.Append("    val result = ").Append(call).Append('\n');
      text.Append("    ").Append(Write(result, "result")).Append('\n');
    } else {
      text.Append("    ").Append(call).Append('\n');
    }

    foreach (var (name, type) in outputs)
      text.Append("    ").Append(Write(type, name)).Append('\n');

    text.Append("}\n\n");
    text.Append("private fun trap(e: Throwable): String = when {\n");
    foreach (var row in KotlinTrapTable.Rows) {
      var message = row.Message is null ? "" : row.Trap == TrapKind.DivideByZero ? $" && (e.message == null || e.message == \"{row.Message}\")" : $" && e.message == \"{row.Message}\"";
      text.Append("    e is ").Append(row.Class).Append(message).Append(" -> \"").Append(new TrapClass(row.Trap)).Append("\"\n");
    }

    text.Append("    else -> \"other:\" + e.javaClass.simpleName\n}\n");
    return text.ToString();
  }

  private const string Support = """
    private fun unhex(text: String): ByteArray {
        if (text.length % 2 != 0) kotlin.system.exitProcess(2)
        return ByteArray(text.length / 2) { i -> text.substring(2 * i, 2 * i + 2).toInt(16).toByte() }
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { b -> String.format("%02x", b.toInt() and 0xFF) }

    private fun count(input: java.nio.ByteBuffer): Int {
        val n = input.getInt()
        if (n < 0 || n > input.remaining()) kotlin.system.exitProcess(2)
        return n
    }

    private fun put(output: java.io.ByteArrayOutputStream, value: Long, bytes: Int) {
        for (i in 0 until bytes) output.write((value ushr (8 * i)).toInt() and 0xFF)
    }

    fun main(args: Array<String>) {
        val out = StringBuilder()
        for (line in java.io.File(args[0]).readLines()) {
            if (line.isEmpty()) continue
            val input = java.nio.ByteBuffer.wrap(unhex(line)).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            val output = java.io.ByteArrayOutputStream()
            try {
                call(input, output)
                out.append(hex(output.toByteArray())).append('\n')
            } catch (e: java.nio.BufferUnderflowException) {
                kotlin.system.exitProcess(2)
            } catch (e: Throwable) {
                out.append('!').append(trap(e)).append(' ').append(e.javaClass.simpleName).append('\n')
            }
        }
        print(out)
    }


    """;

  /// <summary>The Kotlin expression that reads a value of an image type from <c>input</c>.</summary>
  private static string Read(ImageType type) => type.Kind switch {
    ImageKind.Optional => $"if (input.get().toInt() != 0) {Read(type.Element!)} else null",
    ImageKind.Utf16 => "String(CharArray(count(input)) { input.getChar() })",
    ImageKind.Bytes => "ByteArray(count(input)) { input.get() }",
    ImageKind.Sequence => $"{Array(type.Element!)}(count(input)) {{ {Scalar(type.Element!)} }}",
    _ => Scalar(type)
  };

  /// <summary>The primitive array of the element of a <c>seq</c>.</summary>
  private static string Array(ImageType element) => element.Kind switch {
    ImageKind.Boolean => "BooleanArray",
    ImageKind.Char16 => "CharArray",
    ImageKind.Float => element.Width == 4 ? "FloatArray" : "DoubleArray",
    ImageKind.Integer when element.Signed => element.Width switch { 1 => "ByteArray", 2 => "ShortArray", 4 => "IntArray", _ => "LongArray" },
    _ => throw new ArgumentException($"The image type 'seq<{element}>' has no Kotlin array the driver passes.", nameof(element))
  };

  /// <summary>A scalar read from <c>input</c>.</summary>
  private static string Scalar(ImageType type) => type.Kind switch {
    ImageKind.Boolean => "input.get().toInt() != 0",
    ImageKind.Char16 => "input.getChar()",
    ImageKind.Float => type.Width == 4 ? "Float.fromBits(input.getInt())" : "Double.fromBits(input.getLong())",
    ImageKind.Integer => type.Width switch { 1 => "input.get()", 2 => "input.getShort()", 4 => "input.getInt()", _ => "input.getLong()" } + (type.Signed ? "" : type.Width switch { 1 => ".toUByte()", 2 => ".toUShort()", 4 => ".toUInt()", _ => ".toULong()" }),
    _ => throw new ArgumentException($"The image type '{type}' has no Kotlin type the driver passes.", nameof(type))
  };

  /// <summary>The Kotlin statement that writes a value of an image type to <c>output</c>.</summary>
  private static string Write(ImageType type, string value) => type.Kind switch {
    ImageKind.Optional => $"if ({value} == null) output.write(0) else {{ output.write(1); {Write(type.Element!, value)} }}",
    ImageKind.Utf16 => $"run {{ put(output, {value}.length.toLong(), 4); for (c in {value}) put(output, c.code.toLong(), 2) }}",
    ImageKind.Bytes or ImageKind.Sequence => $"run {{ put(output, {value}.size.toLong(), 4); for (e in {value}) {WriteScalar(type.Kind == ImageKind.Bytes ? ImageType.I8 : type.Element!, "e")} }}",
    _ => WriteScalar(type, value)
  };

  /// <summary>A scalar written to <c>output</c> as its little-endian bytes.</summary>
  private static string WriteScalar(ImageType type, string value) => type.Kind switch {
    ImageKind.Boolean => $"output.write(if ({value}) 1 else 0)",
    ImageKind.Char16 => $"put(output, {value}.code.toLong(), 2)",
    ImageKind.Float => type.Width == 4 ? $"put(output, {value}.toRawBits().toLong(), 4)" : $"put(output, {value}.toRawBits(), 8)",
    ImageKind.Integer when type.Width == 8 => type.Signed ? $"put(output, {value}, 8)" : $"put(output, {value}.toLong(), 8)",
    ImageKind.Integer => $"put(output, {value}.toLong(), {type.Width.ToString(CultureInfo.InvariantCulture)})",
    _ => throw new ArgumentException($"The image type '{type}' has no Kotlin type the driver passes.", nameof(type))
  };

}
