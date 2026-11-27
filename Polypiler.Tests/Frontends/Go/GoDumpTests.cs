using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.Frontends.Go.AstDump;
using Polypiler.Core.Frontends.Go.Lowering;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>The wire format of the Go helper, its node model, the text of Go string literals and the commands the helper is tried with.</summary>
[TestFixture]
public sealed class GoDumpTests {

  private static GoNode Node(string json) => new(JsonDocument.Parse(json).RootElement);

  [TestCase("\"plain\"", "plain")]
  [TestCase("\"\\a\\b\\f\\n\\r\\t\\v\\\\\\'\\\"\"", "\a\b\f\n\r\t\v\\'\"")]
  [TestCase("\"\\x41\\101\\u00e9\\U0001F600\"", "AAé😀")]
  [TestCase("\"é😀\"", "é😀")]
  [TestCase("`raw\\n\r`", "raw\\n")]
  public void StringLiterals_DenoteTheirText(string literal, string text) => Assert.That(Strings.Unquote(literal), Is.EqualTo(text));

  [Test]
  public void AStringThatIsNoUtf8_IsRefused()
    => Assert.That(() => Strings.Unquote("\"\\xff\""), Throws.TypeOf<GoUnsupportedException>().With.Message.Contains("no valid UTF-8"));

  [Test]
  public void Nodes_ReadTheirFieldsOnce() {
    var node = Node("""{"_":"Ident","pos":[3,4],"end":[3,5],"Name":"x","type":"int32","mode":"value","obj":"var","decl":[3,4],"field":true,"spread":false,"List":[{"_":"A"},1],"Bits":18446744073709551615,"Count":7}""");
    Assert.That((node.Kind, node.Line, node.Column, node.Type, node.Underlying, node.Mode, node.Object, node.Declaration), Is.EqualTo(("Ident", 3, 4, "int32", "int32", "value", "var", "3:4")));
    Assert.That(node.Is("Ident") && node.Flag("field") && !node.Flag("spread"), Is.True);
    Assert.That(node.Nodes("List"), Has.Count.EqualTo(1).And.SameAs(node.Nodes("List")));
    Assert.That(node.Node("List"), Is.Null);
    Assert.That(node.Node("X"), Is.Null.And.SameAs(node.Node("X")));
    Assert.That(node.Nodes("Missing"), Is.Empty);
    Assert.That((node.Unsigned("Bits"), node.Unsigned("Name"), node.Number("Count"), node.Number("Name")), Is.EqualTo(((ulong?)ulong.MaxValue, (ulong?)null, 7L, 0L)));
    Assert.That((node.Constant, node.ConstantKind, node.Package, node.ToString()), Is.EqualTo(((string?)null, (string?)null, (string?)null, "Ident(3,4)")));
    var bare = Node("""{"pos":"x","decl":[0,0]}""");
    Assert.That((bare.Kind, bare.Line, bare.Type, bare.Mode, bare.Declaration), Is.EqualTo(("", 1, "", "", "")));
  }

  [Test]
  public void Dumps_AreReadByTheirFormat() {
    Assert.That(DumpReader.Read("""{"format":1,"go":"go1.27","error":{"kind":"type","msg":"bad","line":2,"col":5}}""").Error, Is.EqualTo(("type", "bad", 2, 5)));
    Assert.That(DumpReader.Read("""{"format":1,"error":{}}""").Error, Is.EqualTo(("type", "", 0, 0)));
    var parsed = DumpReader.Read("""{"format":1,"go":"go1.27","file":{"_":"File"}}""");
    Assert.That((parsed.File!.Kind, parsed.Go, parsed.Error), Is.EqualTo(("File", "go1.27", ((string, string, int, int)?)null)));
    Assert.That(DumpReader.Read("""{"format":"1"}""").Error!.Value.Message, Does.Contain("another wire format"));
  }

  [Test]
  public void TheToolchain_IsTheOneGivenAlone_OrTheEnvironmentsAndGo() {
    Assert.That(GoHelper.Candidates("/opt/go/bin/go"), Is.EqualTo(new[] { "/opt/go/bin/go" }));
    Assert.That(GoHelper.Candidates(null).Last(), Is.EqualTo("go"));
    Assert.That(GoHelper.Candidates(" ").Last(), Is.EqualTo("go"));
    Assert.That(GoHelper.Source, Does.StartWith("// Command polypiler-go-dump"));
  }

  [Test]
  public void ACommandThatIsNoGo_IsTheFailureOfTheHelper() {
    var (json, failure) = GoFrontend.Print("x.go", "package x\n", "false");
    Assert.That((json, failure is not null), Is.EqualTo(((string?)null, true)));
  }

}
