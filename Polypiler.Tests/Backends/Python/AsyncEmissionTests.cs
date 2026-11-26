using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;
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
  public void ForeignCall_EmitsCtypesBindingAndRoundTripsSyntax() {
    var module = Native(new IrExprStmt(new IrForeignCall("Abs", IrType.I32, [I(-1)])), new IrReturn(new IrForeignCall("Abs", IrType.I32, [I(-2)])));
    Assert.That(Problems(module), Is.Empty);
    var source = Emit(module);
    Assert.That(source, Does.Contain("import ctypes\n").And.Contain("ctypes.CDLL(\"libc.so.6\")").And.Contain("getattr(_t1, \"abs\")").And.Contain("ctypes.c_int32"));
    AssertRoundTrip(module, PythonProfile.Default, source);
  }

  [Test]
  public void LastError_EmitsCtypesErrorHelper_AcrossProfiles() {
    var module = Native(new IrReturn(new IrLastError(false)));
    foreach (var name in new[] { "py3.8", "py3.14" }) {
      var profile = PythonProfile.Parse(name);
      Assert.That(PythonEmitter.Check(module, profile), Is.Empty, name);
      var source = PythonEmitter.Emit(module, profile)[0].Content;
      Assert.That(source, Does.Contain("import ctypes\n").And.Contain("getattr(ctypes, \"get_last_error\", ctypes.get_errno)()"), name);
      AssertRoundTrip(module, profile, source);
    }
  }

  [Test]
  public void ForeignCall_AllMarshalKindsStructsAndConventions_EmitAndRoundTrip() {
    var inner = new IrStructType("Inner", [new IrField("Flag", IrType.Bool, false), new IrField("Code", IrType.U16, false)]) {
      Layout = new IrStructLayout(IrLayoutKind.Sequential, Pack: 1, Size: 0, Offsets: [])
    };
    var outer = new IrStructType("Outer", [new IrField("Sub", IrType.Struct(inner), false), new IrField("Val", IrType.F64, false)]) {
      Layout = new IrStructLayout(IrLayoutKind.Sequential, Pack: 0, Size: 0, Offsets: [])
    };
    var unionStruct = new IrStructType("Overlay", [new IrField("AsInt", IrType.I32, false), new IrField("AsFloat", IrType.F32, false)]) {
      Layout = new IrStructLayout(IrLayoutKind.Explicit, Pack: 0, Size: 4, Offsets: [0, 0])
    };
    var innerType = IrType.Struct(inner);
    var outerType = IrType.Struct(outer);
    var unionType = IrType.Struct(unionStruct);

    var fAll = new IrForeignFunction("AllKinds", true, "native", outerType, [
      new("b8", IrType.I8, IrMarshal.Blittable),
      new("u8", IrType.U8, IrMarshal.Blittable),
      new("s16", IrType.I16, IrMarshal.Blittable),
      new("u32", IrType.U32, IrMarshal.Blittable),
      new("i64", IrType.I64, IrMarshal.Blittable),
      new("u64", IrType.U64, IrMarshal.Blittable),
      new("ni", IrType.I64, new(IrMarshalKind.Blittable) { NativeSize = true }),
      new("nu", IrType.U64, new(IrMarshalKind.Blittable) { NativeSize = true }),
      new("f32", IrType.F32, IrMarshal.Blittable),
      new("ptr", IrType.Pointer(IrType.I32), IrMarshal.Blittable),
      new("b4", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool }),
      new("bu1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }),
      new("bi1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 }),
      new("h", IrType.I64, new(IrMarshalKind.Handle)),
      new("utf8", IrType.String, new(IrMarshalKind.String) { Encoding = IrStringEncoding.Utf8 }),
      new("utf16", IrType.String, new(IrMarshalKind.String) { Encoding = IrStringEncoding.Unicode }),
      new("inArr", IrType.Array(IrType.I32), new(IrMarshalKind.Array, IrMarshalDirection.In)),
      new("outArr", IrType.Array(IrType.I32), new(IrMarshalKind.Array, IrMarshalDirection.InOut)),
      new("refNum", IrType.Array(IrType.I32), new(IrMarshalKind.ByRef, IrMarshalDirection.Out)),
      new("refIn", IrType.Array(IrType.I32), new(IrMarshalKind.ByRef, IrMarshalDirection.In)),
      new("refSt", IrType.Array(outerType), new(IrMarshalKind.ByRef, IrMarshalDirection.InOut)),
      new("refStOut", IrType.Array(innerType), new(IrMarshalKind.ByRef, IrMarshalDirection.Out)),
      new("ov", unionType, new(IrMarshalKind.Struct))
    ]) {
      Convention = IrCallingConvention.StdCall,
      SetLastError = true,
      Result = new(IrMarshalKind.Struct)
    };
    var fVoid = new IrForeignFunction("VoidCall", false, "native", IrType.Void, [
      new("r", IrType.Array(IrType.I64), new(IrMarshalKind.ByRef, IrMarshalDirection.InOut) { NativeSize = true })
    ]);
    var fBool = new IrForeignFunction("BoolRet", false, "native", IrType.Bool, []) { Result = new(IrMarshalKind.Bool) };
    var fHandle = new IrForeignFunction("___", false, "native", IrType.I64, []) { EntryPoint = "get_h", Result = new(IrMarshalKind.Handle) };

    var refI64 = L("r", IrType.Array(IrType.I64));
    var module = new IrModule("InteropAll", [
      Function("run", IrType.I64, [refI64],
        new IrExprStmt(new IrForeignCall("VoidCall", IrType.Void, [R(refI64)])),
        new IrExprStmt(new IrForeignCall("BoolRet", IrType.Bool, [])),
        new IrReturn(new IrForeignCall("___", IrType.I64, [])))
    ]) {
      Types = [inner, outer, unionStruct],
      ForeignFunctions = [fAll, fVoid, fBool, fHandle]
    };

    foreach (var name in PythonProfile.Names) {
      var profile = PythonProfile.Parse(name);
      Assert.That(PythonEmitter.Check(module, profile), Is.Empty, name);
      var source = PythonEmitter.Emit(module, profile)[0].Content;
      Assert.That(source, Does.Contain("ctypes.Structure").And.Contain("ctypes.Union").And.Contain("_pack_ = 1").And.Contain("use_last_error=True").And.Contain("\"WinDLL\""), name);
      AssertRoundTrip(module, profile, source);
    }
  }

  [Test]
  public void Showcase19NativeInterop_TranslatesAndRoundTripsAcrossAllPythonProfiles() {
    var mod = ImportShowcase("19-native-interop.cs");
    foreach (var name in PythonProfile.Names) {
      var profile = PythonProfile.Parse(name);
      Assert.That(PythonEmitter.Check(mod, profile), Is.Empty, name);
      var source = PythonEmitter.Emit(mod, profile)[0].Content;
      Assert.That(source, Does.Contain("import ctypes\n").And.Contain("class _CtypesNativePoint(ctypes.Structure):").And.Contain("ctypes.CDLL(\"geom\")"), name);
      AssertRoundTrip(mod, profile, source);
    }
  }

  [Test]
  public void Showcase17Stdio_AndWriteLineVariants_TranslateAndRunLiveUnderPython3() {
    var mod = ImportShowcase("17-stdio.cs");
    foreach (var name in PythonProfile.Names) {
      var profile = PythonProfile.Parse(name);
      Assert.That(PythonEmitter.Check(mod, profile), Is.Empty, name);
      var source = PythonEmitter.Emit(mod, profile)[0].Content;
      Assert.That(source, Does.Contain("print("), name);
      AssertRoundTrip(mod, profile, source);
    }

    // Exercise every IrWriteLine operand shape (IrNull, IrStrConst, plain local, call/temp) + live python3 execution.
    var s = L("s", IrType.String);
    var variants = new IrModule("WriteVariants", [
      Function("emitAll", IrType.Void, [s],
        new IrWriteLine(new IrStrConst("lit")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(R(s)),
        new IrWriteLine(new IrCall("missing", IrType.String, [])),
        new IrReturn(null)),
      Function("missing", IrType.String, [], new IrReturn(new IrNull(IrType.String))) with { IsPublic = false }
    ]);
    var varSrc = Emit(variants);
    AssertRoundTrip(variants, PythonProfile.Default, varSrc);

    if (File.Exists("/usr/bin/python3")) {
      var stdioSrc = Emit(mod);
      var output = RunPython3(stdioSrc + "\nassert run() == 14\n");
      var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
      Assert.That(lines, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }));

      var varOutput = RunPython3(varSrc + "\nemit_all(None)\nemit_all(\"ok\")\n");
      var varLines = varOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
      Assert.That(varLines[..8], Is.EqualTo(new[] { "lit", "", "", "", "lit", "", "ok", "" }));

      // Live libc abs(-42) via ctypes on Linux!
      if (OperatingSystem.IsLinux()) {
        var libcSrc = Emit(Native(new IrReturn(new IrForeignCall("Abs", IrType.I32, [I(-42)]))));
        RunPython3(libcSrc + "\nassert f() == 42\n");
      }
    }
  }

  private static void AssertRoundTrip(IrModule module, PythonProfile profile, string source) {
    var expected = PythonEmitter.Build(module, profile, FloatMode.Accurate, null, TargetPassOptions.None).Tree;
    var parsed = PythonSyntaxReader.ReadModule(source);
    Assert.That(PythonSyntaxRewriter.Shape(parsed), Is.EqualTo(PythonSyntaxRewriter.Shape(expected)), profile.Name);
  }

  private static IrModule ImportShowcase(string fileName) {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polypiler.slnx")))
      dir = dir.Parent;
    Assert.That(dir, Is.Not.Null);
    var result = CSharpFrontend.Import(fileName, File.ReadAllText(Path.Combine(dir!.FullName, "docs", "showcase", fileName)));
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  private static string RunPython3(string script) {
    var psi = new ProcessStartInfo("/usr/bin/python3") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(script);
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    Assert.That(proc.ExitCode, Is.Zero, () => $"python3 failed:\n{stderr}\n--- SCRIPT ---\n{script}");
    return stdout;
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
