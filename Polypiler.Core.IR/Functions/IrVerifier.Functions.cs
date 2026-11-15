using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// A signature is one of values the IR can pass around: every parameter a storable type that is
    /// no view - a closure may outlive the stack frame a view lives in - and the result such a type or
    /// nothing.
    /// </summary>
    private void Signature(IrFunctionType signature) {
      foreach (var parameter in signature.Parameters) {
        this.Require(parameter.IsStorable && !parameter.IsView && !OnHeap(parameter), $"a function type takes the type {parameter}");
        this.Declared(parameter);
      }

      this.Require(signature.Result == IrType.Void || (signature.Result.IsStorable && !signature.Result.IsView && !OnHeap(signature.Result)), $"a function type returns the type {signature.Result}");
      this.Declared(signature.Result);
    }

    /// <summary>The checks of the nodes of function values.</summary>
    private void FunctionValue(IrExpr expression) {
      switch (expression) {
        case IrLambda lambda:
          this.Lambda(lambda);
          break;
        case IrFunctionRef reference:
          this.Require(reference.Type.IsFunction, $"the function '{reference.Function}' is used as a value of the type {reference.Type}");
          if (!functions.TryGetValue(reference.Function, out var target)) {
            this.Require(false, $"the function '{reference.Function}' used as a value does not exist");
            break;
          }

          this.Require(!this.Members.ContainsKey(reference.Function), $"the function '{reference.Function}' of a member is used as a value without its method");
          this.Require(reference.Type.Signature is { } signature && Matches(signature, target.Parameters.Select(parameter => parameter.Type), target.ReturnType),
            $"the function '{reference.Function}' is used as a value of the type {reference.Type}");
          break;
        case IrBoundMethod bound:
          this.Bound(bound);
          break;
        default:
          this.Invoke((IrInvoke)expression);
          break;
      }
    }

    /// <summary>Whether a signature takes exactly the given parameter types and returns exactly the given type.</summary>
    private static bool Matches(IrFunctionType signature, IEnumerable<IrType> parameters, IrType result)
      => signature.Result == result && signature.Parameters.SequenceEqual(parameters);

    /// <summary>
    /// A lambda: its type is the signature of its parameters and result, each capture is a variable in
    /// scope where the lambda stands and none of its own, and its body is checked as a function of its
    /// own that sees its captures, its parameters and its locals only. No loop and no switch around the
    /// lambda is one a <c>break</c> or a <c>continue</c> in it could leave.
    /// </summary>
    private void Lambda(IrLambda lambda) {
      if (lambda.Type.Signature is not { } signature) {
        this.Require(false, $"a lambda of the type {lambda.Type}");
        return;
      }

      this.Require(Matches(signature, lambda.Parameters.Select(parameter => parameter.Type), signature.Result), $"a lambda of the type {lambda.Type} takes ({string.Join(", ", lambda.Parameters.Select(parameter => parameter.Type))})");
      var own = lambda.Parameters.Concat(lambda.Locals).ToHashSet();
      foreach (var capture in lambda.Captures) {
        this.Require(this._visible.Contains(capture), $"the lambda captures '{capture.Name}', which is not in scope where it stands");
        this.Require(!own.Contains(capture), $"the lambda captures '{capture.Name}', which it declares itself");
      }

      this.Require(lambda.Captures.Distinct().Count() == lambda.Captures.Count, "a lambda captures a variable twice");
      var (visible, locals, result, loops, breakable) = (this._visible.ToHashSet(), this._locals, this._returnType, this._loops, this._breakable);
      this._visible.Clear();
      this._visible.UnionWith(lambda.Captures);
      foreach (var parameter in lambda.Parameters) {
        this.Require(parameter.Type.IsStorable && !parameter.Type.IsView, $"parameter '{parameter.Name}' of a lambda has the type {parameter.Type}");
        this.Require(this._visible.Add(parameter), $"parameter '{parameter.Name}' of a lambda is declared twice");
      }

      foreach (var local in lambda.Locals) {
        this.Require(!OnHeap(local.Type), Stored);
        this.Require(local.Type.IsStorable, $"local '{local.Name}' of a lambda has the type {local.Type}");
        this.Declared(local.Type);
      }

      (this._locals, this._returnType, this._loops, this._breakable) = (lambda.Locals, signature.Result, 0, 0);
      this.Block(lambda.Body);

      this._visible.Clear();
      this._visible.UnionWith(visible);
      (this._locals, this._returnType, this._loops, this._breakable) = (locals, result, loops, breakable);
    }

    /// <summary>A method as a value: of a class of the module, with a receiver exactly for an instance method, and of the method's signature after it.</summary>
    private void Bound(IrBoundMethod bound) {
      if (bound.Receiver is { } receiver)
        this.Expression(receiver);

      var method = bound.Method;
      IrTypeDeclaration? owner = types.Classes.FirstOrDefault(type => type.Methods.Contains(method));
      owner ??= types.Interfaces.FirstOrDefault(type => type.Methods.Contains(method));
      if (owner is null || !functions.TryGetValue(method.Function, out var body)) {
        this.Require(false, $"the method '{method.Name}' bound as a value is not one of a class of the module");
        return;
      }

      this.Require(method.IsStatic == bound.Receiver is null, $"the method '{method.Name}' is bound {(bound.Receiver is null ? "without" : "with")} a receiver");
      this.Require(method.Kind == IrMethodKind.Method, $"the accessor '{method.Name}' is bound as a value");
      var reference = owner is IrClassType type ? IrType.Object(type) : IrType.Object((IrInterfaceType)owner);
      if (bound.Receiver is { } target)
        this.Require(target.Type.IsAssignableTo(reference), $"the method '{method.Name}' of {owner.Name} is bound to the type {target.Type}");

      this.Require(bound.Kind switch {
        IrMethodCallKind.Direct => !method.IsVirtual,
        IrMethodCallKind.Virtual => method.IsVirtual && owner is IrClassType,
        IrMethodCallKind.Interface => owner is IrInterfaceType,
        _ => false
      }, $"the method '{method.Name}' is bound as a {bound.Kind} call it cannot be");
      this.Require(bound.Type.Signature is { } signature && Matches(signature, body.Parameters.Skip(method.IsStatic ? 0 : 1).Select(parameter => parameter.Type), body.ReturnType),
        $"the method '{method.Name}' is bound as a value of the type {bound.Type}");
    }

    /// <summary>A call of a function value: one argument per parameter of its signature, each of the parameter's type, and the result its result.</summary>
    private void Invoke(IrInvoke invocation) {
      this.Expression(invocation.Callee);
      foreach (var argument in invocation.Arguments)
        this.Expression(argument);

      if (invocation.Callee.Type.Signature is not { } signature) {
        this.Require(false, $"a value of the type {invocation.Callee.Type} is called");
        return;
      }

      this.Require(invocation.Arguments.Count == signature.Parameters.Count, $"a function of the type {invocation.Callee.Type} is called with {invocation.Arguments.Count} arguments");
      this.Require(invocation.Arguments.Zip(signature.Parameters).All(pair => pair.First.Type.IsAssignableTo(pair.Second)),
        $"a function of the type {invocation.Callee.Type} is called with ({string.Join(", ", invocation.Arguments.Select(argument => argument.Type))})");
      this.Require(invocation.Type == signature.Result, $"the call of a function of the type {invocation.Callee.Type} has the type {invocation.Type}");
    }

    /// <summary>The variables a loop gives every pass a variable of its own: declared before it, each once.</summary>
    private void PerIteration(IrFor loop) {
      foreach (var variable in loop.PerIteration)
        this.InScope(variable);

      this.Require(loop.PerIteration.Distinct().Count() == loop.PerIteration.Count, "a for lists a variable of every pass twice");
    }

  }

}
