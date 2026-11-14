using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Acceptance.Formats.Llvm;

/// <summary>
/// The characterization of <c>pbc --emit-llvm</c> at the PB-Compiler pin and the audit of its emitter
/// (<c>docs/llvm/pb-emitter-audit.md</c>). The tests that run <c>pbc</c> and LLVM need both toolchains
/// at their pins and are inconclusive, never passed, without them; the comparison and the document
/// checks run everywhere.
/// </summary>
[TestFixture]
[Layer("core")]
public sealed class PbEmitterCharacterizationTests {

  private const string ExternalInterop = "ExternalInterop";
  private const string Module = "; ModuleID = 'X.BAS'\n";

  private static readonly CharacterizationEntry _emitted = new("X.BAS", Characterization.Emitted, null, Characterization.Pass, Characterization.Pass, Characterization.Hash(Module), null);
  private static readonly CharacterizationEntry _declined = new("Y.BAS", Characterization.Declined, "the far entry thunk", null, null, null, null);
  private static readonly CommandResult _accepted = new(0, "", "");
  private static readonly CommandResult _rejected = new(1, "", "error: expected type");

  private static IEnumerable<CharacterizationEntry> Entries() => Characterization.Repository.Fixtures;

  private static IEnumerable<CharacterizationEntry> EmittedEntries() => Entries().Where(entry => entry.Outcome == Characterization.Emitted);

  #region the comparison

  [Test]
  public void Compare_WhenTheRunReproducesAnEmittedEntry_ReportsNothing()
    => Assert.That(Characterization.Compare(_emitted, new(new(0, Module, ""), _accepted, _accepted)), Is.Empty);

  [Test]
  public void Compare_WhenTheRunReproducesADeclinedEntry_ReportsNothing()
    => Assert.That(Characterization.Compare(_declined, new(new(1, "", "pbc: --emit-llvm: LLVM emission: the far entry thunk of 'S6'"), null, null)), Is.Empty);

  [Test]
  public void Compare_WhenARecordedFailureStillFails_ReportsNothing() {
    var failing = _emitted with { LlvmAs = Characterization.Fail, Verify = Characterization.Fail, Finding = "F1" };
    Assert.That(Characterization.Compare(failing, new(new(0, Module, ""), _rejected, _rejected)), Is.Empty);
  }

  [Test]
  public void Compare_WhenAnEmittedEntryIsRecordedAsDeclined_NamesTheFixtureAndBothOutcomes() {
    var edited = _emitted with { Outcome = Characterization.Declined, DeclinedBecause = "x", LlvmAs = null, Verify = null, Sha256 = null };
    Assert.That(Characterization.Compare(edited, new(new(0, Module, ""), _accepted, _accepted)),
      Is.EqualTo(new[] { "X.BAS: recorded outcome 'declined', actual 'emitted': pbc printed a module" }));
  }

  [Test]
  public void Compare_WhenADeclinedEntryNowEmits_NamesTheFixtureAndBothOutcomes()
    => Assert.That(Characterization.Compare(_declined, new(new(0, Module, ""), _accepted, _accepted)).Single(),
      Does.StartWith("Y.BAS: recorded outcome 'declined', actual 'emitted'"));

  [Test]
  public void Compare_WhenAnEmittedEntryNowDeclines_QuotesTheRefusal()
    => Assert.That(Characterization.Compare(_emitted, new(new(1, "", "pbc: --emit-llvm: refused"), null, null)).Single(),
      Is.EqualTo("X.BAS: recorded outcome 'emitted', actual 'declined': pbc: --emit-llvm: refused"));

  [TestCase(0, "", TestName = "Exit code 0 without a module is neither outcome")]
  [TestCase(1, "text", TestName = "Exit code 1 with output is neither outcome")]
  [TestCase(2, "", TestName = "Exit code 2 is neither outcome")]
  [TestCase(-1, "", TestName = "A killed pbc is neither outcome")]
  public void Compare_WhenPbcEndsNeitherWay_ReportsExitCodeAndStderr(int exitCode, string stdout)
    => Assert.That(Characterization.Compare(_emitted, new(new(exitCode, stdout, "boom"), null, null)).Single(),
      Does.StartWith("X.BAS: recorded outcome 'emitted', actual neither").And.Contain($"exited {exitCode}").And.EndWith("boom"));

