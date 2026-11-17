using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// Test design 19 and 20 of #139: each machine port implemented by a test double for a one-opcode ISA
/// on <c>test-m32</c> — <c>0x01 imm8</c> loads <c>imm8</c> into <c>r0l</c> — and the profile as the
/// machine-state model.
/// </summary>
[TestFixture]
public sealed class MachinePortsTests {

  private sealed class Bytes(ulong start, params byte[] bytes) : IMachineCodeSource {
    public byte? ByteAt(ulong address) => address >= start && address - start < (ulong)bytes.Length ? bytes[address - start] : null;
  }

  private sealed class ToyDisassembler : IDisassembler {

    public static readonly DiagnosticDescriptor NoInstruction = new("PLY0001", DiagnosticSeverity.Error, ExitCode.InternalError, "internal compiler error: {0}", "adr-0003.internal-error");

    public string Isa => "test-m32";

    public DisassembledInstruction? Disassemble(IMachineCodeSource source, ulong address, string mode, IDiagnosticSink sink) {
      if (source.ByteAt(address) is not 0x01 || source.ByteAt(address + 1) is not { } value) {
        sink.Report(NoInstruction.At($"no instruction at 0x{address:X8}"));
        return null;
      }

      return new(address, 2, FlowKind.Sequential, [], true, $"ldl r0l, 0x{value:X2}", value);
    }

  }

  private sealed class ToySemantics : IInstructionSemantics {

    public string Isa => "test-m32";

    public MachineFragment Expand(DisassembledInstruction instruction, IMachineStateModel state, IDiagnosticSink sink) {
      var register = state.Register("r0l")!;
      var origin = new MachineOrigin(state.Isa, "flat", instruction.Address, [0x01, (byte)instruction.Payload], instruction.Text);
      return new([
        MachineInstruction.Create(MachineOpcode.Const, MachineType.Of(register.Bits), "v", new MachineImmediate((byte)instruction.Payload)) with { Origin = origin },
        MachineInstruction.Create(MachineOpcode.RegWrite, MachineType.Of(register.Bits), null, new MachineStorage(register.Name), new MachineValue("v")) with { Origin = origin }
      ], instruction.Flow, instruction.Targets);
    }

  }

  private sealed class ToyAssembler : IAssemblerEmitter {
    public string Isa => "test-m32";
    public string Dialect => "toy";
    public GeneratedFile Write(string unitName, IReadOnlyList<AssemblyLine> lines)
      => new(unitName + ".s", string.Concat(lines.Select(line => $"{line.Label}{(line.Label is null ? "" : ":")}\t{line.Mnemonic} {string.Join(", ", line.Operands)}{(line.Comment is null ? "" : " ; " + line.Comment)}\n")));
  }

  private sealed class ToyWriter : IBinaryWriter {
    public string Format => "raw";
    public ImmutableArray<BinaryArtifact> Write(MachineCodeImage image, string profile, IDiagnosticSink sink) => [new($"out.{profile}.bin", [.. image.Bytes.ToArray()])];
  }

  /// <summary>Test design 19: decode, expand, verify; the profile answers as the state model.</summary>
  [Test]
  public void DisassembleThenExpand_GivesAFragmentThatVerifies() {
    var profile = MachineTesting.Profile("test-m32");
    IMachineStateModel state = profile;
    Assert.That(state.Isa, Is.EqualTo("test-m32"));
    Assert.That(state.AddressBits, Is.EqualTo(32));
    Assert.That(state.ByteOrder, Is.EqualTo(ByteOrder.Little));
    Assert.That(state.Register("r0l"), Is.EqualTo(profile.Register("r0l")));
    Assert.That(state.Register("r9"), Is.Null);
    Assert.That(state.Flag("c"), Is.Not.Null);

    var sink = new DiagnosticBag();
    var decoded = new ToyDisassembler().Disassemble(new Bytes(0x1000, 0x01, 0x7F), 0x1000, "flat", sink)!;
    Assert.That(decoded, Is.EqualTo(new DisassembledInstruction(0x1000, 2, FlowKind.Sequential, [], true, "ldl r0l, 0x7F", (byte)0x7F)));
    var fragment = new ToySemantics().Expand(decoded, state, sink);
    Assert.That(sink.Items, Is.Empty);
    Assert.That(fragment.Flow, Is.EqualTo(FlowKind.Sequential));

    var block = new MachineBlock("entry", [.. fragment.Instructions, MachineInstruction.Create(MachineOpcode.Ret, MachineType.Void, null)]);
    var module = new MachineModule(profile, [new MachineFunction("toy", [block])]) { Mode = "flat" };
    Assert.That(MachineVerifier.Verify(module), Is.Empty);
    Assert.That(MachineSyntax.Print(module), Is.EqualTo("machine test-m32\nmode flat\n\n@toy {\nentry:\n  %v = Const.u8 0x7F !at(0x00001000, \"01 7F\", \"ldl r0l, 0x7F\")\n  RegWrite.u8 @r0l, %v !at(0x00001000, \"01 7F\", \"ldl r0l, 0x7F\")\n  Ret\n}\n"));
    Assert.That(fragment, Is.EqualTo(new ToySemantics().Expand(decoded, state, sink)));
    Assert.That(fragment.GetHashCode(), Is.EqualTo(new ToySemantics().Expand(decoded, state, sink).GetHashCode()));
  }

