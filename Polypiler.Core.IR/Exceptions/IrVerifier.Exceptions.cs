using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>How many catch clauses the statement checked stands in, which a rethrow needs one of.</summary>
    private int _catches;

    /// <summary>How many finally blocks the statement checked stands in, which no return leaves.</summary>
    private int _finally;

    /// <summary>The object of a throw: a reference to an exception class.</summary>
    private void Thrown(IrExpr exception) {
      this.Expression(exception);
      this.Require(exception.Type.ClassType is { } type && IrExceptionClass.IsException(type), $"an object of the type {exception.Type}, which is no exception class, is thrown");
    }

    /// <summary>
    /// A try: a catch clause or a finally block; each clause catches an exception class into a local
    /// of its type, which its filter and its body see, under a filter that is a truth value; a rethrow
    /// stands in a catch clause; nothing jumps out of a finally block.
    /// </summary>
    private void Try(IrTry attempt) {
      this.Require(attempt.Catches.Count > 0 || attempt.Finally is not null, "a try without a catch clause and without a finally block");
      this.Block(attempt.Body);
      foreach (var clause in attempt.Catches) {
        this.Require(IrExceptionClass.IsException(clause.Class), $"a catch clause catches the class {clause.Class.Name}, which is no exception class");
        this.Declared(IrType.Object(clause.Class));
        var local = clause.Local;
        if (local is not null) {
          this.Require(function.Locals.Contains(local), $"local '{local.Name}' is not among the locals of the function");
          this.Require(local.Type == IrType.Object(clause.Class), $"the local '{local.Name}' of a catch clause has the type {local.Type}, not that of {clause.Class.Name}");
          this.Require(this._visible.Add(local), $"local '{local.Name}' is declared twice");
        }

        if (clause.Filter is not null)
          this.Value(clause.Filter, IrType.Bool, "the filter of a catch clause");

        ++this._catches;
        this.Block(clause.Body);
        --this._catches;
        if (local is not null)
          this._visible.Remove(local);
      }

      if (attempt.Finally is not { } final)
        return;

      // A finally block is left at its end only: a break or a continue inside it may leave only a loop inside it.
      var (loops, breakable) = (this._loops, this._breakable);
      (this._loops, this._breakable) = (0, 0);
      ++this._finally;
      this.Block(final);
      --this._finally;
      (this._loops, this._breakable) = (loops, breakable);
    }

    /// <summary>The statements of exception handling; <see langword="false"/> for any other.</summary>
    private bool ExceptionStatement(IrStmt statement) {
      switch (statement) {
        case IrThrow thrown:
          this.Thrown(thrown.Exception);
          return true;
        case IrRethrow:
          this.Require(this._catches > 0, "a rethrow outside a catch clause");
          return true;
        case IrTry attempt:
          this.Try(attempt);
          return true;
        default:
          return false;
      }
    }

    /// <summary>A throw where a value is expected: of a type a value may have.</summary>
    private void ThrowExpression(IrThrowExpression thrown) {
      this.Thrown(thrown.Exception);
      this.Require(thrown.Type.IsStorable, $"a throw stands for a value of the type {thrown.Type}");
      this.Declared(thrown.Type);
    }

    /// <summary>
    /// A new object of a library exception class (<see cref="IrExceptionClass"/>), whose constructors are
    /// no functions of the module: its arguments match the parameters of the constructor.
    /// </summary>
    private void LibraryNew(IrNew creation, IrClassType created) {
      this.Require(created.Constructors.Contains(creation.Constructor), $"a constructor that is not one of {created.Name} makes an object of it");
      var parameters = IrExceptionClass.Parameters(creation.Constructor) ?? [];
      this.Require(creation.Arguments.Count == parameters.Count && creation.Arguments.Zip(parameters).All(pair => pair.First.Type.IsAssignableTo(pair.Second)),
        $"the arguments of the constructor of {created.Name} do not match its parameters");
    }

    /// <summary>
    /// A call by name of a constructor of a library exception class: only as the first statement of a
    /// constructor of a class derived from it, on the new object, with arguments of its parameters.
    /// </summary>
    private bool LibraryCall(IrCall call, bool chains) {
      if (IrExceptionClass.ConstructorOf(call.Function) is not { } constructor)
        return false;

      var owner = IrExceptionClass.All.First(type => type.Constructors.Contains(constructor));
      var parameters = IrExceptionClass.Parameters(constructor)!;
      this.Require(chains && this.Self is { IsConstructor: true } self && self.Owner.Base == owner && call.Arguments is [IrLocalRef { Local: var receiver }, ..] && receiver == this.Receiver,
        $"the constructor '{call.Function}' of a library class is called other than first by a constructor of a class derived from it");
      this.Require(call.Type == IrType.Void && call.Arguments.Count == parameters.Count + 1
        && call.Arguments.Skip(1).Zip(parameters).All(pair => pair.First.Type.IsAssignableTo(pair.Second)),
        $"the arguments of the call of '{call.Function}' do not match its parameters");
      return true;
    }

  }

}
