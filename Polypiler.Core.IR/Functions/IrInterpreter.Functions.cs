using System;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>A closure: a lambda with the cells of the variables it captures, shared with the frame it was made in.</summary>
  private sealed class Closure(IrLambda lambda, Cell[] captured) {

    public IrLambda Lambda { get; } = lambda;

    public Cell[] Captured { get; } = captured;

  }

  /// <summary>A function of the module as a value.</summary>
  private sealed class FunctionReference(IrFunction function) {

    public IrFunction Function { get; } = function;

  }

  /// <summary>A method as a value: bound to its receiver, or static without one, with the kind of call that selects what runs.</summary>
  private sealed class BoundMethod(Instance? receiver, IrMethod method, IrMethodCallKind kind) {

    public Instance? Receiver { get; } = receiver;

    public IrMethod Method { get; } = method;

    public IrMethodCallKind Kind { get; } = kind;

  }

  private sealed partial class Machine {

    /// <summary>The nodes of function values: a closure made, a function or a bound method taken as a value, a function value called.</summary>
    private object FunctionValue(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrLambda lambda:
          return new Closure(lambda, [.. lambda.Captures.Select(variable => frame.Locals[variable])]);
        case IrFunctionRef reference:
          return new FunctionReference(program.Functions[reference.Function]);
        case IrBoundMethod bound:
          var receiver = bound.Receiver is null ? null : (Instance)NotNull(this.Evaluate(bound.Receiver, frame));
          return new BoundMethod(receiver, bound.Method, bound.Kind);
        default:
          var invocation = (IrInvoke)expression;
          var callee = this.Evaluate(invocation.Callee, frame);
          var arguments = invocation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
          return this.Call(callee, arguments)!;
      }
    }

    /// <summary>Calls a function value with evaluated arguments; <c>null</c> traps.</summary>
    private object? Call(object callee, object[] arguments) {
      switch (NotNull(callee)) {
        case Closure closure:
          return this.Run(closure, arguments);
        case FunctionReference function:
          return this.Invoke(function.Function, arguments);
        default:
          var bound = (BoundMethod)callee;
          if (bound.Receiver is not { } self)
            return this.Invoke(program.Functions[bound.Method.Function], arguments);

          var method = bound.Kind switch {
            IrMethodCallKind.Virtual => self.Type.Select(bound.Method),
            IrMethodCallKind.Interface => self.Type.Select(self.Type.Implementation(bound.Method)!),
            _ => bound.Method
          };
          return this.Invoke(program.Functions[method.Function], [self, .. arguments]);
      }
    }

    /// <summary>The body of a closure in a frame of its own that holds the cells it captured and its parameters.</summary>
    private object? Run(Closure closure, object[] arguments) {
      var lambda = closure.Lambda;
      var frame = new Frame();
      for (var index = 0; index < lambda.Captures.Count; ++index)
        frame.Locals[lambda.Captures[index]] = closure.Captured[index];

      for (var index = 0; index < arguments.Length; ++index)
        frame.Declare(lambda.Parameters[index], arguments[index]);

      if (this.Run(lambda.Body, frame) != Flow.Return && lambda.ReturnType != IrType.Void)
        throw new InvalidOperationException("A lambda ended without returning a value.");

      return frame.Result;
    }

  }

}
