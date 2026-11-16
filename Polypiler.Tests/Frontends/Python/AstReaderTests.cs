using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.AstDump;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The wire format of the helper (version 1), read into the node model: the tree, python's syntax
/// error, a dump of another format or no dump at all, and the constants as the helper writes them.
/// </summary>
[TestFixture]
public sealed class AstReaderTests {

  [Test]
  public void RecordedDump_ReadsIntoTheNodeModel() {
    const string source = "def f(a: int) -> int:\n    return a + 0x_FF\n";
    var parsed = AstReader.Read(PythonFixture.Dump("snippet.py", source));
    Assert.That(parsed.Error, Is.Null);
    var function = parsed.Module!.Nodes("body")[0];
    Assert.That((function.Kind, function.Text("name"), function.Line, function.Column, function.EndLine), Is.EqualTo(("FunctionDef", "f", 1, 1, 2)));
    var sum = function.Nodes("body")[0].Node("value")!;
    Assert.That((sum.Kind, sum.Operator(), sum.Column, sum.EndColumn), Is.EqualTo(("BinOp", "Add", 12, 21)));
    Assert.That(sum.Node("right")!.Text("value"), Is.EqualTo("255"));
    Assert.That(sum.Node("missing"), Is.Null);
    Assert.That(sum.Nodes("missing"), Is.Empty);
    Assert.That(sum.Texts("missing"), Is.Empty);
    Assert.That(sum.ToString(), Is.EqualTo("BinOp(2,12)"));
    Assert.That(sum.Node("op")!.Line, Is.EqualTo(1), "an operator has no range");
    Assert.That(ReferenceEquals(function.Nodes("body")[0], function.Nodes("body")[0]), Is.True, "a field read twice gives the same node");
  }

  [Test]
  public void GlobalStatement_ListsItsNames() {
    var parsed = AstReader.Read(PythonFixture.Dump("snippet.py", "def f() -> int:\n    global a, b\n    return 1\n"));
    Assert.That(parsed.Module!.Nodes("body")[0].Nodes("body")[0].Texts("names"), Is.EqualTo(new[] { "a", "b" }));
  }

  [TestCase("not json", "the Python helper wrote no tree: ")]
  [TestCase("[]", "the Python helper speaks another wire format than 1")]
  [TestCase("{\"format\": 2}", "the Python helper speaks another wire format than 1")]
  [TestCase("{\"format\": \"1\"}", "the Python helper speaks another wire format than 1")]
  [TestCase("{\"format\": 1, \"python\": \"3.14.8\"}", "the Python helper wrote neither a tree nor an error")]
  public void BrokenDump_IsAnError(string json, string message) {
    var parsed = AstReader.Read(json);
    Assert.That(parsed.Module, Is.Null);
    Assert.That(parsed.Error!.Value.Message, Does.StartWith(message));
  }

  /// <summary>Test design 18: a wire-format mismatch and a dump that is no tree refuse the import with PLY1800.</summary>
  [Test]
  public void ImportOfABrokenDump_IsPLY1800() {
    var result = PythonFrontend.ImportDump("f.py", "", "{\"format\": 2}");
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
    Assert.That(result.Diagnostics[0].ToString(), Is.EqualTo("f.py(1,1): error PLY1800: the Python helper speaks another wire format than 1"));
    Assert.That(result.Python, Is.Empty);
  }

  [Test]
  public void ErrorInTheDump_IsPythonsMessageAtItsPosition() {
    var result = PythonFrontend.ImportDump("f.py", "", "{\"format\": 1, \"python\": \"3.14.8\", \"error\": {\"type\": \"SyntaxError\", \"msg\": \"invalid syntax\", \"line\": 3, \"col\": 7}}");
    Assert.That(result.Diagnostics[0].ToString(), Is.EqualTo("f.py(3,7): error PLY1800: SyntaxError: invalid syntax"));
    Assert.That(result.Python, Is.EqualTo("3.14.8"));
  }

  /// <summary>Test design 18: no interpreter can be started.</summary>
  [Test]
  public void MissingInterpreter_IsAFailureThatSaysHowToNameOne() {
    var missing = Path.Combine(Path.GetTempPath(), "polypiler-no-python-" + Guid.NewGuid().ToString("N"));
    var (json, failure) = AstHelper.Run("f.py", "", [missing, missing + "3"]);
    Assert.That(json, Is.Null);
    Assert.That(failure, Does.StartWith("no Python interpreter could be started (" + missing + ": ").And.EndWith("; set POLYPILER_PYTHON to one"));
  }

