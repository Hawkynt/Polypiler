using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Closed enums on the pinned toolchain (#462): the Rust enum computes what .NET computes with overflow
/// checks on and off, its checked conversion takes a declared value and refuses one between, below or
/// above them, and the crate is lint- and format-clean.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustClosedEnumTests {

  /// <summary>The C# of the scenario, built by the C# compiler as well: the .NET side of the differential.</summary>
  private static class Scenario {

    public const string Source = """
      public enum Op : byte { Add = 1, Sub = 5, Mul = 6 }
      public static class S {
        static Op Decode(byte b) { if (b == 1) return Op.Add; if (b == 5) return Op.Sub; return Op.Mul; }
        static int Apply(Op op, int a, int b) { switch (op) { case Op.Add: return a + b; case Op.Sub: return a - b; case Op.Mul: return a * b; } return 0; }
        public static int F(byte code, int a, int b) => Apply(Decode(code), a, b);
        public static int Code(byte code) => (int)Decode(code);
        public static bool Less(byte x, byte y) => Decode(x) < Decode(y);
      }
      """;

    public enum Op : byte { Add = 1, Sub = 5, Mul = 6 }

    private static Op Decode(byte b) { if (b == 1) return Op.Add; if (b == 5) return Op.Sub; return Op.Mul; }

    private static int Apply(Op op, int a, int b) { switch (op) { case Op.Add: return a + b; case Op.Sub: return a - b; case Op.Mul: return a * b; } return 0; }

    public static int F(byte code, int a, int b) => Apply(Decode(code), a, b);

    public static int Code(byte code) => (int)Decode(code);

    public static bool Less(byte x, byte y) => Decode(x) < Decode(y);

  }

  /// <summary>An integration test of the crate: 5 is Sub, 2 (between), 0 (below) and 7 (above) are refused, each variant back to its value.</summary>
  private const string Conversions = """
    use s::s::Op;

    #[test]
    fn declared_values_convert_and_others_are_refused() {
        assert_eq!(Op::try_from(5u8), Ok(Op::Sub));
        assert_eq!(Op::try_from(1u8), Ok(Op::Add));
        assert_eq!(Op::try_from(6u8), Ok(Op::Mul));
        assert_eq!(Op::try_from(2u8), Err(2));
        assert_eq!(Op::try_from(0u8), Err(0));
        assert_eq!(Op::try_from(7u8), Err(7));
        assert_eq!(u8::from(Op::Mul), 6);
        assert_eq!(Op::default(), Op::Add);
    }

    """;

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ClosedEnum_ComputesWhatDotNetComputes_AndConvertsCheckedly() {
    var module = PrototypeFixture.Import("Scenario.cs", Scenario.Source);
    var files = RustCrates.Emit(module);
    Assert.That(RustCrates.ModuleText(files), Does.Contain("#[repr(u8)]").And.Contain("Sub = 5,").And.Contain("impl TryFrom<u8> for Op {").And.Not.Contain("transmute").And.Not.Contain("unsafe {"));

    PrototypeCall[] calls = [new("F", [(byte)1, 7, 3]), new("F", [(byte)5, 7, 3]), new("F", [(byte)9, int.MaxValue, 2]), new("Code", [(byte)5]), new("Code", [(byte)2]), new("Less", [(byte)1, (byte)5]), new("Less", [(byte)6, (byte)5])];
    var dotnet = calls.Select(call => PrototypeFixture.InvokedOutcome(typeof(Scenario).GetMethod(call.Method)!, call)).ToList();
    Assert.That(dotnet.Take(2), Is.EqualTo(new[] { "10", "4" }));
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(dotnet), $"overflow checks {checks}");

    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = new Dictionary<string, System.ReadOnlyMemory<byte>>(RustCrates.Package(files)) { ["tests/conversions.rs"] = Encoding.UTF8.GetBytes(Conversions) };
    var report = await runner.RunTestsAsync(package);
    Assert.That(report.Tests.Where(test => test.Name.Contains("declared_values", System.StringComparison.Ordinal)).Select(test => test.Outcome), Is.EqualTo(new[] { RustTestOutcome.Passed }), report.Output);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

}
