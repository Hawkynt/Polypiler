using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using Polypiler.Tests.BigInt;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The arbitrary-precision integer written by the Rust backend (#1390), built by the pinned toolchain
/// with overflow checks on and off: every operation and conversion of <see cref="BigIntCases"/> has
/// the interpreter's outcome, whose arithmetic is .NET's <see cref="BigInteger"/>; every function of
/// the Python fixtures the backend writes has the outcome CPython recorded for the original; the
/// crates pass <c>cargo fmt --check</c> and clippy with <c>-D warnings</c>, also at the oldest MSRV
/// a profile promises (Rust 1.31). A driver of its own calls the exports, since the image of a call
/// has no arbitrary-precision integer: it reads one call per line, the integers in decimal, a
/// floating-point number by its bits in hex, and builds a <c>PlyBigInt</c> from its public fields.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class RustBigIntTests {

  private const string DriverPath = "src/bin/big_driver.rs";

  // Test design 1 and 5: every operation of the IR on the operands of BigIntCases - zero, the signs,
  // 2^63 and 2^64 on both sides of each, numbers far beyond - against .NET's BigInteger, in both build modes.
  [TestCase(OverflowChecks.Panic), TestCase(OverflowChecks.Wrap)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task EveryOperation_ComputesWhatDotNetComputes(OverflowChecks checks) {
    var module = BigIntCases.Module();
    var calls = BigIntCases.Calls().ToList();
    var outcomes = await OutcomesAsync(module, [.. calls.Select(call => (call.Function, call.Arguments))], checks, Interpreted);
    var mismatches = calls.Select((call, index) => (call, Rust: outcomes[index], Expected: BigIntCases.Interpreted(module, call.Function, call.Arguments)))
      .Where(row => row.Rust != row.Expected)
      .Select(row => $"{row.call.Function}({string.Join(", ", row.call.Arguments.Select(BigIntCases.Show))}): .NET {row.Expected}, Rust {row.Rust}")
      .ToList();
    TestContext.Out.WriteLine($"overflow checks {checks}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  // Test design 6: the crate is formatted as rustfmt formats it and clean under clippy, in the oldest and the newest profile.
  [TestCase("rust2018"), TestCase("rust2024")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Crate_IsFormattedAndClippyClean(string profile) {
    var files = RustCrates.Package(RustCrates.Emit(BigIntModule(), RustProfile.All.Single(candidate => candidate.Name == profile)));
    var runner = RustRun.Runner(OverflowChecks.Panic);
    Assert.That(await runner.FormatCheckAsync(files), Is.Empty);
    Assert.That((await runner.ClippyAsync(files)).Select(message => message.ToString()), Is.Empty);
  }

  /// <summary>
  /// The helpers name nothing of the standard library newer than Rust 1.31, the MSRV of <c>rust2018</c>:
  /// clippy's <c>incompatible_msrv</c> checks every item used against the version <c>clippy.toml</c> names.
  /// </summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Helpers_UseNothingNewerThanRust131() {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(RustCrates.Package(RustCrates.Emit(BigIntModule(), RustProfile.Rust2018)), StringComparer.Ordinal) {
      ["clippy.toml"] = Encoding.UTF8.GetBytes("msrv = \"1.31.0\"\n")
    };
    Assert.That((await RustRun.Runner(OverflowChecks.Panic).ClippyAsync(files)).Select(message => message.ToString()), Is.Empty);
  }

  private static IEnumerable<TestCaseData> PythonFixtures()
    => PythonFixture.Names.SelectMany(fixture => new[] { OverflowChecks.Panic, OverflowChecks.Wrap }.Select(checks => new TestCaseData(fixture, checks)));

  /// <summary>
  /// Every public function of a Python fixture the backend writes - the others are refused for what
  /// they hold besides <c>int</c>, which the round trip of their own issues covers - called on the
  /// fixture's cases: each outcome is what CPython recorded for the original.
  /// </summary>
  [TestCaseSource(nameof(PythonFixtures))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task PythonFixture_DoesWhatCPythonDid(string fixture, OverflowChecks checks) {
    var module = PythonFixture.Import(fixture);
    var cases = PythonCases.For(fixture, module);
    var recorded = PythonFixture.RecordedLines(fixture);
    Assert.That(recorded, Has.Length.EqualTo(cases.Count));
    var written = module.Functions.Where(function => function.IsPublic && Writable(function) && RustCrates.Check(Restricted(module, [function.Name])).Count == 0)
      .Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    var selected = cases.Select((call, index) => (call, index)).Where(pair => written.Contains(pair.call.Function)).ToList();
    TestContext.Out.WriteLine($"{fixture}: {written.Count} of {module.Functions.Count(function => function.IsPublic)} functions written, {selected.Count} of {cases.Count} calls");
    if (selected.Count == 0)
      Assert.Inconclusive($"the Rust backend writes no function of {fixture} yet");

    var outcomes = await OutcomesAsync(Restricted(module, written), [.. selected.Select(pair => (pair.call.Function, pair.call.Arguments))], checks, Python);
    var mismatches = selected.Select((pair, position) => (pair.call, Rust: outcomes[position], CPython: PythonOutcome.Expected(recorded[pair.index][(recorded[pair.index].IndexOf(" => ", StringComparison.Ordinal) + 4)..])))
      .Where(row => row.Rust != row.CPython)
      .Select(row => $"{row.call}: CPython {row.CPython}, Rust {row.Rust}")
      .ToList();
    TestContext.Out.WriteLine($"overflow checks {checks}: {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {selected.Count} calls differ");
  }

  /// <summary>Scenario: Python's <c>2**100 + 1</c> through the Rust backend is the integer CPython gives, 1267650600228229401496703205377.</summary>
  [TestCase(OverflowChecks.Panic), TestCase(OverflowChecks.Wrap)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task PowerOfTwoPlusOne_IsCPythonsValue(OverflowChecks checks) {
    var module = PythonFixture.Module(PythonFixture.Import("big.py", PowerSource));
    var outcomes = await OutcomesAsync(module, [("big", [1L]), ("big", [-1L]), ("big", [long.MinValue])], checks, Python);
    Assert.That(outcomes, Is.EqualTo(new[] { "int 1267650600228229401496703205377", "int 1267650600228229401496703205375", "int 1267650600219006029459848429568" }));
  }

  /// <summary>The source of the scenario, whose tree is recorded.</summary>
  public const string PowerSource = "def big(n: int) -> int:\n    return 2 ** 100 + n\n";

  /// <summary>The module of <see cref="BigIntCases"/> with a constant beyond <c>i128</c>, so that every helper is in the crate.</summary>
  private static IrModule BigIntModule() {
    var module = BigIntCases.Module();
    var huge = new IrFunction("Huge", true, IrType.BigInt, [], [], new IrBlock([new IrReturn(new IrBigConst(BigInteger.Pow(2, 1100) + 1))]));
    return module with { Functions = [.. module.Functions, huge] };
  }

  /// <summary>Whether the driver passes every parameter and the result of a function: integers of any width, floating-point numbers, truth values.</summary>
  private static bool Writable(IrFunction function)
    => function.Parameters.All(parameter => Scalar(parameter.Type)) && (function.ReturnType == IrType.Void || Scalar(function.ReturnType));

  private static bool Scalar(IrType type) => type.IsBigInt || type.IsInteger && !type.IsEnum || type.IsFloat || type == IrType.Bool;

  /// <summary>A module with the public functions given and every function they reach, the other public ones left out.</summary>
  private static IrModule Restricted(IrModule module, IReadOnlyCollection<string> exports) {
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var kept = new HashSet<string>(StringComparer.Ordinal);
    var pending = new Stack<string>(exports);
    while (pending.Count > 0) {
      var name = pending.Pop();
      if (!kept.Add(name))
        continue;

      foreach (var node in IrTree.Nodes(functions[name].Body))
        if (node is IrCall call && functions.ContainsKey(call.Function))
          pending.Push(call.Function);
        else if (node is IrFunctionRef reference && functions.ContainsKey(reference.Function))
          pending.Push(reference.Function);
    }

    return module with { Functions = [.. module.Functions.Where(function => kept.Contains(function.Name) && (!function.IsPublic || exports.Contains(function.Name)))] };
  }

  /// <summary>How a test writes an outcome: the result of a function, or the panic message of a trap.</summary>
  private delegate string Shown(IrType type, string? text, string? panic);

  /// <summary>An outcome as <see cref="BigIntCases.Interpreted"/> writes it.</summary>
  private static string Interpreted(IrType type, string? text, string? panic) {
    if (panic is not null)
      return RustTraps.TrapName(panic) is { } name && Enum.TryParse<IrTrap>(name, out var trap) ? "raise " + IrExceptionClass.Of(trap).Name : "panic " + panic;

    return type.IsFloat ? "f " + Float(type, text!).ToString("R", CultureInfo.InvariantCulture) : type == IrType.Void ? "void" : text!;
  }

  /// <summary>An outcome as the Python oracle writes it (<see cref="PythonOutcome"/>).</summary>
  private static string Python(IrType type, string? text, string? panic) {
    if (panic is not null)
      return "raise " + PythonOutcome.Exception(RustTraps.TrapName(panic) is { } name && Enum.TryParse<IrTrap>(name, out var trap) ? IrExceptionClass.Of(trap).Name : panic);

    return type == IrType.Void ? "None"
      : type == IrType.Bool ? "bool " + (text == "true" ? "True" : "False")
      : type.IsFloat ? "float " + PythonOutcome.Repr(Float(type, text!))
      : "int " + text;
  }

  private static double Float(IrType type, string bits) => type == IrType.F32
    ? BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
    : BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

  /// <summary>
  /// What every call does in the crate the backend writes for a module, prepared as <c>translate --to rust</c>
  /// prepares it and built with the switch given: one outcome per call, in order.
  /// </summary>
  private static async Task<IReadOnlyList<string>> OutcomesAsync(IrModule module, IReadOnlyList<(string Function, object[] Arguments)> calls, OverflowChecks checks, Shown show) {
    var profile = RustProfile.Default;
    var prepared = RustCrates.Prepared(module, profile);
    var files = RustCrates.Emit(module, profile);
    var exports = RustEmitter.Exports(prepared, profile);
    var called = calls.Select(call => call.Function).Distinct(StringComparer.Ordinal).ToList();
    var functions = called.Select(name => prepared.Functions.Single(function => function.Name == name)).ToList();
    var crate = RustEmitter.ModuleName(module.Name, profile);
    var package = new Dictionary<string, ReadOnlyMemory<byte>>(RustCrates.Package(files), StringComparer.Ordinal) {
      [DriverPath] = Encoding.UTF8.GetBytes(Driver(crate, RustEmitter.ModuleName(module.Name, profile), [.. functions.Select(function => (exports[function.Name], function))]))
    };
    var input = string.Concat(calls.Select(call => {
      var function = functions.Single(candidate => candidate.Name == call.Function);
      return called.IndexOf(call.Function).ToString(CultureInfo.InvariantCulture) + string.Concat(call.Arguments.Select((argument, index) => " " + Argument(argument, function.Parameters[index].Type))) + "\n";
    }));
    var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["RUSTUP_TOOLCHAIN"] = RustLayerRun.Commands.Stable,
      ["RUSTUP_AUTO_INSTALL"] = "0",
      ["CARGO_PROFILE_DEV_OVERFLOW_CHECKS"] = checks == OverflowChecks.Panic ? "true" : "false"
    };
    if (RustLayerRun.Commands.RustupHome is { } home)
      environment[RustCommands.RustupHomeVariable] = home;

    var step = new ProcessSpec(RustRunner.Toolchain, ["run", "--offline", "--quiet", "--bin", "big_driver"], Encoding.UTF8.GetBytes(input)) { Environment = environment };
    var result = await new CrossLanguageRunner(RustLayerRun.Sandbox, Toolchains).RunAsync(new RunPlan(package, [], step, RunLimits.Default, TrustLevel.Trusted));
    var stdout = Encoding.UTF8.GetString(result.Stdout.Span);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)), () => Encoding.UTF8.GetString(result.Stderr.Span) + "\n" + RustCrates.ModuleText(files));
    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines, Has.Length.EqualTo(calls.Count));
    return [.. calls.Zip(lines).Select(pair => {
      var type = functions.Single(function => function.Name == pair.First.Function).ReturnType;
      return pair.Second.StartsWith('!') ? show(type, null, pair.Second[1..]) : show(type, pair.Second, null);
    })];
  }

  /// <summary>An argument as the driver reads it: an integer in decimal, a floating-point number by its bits in hex, a truth value as 1 or 0.</summary>
  private static string Argument(object value, IrType type) => value switch {
    bool truth => truth ? "1" : "0",
    double number when type == IrType.F32 => BitConverter.SingleToUInt32Bits((float)number).ToString("x8", CultureInfo.InvariantCulture),
    double number => BitConverter.DoubleToUInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
    IFormattable integer => integer.ToString(null, CultureInfo.InvariantCulture),
    _ => throw new ArgumentException($"{value} is no argument the driver reads", nameof(value))
  };

  /// <summary>The driver: one arm per function, each argument read as its type, the result written as the test reads it.</summary>
  private static string Driver(string crate, string module, IReadOnlyList<(string Path, IrFunction Function)> exports) {
    var text = new StringBuilder();
    text.Append(CultureInfo.InvariantCulture, $"use std::io::{{self, BufRead, Write}};\n\nuse {crate}::{module}::PlyBigInt;\n\n");
    text.Append("""
      #[allow(dead_code)]
      fn big(text: &str) -> PlyBigInt {
          let mut magnitude: Vec<u32> = Vec::new();
          for digit in text.trim_start_matches('-').bytes() {
              let mut carry = u64::from(digit - b'0');
              for place in magnitude.iter_mut() {
                  carry += u64::from(*place) * 10;
                  *place = carry as u32;
                  carry >>= 32;
              }
              if carry != 0 {
                  magnitude.push(carry as u32);
              }
          }
          while magnitude.last() == Some(&0) {
              magnitude.pop();
          }
          PlyBigInt { negative: text.starts_with('-') && !magnitude.is_empty(), magnitude }
      }

      #[allow(dead_code)]
      fn text(value: &PlyBigInt) -> String {
          let mut rest = value.magnitude.clone();
          let mut chunks = Vec::new();
          while !rest.is_empty() {
              let mut remainder = 0u64;
              for place in rest.iter_mut().rev() {
                  let current = remainder << 32 | u64::from(*place);
                  *place = (current / 1_000_000_000) as u32;
                  remainder = current % 1_000_000_000;
              }
              chunks.push(remainder);
              while rest.last() == Some(&0) {
                  rest.pop();
              }
          }
          let mut out = String::new();
          if value.negative {
              out.push('-');
          }
          match chunks.split_last() {
              None => out.push('0'),
              Some((top, lower)) => {
                  out.push_str(&top.to_string());
                  for chunk in lower.iter().rev() {
                      out.push_str(&format!("{:09}", chunk));
                  }
              }
          }
          out
      }


      """);
    text.Append("fn call(export: usize, a: &[&str]) -> String {\n    match export {\n");
    for (var index = 0; index < exports.Count; ++index) {
      var (path, function) = exports[index];
      var arguments = function.Parameters.Select((parameter, position) => Read(parameter.Type, position));
      text.Append(CultureInfo.InvariantCulture, $"        {index} => {{\n            let result = {crate}::{path}({string.Join(", ", arguments)});\n            {Written(function.ReturnType)}\n        }}\n");
    }

    text.Append("""
              _ => std::process::exit(2),
          }
      }

      fn main() {
          std::panic::set_hook(Box::new(|_| {}));
          let stdin = io::stdin();
          let stdout = io::stdout();
          let mut out = stdout.lock();
          for line in stdin.lock().lines() {
              let line = line.unwrap();
              let parts: Vec<&str> = line.split(' ').collect();
              let export: usize = parts[0].parse().unwrap();
              match std::panic::catch_unwind(|| call(export, &parts[1..])) {
                  Ok(value) => writeln!(out, "{}", value).unwrap(),
                  Err(payload) => {
                      let message = payload.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| payload.downcast_ref::<String>().cloned()).unwrap_or_default();
                      writeln!(out, "!{}", message.lines().next().unwrap_or("")).unwrap();
                  }
              }
          }
      }

      """);
    return text.ToString();
  }

  /// <summary>An argument read from its text as the type of its parameter.</summary>
  private static string Read(IrType type, int position) {
    var argument = $"a[{position.ToString(CultureInfo.InvariantCulture)}]";
    return type.Kind switch {
      IrTypeKind.BigInt => $"big({argument})",
      IrTypeKind.Bool => $"{argument} == \"1\"",
      IrTypeKind.F64 => $"f64::from_bits(u64::from_str_radix({argument}, 16).unwrap())",
      IrTypeKind.F32 => $"f32::from_bits(u32::from_str_radix({argument}, 16).unwrap())",
      _ => $"{argument}.parse::<{RustEmitter.TypeName(type)}>().unwrap()"
    };
  }

  /// <summary>The result written as the test reads it.</summary>
  private static string Written(IrType type) => type.Kind switch {
    IrTypeKind.Void => "let () = result;\n            String::from(\"void\")",
    IrTypeKind.BigInt => "text(&result)",
    IrTypeKind.F64 => "format!(\"{:016x}\", result.to_bits())",
    IrTypeKind.F32 => "format!(\"{:08x}\", result.to_bits())",
    _ => "result.to_string()"
  };

}
