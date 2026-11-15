using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0002, compile-time evaluation of pure calls (prototype): a call of a function whose effect summary
/// (<see cref="IrEffects"/>) finds it pure, with constant integer or truth-value arguments and an
/// integer or truth-value result, becomes its value, as the interpreter computes it within a budget of
/// steps (<see cref="StepBudget"/>). A call whose evaluation traps stays, so the trap happens where the
/// program has it, and so does one that needs more than the budget - which the pass notes - or whose
/// functions use an operation of floating point a target may compute otherwise than the host. A call
/// whose value nothing uses is left to dead-code removal (O0005).
/// </summary>
public sealed class O0002CallEvaluation : IIrPass {

  /// <summary>The most steps one evaluation takes: each statement executed is one, and so is each call.</summary>
  public const int StepBudget = 10_000;

  private readonly long _steps;

  /// <summary>The pass with the budget of the pipeline.</summary>
  public O0002CallEvaluation() : this(StepBudget) { }

  /// <summary>The pass with a budget of its own.</summary>
  /// <param name="steps">The most steps one evaluation takes.</param>
  public O0002CallEvaluation(long steps) => this._steps = steps;

  /// <inheritdoc/>
  public string Id => "O0002";

  /// <inheritdoc/>
  public string Name => "compile-time evaluation";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) => this.Run(module, scope, applied, _ => { });

  /// <summary>Runs the pass once and reports every call the budget leaves as it is.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Whether the pass looks at a function.</param>
  /// <param name="applied">Receives every change: the function and the position of the statement it was made at.</param>
  /// <param name="note">Receives a note for every call the budget leaves as it is.</param>
  /// <returns>The module, the same object when nothing changed.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    var evaluation = new Evaluation(this, module, note);
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function))
        return function;

      var body = evaluation.Block(function.Body, [], function.Name, applied);
      if (ReferenceEquals(body, function.Body))
        return function;

      changed = true;
      return function with { Body = body };
    }).ToList();
    return changed ? module with { Functions = functions } : module;
  }

  /// <summary>Whether a type crosses into the interpreter and back as a constant: an integer or a truth value.</summary>
  private static bool Constant(IrType type) => type.IsInteger || type == IrType.Bool;

  /// <summary>A constant as the interpreter takes it across its boundary: a .NET value of exactly its type.</summary>
  private static object Host(IrConst constant) => constant.Type.Kind switch {
    IrTypeKind.Bool => constant.Value != 0,
    IrTypeKind.I8 => (sbyte)constant.Value,
    IrTypeKind.U8 => (byte)constant.Value,
    IrTypeKind.I16 => (short)constant.Value,
    IrTypeKind.U16 => (ushort)constant.Value,
    IrTypeKind.I32 => (int)constant.Value,
    IrTypeKind.U32 => (uint)constant.Value,
    IrTypeKind.U64 => unchecked((ulong)constant.Value),
    _ => constant.Value
  };

  /// <summary>The run of the pass over one module: its summaries, and the smallest module each function evaluated needs.</summary>
  private sealed class Evaluation(O0002CallEvaluation pass, IrModule module, Action<string> note) {

    private readonly IrEffectTable _effects = IrEffects.Of(module);
    private readonly Dictionary<string, IrFunction> _functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, IrModule?> _programs = new(StringComparer.Ordinal);

    public IrBlock Block(IrBlock block, List<int> position, string function, Action<string, string> applied) {
      var statements = new List<IrStmt>();
      var changed = false;
      for (var index = 0; index < block.Statements.Count; ++index) {
        position.Add(index + 1);
        var statement = block.Statements[index];
        var evaluated = 0;
        // A call whose value nothing uses is dead-code removal's to take away.
        var mapped = statement is IrExprStmt { Expression: IrCall }
          ? statement
          : IrPassSupport.Map(statement, expression => IrShape.Map(expression, node => this.Value(node, function) is { } value ? Count(value) : null), inner => this.Block(inner, position, function, applied));
        if (evaluated > 0)
          applied(function, string.Join('.', position));

        changed |= !ReferenceEquals(mapped, statement);
        statements.Add(mapped);
        position.RemoveAt(position.Count - 1);

        IrExpr Count(IrExpr value) {
          ++evaluated;
          return value;
        }
      }

      return changed ? new IrBlock(statements) : block;
    }

    /// <summary>The value of a call the pass evaluates, as a constant; <see langword="null"/> for any other node and for a call it keeps.</summary>
    private IrConst? Value(IrExpr node, string caller) {
      if (node is not IrCall call || !Constant(call.Type) || !call.Arguments.All(argument => argument is IrConst constant && Constant(constant.Type))
          || !this._effects.Function(call.Function).IsPure || this.Program(call.Function) is not { } program)
        return null;

      try {
        var value = IrInterpreter.Call(program, call.Function, [.. call.Arguments.Select(argument => Host((IrConst)argument))], pass._steps)!;
        return IrPassSupport.Constant(call.Type, value);
      } catch (IrException) {
        // The trap happens at run time, where the program has it.
        return null;
      } catch (IrBudgetException) {
        note($"{pass.Id} {pass.Name}: '{caller}' keeps its call of '{call.Function}': its evaluation needs more than {pass._steps} steps or nests calls deeper than {IrInterpreter.BudgetDepth}");
        return null;
      }
    }

    /// <summary>
    /// The module a pure function is evaluated in: it and every function it calls, no globals - a pure
    /// function reads none - and the tables and types of the module; <see langword="null"/> where one of
    /// them uses an operation of floating point, which the host may compute otherwise than a target.
    /// </summary>
    private IrModule? Program(string name) {
      if (this._programs.TryGetValue(name, out var known))
        return known;

      var reached = new List<IrFunction>();
      var seen = new HashSet<string>(StringComparer.Ordinal) { name };
      var pending = new Queue<string>([name]);
      while (pending.Count > 0) {
        var function = this._functions[pending.Dequeue()];
        reached.Add(function);
        foreach (var callee in IrPassSupport.Nodes(function.Body).OfType<IrCall>().Select(call => call.Function))
          if (seen.Add(callee))
            pending.Enqueue(callee);
      }

      var inexact = reached.SelectMany(function => IrPassSupport.Nodes(function.Body)).OfType<IrIntrinsic>()
        .Any(intrinsic => intrinsic.Type.IsFloat || intrinsic.Arguments.Any(argument => argument.Type.IsFloat));
      return this._programs[name] = inexact ? null : new IrModule(module.Name, reached, module.Constants) { Types = module.Types };
    }

  }

}
