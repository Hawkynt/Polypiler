using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// A call as a feature file writes it, <c>Name(argument, ...)</c>, read into a <see cref="PrototypeCall"/>
/// with the arguments of the types the method declares. An argument is <c>bytes of "text"</c> (its
/// ASCII bytes), <c>empty array</c> or <c>empty</c> (no bytes), <c>key 00..0f</c> or
/// <c>bytes 00..0e</c> (the bytes of a range), <c>bytes 78 56 34 12</c> (the bytes listed),
/// <c>[1, 2, 3]</c> (an array), or a number: decimal or <c>0x</c> hexadecimal, a trailing <c>n</c>
/// of a JavaScript BigInt, and an <c>f</c> or <c>d</c> of a C# literal of a floating-point parameter, ignored.
/// </summary>
internal static partial class CallText {

  [GeneratedRegex(@"^(?<name>\w+)\((?<arguments>.*)\)$")]
  private static partial Regex Call();

  [GeneratedRegex(@"^(?:key|bytes) (?<from>[0-9a-fA-F]{2})\.\.(?<to>[0-9a-fA-F]{2})$")]
  private static partial Regex ByteRange();

  [GeneratedRegex(@"^bytes(?<bytes>(?: [0-9a-fA-F]{2})+)$")]
  private static partial Regex ByteList();

  /// <summary>Reads a call of a method whose parameters are those of <paramref name="method"/>.</summary>
  public static PrototypeCall Parse(string text, MethodInfo method) {
    var match = Call().Match(text.Trim());
    if (!match.Success)
      throw new FormatException($"not a call: {text}");

    var parameters = method.GetParameters();
    var arguments = Split(match.Groups["arguments"].Value);
    if (arguments.Count != parameters.Length)
      throw new FormatException($"{text} has {arguments.Count} arguments, {method.Name} takes {parameters.Length}");

    return new PrototypeCall(match.Groups["name"].Value, [.. arguments.Select((argument, index) => Value(argument, parameters[index].ParameterType))]) {
      Returns = method.ReturnType == typeof(void) ? null : method.ReturnType
    };
  }

  /// <summary>
  /// Reads a call of a JavaScript function, which declares no types: bytes are a <c>byte[]</c>, a
  /// number with a fraction a <c>double</c>, a number with a trailing <c>n</c> an <c>ulong</c>, and any
  /// other number an <c>int</c> where it fits and a <c>double</c> where it does not.
  /// </summary>
  public static PrototypeCall OfScript(string text) {
    var match = Call().Match(text.Trim());
    if (!match.Success)
      throw new FormatException($"not a call: {text}");

    return new PrototypeCall(match.Groups["name"].Value, [.. Split(match.Groups["arguments"].Value).Select(argument => Value(argument, ScriptType(argument)))]);
  }

  private static Type ScriptType(string argument) {
    if (argument.StartsWith("bytes", StringComparison.Ordinal) || argument.StartsWith("key", StringComparison.Ordinal) || argument.StartsWith("empty", StringComparison.Ordinal))
      return typeof(byte[]);

    if (argument.EndsWith('n'))
      return typeof(ulong);

    return int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && argument.Length <= 10
      ? typeof(int) : typeof(double);
  }

  /// <summary>Reads a call of a method of a fixture compiled into this assembly.</summary>
  public static PrototypeCall OfFixture(string text, string fixture)
    => Parse(text, PrototypeFixture.Compiled(fixture, Call().Match(text.Trim()).Groups["name"].Value));

  /// <summary>Reads a call of a method of a C# source, compiled by Roslyn for its parameter types.</summary>
  public static PrototypeCall OfSource(string text, string source)
    => Parse(text, GeneratedCSharp.Compile(source, false).Method(Call().Match(text.Trim()).Groups["name"].Value));

  /// <summary>An argument as a value of a parameter type.</summary>
  public static object Value(string text, Type type) {
    text = text.Trim();
    if (text.StartsWith("bytes of \"", StringComparison.Ordinal) && text.EndsWith('"'))
      return PrototypeCases.Text(text[10..^1]);

    if (text is "empty array" or "empty")
      return Array.CreateInstance(type.GetElementType() ?? typeof(byte), 0);

    if (ByteRange().Match(text) is { Success: true } range) {
      var from = Convert.ToByte(range.Groups["from"].Value, 16);
      var to = Convert.ToByte(range.Groups["to"].Value, 16);
      return Enumerable.Range(from, to - from + 1).Select(value => (byte)value).ToArray();
    }

    if (ByteList().Match(text) is { Success: true } list)
      return list.Groups["bytes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Convert.ToByte(value, 16)).ToArray();

    if (text.StartsWith('[') && text.EndsWith(']')) {
      var element = type.GetElementType() ?? throw new FormatException($"{text} is an array, the parameter is {type}");
      var values = Split(text[1..^1]);
      var array = Array.CreateInstance(element, values.Count);
      for (var index = 0; index < values.Count; ++index)
        array.SetValue(Value(values[index], element), index);

      return array;
    }

    return Number(text.TrimEnd('n'), type);
  }

  /// <summary>A number of a parameter type: a hexadecimal one is taken as the bits of the type.</summary>
  private static object Number(string text, Type type) {
    if (type == typeof(float))
      return float.Parse(text.TrimEnd('f'), CultureInfo.InvariantCulture);

    if (type == typeof(double))
      return double.Parse(text.TrimEnd('d'), CultureInfo.InvariantCulture);

    if (type == typeof(bool))
      return bool.Parse(text);

    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      var bits = ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      return Type.GetTypeCode(type) switch {
        TypeCode.SByte => unchecked((sbyte)bits),
        TypeCode.Byte => unchecked((byte)bits),
        TypeCode.Int16 => unchecked((short)bits),
        TypeCode.UInt16 => unchecked((ushort)bits),
        TypeCode.Int32 => unchecked((int)bits),
        TypeCode.UInt32 => unchecked((uint)bits),
        TypeCode.Int64 => unchecked((long)bits),
        _ => bits
      };
    }

    return Convert.ChangeType(decimal.Parse(text, CultureInfo.InvariantCulture), type, CultureInfo.InvariantCulture);
  }

  /// <summary>The arguments of a call, split at the commas outside brackets and quotes.</summary>
  private static List<string> Split(string text) {
    var parts = new List<string>();
    var depth = 0;
    var quoted = false;
    var start = 0;
    for (var index = 0; index < text.Length; ++index)
      switch (text[index]) {
        case '"':
          quoted = !quoted;
          break;
        case '[' when !quoted:
          ++depth;
          break;
        case ']' when !quoted:
          --depth;
          break;
        case ',' when !quoted && depth == 0:
          parts.Add(text[start..index].Trim());
          start = index + 1;
          break;
      }

    if (text.Trim().Length > 0)
      parts.Add(text[start..].Trim());

    return parts;
  }

}
