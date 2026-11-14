using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="BinarySpecFamily"/> through the whole engine: the seed documents of <c>spec/binary/</c>,
/// the schema's limits, every rule and every fixture of <c>tests/fixtures/spec/binary/</c>.
/// </summary>
[TestFixture]
public sealed class BinarySpecFamilyTests {

  private const string Instructions = "binary/mos6502/instructions.yaml";

  private const string Tokens = "binary/c64-basic-v2/tokens.yaml";

  private const string Format = "binary/c64-prg/format.yaml";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private static readonly string _fixtures = Path.Combine(_root, "tests", "fixtures", "spec", "binary");

  private static string Seed(string path) => File.ReadAllText(Path.Combine(_root, "spec", path));

  private static string FormatText => File.ReadAllText(Path.Combine(_fixtures, "format", Format));

  private static string Invalid(string name) => File.ReadAllText(Path.Combine(_fixtures, "invalid", name));

  /// <summary>A text with one occurrence of a part replaced.</summary>
  private static string Replace(string text, string old, string replacement) {
    Assert.That(text.Split(old), Has.Length.EqualTo(2), $"'{old}' occurs once");
    return text.Replace(old, replacement, StringComparison.Ordinal);
  }

  /// <summary>The diagnostics of the binary family over the given documents, as text lines.</summary>
  private static string[] Lines(params (string Path, string Text)[] documents) {
    using var repository = new SpecRepository();
    repository.Write("spec/schemas/binary.schema.json", File.ReadAllText(Path.Combine(_root, "spec", "schemas", "binary.schema.json")));
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository.Lines("binary");
  }

  private static string[] Instruction(string old, string replacement) => Lines((Instructions, Replace(Seed(Instructions), old, replacement)));

  private static string[] Token(string old, string replacement) => Lines((Tokens, Replace(Seed(Tokens), old, replacement)));

  private static string[] Field(string fields) => Lines((Format, FormatText[..FormatText.IndexOf("  fields:", StringComparison.Ordinal)] + "  fields:\n" + fields));

  /// <summary>The path a fixture document belongs at: <c>BINARY.&lt;name&gt;.&lt;kind&gt;</c> lives at <c>binary/&lt;name&gt;/&lt;kind&gt;.yaml</c>.</summary>
  private static string PathOf(string text) {
    var id = text.Split('\n')[0]["id: ".Length..].Split('.');
    return $"binary/{id[1]}/{id[2]}.yaml";
  }

