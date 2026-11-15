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

  /// <summary>The image type of an IR scalar.</summary>
  public static ImageType ImageOf(IrType type) => type.Kind switch {
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
    _ => throw new ArgumentException($"{type} is no scalar.", nameof(type))
  };

  /// <summary>The signature of an IR function whose parameters and result are scalars, every parameter <c>in</c>.</summary>
  public static CallSignature SignatureOf(IrFunction function)
    => new([.. function.Parameters.Select(parameter => new CallParameter(parameter.Name, ImageOf(parameter.Type), ParameterMode.In))],
      function.ReturnType == IrType.Void ? null : ImageOf(function.ReturnType));

  /// <summary>An argument as the little-endian bytes of its image.</summary>
  public static byte[] Bytes(object value) => value switch {
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
  /// What every call does in the crate the backend writes for a module, built with the switches given:
  /// one outcome per call, in order. The module is translated as <c>translate --to rust</c> would.
  /// </summary>
  public static async Task<IReadOnlyList<string>> OutcomesAsync(IrModule module, IReadOnlyList<PrototypeCall> calls, OverflowChecks checks, RustProfile? profile = null) {
    profile ??= RustProfile.Default;
    var prepared = RustCrates.Prepared(module, profile);
    var files = RustCrates.Emit(module, profile);
    var exports = RustEmitter.Exports(prepared, profile);
    var functions = prepared.Functions.Where(function => exports.ContainsKey(function.Name)).ToList();
    var index = functions.Select((function, position) => (function.Name, position)).ToDictionary(pair => pair.Name, pair => pair.position, StringComparer.Ordinal);
    var crate = RustEmitter.ModuleName(module.Name, profile);
    var execution = await Runner(checks).ExecuteAsync(RustCrates.Package(files), crate,
      [.. functions.Select(function => new RustExport(exports[function.Name], SignatureOf(function)))],
      [.. calls.Select(call => (index[call.Method], call.Arguments.SelectMany(Bytes).ToArray()))]);
    Assert.That(execution.Diagnostics, Is.Empty, () => string.Join("\n", execution.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")) + "\n" + RustCrates.ModuleText(files));
    return [.. calls.Zip(execution.Outcomes!).Select(pair => pair.Second.Panic is { } panic
      ? Trap(panic)
      : PrototypeFixture.Outcome(Value(functions[index[pair.First.Method]].ReturnType, pair.Second.Image!), pair.First.Arguments))];
  }

}
