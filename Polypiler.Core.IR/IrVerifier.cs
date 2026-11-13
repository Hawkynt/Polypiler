using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>Checks that a module is well-typed before anything interprets or emits it.</summary>
public static class IrVerifier {

  /// <summary>The code of every diagnostic the verifier reports.</summary>
  public const string Code = "PLY0100";

  /// <summary>Checks every function of a module.</summary>
  /// <param name="module">The module to check.</param>
  /// <returns>One diagnostic per violated rule; empty when the module is well-typed.</returns>
  public static IReadOnlyList<Diagnostic> Verify(IrModule module) {
    var diagnostics = new List<Diagnostic>();
    var functions = new Dictionary<string, IrFunction>();
    foreach (var function in module.Functions)
      if (!functions.TryAdd(function.Name, function))
        diagnostics.Add(Error(function.Name, "the module declares the function twice"));

    var constants = new HashSet<IrModuleConst>(ReferenceEqualityComparer.Instance);
    var names = new HashSet<string>(functions.Keys);
    foreach (var constant in module.Constants) {
      constants.Add(constant);
      var element = constant.Type.Element ?? constant.Type;
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(constant.Name, message));
      }

      Require(names.Add(constant.Name), "the module declares the name twice");
      Require(constant.Type.IsStorable, $"a constant of the type {constant.Type}");
      Require(constant.Type.IsArray || constant.Values.Count == 1, $"a constant of the type {constant.Type} with {constant.Values.Count} values");
      foreach (var value in constant.Values.Where(value => !FunctionCheck.InRange(new IrConst(element, value))).Take(1))
        Require(false, $"the constant {value} is not a value of {element}");
    }

    foreach (var function in module.Functions)
      new FunctionCheck(function, functions, constants, diagnostics).Run();

    return diagnostics;
  }

  private static Diagnostic Error(string member, string message)
    => new(Code, DiagnosticSeverity.Error, $"ill-typed IR in '{member}': {message}");

  private sealed class FunctionCheck(
    IrFunction function,
    Dictionary<string, IrFunction> functions,
    HashSet<IrModuleConst> constants,
    List<Diagnostic> diagnostics) {

    private readonly HashSet<IrLocal> _visible = [];
    private int _loops;
    private int _breakable;

    public void Run() {
      this.Require(function.ReturnType.IsStorable || function.ReturnType == IrType.Void,
        $"the return type {function.ReturnType} is not a type of the IR");
      foreach (var parameter in function.Parameters) {
        this.Require(parameter.Type.IsStorable, $"parameter '{parameter.Name}' has the type {parameter.Type}");
        this.Require(this._visible.Add(parameter), $"parameter '{parameter.Name}' is declared twice");
      }

      foreach (var local in function.Locals)
        this.Require(local.Type.IsStorable, $"local '{local.Name}' has the type {local.Type}");

      this.Block(function.Body);
    }

    private void Require(bool holds, string message) {
      if (!holds)
        diagnostics.Add(Error(function.Name, message));
    }

    private void Block(IrBlock block) {
      var declared = new List<IrLocal>();
      foreach (var statement in block.Statements)
        this.Statement(statement, declared);

      this._visible.ExceptWith(declared);
    }

    private void Statement(IrStmt statement, List<IrLocal> declared) {
      switch (statement) {
        case IrBlock block:
          this.Block(block);
          break;
        case IrDeclare declare:
          this.Require(function.Locals.Contains(declare.Local),
            $"local '{declare.Local.Name}' is not among the locals of the function");
          if (declare.Initializer is not null)
            this.Value(declare.Initializer, declare.Local.Type, $"the initializer of '{declare.Local.Name}'");

          if (this._visible.Add(declare.Local))
            declared.Add(declare.Local);
          else
            this.Require(false, $"local '{declare.Local.Name}' is declared twice");

          break;
        case IrAssign assign:
          this.InScope(assign.Local);
          this.Value(assign.Value, assign.Local.Type, $"the value assigned to '{assign.Local.Name}'");
          break;
        case IrStore store:
          this.Writable(store.Array);
          this.Element(store.Array, store.Index);
          this.Value(store.Value, store.Array.Type.Element, "the value stored in an array element");
          break;
        case IrExprStmt expression:
          this.Expression(expression.Expression);
          break;
        case IrIf conditional:
          this.Value(conditional.Condition, IrType.Bool, "the condition of an if");
          this.Block(conditional.Then);
          if (conditional.Else is not null)
            this.Block(conditional.Else);

          break;
        case IrWhile loop:
          this.Value(loop.Condition, IrType.Bool, "the condition of a while");
          this.Loop(loop.Body);
          break;
        case IrDoWhile loop:
          this.Loop(loop.Body);
          this.Value(loop.Condition, IrType.Bool, "the condition of a do");
          break;
        case IrFor loop:
          this.Value(loop.Condition, IrType.Bool, "the condition of a for");
          this.Loop(loop.Body);
          foreach (var step in loop.Step.Statements)
            this.Require(step is IrDeclare or IrAssign or IrStore or IrExprStmt, $"the step of a for holds a {step.GetType().Name}");

          this.Block(loop.Step);
          break;
        case IrSwitch choice:
          this.Switch(choice);
          break;
        case IrBreak:
          this.Require(this._breakable > 0, "a break outside a loop and outside a switch");
          break;
        case IrContinue:
          this.Require(this._loops > 0, "a continue outside a loop");
          break;
        case IrReturn result:
          if (result.Value is null)
            this.Require(function.ReturnType == IrType.Void, "a return without a value in a function that returns one");
          else
            this.Value(result.Value, function.ReturnType, "the value returned");

          break;
        default:
          this.Require(false, $"unknown statement {statement.GetType().Name}");
          break;
      }
    }

    private void Loop(IrBlock body) {
      ++this._loops;
      ++this._breakable;
      this.Block(body);
      --this._breakable;
      --this._loops;
    }

    private void Switch(IrSwitch choice) {
      this.Expression(choice.Value);
      var type = choice.Value.Type;
      this.Require(type.IsInteger, $"a switch on the type {type}");
      var labels = new HashSet<long>();
      var defaults = 0;
      foreach (var section in choice.Sections) {
        this.Require(section.IsDefault || section.Labels.Count > 0, "a switch section without a label");
        foreach (var label in section.Labels) {
          this.Require(!type.IsInteger || InRange(new IrConst(type, label)), $"the label {label} is not a value of {type}");
          this.Require(labels.Add(label), $"the label {label} stands twice in a switch");
        }

        if (section.IsDefault)
          ++defaults;

        ++this._breakable;
        this.Block(section.Body);
        --this._breakable;
      }

      this.Require(defaults <= 1, "a switch with more than one default section");
    }

    private void InScope(IrLocal local)
      => this.Require(this._visible.Contains(local), $"'{local.Name}' is used outside the scope that declares it");

    private void Value(IrExpr expression, IrType? expected, string what) {
      this.Expression(expression);
      this.Require(expression.Type == expected, $"{what} has the type {expression.Type}, expected {expected}");
    }

    /// <summary>
    /// An array that is only read: unlike everywhere else, a table of the module may stand here.
    /// </summary>
    private void Readable(IrExpr array) {
      if (array is IrConstRef table)
        this.Known(table.Constant);
      else
        this.Expression(array);
    }

    private void Writable(IrExpr array)
      => this.Require(array is not IrConstRef, "the elements of a constant table are written");

    private void Known(IrModuleConst constant)
      => this.Require(constants.Contains(constant), $"the constant '{constant.Name}' is not among the constants of the module");

    private void Element(IrExpr array, IrExpr index) {
      this.Readable(array);
      this.Expression(index);
      this.Require(array.Type.IsArray, $"an element is taken of the type {array.Type}");
      this.Require(index.Type.IsWord, $"an array index has the type {index.Type}");
    }

    private void Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          this.Require(InRange(constant), $"the constant {constant.Value} is not a value of {constant.Type}");
          break;
        case IrLocalRef reference:
          this.InScope(reference.Local);
          break;
        case IrConstRef reference:
          this.Known(reference.Constant);
          this.Require(!reference.Type.IsArray, $"the constant table '{reference.Constant.Name}' is used as a value");
          break;
        case IrUnary unary:
          this.Expression(unary.Operand);
          this.Require(IrOperators.Accepts(unary.Op, unary.Operand.Type), $"{unary.Op} is applied to {unary.Operand.Type}");
          break;
        case IrBinary binary:
          this.Expression(binary.Left);
          this.Expression(binary.Right);
          this.Require(IrOperators.Accepts(binary.Op, binary.Left.Type, binary.Right.Type),
            $"{binary.Op} is applied to {binary.Left.Type} and {binary.Right.Type}");
          break;
        case IrConvert conversion:
          this.Expression(conversion.Operand);
          this.Require(Converts(conversion),
            $"a conversion from {conversion.Operand.Type} to {conversion.Type} with the contract {conversion.Contract}");
          break;
        case IrConditional choice:
          this.Value(choice.Condition, IrType.Bool, "the condition of a conditional expression");
          this.Expression(choice.WhenTrue);
          this.Value(choice.WhenFalse, choice.WhenTrue.Type, "the second branch of a conditional expression");
          this.Require(choice.Type.IsStorable, $"a conditional expression of the type {choice.Type}");
          break;
        case IrArrayLoad load:
          this.Element(load.Array, load.Index);
          break;
        case IrArrayLength length:
          this.Readable(length.Array);
          this.Require(length.Array.Type.IsArray, $"the length is taken of the type {length.Array.Type}");
          break;
        case IrNewArray creation:
          this.Expression(creation.Length);
          this.Require(creation.Type.IsArray && creation.Type.IsStorable, $"an array is created of the type {creation.Type}");
          this.Require(creation.Length.Type.IsWord, $"an array length has the type {creation.Length.Type}");
          break;
        case IrCall call:
          this.Call(call);
          break;
        case IrIntrinsic intrinsic:
          for (var index = 0; index < intrinsic.Arguments.Count; ++index)
            if (index == 0 && intrinsic.Op == IrIntrinsicOp.ArrayCopy)
              this.Readable(intrinsic.Arguments[index]);
            else
              this.Expression(intrinsic.Arguments[index]);

          this.Require(IrOperators.Accepts(intrinsic.Op, intrinsic.Type, intrinsic.Arguments.Select(argument => argument.Type).ToList()),
            $"{intrinsic.Op} with the type {intrinsic.Type} is applied to ({string.Join(", ", intrinsic.Arguments.Select(argument => argument.Type))})");
          break;
        case IrStep step:
          if (step.Target is IrArrayLoad stepped)
            this.Writable(stepped.Array);

          this.Expression(step.Target);
          this.Require(step.Target is IrLocalRef or IrArrayLoad, $"a step of a {step.Target.GetType().Name}");
          this.Require(step.Type.IsNumeric, $"a step of the type {step.Type}");
          this.Require(!step.Trap || !step.Type.IsFloat, $"a trapping step of the type {step.Type}");
          break;
        default:
          this.Require(false, $"unknown expression {expression.GetType().Name}");
          break;
      }
    }

    private void Call(IrCall call) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      if (!functions.TryGetValue(call.Function, out var target)) {
        this.Require(false, $"the called function '{call.Function}' does not exist");
        return;
      }

      this.Require(call.Type == target.ReturnType,
        $"the call of '{call.Function}' has the type {call.Type}, the function returns {target.ReturnType}");
      this.Require(call.Arguments.Select(argument => argument.Type).SequenceEqual(target.Parameters.Select(parameter => parameter.Type)),
        $"the arguments of the call of '{call.Function}' do not match its parameters");
    }

    private static bool Converts(IrConvert conversion) {
      var from = conversion.Operand.Type;
      if (!from.IsNumeric || !conversion.Type.IsNumeric)
        return false;

      if (conversion.Type.IsFloat)
        return conversion.Contract == IrConversion.Wrap;

      return conversion.Contract == IrConversion.Trap || conversion.Contract == (from.IsFloat ? IrConversion.Saturate : IrConversion.Wrap);
    }

    internal static bool InRange(IrConst constant) => constant.Type.Kind switch {
      IrTypeKind.Bool => constant.Value is 0 or 1,
      IrTypeKind.Void or IrTypeKind.Array => false,
      IrTypeKind.F32 => constant.Value is >= int.MinValue and <= int.MaxValue,
      IrTypeKind.F64 => true,
      _ => constant.Type.IsLong || (constant.Value >= IrOperators.Minimum(constant.Type) && constant.Value <= IrOperators.Maximum(constant.Type))
    };

  }

}
