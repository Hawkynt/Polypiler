using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// Replaces the floating-point operations a target has no admitted native call for by calls of their
/// portable implementations, so that a backend only has to write what its target has. The portable
/// implementations come as the module the C# frontend imports from <see cref="FloatOperationSources"/>;
/// the functions a call needs, the ones they call in turn and the tables they read are copied into the
/// module under names no name of the module has, and their own operations are legalized the same way.
/// Generated code stays self-contained, and the interpreter, which runs the same implementations,
/// gives the same bits.
/// </summary>
public static class FloatLegalization {

  /// <summary>
  /// The module with every floating-point intrinsic that has a portable implementation and that
  /// <paramref name="native"/> rejects replaced by a call of that implementation. The operations that
  /// trap on their operands (<c>Clamp</c>, <c>RoundDigits</c>) stay intrinsics: a backend writes them.
  /// </summary>
  /// <param name="module">The module to legalize.</param>
  /// <param name="implementations">The portable implementations as one module.</param>
  /// <param name="native">Whether the target writes an intrinsic itself.</param>
  /// <returns>The legalized module; the module itself when nothing had to be replaced.</returns>
  /// <exception cref="ArgumentException">An implementation the module needs is missing from <paramref name="implementations"/>.</exception>
  public static IrModule Lower(IrModule module, IrModule implementations, Func<IrIntrinsic, bool> native)
    => Lower(module, () => implementations, native);

  /// <summary>
  /// The module legalized as <see cref="Lower(IrModule, IrModule, Func{IrIntrinsic, bool})"/> does, the
  /// portable implementations asked for only when an operation has to be replaced: importing them
  /// compiles their source.
  /// </summary>
  /// <param name="module">The module to legalize.</param>
  /// <param name="implementations">Gives the portable implementations as one module.</param>
  /// <param name="native">Whether the target writes an intrinsic itself.</param>
  /// <returns>
  /// The legalized module, which records every implementation it copied in under
  /// <see cref="IrModule.Implementations"/>; the module itself when nothing had to be replaced.
  /// </returns>
  public static IrModule Lower(IrModule module, Func<IrModule> implementations, Func<IrIntrinsic, bool> native) {
    var run = new Run(module, implementations, native);
    var functions = module.Functions.Select(run.Function).ToList();
    if (run.Names.Count == 0)
      return module;

    var helpers = run.Helpers();
    return module with {
      Functions = [.. functions, .. helpers.Functions],
      Constants = [.. module.Constants, .. helpers.Constants],
      Implementations = [.. module.Implementations, .. run.Names.Select(pair => (pair.Value, pair.Key))]
    };
  }

  /// <summary>Whether an intrinsic is one the legalization replaces where the target lacks it.</summary>
  /// <param name="intrinsic">The intrinsic.</param>
  /// <returns><see langword="true"/> for a floating-point operation with a portable implementation that never traps.</returns>
  public static bool IsReplaceable(IrIntrinsic intrinsic)
    => FloatOperationTable.Of(intrinsic.Op) is { DoubleName: not null, Traps: null }
      && intrinsic.Arguments.Count > 0 && intrinsic.Arguments[0].Type.IsFloat;

  /// <summary>The name of the portable implementation of an intrinsic.</summary>
  private static string Implementation(IrIntrinsic intrinsic) {
    var operation = FloatOperationTable.Of(intrinsic.Op)!;
    return intrinsic.Arguments[0].Type == IrType.F32 ? operation.SingleName! : operation.DoubleName!;
  }

  private sealed class Run(IrModule module, Func<IrModule> implementations, Func<IrIntrinsic, bool> native) {

    private readonly Lazy<Dictionary<string, IrFunction>> _available = new(() => implementations().Functions.ToDictionary(function => function.Name));
    private readonly HashSet<string> _taken = [
      .. module.Functions.Select(function => function.Name), .. module.Constants.Select(constant => constant.Name), .. module.Globals.Select(global => global.Name)
    ];
    private readonly Dictionary<IrModuleConst, IrModuleConst> _tables = new(ReferenceEqualityComparer.Instance);
    private bool _inHelper;

    /// <summary>The name in the module of every implementation it needs, in the order they were first needed.</summary>
    public Dictionary<string, string> Names { get; } = [];

    public IrFunction Function(IrFunction function) => function with { Body = this.Block(function.Body) };

    /// <summary>The implementations the module calls, each legalized itself, and the tables they read.</summary>
    public (List<IrFunction> Functions, List<IrModuleConst> Constants) Helpers() {
      this._inHelper = true;
      var done = new List<IrFunction>();
      var index = 0;
      while (index < this.Names.Count) {
        var name = this.Names.Keys.ElementAt(index++);
        var source = this._available.Value[name];
        done.Add(this.Function(source) with { Name = this.Names[name], IsPublic = false });
      }

      return (done, [.. this._tables.Values]);
    }

