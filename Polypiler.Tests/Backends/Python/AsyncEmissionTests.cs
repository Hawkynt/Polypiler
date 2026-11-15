using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using static Polypiler.Tests.Backends.Python.PythonIr;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The Python backend writes the async functions of the IR (#1330) with asyncio: an async function is
/// <c>async def</c> typed with its result, an await of a call is <c>await</c>, of a result the value,
/// of the completed task nothing, and WhenAll is <c>await asyncio.gather(…)</c>. A native call and the
/// error one left (#1332) have no Python form and are refused by name.
/// </summary>
[TestFixture]
public sealed class AsyncEmissionTests {

  private static readonly IrLocal _x = L("x", IrType.I32);

  private static IrAsync Completed() => new(IrAsyncOp.Completed, IrType.Async(IrType.Void), []);

  private static IrAsync FromResult(IrExpr value) => new(IrAsyncOp.FromResult, IrType.Async(value.Type), [value]);

  private static IrCall Call(string name, IrType result, params IrExpr[] arguments) => new(name, IrType.Async(result), arguments);

  /// <summary><c>async Task&lt;int&gt; Step(int x) { await Task.CompletedTask; return x * 3 + 1; }</c>, private when asked.</summary>
  private static IrFunction Step(string name = "Step", bool isPublic = true)
    => Function(name, IrType.Async(IrType.I32), [_x], new IrExprStmt(new IrAwait(Completed())), new IrReturn(Bin(IrBinaryOp.Add, R(_x), I(1)))) with { IsPublic = isPublic };

  /// <summary><c>async Task Touch() { await Task.CompletedTask; }</c>.</summary>
  private static IrFunction Touch() => Function("Touch", IrType.Async(IrType.Void), [], new IrExprStmt(new IrAwait(Completed())), new IrReturn(null)) with { IsPublic = false };

  [Test]
  public void AsyncFunction_IsAnAsyncDef_TypedWithItsResult_AndTheCompletedTaskIsLeftOut() {
    var run = Function("Run", IrType.Async(IrType.I32), [_x], new IrReturn(new IrAwait(Call("Step", IrType.I32, R(_x)))));
    var text = Emit(new IrModule("M", [Step(), run, Touch()]));
    Assert.That(text, Does.Contain("\n\n\nasync def step(x: int) -> int:\n    return "));
    Assert.That(text, Does.Contain("async def run(x: int) -> int:\n    return await step(x)\n"));
    Assert.That(text, Does.Contain("async def _touch() -> None:\n    pass\n"));
    Assert.That(text, Does.Not.Contain("asyncio").And.Not.Contain("None\n    None"));
  }

  [Test]
  public void AwaitOfAResult_IsTheValue_AndAnAwaitStandsInsideAnExpression() {
    var five = L("five", IrType.I32);
    var completed = Function("Completed", IrType.Async(IrType.I32), [],
      new IrDeclare(five, new IrAwait(FromResult(I(5)))),
      new IrExprStmt(new IrAwait(FromResult(I(7)))),
      new IrReturn(Bin(IrBinaryOp.Multiply, R(five), new IrAwait(Call("Step", IrType.I32, I(2))))));
    var text = Emit(new IrModule("M", [Step(), completed]));
    Assert.That(text, Does.Contain("async def completed() -> int:\n    five: int = 5\n    7\n"));
    Assert.That(text, Does.Contain("five * await step(2)"));
  }

  [Test]
  public void WhenAll_IsAsyncioGather_WhoseListIsTheArray() {
    var squares = Function("Squares", IrType.Async(IrType.Array(IrType.I32)), [],
      new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Array(IrType.I32)), [Call("Sq", IrType.I32, I(2)), Call("Sq", IrType.I32, I(3))]))));
    var none = Function("NoSquares", IrType.Async(IrType.Array(IrType.I32)), [],
      new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Array(IrType.I32)), []))));
    var both = Function("Both", IrType.Async(IrType.I32), [],
      new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [Call("Touch", IrType.Void), Call("Touch", IrType.Void)]))),
      new IrReturn(I(2)));
    var text = Emit(new IrModule("M", [Step("Sq", false), squares, none, Touch(), both]));
    Assert.That(text, Does.Contain("\nimport asyncio\n"));
    Assert.That(text, Does.Contain("async def squares() -> list[int]:\n    return await asyncio.gather(_sq(2), _sq(3))\n"));
    Assert.That(text, Does.Contain("async def no_squares() -> list[int]:\n    return await asyncio.gather()\n"));
    Assert.That(text, Does.Contain("async def both() -> int:\n    await asyncio.gather(_touch(), _touch())\n    return 2\n"));
  }

  [Test]
  public void OldestProfile_WritesTheSameAsyncFunctions() {
    var squares = Function("Squares", IrType.Async(IrType.Array(IrType.I32)), [],
      new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Array(IrType.I32)), [Call("Sq", IrType.I32, I(2))]))));
    var text = Emit(new IrModule("M", [Step("Sq", false), squares]), "py3.8");
    Assert.That(text, Does.Contain("import asyncio\n").And.Contain("async def squares() -> List[int]:\n    return await asyncio.gather(_sq(2))\n"));
  }

  [Test]
  public void ValueTask_IsWrittenAsAnyAsyncFunction() {
    var valued = Function("Valued", IrType.Async(IrType.I32, true), [_x], new IrReturn(R(_x)));
    Assert.That(Emit(new IrModule("M", [valued])), Does.Contain("async def valued(x: int) -> int:\n    return x\n"));
  }

  [Test]
  public void AsyncFixture_IsWrittenWithoutARefusal() {
    var translated = PythonFixtures.Translate(AsyncFixtures.Imported());
    Assert.That(translated.Problems, Is.Empty);
    Assert.That(translated.Source, Does.Contain("async def run(n: int) -> int:").And.Contain("await asyncio.gather(_sq(2), _sq(3))").And.Contain("import asyncio"));
    var read = PythonSyntaxReader.ReadModule(translated.Source!);
    Assert.That(PythonSyntaxRewriter.Shape(read), Is.EqualTo(PythonSyntaxRewriter.Shape(translated.Tree!)));
  }

  private static IrModule Native(params IrStmt[] body) => new("M", [Function("f", IrType.I32, [], body)]) {
    ForeignFunctions = [new IrForeignFunction("Abs", false, "libc.so.6", IrType.I32, [new IrForeignParameter("x", IrType.I32, IrMarshal.Blittable)]) { EntryPoint = "abs" }]
  };

  [Test]
  public void ForeignCall_IsRefusedByName() {
    var module = Native(new IrExprStmt(new IrForeignCall("Abs", IrType.I32, [I(-1)])), new IrReturn(new IrForeignCall("Abs", IrType.I32, [I(-2)])));
    Assert.That(Problems(module), Is.EqualTo(new[] { "error PLY1851: 'the native call of abs (libc.so.6)' cannot be expressed in Python profile 'py3.14' (function 'f')" }));
  }

  [Test]
  public void LastError_IsRefusedByName() {
    var module = Native(new IrReturn(new IrLastError(false)));
    Assert.That(Problems(module, "py3.8"), Is.EqualTo(new[] { "error PLY1851: 'the last error of a native call (Marshal.GetLastWin32Error)' cannot be expressed in Python profile 'py3.8' (function 'f')" }));
  }

  [TestCase("await f()", "await f()")]
  [TestCase("await (a + b)", "await (a + b)")]
  [TestCase("-await f()", "-await f()")]
  [TestCase("await f() ** 2", "await f() ** 2")]
  [TestCase("(await f()) ** 2", "await f() ** 2")]
  [TestCase("2 ** await f()", "2 ** await f()")]
  [TestCase("await f().x", "await f().x")]
  [TestCase("(await f()).x", "(await f()).x")]
  [TestCase("g(await f(), await h())", "g(await f(), await h())")]
  public void Await_PrintsWithTheParenthesesItNeeds_AndReadsBack(string source, string printed) {
    var tree = PythonSyntaxReader.ReadExpression(source);
    Assert.That(PythonPrinter.Print(tree), Is.EqualTo(printed));
    Assert.That(PythonSyntaxRewriter.Shape(PythonSyntaxReader.ReadExpression(printed)), Is.EqualTo(PythonSyntaxRewriter.Shape(tree)));
  }

  [Test]
  public void AsyncDef_PrintsAndReadsBack_AndTheRewriterWalksTheAwait() {
    const string source = "@cached\nasync def f(x: int) -> int:\n    return await g(x)\nasync def h() -> None:\n    pass\ndef k() -> None:\n    pass\n";
    var module = PythonSyntaxReader.ReadModule(source);
    Assert.That(module.Body.OfType<PyFunctionDef>().Select(function => function.IsAsync), Is.EqualTo(new[] { true, true, false }));
    Assert.That(PythonPrinter.Print(module), Is.EqualTo(source));
    Assert.That(PythonSyntaxRewriter.Shape(module), Does.Contain("FunctionDef async f(").And.Contain("Await(Call(Name g Name x))").And.Contain("FunctionDef k("));
    var renamed = PythonSyntaxRewriter.Rewrite(module, node => node is PyName { Name: "x" } ? new PyName("y") : node);
    Assert.That(PythonPrinter.Print(renamed), Does.Contain("return await g(y)"));
    Assert.That(PythonSyntaxRewriter.Rewrite(module, node => node), Is.SameAs(module));
  }

  [Test]
  public void AsyncDef_OverTheLineWidth_PutsAParameterOnEachLine() {
    var parameters = Enumerable.Range(0, 6).Select(index => new PyParameter($"parameter_number_{index}", new PyName("int"))).ToList();
    var function = new PyFunctionDef("f", parameters, new PyName("int"), [new PyReturn(new PyAwait(new PyCall(new PyName("g"), [])))]) { IsAsync = true };
    var text = PythonPrinter.Print(function);
    Assert.That(text, Does.StartWith("async def f(\n    parameter_number_0: int,\n").And.Contain(") -> int:\n    return await g()\n"));
    Assert.That(PythonSyntaxReader.ReadModule(text).Body.Single(), Is.InstanceOf<PyFunctionDef>().With.Property(nameof(PyFunctionDef.IsAsync)).True);
  }

  [Test]
  public void Await_OfAnOperandThatIsNoPrimary_IsParenthesized()
    => Assert.That(PythonPrinter.Print(new PyAwait(new PyUnary("-", new PyName("x")))), Is.EqualTo("await (-x)"));

  [TestCase("await")]
  [TestCase("async")]
  [TestCase("x + async")]
  [TestCase("await await f()")]
  public void AsyncKeywords_WhereNoExpressionStands_AreRefused(string source)
    => Assert.That(() => PythonSyntaxReader.ReadExpression(source), Throws.TypeOf<FormatException>());

}
