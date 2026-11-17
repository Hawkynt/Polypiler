using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.IR;

/// <summary>
/// Source locations and their provenance on the IR's trees: the three kinds of origin and their text
/// form, the table of sources, the rules by which rewrites carry origins, the verifier's rule of
/// origins (PLY0115 to PLY0117), the binary form and the exported map. One test per row of the test
/// design of the work order, with the rows named in the comments.
/// </summary>
[TestFixture]
public sealed class SourceMapTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "ir", "sourcemap");

  private static readonly SourceArtifact _source = new("Hawkynt/PB-Compiler", "0d73acbea185a2597357d53f7ce9acb44758ba76", "tests/idioms/FIXBCD.BAS",
    "3cbfc6e96157a8d450632afa44b4637c302f3ab84099beb50079025b159ef7ce", "pb/3.5");

  /// <summary>The schema of the map, built once: the registry of the validator refuses a second schema of the same ID.</summary>
  private static readonly Lazy<JsonSchema> _schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "ir", "sourcemap.schema.json"))));

  private static readonly IrLocal _a = new("a", IrType.I32);

  /// <summary>The bytes before the content: magic, version, checksum and length.</summary>
  private const int HeaderSize = 8 + 4 + 32 + 4;

  private static SourceSpan Span(int line, int start, int end) => new(0, line, start, line, end);

  private static IrConst Constant(long value) => new(IrType.I32, value);

  /// <summary>A module of one public function <c>void Main()</c> with the statements, declaring the given sources.</summary>
  private static IrModule Module(IReadOnlyList<SourceArtifact> sources, params IrStmt[] statements)
    => new("M", [new IrFunction("Main", true, IrType.Void, [], [], new IrBlock(statements))]) { Sources = sources };

  private static IReadOnlyList<Origin> Parsed(string text, int sources = 1) {
    Assert.That(Origin.TryParse(text, sources, out var origins, out var diagnostic), Is.True, diagnostic?.ToString());
    return origins;
  }

  private static Diagnostic Refused(string text, int sources = 1) {
    Assert.That(Origin.TryParse(text, sources, out var origins, out var diagnostic), Is.False);
    Assert.That(origins, Is.Empty);
    return diagnostic!;
  }

  // Row 1: a text span round trips through parse and print.
  [Test]
  public void ATextSpan_RoundTripsThroughParseAndPrint() {
    var origins = Parsed("!loc(0:12:5-12:18)");
    Assert.That(origins, Is.EqualTo(new[] { new SourceSpan(0, 12, 5, 12, 18) }));
    Assert.That(Origin.Format(origins), Is.EqualTo("!loc(0:12:5-12:18)"));
  }

  // Row 2: every kind of origin round trips, and several stand after one another.
  [TestCase("!loc(0:12:5-12:18)")]
  [TestCase("!addr(0:0x1F40+3)")]
  [TestCase("!synthetic(\"O0100\")")]
  [TestCase("!loc(0:3:1-3:9) !addr(0:0x0+1) !synthetic(\"O0003\")")]
  public void EveryKindOfOrigin_RoundTrips(string text) => Assert.That(Origin.Format(Parsed(text)), Is.EqualTo(text));

  [Test]
  public void NoSuffix_IsNoOrigin() {
    Assert.That(Parsed("  "), Is.Empty);
    Assert.That(Origin.Format([]), Is.Empty);
  }

  [Test]
  public void WhiteSpace_SeparatesSuffixes() => Assert.That(Parsed("\t!loc(0:1:1-1:2)\n  !synthetic(\"P\") "), Has.Count.EqualTo(2));

  [Test]
  public void ASyntheticOrigin_NamesNoSource() => Assert.That(Parsed("!synthetic(\"O0003\")", 0).Single().SourceIndex, Is.EqualTo(-1));

  // Row 4: a binary input's address range.
  [Test]
  public void AnAddress_IsAnOffsetAndALength() {
    var address = (AddressSpan)Parsed("!addr(0:0x1F40+3)").Single();
    Assert.That((address.Artifact, address.Offset, address.Length), Is.EqualTo((0, 8000L, 3)));
    var module = Module([_source], new IrReturn(null) { Origins = [address] });
    using var map = JsonDocument.Parse(SourceMapWriter.Write(module, module.Functions[0]));
    var origin = map.RootElement.GetProperty("mappings")[0].GetProperty("origins")[0];
    Assert.That((origin.GetProperty("offset").GetInt64(), origin.GetProperty("length").GetInt32()), Is.EqualTo((8000L, 3)));
  }

  // Row 5: a span at line 1, column 1, and an empty span.
  [TestCase("!loc(0:1:1-1:1)", TestName = "An empty span at line 1, column 1 is accepted")]
  [TestCase("!loc(0:1:1-1:2)", TestName = "A span of one code unit at line 1, column 1 is accepted")]
  [TestCase("!loc(0:1:9-2:1)", TestName = "A span that ends on a later line at an earlier column is accepted")]
  [TestCase("!addr(0:0x0+0)", TestName = "An empty address range at offset 0 is accepted")]
  public void ASpanOnTheLowerBoundary_IsAccepted(string text) => Assert.That(Parsed(text), Has.Count.EqualTo(1));

  // Row 6: an end before the start.
  [TestCase("!loc(0:5:9-5:3)", TestName = "A span that ends before it starts on its line is PLY0116")]
  [TestCase("!loc(0:5:1-4:9)", TestName = "A span that ends on an earlier line is PLY0116")]
  [TestCase("!loc(0:0:1-1:1)", TestName = "Line 0 is PLY0116")]
  [TestCase("!loc(0:1:0-1:1)", TestName = "Column 0 is PLY0116")]
  public void ASpanThatDescribesNoText_IsMalformed(string text) {
    var diagnostic = Refused(text);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0116", $"malformed location '{text}'")));
  }

  // Row 7: the source index on and just past the last declared source.
  [Test]
  public void TheLastDeclaredSource_IsAccepted() => Assert.That(Parsed("!loc(0:1:1-1:2) !addr(0:0x10+2)"), Has.Count.EqualTo(2));

  [TestCase("!loc(1:1:1-1:2)")]
  [TestCase("!addr(1:0x10+2)")]
  public void ASourceIndexEqualToTheCount_IsUndeclared(string text) {
    var diagnostic = Refused(text);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0117", "location refers to source 1, but the module declares 1 sources")));
  }

  [Test]
  public void ALocationInAModuleWithoutSources_IsUndeclared() => Assert.That(Refused("!loc(3:1:1-1:2)", 0).Code, Is.EqualTo("PLY0117"));

  // Row 11: a malformed suffix.
  [TestCase("!loc(0:x:1-1:2)", TestName = "A line that is no number is PLY0116")]
  [TestCase("!loc(0:1:1)", TestName = "A span without its end is PLY0116")]
  [TestCase("!loc(0:99999999999:1-1:2)", TestName = "A line beyond int is PLY0116")]
  [TestCase("!addr(0:1F40+3)", TestName = "An offset without 0x is PLY0116")]
  [TestCase("!addr(99999999999:0x1+3)", TestName = "A source beyond int is PLY0116")]
  [TestCase("!addr(0:0xFFFFFFFFFFFFFFFF+3)", TestName = "An offset beyond long is PLY0116")]
  [TestCase("!addr(0:0x1+99999999999)", TestName = "A length beyond int is PLY0116")]
  [TestCase("!synthetic(O0100)", TestName = "A pass without quotes is PLY0116")]
  [TestCase("!synthetic(\"\")", TestName = "An empty pass is PLY0116")]
  [TestCase("!where(0)", TestName = "An unknown suffix is PLY0116")]
  public void AMalformedSuffix_IsRefused(string text) => Assert.That(Refused(text).Code, Is.EqualTo("PLY0116"));

  [Test]
  public void TheFirstBadSuffix_IsNamed() => Assert.That(Refused("!loc(0:1:1-1:2) !loc(0:x:1-1:2) !bad").Message, Is.EqualTo("malformed location '!loc(0:x:1-1:2)'"));

  [TestCase(-1, 1, 1, 1, 1, "the source index is negative")]
  [TestCase(0, 1, 1, 1, 0, "lines and columns count from 1")]
  [TestCase(0, 1, 1, 0, 1, "lines and columns count from 1")]
  [TestCase(0, 1, 0, 1, 1, "lines and columns count from 1")]
  public void AConstructedSpan_SaysWhatIsWrong(int artifact, int startLine, int startColumn, int endLine, int endColumn, string problem)
    => Assert.That(new SourceSpan(artifact, startLine, startColumn, endLine, endColumn).Problem, Is.EqualTo(problem));

  [TestCase(-1, 0L, 0, "the source index is negative")]
  [TestCase(0, -1L, 0, "offset and length are never negative")]
  [TestCase(0, 0L, -1, "offset and length are never negative")]
  [TestCase(0, 0L, 0, null)]
  public void AConstructedAddress_SaysWhatIsWrong(int artifact, long offset, int length, string? problem)
    => Assert.That(new AddressSpan(artifact, offset, length).Problem, Is.EqualTo(problem));

  [TestCase("", false)]
  [TestCase("a b", false)]
  [TestCase("a\"b", false)]
  [TestCase("O0100", true)]
  public void APassId_HasNoQuoteAndNoWhiteSpace(string pass, bool valid) => Assert.That(new Synthetic(pass).Problem is null, Is.EqualTo(valid));

  // Row 12: multi-byte source text; columns count UTF-16 code units.
  [Test]
  public void ColumnsAfterACharacterOutsideTheBasicPlane_CountUtf16Units() {
    const string text = "a = \"\U0001F600\" + b\nc";
    var b = text.IndexOf('b', StringComparison.Ordinal);
    Assert.That(SourceSpan.FromOffsets(0, text, b, b + 1), Is.EqualTo(new SourceSpan(0, 1, 12, 1, 13)));
    Assert.That(SourceSpan.FromOffsets(0, text, b, text.Length), Is.EqualTo(new SourceSpan(0, 1, 12, 2, 2)));
    Assert.That(SourceSpan.FromOffsets(0, "\u00E9x", 1, 2), Is.EqualTo(new SourceSpan(0, 1, 2, 1, 3)));
  }

  [Test]
  public void OffsetsAtTheStartAndTheEnd_AreLineOneAndTheLastLine() {
    Assert.That(SourceSpan.FromOffsets(2, "", 0, 0), Is.EqualTo(new SourceSpan(2, 1, 1, 1, 1)));
    Assert.That(SourceSpan.FromOffsets(0, "ab\r\n", 4, 4), Is.EqualTo(new SourceSpan(0, 2, 1, 2, 1)));
  }

  [TestCase(-1, 0)]
  [TestCase(1, 0)]
  [TestCase(0, 3)]
  public void OffsetsOutsideTheText_AreRefused(int start, int end)
    => Assert.That(() => SourceSpan.FromOffsets(0, "ab", start, end), Throws.TypeOf<ArgumentOutOfRangeException>());

  [Test]
  public void FromOffsets_RefusesNoText() => Assert.That(() => SourceSpan.FromOffsets(0, null!, 0, 0), Throws.ArgumentNullException);

  // The table of sources.
  [Test]
  public void TheTableOfSources_RoundTrips() {
    var other = new SourceArtifact("Hawkynt/Polypiler", "main", "tests/a.cs", new string('0', 64), "csharp/14");
    var text = SourceArtifact.FormatTable([_source, other]);
    Assert.That(text, Is.EqualTo("sources {\n"
      + "  0: \"Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76:tests/idioms/FIXBCD.BAS\" sha256:3cbfc6e96157a8d450632afa44b4637c302f3ab84099beb50079025b159ef7ce dialect:\"pb/3.5\"\n"
      + "  1: \"Hawkynt/Polypiler@main:tests/a.cs\" sha256:0000000000000000000000000000000000000000000000000000000000000000 dialect:\"csharp/14\"\n}"));
    Assert.That(SourceArtifact.TryParseTable(text, out var sources, out var diagnostic), Is.True, diagnostic?.ToString());
    Assert.That(sources, Is.EqualTo(new[] { _source, other }));
  }

  [Test]
  public void AnEmptyTableOnOneLine_Reads() {
    Assert.That(SourceArtifact.TryParseTable("sources { }", out var sources, out _), Is.True);
    Assert.That(sources, Is.Empty);
    Assert.That(SourceArtifact.FormatTable([]), Is.EqualTo("sources {\n}"));
  }

  [TestCase("source { }", "source { }", TestName = "A table without the keyword is PLY0116")]
  [TestCase("sources { 1: \"r@c:p\" sha256:0000000000000000000000000000000000000000000000000000000000000000 dialect:\"d\" }", "1: \"r@c:p\"", TestName = "An entry out of order is PLY0116")]
  [TestCase("sources { 0: \"no-reference\" sha256:0000000000000000000000000000000000000000000000000000000000000000 dialect:\"d\" }", "0: \"no-reference\"", TestName = "An entry that names no commit is PLY0116")]
  [TestCase("sources { 0: \"r@c:/home/me/a.bas\" sha256:0000000000000000000000000000000000000000000000000000000000000000 dialect:\"d\" }", "0: \"r@c:/home/me/a.bas\"", TestName = "An absolute path is PLY0116")]
  [TestCase("sources { 0: \"r@c:p\" sha256:00 dialect:\"d\" }", "0: \"r@c:p\" sha256:00", TestName = "A short hash is PLY0116")]
  [TestCase("sources { 0: \"r@c:p\" sha256:0000000000000000000000000000000000000000000000000000000000000000 dialect:\"d\" junk }", "junk", TestName = "Text after the entries is PLY0116")]
  public void AMalformedTable_IsRefused(string text, string named) {
    Assert.That(SourceArtifact.TryParseTable(text, out var sources, out var diagnostic), Is.False);
    Assert.That(sources, Is.Empty);
    Assert.That(diagnostic!.Code, Is.EqualTo("PLY0116"));
    Assert.That(diagnostic.Message, Does.Contain(named));
  }

  [TestCase("r", "c", "a/../b", "the path is not relative to the repository")]
  [TestCase("r", "c", "a\\b", "the path is not relative to the repository")]
  [TestCase("r", "c", "C:x", "the path is not relative to the repository")]
  [TestCase("r", "c", "a//b", "the path is not relative to the repository")]
  [TestCase("r", "c", "./a", "the path is not relative to the repository")]
  [TestCase("", "c", "a", "the reference is not repository@commit:path")]
  [TestCase("r", "c", "a b", "the reference is not repository@commit:path")]
  public void ASourceThatIsNoRepositoryFile_SaysWhy(string repository, string commit, string path, string problem)
    => Assert.That(new SourceArtifact(repository, commit, path, new string('a', 64), "d").Problem, Is.EqualTo(problem));

  [TestCase("ABCD", "d", "the SHA-256 is not 64 lower-case hexadecimal digits")]
  [TestCase("", "d", "the SHA-256 is not 64 lower-case hexadecimal digits")]
  [TestCase(null, "", "the dialect is empty or holds a quote")]
  [TestCase(null, "a\"b", "the dialect is empty or holds a quote")]
  [TestCase(null, "pb/3.5", null)]
  public void AHashOrADialectThatIsMalformed_SaysWhy(string? hash, string dialect, string? problem)
    => Assert.That(new SourceArtifact("r", "c", "p", hash ?? new string('a', 64), dialect).Problem, Is.EqualTo(problem));

  // Origins take no part in equality, hashing or the text of a node.
  [Test]
  public void NodesThatDifferOnlyInOrigins_AreEqual() {
    var plain = new IrReturn(new IrBinary(IrBinaryOp.Add, Constant(1), Constant(2)));
    var located = plain with { Origins = [Span(1, 1, 2)], Value = plain.Value! with { Origins = [new Synthetic("O1")] } };
    Assert.That(located, Is.EqualTo(plain));
    Assert.That(located.GetHashCode(), Is.EqualTo(plain.GetHashCode()));
    Assert.That(located.ToString(), Is.EqualTo(plain.ToString()));
    Assert.That(located.ToString(), Is.EqualTo("IrReturn { Value = IrBinary { Type = i32, Op = Add, Left = IrConst { Type = i32, Value = 1 }, Right = IrConst { Type = i32, Value = 2 } } }"));
    Assert.That(new IrBreak { Origins = [Span(1, 1, 2)] }.ToString(), Is.EqualTo("IrBreak { }"));
  }

  [Test]
  public void NodesThatDifferInMeaning_StayDifferent() {
    Assert.That(Constant(1), Is.Not.EqualTo(Constant(2)));
    Assert.That(Constant(1), Is.Not.EqualTo(new IrConst(IrType.I64, 1)));
    Assert.That((IrExpr)Constant(1), Is.Not.EqualTo((IrExpr)new IrLocalRef(_a)));
    Assert.That(((IrExpr)Constant(1)).Equals((IrExpr?)null), Is.False);
    Assert.That((IrStmt)new IrBreak(), Is.Not.EqualTo((IrStmt)new IrContinue()));
    Assert.That(((IrStmt)new IrBreak()).Equals((IrStmt?)null), Is.False);
    var same = new IrBreak();
    Assert.That(same.Equals(same), Is.True);
    Assert.That(Constant(1).Equals(Constant(1)), Is.True);
  }

  // Row 3: every rewrite operation carries origins.
  [Test]
  public void AReplacement_InheritsTheOriginOfWhatItReplaces() {
    var old = new IrAssign(_a, new IrConditional(new IrConst(IrType.Bool, 1), new IrLocalRef(_a), Constant(7))) { Origins = Parsed("!loc(0:12:5-12:18)") };
    var replacement = IrOrigins.Replace(old with { Value = new IrConditional(new IrConst(IrType.Bool, 1), new IrLocalRef(_a), Constant(8)), Origins = [] }, old);
    Assert.That(Origin.Format(replacement.Origins), Is.EqualTo("!loc(0:12:5-12:18)"));
    var expression = IrOrigins.Replace(Constant(8), Constant(7) with { Origins = [Span(12, 5, 18)] });
    Assert.That(expression.Origins, Is.EqualTo(new[] { Span(12, 5, 18) }));
  }

  [Test]
  public void ReplacingUses_KeepsTheOriginsOfTheUser() {
    var statement = new IrAssign(_a, new IrLocalRef(_a)) { Origins = [Span(1, 1, 5)] };
    var rewritten = IrTree.With(statement, [Constant(3)], []);
    Assert.That(rewritten.Origins, Is.SameAs(statement.Origins));
  }

  /// <summary>Every kind of expression <see cref="IrTree.With(IrExpr, IReadOnlyList{IrExpr})"/> rebuilds, with an origin.</summary>
  private static IEnumerable<TestCaseData> Rebuilt() {
    IrExpr bit = new IrConst(IrType.Bool, 1), number = Constant(1);
    var array = new IrLocalRef(new IrLocal("xs", IrType.Array(IrType.I32)));
    var optional = new IrLocalRef(new IrLocal("o", IrType.Optional(IrType.I32)));
    var text = new IrStrConst("s");
    IrExpr[] cases = [
      new IrUnary(IrUnaryOp.Negate, number), new IrBinary(IrBinaryOp.Add, number, number), new IrConditional(bit, number, number),
      new IrArrayLoad(array, number), new IrArrayLength(array), new IrStructEquals(number, number), new IrReferenceEquals(text, text),
      new IrSome(number), new IrIsNone(optional), new IrCoalesce(optional, number), new IrIsNull(text), new IrGenericEquals(number, number),
      new IrGenericCompare(number, number), new IrArraySequence(array), new IrAwait(new IrLocalRef(new IrLocal("t", IrType.I32))),
      new IrCall("F", IrType.I32, [number]), new IrConvert(IrType.I64, number), new IrConst(IrType.I32, 5)
    ];
    return cases.Select(expression => new TestCaseData(expression with { Origins = [Span(2, 1, 4)] }).SetName($"{expression.GetType().Name} rebuilt keeps its origin"));
  }

  [TestCaseSource(nameof(Rebuilt))]
  public void AnExpressionRebuiltWithNewOperands_KeepsItsOrigins(IrExpr expression) {
    var rebuilt = IrTree.With(expression, [.. IrTree.Operands(expression)]);
    Assert.That(rebuilt.Origins, Is.EqualTo(new[] { Span(2, 1, 4) }));
    Assert.That(rebuilt.GetType(), Is.EqualTo(expression.GetType()));
  }

  [Test]
  public void AnExpressionWithoutOrigins_IsRebuiltWithoutThem() {
    var rebuilt = IrTree.With(new IrUnary(IrUnaryOp.Negate, Constant(1)), [Constant(2)]);
    Assert.That(rebuilt.Origins, Is.Empty);
  }

  [Test]
  public void AnInsertedStatement_MustBeMarkedSynthetic() {
    var located = new IrReturn(null) { Origins = [Span(1, 1, 7)] };
    var inserted = new IrExprStmt(new IrCall("G", IrType.Void, []));
    Assert.That(OriginRule.Check(Module([_source], inserted, located)).Select(problem => problem.Message),
      Is.EqualTo(new[] { "instruction #1 (ExprStmt) in '@Main' has no origin and is not marked synthetic" }));
    var marked = IrOrigins.MarkSynthetic(inserted, "O0003");
    Assert.That(Origin.Format(marked.Origins), Is.EqualTo("!synthetic(\"O0003\")"));
    Assert.That(OriginRule.Check(Module([_source], marked, located)), Is.Empty);
  }

  [Test]
  public void MarkingAnExpression_AddsThePassAfterItsOrigins() {
    var marked = IrOrigins.MarkSynthetic(Constant(1) with { Origins = [Span(1, 1, 2)] }, "O0004");
    Assert.That(Origin.Format(marked.Origins), Is.EqualTo("!loc(0:1:1-1:2) !synthetic(\"O0004\")"));
  }

  [Test]
  public void RemovingAStatement_LeavesTheOthersTheirOrigins() {
    IrStmt first = new IrExprStmt(Constant(1)) { Origins = [Span(1, 1, 2)] }, second = new IrReturn(null) { Origins = [Span(2, 1, 7)] };
    var block = new IrBlock([first, second]) { Origins = [Span(1, 1, 7)] };
    var removed = block with { Statements = [second] };
    Assert.That((removed.Origins, removed.Statements[0].Origins), Is.EqualTo((block.Origins, second.Origins)));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  public void SplittingABlock_LeavesBothHalvesItsOrigins(int index) {
    IrStmt first = new IrExprStmt(Constant(1)) { Origins = [Span(1, 1, 2)] }, second = new IrReturn(null) { Origins = [Span(2, 1, 7)] };
    var block = new IrBlock([first, second]) { Origins = [Span(1, 1, 7)] };
    var (head, tail) = IrOrigins.Split(block, index);
    Assert.That(head.Statements.Concat(tail.Statements), Is.EqualTo(block.Statements));
    Assert.That((head.Origins, tail.Origins), Is.EqualTo((block.Origins, block.Origins)));
  }

  // Row 8: 0, 1 and 64 origins along a chain of merges.
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(64)]
  public void AChainOfMerges_KeepsEveryOriginInOrder(int count) {
    var spans = Enumerable.Range(1, count).Select(line => (Origin)Span(line, 1, 9)).ToList();
    IrStmt merged = new IrExprStmt(Constant(0));
    foreach (var span in spans)
      merged = IrOrigins.Replace(new IrExprStmt(Constant(0)), merged, new IrExprStmt(Constant(1)) { Origins = [span] });

    Assert.That(merged.Origins, Is.EqualTo(spans));
    Assert.That(IrOrigins.Of(new IrStmt[] { merged, merged }), Has.Count.EqualTo(2 * count));
    Assert.That(IrOrigins.Of(Constant(1) with { Origins = spans }), Is.EqualTo(spans));
  }

  [Test]
  public void MergingTwoInstructions_PrintsBothOriginsInOrder() {
    IrStmt a = new IrExprStmt(Constant(1)) { Origins = Parsed("!loc(0:3:1-3:9)") }, b = new IrExprStmt(Constant(2)) { Origins = Parsed("!loc(0:4:1-4:9)") };
    Assert.That(Origin.Format(IrOrigins.Replace(new IrExprStmt(Constant(3)), a, b).Origins), Is.EqualTo("!loc(0:3:1-3:9) !loc(0:4:1-4:9)"));
  }

  // Rows 9 and 10: the IR has no stages; every instruction needs an origin wherever it stands.
  private static IEnumerable<TestCaseData> Unmarked() {
    IrStmt Located(IrStmt statement) => statement with { Origins = [Span(1, 1, 2)] };
    var bare = new IrBreak();
    yield return new TestCaseData(new IrStmt[] { new IrReturn(null) }, "#1 (Return)").SetName("A statement at the top of the body is PLY0115");
    yield return new TestCaseData(new IrStmt[] { Located(new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([bare]))) }, "#2 (Break)").SetName("A statement in the body of a loop is PLY0115");
    yield return new TestCaseData(new IrStmt[] { Located(new IrIf(new IrConst(IrType.Bool, 1), new IrBlock([Located(bare)]), new IrBlock([bare]))) }, "#3 (Break)").SetName("A statement in an else block is PLY0115");
    var lambda = new IrLambda(IrType.Function(IrType.Void), [], [], [], new IrBlock([new IrReturn(null)]));
    yield return new TestCaseData(new IrStmt[] { Located(new IrExprStmt(lambda)) }, "#2 (Return)").SetName("A statement in the body of a lambda is PLY0115");
    yield return new TestCaseData(new IrStmt[] { Located(new IrDeclare(_a, Constant(1))), new IrBlock([new IrAssign(_a, Constant(2))]) }, "#2 (Assign)").SetName("A statement in a nested block is PLY0115");
  }

  [TestCaseSource(nameof(Unmarked))]
  public void AnInstructionWithoutOrigin_IsPly0115(IrStmt[] statements, string instruction) {
    var problems = OriginRule.Check(Module([_source], statements));
    Assert.That(problems.Select(problem => (problem.Code, problem.Message)),
      Is.EqualTo(new[] { ("PLY0115", $"instruction {instruction} in '@Main' has no origin and is not marked synthetic") }));
  }

  [Test]
  public void BlocksNeedNoOrigin() => Assert.That(OriginRule.Check(Module([_source], new IrBlock([new IrReturn(null) { Origins = [Span(1, 1, 2)] }]))), Is.Empty);

  [Test]
  public void AModuleWithoutSources_NeedsNoOrigins() => Assert.That(OriginRule.Check(Module([], new IrReturn(null))), Is.Empty);

  [Test]
  public void ALambdaInsideALambda_IsCheckedOnce() {
    var inner = new IrLambda(IrType.Function(IrType.Void), [], [], [], new IrBlock([new IrReturn(null)]));
    var outer = new IrLambda(IrType.Function(IrType.Void), [], [], [], new IrBlock([new IrExprStmt(inner) { Origins = [Span(2, 1, 2)] }]));
    var problems = OriginRule.Check(Module([_source], new IrExprStmt(outer) { Origins = [Span(1, 1, 2)] }));
    Assert.That(problems.Select(problem => problem.Message), Is.EqualTo(new[] { "instruction #3 (Return) in '@Main' has no origin and is not marked synthetic" }));
  }

  [Test]
  public void TheVerifier_AppliesTheRule() {
    var unmarked = Module([_source], new IrReturn(null));
    Assert.That(IrVerifier.Verify(unmarked).Select(problem => problem.Code), Is.EqualTo(new[] { "PLY0115" }));
    Assert.That(IrVerifier.Verify(Module([_source], IrOrigins.MarkSynthetic(new IrReturn(null), "O0003"))), Is.Empty);
  }

  [Test]
  public void MalformedOriginsAndSources_AreReportedByTheVerifier() {
    var badSource = _source with { Path = "/home/me/FIXBCD.BAS" };
    var module = Module([badSource],
      new IrExprStmt(new IrCall("G", IrType.Void, [Constant(1) with { Origins = [new SourceSpan(0, 5, 9, 5, 3)] }])) { Origins = [new AddressSpan(1, 0, 1)] },
      new IrReturn(null) { Origins = [new Synthetic("")] });
    Assert.That(OriginRule.Check(module).Select(problem => problem.ToString()), Is.EqualTo(new[] {
      "error PLY0116: malformed location 'Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76:/home/me/FIXBCD.BAS'",
      "error PLY0117: location refers to source 1, but the module declares 1 sources",
      "error PLY0116: malformed location '!loc(0:5:9-5:3)'",
      "error PLY0116: malformed location '!synthetic(\"\")'"
    }));
  }

  [Test]
  public void AnExpressionSharedByTwoStatements_IsCheckedOnce() {
    var shared = Constant(1) with { Origins = [new AddressSpan(4, 0, 1)] };
    var module = Module([_source], new IrExprStmt(shared) { Origins = [Span(1, 1, 2)] }, new IrExprStmt(shared) { Origins = [Span(2, 1, 2)] });
    Assert.That(OriginRule.Check(module), Has.Count.EqualTo(1));
  }

  // The binary form keeps sources and origins exactly, and the bytes of a module without them as they were.
  [Test]
  public void SourcesAndOrigins_RoundTripThroughTheBinaryForm() {
    var shared = Constant(1) with { Origins = [Span(1, 1, 2), new Synthetic("O0100")] };
    var module = Module([_source, _source with { Path = "b.bas" }],
      new IrExprStmt(new IrBinary(IrBinaryOp.Add, shared, shared) { Origins = [new AddressSpan(1, 0x1F40, 3)] }) { Origins = [Span(4, 7, 12), Span(4, 14, 19)] },
      new IrExprStmt(Constant(2)),
      new IrReturn(null) { Origins = [new SourceSpan(-1, 0, 0, -5, 0)] });
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(IrGraph.Difference(module, read), Is.Null);
    Assert.That(read.Sources, Is.EqualTo(module.Sources));
    var binary = (IrBinary)((IrExprStmt)read.Functions[0].Body.Statements[0]).Expression;
    Assert.That(binary.Left, Is.SameAs(binary.Right));
    Assert.That(Origin.Format(binary.Left.Origins), Is.EqualTo("!loc(0:1:1-1:2) !synthetic(\"O0100\")"));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
  }

  [Test]
  public void AModuleWithoutOrigins_KeepsItsBytes() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new IrExprStmt(Constant(7))]))]);
    var expected = Precomputed.IrModuleFormatTests.Expression(writer => {
      writer.Write(Precomputed.IrModuleFormatTests.Const);
      writer.Write(Precomputed.IrModuleFormatTests.I32);
      writer.Write(7L);
    });
    Assert.That(IrModuleFormat.Write(module), Is.EqualTo(expected));
  }

  private static byte[] WithOrigins(Action<BinaryWriter> origins) => Precomputed.IrModuleFormatTests.Function(writer => {
    writer.Write(Precomputed.IrModuleFormatTests.Block);
    writer.Write7BitEncodedInt(1);
    origins(writer);
  });

  private static IEnumerable<TestCaseData> MalformedOrigins() {
    const byte located = 0xFE;
    yield return new TestCaseData(WithOrigins(writer => { writer.Write(located); writer.Write7BitEncodedInt(1); writer.Write((byte)9); }), "9 is no kind of origin").SetName("An origin of no kind is refused");
    yield return new TestCaseData(WithOrigins(writer => { writer.Write(located); writer.Write7BitEncodedInt(0); writer.Write(Precomputed.IrModuleFormatTests.Null); }), "Origins stand before no new node").SetName("Origins before no node are refused");
    yield return new TestCaseData(WithOrigins(writer => { writer.Write(located); writer.Write7BitEncodedInt(0); writer.Write(Precomputed.IrModuleFormatTests.Shared); }), "Origins stand before no new node").SetName("Origins before a shared node are refused");
    yield return new TestCaseData(WithOrigins(writer => { writer.Write(located); writer.Write7BitEncodedInt(0); writer.Write(located); }), "Origins stand before no new node").SetName("Origins before origins are refused");
  }

  [TestCaseSource(nameof(MalformedOrigins))]
  public void MalformedOrigins_AreRefused(byte[] bytes, string message)
    => Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));

  [Test]
  public void ATableOfSourcesThatEndsEarly_IsRefused() {
    var bytes = Precomputed.IrModuleFormatTests.Sealed(writer => {
      var content = IrModuleFormat.Write(Module([_source], new IrReturn(null) { Origins = [Span(1, 1, 2)] }));
      writer.Write(content.AsSpan(HeaderSize, content.Length - HeaderSize - 3));
    });
    Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("malformed"));
  }

  // The exported map: the schema, the instructions and the text each span points at.
  private static (IrModule Module, List<(Origin Origin, string? Lexeme)> Expected) Fixture() {
    var lines = File.ReadAllText(Path.Combine(_fixtures, "fixbcd.locations")).Split('\n');
    var header = string.Join("\n", lines.TakeWhile(line => line != "}")) + "\n}";
    Assert.That(SourceArtifact.TryParseTable(header, out var sources, out var diagnostic), Is.True, diagnostic?.ToString());
    var expected = new List<(Origin, string?)>();
    var statements = new List<IrStmt>();
    foreach (var line in lines.SkipWhile(line => line != "}").Skip(1).Where(line => line.Length > 0)) {
      var parts = line.Split('\t');
      var origin = Parsed(parts[0], sources.Count).Single();
      expected.Add((origin, parts.Length > 1 ? parts[1] : null));
      statements.Add(new IrExprStmt(Constant(statements.Count)) { Origins = [origin] });
    }

    return (Module(sources, [.. statements]), expected);
  }

  /// <summary>The text of a span, found with an index of its own: the offsets of the line starts in the UTF-16 text.</summary>
  private static string Slice(string text, SourceSpan span) {
    var starts = new List<int> { 0 };
    for (var index = 0; index < text.Length; ++index)
      if (text[index] == '\n')
        starts.Add(index + 1);

    int Offset(int line, int column) => starts[line - 1] + column - 1;
    return text[Offset(span.StartLine, span.StartColumn)..Offset(span.EndLine, span.EndColumn)];
  }

  [Test]
  public void TheFixtureSource_IsThePinnedFile()
    => Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_fixtures, "FIXBCD.BAS")))), Is.EqualTo(_source.Sha256));

  [Test]
  public void EverySpanOfTheFixture_PointsAtItsLexeme() {
    var (module, expected) = Fixture();
    Assert.That(module.Sources, Is.EqualTo(new[] { _source }));
    var bytes = File.ReadAllBytes(Path.Combine(_fixtures, "FIXBCD.BAS"));
    var text = System.Text.Encoding.UTF8.GetString(bytes);
    foreach (var (origin, lexeme) in expected)
      switch (origin) {
        case SourceSpan span:
          Assert.That(Slice(text, span), Is.EqualTo(lexeme), span.Format());
          break;
        case AddressSpan address:
          Assert.That(System.Text.Encoding.ASCII.GetString(bytes, (int)address.Offset, address.Length), Is.EqualTo(lexeme), address.Format());
          break;
        default:
          Assert.That(lexeme, Is.Null);
          break;
      }

    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void TheMapOfTheFixture_ValidatesAndListsEveryInstruction() {
    var (module, expected) = Fixture();
    var json = SourceMapWriter.Write(module, module.Functions[0]);
    var schema = _schema.Value;
    using var document = JsonDocument.Parse(json);
    Assert.That(schema.Evaluate(document.RootElement).IsValid, Is.True, json);
    Assert.That(json, Is.EqualTo(File.ReadAllText(Path.Combine(_fixtures, "fixbcd.map.json"))));
    var mappings = document.RootElement.GetProperty("mappings");
    Assert.That(mappings.GetArrayLength(), Is.EqualTo(expected.Count));
    Assert.That(json, Does.Not.Contain("PRINT"));
  }

  [Test]
  public void TheSchema_RefusesAnAbsolutePathAndEmbeddedText() {
    var schema = _schema.Value;
    bool Valid(string json) {
      using var document = JsonDocument.Parse(json);
      return schema.Evaluate(document.RootElement).IsValid;
    }

    const string Rest = "\"sourcesSha256\":[],\"dialects\":[],\"mappings\":[]";
    Assert.That(Valid($"{{\"version\":1,\"function\":\"F\",\"sources\":[\"r@c:a/b.bas\"],{Rest}}}"), Is.True);
    Assert.That(Valid($"{{\"version\":1,\"function\":\"F\",\"sources\":[\"r@c:/home/me/b.bas\"],{Rest}}}"), Is.False);
    Assert.That(Valid($"{{\"version\":1,\"function\":\"F\",\"sources\":[],\"sourcesContent\":[\"PRINT\"],{Rest}}}"), Is.False);
  }

  [Test]
  public void Write_RefusesNull() {
    var module = Module([]);
    Assert.That(() => SourceMapWriter.Write(null!, module.Functions[0]), Throws.ArgumentNullException);
    Assert.That(() => SourceMapWriter.Write(module, null!), Throws.ArgumentNullException);
  }

  private static IEnumerable<IrStmt> Flat(IEnumerable<IrStmt> statements)
    => statements.SelectMany(statement => statement is IrBlock block ? Flat(block.Statements) : [statement]);

  // The measurement: 2000 random rewrites, seed 115, leave every instruction and expression an origin.
  [Test]
  public void RandomRewrites_LeaveEveryNodeAnOrigin() {
    var random = new Random(115);
    var line = 0;
    Origin Next() => Span(++line, 1, 2);
    IrExpr Leaf() => random.Next(2) == 0 ? Constant(random.Next(100)) with { Origins = [Next()] } : new IrLocalRef(_a) { Origins = [Next()] };
    IrExpr Tree(int depth) => depth == 0 ? Leaf() : random.Next(5) switch {
      0 => new IrUnary(IrUnaryOp.Negate, Tree(depth - 1)) { Origins = [Next()] },
      1 => new IrBinary(IrBinaryOp.Add, Tree(depth - 1), Tree(depth - 1)) { Origins = [Next()] },
      2 => new IrConditional(Tree(depth - 1), Tree(depth - 1), Tree(depth - 1)) { Origins = [Next()] },
      3 => new IrCall("F", IrType.I32, [Tree(depth - 1), Tree(depth - 1)]) { Origins = [Next()] },
      _ => Leaf()
    };
    IrStmt Statement(int depth) => depth > 0 && random.Next(4) == 0
      ? new IrIf(Tree(2), new IrBlock([Statement(depth - 1), Statement(depth - 1)]), new IrBlock([Statement(depth - 1)])) { Origins = [Next()] }
      : new IrAssign(_a, Tree(3)) { Origins = [Next()] };
    IrExpr MapLeaves(IrExpr expression) {
      var operands = IrTree.Operands(expression);
      return operands.Count == 0
        ? random.Next(3) == 0 ? IrOrigins.Replace(Constant(random.Next(100)), expression) : expression
        : IrTree.With(expression, [.. operands.Select(MapLeaves)]);
    }

    IrStmt Rewrite(IrStmt statement) => statement is IrBlock block
      ? block with { Statements = [.. block.Statements.Select(Rewrite)] }
      : IrTree.With(statement, [.. IrTree.Expressions(statement).Select(MapLeaves)], [.. IrTree.Blocks(statement).Select(inner => (IrBlock)Rewrite(inner))]);

    var body = new IrBlock([.. Enumerable.Range(0, 8).Select(_ => Statement(2))]);
    var nodes = 0;
    for (var round = 0; round < 2000; ++round) {
      var statements = body.Statements.ToList();
      var at = random.Next(statements.Count);
      switch (random.Next(6)) {
        case 0:
          statements[at] = Rewrite(statements[at]);
          break;
        case 1:
          statements[at] = IrOrigins.Replace(Statement(1), statements[at]);
          break;
        case 2 when at + 1 < statements.Count:
          statements[at] = IrOrigins.Replace(new IrAssign(_a, Tree(1)), statements[at], statements[at + 1]);
          statements.RemoveAt(at + 1);
          break;
        case 3:
          statements.Insert(at, IrOrigins.MarkSynthetic(new IrAssign(_a, IrOrigins.MarkSynthetic(Constant(0), "O0002")), "O0002"));
          break;
        case 4 when statements.Count > 4:
          statements.RemoveAt(at);
          break;
        default:
          var (head, tail) = IrOrigins.Split(new IrBlock(statements), at);
          statements = [head, tail];
          break;
      }

      var module = Module([_source], [.. statements]);
      foreach (var (_, statement) in IrOrigins.Instructions(module.Functions[0])) {
        Assert.That(statement.Origins, Is.Not.Empty);
        foreach (var expression in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
          Assert.That(expression.Origins, Is.Not.Empty);
          ++nodes;
        }
      }

      // The halves of a split are blocks, no instructions: the next round works on their statements, at most 24.
      body = new IrBlock([.. Flat(statements).Take(24)]);
    }

    Assert.That(nodes, Is.GreaterThan(2000));
  }

}
