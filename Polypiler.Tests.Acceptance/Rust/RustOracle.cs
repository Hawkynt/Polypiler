using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Runs calls of a Rust source compiled by <c>rustc</c>: a driver includes the source as a module,
/// calls each function under <c>catch_unwind</c> and prints one outcome per call - the result as
/// <see cref="PrototypeFixture.Show(object)"/> writes it, or <c>panic: </c> and the panic message. The
/// driver is compiled with <c>-C overflow-checks</c> on or off, which is the overflow mode.
/// </summary>
internal static class RustOracle {

  private const string Prelude = """
    #![allow(warnings)]
    use std::fmt::Write;

    trait Show { fn show(&self) -> String; }
    macro_rules! integers { ($($t:ty),*) => { $(impl Show for $t { fn show(&self) -> String { self.to_string() } })* } }
    integers!(i8, i16, i32, i64, isize, u8, u16, u32, u64, usize, bool);
    fn float(x: f64) -> String {
      if x.is_nan() { "NaN".to_string() }
      else if x.abs() <= 9007199254740991.0 && x.trunc() == x && !(x == 0.0 && x.is_sign_negative()) { (x as i64).to_string() }
      else { format!("f:{:016x}", x.to_bits()) }
    }
    impl Show for f32 { fn show(&self) -> String { float(*self as f64) } }
    impl Show for f64 { fn show(&self) -> String { float(*self) } }
    impl Show for () { fn show(&self) -> String { "void".to_string() } }
    impl<A: Show, B: Show> Show for (A, B) { fn show(&self) -> String { format!("{{{},{}}}", self.0.show(), self.1.show()) } }
    impl<A: Show, B: Show, C: Show> Show for (A, B, C) { fn show(&self) -> String { format!("{{{},{},{}}}", self.0.show(), self.1.show(), self.2.show()) } }
    fn slice<T: Show>(v: &[T]) -> String { format!("[{}]", v.iter().map(|x| x.show()).collect::<Vec<_>>().join(",")) }

    fn out_with<T: Show>(o: &mut String, r: std::thread::Result<T>, after: &[String]) {
      match r {
        Ok(v) => {
          o.push_str(&v.show());
          for a in after { o.push_str(" | "); o.push_str(a); }
          o.push('\n');
        }
        Err(e) => out::<()>(o, Err(e)),
      }
    }

    fn out<T: Show>(o: &mut String, r: std::thread::Result<T>) {
      match r {
        Ok(v) => o.push_str(&v.show()),
        Err(e) => {
          let m = e.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| e.downcast_ref::<String>().cloned()).unwrap_or_default();
          o.push_str("panic: ");
          o.push_str(&m.replace('\n', " "));
        }
      }
      o.push('\n');
    }

    """;

