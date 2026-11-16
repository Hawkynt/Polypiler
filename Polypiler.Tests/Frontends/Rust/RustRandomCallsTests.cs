using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The seeded random calls of the Rust differentials (<see cref="RustRandomCalls"/>), which the
/// acceptance tests hold to rustc: the same seed gives the same calls, every call ends within the
/// budget, slices stay within their lengths, and a function no draw ends is reported, not looped on.
/// </summary>
[TestFixture]
public sealed class RustRandomCallsTests {

  private static IrModule Module(string source) => RustFixture.Import("snippet.rs", source, OverflowChecks.Panic);

  private const string Source = "pub fn add(a: u8, b: i16) -> i32 { a as i32 + b as i32 } pub fn sum(d: &[u32], f: f64, t: bool) -> u32 { d.iter().fold(0u32, |s, &x| s.wrapping_add(x)) + (f > 0.0) as u32 + t as u32 } pub fn count(n: u32) -> u32 { let mut i = 0; while i < n { i += 1; } i }";

  [Test]
  public void SameSeed_GivesTheSameCalls() {
    var module = Module(Source);
    var first = RustRandomCalls.For(module, RustRandomCalls.FunctionSeed, 50, 2_000);
    var second = RustRandomCalls.For(module, RustRandomCalls.FunctionSeed, 50, 2_000);
    Assert.That(first.Select(call => call.ToString()), Is.EqualTo(second.Select(call => call.ToString())));
    Assert.That(RustRandomCalls.For(module, RustRandomCalls.TypeSeed, 50, 2_000).Select(call => call.ToString()), Is.Not.EqualTo(first.Select(call => call.ToString())));
  }

  [Test]
  public void EveryDrawableFunction_GetsItsCalls_AndEachEnds() {
    var module = Module(Source);
    var calls = RustRandomCalls.For(module, RustRandomCalls.SliceSeed, 50, 2_000);
    Assert.That(calls.GroupBy(call => call.Method).Select(group => (group.Key, group.Count())), Is.EqualTo(new[] { ("add", 50), ("sum", 50), ("count", 50) }));
    Assert.That(calls.All(call => RustRandomCalls.Ends(module, call, 2_000)), Is.True);
    // A loop to a random bound: the large bounds were drawn again, the small ones kept.
    Assert.That(calls.Where(call => call.Method == "count").Select(call => (uint)call.Arguments[0]), Has.All.LessThan(2_000u));
    var lengths = calls.Where(call => call.Method == "sum").Select(call => ((uint[])call.Arguments[0]).Length).ToList();
    Assert.That(lengths, Has.All.InRange(0, RustRandomCalls.MaximumLength));
    Assert.That(lengths.Distinct().Count(), Is.GreaterThan(10));
  }

  [Test]
  public void Draw_CoversBoundariesSmallNumbersAndSpecialFloats() {
    var random = new Random(RustRandomCalls.FunctionSeed);
    var bytes = Enumerable.Range(0, 400).Select(_ => (byte)RustRandomCalls.Draw(IrType.U8, random)).ToList();
    Assert.That(bytes, Does.Contain((byte)0).And.Contain((byte)255).And.Contain((byte)254));
    var shorts = Enumerable.Range(0, 400).Select(_ => (short)RustRandomCalls.Draw(IrType.I16, random)).ToList();
    Assert.That(shorts, Does.Contain(short.MinValue).And.Contain((short)-1));
    var floats = Enumerable.Range(0, 400).Select(_ => (float)RustRandomCalls.Draw(IrType.F32, random)).ToList();
    Assert.That(floats.Any(float.IsNaN) && floats.Any(float.IsInfinity), Is.True);
  }

  /// <summary>A function that sizes an array by a 64-bit argument gets sizes; one that does not, any 64-bit value.</summary>
  [Test]
  public void ArraySizedByAnArgument_GetsSizes() {
    var module = Module("pub fn sized(n: usize, k: u32) -> usize { vec![1u8; n].len() + k as usize } pub fn fixed(n: u64) -> u64 { let a = [0u8; 4]; n ^ a.len() as u64 }");
    Assert.That(module.Functions.Select(RustRandomCalls.Allocates), Is.EqualTo(new[] { true, false }));
    var calls = RustRandomCalls.For(module, RustRandomCalls.SliceSeed, 200, 2_000);
    Assert.That(calls.Where(call => call.Method == "sized").Select(call => (ulong)call.Arguments[0]), Has.All.LessThanOrEqualTo((ulong)RustRandomCalls.MaximumSize));
    Assert.That(calls.Where(call => call.Method == "sized").Select(call => (uint)call.Arguments[1]), Has.Some.GreaterThan(1_000_000u));
    Assert.That(calls.Where(call => call.Method == "fixed").Select(call => (ulong)call.Arguments[0]), Has.Some.GreaterThan((ulong)uint.MaxValue));
  }

  [Test]
  public void TrappingCall_Ends() {
    var module = Module("pub fn f(a: u8) -> u8 { 200 / a }");
    Assert.That(RustRandomCalls.Ends(module, new PrototypeCall("f", [(byte)0]), 100), Is.True);
  }

  [Test]
  public void FunctionNoDrawEnds_IsReported()
    => Assert.That(() => RustRandomCalls.For(Module("pub fn f(a: u8) -> u8 { let mut x = a; loop { x = x.wrapping_add(1); } }"), RustRandomCalls.FunctionSeed, 2, 100),
      Throws.InvalidOperationException.With.Message.Contains("'f' did not end within 100 steps for 40"));

  [TestCase("pub fn f(s: &str) -> usize { s.len() }")]
  [TestCase("fn f(a: u8) -> u8 { a }")]
  [TestCase("pub fn f(a: (u8, u8)) -> u8 { a.0 }")]
  public void FunctionOutsideTheDrawableForms_IsLeftOut(string source)
    => Assert.That(RustRandomCalls.For(Module(source), RustRandomCalls.FunctionSeed, 5, 100), Is.Empty);

  [TestCase("memory", RustRandomCalls.SliceSeed)]
  [TestCase("types", RustRandomCalls.TypeSeed)]
  [TestCase("functions", RustRandomCalls.FunctionSeed)]
  [TestCase("closures", RustRandomCalls.FunctionSeed)]
  public void Fixture_HasTheSeedOfItsIssue(string fixture, int seed) => Assert.That(RustRandomCalls.SeedOf(fixture), Is.EqualTo(seed));

}