  [Test]
  public void Compare_WhenTheRefusalNoLongerNamesTheConstruct_QuotesStderr()
    => Assert.That(Characterization.Compare(_declined, new(new(1, "", "pbc: --emit-llvm: something else"), null, null)).Single(),
      Is.EqualTo("Y.BAS: declined, but stderr does not contain 'the far entry thunk': pbc: --emit-llvm: something else"));

  [Test]
  public void Compare_WhenTheModuleChanged_NamesBothHashes() {
    var changed = Module + "\n";
    Assert.That(Characterization.Compare(_emitted, new(new(0, changed, ""), _accepted, _accepted)).Single(),
      Is.EqualTo($"X.BAS: the module changed: recorded sha256 {_emitted.Sha256}, actual {Characterization.Hash(changed)}"));
  }

  [Test]
  public void Compare_WhenLlvmAsRejectsAModuleRecordedAsPassing_NamesToolAndStderr()
    => Assert.That(Characterization.Compare(_emitted, new(new(0, Module, ""), _rejected, _accepted)).Single(),
      Is.EqualTo("X.BAS: llvm-as: recorded pass, actual fail (exit code 1): error: expected type"));

  [Test]
  public void Compare_WhenTheVerifierAcceptsAModuleRecordedAsFailing_NamesTheTool() {
    var failing = _emitted with { Verify = Characterization.Fail, Finding = "F1" };
    Assert.That(Characterization.Compare(failing, new(new(0, Module, ""), _accepted, _accepted)).Single(),
      Does.StartWith("X.BAS: opt -passes=verify: recorded fail, actual pass"));
  }

  [Test]
  public void Compare_WhenTheToolsDidNotRun_SaysSo()
    => Assert.That(Characterization.Compare(_emitted, new(new(0, Module, ""), null, null)),
      Is.EqualTo(new[] { "X.BAS: llvm-as did not run", "X.BAS: opt -passes=verify did not run" }));

