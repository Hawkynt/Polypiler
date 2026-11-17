using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Analysis.Dynamic;

namespace Polypiler.Tests.Analysis.Dynamic;

/// <summary>The grammar of caller contracts (issue #777, test design 3-7, 10, 16, 19 and the malformed outline).</summary>
[TestFixture]
public sealed class ContractParserTests {

  private static ContractAssumption Parsed(string text) {
    Assert.That(ContractParser.TryParse(text, true, ContractParser.Checked, out var result, out var problem), Is.True, problem);
    return result!;
  }

  private static string Refused(string text) {
    Assert.That(ContractParser.TryParse(text, true, ContractParser.Checked, out var result, out var problem), Is.False);
    Assert.That(result, Is.Null);
    return problem!;
  }

  /// <summary>The examples of the grammar, read and written canonically.</summary>
  [TestCase("transform.data: seq<u8>", "transform.data", "seq<u8>")]
  [TestCase("sum.n: int[0, 65535]", "sum.n", "int[0, 65535]")]
  [TestCase("crc.table: seq<u32>[256]", "crc.table", "seq<u32>[256]")]
  [TestCase("f.data:seq<u8>[0..1024]", "f.data", "seq<u8>[0..1024]")]
  [TestCase("area.shape: record{ w: int[0, 65535], h: nullable(int[0, 65535]) }", "area.shape", "record{ w: int[0, 65535], h: nullable(int[0, 65535]) }")]
  [TestCase("f.return: i32", "f.return", "i32")]
  [TestCase("global.limit: int[0x0, 0xFF]", "global.limit", "int[0, 255]")]
  [TestCase("f.x: nonnull(seq<u8>)", "f.x", "seq<u8>")]
  [TestCase("f.x: nonnull(nullable(u8))", "f.x", "u8")]
  [TestCase("f.p: seq<record{ x: i32, y: i32 }>", "f.p", "seq<record{ x: i32, y: i32 }>")]
  [TestCase("f.p: nullable(seq<u8>)", "f.p", "nullable(seq<u8>)")]
  [TestCase("  f . x  :  int [ -5 , 5 ]  ", "f.x", "int[-5, 5]")]
  public void Examples_AreReadCanonically(string text, string target, string contract) {
    var assumption = Parsed(text);
    Assert.That((assumption.Target, assumption.Contract.Text, assumption.Text), Is.EqualTo((target, contract, $"{target}: {contract}")));
    Assert.That(Parsed(assumption.Text), Is.EqualTo(assumption));
  }

  /// <summary>Test design 3: every scalar.</summary>
  [TestCase("u8"), TestCase("u16"), TestCase("u32"), TestCase("u64"), TestCase("i8"), TestCase("i16")]
  [TestCase("i32"), TestCase("i64"), TestCase("f32"), TestCase("f64"), TestCase("bool"), TestCase("text")]
  public void EveryScalar_IsRead(string scalar) => Assert.That(Parsed($"f.x: {scalar}").Contract, Is.EqualTo(new ScalarContract(scalar)));

  /// <summary>Test design 4 and 10: lengths and ranges on their boundaries.</summary>
  [Test]
  public void LengthsAndRanges_OnTheirBoundaries() {
    Assert.That(Parsed("f.x: seq<u8>[256]").Contract, Is.EqualTo(new SequenceContract(new ScalarContract("u8"), 256, 256)));
    Assert.That(Parsed("f.x: seq<u8>[0..1024]").Contract, Is.EqualTo(new SequenceContract(new ScalarContract("u8"), 0, 1024)));
    Assert.That(Parsed("f.x: seq<u8>[5..5]").Contract.Text, Is.EqualTo("seq<u8>[5]"));
    Assert.That(Parsed("f.x: int[5, 5]").Contract, Is.EqualTo(new IntegerContract(5, 5)));
    Assert.That(Parsed("f.x: int[0, 0x10000000000000000]").Contract, Is.EqualTo(new IntegerContract(0, BigInteger.Pow(2, 64))));
  }

