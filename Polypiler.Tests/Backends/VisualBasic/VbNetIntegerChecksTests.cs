using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// The Visual Basic .NET backend with integer checks on (<c>RemoveIntegerChecks</c> false): the
/// wrapping arithmetic, negation, logical shift, conversions and steps of C# written out through
/// helpers, the checked ones as the native operators, each compiled with checks on and called at the
/// boundaries of its type against the IR interpreter.
/// </summary>
[TestFixture]
public sealed class VbNetIntegerChecksTests {

  private static readonly VisualBasicProfile Checked = VisualBasicProfile.VbNetLatest with { RemoveIntegerChecks = false };

  private static void Agrees(string method, params object[][] calls) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public " + method));
    var translation = VbNetFixtures.Translate(module, Checked);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    Assert.That(translation.Source, Does.Contain("compiled with integer checks on"));
    Assert.That(translation.Source, method.Contains(" checked(", System.StringComparison.Ordinal) ? Does.Not.Contain("PlyAddChk") : Does.Contain("Wrap"));
    var procedure = VbNetFixtures.Procedure(VbNetFixtures.Load(translation.Source!, Checked, removeIntegerChecks: false), "F");
    foreach (var arguments in calls) {
      var call = new PrototypeCall("F", arguments);
      Assert.That(PrototypeFixture.InvokedOutcome(procedure, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), $"{call}\n{translation.Source}");
    }
  }

  [Test]
  public void Int32_Wraps() => Agrees("static int F(int a, int b) { return unchecked(a + b) ^ unchecked(a - b) * 3 ^ unchecked(a * b) ^ unchecked(-a) ^ (a >>> 3) ^ (a << 31); }",
    [int.MaxValue, 1], [int.MinValue, -1], [int.MinValue, int.MinValue], [0, 0], [-7, 5], [123456789, 987654321]);

  [Test]
  public void UInt32_Wraps() => Agrees("static uint F(uint a, uint b) { return unchecked(a + b) ^ unchecked(a - b) ^ unchecked(a * b) ^ unchecked(0u - a) ^ (a >> 31); }",
    [uint.MaxValue, 1u], [0u, 1u], [0x80000000u, 0x80000000u], [12345u, 4000000000u]);

  [Test]
  public void Int64_Wraps() => Agrees("static long F(long a, long b) { return unchecked(a + b) ^ unchecked(a - b) ^ unchecked(a * b) ^ unchecked(-a) ^ (a >>> 7) ^ (a << 63); }",
    [long.MaxValue, 1L], [long.MinValue, -1L], [long.MinValue, long.MinValue], [0L, 0L], [-3L, 0x7FFFFFFFFFFFL], [unchecked((long)0x9E3779B97F4A7C15UL), unchecked((long)0xBF58476D1CE4E5B9UL)]);

  [Test]
  public void UInt64_Wraps() => Agrees("static ulong F(ulong a, ulong b) { return unchecked(a + b) ^ unchecked(a - b) ^ unchecked(a * b) ^ unchecked(0ul - a) ^ (a >> 1); }",
    [ulong.MaxValue, 1UL], [0UL, 1UL], [0x8000000000000000UL, 3UL], [0x9E3779B97F4A7C15UL, 0x94D049BB133111EBUL]);

  [Test]
  public void SplitMix64_GivesItsPublishedFirstOutput() => Agrees(
    "static ulong F(ulong seed) { ulong z = unchecked(seed + 0x9E3779B97F4A7C15UL); z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL); z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL); return z ^ (z >> 31); }",
    [131UL], [0UL], [ulong.MaxValue]);

  [Test]
  public void Conversions_KeepTheLowBits() => Agrees(
    "static long F(long a, int b) { unchecked { return (sbyte)a + (byte)a + (short)a + (ushort)a + (int)a + (uint)a + (long)(ulong)a + (long)(uint)b + (long)(ulong)b + (byte)b; } }",
    [-1L, -1], [long.MinValue, int.MinValue], [0x123456789ABCDEFL, 0x7FFFFFFF], [255L, 128]);

  [Test]
  public void Steps_Wrap() => Agrees(
    "static int F(int a, byte b) { int x = a; x++; long y = a; y--; byte z = b; z++; sbyte w = (sbyte)b; w--; uint u = (uint)a; u++; ulong v = (ulong)a; v--; return x ^ (int)y ^ z ^ w ^ (int)u ^ (int)v; }",
    [int.MaxValue, (byte)255], [int.MinValue, (byte)0], [0, (byte)128]);

  [Test]
  public void CheckedArithmetic_ThrowsAsTheOperatorDoes() => Agrees("static long F(int a, long b) { return checked(a + 1) + checked(b * 2) + checked(-b); }",
    [int.MaxValue, 0L], [0, long.MaxValue], [0, long.MinValue], [3, 4L]);

  [Test]
  public void ProjectFile_StatesTheChecks() {
    Assert.That(VisualBasicEmitter.ProjectFile(Checked), Does.Contain("<RemoveIntegerChecks>false</RemoveIntegerChecks>"));
    Assert.That(VisualBasicEmitter.ProjectFile(VisualBasicProfile.VbNetLatest), Does.Contain("<RemoveIntegerChecks>true</RemoveIntegerChecks>"));
    Assert.That(Checked.Name, Is.EqualTo(VisualBasicProfile.VbNetLatest.Name));
  }

  [TestCase("vbnet/vb9")]
  [TestCase("vbnet/vb16.9/net2.0")]
  [TestCase("vbnet/vb16.9/netstandard2.0")]
  public void EveryHelper_CompilesWithChecksOn(string name) {
    var profile = VisualBasicProfile.Parse(name) with { RemoveIntegerChecks = false };
    var translation = VbNetFixtures.Snippet("public static long F(int a, uint b, long c, ulong d) { unchecked { return a * a + (long)(b * b - b) + c * c - (long)(d * d + d) + (-a) + (-c) + (a >>> 1) + (c >>> 1) + (short)c + (byte)d; } }", profile);
    Assert.That(translation.Problems, Is.Empty);
    Assert.That(VbNetFixtures.Complaints(translation.Source!, profile, removeIntegerChecks: false), Is.Empty, translation.Source);
  }

}