  [Test]
  public void Hash_IsTheLowerCaseSha256OfTheUtf8Text()
    => Assert.That(Characterization.Hash(""), Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

  #endregion

  #region the characterization file

  [Test]
  public void Repository_RecordsTheFourListedFixturesTheBoundariesAndTheKnownFailures() {
    var characterization = Characterization.Repository;
    Assert.Multiple(() => {
      Assert.That(characterization.Pin, Is.EqualTo("0d73acbea185a2597357d53f7ce9acb44758ba76"));
      Assert.That(characterization.Llvm, Is.EqualTo("23.1.3"));
      Assert.That(characterization.Fixtures.Select(entry => entry.Name),
        Is.EqualTo(new[] { "MINI.BAS", "ARITH.BAS", "CTRL.BAS", "ARRAY.BAS", "NARROW.BAS", "DIFF36.BAS", "EMPTY.BAS", "GOTODW.BAS" }));
      Assert.That(characterization.Fixtures.Count(entry => entry.Outcome == Characterization.Declined), Is.EqualTo(1));
      Assert.That(characterization.Fixtures.Count(entry => entry.LlvmAs == Characterization.Fail), Is.EqualTo(2));
    });
  }

  [Test]
  public void Repository_EveryCorpusSourceIsAFixtureOfTheCorpusAtThePin() {
    var characterization = Characterization.Repository;
    var corpus = Entries().Where(entry => PbCorpus.IsCorpusFile(entry.Source)).ToList();
    Assert.That(corpus, Has.Count.EqualTo(6));
    Assert.That(corpus.Select(entry => entry.Source), Has.All.StartWith($"Hawkynt/PB-Compiler@{characterization.Pin}:"));
  }

  [Test]
  public void Repository_EveryLocalSourceExists()
    => Assert.That(Entries().Where(entry => !PbCorpus.IsCorpusFile(entry.Source)).Select(entry => PbCorpus.Resolve(entry.Source)), Has.All.Matches<string>(File.Exists));

  [Test]
  public void Repository_EveryRecordedFailureIsAFindingOfTheAudit() {
    var findings = PbEmitterAudit.Repository.Table(6).Select(row => row.Cells[0]).ToHashSet();
    Assert.That(Entries().Where(entry => entry.Finding != null).Select(entry => entry.Finding), Is.Not.Empty.And.SubsetOf(findings));
  }

  private const string Header = "schema: 1\npin: p\nllvm: l\nfixtures:\n";

  [TestCase("- source: A.BAS\n    outcome: maybe\n", "outcome 'maybe' is neither", TestName = "An unknown outcome is rejected")]
  [TestCase("- source: A.BAS\n    outcome: emitted\n    llvmAs: pass\n", "needs llvmAs and verify", TestName = "An emitted entry without verify is rejected")]
  [TestCase("- source: A.BAS\n    outcome: emitted\n    llvmAs: ok\n    verify: pass\n", "needs llvmAs and verify", TestName = "A tool result other than pass or fail is rejected")]
  [TestCase("- source: A.BAS\n    outcome: emitted\n    llvmAs: pass\n    verify: pass\n    sha256: abc\n", "64 hex digits", TestName = "A short hash is rejected")]
  [TestCase("- source: A.BAS\n    outcome: emitted\n    llvmAs: fail\n    verify: fail\n    sha256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n", "needs the finding", TestName = "A fail without a finding is rejected")]
  [TestCase("- source: A.BAS\n    outcome: declined\n", "needs declinedBecause", TestName = "A declined entry without a reason is rejected")]
  [TestCase("- source: A.BAS\n    outcome: declined\n    declinedBecause: x\n    llvmAs: pass\n", "has no module", TestName = "A declined entry with a tool result is rejected")]
  [TestCase("- outcome: declined\n", "'source' is missing", TestName = "An entry without a source is rejected")]
  [TestCase("- source: A.BAS\n", "'outcome' is missing", TestName = "An entry without an outcome is rejected")]
  [TestCase("- just text\n", "not a mapping", TestName = "An entry that is no mapping is rejected")]
  public void Parse_RejectsAMalformedEntry(string fixture, string message)
    => Assert.That(() => Characterization.Parse(Header + "  " + fixture), Throws.TypeOf<FormatException>().With.Message.Contains(message));

  [TestCase("", "not a mapping", TestName = "An empty characterization is rejected")]
  [TestCase("- a\n", "not a mapping", TestName = "A characterization that is a list is rejected")]
  [TestCase("schema: 1\npin: p\nllvm: l\nfixtures: x\n", "'fixtures' is not a list", TestName = "Fixtures that are no list are rejected")]
  [TestCase("schema: 1\nllvm: l\nfixtures: []\n", "'pin' is missing", TestName = "A characterization without a pin is rejected")]
  [TestCase("schema: 1\npin: p\nfixtures: []\n", "'llvm' is missing", TestName = "A characterization without the LLVM version is rejected")]
  public void Parse_RejectsAMalformedFile(string text, string message)
    => Assert.That(() => Characterization.Parse(text), Throws.TypeOf<FormatException>().With.Message.Contains(message));

  [Test]
  public void Parse_ReadsAWellFormedEntry() {
    var parsed = Characterization.Parse(Header + "  - source: o/r@0123456789012345678901234567890123456789:t/A.BAS\n    outcome: declined\n    declinedBecause: why\n");
    Assert.That(parsed.Fixtures.Single(), Is.EqualTo(new CharacterizationEntry("o/r@0123456789012345678901234567890123456789:t/A.BAS", Characterization.Declined, "why", null, null, null, null)));
    Assert.That(parsed.Fixtures.Single().Name, Is.EqualTo("A.BAS"));
  }

  #endregion

  #region the audit document

  [Test]
  public void Audit_EveryRowOfTheSixTablesCitesFileAndLine() {
    var audit = PbEmitterAudit.Repository;
    Assert.Multiple(() => {
      Assert.That(Enumerable.Range(1, 6).Select(table => audit.Table(table).Count()), Has.All.GreaterThan(0));
      Assert.That(audit.RowsWithoutCitation(), Is.Empty);
    });
  }

  [Test]
  public void Audit_EveryCitationOfTheEmitterLiesWithinItsLines()
    => Assert.That(PbEmitterAudit.Repository.CitationsOutside("LlvmEmitter.cs", 330), Is.Empty);

  [Test]
  public void RowsWithoutCitation_NamesTheRow() {
    var audit = PbEmitterAudit.Parse("# T\n\n## 2. Declined\n\n| A | B |\n| --- | --- |\n| `IrX` | `X.cs:12` |\n| `IrY` | no citation |\n");
    Assert.That(audit.RowsWithoutCitation().Select(row => row.ToString()), Is.EqualTo(new[] { "table 2, line 8: `IrY`" }));
  }

  [Test]
  public void Parse_ReadsOnlyTheNumberedTables() {
    var audit = PbEmitterAudit.Parse("| a | b |\n| - | - |\n| top | x |\n\n## Pin\n\n| a |\n| - |\n| pin |\n\n## 1. One\n\n| a | b |\n| - | - |\n| `IrA` | `a\\|b` |\n\ntext\n\n| c |\n| - |\n| `IrB` |\n");
    Assert.Multiple(() => {
      Assert.That(audit.Rows.Select(row => row.Cells[0]), Is.EqualTo(new[] { "`IrA`", "`IrB`" }));
      Assert.That(audit.Rows[0].Cells[1], Is.EqualTo("`a\\|b`"));
      Assert.That(audit.Covers(1, "IrA"), Is.True);
      Assert.That(audit.Covers(1, "IrC"), Is.False);
      Assert.That(audit.Covers(2, "IrA"), Is.False);
    });
  }

  [TestCase("`E.cs:0`", TestName = "Line 0 lies outside the file")]
  [TestCase("`E.cs:11`", TestName = "The line after the last lies outside the file")]
  [TestCase("`E.cs:5-4`", TestName = "A range that runs backwards is rejected")]
  [TestCase("`E.cs:9-11`", TestName = "A range that runs past the end is rejected")]
  public void CitationsOutside_FindsALineBeyondTheFile(string citation)
    => Assert.That(PbEmitterAudit.Parse($"## 1. T\n\n| a |\n| - |\n| {citation} |\n").CitationsOutside("E.cs", 10), Has.Exactly(1).Items);

  [TestCase("`E.cs:1`", TestName = "The first line lies within the file")]
  [TestCase("`E.cs:10`", TestName = "The last line lies within the file")]
  [TestCase("`E.cs:1-10`", TestName = "The whole file lies within the file")]
  [TestCase("`F.cs:99`", TestName = "A citation of another file is not checked")]
  public void CitationsOutside_AcceptsALineWithinTheFile(string citation)
    => Assert.That(PbEmitterAudit.Parse($"## 1. T\n\n| a |\n| - |\n| {citation} |\n").CitationsOutside("E.cs", 10), Is.Empty);

  private const string Emitter = """
    private string EmitInstruction(IrInstruction inst) {
      if (inst is IrCast { Op: IrCastOp.FPToSIRound } round)
        return "";
      return inst switch {
        IrBinary b when b.IsFloatOp => "",
        IrBinary b => "",
        // IrNotACase in a comment
        IrUnreachable => "unreachable",
        _ => throw new Exception(),
      };
    }
    private static string FastMath(IrInstruction instruction) => "";
    private string Ref(IrValue value) => value switch {
      IrConstantInt ci => "",
      IrFarEntry farEntry => throw new Exception(),
      _ => "",
    };
    private static string Suffix(IrType t) => "";
    private static string Ty(IrType t) => t.Kind switch {
      IrTypeKind.Void => "void",
      IrTypeKind.Float when t.IsMbf => throw new Exception(),
      IrTypeKind.Float => "double",
    };
    private static string EscapeBytes(byte[] bytes) => "";
    """;

  [Test]
  public void EmitterSource_ListsTheCasesOfEachSwitch() {
    Assert.Multiple(() => {
      Assert.That(PbEmitterSource.InstructionArms(Emitter), Is.EquivalentTo(new[] { "IrBinary", "IrUnreachable", "IrCast" }));
      Assert.That(PbEmitterSource.RefCases(Emitter), Is.EquivalentTo(new[] { "IrConstantInt", "IrFarEntry" }));
      Assert.That(PbEmitterSource.TypeKinds(Emitter), Is.EquivalentTo(new[] { "IrTypeKind.Void", "IrTypeKind.Float" }));
    });
  }

  [Test]
  public void EmitterSource_WhenAMethodIsMissing_SaysWhich()
    => Assert.That(() => PbEmitterSource.RefCases("private string Ref("), Throws.TypeOf<FormatException>().With.Message.Contains("private static string Suffix("));

  #endregion

  #region the toolchains

  [TestCase("pb-compiler", TestName = "Without pbc the characterization is inconclusive, and fails in CI")]
  [TestCase("llvm", TestName = "Without LLVM the characterization is inconclusive, and fails in CI")]
  public void MissingToolchain_IsNotRunRatherThanPassed(string key) {
    var attribute = new RequiresToolchainAttribute(key) { Lock = "tests/fixtures/formats/llvm/pb/absent.lock" };
    Assert.Multiple(() => {
      Assert.That(Assert.Throws<InconclusiveException>(() => attribute.Check(null))!.Message, Does.Contain($"toolchain '{key}'").And.Contain("PLY0021"));
      Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0021"));
    });
  }

  [Category(ExternalInterop), RequiresToolchain("pb-compiler"), RequiresToolchain("llvm")]
  [TestCaseSource(nameof(Entries))]
  public void RecordedOutcomeIsReproduced(CharacterizationEntry entry) {
    var observed = PbEmitterTools.Installed.Observe(PbCorpus.Resolve(entry.Source));
    Assert.That(Characterization.Compare(entry, observed), Is.Empty);
  }

  [Test, Category(ExternalInterop), RequiresToolchain("pb-compiler")]
  public void EmittedModules_AccessMemoryOnlyWithAlignOne() {
    var accesses = EmittedModules().SelectMany(module => MemoryAccesses(module.Text).Select(line => $"{module.Name}: {line}")).ToList();
    Assert.That(accesses, Is.Not.Empty);
    Assert.That(accesses, Has.All.EndsWith(", align 1"));
  }

  [Test, Category(ExternalInterop), RequiresToolchain("pb-compiler")]
  public void EmittedModules_CarryTheDriversTargetTripleAndNoDataLayout() {
    var modules = EmittedModules().ToList();
    Assert.Multiple(() => {
      Assert.That(modules.Select(module => Lines(module.Text).Count(line => line == "target triple = \"x86_64-unknown-linux-gnu\"")), Has.All.EqualTo(1));
      Assert.That(modules.Select(module => module.Text), Has.None.Contains("target datalayout"));
    });
  }

  [Test, Category(ExternalInterop), RequiresToolchain("git")]
  public void Audit_CoversEveryArmTypeKindAndRefCaseOfThePinnedEmitter() {
    var source = PbCorpus.ReadPinned(PbEmitterSource.Repository, PbEmitterSource.EmitterPath);
    var audit = PbEmitterAudit.Repository;
    Assert.Multiple(() => {
      Assert.That(source.Split('\n'), Has.Length.EqualTo(331), "the pinned emitter has 330 lines and a final line break");
      Assert.That(PbEmitterSource.InstructionArms(source), Has.Count.EqualTo(16).And.All.Matches<string>(arm => audit.Covers(1, arm)));
      Assert.That(PbEmitterSource.RefCases(source), Has.Count.EqualTo(7).And.All.Matches<string>(value => audit.Covers(1, value)));
      Assert.That(PbEmitterSource.TypeKinds(source), Has.Count.EqualTo(4).And.All.Matches<string>(kind => audit.Covers(3, kind)));
    });
  }

  /// <summary>The modules of the entries recorded as emitted, as <c>pbc</c> prints them now.</summary>
  internal static IEnumerable<(string Name, string Text)> EmittedModules()
    => EmittedEntries().Select(entry => (entry.Name, PbEmitterTools.Installed.EmitLlvm(PbCorpus.Resolve(entry.Source)).Stdout));

  /// <summary>The <c>load</c> and <c>store</c> instructions of a module.</summary>
  internal static IEnumerable<string> MemoryAccesses(string module)
    => Lines(module).Select(line => line.Trim()).Where(line => line.StartsWith("store ", StringComparison.Ordinal) || line.Contains(" = load ", StringComparison.Ordinal));

  private static string[] Lines(string text) => text.Split('\n');

  #endregion

}
