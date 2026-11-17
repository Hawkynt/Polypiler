using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// #1424 on the pinned toolchain: a global of an enum type, open and closed, and a reference to a
/// derived class that may be null converted to its base class compute what .NET computes with
/// overflow checks on and off, and the crate builds clean under clippy and rustfmt.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustEnumGlobalTests {

  /// <summary>The C# of the scenario, built by the C# compiler as well: the .NET side of the differential.</summary>
  private static class Scenario {

    public const string Source = """
      public enum Color : byte { Red = 1, Green = 2, Blue = 4 }
      public enum Mode { Off, On, Auto }
      public class A { public virtual int M() => 1; }
      public sealed class B : A { public override int M() => 2; }
      public static class S {
        static Color current = Color.Green;
        static Color unset;
        static Mode mode = Mode.On;
        public static int Swap(int c) { var old = current; current = (Color)c; return (int)old * 1000 + (int)current * 10 + (int)unset; }
        public static int Toggle(bool auto) { var was = mode == Mode.On ? 1 : mode == Mode.Auto ? 2 : 0; mode = auto ? Mode.Auto : Mode.Off; return was; }
        static int Of(A a) => a == null ? 0 : a.M();
        static A Base(B b) => b;
        public static int Upcast(bool make) { B b = make ? new B() : null; A a = b; return Of(b) + (a == null ? 10 : 20) + (Base(b) == null ? 100 : 200); }
      }
      """;

    public enum Color : byte { Red = 1, Green = 2, Blue = 4 }

    public enum Mode { Off, On, Auto }

    public class A { public virtual int M() => 1; }

    public sealed class B : A { public override int M() => 2; }

    private static Color current = Color.Green;
    private static readonly Color unset = default;
    private static Mode mode = Mode.On;

    public static int Swap(int c) { var old = current; current = (Color)c; return (int)old * 1000 + (int)current * 10 + (int)unset; }
    public static int Toggle(bool auto) { var was = mode == Mode.On ? 1 : mode == Mode.Auto ? 2 : 0; mode = auto ? Mode.Auto : Mode.Off; return was; }
    private static int Of(A? a) => a == null ? 0 : a.M();
    private static A? Base(B? b) => b;
    public static int Upcast(bool make) { var b = make ? new B() : null; A? a = b; return Of(b) + (a == null ? 10 : 20) + (Base(b) == null ? 100 : 200); }

  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task EnumGlobalsAndNullableUpcast_ComputeWhatDotNetComputes() {
    var module = PrototypeFixture.Import("Scenario.cs", Scenario.Source);
    // The crate runs the calls in order in a process of its own per build mode; .NET's statics live in this process, so .NET is asked once.
    PrototypeCall[] calls = [new("Swap", [4]), new("Swap", [7]), new("Swap", [255]), new("Toggle", [true]), new("Toggle", [false]), new("Toggle", [true]), new("Upcast", [true]), new("Upcast", [false])];
    var dotnet = calls.Select(call => PrototypeFixture.InvokedOutcome(typeof(Scenario).GetMethod(call.Method)!, call)).ToList();
    Assert.That(dotnet, Is.EqualTo(new[] { "2040", "4070", "9550", "1", "2", "0", "222", "110" }));
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(dotnet), $"overflow checks {checks}");

    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(RustCrates.Emit(module));
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

}