    private string Rename(string name) {
      if (this.Names.TryGetValue(name, out var renamed))
        return renamed;

      if (!this._available.Value.ContainsKey(name))
        throw new ArgumentException($"The portable implementation '{name}' is missing.", nameof(implementations));

      var candidate = "Float" + name;
      while (!this._taken.Add(candidate))
        candidate += "_";

      this.Names[name] = candidate;
      return candidate;
    }

    private IrModuleConst Table(IrModuleConst constant) {
      if (!this._inHelper)
        return constant;

      if (!this._tables.TryGetValue(constant, out var renamed)) {
        var candidate = "Float" + constant.Name.TrimStart('_');
        while (!this._taken.Add(candidate))
          candidate += "_";

        renamed = constant with { Name = candidate };
        this._tables[constant] = renamed;
      }

      return renamed;
    }

    private IrBlock Block(IrBlock block) => new(block.Statements.Select(this.Statement).ToList());

    private IrStmt Statement(IrStmt statement) => statement switch {
      IrBlock block => this.Block(block),
      IrDeclare declare => declare with { Initializer = declare.Initializer is null ? null : this.Expression(declare.Initializer) },
      IrAssign assign => assign with { Value = this.Expression(assign.Value) },
      IrGlobalAssign assign => assign with { Value = this.Expression(assign.Value) },
      IrStore store => new IrStore(this.Expression(store.Array), this.Expression(store.Index), this.Expression(store.Value)),
      IrFieldStore store => store with { Target = this.Expression(store.Target), Value = this.Expression(store.Value) },
      IrExprStmt expression => new IrExprStmt(this.Expression(expression.Expression)),
      IrIf conditional => new IrIf(this.Expression(conditional.Condition), this.Block(conditional.Then), conditional.Else is null ? null : this.Block(conditional.Else)),
      IrWhile loop => new IrWhile(this.Expression(loop.Condition), this.Block(loop.Body)),
      IrDoWhile loop => new IrDoWhile(this.Block(loop.Body), this.Expression(loop.Condition)),
      IrFor loop => new IrFor(this.Expression(loop.Condition), this.Block(loop.Body), this.Block(loop.Step)),
      IrSwitch choice => new IrSwitch(this.Expression(choice.Value), choice.Sections.Select(section => section with { Body = this.Block(section.Body) }).ToList()),
      IrReturn result => new IrReturn(result.Value is null ? null : this.Expression(result.Value)),
      _ => statement
    };

    private IrExpr Expression(IrExpr expression) {
      switch (expression) {
        case IrConstRef reference:
          return new IrConstRef(this.Table(reference.Constant));
        case IrUnary unary:
          return new IrUnary(unary.Op, this.Expression(unary.Operand));
        case IrBinary binary:
          return new IrBinary(binary.Op, this.Expression(binary.Left), this.Expression(binary.Right));
        case IrConvert conversion:
          return new IrConvert(conversion.Type, this.Expression(conversion.Operand), conversion.Contract);
        case IrConditional choice:
          return new IrConditional(this.Expression(choice.Condition), this.Expression(choice.WhenTrue), this.Expression(choice.WhenFalse));
        case IrArrayLoad load:
          return new IrArrayLoad(this.Expression(load.Array), this.Expression(load.Index));
        case IrArrayLength length:
          return new IrArrayLength(this.Expression(length.Array));
        case IrNewArray creation:
          return new IrNewArray(creation.Type, this.Expression(creation.Length));
        case IrStep step:
          return step with { Target = this.Expression(step.Target) };
        case IrStructNew creation:
          return creation with { Arguments = [.. creation.Arguments.Select(this.Expression)] };
        case IrFieldLoad load:
          return load with { Target = this.Expression(load.Target) };
        case IrStructEquals equality:
          return new IrStructEquals(this.Expression(equality.Left), this.Expression(equality.Right));
        case IrCall call:
          var arguments = call.Arguments.Select(this.Expression).ToList();
          return new IrCall(this._inHelper ? this.Rename(call.Function) : call.Function, call.Type, arguments);
        case IrIntrinsic intrinsic:
          var operands = intrinsic.Arguments.Select(this.Expression).ToList();
          var lowered = new IrIntrinsic(intrinsic.Op, intrinsic.Type, operands);
          return IsReplaceable(lowered) && !native(lowered)
            ? new IrCall(this.Rename(Implementation(lowered)), lowered.Type, operands)
            : lowered;
        default:
          return expression;
      }
    }

  }

}
