using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// A constructor that runs no constructor of its base classes (#1388), as a JavaScript object literal
/// that meets objects of a class runs none: the fields of the base classes hold their defaults until its
/// body stores them. C# reaches the base classes through constructors that run no code, JavaScript
/// makes the object from the prototype.
/// </summary>
[TestFixture]
public sealed class IrSkipsBaseTests {

  /// <summary>A class A with a constructor that stores 7 into x, and a class B below it whose constructor skips it and stores its argument into y.</summary>
  private sealed class Built {

    public IrClassType A { get; } = new("A");

    public IrClassType B { get; } = new("B");

    public IrField X { get; } = new("x", IrType.I32, false);

    public IrField Y { get; } = new("y", IrType.I32, false);

    public IrConstructor ACtor { get; } = new("A.ctor");

    public IrConstructor BCtor { get; }

    public List<IrFunction> Functions { get; } = [];

    public Built(IrBlock? bBody = null, bool another = false, IrClassType? library = null) {
      this.BCtor = new("B.ctor") { SkipsBase = true };
      this.A.Inherit(library, []);
      this.B.Inherit(this.A, []);
      this.A.Define([this.X]);
      this.B.Define([this.Y]);
      var extra = new IrConstructor("B.other");
      this.A.DefineMembers([this.ACtor], [], [], []);
      this.B.DefineMembers(another ? [this.BCtor, extra] : [this.BCtor], [], [], []);
      var aSelf = new IrLocal("this", IrType.Object(this.A));
      var bSelf = new IrLocal("this", IrType.Object(this.B));
      var y = new IrLocal("y", IrType.I32);
      var b = new IrLocal("b", IrType.Object(this.B));
      this.Functions.AddRange([
        new IrFunction("A.ctor", false, IrType.Void, [aSelf], [], new IrBlock([new IrFieldStore(new IrLocalRef(aSelf), this.X, new IrConst(IrType.I32, 7))])),
        new IrFunction("B.ctor", false, IrType.Void, [bSelf, y], [], bBody ?? new IrBlock([new IrFieldStore(new IrLocalRef(bSelf), this.Y, new IrLocalRef(y))])),
        new IrFunction("f", true, IrType.I32, [], [b], new IrBlock([
          new IrDeclare(b, new IrNew(IrType.Object(this.B), this.BCtor, [new IrConst(IrType.I32, 3)])),
          new IrReturn(new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrLocalRef(b), this.X), new IrBinary(IrBinaryOp.Multiply, new IrFieldLoad(new IrLocalRef(b), this.Y), new IrConst(IrType.I32, 10))))
        ]))
      ]);
      if (another)
        this.Functions.Add(new IrFunction("B.other", false, IrType.Void, [new IrLocal("this", IrType.Object(this.B))], [], new IrBlock([])));
    }

    public IrModule Module => new("M", this.Functions) { Types = [this.A, this.B] };

  }

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  /// <summary>The object of B has x at its default, 0, as A's constructor never ran, and y as B's stored it: 30, in the interpreter and in C#.</summary>
  [Test]
  public void SkippingConstructor_LeavesTheBaseFieldsAtTheirDefaults() {
    var module = new Built().Module;
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(30));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(30), text);
    Assert.That(text, Does.Contain("protected A(M.Uninitialized uninitialized) {").And.Contain(": base((M.Uninitialized)null)"));
  }

  /// <summary>JavaScript makes the object from B's prototype where it has classes, and writes into this where a constructor function stands for the class.</summary>
  [TestCase("es2022", "Object.create(B.prototype)")]
  [TestCase("es5", "function B(y) {")]
  public void SkippingConstructor_IsWrittenAtEveryLevel(string level, string expected) {
    var profile = JavaScriptProfile.Parse(level);
    var script = LegalizedJavaScript.Emit(new Built().Module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    Assert.That(script, Does.Contain(expected).And.Not.Contain("A.call(this").And.Not.Contain("super("), script);
  }

  /// <summary>A class without a base class whose constructor skips nothing is written as any other.</summary>
  [Test]
  public void RootClassThatSkips_IsAnOrdinaryClass() {
    var root = new IrClassType("R");
    var field = new IrField("z", IrType.I32, false);
    var constructor = new IrConstructor("R.ctor") { SkipsBase = true };
    root.Inherit(null, []);
    root.Define([field]);
    root.DefineMembers([constructor], [], [], []);
    var self = new IrLocal("this", IrType.Object(root));
    var module = new IrModule("M", [
      new IrFunction("R.ctor", false, IrType.Void, [self], [], new IrBlock([new IrFieldStore(new IrLocalRef(self), field, new IrConst(IrType.I32, 4))])),
      new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrFieldLoad(new IrNew(IrType.Object(root), constructor, []), field))]))
    ]) { Types = [root] };
    Assert.That(Problems(module), Is.Empty);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(4), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es2022")).Single().Content;
    Assert.That(script, Does.Not.Contain("Object.create").And.Not.Contain("Uninitialized"), script);
    var python = PythonFixtures.Translate(module);
    Assert.That(python.Problems, Is.Empty);
    Assert.That(python.Source, Does.Contain("self.z = 0").And.Not.Contain("super()"), python.Source);
    Assert.That(RustCrates.Check(module), Is.Empty);
  }

  /// <summary>Python initializes every inherited field to its default and emits no super().__init__ call, keeping even an empty __init__ so the base constructor is never inherited (#1419).</summary>
  [Test]
  public void Python_SkippingConstructor_InitializesAllFieldsAndNeverCallsSuper() {
    var translation = PythonFixtures.Translate(new Built().Module);
    Assert.That(translation.Problems, Is.Empty);
    var text = translation.Source!;
    Assert.That(text, Does.Contain("class B(A):\n    def __init__(self, y: int) -> None:\n        self.x = 0\n        self.y = 0\n        self.y = y"), text);
    Assert.That(text, Does.Not.Contain("super()"), text);
    var parsed = PythonSyntaxReader.ReadModule(text);
    Assert.That(PythonPrinter.Print(PythonSyntaxReader.ReadModule(PythonPrinter.Print(parsed))), Is.EqualTo(PythonPrinter.Print(parsed)));

    // When neither base nor derived declares fields and B.ctor has an empty body, B.__init__ must still be kept as `pass` so A.__init__ is not inherited.
    var ran = new IrGlobal("ran", IrType.I32, new IrConst(IrType.I32, 0));
    var a = new IrClassType("A");
    var b = new IrClassType("B");
    var aCtor = new IrConstructor("A.ctor");
    var bCtor = new IrConstructor("B.ctor") { SkipsBase = true };
    a.Inherit(null, []);
    b.Inherit(a, []);
    a.Define([]);
    b.Define([]);
    a.DefineMembers([aCtor], [], [], []);
    b.DefineMembers([bCtor], [], [], []);
    var o = new IrLocal("o", IrType.Object(b));
    var emptyModule = new IrModule("Empty", [
      new IrFunction("A.ctor", false, IrType.Void, [new IrLocal("this", IrType.Object(a))], [], new IrBlock([new IrGlobalAssign(ran, new IrConst(IrType.I32, 1))])),
      new IrFunction("B.ctor", false, IrType.Void, [new IrLocal("this", IrType.Object(b))], [], new IrBlock([])),
      new IrFunction("f", true, IrType.I32, [], [o], new IrBlock([
        new IrDeclare(o, new IrNew(IrType.Object(b), bCtor, [])),
        new IrReturn(new IrGlobalRef(ran))
      ]))
    ]) { Types = [a, b], Globals = [ran] };
    Assert.That(Problems(emptyModule), Is.Empty);
    Assert.That(IrInterpreter.Call(emptyModule, "f", []), Is.EqualTo(0));
    var emptyPy = PythonFixtures.Translate(emptyModule);
    Assert.That(emptyPy.Problems, Is.Empty);
    Assert.That(emptyPy.Source, Does.Contain("class B(A):\n    def __init__(self) -> None:\n        pass"), emptyPy.Source);
  }

  /// <summary>Rust constructs the derived struct with all fields defaulted and runs only B.ctor without calling A.ctor (#1419).</summary>
  [Test]
  public void Rust_SkippingConstructor_LeavesBaseFieldsAtDefaults() {
    var module = new Built().Module;
    Assert.That(RustCrates.Check(module), Is.Empty);
    var text = RustCrates.ModuleText(RustCrates.Emit(module));
    Assert.That(text, Does.Contain("pub struct B {").And.Contain("x: Cell<i32>,").And.Contain("y: Cell<i32>,"));
    Assert.That(text, Does.Contain("pub fn b_ctor(this: &B, y: i32) {\n    this.y.set(y);\n}"), text);
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
  }

  [Test]
  [Category("EndToEnd")]
  public void PythonAndRust_SkippingConstructor_RunAndReturn30() {
    var module = new Built().Module;
    var pyDir = Directory.CreateTempSubdirectory("skips-base-py-");
    var rsDir = Directory.CreateTempSubdirectory("skips-base-rs-");
    try {
      File.WriteAllText(Path.Combine(pyDir.FullName, "m.py"), PythonFixtures.Translate(module).Source!);
      using (var python = Process.Start(new ProcessStartInfo("python3", ["-c", "import m; assert m.f() == 30"]) { WorkingDirectory = pyDir.FullName, RedirectStandardError = true })!) {
        var stderr = python.StandardError.ReadToEnd();
        python.WaitForExit();
        Assert.That(python.ExitCode, Is.EqualTo(0), stderr);
      }

      foreach (var file in RustCrates.Emit(module)) {
        var full = Path.Combine(rsDir.FullName, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, file.Content);
      }

      Directory.CreateDirectory(Path.Combine(rsDir.FullName, "tests"));
      File.WriteAllText(Path.Combine(rsDir.FullName, "tests", "skips.rs"), "#[test]\nfn skips_base() {\n    assert_eq!(m::m::f(), 30);\n}\n");
      using (var cargo = Process.Start(new ProcessStartInfo("cargo", ["test", "--offline"]) { WorkingDirectory = rsDir.FullName, RedirectStandardOutput = true, RedirectStandardError = true })!) {
        var output = cargo.StandardOutput.ReadToEnd() + "\n" + cargo.StandardError.ReadToEnd();
        cargo.WaitForExit();
        Assert.That(cargo.ExitCode, Is.EqualTo(0), output);
      }
    } finally {
      pyDir.Delete(true);
      rsDir.Delete(true);
    }
  }

  /// <summary>The verifier refuses a skipping constructor that calls a constructor, stands beside another or skips a class of the library.</summary>
  [Test]
  public void Verifier_RefusesWhatNoTargetWrites() {
    var built = new Built();
    var self = new IrLocal("this", IrType.Object(built.B));
    var calling = new Built(new IrBlock([new IrExprStmt(new IrCall("A.ctor", IrType.Void, [new IrLocalRef(self)]))]));
    Assert.That(Problems(calling.Module), Has.Some.Contains("the constructor that skips its base classes calls a constructor"));
    Assert.That(Problems(new Built(another: true).Module), Has.Some.Contains("the class B has a constructor that skips its base classes beside another"));
    Assert.That(Problems(new Built(library: IrExceptionClass.Exception).Module), Has.Some.Contains("the constructor of B skips the constructor of a class of the library"));
  }

  /// <summary>The format keeps whether a constructor skips its base classes.</summary>
  [Test]
  public void Format_KeepsTheMark() {
    var read = IrModuleFormat.Read(IrModuleFormat.Write(new Built().Module));
    var constructors = read.Types.OfType<IrClassType>().SelectMany(type => type.Constructors).ToList();
    Assert.That(constructors.Select(constructor => (constructor.Function, constructor.SkipsBase)), Is.EqualTo(new[] { ("A.ctor", false), ("B.ctor", true) }));
    Assert.That(IrInterpreter.Call(read, "f", []), Is.EqualTo(30));
  }

}