  /// <summary>Test design 6 and 6d: the language-native spellings and the language each belongs to.</summary>
  [TestCase("f.x: Uint8Array", "seq<u8>", "Uint8Array", "javascript")]
  [TestCase("f.x: Float64Array", "seq<f64>", "Float64Array", "javascript")]
  [TestCase("f.x: bytes", "seq<u8>", "bytes", "python")]
  [TestCase("f.x: Optional[int[0, 9]]", "nullable(int[0, 9])", "Optional", "python")]
  [TestCase("f.x: arrayref<u8>", "seq<u8>", "arrayref", "perl")]
  [TestCase("f.x: arrayref<u8>[4]", "seq<u8>[4]", "arrayref", "perl")]
  [TestCase("f.x: hashref{ w: i32 }", "record{ w: i32 }", "hashref", "perl")]
  public void Aliases_AreTheirContracts(string text, string contract, string spelling, string language) {
    var assumption = Parsed(text);
    Assert.That(assumption.Contract.Text, Is.EqualTo(contract));
    Assert.That(assumption.Aliases, Is.EqualTo(new[] { (spelling, language) }));
  }

  /// <summary>Test design 16, 6h and the malformed outline: every problem, named.</summary>
  [TestCase("", "empty assumption")]
  [TestCase("transform.data seq<u8>", "expected ':' after the target")]
  [TestCase("transform.data: seq<u9>", "unknown contract 'u9'")]
  [TestCase("sum.n: int[10, 5]", "lower bound 10 exceeds upper bound 5")]
  [TestCase(".data: u8", "expected a function name or 'global' at the start")]
  [TestCase("f: u8", "expected '.' and a parameter, 'return' or a global name after 'f'")]
  [TestCase("f.: u8", "expected a parameter, 'return' or a global name after 'f.'")]
  [TestCase("f.x:", "expected a contract")]
  [TestCase("f.x: [", "expected a contract at '['")]
  [TestCase("f.x: int 0, 5]", "expected '[' after 'int'")]
  [TestCase("f.x: int[0 5]", "expected ',' between the bounds")]
  [TestCase("f.x: int[0, 5", "expected ']' after the upper bound")]
  [TestCase("f.x: int[a, 5]", "'a' is not an integer")]
  [TestCase("f.x: int[, 5]", "expected an integer")]
  [TestCase("f.x: int[0, ]", "expected an integer")]
  [TestCase("f.x: seq u8", "expected '<'")]
  [TestCase("f.x: seq<u8", "expected '>' after 'u8'")]
  [TestCase("f.x: seq<u8>[x]", "'x' is not an integer")]
  [TestCase("f.x: seq<u8>[1..x]", "'x' is not an integer")]
  [TestCase("f.x: seq<u8>[-1]", "length -1 is negative")]
  [TestCase("f.x: seq<u8>[1..-1]", "length -1 is negative")]
  [TestCase("f.x: seq<u8>[5..2]", "shortest length 5 exceeds longest length 2")]
  [TestCase("f.x: seq<u8>[5", "expected ']' after the length")]
  [TestCase("f.x: seq<>", "expected a contract at '>'")]
  [TestCase("f.x: nullable(u8", "expected ')' after 'u8'")]
  [TestCase("f.x: nullable(u7)", "unknown contract 'u7'")]
  [TestCase("f.x: nonnull(u7)", "unknown contract 'u7'")]
  [TestCase("f.x: Optional[u7]", "unknown contract 'u7'")]
  [TestCase("f.x: record w: i32 }", "expected '{' after 'record'")]
  [TestCase("f.x: record{ w i32 }", "expected ':' after the field 'w'")]
  [TestCase("f.x: record{}", "a record lists at least one field")]
  [TestCase("f.x: record{ w: i32, }", "expected a field name")]
  [TestCase("f.x: record{ 1: i32 }", "expected a field name")]
  [TestCase("f.x: record{ w: i32, w: u8 }", "the field 'w' is listed twice")]
  [TestCase("f.x: record{ w: u7 }", "unknown contract 'u7'")]
  [TestCase("f.x: record{ w: i32 ", "expected ',' or '}' after a field")]
  [TestCase("f.x: u8 u8", "unexpected 'u8' after the contract")]
  [TestCase("f.x.y: u8", "expected ':' after the target")]
  public void Malformed_IsRefusedWithItsProblem(string text, string problem) => Assert.That(Refused(text), Is.EqualTo(problem));

  [Test]
  public void Parse_ThrowsWithTheProblem() {
    Assert.That(ContractParser.Parse("f.x: u8").Origin, Is.EqualTo("--assume"));
    Assert.That(() => ContractParser.Parse("f.x u8"), Throws.TypeOf<FormatException>().With.Message.EqualTo("expected ':' after the target"));
  }

