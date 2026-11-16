using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The scenarios of the Rust backend's issues on the pinned toolchain: scalar contracts (#461),
/// control flow (#458) and the package with its generated tests (#459). Each function comes from C#
/// through the frontend; the expected values are the issues' hand-checked ones, which the interpreter
/// gives as well, and the crate must give them with overflow checks on and off.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustBackendScenarioTests {

  private static async Task Expect(string methods, string method, object[] arguments, string expected) {
    var module = RustCrates.Import(methods);
    var call = new PrototypeCall(method, arguments);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(expected), "interpreter");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That((await RustRun.OutcomesAsync(module, [call], checks)).Single(), Is.EqualTo(expected), $"Rust with overflow checks {checks}");
  }

  private static string Trap(IrTrap trap) => PrototypeFixture.Trap(trap);

  /// <summary>#461, the overflow contract outline and the shift of a signed value through its unsigned type.</summary>
  [TestCase("public static int F(int a, int b) => a + b;", new object[] { int.MaxValue, 1 }, "-2147483648")]
  [TestCase("public static uint F(uint a, uint b) => a * b;", new object[] { 65536u, 65536u }, "0")]
  [TestCase("public static uint F(uint a, int b) => a << b;", new object[] { 1u, 33 }, "2")]
  [TestCase("public static uint F(uint a, int b) => System.Numerics.BitOperations.RotateLeft(a, b);", new object[] { 0x12345678u, 8 }, "878082066")]
  [TestCase("public static int F(int a, int n) => a >>> n;", new object[] { -8, 1 }, "2147483644")]
  [TestCase("public static uint F(uint h, byte b) => (h ^ b) * 16777619u;", new object[] { 2166136261u, (byte)97 }, "3826002220")]
  [TestCase("public static int F(int a, int b) => checked(a + b);", new object[] { 1, 2 }, "3")]
  [TestCase("public static uint F(uint a) => (uint)((int)a >> 31);", new object[] { 0x80000000u }, "4294967295")]
  [TestCase("public static bool F(int a, int b) => (uint)a < (uint)b;", new object[] { -1, 1 }, "false")]
  [TestCase("public static int F(sbyte a) => (byte)a;", new object[] { (sbyte)-1 }, "255")]
  [TestCase("public static byte F(int a) => (byte)a;", new object[] { 300 }, "44")]
  [TestCase("public static float F(int a) => a;", new object[] { 16777217 }, "16777216")]
  [TestCase("public static int F(ushort a) => System.Numerics.BitOperations.PopCount(a);", new object[] { (ushort)0xF0F0 }, "8")]
  [TestCase("public static int F(uint a) => System.Numerics.BitOperations.TrailingZeroCount(a);", new object[] { 0u }, "32")]
  [TestCase("public static uint F(uint a) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(a);", new object[] { 0x12345678u }, "2018915346")]
  [TestCase("public static long F() => long.MinValue;", new object[0], "-9223372036854775808")]
  [TestCase("public static ulong F() => ulong.MaxValue;", new object[0], "18446744073709551615")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task Scalar_ComputesTheSameInBothBuildModes(string methods, object[] arguments, string expected) => Expect(methods, "F", arguments, expected);

  /// <summary>#461: the trapping operations panic with the trap's name in both build modes; the division traps.</summary>
  [TestCase("public static int F(int a, int b) => checked(a + b);", new object[] { int.MaxValue, 1 }, IrTrap.IntegerOverflow)]
  [TestCase("public static int F(int a, int b) => a / b;", new object[] { 1, 0 }, IrTrap.DivideByZero)]
  [TestCase("public static int F(int a, int b) => a / b;", new object[] { int.MinValue, -1 }, IrTrap.IntegerOverflow)]
  [TestCase("public static int F(int a, int b) => a % b;", new object[] { int.MinValue, -1 }, IrTrap.IntegerOverflow)]
  [TestCase("public static byte F(int a) => checked((byte)a);", new object[] { 256 }, IrTrap.IntegerOverflow)]
  [TestCase("public static int F(int a) => checked(-a);", new object[] { int.MinValue }, IrTrap.IntegerOverflow)]
  [TestCase("public static int F(int a) => System.Math.Abs(a);", new object[] { int.MinValue }, IrTrap.IntegerOverflow)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task Trap_IsTheSameInBothBuildModes(string methods, object[] arguments, IrTrap trap) => Expect(methods, "F", arguments, Trap(trap));

  /// <summary>#461, the float-to-integer outline and test design 9.</summary>
  [TestCase("public static int F(double x) => (int)x;", 3000000000.0, "2147483647")]
  [TestCase("public static int F(double x) => (int)x;", double.NaN, "0")]
  [TestCase("public static int F(double x) => checked((int)x);", 2147483647.5, "2147483647")]
  [TestCase("public static int F(double x) => checked((int)x);", -2147483648.9, "-2147483648")]
  [TestCase("public static ulong F(double x) => checked((ulong)x);", -0.5, "0")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task FloatToInteger_KeepsItsContract(string methods, double x, string expected) => Expect(methods, "F", [x], expected);

  [TestCase(2147483648.0)]
  [TestCase(-2147483649.0)]
  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(double.NegativeInfinity)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task TrappingFloatToInteger_TrapsOutsideTheRange(double x) => Expect("public static int F(double x) => checked((int)x);", "F", [x], Trap(IrTrap.IntegerOverflow));

  /// <summary>#461, test design 10: float constants come back bit for bit.</summary>
  [TestCase(0.1)]
  [TestCase(-0.0)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(5e-324)]
  [TestCase(1.7976931348623157e308)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task FloatConstant_IsItsBits(double value)
    => Expect($"public static long F() => System.BitConverter.DoubleToInt64Bits({value.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace("Infinity", "1.0/0.0")}{(double.IsInfinity(value) ? "" : "d")});",
      "F", [], System.BitConverter.DoubleToInt64Bits(value).ToString(System.Globalization.CultureInfo.InvariantCulture));

  private const string Control = """
    public static uint Gcd(uint a, uint b) { while (b != 0) { uint t = a % b; a = b; b = t; } return a; }
    public static int CollatzSteps(long n) { int steps = 0; while (n != 1) { n = n % 2 == 0 ? n / 2 : 3 * n + 1; steps++; } return steps; }
    public static int SumTo(int n) { int s = 0; for (int i = 0; i <= n; i++) s += i; return s; }
    public static int Sign(int x) { if (x < 0) return -1; else if (x == 0) return 0; else return 1; }
    public static int Classify(int x) { switch (x) { case 0: return 1; case 1: case 2: return 2; case 10: case 11: case 12: case 13: case 14: case 15: case 16: case 17: case 18: case 19: return 3; default: return 4; } }
    public static int Countdown(int n) { int r = 0; do { r = r * 10 + n; n--; } while (n > 0); return r; }
    public static int Grade(int x) { int g = 0; for (int i = 0; i < 3; i++) { if (x > 10 - 5 * i) continue; g++; } return g; }
    public static uint NextPowerOfTwo(uint x) { uint p = 1; while (true) { if (p >= x) break; p <<= 1; } return p; }
    """;

  /// <summary>#458: the control-flow fixtures with their hand-checked values.</summary>
  [TestCase("Gcd", new object[] { 48u, 18u }, "6")]
  [TestCase("Gcd", new object[] { 7u, 0u }, "7")]
  [TestCase("Gcd", new object[] { 0u, 7u }, "7")]
  [TestCase("CollatzSteps", new object[] { 27L }, "111")]
  [TestCase("SumTo", new object[] { 100 }, "5050")]
  [TestCase("SumTo", new object[] { 0 }, "0")]
  [TestCase("SumTo", new object[] { 1 }, "1")]
  [TestCase("Sign", new object[] { -5 }, "-1")]
  [TestCase("Sign", new object[] { 0 }, "0")]
  [TestCase("Sign", new object[] { 7 }, "1")]
  [TestCase("Classify", new object[] { 0 }, "1")]
  [TestCase("Classify", new object[] { 2 }, "2")]
  [TestCase("Classify", new object[] { 10 }, "3")]
  [TestCase("Classify", new object[] { 19 }, "3")]
  [TestCase("Classify", new object[] { 20 }, "4")]
  [TestCase("Classify", new object[] { -1 }, "4")]
  [TestCase("Countdown", new object[] { 3 }, "321")]
  [TestCase("Grade", new object[] { 11 }, "0")]
  [TestCase("Grade", new object[] { 6 }, "1")]
  [TestCase("Grade", new object[] { 1 }, "2")]
  [TestCase("NextPowerOfTwo", new object[] { 100u }, "128")]
  [TestCase("NextPowerOfTwo", new object[] { 1u }, "1")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task ControlFlow_GivesTheHandCheckedValues(string method, object[] arguments, string expected) => Expect(Control, method, arguments, expected);

  /// <summary>Every crate of the scenarios builds without a warning under clippy and is formatted.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ScenarioCrates_AreLintAndFormatClean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    foreach (var methods in new[] { Control, "public static uint F(uint h, byte b) => (h ^ b) * 16777619u; public static int G(double x) => checked((int)x); public static double H(double a) => System.Math.Round(a);" }) {
      var package = RustCrates.Package(RustCrates.Emit(RustCrates.Import(methods)));
      Assert.That(await runner.ClippyAsync(package), Is.Empty);
      Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    }
  }

  /// <summary>#459: the generated unit tests pass, and one with a wrong expectation fails, naming both values.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task GeneratedTests_ReallyTest() {
    const string methods = "public static uint Fnv1aStep(uint h, byte b) => (h ^ b) * 16777619u; public static int Quotient(int a, int b) => a / b;";
    RustEmitOptions Options(uint expected) => new() {
      PackageName = "checksum",
      Tests = [
        new RustTestCase("fnv1a_of_a", "Fnv1aStep", [new IrConst(IrType.U32, 2166136261u), new IrConst(IrType.U8, 97)], new IrConst(IrType.U32, expected)),
        new RustTestCase("rejects_zero", "Quotient", [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0)], null, IrTrap.DivideByZero)
      ]
    };
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(RustCrates.Emit(RustCrates.Import(methods), options: Options(0xE40C292Cu)));
    var report = await runner.RunTestsAsync(package);
    Assert.That((report.Passed, report.Failed), Is.EqualTo((2, 0)));
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);

    var mutated = await runner.RunTestsAsync(RustCrates.Package(RustCrates.Emit(RustCrates.Import(methods), options: Options(0xE40C292Du))));
    Assert.That((mutated.Passed, mutated.Failed), Is.EqualTo((1, 1)));
    Assert.That(mutated.Output, Does.Contain("3826002220").And.Contain("3826002221"));
  }

}
