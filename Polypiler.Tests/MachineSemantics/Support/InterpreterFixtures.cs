using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Interpreter;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Core.MachineSemantics.Tracing;

namespace Polypiler.Tests.MachineSemantics.Support;

/// <summary>
/// The fixtures of the machine-state interpreter, <c>tests/fixtures/native/mach-interp</c>: the routines
/// of <c>routines.mir</c>, and the vectors with the results the C reference <c>routines.ref.c</c>
/// printed under gcc, captured in <c>golden/routines.expected.txt</c>.
/// </summary>
public static class InterpreterFixtures {

  /// <summary>The fixture folder.</summary>
  public static readonly string Directory = Path.Combine(MachineTesting.Fixtures, "mach-interp");

  /// <summary>The routines.</summary>
  public static Lazy<MachineModule> Routines { get; } = new(() => MachineTesting.Verified(File.ReadAllText(Path.Combine(Directory, "routines.mir"))));

  /// <summary>The address map of the IBM PC the fixtures use: ram, the video buffer, the BIOS ROM.</summary>
  public static AddressMap PcVga() => new([
    new("ram", 0x00000, 0x9FFFF, AddressKind.Ram, false),
    new("vga", 0xA0000, 0xBFFFF, AddressKind.Mmio, false),
    new("rom", 0xF0000, 0xFFFFF, AddressKind.Rom, true)
  ], 32, "test-m32");

  /// <summary>One captured line: the routine, its initial state and the reference's final state.</summary>
  /// <param name="Line">The line as captured.</param>
  /// <param name="Routine">The routine.</param>
  /// <param name="Initial">The initial registers and memory.</param>
  /// <param name="Expected">The final state the reference printed, with its outcome.</param>
  public sealed record Vector(string Line, string Routine, Dictionary<string, string> Initial, Dictionary<string, string> Expected) {
    /// <inheritdoc/>
    public override string ToString() => this.Line;
  }

  /// <summary>Every captured vector.</summary>
  public static IEnumerable<Vector> Vectors() {
    foreach (var line in File.ReadAllLines(Path.Combine(Directory, "golden", "routines.expected.txt"))) {
      var parts = line.Split(" => ");
      var input = parts[0].Split(' ');
      static Dictionary<string, string> Fields(IEnumerable<string> tokens)
        => tokens.Select(token => token.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : "", StringComparer.Ordinal);
      var output = parts[1].Split(' ');
      var expected = Fields(output.Skip(1));
      expected["outcome"] = output[0];
      yield return new(line, input[0], Fields(input.Skip(1)), expected);
    }
  }

  /// <summary>Runs a vector on the interpreter; the routine <c>wild</c> runs with the pc-vga map.</summary>
  public static MachineExecution Run(Vector vector) {
    var module = Routines.Value;
    var state = new MachineState(module.Profile!, vector.Routine == "wild" ? PcVga() : null);
    foreach (var (name, value) in vector.Initial)
      if (name.StartsWith('m')) {
        var address = ulong.Parse(name[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        state.Memory.Write(address, Convert.FromHexString(value));
      } else {
        state.SetRegister(name, ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
      }

    return MachineInterpreter.Run(module, vector.Routine, state);
  }

  /// <summary>The interpreter's result in the reference's notation, for a field-by-field comparison.</summary>
  public static Dictionary<string, string> Describe(MachineExecution execution, Vector vector) {
    var trace = execution.Trace;
    var result = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["outcome"] = trace.Outcome.ToString().ToLowerInvariant()
    };
    foreach (var (name, value) in trace.Final.Registers.Where(register => vector.Expected.ContainsKey(register.Key)))
      result[name] = value.ToString("X8", CultureInfo.InvariantCulture);
    foreach (var (name, value) in trace.Final.Flags)
      result[name] = value is { } bit ? (bit ? "1" : "0") : "undefined";
    foreach (var write in trace.Events.OfType<MemoryWrite>())
      for (var index = 0; index < write.Bytes.Length; ++index)
        result["w" + (write.Address + (ulong)index).ToString("X", CultureInfo.InvariantCulture)] = write.Bytes[index].ToString("X2", CultureInfo.InvariantCulture);
    foreach (var trap in trace.Events.OfType<Trap>())
      result["trap"] = trap.Kind == "access-fault" ? $"{trap.Kind}@{trap.At:X}" : trap.Kind;
    return result;
  }

}