  [Test]
  public void Arguments_AreChecked() {
    Assert.That(() => Polypiler.Core.Options.AssumptionParser.TryParseInteger(null!, out _), Throws.ArgumentNullException);
    Assert.That(() => ContractParser.TryParse(null!, true, "o", out _, out _), Throws.ArgumentNullException);
    Assert.That(() => ContractParser.TryParse("f.x: u8", true, null!, out _, out _), Throws.ArgumentNullException);
    Assert.That(() => ContractParser.ParseFile(null!, "s", true, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => ContractParser.ParseFile("p", null!, true, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => ContractParser.ParseFile("p", "s", true, null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 7: a file of three contracts, a comment and a blank line; each with its line as origin.</summary>
  [Test]
  public void AFile_HasOneContractPerLine() {
    var path = Path.Combine(Path.GetTempPath(), "polypiler-contracts-" + Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllText(path, "# contracts of sum\nsum.n: int[0, 65535]\n\nsum.data: seq<u8>  # the bytes\nsum.return: u32\n");
    try {
      var bag = new DiagnosticBag();
      var read = ContractParser.ParseFile(path, "contracts.txt", false, bag);
      Assert.That(bag.Items, Is.Empty);
      Assert.That(read.Select(assumption => (assumption.Text, assumption.Checked, assumption.Origin)), Is.EqualTo(new[] {
        ("sum.n: int[0, 65535]", false, "--assume-file contracts.txt:2"),
        ("sum.data: seq<u8>", false, "--assume-file contracts.txt:4"),
        ("sum.return: u32", false, "--assume-file contracts.txt:5")
      }));
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>A bad line of a file is PLY0514 at its line; the good lines are still read.</summary>
  [Test]
  public void ABadLine_IsReportedAtItsLine() {
    var path = Path.Combine(Path.GetTempPath(), "polypiler-contracts-" + Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllText(path, "f.x: u8\nf.y u8\n");
    try {
      var bag = new DiagnosticBag();
      Assert.That(ContractParser.ParseFile(path, "c.txt", true, bag).Single().Text, Is.EqualTo("f.x: u8"));
      Assert.That(bag.Items.Select(item => (item.Code, item.Message, item.File, item.Line)), Is.EqualTo(new[] {
        ("PLY0514", "invalid assumption 'f.y u8': expected ':' after the target", (string?)"c.txt", 2)
      }));
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>Test design 19: a file that does not exist.</summary>
  [Test]
  public void AMissingFile_IsPLY0514() {
    var bag = new DiagnosticBag();
    Assert.That(ContractParser.ParseFile(Path.Combine(Path.GetTempPath(), "polypiler-no-such-" + Guid.NewGuid().ToString("N")), "missing.txt", true, bag), Is.Empty);
    Assert.That(bag.Items.Single().Message, Does.StartWith("invalid assumption 'missing.txt': cannot read the file: "));
    Assert.That(bag.ExitCode, Is.EqualTo(ExitCode.UsageError));
  }

  /// <summary>The parts of a target, and equality without the spellings.</summary>
  [Test]
  public void Targets_HaveTheirParts() {
    Assert.That((Parsed("f.x: u8").Function, Parsed("f.x: u8").Name, Parsed("f.x: u8").IsReturn, Parsed("f.x: u8").IsGlobal), Is.EqualTo(("f", "x", false, false)));
    Assert.That((Parsed("f.return: u8").IsReturn, Parsed("global.g: u8").IsGlobal, Parsed("global.g: u8").Function, Parsed("global.g: u8").Name), Is.EqualTo((true, true, "", "g")));
    Assert.That(Parsed("f.x: Uint8Array"), Is.EqualTo(Parsed("f.x: seq<u8>")));
    Assert.That(Parsed("f.x: Uint8Array").GetHashCode(), Is.EqualTo(Parsed("f.x: seq<u8>").GetHashCode()));
    Assert.That(Parsed("f.x: u8").Equals(null), Is.False);
    var first = new RecordContract([new ContractField("a", new ScalarContract("u8"))]);
    var second = new RecordContract([new ContractField("a", new ScalarContract("u8"))]);
    Assert.That((first.Equals(second), first.GetHashCode() == second.GetHashCode(), first.Equals(null)), Is.EqualTo((true, true, false)));
    Assert.That(new ScalarContract("u8").ToString(), Is.EqualTo("u8"));
  }

}
