using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Closed enums as Rust enums (#462): an enum whose values the module only ever takes from its members is
/// <c>#[repr(u8)] pub enum Op { Add = 1, Sub = 5 }</c> with the checked conversion <c>impl TryFrom&lt;u8&gt;</c>
/// and a <c>match</c> over its variants without a <c>_</c> arm; every way a value may be no member keeps
/// the open newtype.
/// </summary>
[TestFixture]
public sealed class ClosedEnumEmitTests {

  private static string Emitted(string source) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source), level: OptimizationLevel.None));

  private const string Op = "public enum Op : byte { Add = 1, Sub = 5, Mul = 6 } ";

  private const string Uses = "public static class S { static Op Decode(byte b) { if (b == 1) return Op.Add; if (b == 5) return Op.Sub; return Op.Mul; } "
    + "static int Apply(Op op, int a, int b) { switch (op) { case Op.Add: return a + b; case Op.Sub: return a - b; case Op.Mul: return a * b; } return 0; } "
    + "public static int F(byte code, int a, int b) => Apply(Decode(code), a, b); public static int Code(byte code) => (int)Decode(code); ";

  private static string Closed(string more = "", string type = Op) => Emitted(type + Uses + more + " }");

  [Test]
  public void EnumOnlyOfItsMembers_IsARustEnumWithACheckedConversion() {
    var text = Closed();
    Assert.That(text, Does.Contain("#[repr(u8)]\n#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]\npub enum Op {\n    #[default]\n    Add = 1,\n    Sub = 5,\n    Mul = 6,\n}"));
    Assert.That(text, Does.Contain("impl TryFrom<u8> for Op {\n    type Error = u8;\n\n    fn try_from(value: u8) -> Result<Self, u8> {\n        match value {\n            1 => Ok(Self::Add),\n            5 => Ok(Self::Sub),\n            6 => Ok(Self::Mul),\n            _ => Err(value),\n        }\n    }\n}"));
    Assert.That(text, Does.Contain("impl From<Op> for u8 {\n    fn from(value: Op) -> Self {\n        value as u8\n    }\n}"));
    Assert.That(text, Does.Contain("return Op::Sub;"));
    Assert.That(text, Does.Contain("i32::from(decode(code) as u8)"));
    Assert.That(text, Does.Not.Contain("transmute").And.Not.Contain("unsafe"));
  }

  [Test]
  public void SwitchOverEveryVariant_HasNoWildcardArm() {
    var text = Closed();
    Assert.That(text, Does.Contain("    match op {\n        Op::Add => a.wrapping_add(b),\n        Op::Sub => a.wrapping_sub(b),\n        Op::Mul => a.wrapping_mul(b),\n    }\n}"));
  }

  [Test]
  public void SwitchOverSomeVariants_KeepsTheWildcardArm() {
    var text = Closed("static int Some(Op op) { switch (op) { case Op.Add: return 1; default: return 2; } } public static int G(byte b) => Some(Decode(b));");
    Assert.That(text, Does.Contain("Op::Add => 1,").And.Contain("_ => 2,"));
  }

  [Test]
  public void MemberOfValueZero_IsTheDefault_AndNewArraysMayHoldIt() {
    var text = Emitted("public enum Level : int { Low, Mid, High } public static class S { static Level At(int i) { var a = new Level[2]; a[1] = Level.High; return a[i]; } public static int F(int i) => (int)At(i); }");
    Assert.That(text, Does.Contain("#[repr(i32)]").And.Contain("    #[default]\n    Low = 0,"));
    Assert.That(text, Does.Not.Contain("pub struct Level("));
  }

  [Test]
  public void NegativeMembers_AreDiscriminantsWithTheirSign() {
    var text = Emitted("public enum Sign : sbyte { Minus = -1, Zero, Plus } public static class S { static Sign Of(int x) => x < 0 ? Sign.Minus : x == 0 ? Sign.Zero : Sign.Plus; public static int F(int x) => (int)Of(x) * 10; }");
    Assert.That(text, Does.Contain("#[repr(i8)]").And.Contain("    Minus = -1,\n    #[default]\n    Zero = 0,\n    Plus = 1,"));
    Assert.That(text, Does.Contain("-1 => Ok(Self::Minus),"));
    Assert.That(RustSyntaxReader.Read(text).Items.OfType<RsEnum>().Single().Variants.Select(variant => (variant.Discriminant, variant.IsDefault)), Is.EqualTo(new[] { ("-1", false), ("0", true), ("1", false) }));
  }

  [Test]
  public void EveryValueOfAByte_HasNoWildcardInItsConversion() {
    var members = string.Join(", ", Enumerable.Range(0, 256).Select(value => $"V{value} = {value}"));
    var text = Emitted($"public enum Full : byte {{ {members} }} public static class S {{ static Full One() => Full.V200; public static int F() => (int)One(); }}");
    Assert.That(text, Does.Contain("255 => Ok(Self::V255),\n        }"));
    Assert.That(text, Does.Not.Contain("_ => Err(value)"));
  }

  [Test]
  public void OneMember_IsAnEnumOfOneVariant()
    => Assert.That(Emitted("public enum One : byte { Only = 3 } public static class S { static One Get() => One.Only; public static int F() => (int)Get(); }"), Does.Contain("pub enum One {\n    #[default]\n    Only = 3,\n}"));

  [TestCase("public static int G(Op op) => (int)op;", TestName = "TakenByAPublicFunction_IsOpen")]
  [TestCase("public static Op G() => Op.Add;", TestName = "GivenByAPublicFunction_IsOpen")]
  [TestCase("static Op Cast(byte b) => (Op)b; public static int G(byte b) => (int)Cast(b);", TestName = "IntegerConverted_IsOpen")]
  [TestCase("static Op Or(Op a, Op b) => a | b; public static int G() => (int)Or(Op.Add, Op.Sub);", TestName = "BitwiseOperator_IsOpen")]
  [TestCase("static Op Flip(Op a) => ~a; public static int G() => (int)Flip(Op.Add);", TestName = "Complement_IsOpen")]
  [TestCase("static Op Seven() => (Op)7; public static int G() => (int)Seven();", TestName = "ConstantThatIsNoMember_IsOpen")]
  [TestCase("static Op Parsed(string s) => System.Enum.Parse<Op>(s); public static int G(string s) => (int)Parsed(s);", TestName = "ParsedText_IsOpen")]
  [TestCase("static Op First() => (new Op[1])[0]; public static int G() => (int)First();", TestName = "NewArrayWithoutAZeroMember_IsOpen")]
  [TestCase("static Op Held() => default(Pair).A; public static int G() => (int)Held(); public struct Pair { public Op A; public int B; }", TestName = "DefaultStructWithoutAZeroMember_IsOpen")]
  public void ValueThatMayBeNoMember_KeepsTheNewtype(string more) {
    var text = Closed(more);
    Assert.That(text, Does.Contain("pub struct Op(pub u8);"));
    Assert.That(text, Does.Not.Contain("impl TryFrom<u8> for Op"));
  }

  [TestCase("[System.Flags] public enum Op : byte { Add = 1, Sub = 4, Mul = 8 } ", TestName = "FlagsEnum_IsOpen")]
  [TestCase("public enum Op : byte { Add = 6, Sub = 5, Mul = 1 } ", TestName = "DescendingDeclarationOrder_IsOpen")]
  public void EnumThatCannotBeClosed_KeepsTheNewtype(string type)
    => Assert.That(Closed("", type), Does.Contain("pub struct Op(pub u8);"));

  [Test]
  public void RepeatedValues_AreOpen()
    => Assert.That(Emitted("public enum Op : byte { Add = 1, Sub = 5, Mul = 5 } public static class S { static Op Get() => Op.Add; public static int F() => (int)Get(); }"), Does.Contain("pub struct Op(pub u8);"));

  [Test]
  public void FieldOfAClassWithoutAZeroMember_IsOpen()
    => Assert.That(Emitted(Op + "public sealed class Holder { public Op Kept; } " + Uses + "public static int G() { var h = new Holder(); var x = h; x.Kept = Op.Sub; return (int)h.Kept; } }"),
      Does.Contain("pub struct Op(pub u8);"));

  [Test]
  public void RustFixtureEnums_AreClosed() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustFrontend.Import("types.rs", RustFixture.SourceOf("types"), OverflowChecks.Panic).Module!));
    Assert.That(text, Does.Contain("#[repr(u8)]\n#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]\npub enum Op {"));
    Assert.That(text, Does.Contain("Halt = 255,"));
    Assert.That(text, Does.Contain("pub enum Sign {\n    Minus = -1,"));
  }

  [Test]
  public void DefaultVariant_PrintsItsAttribute()
    => Assert.That(RustPrinter.Print(new RsEnum("E", "", [new RsVariant("A", "1"), new RsVariant("B", "2") { IsDefault = true }])), Is.EqualTo("enum E {\n    A = 1,\n    #[default]\n    B = 2,\n}\n"));

}
