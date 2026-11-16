using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// O0062 end to end on the hashes and generators of <c>tests/fixtures/optimize/o0062</c>: CPython 3.14
/// runs the original source, and the interpreter, the emitted Python and the C# compiler's build of the
/// emitted C# - all after the specialization - have its outcome on every call; the C# of a function the
/// pass narrowed computes without <c>BigInteger</c>; and the command line runs the pass at every level
/// and without it leaves the arbitrary-precision integer to the target.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PyIntSpecializationTests {

  private static readonly string Directory = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "o0062");

  private static string Source(string name) => File.ReadAllText(Path.Combine(Directory, name)).Replace("\r\n", "\n", StringComparison.Ordinal);

  /// <summary>The functions the pass narrows to native arithmetic, and those it must leave exact.</summary>
  private static readonly string[] Narrowed = ["fnv1a64", "fnv1a32", "splitmix64", "xorshift64", "xorshift32_sum", "checksum"];

  private static readonly BigInteger[] Operands = [
    0, 1, -1, 255, 4294967295, 0x0123456789ABCDEF, long.MinValue, BigInteger.One << 64, (BigInteger.One << 70) + 5, -(BigInteger.One << 65) + 3
  ];

  private static List<PrototypeCall> Calls() {
    var calls = new List<PrototypeCall>();
    foreach (var name in new[] { "fnv1a64", "fnv1a32", "splitmix64", "xorshift64", "xorshift32_sum", "shifted_low" })
      calls.AddRange(Operands.Select(operand => new PrototypeCall(name, [operand])));

    calls.Add(new("checksum", []));
    foreach (var (a, b) in new (BigInteger, BigInteger)[] { (BigInteger.One << 40, 2), (BigInteger.One << 40, 3), (7, -2), (-7, 2), (5, 0), (BigInteger.One << 80, 7) }) {
      calls.Add(new("unmasked_product", [a, b]));
      calls.Add(new("quotient_low", [a, b]));
    }

    calls.AddRange(new BigInteger[] { 0, 63, 64, 100 }.Select(n => new PrototypeCall("power", [n])));
    return calls;
  }

  private static IrModule Specialized() {
    var imported = PythonFrontend.Import("hashes.py", Source("hashes.py"));
    Assert.That(imported.Diagnostics, Is.Empty);
    var module = PyIntSpecialization.Run(imported.Module!).Module;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
  }

  [Test, RequiresToolchain("python")]
  public async Task EveryImplementation_AgreesWithCPythonOnTheOriginal() {
    var calls = Calls();
    var expected = await PythonDriver.OutcomesAsync(Source("hashes.py"), calls);
    var module = Specialized();
    var translated = PythonFixtures.Translate(module, PythonProfile.Parse("py3.14"));
    Assert.That(translated.Problems, Is.Empty);
    var python = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    var build = GeneratedCSharp.Compile(text, true);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var interpreted = Interpreted(module, calls[index]);
      var compiled = Invoked(build.Method(calls[index].Method), calls[index].Arguments);
      if (interpreted != expected[index] || python[index] != expected[index] || compiled != expected[index])
        mismatches.Add($"{calls[index]}: CPython {expected[index]}, interpreter {interpreted}, python {python[index]}, C# {compiled}");
    }

    TestContext.Out.WriteLine($"o0062: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches, Is.Empty, text);
  }

  /// <summary>Where O0062 fired the C# computes natively: the one BigInteger of a narrowed function is its parameter, cut to its low bits once.</summary>
  [Test, RequiresToolchain("python")]
  public void NarrowedFunctions_ComputeWithoutBigInteger() {
    var text = LegalizedCSharp.Emit(Specialized(), CSharpProfile.Default)[0].Content;
    foreach (var name in Narrowed) {
      var body = Regex.Match(text, $@"public static \w+ {name}\(.*?\n  \}}\n", RegexOptions.Singleline).Value;
      Assert.That(body, Is.Not.Empty, name);
      var big = body.Split('\n').Where(line => line.Contains("BigInteger", StringComparison.Ordinal)).ToList();
      Assert.That(big.Skip(1).All(line => Regex.IsMatch(line, @"\((ulong|uint)\)\(\w+ & (ulong|uint)\.MaxValue\)")), Is.True, body);
      Assert.That(big.Count, Is.LessThanOrEqualTo(2), body);
    }
  }

  /// <summary>Test design 18 and the scenario of a target without big integers: required at --opt none, PLY1053 without it.</summary>
  [Test, RequiresToolchain("python")]
  public void CommandLine_RunsThePassAtEveryLevel_AndWithoutItTheTargetRefuses() {
    var directory = System.IO.Directory.CreateTempSubdirectory("polypiler-o0062-").FullName;
    try {
      var file = Path.Combine(Directory, "checksum.py");
      var (narrowed, output) = Toolchain.RunPolypiler(directory, "translate", file, "--from", "python", "--to", "csharp", "--profile", "net3.5", "--opt", "none", "--explain-opt", "--output", "narrowed");
      Assert.That(narrowed, Is.Zero, output);
      Assert.That(output, Does.Contain("opt O0062 applied: & 0xFFFF -> u32 [wrap] (mask) [proof: range-facts]: 'checksum'"));
      Assert.That(File.ReadAllText(Path.Combine(directory, "narrowed", "checksum.cs")), Does.Not.Contain("BigInteger"));

      var (exact, refusal) = Toolchain.RunPolypiler(directory, "translate", file, "--from", "python", "--to", "csharp", "--profile", "net3.5", "--disable-opt", "O0062", "--output", "exact");
      Assert.That(exact, Is.EqualTo(3), refusal);
      Assert.That(refusal, Does.Contain("PLY1053").And.Contain("'System.Numerics.BigInteger' (the arbitrary-precision integer, a Python int no fixed width holds) cannot be expressed in C# profile 'net3.5'"));
      Assert.That(System.IO.Directory.Exists(Path.Combine(directory, "exact")) && System.IO.Directory.EnumerateFiles(Path.Combine(directory, "exact")).Any(), Is.False);
    } finally {
      System.IO.Directory.Delete(directory, true);
    }
  }

  /// <summary>What the interpreter does for a call; a raise of the import by the trap of its class, as the driver reads Python's exceptions.</summary>
  private static string Interpreted(IrModule module, PrototypeCall call) {
    try {
      return PrototypeFixture.Outcome(IrInterpreter.Call(module, call.Method, call.Arguments), call.Arguments);
    } catch (IrException exception) {
      return PrototypeFixture.Trap(exception.Class.Name == "DivideByZeroException" ? IrTrap.DivideByZero : IrTrap.IntegerOverflow);
    }
  }

  private static string Invoked(MethodInfo method, object[] arguments) {
    try {
      return PrototypeFixture.Outcome(method.Invoke(null, arguments), arguments);
    } catch (TargetInvocationException exception) when (exception.InnerException is { } inner) {
      return inner switch {
        DivideByZeroException => PrototypeFixture.Trap(IrTrap.DivideByZero),
        OverflowException => PrototypeFixture.Trap(IrTrap.IntegerOverflow),
        _ => "raise " + inner.GetType().Name
      };
    }
  }

}