  private const string Lda = "{opcode: \"0xA9\", mnemonic: LDA, mode: immediate, length: 2,";

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<BinarySpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("binary", "spec/schemas/binary.schema.json")));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "spec", "schemas", "binary.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void RepositorySpecTree_HasNoBinaryErrors() {
    var validation = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(_root, "spec"), _root), "binary");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((2, 0)));
  }

  [TestCase(Instructions)]
  [TestCase(Tokens)]
  public void ValidFixture_IsACopyOfTheSeedDocument(string path)
    => Assert.That(File.ReadAllText(Path.Combine(_fixtures, "valid", path)), Is.EqualTo(Seed(path)));

  /// <summary>Test design 1 and 2.</summary>
  [TestCase(Instructions)]
  [TestCase(Tokens)]
  public void SeedDocument_IsValid(string path) => Assert.That(Lines((path, Seed(path))), Is.Empty);

  /// <summary>Test design 3: each kind is valid.</summary>
  [Test]
  public void FormatDocument_IsValid() => Assert.That(Lines((Format, FormatText)), Is.Empty);

  /// <summary>Test design 3: a property of another kind is a schema violation.</summary>
  [TestCase(Instructions, "  kind: instructions\n", "  kind: instructions\n  dialect: c64-basic-v2\n", "/spec/dialect")]
  [TestCase(Tokens, "  kind: tokens\n", "  kind: tokens\n  magic: \"0x0108\"\n", "/spec/magic")]
  [TestCase(Format, "  kind: format\n", "  kind: format\n  flags: [C]\n", "/spec/flags")]
  public void PropertyOfAnotherKind_IsPLY0040(string path, string old, string replacement, string pointer) {
    var text = path == Format ? FormatText : Seed(path);
    Assert.That(Lines((path, Replace(text, old, replacement))), Has.Some.Contains($"error PLY0040: {pointer}:"));
  }

  [Test]
  public void FormatWithMagic_IsValid() => Assert.That(Lines((Format, Replace(FormatText, "  kind: format\n", "  kind: format\n  magic: \"0x0108\"\n"))), Is.Empty);

  [Test]
  public void BigEndianField_IsValid() => Assert.That(Field("    - {name: header, offset: 0, size: 2, byteOrder: big, meaning: a header}\n"), Is.Empty);

  /// <summary>Test design 4.</summary>
  [Test]
  public void UndocumentedInstruction_IsValid()
    => Assert.That(Instruction("    - {opcode: \"0xEA\"", "    - {opcode: \"0x02\", mnemonic: JAM, mode: implied, length: 1, flags: [], documented: false}\n    - {opcode: \"0xEA\""), Is.Empty);

  /// <summary>Test design 5: a variant with its parent, without one, and with an unknown parent.</summary>
  [Test]
  public void VariantWithParent_IsValid() {
    var variant = File.ReadAllText(Path.Combine(_fixtures, "variant", "binary", "wdc65c02", "instructions.yaml"));
    Assert.That(Lines((Instructions, Seed(Instructions)), ("binary/wdc65c02/instructions.yaml", variant)), Is.Empty);
  }

  [Test]
  public void VariantWithUnknownParent_IsPLY0043AtTheParent()
    => Assert.That(Instruction("variant: nmos,", "variant: nmos, parent: BINARY.mos6510.instructions,"),
      Is.EqualTo(new[] { "binary/mos6502/instructions.yaml(14,47): error PLY0043: unknown spec id 'BINARY.mos6510.instructions'" }));

  [Test]
  public void ParentThatIsNoInstructionDocument_IsPLY0040()
    => Assert.That(Instruction("variant: nmos,", "variant: nmos, parent: BINARY.c64-basic-v2.tokens,").Single(), Does.Contain("error PLY0040: /spec/cpu/parent:"));

  /// <summary>Test design 6 and 7: opcodes of one, two and four bytes are valid.</summary>
  [TestCase("0xCB40")]
  [TestCase("0x0F38F001")]
  public void MultiByteOpcode_IsValidWithItsLength(string opcode)
    => Assert.That(Instruction("{opcode: \"0xEA\", mnemonic: NOP, mode: implied, length: 1,", $"{{opcode: \"{opcode}\", mnemonic: NOP, mode: implied, length: {(opcode.Length - 2) / 2},"), Is.Empty);

  /// <summary>Test design 7: no byte, five bytes, odd digits, lower case and a number are schema violations.</summary>
  [TestCase("\"0x\"")]
  [TestCase("\"0x0F38F00102\"")]
  [TestCase("\"0xA\"")]
  [TestCase("\"0xa9\"")]
  [TestCase("0xA9")]
  public void MalformedOpcode_IsPLY0040(string opcode)
    => Assert.That(Instruction("{opcode: \"0xA9\"", $"{{opcode: {opcode}").Single(), Does.Contain("error PLY0040: /spec/instructions/0/opcode:"));

  /// <summary>Test design 8.</summary>
  [TestCase(-1, false)]
  [TestCase(0, true)]
  [TestCase(8, true)]
  [TestCase(9, false)]
  public void OperandBytes_Boundaries(int operandBytes, bool valid) {
    var lines = Instruction("{name: implied, operandBytes: 0}, ", $"{{name: implied, operandBytes: 0}}, {{name: wide, operandBytes: {operandBytes}}}, ");
    Assert.That(lines.Length == 0, Is.EqualTo(valid), string.Join("\n", lines));
    if (!valid)
      Assert.That(lines.Single(), Does.Contain("error PLY0040: /spec/modes/1/operandBytes:"));
  }

  /// <summary>Test design 9.</summary>
  [TestCase(1)]
  [TestCase(3)]
  public void LengthBesideOpcodePlusOperand_IsLengthMatchesMode(int length)
    => Assert.That(Instruction(Lda, $"{{opcode: \"0xA9\", mnemonic: LDA, mode: immediate, length: {length},"),
      Is.EqualTo(new[] { $"binary/mos6502/instructions.yaml(19,64): error PLY0046: instruction 0xA9 has length {length}, opcode (1) + mode 'immediate' (1) is 2 (rule: length-matches-mode)" }));

  /// <summary>Test design 10: the ends of the range are inside it.</summary>
  [TestCase("0x80")]
  [TestCase("0xCB")]
  public void TokenAtTheEndOfTheRange_IsValid(string value) => Assert.That(Token("{value: \"0x89\", text: GOTO}", $"{{value: \"{value}\", text: GO}}").Where(line => !line.Contains("token-unique", StringComparison.Ordinal)), Is.Empty);

  [TestCase("0x7F", "0x80-0xCB")]
  [TestCase("0xCC", "0x80-0xCB")]
  public void TokenOutsideTheRange_IsTokenInRange(string value, string range)
    => Assert.That(Token("{value: \"0x89\", text: GOTO}", $"{{value: \"{value}\", text: GOTO}}"),
      Is.EqualTo(new[] { $"binary/c64-basic-v2/tokens.yaml(19,15): error PLY0046: token {value} (GOTO) is outside the range {range} (rule: token-in-range)" }));

  [Test]
  public void TwoByteTokenInsideATwoByteRange_IsValid()
    => Assert.That(Token("{min: \"0x80\", max: \"0xCB\"}", "{min: \"0x80\", max: \"0xFFFF\"}").Concat(Lines((Tokens, Replace(Replace(Seed(Tokens), "max: \"0xCB\"", "max: \"0xFFFF\""), "0x99", "0xCE0A")))), Is.Empty);

  /// <summary>Test design 11.</summary>
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(128, true)]
  [TestCase(129, false)]
  public void RegisterBits_Boundaries(int bits, bool valid) {
    var lines = Instruction("{name: P, bits: 8}", $"{{name: P, bits: {bits}}}");
    Assert.That(lines.Length == 0, Is.EqualTo(valid), string.Join("\n", lines));
    if (!valid)
      Assert.That(lines.Single(), Does.Contain("error PLY0040: /spec/registers/5/bits:"));
  }

  /// <summary>Test design 12.</summary>
  [TestCase(2)]
  [TestCase(3)]
  public void FieldAdjacentOrAfterAGap_IsValid(int offset)
    => Assert.That(Field($"    - {{name: header, offset: 0, size: 2, byteOrder: little, meaning: a}}\n    - {{name: data, offset: {offset}, size: 1, byteOrder: little, meaning: b}}\n"), Is.Empty);

  [Test]
  public void FieldOverlappingByOneByte_IsFieldsOrdered()
    => Assert.That(Field("    - {name: header, offset: 0, size: 2, byteOrder: little, meaning: a}\n    - {name: data, offset: 1, size: 1, byteOrder: little, meaning: b}\n"),
      Is.EqualTo(new[] { "binary/c64-prg/format.yaml(16,28): error PLY0046: field 'data' at offset 1 overlaps 'header' (offset 0, size 2) (rule: fields-ordered)" }));

  [Test]
  public void FieldBeforeThePrevious_IsFieldsOrdered()
    => Assert.That(Field("    - {name: header, offset: 4, size: 2, byteOrder: little, meaning: a}\n    - {name: data, offset: 0, size: 1, byteOrder: little, meaning: b}\n"),
      Is.EqualTo(new[] { "binary/c64-prg/format.yaml(16,28): error PLY0046: field 'data' at offset 0 comes before 'header' (offset 4) (rule: fields-ordered)" }));

  /// <summary>Test design 13.</summary>
  [Test]
  public void EmptyInstructionList_IsPLY0040()
    => Assert.That(Lines((Instructions, Seed(Instructions)[..Seed(Instructions).IndexOf("  instructions:", StringComparison.Ordinal)] + "  instructions: []\n")).Single(), Does.Contain("error PLY0040: /spec/instructions:"));

  [Test]
  public void EmptyTokenList_IsPLY0040()
    => Assert.That(Lines((Tokens, Seed(Tokens)[..Seed(Tokens).IndexOf("  tokens:", StringComparison.Ordinal)] + "  tokens: []\n")).Single(), Does.Contain("error PLY0040: /spec/tokens:"));

  [Test]
  public void EmptyFieldList_IsPLY0040() => Assert.That(Field("    []\n").Single(), Does.Contain("error PLY0040: /spec/fields:"));

  /// <summary>Test design 14–20, 22 and the rule <c>layout</c>: every rule fixture, at the path its ID names.</summary>
  [TestCase("opcode-unique.yaml", "binary/mos6502/instructions.yaml(20,16): error PLY0046: opcode 0xA9 is defined twice (LDA immediate, LDX immediate) (rule: opcode-unique)")]
  [TestCase("mode-declared.yaml", "binary/mos6502/instructions.yaml(19,45): error PLY0046: instruction 0xA9 uses mode 'imm', declared modes are implied, immediate, absolute (rule: mode-declared)")]
  [TestCase("flags-declared.yaml", "binary/mos6502/instructions.yaml(20,98): error PLY0046: instruction 0x69 affects flag 'H', declared flags are N, V, B, D, I, Z, C (rule: flags-declared)")]
  [TestCase("length-matches-mode.yaml", "binary/mos6502/instructions.yaml(19,64): error PLY0046: instruction 0xA9 has length 3, opcode (1) + mode 'immediate' (1) is 2 (rule: length-matches-mode)")]
  [TestCase("register-unique.yaml", "binary/mos6502/instructions.yaml(15,62): error PLY0046: register 'X' is defined twice (rule: register-unique)")]
  [TestCase("token-unique.yaml", "binary/c64-basic-v2/tokens.yaml(22,15): error PLY0046: token value 0x99 is defined twice (PRINT, ?) (rule: token-unique)")]
  [TestCase("token-in-range.yaml", "binary/c64-basic-v2/tokens.yaml(22,15): error PLY0046: token 0x7F (X) is outside the range 0x80-0xCB (rule: token-in-range)")]
  [TestCase("fields-ordered.yaml", "binary/c64-prg/format.yaml(16,28): error PLY0046: field 'data' at offset 1 overlaps 'loadAddress' (offset 0, size 2) (rule: fields-ordered)")]
  [TestCase("layout.yaml", "binary/mos6502/instructions.yaml(13,9): error PLY0046: kind 'tokens' must be in a file named tokens.yaml, found instructions.yaml (rule: layout)")]
  [TestCase("extra-property.yaml", "binary/mos6502/instructions.yaml(15,11): error PLY0040: /spec/endian: property 'endian' is not allowed")]
  public void RuleFixture_IsReported(string fixture, string expected) {
    var text = Invalid(fixture);
    Assert.That(Lines((PathOf(text), text)), Is.EqualTo(new[] { expected }));
  }

  [Test]
  public void TokenTextTwice_IsTokenUnique()
    => Assert.That(Token("{value: \"0x89\", text: GOTO}", "{value: \"0x89\", text: END}"),
      Is.EqualTo(new[] { "binary/c64-basic-v2/tokens.yaml(19,29): error PLY0046: token text 'END' is defined twice (0x80, 0x89) (rule: token-unique)" }));

  /// <summary>Test design 21: each required property removed.</summary>
  [Test]
  public void EveryMissingFixture_IsARequiredPropertyViolation() {
    var fixtures = Directory.GetFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml");
    Assert.That(fixtures, Has.Length.EqualTo(32));
    foreach (var fixture in fixtures) {
      var text = File.ReadAllText(fixture);
      var lines = Lines((PathOf(text), text));
      var property = Path.GetFileNameWithoutExtension(fixture).Split('-')[^1];
      Assert.That(lines, Has.Length.EqualTo(1), fixture);
      Assert.That(lines[0], Does.Match($@"error PLY0040: /spec(/[a-zA-Z]+(/0)?)?: Required properties \[.*""{property}"".*\] are not present"), fixture);
    }
  }

  /// <summary>Test design 23 and the other ways out of the layout.</summary>
  [TestCase("binary/mos6502.yaml", "binary/mos6502.yaml(1,1): error PLY0046: path 'binary/mos6502.yaml' does not follow binary/<name>/<kind>.yaml with kind instructions, tokens, format (rule: layout)")]
  [TestCase("binary/mos6502/nmos/instructions.yaml", "binary/mos6502/nmos/instructions.yaml(1,1): error PLY0046: path 'binary/mos6502/nmos/instructions.yaml' does not follow binary/<name>/<kind>.yaml with kind instructions, tokens, format (rule: layout)")]
  [TestCase("binary/mos6502/opcodes.yaml", "binary/mos6502/opcodes.yaml(1,1): error PLY0046: path 'binary/mos6502/opcodes.yaml' does not follow binary/<name>/<kind>.yaml with kind instructions, tokens, format (rule: layout)")]
  [TestCase("binary/mos6510/instructions.yaml", "binary/mos6510/instructions.yaml(1,5): error PLY0046: id 'BINARY.mos6502.instructions' must be 'BINARY.mos6510.instructions' for binary/mos6510/instructions.yaml (rule: layout)")]
  public void DocumentOutsideTheLayout_IsLayout(string path, string expected) => Assert.That(Lines((path, Seed(Instructions))), Is.EqualTo(new[] { expected }));

  [Test]
  public void Check_RefusesNull() {
    var family = new BinarySpecFamily();
    var document = SpecDocument.Parse(Instructions, Seed(Instructions));
    using var repository = new SpecRepository();
    Assert.That(() => family.Check(null!, repository.Tree()), Throws.ArgumentNullException);
    Assert.That(() => family.Check(document, null!), Throws.ArgumentNullException);
  }

}
