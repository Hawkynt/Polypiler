using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Enums;

/// <summary>
/// The enums written by the JavaScript backend (#1318): every operation over every underlying type at
/// every level, parsed at the level it claims and using nothing its level lacks; the frozen object of
/// an enum, its export in every module format, a public enum of 64 bits below ECMAScript 2020, and the
/// form of each operation. Whether the code computes what the interpreter computes is decided by node
/// in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptEnumEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "Generated" : null, typed)
  ];

  private static string Emit(JavaScriptProfile profile, IrModule? module = null) {
    module ??= EnumFixtures.Module;
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 13: every operation at every level and in every module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryOperation_IsWrittenAtEveryLevel() {
    foreach (var profile in _profiles)
      Assert.That(Emit(profile), Does.Contain("Object.freeze({ None: 0, Read: 1, Write: 2, Exec: 4, ReadWrite: 3 })"), profile.ToString());
  }

  [TestCase("es5", "cjs", "var Perm = Object.freeze({ None: 0, Read: 1, Write: 2, Exec: 4, ReadWrite: 3 });")]
  [TestCase("es5", "cjs", "exports.Perm = Perm;")]
  [TestCase("es5", "cjs", "var $names$Perm = [[\"None\", 0], [\"Read\", 1], [\"Write\", 2], [\"ReadWrite\", 3], [\"Exec\", 4]];")]
  [TestCase("es5", "cjs", "var $names$S = [[\"A\", 0], [\"C\", 1], [\"B\", 2], [\"Neg\", 4294967295]];")]
  [TestCase("es5", "cjs", "return $enumName($names$Perm, true, v, 32);")]
  [TestCase("es5", "cjs", "$enumName64($names$Big, false, ")]
  [TestCase("es5", "cjs", "$enumParse64($names$Big, s, false, false)")]
  [TestCase("es5", "cjs", "$enumName($names$Small, false, ")]
  [TestCase("es5", "cjs", "return $enumParse($names$Perm, s, false, 32, true);")]
  [TestCase("es5", "cjs", "$enumDefined($names$Color, v & 0xFF, 8)")]
  [TestCase("es5", "cjs", "case Color.Red: {")]
  [TestCase("es5", "cjs", "[\"A\", \"C\", \"B\", \"Neg\"].join(\",\")")]
  [TestCase("es5", "iife", "Perm: Perm")]
  [TestCase("es2015", "esm", "export const Perm = Object.freeze(")]
  [TestCase("es2020", "esm", "const Big = Object.freeze({ Max: 18446744073709551615n, One: 1n });")]
  [TestCase("es2020", "esm", "const $names$Big = [[\"One\", 1, 0], [\"Max\", 4294967295, 4294967295]];")]
  [TestCase("es2020", "esm", "const Long = Object.freeze({ Low: 1n, High: -9223372036854775808n });")]
  [TestCase("es2020", "esm", "((x & Perm.Exec) === Perm.Exec)")]
  [TestCase("es2022", "esm", "new Int32Array([S.A, S.C, S.B, S.Neg])")]
  [TestCase("es2022", "esm", "export const None = Object.freeze({});")]
  [TestCase("es2022", "esm", "(((~x) & f) === 0)")]
  public void Operation_HasTheFormOfTheLevel(string level, string module, string form)
    => Assert.That(Emit(JavaScriptProfile.Parse(level, module, module == "iife" ? "Generated" : null)), Does.Contain(form));

  /// <summary>A private enum of 64 bits below ECMAScript 2020 has no object: its members are pairs of halves where they are used.</summary>
  [TestCase("es5")]
  [TestCase("es2017")]
  public void PrivateLongEnum_HasNoObjectBelowEs2020(string level) {
    var text = Emit(JavaScriptProfile.Parse(level, "cjs"));
    Assert.That(text, Does.Not.Contain("Big = Object.freeze").And.Not.Contain("Long = Object.freeze"));
    Assert.That(text, Does.Contain("{ hi: 2147483648, lo: 0 }"));
  }

  /// <summary>Test design 9: a public enum of 64 bits below ECMAScript 2020 has no object, and nothing is written.</summary>
  [TestCase("es5", true)]
  [TestCase("es2017", true)]
  [TestCase("es2020", false)]
  public void PublicLongEnum_IsInexpressibleBelowEs2020(string level, bool refused) {
    var big = new IrEnumType("Big", IrType.U64, [new("Max", -1)]) { IsPublic = true };
    var function = new IrFunction("F", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Equal, new IrConst(big.Type, -1), new IrConst(big.Type, -1)))]));
    var module = new IrModule("M", [function]) { Types = [big] };
    var profile = JavaScriptProfile.Parse(level, "cjs");
    var problems = JavaScriptEmitter.Check(module, profile).Select(problem => (problem.Code, problem.Message)).ToList();
    if (refused) {
      Assert.That(problems, Is.EqualTo(new[] { (JavaScriptEmitter.Inexpressible, $"'the public 64-bit enum Big' cannot be expressed in JavaScript profile '{level}'") }));
      Assert.That(() => JavaScriptEmitter.Emit(module, profile), Throws.ArgumentException.With.Message.Contains("PLY1152").Or.Message.Contains("the public 64-bit enum Big"));
    } else {
      Assert.That(problems, Is.Empty);
      Assert.That(Emit(profile, module), Does.Contain("exports.Big = Big;"));
    }
  }

  /// <summary>An enum named like a variable of the module gets a name of its own, which its member reads use.</summary>
  [Test]
  public void EnumNamedLikeAVariable_IsRenamed() {
    var named = new IrEnumType("v", IrType.I32, [new("A", 3)]);
    var parameter = new IrLocal("v", IrType.I32);
    var function = new IrFunction("F", true, IrType.Bool, [parameter], [], new IrBlock([
      new IrReturn(new IrBinary(IrBinaryOp.Equal, new IrConvert(named.Type, new IrLocalRef(parameter)), new IrConst(named.Type, 3)))
    ]));
    var text = Emit(JavaScriptProfile.Parse("es2022"), new IrModule("M", [function]) { Types = [named] });
    Assert.That(text, Does.Contain("const v$e = Object.freeze({ A: 3 });"));
    Assert.That(text, Does.Contain("return v === v$e.A;"));
  }

}
