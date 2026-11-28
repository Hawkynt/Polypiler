using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Steps;

[Binding, Scope(Feature = "Closed tagged unions")]
public sealed class ClosedUnionSteps {

  private static readonly IrType _numberOrText = IrType.Union(IrType.I32, IrType.String);
  private IrModule? _module;
  private IrExpr? _value;
  private object? _tag;
  private object? _payload;
  private IrTrapException? _trap;

  private static IrModule Function(IrType returns, IrExpr body, params IrLocal[] parameters)
    => new("Unions", [new IrFunction("F", true, returns, parameters, [], new IrBlock([new IrReturn(body)]))]);

  [Given("^a function returning union<i32, str> that returns the union member (\\d+) of " + StepText.Quoted + "$")]
  public void Returning(int member, string text) => this._module = Function(_numberOrText, new IrUnionOf(_numberOrText, member, new IrStrConst(text)));

  [When(@"^the interpreter runs it and reads the union tag and the union payload of member (\d+)$")]
  public void Runs(int member) {
    Assert.That(IrVerifier.Verify(this._module!), Is.Empty);
    var union = IrInterpreter.Call(this._module!, "F", []);
    var u = new IrLocal("u", _numberOrText);
    this._tag = IrInterpreter.Call(Function(IrType.I32, new IrUnionTagOf(new IrLocalRef(u)), u), "F", [union!]);
    this._payload = IrInterpreter.Call(Function(IrType.String, new IrUnionValue(new IrLocalRef(u), member), u), "F", [union!]);
  }

  [Then("^the union tag is (\\d+) and the union payload is " + StepText.Quoted + "$")]
  public void TagAndPayload(int tag, string payload) => Assert.That((this._tag, this._payload), Is.EqualTo((tag, payload)));

  [Given(@"^the union value of member (\d+) holding (\d+) of union<i32, str>$")]
  public void Holding(int member, int value) => this._value = new IrUnionOf(_numberOrText, member, new IrConst(IrType.I32, value));

  [When(@"^the union payload of member (\d+) is evaluated$")]
  public void Payload(int member) {
    var module = Function(IrType.String, new IrUnionValue(this._value!, member));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    try {
      IrInterpreter.Call(module, "F", []);
    } catch (IrTrapException trap) {
      this._trap = trap;
    }
  }

  [Then(@"^the interpreter traps with (\w+)$")]
  public void Traps(string trap) => Assert.That(this._trap?.Trap, Is.EqualTo(Enum.Parse<IrTrap>(trap)));

  [Given(@"^a local of a union of (\d+) integer members$")]
  public void Local(int count) {
    IrType[] members = [IrType.I8, IrType.I16, IrType.I32, IrType.I64, IrType.U8];
    var local = new IrLocal("u", IrType.Union(members[..count]));
    this._module = new("Unions", [new IrFunction("F", true, IrType.Void, [], [local], new IrBlock([]))]);
  }

  [When("^the module of the union is verified$")]
  public void Verified() { }

  [Then("^PLY0100 reports " + StepText.Quoted + "$")]
  public void Reports(string reason) {
    var diagnostics = IrVerifier.Verify(this._module!);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(IrVerifier.Code));
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(reason));
  }

}
