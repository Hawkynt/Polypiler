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

    foreach (var function in module.Functions)
      new FunctionCheck(function, functions, diagnostics).Run();

    return diagnostics;
  }

  private static Diagnostic Error(string function, string message)
    => new(Code, DiagnosticSeverity.Error, $"ill-typed IR in '{function}': {message}");

  private sealed class FunctionCheck(
    IrFunction function,
    Dictionary<string, IrFunction> functions,
    List<Diagnostic> diagnostics) {

    private readonly HashSet<IrLocal> _visible = [];

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
          this.Block(loop.Body);
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

    private void InScope(IrLocal local)
      => this.Require(this._visible.Contains(local), $"'{local.Name}' is used outside the scope that declares it");

    private void Value(IrExpr expression, IrType? expected, string what) {
      this.Expression(expression);
      this.Require(expression.Type == expected, $"{what} has the type {expression.Type}, expected {expected}");
    }

    private void Element(IrExpr array, IrExpr index) {
      this.Expression(array);
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
          this.Expression(length.Array);
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

    private static bool Converts(IrConvert conversion)
      => conversion.Type.IsInteger && conversion.Operand.Type.IsInteger && conversion.Contract is IrConversion.Wrap or IrConversion.Trap;

    private static bool InRange(IrConst constant) => constant.Type.Kind switch {
      IrTypeKind.Bool => constant.Value is 0 or 1,
      IrTypeKind.Void or IrTypeKind.Array => false,
      _ => constant.Type.IsLong || (constant.Value >= IrOperators.Minimum(constant.Type) && constant.Value <= IrOperators.Maximum(constant.Type))
    };

  }

}
