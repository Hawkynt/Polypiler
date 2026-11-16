using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The Rust type of an exception the backend propagates: the shared object of <c>PlyError</c>.</summary>
  private const string ErrorType = "Rc<PlyError>";

  private sealed partial class Writer {

    /// <summary>The exception classes of the module, the library's first, <c>Exception</c> at 0: the index is the class an exception carries.</summary>
    private List<IrClassType> _errorClasses = [];

    /// <summary>The functions that let an exception of the module out: they return <c>Result&lt;T, Rc&lt;PlyError&gt;&gt;</c>.</summary>
    private readonly HashSet<string> _throwing = new(StringComparer.Ordinal);

    /// <summary>The bodies of <c>try</c> statements around the statement being written, innermost last.</summary>
    private readonly List<TryFrame> _tries = [];

    /// <summary>The exceptions the catch clauses around the statement being written hold, innermost last: the name of the binding.</summary>
    private readonly List<string> _caught = [];

    /// <summary>The functions of the module known to trap in no way; computed on demand.</summary>
    private readonly Dictionary<string, bool> _trapFree = new(StringComparer.Ordinal);

    /// <summary>
    /// The labelled block a <c>try</c> body is: an exception thrown in it leaves the block with its
    /// value, <c>break 't0 Err(e)</c> where the body can also complete (<see cref="Wrapped"/>), else
    /// <c>break 't0 e</c>; in the body of a <c>finally</c> a <c>return</c> leaves it with the value.
    /// </summary>
    private sealed class TryFrame(string label, bool wrapped, bool returns, int breakables) {

      public string Label { get; } = label;

      public bool Wrapped { get; } = wrapped;

      /// <summary>Whether a <c>return</c> leaves the block with its value, for a <c>finally</c> to run first.</summary>
      public bool Returns { get; } = returns;

      /// <summary>The loops around the <c>try</c>, which a jump out of the body would cross.</summary>
      public int Breakables { get; } = breakables;

      public bool Used { get; set; }

    }

    /// <summary>Records that an exception region cannot be written (<c>PLY1560</c>), naming why, and returns a stand-in.</summary>
    private RsExpression RefuseException(string what) {
      if (this._refused.Add(what))
        this._problems.Add(ExceptionRegionDescriptor.At($"exception region in {this._construct} cannot be emitted under the trap policy '{(this.Results ? "result" : "panic")}': '{what}' is not supported by the Rust backend"));

      return new RsPath("_refused");
    }

    /// <summary>Whether a type is an exception: a class that derives from <c>Exception</c>.</summary>
    private static bool IsError(IrType type) => type.ClassType is { } declared && IrExceptionClass.IsException(declared);

    /// <summary>Whether a throw of an exception class propagates: one of the module. A throw of a class of the library is a trap, a panic, as the library helpers throw them.</summary>
    private static bool IsCatchable(IrType type) => type.ClassType is { IsLibrary: false } declared && IrExceptionClass.IsException(declared);

    /// <summary>
    /// Prepares the exceptions of a module: the classes, numbered with their bases; the constructors of
    /// the module's exception classes, which are not written (an exception is a <c>PlyError</c>); and
    /// the functions that let an exception out, a fixed point over the calls.
    /// </summary>
    private void Exceptions(IrModule module) {
      this._throwing.UnionWith(this._declared);
      var own = module.Types.OfType<IrClassType>().Where(type => !type.IsLibrary && IrExceptionClass.IsException(type)).ToList();
      if (own.Count == 0 && !module.Functions.Any(function => IrTree.Nodes(function.Body).Any(node => node is IrFieldLoad load && IsError(load.Target.Type)))
        && !(this.Results && module.Functions.Any(function => IrClosures.Within(function.Body).Any(statement => statement is IrTry { Catches.Count: > 0 }))))
        return;

      this._errorClasses = [.. IrExceptionClass.All, .. own];
      foreach (var type in own) {
        if (type.Methods.Count > 0 || type.Properties.Count > 0 || type.Fields.Count > 0)
          this.RefuseException($"an exception class with members of its own ({type.Name})");
        foreach (var constructor in type.Constructors)
          this._bodiless.Add(constructor.Function);
      }

      var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      for (var changed = true; changed;) {
        changed = false;
        foreach (var function in module.Functions.Where(function => (this.Results || !function.IsPublic) && !this._bodiless.Contains(function.Name) && !this._throwing.Contains(function.Name)))
          if (this.Escapes(function.Body.Statements, [], null))
            changed |= this._throwing.Add(function.Name);
      }

      foreach (var function in module.Functions.Where(function => !this._bodiless.Contains(function.Name))) {
        this._construct = $"the function '{function.Name}'";
        if (Lambdas(function.Body).Any(lambda => IrTree.Nodes(lambda.Body).Any(node => node is IrThrowExpression thrown && IsCatchable(thrown.Exception.Type) || node is IrCall call && this._throwing.Contains(call.Function))
          || IrClosures.Within(lambda.Body).Any(statement => statement is IrThrow thrown && IsCatchable(thrown.Exception.Type) || statement is IrTry)))
          this.RefuseException("an exception thrown inside a closure");
        if (!this.Results && function.IsPublic && this.Escapes(function.Body.Statements, [], null)
          && module.Functions.Any(caller => IrTree.Nodes(caller.Body).Any(node => node is IrCall call && call.Function == function.Name)))
          this.RefuseException("a public function that lets an exception out, called inside the module");
      }
    }

    /// <summary>Whether statements let an exception out of the catch clauses around them: a throw, a rethrow or a call that propagates, none catching it unfiltered.</summary>
    private bool Escapes(IEnumerable<IrStmt> statements, IReadOnlyList<IReadOnlyList<IrCatch>> covering, IrClassType? caught) {
      bool Covered(IrClassType thrown) => covering.Any(catches => catches.Any(clause => clause.Filter is null && (thrown == clause.Class || thrown.DerivesFrom(clause.Class))));
      foreach (var statement in statements) {
        switch (statement) {
          case IrTry attempt:
            if (this.Escapes(attempt.Body.Statements, [.. covering, attempt.Catches], caught)
              || attempt.Catches.Any(clause => this.Escapes(clause.Body.Statements, covering, clause.Class))
              || attempt.Finally is { } final && this.Escapes(final.Statements, covering, caught))
              return true;
            continue;
          case IrThrow throwing when IsCatchable(throwing.Exception.Type) && !Covered(throwing.Exception.Type.ClassType!):
            return true;
          case IrRethrow when caught is null || !Covered(caught):
            return true;
        }

        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
          if (node is IrThrowExpression thrown && IsCatchable(thrown.Exception.Type) && !Covered(thrown.Exception.Type.ClassType!)
            || node is IrCall call && this._throwing.Contains(call.Function) && !Covered(IrExceptionClass.Exception))
            return true;
        }

        var inner = statement is IrBlock block ? [block] : IrTree.Blocks(statement);
        if (inner.Any(nested => this.Escapes(nested.Statements, covering, caught)))
          return true;
      }

      return false;
    }

    /// <summary>The number of an exception class, registered where it is no class of the module's table yet.</summary>
    private int ErrorClass(IrClassType type) {
      var index = this._errorClasses.IndexOf(type);
      return index >= 0 ? index : ((Func<int>)(() => { this.RefuseException($"the exception class {type.Name}"); return 0; }))();
    }

    /// <summary>
    /// The items of the exceptions, where the module has any: <c>PlyError</c>, the class, message and
    /// inner exception of an exception; the base and the name of every class, by number.
    /// </summary>
    private IEnumerable<RsItem> ErrorItems() {
      if (this._errorClasses.Count == 0)
        yield break;

      this._uses.Add("std::rc::Rc");
      var count = this._errorClasses.Count.ToString(CultureInfo.InvariantCulture);
      yield return new RsStruct("PlyError", "pub", [new RsFieldDeclaration("class", "pub", "usize"), new RsFieldDeclaration("message", "pub", OwnedString), new RsFieldDeclaration("inner", "pub", $"Option<{ErrorType}>")]) {
        Docs = ["An exception: the number of its class, its message and the exception it wraps."]
      };
      yield return new RsConst("PLY_ERROR_BASES", "pub", $"[usize; {count}]",
        new RsArray([.. this._errorClasses.Select(type => (RsExpression)new RsLiteral((type.Base is { } parent && this._errorClasses.Contains(parent) ? this._errorClasses.IndexOf(parent) : 0).ToString(CultureInfo.InvariantCulture)))])) { IsStatic = true };
      yield return new RsConst("PLY_ERROR_NAMES", "pub", $"[&str; {count}]", new RsArray([.. this._errorClasses.Select(type => (RsExpression)new RsString(type.Name))])) { IsStatic = true };
    }

    /// <summary>
    /// A new exception: <c>ply_error(class, message, inner)</c>, its message and inner exception those its
    /// constructor hands down its chain to the library's <c>Exception</c> constructor - each argument
    /// passed on in its order, once; the default message of the class where none is.
    /// </summary>
    private RsExpression ErrorNew(IrNew creation) {
      var actual = creation.Type.ClassType!;
      var constructor = creation.Constructor;
      IReadOnlyList<IrExpr> arguments = creation.Arguments;
      for (var depth = 0; !IrExceptionClass.IsLibrary(constructor); ++depth) {
        var function = this._module.Functions.FirstOrDefault(candidate => candidate.Name == constructor.Function);
        if (depth > 16 || function is not { Body.Statements: [IrExprStmt { Expression: IrCall { Arguments.Count: > 0 } call }] } || call.Arguments[0] is not IrLocalRef self || self.Local != function.Parameters[0])
          return this.RefuseException("an exception constructor that does more than hand its arguments to its base");

        var handed = call.Arguments.Skip(1).ToList();

        var parameters = function.Parameters.Skip(1).ToList();
        var mapped = new List<IrExpr>();
        foreach (var argument in handed) {
          if (argument is IrLocalRef { Local: var local } && parameters.IndexOf(local) is var position and >= 0)
            mapped.Add(arguments[position]);
          else if (argument is IrConst or IrStrConst or IrNull)
            mapped.Add(argument);
          else
            return this.RefuseException("an exception constructor that does more than hand its arguments to its base");
        }

        if (!handed.OfType<IrLocalRef>().Select(reference => parameters.IndexOf(reference.Local)).SequenceEqual(Enumerable.Range(0, parameters.Count)))
          return this.RefuseException("an exception constructor that does more than hand its arguments to its base");

        constructor = IrExceptionClass.ConstructorOf(call.Function)
          ?? this._module.Types.OfType<IrClassType>().SelectMany(type => type.Constructors).FirstOrDefault(candidate => candidate.Function == call.Function)
          ?? constructor;
        if (constructor.Function != call.Function)
          return this.RefuseException("an exception constructor that does more than hand its arguments to its base");

        arguments = mapped;
      }

      var library = IrExceptionClass.All.First(type => constructor.Function.StartsWith(type.Name + "..ctor", StringComparison.Ordinal));
      var message = arguments.Count > 0 ? this.StringValue(arguments[0])
        : this.Utf16(IrExceptionClass.DefaultMessage(library == IrExceptionClass.Exception ? actual : library));
      var inner = arguments.Count > 1 ? this.ErrorOption(arguments[1]) : new RsPath("None");
      return new RsCall(new RsPath(this.StringHelper("ply_error")), [new RsLiteral(this.ErrorClass(actual).ToString(CultureInfo.InvariantCulture)), message, inner]) { Type = creation.Type };
    }

    /// <summary>An exception where a place owns it: a new one, or a variable's shared with <c>Rc::clone</c>.</summary>
    private RsExpression ErrorValue(IrExpr value) => value switch {
      IrNew creation => this.ErrorNew(creation),
      IrLocalRef reference => this.Cloned(new RsReference(false, new RsPath(this._locals[reference.Local]) { Type = value.Type })),
      _ => this.RefuseException("an exception of the form " + value.GetType().Name[2..])
    };

    /// <summary>An exception that may be none, as an inner exception: <c>None</c>, a field's <c>Option</c>, any other in <c>Some</c>.</summary>
    private RsExpression ErrorOption(IrExpr value) => value switch {
      IrNull => new RsPath("None"),
      IrFieldLoad { Field: var field } load when field == IrExceptionClass.InnerException => this.ErrorField(load),
      _ => new RsCall(new RsPath("Some"), [this.ErrorValue(value)])
    };

    /// <summary>The message or the inner exception of an exception: <c>e.message.clone()</c>, <c>e.inner.clone()</c>, through an inner exception that may be none <c>ply_non_null(&amp;e.inner)</c>.</summary>
    private RsExpression ErrorField(IrFieldLoad load) {
      RsExpression target = load.Target switch {
        IrFieldLoad { Field: var field } inner when field == IrExceptionClass.InnerException
          => this.NonNull(new RsField(this.ErrorPlace(inner.Target), "inner")),
        var other => this.ErrorPlace(other)
      };
      var name = load.Field == IrExceptionClass.Message ? "message" : load.Field == IrExceptionClass.InnerException ? "inner" : ((RsPath)this.RefuseException($"the field {load.Field.Name} of an exception")).Name;
      return new RsMethodCall(new RsField(target, name), "clone", []) { Type = load.Type };
    }

    private RsExpression ErrorPlace(IrExpr target) => target switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = target.Type },
      IrNew creation => this.Operand(this.ErrorNew(creation), 15),
      _ => this.RefuseException("an exception of the form " + target.GetType().Name[2..])
    };

    /// <summary>
    /// An exception leaving where it is thrown: the block of the innermost <c>try</c> body,
    /// <c>break 't0 Err(e)</c>; else the function, <c>return Err(e)</c>; else, from a public function,
    /// the panic of an unhandled exception, <c>ply_unhandled(&amp;e)</c>.
    /// </summary>
    private RsExpression Escape(RsExpression error) {
      if (this._tries.LastOrDefault(frame => !frame.Returns) is { } frame && this._tries.IndexOf(frame) == this._tries.Count - 1) {
        frame.Used = true;
        return new RsBreak(frame.Label, frame.Wrapped ? new RsCall(new RsPath("Err"), [error]) : error);
      }

      if (this._tries.Count > 0)
        return this.RefuseException("an exception thrown inside a finally's body");
      if (this.Results)
        this._escaping.Add(this._function.Name);
      if (this._throwing.Contains(this._function.Name) || this.Results)
        return new RsReturn(new RsCall(new RsPath("Err"), [error]));

      return new RsCall(new RsPath(this.StringHelper("ply_unhandled")), [new RsReference(false, error)]);
    }

    /// <summary>The call of a function that propagates: inside a <c>try</c> body a <c>match</c> that leaves the body with the error, else <c>f(x)?</c>, from a public function the panic of an unhandled one.</summary>
    private RsExpression Propagated(RsExpression call) {
      if (this._spelling)
        return new RsTry(this.Operand(call, 15)) { Type = call.Type };
      if (this.Results && this._closureResults.Count > 0)
        return this.LimitResult("a call that may trap inside a closure");
      if (this._tries.Count > 0) {
        var frame = this._tries[^1];
        if (frame.Returns)
          return this.RefuseException("an exception thrown inside a finally's body");

        frame.Used = true;
        return new RsMatch(call, [
          new RsMatchArm(["Ok(value)"], new RsPath("value")),
          new RsMatchArm(["Err(error)"], new RsBreak(frame.Label, frame.Wrapped ? new RsCall(new RsPath("Err"), [new RsPath("error")]) : new RsPath("error")))
        ]) { Type = call.Type };
      }

      if (this.Results)
        this._escaping.Add(this._function.Name);
      if (this._throwing.Contains(this._function.Name) || this.Results)
        return new RsTry(this.Operand(call, 15)) { Type = call.Type };

      return new RsMethodCall(this.Operand(call, 15), "unwrap_or_else", [new RsClosure(false, [new RsParameter("error", "")], new RsCall(new RsPath(this.StringHelper("ply_unhandled")), [new RsReference(false, new RsPath("error"))]))]) { Type = call.Type };
    }

    /// <summary>The result type of a function that propagates: <c>Result&lt;T, Rc&lt;PlyError&gt;&gt;</c>.</summary>
    private static string Fallible(string? result, string error = ErrorType) => $"Result<{result ?? "()"}, {error}>";

    /// <summary>Whether a block can complete normally, conservatively: one whose last statement throws or returns, on every path, cannot.</summary>
    private static bool CanComplete(IrBlock block) => block.Statements.Count == 0 || block.Statements[^1] switch {
      IrReturn or IrThrow or IrRethrow => false,
      IrExprStmt { Expression: IrThrowExpression } => false,
      IrBlock nested => CanComplete(nested),
      IrIf { Else: { } otherwise } choice => CanComplete(choice.Then) || CanComplete(otherwise),
      IrTry { Finally: null } attempt => CanComplete(attempt.Body) || attempt.Catches.Any(clause => CanComplete(clause.Body)),
      _ => true
    };

    /// <summary>
    /// A <c>try</c> with catch clauses, its body a labelled block that gives the exception, its clauses
    /// an <c>if</c> chain over the class - <c>ply_is(error.class, 1)</c> - and the filter, the exception
    /// going on where none takes it. A clause of a class a trap belongs to stands only around a body that
    /// cannot trap, since a trap is a panic no clause sees; a filter only where no <c>finally</c> lies
    /// between the throw and the clause, which .NET would run after the filter.
    /// </summary>
    private void Try(IrTry attempt, List<RsStatement> into) {
      if (attempt.Finally is { } final) {
        if (attempt.Catches.Count > 0)
          into.Add(new RsExpressionStatement(this.RefuseException("a try with both catch clauses and a finally block")));
        else
          this.Finally(attempt.Body, final, into);
        return;
      }

      var trapped = Enum.GetValues<IrTrap>().Select(IrExceptionClass.Of).Distinct().ToList();
      foreach (var clause in attempt.Catches) {
        if (!this.Results && trapped.Any(type => type == clause.Class || type.DerivesFrom(clause.Class)) && !this.TrapFree(attempt.Body))
          this.RefuseException("a catch of a class a trap belongs to, around code that may trap");
        if (clause.Filter is { } filter && (!this.TrapFree(filter) || IrClosures.Within(attempt.Body).Any(statement => statement is IrTry { Finally: not null })
            || IrTree.Nodes(attempt.Body).Any(node => node is IrCall call && this._throwing.Contains(call.Function))))
          this.RefuseException("an exception filter that .NET runs before the finally blocks the exception leaves, or that may trap");
      }

      var completes = CanComplete(attempt.Body);
      var frame = new TryFrame(this.NewLabel("t"), completes, false, this._breakables.Count);
      this._tries.Add(frame);
      var body = this.Block(attempt.Body.Statements);
      this._tries.RemoveAt(this._tries.Count - 1);
      if (!frame.Used) {
        // Nothing in the body throws: no clause can run.
        into.AddRange(body.Statements);
        return;
      }

      if (completes && !(body.Statements.Count > 0 && Diverges(body.Statements[^1])))
        body = body with { Tail = new RsCall(new RsPath("Ok"), [new RsTuple([])]) };

      var error = this.Temporary("error");
      // The block's last break out of itself is its value; a block nothing else leaves needs no label (clippy's diverging_sub_expression).
      if (body is { Tail: null, Statements: [.., RsExpressionStatement { Expression: RsBreak { Value: { } last } exit }] } && exit.Label == frame.Label)
        body = new RsBlock([.. body.Statements.Take(body.Statements.Count - 1)], last);
      RsExpression outcome = RustSyntaxRewriter.DescendantsAndSelf(body).Any(node => node is RsBreak other && other.Label == frame.Label) ? new RsLabeledBlock(frame.Label, body)
        : body.Statements.Count == 0 && body.Tail is { } only ? only
        : new RsBlockExpression(body);
      var dispatch = this.Dispatch(attempt.Catches, error);
      if (!completes) {
        into.Add(new RsLet(error, false, ErrorType, outcome));
        into.AddRange(dispatch.Statements);
        return;
      }

      var result = this.Temporary("outcome");
      into.Add(new RsLet(result, false, Fallible(null), outcome));
      into.Add(new RsExpressionStatement(new RsIf(new RsLetCondition($"Err({error})", new RsPath(result)), dispatch, null)));
    }

    /// <summary>The catch clauses over an exception bound to a name: an <c>if</c> chain, the exception going on where no clause takes it.</summary>
    private RsBlock Dispatch(IReadOnlyList<IrCatch> catches, string error) {
      RsExpression? chain = null;
      var rest = new RsBlock([new RsExpressionStatement(this.Escape(new RsPath(error)))], null);
      foreach (var clause in catches.Reverse()) {
        if (clause.Local is { } local)
          this._locals[local] = error;

        RsExpression? test = this.ErrorClass(clause.Class) == 0 ? null
          : new RsCall(new RsPath(this.StringHelper("ply_is")), [new RsField(new RsPath(error), "class"), new RsLiteral(this.ErrorClass(clause.Class).ToString(CultureInfo.InvariantCulture))]) { Type = IrType.Bool };
        if (clause.Filter is { } filter)
          test = test is null ? this.Expression(filter) : this.Binary(test, "&&", this.Expression(filter), IrType.Bool);

        this._caught.Add(error);
        var body = this.Block(clause.Body.Statements);
        this._caught.RemoveAt(this._caught.Count - 1);
        if (test is null) {
          // A clause that takes every exception: what follows it never runs.
          rest = body;
          chain = null;
          continue;
        }

        chain = new RsIf(test, body, chain ?? (rest.Statements.Count == 0 && rest.Tail is null ? null : new RsBlockExpression(rest)));
        rest = new RsBlock([new RsExpressionStatement(chain)], null);
      }

      return chain is null ? rest : new RsBlock([new RsExpressionStatement(chain)], null);
    }

    /// <summary>
    /// A <c>try</c> with only a <c>finally</c>, around a body that always returns and throws nothing:
    /// the body a labelled block its <c>return</c> leaves with the value, then the <c>finally</c>, then
    /// the return. Any other <c>finally</c> is refused.
    /// </summary>
    private void Finally(IrBlock body, IrBlock final, List<RsStatement> into) {
      if (CanComplete(body) || this.Escapes(body.Statements, [], null)) {
        into.Add(new RsExpressionStatement(this.RefuseException("a finally block around a body that may throw or complete")));
        return;
      }

      var frame = new TryFrame(this.NewLabel("f"), false, true, this._breakables.Count);
      this._tries.Add(frame);
      var written = this.Block(body.Statements);
      this._tries.RemoveAt(this._tries.Count - 1);
      var value = this.Temporary("value");
      var returns = this._function.ReturnType != IrType.Void;
      var block = new RsLabeledBlock(frame.Label, written);
      into.Add(returns ? new RsLet(value, false, null, block) : new RsExpressionStatement(block));
      this.Statements(final.Statements, into);
      into.Add(new RsExpressionStatement(this.Returned(returns ? new RsPath(value) : null)));
    }

    /// <summary><c>return v</c>; in a function that propagates <c>return Ok(v)</c>.</summary>
    private RsReturn Returned(RsExpression? value) {
      if (!this._throwing.Contains(this._function.Name) || this._closureResults.Count > 0)
        return new RsReturn(value);

      // A value whose error is handed on is the result itself: return f(x), not return Ok(f(x)?) (clippy's needless_question_mark).
      return new RsReturn(value is RsTry handed ? handed.Operand : new RsCall(new RsPath("Ok"), [value ?? new RsTuple([])]));
    }

    /// <summary>A <c>return</c> inside the body of a <c>finally</c>: <c>break 'f0 value</c>, so that the <c>finally</c> runs first.</summary>
    private RsExpression? FinallyReturn(RsExpression? value) {
      if (this._tries.Count == 0 || !this._tries[^1].Returns)
        return null;

      this._tries[^1].Used = true;
      return new RsBreak(this._tries[^1].Label, value);
    }

    /// <summary>Whether a statement or an expression traps in no way: what a catch of a class of a trap may stand around.</summary>
    private bool TrapFree(IrBlock block) => IrClosures.Within(block).All(this.TrapFree);

    private bool TrapFree(IrStmt statement) => statement switch {
      IrBlock or IrIf or IrWhile or IrDoWhile or IrFor or IrDeclare or IrAssign or IrExprStmt or IrReturn or IrRethrow or IrTry or IrBreak or IrContinue
        => IrTree.Expressions(statement).All(this.TrapFree),
      IrThrow thrown => IsCatchable(thrown.Exception.Type) && this.TrapFree(thrown.Exception),
      IrGlobalAssign { Global: var global } assign => !IsLocked(global) && this.TrapFree(assign.Value),
      _ => false
    };

    private bool TrapFree(IrExpr expression) => expression switch {
      IrConst or IrLocalRef or IrStrConst or IrNull or IrLambda => true,
      IrGlobalRef { Global: var global } => !IsLocked(global),
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when binary.Type.IsInteger && binary.Right is not IrConst { Value: not 0 and not -1 } => false,
      IrBinary { Op: IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked } => false,
      IrBinary binary => this.TrapFree(binary.Left) && this.TrapFree(binary.Right),
      IrUnary { Op: IrUnaryOp.LogicalNot or IrUnaryOp.BitNot or IrUnaryOp.Negate } unary => this.TrapFree(unary.Operand),
      IrConvert { Contract: not IrConversion.Trap } convert => this.TrapFree(convert.Operand),
      IrConditional choice => this.TrapFree(choice.Condition) && this.TrapFree(choice.WhenTrue) && this.TrapFree(choice.WhenFalse),
      IrNew creation when IsCatchable(creation.Type) => creation.Arguments.All(this.TrapFree),
      IrThrowExpression thrown => IsCatchable(thrown.Exception.Type) && this.TrapFree(thrown.Exception),
      IrFieldLoad { Target: IrLocalRef, Field: var field } when field == IrExceptionClass.Message => true,
      IrIntrinsic { Op: IrIntrinsicOp.StrConcat or IrIntrinsicOp.StrEquals or IrIntrinsicOp.StrLength or IrIntrinsicOp.StrFromCodeUnit } operation => operation.Arguments.All(this.TrapFree),
      IrCall call => call.Arguments.All(this.TrapFree) && this.TrapFreeFunction(call.Function),
      _ => false
    };

    /// <summary>Whether a function of the module traps in no way, a recursive one taken to trap.</summary>
    private bool TrapFreeFunction(string name) {
      if (this._trapFree.TryGetValue(name, out var known))
        return known;

      this._trapFree[name] = false;
      var function = this._module.Functions.FirstOrDefault(candidate => candidate.Name == name);
      var free = function is not null && !this._members.ContainsKey(name) && this.TrapFree(function.Body);
      this._trapFree[name] = free;
      return free;
    }

  }

}