  /// <summary>The outcomes of the calls, one per call, a panic as its class (<see cref="RustFixture.Normalized"/>).</summary>
  /// <param name="directory">A directory for the driver and the executable.</param>
  /// <param name="source">The Rust source file.</param>
  /// <param name="module">The source imported, whose parameter types say how the arguments are written.</param>
  /// <param name="calls">The calls, in the order of the outcomes.</param>
  /// <param name="mode">Whether overflow checks are on.</param>
  public static IReadOnlyList<string> Outcomes(string directory, string source, IrModule module, IReadOnlyList<PrototypeCall> calls, OverflowChecks mode) {
    Directory.CreateDirectory(directory);
    var driver = Path.Combine(directory, "main.rs");
    File.WriteAllText(driver, Driver(source, module, calls));
    var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "driver.exe" : "driver");
    var (compiled, _, diagnostics) = Run("rustc", directory, "--edition", "2024", "-C", $"overflow-checks={(mode == OverflowChecks.Panic ? "on" : "off")}", "-C", "opt-level=0", "-o", executable, driver);
    Assert.That(compiled, Is.Zero, diagnostics);
    var (exitCode, output, failure) = Run(executable, directory);
    Assert.That(exitCode, Is.Zero, failure);
    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => RustFixture.Normalized(line.TrimEnd('\r'))).ToList();
    Assert.That(lines, Has.Count.EqualTo(calls.Count), "the driver printed another number of outcomes than there are calls");
    return lines;
  }

  /// <summary>The driver: the source as a module, then one loop over the argument tuples of each run of calls to one function.</summary>
  private static string Driver(string source, IrModule module, IReadOnlyList<PrototypeCall> calls) {
    var text = new StringBuilder(Prelude);
    text.Append(CultureInfo.InvariantCulture, $"#[path = \"{Path.GetFullPath(source).Replace('\\', '/')}\"]\nmod fixture;\nuse fixture::*;\n\nfn main() {{\n");
    text.Append("  std::panic::set_hook(Box::new(|_| {}));\n  let mut o = String::new();\n");
    var functions = module.Functions.ToDictionary(function => function.Name);
    for (var start = 0; start < calls.Count;) {
      var end = start;
      while (end < calls.Count && calls[end].Method == calls[start].Method)
        ++end;

      var function = functions[calls[start].Method];
      var types = function.Parameters.Select(parameter => RustType(parameter.Type)).ToList();
      if (function.Parameters.Any(parameter => parameter.Type.IsView)) {
        Views(text, function, calls, start, end);
      } else if (types.Count == 0) {
        for (var index = start; index < end; ++index)
          text.Append(CultureInfo.InvariantCulture, $"  out(&mut o, std::panic::catch_unwind(|| {function.Name}()));\n");
      } else {
        var names = types.Select((_, index) => $"a{index}").ToList();
        var tuple = $"({string.Join(", ", types)},)";
        text.Append(CultureInfo.InvariantCulture, $"  {{\n    let cases: Vec<{tuple}> = vec![\n");
        for (var index = start; index < end; ++index)
          text.Append(CultureInfo.InvariantCulture, $"      ({string.Join(", ", calls[index].Arguments.Select(Literal))},),\n");

        text.Append(CultureInfo.InvariantCulture, $"    ];\n    for &({string.Join(", ", names)},) in cases.iter() {{\n");
        text.Append(CultureInfo.InvariantCulture, $"      out(&mut o, std::panic::catch_unwind(move || {function.Name}({string.Join(", ", names.Select(name => name + " as _"))})));\n    }}\n  }}\n");
      }

      start = end;
    }

    text.Append("  print!(\"{}\", o);\n}\n");
    return text.ToString();
  }

  /// <summary>
  /// A run of calls of a function that takes slices: each slice argument is a <c>Vec</c> of its own,
  /// passed as <c>&amp;v[..]</c> or <c>&amp;mut v[..]</c>, and after a call that returned its contents
  /// follow the result, as <see cref="PrototypeFixture.Outcome"/> writes the arrays a call was given.
  /// </summary>
  private static void Views(StringBuilder text, IrFunction function, IReadOnlyList<PrototypeCall> calls, int start, int end) {
    var parameters = function.Parameters;
    var types = parameters.Select(parameter => parameter.Type.IsView ? $"Vec<{RustType(parameter.Type.Element!)}>" : RustType(parameter.Type)).ToList();
    var names = parameters.Select((_, index) => $"a{index}").ToList();
    text.Append(CultureInfo.InvariantCulture, $"  {{\n    let cases: Vec<({string.Join(", ", types)},)> = vec![\n");
    for (var index = start; index < end; ++index)
      text.Append(CultureInfo.InvariantCulture, $"      ({string.Join(", ", calls[index].Arguments.Select((argument, position) => Argument(argument, parameters[position].Type)))},),\n");

    text.Append("    ];\n");
    var arguments = parameters.Select((parameter, index) => parameter.Type.Kind switch {
      IrTypeKind.Span => $"&mut a{index}[..]",
      IrTypeKind.ReadOnlySpan => $"&a{index}[..]",
      _ => $"a{index} as _"
    });
    var after = parameters.Select((parameter, index) => (parameter, index)).Where(pair => pair.parameter.Type.IsView).Select(pair => $"slice(&a{pair.index})");
    text.Append(CultureInfo.InvariantCulture, $"    for case in cases.into_iter() {{\n      let (mut {string.Join(", mut ", names)},) = case;\n");
    text.Append(CultureInfo.InvariantCulture, $"      let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {function.Name}({string.Join(", ", arguments)})));\n");
    text.Append(CultureInfo.InvariantCulture, $"      out_with(&mut o, r, &[{string.Join(", ", after)}]);\n    }}\n  }}\n");
  }

  /// <summary>An argument as a Rust literal of its parameter's type: a slice argument as a <c>vec!</c> of typed elements.</summary>
  private static string Argument(object value, IrType type) {
    if (!type.IsView)
      return Literal(value);

    var element = RustType(type.Element!);
    var items = ((Array)value).Cast<object>().Select(item => item is float or double ? Literal(item) : $"{Literal(item)}{element}");
    return $"vec![{string.Join(", ", items)}]";
  }

  private static string RustType(IrType type) => type == IrType.Bool ? "bool" : type.ToString();

  private static string Literal(object value) => value switch {
    bool truth => truth ? "true" : "false",
    float single => $"f32::from_bits(0x{BitConverter.SingleToUInt32Bits(single):x}u32)",
    double number => $"f64::from_bits(0x{BitConverter.DoubleToUInt64Bits(number):x}u64)",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
  };

  /// <summary>Runs a program in a folder and waits for it: its exit code, standard output and standard error.</summary>
  internal static (int ExitCode, string Output, string Error) Run(string program, string directory, params string[] arguments) {
    var start = new ProcessStartInfo(program) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error.Result);
  }

}
