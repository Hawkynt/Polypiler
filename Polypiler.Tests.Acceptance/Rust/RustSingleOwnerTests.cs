using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Single-owner classes as plain structs on the pinned toolchain (#1393): the state object of the first
/// Rust demonstration computes what .NET computes with overflow checks on and off, an object two
/// variables hold stays shared and computes the same, and the crates - with those of the Rust
/// fixtures that came back through the backend - build lint- and format-clean.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustSingleOwnerTests {

  /// <summary>The C# of the scenarios, built by the C# compiler as well: the .NET side of the differential.</summary>
  private static class Scenario {

    public const string Source = """
      public sealed class Xorshift32 {
        private uint _state;
        public Xorshift32(uint seed) { this._state = seed; }
        public uint Next() { var x = this._state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; this._state = x; return x; }
      }
      public sealed class Counter {
        private int _n;
        public void Add(int v) { this._n += v; }
        public int Get() => this._n;
      }
      public static class S {
        public static uint First(uint seed) => new Xorshift32(seed).Next();
        public static uint Second(uint seed) { var g = new Xorshift32(seed); g.Next(); return g.Next(); }
        public static int Aliased(int v) { var a = new Counter(); var b = a; b.Add(v); a.Add(1); return a.Get(); }
      }
      """;

    public sealed class Xorshift32 {
      private uint _state;
      public Xorshift32(uint seed) { this._state = seed; }
      public uint Next() { var x = this._state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; this._state = x; return x; }
    }

    public sealed class Counter {
      private int _n;
      public void Add(int v) { this._n += v; }
      public int Get() => this._n;
    }

    public static uint First(uint seed) => new Xorshift32(seed).Next();
    public static uint Second(uint seed) { var g = new Xorshift32(seed); g.Next(); return g.Next(); }
    public static int Aliased(int v) { var a = new Counter(); var b = a; b.Add(v); a.Add(1); return a.Get(); }

  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task StateObject_IsAPlainStruct_ComputingWhatDotNetComputes() {
    var module = PrototypeFixture.Import("Scenario.cs", Scenario.Source);
    var text = RustCrates.ModuleText(RustCrates.Emit(module));
    Assert.That(text, Does.Contain("pub struct Xorshift32 {").And.Contain("state: u32,").And.Contain("pub fn new(seed: u32) -> Self {").And.Contain("pub fn next(&mut self) -> u32 {"));
    Assert.That(text, Does.Contain("pub struct Xorshift32 {\n    state: u32,\n}").And.Not.Contain("Rc<Xorshift32>"));
    Assert.That(text, Does.Contain("Rc<Counter>"), "an object two variables hold stays shared");

    PrototypeCall[] calls = [new("First", [2463534242u]), new("Second", [2463534242u]), new("First", [1u]), new("Second", [uint.MaxValue]), new("Aliased", [41]), new("Aliased", [int.MaxValue])];
    var dotnet = calls.Select(call => PrototypeFixture.InvokedOutcome(typeof(Scenario).GetMethod(call.Method)!, call)).ToList();
    Assert.That(dotnet.Take(2), Is.EqualTo(new[] { "723471715", "2497366906" }), "Marsaglia's first two outputs of the seed 2463534242");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(dotnet), $"overflow checks {checks}");

    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(RustCrates.Emit(module));
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

  /// <summary>The Rust fixtures that came back through the backend with arrays as struct fields and borrowed second variables, in both import modes.</summary>
  [TestCase("types")]
  [TestCase("edges")]
  [TestCase("reassign")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task RoundTripCrate_IsLintAndFormatClean(string fixture) {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    foreach (var mode in RustFixture.Modes) {
      var package = RustCrates.Package(RustCrates.Emit(RustFixture.Import(fixture, mode)));
      Assert.That(await runner.ClippyAsync(package), Is.Empty, RustFixture.NameOf(mode));
      Assert.That(await runner.FormatCheckAsync(package), Is.Empty, RustFixture.NameOf(mode));
    }
  }

}
