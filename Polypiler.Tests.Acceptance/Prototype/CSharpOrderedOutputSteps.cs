using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

[Binding, Scope(Feature = "Emit ordered standard output in C#")]
public sealed class CSharpOrderedOutputSteps {

  private IrModule _module = null!;
  private Diagnostic[] _diagnostics = [];
  private (string Text, object? Value, Exception? Error) _oracle;
  private (string Text, object? Value, Exception? Error) _generated;

  [Given("the IR equivalent of the pinned OutputEffect.cs fixture")]
  public void Oracle() => this._module = OutputEffectFixture.Module();

  [Given("an IR write of the Int32 value 42")]
  public void NonString() => this._module = new("NonString", [
    new("Run", true, IrType.Void, [], [], new([new IrWriteLine(new IrConst(IrType.I32, 42)), new IrReturn(null)]))
  ]);

  [When("^it is emitted to C# and the two compiled versions run with choose (true|false)$")]
  public void Choose(string choose) => this.Run("Run", bool.Parse(choose));

  [When("the two compiled versions call Trap with divisor 0")]
  public void DivideByZero() => this.Run("Trap", 0);

  [When("the module is checked for C# emission")]
  public void Check() => this._diagnostics = CSharpEmitter.Check(this._module).ToArray();

  [Then("^both emit " + StepText.Quoted + ", a blank line, and " + StepText.Quoted + " in order$")]
  public void WithBlank(string first, string last) => this.Output(first, "", last);

  [Then("^both emit " + StepText.Quoted + " and " + StepText.Quoted + " in order$")]
  public void WithoutBlank(string first, string last) => this.Output(first, last);

  [Then("^both emit only " + StepText.Quoted + "$")]
  public void Only(string first) => this.Output(first);

  [Then("both return 42")]
  public void ReturnValue() {
    Assert.That(this._oracle.Value, Is.EqualTo(42));
    Assert.That(this._generated.Value, Is.EqualTo(this._oracle.Value));
    Assert.That(this._oracle.Error, Is.Null);
    Assert.That(this._generated.Error, Is.Null);
  }

  [Then("both raise DivideByZeroException")]
  public void Trap() {
    Assert.That(this._oracle.Error, Is.TypeOf<DivideByZeroException>());
    Assert.That(this._generated.Error, Is.TypeOf<DivideByZeroException>());
  }

  [Then("it reports PLY0100 and no C# file is produced")]
  public void Refused() {
    Assert.That(this._diagnostics.Select(error => error.Code), Is.EqualTo(new[] { "PLY0100" }));
    Assert.That(() => CSharpEmitter.Emit(this._module), Throws.ArgumentException);
  }

  private void Run(string method, object argument) {
    var original = GeneratedCSharp.Compile(OutputEffectFixture.Source(), checkOverflow: true);
    Assert.That(CSharpEmitter.Check(this._module), Is.Empty);
    var source = CSharpEmitter.Emit(this._module).Single().Content;
    TestContext.Out.WriteLine($"Generated C# SHA-256 {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}");
    var generated = GeneratedCSharp.Compile(source, checkOverflow: true);
    this._oracle = Capture(original.Method(method), argument);
    this._generated = Capture(generated.Method(method), argument);
  }

  private void Output(params string[] lines) {
    Assert.That(this._oracle.Text, Is.EqualTo(string.Join(Environment.NewLine, lines) + Environment.NewLine));
    Assert.That(this._generated.Text, Is.EqualTo(this._oracle.Text));
  }

  private static (string Text, object? Value, Exception? Error) Capture(MethodInfo method, object argument) {
    using var output = new StringWriter(CultureInfo.InvariantCulture);
    var previous = Console.Out;
    try {
      Console.SetOut(output);
      var value = method.Invoke(null, [argument]);
      return (output.ToString(), value, null);
    } catch (TargetInvocationException error) {
      return (output.ToString(), null, error.InnerException);
    } finally {
      Console.SetOut(previous);
    }
  }

}