  /// <summary>Test design 20: an address without a byte gives null and a diagnostic, no exception.</summary>
  [Test]
  public void DisassembleWithoutABye_ReportsAndReturnsNull() {
    var sink = new DiagnosticBag();
    Assert.That(new ToyDisassembler().Disassemble(new Bytes(0), 0x2000, "flat", sink), Is.Null);
    Assert.That(sink.Items.Select(item => item.Message), Is.EqualTo(new[] { "no instruction at 0x00002000" }));
  }

  [Test]
  public void AssemblerAndBinaryWriter_ExchangeTheirRecords() {
    var line = new AssemblyLine("start", "ldl", ["r0l", "0x7F"], "load");
    Assert.That(line, Is.EqualTo(new AssemblyLine("start", "ldl", ["r0l", "0x7F"], "load")));
    Assert.That(line.GetHashCode(), Is.EqualTo(new AssemblyLine("start", "ldl", ["r0l", "0x7F"], "load").GetHashCode()));
    Assert.That(line.Equals(null), Is.False);
    var file = new ToyAssembler().Write("unit", [line, new AssemblyLine(null, "ret", [], null)]);
    Assert.That(file, Is.EqualTo(new GeneratedFile("unit.s", "start:\tldl r0l, 0x7F ; load\n\tret \n")));

    var image = new MachineCodeImage(new byte[] { 0x01, 0x7F }, 0x1000, 0x1000, ImmutableDictionary<string, ulong>.Empty.Add("start", 0x1000));
    var artifacts = new ToyWriter().Write(image, "flat", new DiagnosticBag());
    Assert.That(artifacts, Is.EqualTo(new[] { new BinaryArtifact("out.flat.bin", [0x01, 0x7F]) }));
    Assert.That(artifacts[0].GetHashCode(), Is.EqualTo(new BinaryArtifact("out.flat.bin", [0x01, 0x7F]).GetHashCode()));
    Assert.That(artifacts[0].Equals(null), Is.False);
    Assert.That(new ToyWriter().Format + new ToyAssembler().Dialect + new ToyAssembler().Isa + new ToySemantics().Isa + new ToyDisassembler().Isa, Is.EqualTo("rawtoytest-m32test-m32test-m32"));
  }

  [Test]
  public void PortRecords_CompareByContent() {
    var a = new DisassembledInstruction(1, 2, FlowKind.ConditionalJump, [5, 6], true, "jz 5", "p");
    Assert.That(a, Is.EqualTo(new DisassembledInstruction(1, 2, FlowKind.ConditionalJump, [5, 6], true, "jz 5", "p")));
    Assert.That(a.GetHashCode(), Is.EqualTo(new DisassembledInstruction(1, 2, FlowKind.ConditionalJump, [5, 6], true, "jz 5", "p").GetHashCode()));
    Assert.That(a, Is.Not.EqualTo(a with { Targets = [5] }));
    Assert.That(a.Equals(null), Is.False);
    Assert.That(new MachineFragment([], FlowKind.Return, []).Equals(null), Is.False);
    Assert.That(Enum.GetValues<FlowKind>(), Has.Length.EqualTo(9));
  }

}
