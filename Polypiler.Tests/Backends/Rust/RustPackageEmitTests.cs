using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>The crate as a package (#459): its functions, their visibility and calls, and the unit tests it carries.</summary>
[TestFixture]
public sealed class RustPackageEmitTests {

  private const string Mixer = "public static uint Mix(uint h, uint b) => Step(h ^ b); static uint Step(uint h) => h * 16777619u; public static uint Twice(uint h) => Mix(Mix(h, 1), 2);";

  [Test]
  public void Functions_KeepTheirVisibilityAndCalls() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(Mixer), level: Polypiler.Core.Optimize.OptimizationLevel.None));
    Assert.That(text, Does.Contain("pub fn mix(h: u32, b: u32) -> u32 {\n    step(h ^ b)\n}"));
    Assert.That(text, Does.Contain("fn step(h: u32) -> u32 {\n    h.wrapping_mul(16777619)\n}"));
    Assert.That(text, Does.Contain("pub fn twice(h: u32) -> u32 {\n    mix(mix(h, 1), 2)\n}"));
  }

  [Test]
  public void Recursion_IsACallOfItself()
    => Assert.That(RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);"), level: Polypiler.Core.Optimize.OptimizationLevel.None)),
      Does.Contain("if n < 2 {\n        n\n    } else {\n        fib(n.wrapping_sub(1)).wrapping_add(fib(n.wrapping_sub(2)))\n    }"));

  [Test]
  public void Tests_AreTheUnitTestsOfTheModule() {
    var options = new RustEmitOptions {
      Tests = [
        new RustTestCase("MixOfA", "Mix", [new IrConst(IrType.U32, 2166136261u), new IrConst(IrType.U32, 97)], new IrConst(IrType.U32, 0xE40C292Cu)),
        new RustTestCase("rejects_overflow", "Twice", [new IrConst(IrType.U32, 0)], null, IrTrap.IntegerOverflow),
        new RustTestCase("runs", "Twice", [new IrConst(IrType.U32, 0)], null)
      ]
    };
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(Mixer), options: options));
    Assert.That(text, Does.Contain("#[cfg(test)]\nmod tests {\n    use super::*;\n\n    #[test]\n    fn mix_of_a() {\n        assert_eq!(mix(2166136261, 97), 3826002220);\n    }\n"));
    Assert.That(text, Does.Contain("    #[test]\n    #[should_panic(expected = \"IntegerOverflow\")]\n    fn rejects_overflow() {\n        twice(0);\n    }\n"));
    Assert.That(text, Does.EndWith("    #[test]\n    fn runs() {\n        twice(0);\n    }\n}\n"));
  }

  [Test]
  public void FloatTest_ComparesBits() {
    var options = new RustEmitOptions { Tests = [new RustTestCase("half", "Half", [new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 3.0))], new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.5)))] };
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("public static double Half(double a) => a / 2;"), options: options));
    Assert.That(text, Does.Contain("assert_eq!(half(3.0).to_bits(), 1.5f64.to_bits());"));
  }

  [Test]
  public void TestOfAnUnknownFunction_IsRefused() {
    var options = new RustEmitOptions { Tests = [new RustTestCase("t", "Missing", [], null)] };
    var problems = RustEmitter.Check(RustCrates.Prepared(RustCrates.Import(Mixer)), RustProfile.Default, null, options);
    Assert.That(problems.Single().Message, Does.Contain("a test of the function 'Missing' with 0 arguments"));
  }

}
