using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra.Ci;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Generated Rust as the pinned toolchain builds and runs it: a module through the backend into a
/// crate, the crate through <see cref="RustRunner"/> with a driver that calls its exports, the outcome
/// of every call in the notation of <see cref="PrototypeFixture.Outcome"/>, so that it compares with
/// the C# compiler's build of the fixture and with the interpreter.
/// </summary>
internal static class RustRun {

  /// <summary>A runner of the Rust layer with the build switches given, offline, at the stable toolchain of the lock.</summary>
  public static RustRunner Runner(OverflowChecks checks, bool debugAssertions = true) {
    var commands = RustLayerRun.Commands;
    var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["RUSTUP_TOOLCHAIN"] = commands.Stable,
      ["RUSTUP_AUTO_INSTALL"] = "0"
    };
    if (commands.RustupHome is { } home)
      environment[RustCommands.RustupHomeVariable] = home;

    return new RustRunner(new CrossLanguageRunner(RustLayerRun.Sandbox, Toolchains), new RustBuildOptions(checks, debugAssertions), environment);
  }

  /// <summary>The image type of an IR scalar, or of an array of scalars as a <c>seq</c>.</summary>
  public static ImageType ImageOf(IrType type) => type.Kind switch {
    IrTypeKind.Optional => ImageType.Opt(ImageOf(type.Underlying!)),
    IrTypeKind.Array or IrTypeKind.Span or IrTypeKind.ReadOnlySpan => ImageType.Seq(ImageOf(type.Element!)),
    IrTypeKind.String => ImageType.Utf16,
    IrTypeKind.Bool => ImageType.Bool,
    IrTypeKind.I8 => ImageType.I8,
    IrTypeKind.U8 => ImageType.U8,
    IrTypeKind.I16 => ImageType.I16,
    IrTypeKind.U16 => ImageType.U16,
    IrTypeKind.I32 => ImageType.I32,
    IrTypeKind.U32 => ImageType.U32,
    IrTypeKind.I64 => ImageType.I64,
    IrTypeKind.U64 => ImageType.U64,
    IrTypeKind.F32 => ImageType.F32,
    IrTypeKind.F64 => ImageType.F64,
    // A struct of scalars, a tuple of the source: the record of its fields as the Rust struct names them.
    _ when type.StructType is { } declared && declared.Fields.All(field => field.Type.StructType is null)
      => ImageType.Record([.. declared.Fields.Select(field => new ImageField(Polypiler.Core.Backends.Rust.Naming.RustNames.Snake(field.Name.Trim('_'), RustProfile.Default.Edition), ImageOf(field.Type)))]),
    _ => throw new ArgumentException($"{type} is no scalar.", nameof(type))
  };

  /// <summary>
  /// The signature of an IR function whose parameters and result are scalars or arrays of them: a
  /// scalar <c>in</c>, an array <c>inout</c>, since the outcome shows every array after the call.
  /// </summary>
  public static CallSignature SignatureOf(IrFunction function, Func<int, bool>? takesNull = null)
    => new([.. function.Parameters.Select((parameter, index) => new CallParameter(parameter.Name,
        parameter.Type.IsArray && takesNull?.Invoke(index) == true ? ImageType.Opt(ImageOf(parameter.Type)) : ImageOf(parameter.Type),
        parameter.Type.IsSequence ? ParameterMode.InOut : ParameterMode.In))],
      function.ReturnType == IrType.Void ? null : ImageOf(function.ReturnType));

  /// <summary>An argument as the little-endian bytes of its image type: an optional one as its presence byte and its value.</summary>
  public static byte[] Bytes(object? value, ImageType type) => type.Kind switch {
    ImageKind.Optional when value is null => [0],
    ImageKind.Optional => [1, .. Bytes(value!, type.Element!)],
    _ => Bytes(value!)
  };

  /// <summary>An argument as the little-endian bytes of its image; an array as its count and its elements.</summary>
  public static byte[] Bytes(object value) => value switch {
    Array array => [.. BitConverter.GetBytes((uint)array.Length), .. array.Cast<object>().SelectMany(Bytes)],
    string text => [.. BitConverter.GetBytes((uint)text.Length), .. text.SelectMany(BitConverter.GetBytes)],
    bool truth => [truth ? (byte)1 : (byte)0],
    sbyte number => [unchecked((byte)number)],
    byte number => [number],
    short number => BitConverter.GetBytes(number),
    ushort number => BitConverter.GetBytes(number),
    char unit => BitConverter.GetBytes(unit),
    int number => BitConverter.GetBytes(number),
    uint number => BitConverter.GetBytes(number),
    long number => BitConverter.GetBytes(number),
    ulong number => BitConverter.GetBytes(number),
    float number => BitConverter.GetBytes(number),
    double number => BitConverter.GetBytes(number),
    _ => throw new ArgumentException($"{value.GetType()} is no scalar.", nameof(value))
  };

  /// <summary>The values an output image holds: the result, then the contents of every array or view argument, each as the .NET value of its IR type.</summary>
  public static (object? Result, object[] Arguments) Values(IrFunction function, CallSignature signature, object[] arguments, byte[] image) {
    var at = 0;
    var result = Present(signature.Result, image, ref at) ? Read(function.ReturnType.Underlying ?? function.ReturnType, image, ref at) : null;
    var after = arguments.Select((argument, index) => function.Parameters[index].Type.IsSequence
      ? Present(signature.Parameters[index].Type, image, ref at) ? Read(function.Parameters[index].Type, image, ref at)! : argument
      : argument).ToArray();
    Assert.That(at, Is.EqualTo(image.Length), "the output image holds more than the result and the arrays");
    return (result, after);
  }

  /// <summary>Whether the value at a position of an image is there: the presence byte of an optional one, which the position moves past; always for any other.</summary>
  private static bool Present(ImageType? type, byte[] image, ref int at) {
    if (type?.Kind != ImageKind.Optional)
      return true;

    return image[at++] != 0;
  }

  /// <summary>The value at a position of an image, which moves past it.</summary>
  private static object? Read(IrType type, byte[] image, ref int at) {
    if (type.IsString) {
      var length = BitConverter.ToInt32(image, at);
      var units = new char[length];
      for (var index = 0; index < length; ++index)
        units[index] = (char)BitConverter.ToUInt16(image, at + 4 + 2 * index);

      at += 4 + 2 * length;
      return new string(units);
    }

    if (type.IsSequence) {
      var count = BitConverter.ToInt32(image, at);
      at += 4;
      var element = type.Element!;
      var array = Array.CreateInstance(Value(element, new byte[Width(element)])!.GetType(), count);
      for (var index = 0; index < count; ++index)
        array.SetValue(Read(element, image, ref at), index);

      return array;
    }

    // A struct: its fields in order, as Show writes a struct.
    if (type.StructType is { } declared) {
      var fields = new object?[declared.Fields.Count];
      for (var index = 0; index < fields.Length; ++index)
        fields[index] = Read(declared.Fields[index].Type, image, ref at);

      return fields;
    }

    var width = Width(type);
    var value = Value(type, image[at..(at + width)]);
    at += width;
    return value;
  }

  /// <summary>The bytes of a scalar's image.</summary>
  private static int Width(IrType type) => type.Kind switch {
    IrTypeKind.Void => 0,
    IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8 => 1,
    IrTypeKind.I16 or IrTypeKind.U16 => 2,
    IrTypeKind.I32 or IrTypeKind.U32 or IrTypeKind.F32 => 4,
    _ => 8
  };

  /// <summary>An output image as the .NET value of its IR type, as the interpreter gives it.</summary>
  public static object? Value(IrType type, byte[] image) => type.Kind switch {
    IrTypeKind.Void => null,
    IrTypeKind.Bool => image[0] != 0,
    IrTypeKind.I8 => unchecked((sbyte)image[0]),
    IrTypeKind.U8 => image[0],
    IrTypeKind.I16 => BitConverter.ToInt16(image),
    IrTypeKind.U16 => BitConverter.ToUInt16(image),
    IrTypeKind.I32 => BitConverter.ToInt32(image),
    IrTypeKind.U32 => BitConverter.ToUInt32(image),
    IrTypeKind.I64 => BitConverter.ToInt64(image),
    IrTypeKind.U64 => BitConverter.ToUInt64(image),
    IrTypeKind.F32 => BitConverter.ToSingle(image),
    _ => BitConverter.ToDouble(image)
  };

  /// <summary>The outcome of a call that panicked: the trap of the IR whose name or Rust message it carries, or the message itself.</summary>
  public static string Trap(string panic)
    => RustTraps.TrapName(panic) is { } name && Enum.TryParse<IrTrap>(name, out var trap) ? PrototypeFixture.Trap(trap) : "panic: " + panic;

  /// <summary>
  /// Formats a failed Rust runner execution with every diagnostic (and its cause when present) followed by
  /// every generated file's path and content in input order, so multi-file crates never hide the runner's
  /// original diagnostic.
  /// </summary>
  internal static string FailureMessage(IReadOnlyList<Polypiler.Core.Diagnostic> diagnostics, IReadOnlyList<Polypiler.Core.GeneratedFile> files) {
    ArgumentNullException.ThrowIfNull(diagnostics);
    ArgumentNullException.ThrowIfNull(files);
    var parts = new List<string>(diagnostics.Count + files.Count);
    foreach (var diagnostic in diagnostics)
      parts.Add(string.IsNullOrEmpty(diagnostic.Cause) ? diagnostic.ToString() : $"{diagnostic}: {diagnostic.Cause}");
    foreach (var file in files)
      parts.Add($"--- {file.Path} ---\n{file.Content}");
    return string.Join("\n", parts);
  }

  /// <summary>
  /// What every call does in the crate the backend writes for a module, built with the switches given:
  /// one outcome per call, in order. The module is translated as <c>translate --to rust</c> would. An
  /// error an export returns under the trap policy <c>result</c> is read as the trap it displays, and
  /// under that policy no call may panic.
  /// </summary>
  public static async Task<IReadOnlyList<string>> OutcomesAsync(IrModule module, IReadOnlyList<PrototypeCall> calls, OverflowChecks checks, RustProfile? profile = null, RustEmitOptions? options = null) {
    profile ??= RustProfile.Default;
    var prepared = RustCrates.Prepared(module, profile);
    var files = RustCrates.Emit(module, profile, options);
    var exports = RustEmitter.Exports(prepared, profile, options);
    var called = calls.Select(call => call.Method).ToHashSet(StringComparer.Ordinal);
    var functions = prepared.Functions.Where(function => exports.ContainsKey(function.Name) && called.Contains(function.Name)).ToList();
    var signatures = functions.ToDictionary(function => function.Name, function => SignatureOf(function, index => RustEmitter.TakesNull(prepared, function.Name, index)), StringComparer.Ordinal);
    var index = functions.Select((function, position) => (function.Name, position)).ToDictionary(pair => pair.Name, pair => pair.position, StringComparer.Ordinal);
    var crate = options?.PackageName ?? RustEmitter.ModuleName(module.Name, profile);
    var fallible = RustEmitter.Fallible(prepared, profile);
    var execution = await Runner(checks).ExecuteAsync(RustCrates.Package(files), crate,
      [.. functions.Select(function => new RustExport(exports[function.Name], signatures[function.Name]) {
        Converted = function.ReturnType.IsEnum || function.Parameters.Any(parameter => parameter.Type.IsEnum),
        Fallible = fallible.Contains(function.Name)
      })],
      [.. calls.Select(call => (index[call.Method], call.Arguments.SelectMany((argument, position) => Bytes(argument, signatures[call.Method].Parameters[position].Type)).ToArray()))]);
    Assert.That(execution.Diagnostics, Is.Empty, () => FailureMessage(execution.Diagnostics, files));
    if (profile.TrapPolicy == RustTrapPolicy.Result)
      Assert.That(calls.Zip(execution.Outcomes!).Where(pair => pair.Second.Panic is not null).Select(pair => $"{pair.First}: {pair.Second.Panic}"), Is.Empty, "no call panics under the trap policy result");

    return [.. calls.Zip(execution.Outcomes!).Select(pair => {
      if ((pair.Second.Panic ?? pair.Second.Error) is { } panic)
        return Trap(panic);

      var (result, arguments) = Values(functions[index[pair.First.Method]], signatures[pair.First.Method], pair.First.Arguments, pair.Second.Image!);
      return PrototypeFixture.Outcome(result, arguments);
    })];
  }

}
