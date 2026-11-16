using System.Collections.Generic;
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

  /// <summary>Every outcome of the calls of a module in the crate built with overflow checks on and off.</summary>
  private static async Task<IReadOnlyList<string>[]> BothModes(IrModule module, IReadOnlyList<PrototypeCall> calls, RustEmitOptions? options = null)
    => [await RustRun.OutcomesAsync(module, calls, OverflowChecks.Panic, options: options), await RustRun.OutcomesAsync(module, calls, OverflowChecks.Wrap, options: options)];

  /// <summary>The C# methods of the shapes added for #458, #459 and #461, built by the C# compiler: the .NET side of the differential.</summary>
  private static class Shapes {

    public const string Source = """
      public static uint NextPowerOfTwo(uint x) { uint p = 1; while (true) { if (p >= x) break; p <<= 1; } return p; }
      public static int Steps(int x) { int i = 0; while (true) { if (i > x) break; if (i == 7) return -1; i += 2; if (i * i > 50) break; } return i * 3; }
      public static int SumTo100() { int s = 0; for (int i = 0; i <= 100; i++) s += i; return s; }
      public static uint Sum(byte[] data) { uint s = 0; foreach (var b in data) s += b; return s; }
      public static byte Parity(byte[] data) { byte s = 0; foreach (var b in data) s ^= b; return s; }
      public static ulong Product(ulong[] data, ulong h) { ulong s = h; foreach (var v in data) s *= v; return s; }
      """;

    public static uint NextPowerOfTwo(uint x) { uint p = 1; while (true) { if (p >= x) break; p <<= 1; } return p; }
    public static int Steps(int x) { int i = 0; while (true) { if (i > x) break; if (i == 7) return -1; i += 2; if (i * i > 50) break; } return i * 3; }
    public static int SumTo100() { int s = 0; for (int i = 0; i <= 100; i++) s += i; return s; }
    public static uint Sum(byte[] data) { uint s = 0; foreach (var b in data) s += b; return s; }
    public static byte Parity(byte[] data) { byte s = 0; foreach (var b in data) s ^= b; return s; }
    public static ulong Product(ulong[] data, ulong h) { ulong s = h; foreach (var v in data) s *= v; return s; }

  }

  /// <summary>
  /// #458 and #459, the loop with a value, the inclusive counted loop and the reductions: what the
  /// C# compiler's build does, the interpreter and the crate in both build modes agree, the
  /// scenarios' hand-checked values among them (next_power_of_two of 100 and 1, 65536 bytes of 255).
  /// </summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task NewShapes_ComputeWhatDotNetComputes() {
    var module = RustCrates.Import(Shapes.Source);
    PrototypeCall[] calls = [
      new("NextPowerOfTwo", [100u]), new("NextPowerOfTwo", [1u]), new("NextPowerOfTwo", [0u]), new("NextPowerOfTwo", [0x80000000u]),
      new("Steps", [-1]), new("Steps", [3]), new("Steps", [100]), new("Steps", [6]),
      new("SumTo100", []),
      new("Sum", [Enumerable.Repeat((byte)255, 65536).ToArray()]), new("Sum", [System.Array.Empty<byte>()]), new("Sum", [new byte[] { 1 }]),
      new("Parity", [new byte[] { 1, 2, 4, 1 }]), new("Parity", [System.Array.Empty<byte>()]),
      new("Product", [new ulong[] { 3, ulong.MaxValue, 5 }, 7ul]), new("Product", [System.Array.Empty<ulong>(), 9ul])
    ];
    var dotnet = calls.Select(call => PrototypeFixture.InvokedOutcome(typeof(Shapes).GetMethod(call.Method)!, call)).ToList();
    Assert.That(dotnet.Take(2), Is.EqualTo(new[] { "128", "1" }));
    Assert.That(dotnet[9], Does.StartWith("16711680"));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(dotnet), "interpreter");
    foreach (var outcomes in await BothModes(module, calls))
      Assert.That(outcomes, Is.EqualTo(dotnet));
  }

  private const string RustRanges = """
    pub fn count_bytes() -> u32 {
        let mut c = 0u32;
        for _ in 0..=255u8 {
            c += 1;
        }
        c
    }

    pub fn tri(n: u8) -> u32 {
        let mut s = 0u32;
        for i in 0..=n {
            s += i as u32;
        }
        s
    }

    pub fn span(a: i8, b: i8) -> i32 {
        let mut s = 0i32;
        for i in a..=b {
            s += i as i32;
        }
        s
    }

    pub fn count_i8() -> u32 {
        let mut c = 0u32;
        for _ in -128i8..=127 {
            c += 1;
        }
        c
    }

    pub fn empty() -> u32 {
        let mut c = 0u32;
        for _ in 5u8..=3 {
            c += 1;
        }
        c
    }

    pub fn skip(n: u64) -> u64 {
        let mut s = 0u64;
        for i in 1..=n {
            if i % 3 == 0 {
                continue;
            }
            s += i;
        }
        s
    }
    """;

  /// <summary>
  /// #458, the scenario of the counted loop over an inclusive range ending at the type's maximum and
  /// test design 9: Rust's range comes back as Rust's range, which the crate runs as rustc runs the
  /// original - 256 iterations to 255u8 and over all of i8, none over 5..=3 - in both build modes.
  /// </summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task InclusiveRanges_RunAsRustcRunsThem() {
    var directory = System.IO.Directory.CreateTempSubdirectory("polypiler-rust-ranges-").FullName;
    try {
      var source = System.IO.Path.Combine(directory, "ranges.rs");
      await System.IO.File.WriteAllTextAsync(source, RustRanges);
      foreach (var mode in RustFixture.Modes) {
        var module = RustFixture.Import("ranges.rs", RustRanges, mode);
        PrototypeCall[] calls = [
          new("count_bytes", []), new("tri", [(byte)255]), new("tri", [(byte)0]), new("span", [(sbyte)-128, (sbyte)127]), new("span", [(sbyte)5, (sbyte)3]),
          new("count_i8", []), new("empty", []), new("skip", [10ul]), new("skip", [0ul])
        ];
        var rustc = RustOracle.Outcomes(System.IO.Path.Combine(directory, RustFixture.NameOf(mode)), source, module, calls, mode);
        Assert.That(rustc.Take(7), Is.EqualTo(new[] { "256", "32640", "0", "-128", "0", "256", "0" }));
        foreach (var outcomes in await BothModes(module, calls))
          Assert.That(outcomes.Select(RustFixture.Normalized), Is.EqualTo(rustc), $"imported in mode {mode}");
        Assert.That(RustCrates.ModuleText(RustCrates.Emit(module)), Does.Contain("for _ in 0..=255u8 {"));
      }
    } finally {
      System.IO.Directory.Delete(directory, true);
    }
  }

  /// <summary>#461: the wrapping conversion from floating point, the scenario's outline and its edges, against .NET's (the IR library's) ToInt32.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task WrappingFloatToInteger_ComputesWhatDotNetComputes() {
    var x = new IrLocal("x", IrType.F64);
    var module = new IrModule("wrap", [
      new IrFunction("ToInt32", true, IrType.I32, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [new IrLocalRef(x)]))])),
      new IrFunction("ToUint32", true, IrType.U32, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [new IrLocalRef(x)]))]))
    ]);
    double[] values = [4294967301.0, 2147483648.0, -2147483649.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.5, 1e300, -4294967296.5, 3.99, 9007199254740993.0];
    var calls = values.SelectMany(value => new[] { new PrototypeCall("ToInt32", [value]), new PrototypeCall("ToUint32", [value]) }).ToList();
    var dotnet = values.SelectMany(value => new[] { HelperOperations.ToInt32(value).ToString(System.Globalization.CultureInfo.InvariantCulture), HelperOperations.ToUint32(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();
    Assert.That(dotnet.Take(6), Is.EqualTo(new[] { "5", "5", "-2147483648", "2147483648", "2147483647", "2147483647" }));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(dotnet), "interpreter");
    foreach (var outcomes in await BothModes(module, calls))
      Assert.That(outcomes, Is.EqualTo(dotnet));
  }

  /// <summary>#461, the scenario's saturating addition of u8 in both build modes, against .NET's clamped sum.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task SaturatingAddition_ComputesWhatDotNetComputes() {
    var a = new IrLocal("a", IrType.U8);
    var b = new IrLocal("b", IrType.U8);
    var module = new IrModule("saturate", [new IrFunction("F", true, IrType.U8, [a, b], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.AddSaturate, IrType.U8, [new IrLocalRef(a), new IrLocalRef(b)]))]))]);
    (byte A, byte B)[] pairs = [(250, 10), (250, 5), (255, 0), (0, 0), (128, 127), (128, 128)];
    var calls = pairs.Select(pair => new PrototypeCall("F", [pair.A, pair.B])).ToList();
    var dotnet = pairs.Select(pair => System.Math.Min(pair.A + pair.B, 255).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    Assert.That(dotnet[0], Is.EqualTo("255"));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(dotnet), "interpreter");
    foreach (var outcomes in await BothModes(module, calls))
      Assert.That(outcomes, Is.EqualTo(dotnet));
  }

  private const string Layered = """
    public static uint Rotl(uint x, int n) => System.Numerics.BitOperations.RotateLeft(x, n);
    public static uint Prime() => 16777619u;
    public static uint Fnv1a(byte[] data) { uint h = 2166136261u; foreach (var b in data) { h ^= b; h *= Prime(); } return h; }
    public static uint Mixed(uint h) => Rotl(h, 5) ^ h;
    """;

  /// <summary>
  /// #459, the scenario of the crate of the modules util and hash: it builds, lints and is formatted,
  /// its exports are called by their paths in the modules, fnv1a("foobar") is the published check
  /// value, and the crate is the same when emitted twice.
  /// </summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task CrateOfSeveralModules_BuildsAndComputesTheCheckValues() {
    var options = new RustEmitOptions { Modules = new Dictionary<string, string> { ["Rotl"] = "util", ["Prime"] = "util", ["Fnv1a"] = "hash", ["Mixed"] = "hash" } };
    var module = RustCrates.Import(Layered);
    PrototypeCall[] calls = [new("Fnv1a", ["foobar"u8.ToArray()]), new("Fnv1a", ["a"u8.ToArray()]), new("Mixed", [0x80000001u]), new("Rotl", [1u, 33])];
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(expected.Take(2).Select(outcome => outcome.Split(' ')[0]), Is.EqualTo(new[] { "3214735720", "3826002220" }));
    foreach (var outcomes in await BothModes(module, calls, options))
      Assert.That(outcomes, Is.EqualTo(expected));

    var files = RustCrates.Emit(module, options: options);
    Assert.That(RustCrates.Text(files, "src/hash.rs"), Does.Contain("use crate::util::rotl;"));
    Assert.That(files.Select(file => file.Content), Is.EqualTo(RustCrates.Emit(module, options: options).Select(file => file.Content)));
    var runner = RustRun.Runner(OverflowChecks.Panic);
    Assert.That(await runner.ClippyAsync(RustCrates.Package(files)), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(RustCrates.Package(files)), Is.Empty);
  }

  /// <summary>The crates of the new shapes build without a warning under clippy and are formatted, in both import modes of the Rust ranges.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task NewShapeCrates_AreLintAndFormatClean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var x = new IrLocal("x", IrType.F64);
    var wrapping = new IrModule("wrap", [new IrFunction("F", true, IrType.I32, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [new IrLocalRef(x)]))]))]);
    IrModule[] modules = [RustCrates.Import(Shapes.Source), wrapping, .. RustFixture.Modes.Select(mode => RustFixture.Import("ranges.rs", RustRanges, mode))];
    foreach (var module in modules) {
      var package = RustCrates.Package(RustCrates.Emit(module));
      Assert.That(await runner.ClippyAsync(package), Is.Empty, module.Name);
      Assert.That(await runner.FormatCheckAsync(package), Is.Empty, module.Name);
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