  [Test]
  public void Candidates_PutTheNamedInterpreterFirst() {
    Assert.That(AstHelper.Candidates("/opt/python"), Is.EqualTo(Environment.GetEnvironmentVariable(AstHelper.InterpreterVariable) is { Length: > 0 } configured
      ? new[] { "/opt/python", configured, "python3", "python" }
      : new[] { "/opt/python", "python3", "python" }));
    Assert.That(AstHelper.Candidates(" ").TakeLast(2), Is.EqualTo(new[] { "python3", "python" }));
    Assert.That(AstHelper.Script, Does.Contain("ast.parse").And.Not.Contain("exec(").And.Not.Contain("import_module"));
  }

  [TestCase("0x1.8000000000000p+1", 3.0)]
  [TestCase("-0x1.0000000000000p-1", -0.5)]
  [TestCase("0x0.0p+0", 0.0)]
  [TestCase("0x0.0000000000001p-1022", 5e-324)]
  [TestCase("0x1.fffffffffffffp+1023", double.MaxValue)]
  [TestCase("inf", double.PositiveInfinity)]
  [TestCase("-inf", double.NegativeInfinity)]
  [TestCase("+inf", double.PositiveInfinity)]
  [TestCase("0x1p+4", 16.0)]
  public void FloatHex_ReadsBackExactly(string text, double expected)
    => Assert.That(BitConverter.DoubleToInt64Bits(Constants.Float(text)), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));

  [Test]
  public void FloatHex_ReadsNaNAndNegativeZero() {
    Assert.That(double.IsNaN(Constants.Float("nan")), Is.True);
    Assert.That(double.IsNegative(Constants.Float("-0x0.0p+0")), Is.True);
  }

  [Test]
  public void PythonTypes_JoinToTheWiderNumber() {
    Assert.That(PyTypes.Join(PyType.Unknown, PyType.Int), Is.EqualTo(PyType.Int));
    Assert.That(PyTypes.Join(PyType.Float, PyType.Unknown), Is.EqualTo(PyType.Float));
    Assert.That(PyTypes.Join(PyType.Bool, PyType.Float), Is.EqualTo(PyType.Float));
    Assert.That(PyTypes.Join(PyType.Int, PyType.Bool), Is.EqualTo(PyType.Int));
    Assert.That(PyTypes.Join(PyType.None, PyType.Int), Is.EqualTo(PyType.Optional(PyType.Int)));
    Assert.That(PyTypes.Join(PyType.Optional(PyType.Int), PyType.Float), Is.EqualTo(PyType.Optional(PyType.Float)));
    Assert.That(PyTypes.Join(PyType.None, PyType.Str), Is.Null);
    Assert.That(PyTypes.Join(PyType.Str, PyType.Int), Is.Null);
    Assert.That(PyTypes.Join(PyType.List(PyType.Unknown), PyType.List(PyType.Int)), Is.EqualTo(PyType.List(PyType.Int)));
    Assert.That(PyTypes.Join(PyType.List(PyType.Int), PyType.List(PyType.Float)), Is.Null);
    Assert.That(PyTypes.Join(PyType.Tuple([PyType.Int]), PyType.Tuple([PyType.Int, PyType.Int])), Is.Null);
    Assert.That(new[] { PyType.Unknown, PyType.Bool, PyType.Int, PyType.Float, PyType.None, PyType.Str, PyType.Bytes, PyType.ByteArray }.Select(PyTypes.Name),
      Is.EqualTo(new[] { "unknown", "bool", "int", "float", "None", "str", "bytes", "bytearray" }));
    Assert.That(new[] { PyType.List(PyType.Int), PyType.Tuple([PyType.Int, PyType.Float]), PyType.Optional(PyType.Int), PyType.Iterator(PyType.Str), PyType.Table(PyType.Int) }.Select(PyTypes.Name),
      Is.EqualTo(new[] { "list[int]", "tuple[int, float]", "int | None", "Iterator[str]", "tuple[int, ...]" }));
    Assert.That(() => PyTypes.Ir(PyType.Unknown), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

}
