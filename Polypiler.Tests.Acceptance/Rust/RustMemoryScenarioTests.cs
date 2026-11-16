using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The scenarios of #460 with their hand-checked values, as C# sources: a pointer walk becomes an index and
/// counts to the first zero byte; a parallel loop becomes <c>zip</c> and writes only as far as the shorter
/// view. Each crate is built by the pinned toolchain and called with overflow checks on and off.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed partial class RustMemoryScenarioTests {

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task PointerWalk_IsAnIndex_AndCountsToTheFirstZero() {
    var module = RustCrates.Import("public static unsafe int Strlen(byte[] d) { fixed (byte* s = d) { byte* p = s; while (*p != 0) p++; return (int)(p - s); } }");
    var text = RustCrates.ModuleText(RustCrates.Emit(module));
    Assert.That(RawPointer().IsMatch(text), Is.False, "no raw pointer type and no unsafe");
    var calls = new[] { new PrototypeCall("Strlen", [new byte[] { 0x41, 0x42, 0x43, 0x00, 0x44 }]) { Returns = typeof(int) } };
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))).And.Some.StartsWith("3 |").Or.Some.EqualTo("3"));
  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ParallelLoop_IsAZip_AndWritesAsFarAsTheShorterView() {
    var module = RustCrates.Import("public static void Xor(byte[] src, byte[] dst) { for (int i = 0; i < System.Math.Min(src.Length, dst.Length); i++) dst[i] = (byte)(src[i] ^ 0x5A); }");
    Assert.That(RustCrates.ModuleText(RustCrates.Emit(module)), Does.Contain("in src.iter().zip(dst.iter_mut()) {"));
    var calls = new[] {
      new PrototypeCall("Xor", [new byte[] { 0x00, 0xFF, 0x5A, 0x01 }, new byte[5]]),
      new PrototypeCall("Xor", [new byte[] { 1, 2, 3, 4, 5 }, new byte[4]]),
      new PrototypeCall("Xor", [new byte[0], new byte[2]])
    };
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(expected[0], Is.EqualTo("void | [0,255,90,1] | [90,165,0,91,0]"));
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(expected));
  }

  [GeneratedRegex(@"\*const|\*mut|\bunsafe\b(?!_code)")]
  private static partial Regex RawPointer();

}
