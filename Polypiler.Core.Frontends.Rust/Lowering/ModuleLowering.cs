using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Checks and lowers the items of a file: the constants first, then every function, then the
/// helpers the functions asked for (the integer powers, which are written in Rust and lowered by
/// this same frontend). A file whose functions do not all check imports nothing.
/// </summary>
internal sealed class ModuleLowering {

  private readonly OverflowChecks _mode;
  private readonly Typed _typed = new();
  private readonly SortedSet<(string Kind, string Type)> _helpers = [];
  private readonly HashSet<string> _names = new(StringComparer.Ordinal);

  private ModuleLowering(OverflowChecks mode) => this._mode = mode;

  /// <summary>The functions of a file in source order followed by the helpers, or <see langword="null"/> when something was reported.</summary>
  public static IReadOnlyList<IrFunction>? Run(SourceFile file, OverflowChecks mode, Action<string, string, Span> report) {
    var lowering = new ModuleLowering(mode);
    var functions = lowering.Functions(file, report);
    if (functions is null)
      return null;

    return [.. functions, .. lowering.Helpers(report)];
  }

  private List<IrFunction>? Functions(SourceFile file, Action<string, string, Span> report) {
    var failed = false;
    void Report(string code, string message, Span at) {
      failed = true;
      report(code, message, at);
    }

    var checker = new TypeChecker(this._typed, Report);
    var signatures = new List<FunctionSignature>();
    foreach (var item in file.Items) {
      switch (item) {
        case RefusedItem refused:
          Report(refused.Refusal.Code, refused.Refusal.Construct, refused.Refusal.At);
          break;
        case FunctionItem function: {
          if (this._typed.Functions.ContainsKey(function.Name)) {
            Report(RustFrontend.InvalidSource, $"E0428: the name `{function.Name}` is defined multiple times", function.At);
            break;
          }

          var parameters = function.Parameters.Select(parameter => checker.Resolve(parameter.Type, false)).ToList();
          var result = checker.Resolve(function.ReturnType, true);
          if (parameters.Any(parameter => parameter is null) || result is null) {
            foreach (var refusal in Refusals.In(function.Body))
              Report(refusal.Code, refusal.Construct, refusal.At);

            break;
          }

          var signature = new FunctionSignature(function, parameters!, result);
          this._typed.Functions[function.Name] = signature;
          this._names.Add(function.Name);
          signatures.Add(signature);
          break;
        }
      }
    }

    foreach (var constant in file.Items.OfType<ConstItem>())
      checker.Constant(constant);

    foreach (var signature in signatures)
      checker.Function(signature);

    if (failed)
      return null;

    return [.. signatures.Select(signature => new FunctionLowering(this, this._typed, this._mode).Lower(signature))];
  }

  /// <summary>The names of the functions of the module, which no local may take.</summary>
  public IReadOnlySet<string> Names => this._names;

  /// <summary>
  /// The name of a helper of the module: <c>wrapping</c>, the power modulo 2^width, or
  /// <c>overflows</c>, whether the exact power is no value of the type; the helper is lowered with the module.
  /// </summary>
  public string Power(string kind, RType type) {
    var rust = type.Ir.ToString();
    this._helpers.Add((kind, rust));
    return this.HelperName(kind, rust);
  }

  private string HelperName(string kind, string type) {
    var name = $"rust_pow_{kind}_{type}";
    while (this._typed.Functions.ContainsKey(name))
      name = "_" + name;

    return name;
  }

  private List<IrFunction> Helpers(Action<string, string, Span> report) {
    if (this._helpers.Count == 0)
      return [];

    var source = string.Concat(this._helpers.Select(helper => PowerSource(this.HelperName(helper.Kind, helper.Type), helper.Kind, helper.Type)));
    var file = Parser.Parse(source);
    var module = new ModuleLowering(OverflowChecks.Wrap);
    foreach (var name in this._names)
      module._names.Add(name);

    return module.Functions(file, report)!;
  }

  /// <summary>
  /// The Rust source of a power helper: exponentiation by squaring as <c>core</c>'s <c>wrapping_pow</c>
  /// and <c>overflowing_pow</c> do it, so that the flag is set exactly when one of the products
  /// overflows, which is exactly when the power is no value of the type.
  /// </summary>
  private static string PowerSource(string name, string kind, string type) => kind == "wrapping"
    ? $$"""
      fn {{name}}(base: {{type}}, exp: u32) -> {{type}} {
        let mut base = base;
        let mut exp = exp;
        let mut acc: {{type}} = 1;
        while exp > 1 {
          if exp & 1 == 1 {
            acc = acc.wrapping_mul(base);
          }
          exp /= 2;
          base = base.wrapping_mul(base);
        }
        if exp == 1 {
          acc = acc.wrapping_mul(base);
        }
        acc
      }

      """
    : $$"""
      fn {{name}}(base: {{type}}, exp: u32) -> bool {
        let mut base = base;
        let mut exp = exp;
        let mut acc: {{type}} = 1;
        let mut overflown = false;
        while exp > 1 {
          if exp & 1 == 1 {
            overflown = overflown | acc.overflowing_mul(base).1;
            acc = acc.wrapping_mul(base);
          }
          exp /= 2;
          overflown = overflown | base.overflowing_mul(base).1;
          base = base.wrapping_mul(base);
        }
        if exp == 1 {
          overflown = overflown | acc.overflowing_mul(base).1;
        }
        overflown
      }

      """;

}
