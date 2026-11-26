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

    /// <summary>The functions of the module that may run a <c>finally</c> block while an exception leaves them; computed on demand.</summary>
    private readonly Dictionary<string, bool> _runsFinally = new(StringComparer.Ordinal);

    /// <summary>The throws of an exception of a library class that a catch clause of the same function stands around: values, as the module's own exceptions are.</summary>
    private readonly HashSet<IrExpr> _coveredThrows = new(ReferenceEqualityComparer.Instance);

    /// <summary>The marks a way out of the body of a <c>try</c> with a <c>finally</c> carries until the form of that body is known.</summary>
    private const string ReturnedMark = "$returned", ThrownMark = "$thrown", JumpedMark = "$jumped";

    /// <summary>
    /// The labelled block a <c>try</c> body is. Of a <c>try</c> with catch clauses: an exception thrown
    /// in it leaves the block with its value, <c>break 't0 Err(e)</c> where the body can also complete
    /// (<see cref="Wrapped"/>), else <c>break 't0 e</c>. Of a <c>try</c> with a <c>finally</c>
    /// (<see cref="IsFinally"/>): every way out of the body - an exception, a <c>return</c>, a
    /// <c>break</c> or <c>continue</c> of a loop around it - leaves the block first, so that the
    /// <c>finally</c> runs, and is made after it.
    /// </summary>
    private sealed class TryFrame(string label, bool wrapped, int breakables) {

      public string Label { get; } = label;

      public bool Wrapped { get; } = wrapped;

      /// <summary>The loops around the <c>try</c>, which a jump out of the body would cross.</summary>
      public int Breakables { get; } = breakables;

      public bool Used { get; set; }

      /// <summary>Whether the block is the body of a <c>try</c> with a <c>finally</c>.</summary>
      public bool IsFinally { get; init; }

      /// <summary>
      /// The catch clauses of the <c>try</c> where one of them is chosen where the exception is thrown,
      /// before the <c>finally</c> blocks between run - .NET's first pass, which runs the filters - and
      /// the exception leaves the body with the number of the clause, <c>(e, 0)</c>; else <see langword="null"/>.
      /// </summary>
      public IReadOnlyList<IrCatch>? Selecting { get; init; }

      /// <summary>The jumps out of the body of a <c>try</c> with a <c>finally</c>: the loop or switch, and whether the jump is a <c>continue</c>.</summary>
      public List<(Breakable Target, bool Continues)> Jumps { get; } = [];

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

    /// <summary>Whether a thrown exception propagates as a value: one of a class of the module, or a new one of a class of the library that a catch clause of the same function stands around.</summary>
    private bool Propagates(IrExpr exception) => IsCatchable(exception.Type) || this._coveredThrows.Contains(exception);

    /// <summary>
    /// Records the throws of a new exception of a class of the library that a catch clause of that
    /// class or a base stands around in the same function, the bodies of lambdas left out.
    /// </summary>
    private void CoverThrows(IEnumerable<IrStmt> statements, List<IReadOnlyList<IrCatch>> covering) {
      foreach (var statement in statements) {
        if (statement is IrTry attempt) {
          covering.Add(attempt.Catches);
          this.CoverThrows(attempt.Body.Statements, covering);
          covering.RemoveAt(covering.Count - 1);
          foreach (var clause in attempt.Catches)
            this.CoverThrows(clause.Body.Statements, covering);
          if (attempt.Finally is { } final)
            this.CoverThrows(final.Statements, covering);
          continue;
        }

        var thrown = IrTree.Expressions(statement).SelectMany(Operations).OfType<IrThrowExpression>().Select(expression => expression.Exception);
        if (statement is IrThrow throwing)
          thrown = thrown.Prepend(throwing.Exception);
        foreach (var exception in thrown)
          if (exception is IrNew { Type.ClassType: { IsLibrary: true } type } && IrExceptionClass.IsException(type)
            && covering.Any(catches => catches.Any(clause => type == clause.Class || type.DerivesFrom(clause.Class))))
            this._coveredThrows.Add(exception);

        this.CoverThrows(statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements), covering);
      }
    }

    /// <summary>Every node of an expression, those of the bodies of its lambdas left out.</summary>
    private static IEnumerable<IrExpr> Operations(IrExpr expression) => IrTree.Operands(expression).SelectMany(Operations).Prepend(expression);

    /// <summary>
    /// Prepares the exceptions of a module: the classes, numbered with their bases; the constructors of
    /// the module's exception classes, which are not written (an exception is a <c>PlyError</c>); and
    /// the functions that let an exception out, a fixed point over the calls.
    /// </summary>
    private void Exceptions(IrModule module) {
      this._throwing.UnionWith(this._declared);
      foreach (var function in module.Functions)
        this.CoverThrows(function.Body.Statements, []);
      var own = module.Types.OfType<IrClassType>().Where(type => !type.IsLibrary && IrExceptionClass.IsException(type)).ToList();
      if (own.Count == 0 && this._coveredThrows.Count == 0 && !module.Functions.Any(function => IrTree.Nodes(function.Body).Any(node => node is IrFieldLoad load && IsError(load.Target.Type)))
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
          case IrThrow throwing when this.Propagates(throwing.Exception) && !Covered(throwing.Exception.Type.ClassType!):
            return true;
          case IrRethrow when caught is null || !Covered(caught):
            return true;
        }

        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
          if (node is IrThrowExpression thrown && this.Propagates(thrown.Exception) && !Covered(thrown.Exception.Type.ClassType!)
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
    /// the panic of an unhandled exception, <c>ply_unhandled(&amp;e)</c>. Where the handler it goes to
    /// chooses its clause where the exception is thrown (<see cref="TryFrame.Selecting"/>), the clause
    /// is chosen here, unless <paramref name="selected"/> says it has been already.
    /// </summary>
    private RsExpression Escape(RsExpression error, bool selected = false) {
      if (this._tries.Count > 0) {
        var frame = this._tries[^1];
        if (!selected && this._tries.LastOrDefault(candidate => !candidate.IsFinally) is { Selecting: { } clauses })
          return this.Selected(error, clauses, frame);

        return Leave(frame, error);
      }

      if (this.Results)
        this._escaping.Add(this._function.Name);
      if (this._throwing.Contains(this._function.Name) || this.Results)
        return new RsReturn(new RsCall(new RsPath("Err"), [error]));

      return new RsCall(new RsPath(this.StringHelper("ply_unhandled")), [new RsReference(false, error)]);
    }

    /// <summary>The exception, or the exception with its clause, leaving the block of a <c>try</c> body: in a <c>finally</c>'s, marked until the block's form is known.</summary>
    private static RsBreak Leave(TryFrame frame, RsExpression error) {
      frame.Used = true;
      if (frame.IsFinally)
        return new RsBreak(frame.Label, new RsCall(new RsPath(ThrownMark), [error]));

      return new RsBreak(frame.Label, frame.Wrapped ? new RsCall(new RsPath("Err"), [error]) : error);
    }

    /// <summary>
    /// An exception leaving for a handler that chooses its clause where the exception is thrown, as
    /// .NET's first pass does before any <c>finally</c> block runs: the exception bound, the number of
    /// the first clause whose class and filter take it - the count of the clauses where none does -
    /// and the pair leaving: <c>let selected = if ply_is(e.class, 13) &amp;&amp; f { 0 } else { 1 }; break 't0 (e, selected);</c>.
    /// </summary>
    private RsBlockExpression Selected(RsExpression error, IReadOnlyList<IrCatch> clauses, TryFrame frame) {
      var statements = new List<RsStatement>();
      string name;
      if (error is RsPath path) {
        name = path.Name;
      } else {
        name = this.Temporary("thrown");
        statements.Add(new RsLet(name, false, null, error));
      }

      RsExpression chain = new RsLiteral(clauses.Count.ToString(CultureInfo.InvariantCulture));
      for (var index = clauses.Count - 1; index >= 0; --index) {
        var clause = clauses[index];
        if (clause.Local is { } local)
          this._locals[local] = name;

        var number = new RsLiteral(index.ToString(CultureInfo.InvariantCulture));
        var test = this.ClauseTest(clause, name);
        chain = test is null ? number : new RsIf(test, new RsBlock([], number), chain is RsIf ? chain : new RsBlockExpression(new RsBlock([], chain)));
      }

      var selection = this.Temporary("selected");
      statements.Add(new RsLet(selection, false, null, chain));
      statements.Add(new RsExpressionStatement(Leave(frame, new RsTuple([new RsPath(name), new RsPath(selection)]))));
      return new RsBlockExpression(new RsBlock(statements, null));
    }

    /// <summary>Whether a catch clause takes an exception bound to a name: its class, <c>ply_is(e.class, 13)</c>, and its filter; <see langword="null"/> for a clause that takes every one.</summary>
    private RsExpression? ClauseTest(IrCatch clause, string error) {
      RsExpression? test = this.ErrorClass(clause.Class) == 0 ? null
        : new RsCall(new RsPath(this.StringHelper("ply_is")), [new RsField(new RsPath(error), "class"), new RsLiteral(this.ErrorClass(clause.Class).ToString(CultureInfo.InvariantCulture))]) { Type = IrType.Bool };
      if (clause.Filter is { } filter)
        test = test is null ? this.Expression(filter) : this.Binary(test, "&&", this.Expression(filter), IrType.Bool);

      return test;
    }

    /// <summary>The call of a function that propagates: inside a <c>try</c> body a <c>match</c> that leaves the body with the error, else <c>f(x)?</c>, from a public function the panic of an unhandled one.</summary>
    private RsExpression Propagated(RsExpression call) {
      if (this._spelling)
        return new RsTry(this.Operand(call, 15)) { Type = call.Type };
      if (this.Results && this._closureResults.Count > 0)
        return this.LimitResult("a call that may trap inside a closure");
      if (this._tries.Count > 0) {
        return new RsMatch(call, [
          new RsMatchArm(["Ok(value)"], new RsPath("value")),
          new RsMatchArm(["Err(error)"], this.Escape(new RsPath("error")))
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
      IrTry attempt => (CanComplete(attempt.Body) || attempt.Catches.Any(clause => CanComplete(clause.Body))) && (attempt.Finally is not { } final || CanComplete(final)),
      _ => true
    };

    /// <summary>
    /// A <c>try</c> with catch clauses, its body a labelled block that gives the exception, its clauses
    /// an <c>if</c> chain over the class - <c>ply_is(error.class, 1)</c> - and the filter, the exception
    /// going on where none takes it. A clause of a class a trap belongs to stands only around a body that
    /// cannot trap, since a trap is a panic no clause sees. Where a filter stands and a <c>finally</c>
    /// lies between a throw and the clauses, the clause is chosen where the exception is thrown, before
    /// the <c>finally</c> runs (<see cref="TryFrame.Selecting"/>). A <c>try</c> with a <c>finally</c> is <see cref="Finally"/>.
    /// </summary>
    private void Try(IrTry attempt, List<RsStatement> into) {
      if (attempt.Finally is not null) {
        this.Finally(attempt, into);
        return;
      }

      var trapped = Enum.GetValues<IrTrap>().Select(IrExceptionClass.Of).Distinct().ToList();
      var selecting = attempt.Catches.Any(clause => clause.Filter is not null) && IrClosures.Within(attempt.Body).Any(statement => statement is IrTry { Finally: not null });
      foreach (var clause in attempt.Catches) {
        if (!this.Results && trapped.Any(type => type == clause.Class || type.DerivesFrom(clause.Class)) && !this.TrapFree(attempt.Body))
          this.RefuseException("a catch of a class a trap belongs to, around code that may trap");
        // A filter is chosen at the throw only for the clauses of this try: a handler inside the body
        // with a finally inside its own body would hand an exception on after that finally ran; and a
        // finally inside a function called is run before the call hands the exception back.
        if (clause.Filter is { } filter && (!this.TrapFree(filter)
            || selecting && IrClosures.Within(attempt.Body).Any(statement => statement is IrTry { Catches.Count: > 0 } inner && IrClosures.Within(inner.Body).Any(nested => nested is IrTry { Finally: not null }))
            || IrTree.Nodes(attempt.Body).Any(node => node is IrCall call && this._throwing.Contains(call.Function) && this.RunsFinally(call.Function))))
          this.RefuseException("an exception filter that .NET runs before the finally blocks the exception leaves, or that may trap");
      }

      var completes = CanComplete(attempt.Body);
      var frame = new TryFrame(this.NewLabel("t"), completes, this._breakables.Count) { Selecting = selecting ? attempt.Catches : null };
      this._tries.Add(frame);
      var body = Spliced(this.Block(attempt.Body.Statements));
      this._tries.RemoveAt(this._tries.Count - 1);
      if (!frame.Used) {
        // Nothing in the body throws: no clause can run.
        into.AddRange(body.Statements);
        return;
      }

      if (completes && !(body.Statements.Count > 0 && Diverges(body.Statements[^1])))
        body = body with { Tail = new RsCall(new RsPath("Ok"), [new RsTuple([])]) };

      var error = this.Temporary("error");
      var selection = selecting ? this.Temporary("selected") : null;
      // The block's last break out of itself is its value; a block nothing else leaves needs no label (clippy's diverging_sub_expression).
      if (body is { Tail: null, Statements: [.., RsExpressionStatement { Expression: RsBreak { Value: { } last } exit }] } && exit.Label == frame.Label)
        body = new RsBlock([.. body.Statements.Take(body.Statements.Count - 1)], last);
      RsExpression outcome = RustSyntaxRewriter.DescendantsAndSelf(body).Any(node => node is RsBreak other && other.Label == frame.Label) ? new RsLabeledBlock(frame.Label, body)
        : body.Statements.Count == 0 && body.Tail is { } only ? only
        : new RsBlockExpression(body);
      var dispatch = Spliced(this.Dispatch(attempt.Catches, error, selection));
      // A clause that takes every exception and reads none leaves the exception unnamed (rustc's unused_variables).
      var bound = RustSyntaxRewriter.DescendantsAndSelf(dispatch).Any(node => node is RsPath path && path.Name == error) ? error : "_" + error;
      var pattern = selection is null ? bound : $"({bound}, {selection})";
      var payload = selection is null ? ErrorType : $"({ErrorType}, usize)";
      if (!completes) {
        into.Add(new RsLet(pattern, false, payload, outcome));
        into.AddRange(dispatch.Statements);
        return;
      }

      var result = this.Temporary("outcome");
      into.Add(new RsLet(result, false, Fallible(null, payload), outcome));
      into.Add(new RsExpressionStatement(new RsIf(new RsLetCondition($"Err({pattern})", new RsPath(result)), dispatch, null)));
    }

    /// <summary>
    /// The catch clauses over an exception bound to a name: an <c>if</c> chain, the exception going on
    /// where no clause takes it; over the number of the clause chosen at the throw where there is one.
    /// </summary>
    private RsBlock Dispatch(IReadOnlyList<IrCatch> catches, string error, string? selection) {
      RsExpression? chain = null;
      var rest = new RsBlock([new RsExpressionStatement(this.Escape(new RsPath(error)))], null);
      for (var index = catches.Count - 1; index >= 0; --index) {
        var clause = catches[index];
        if (clause.Local is { } local)
          this._locals[local] = error;

        var test = selection is null ? this.ClauseTest(clause, error)
          : new RsBinary(new RsPath(selection), "==", new RsLiteral(index.ToString(CultureInfo.InvariantCulture))) { Type = IrType.Bool };
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
    /// A <c>try</c> with a <c>finally</c>: the body - with its catch clauses, where it has any - a block
    /// every way out of which leaves it first; then the <c>finally</c>; then the way out made. A body
    /// left in one way only gives what leaves as its value: <c>let value = 'f0: { …; break 'f0 v; … };</c>,
    /// the <c>finally</c>, <c>return value</c>. A body left in several, or one that may also complete,
    /// sets a variable for each way before it leaves - <c>returned = Some(v)</c>, <c>thrown = Some(e)</c>,
    /// <c>jumped = 1</c> - and each is made after the <c>finally</c>, an exception handed on as the
    /// innermost handler or <c>try</c> around takes it, a <c>return</c> passing any <c>finally</c> around.
    /// </summary>
    private void Finally(IrTry attempt, List<RsStatement> into) {
      var frame = new TryFrame(this.NewLabel("f"), false, this._breakables.Count) { IsFinally = true };
      this._tries.Add(frame);
      var inner = new List<RsStatement>();
      if (attempt.Catches.Count > 0)
        this.Try(attempt with { Finally = null }, inner);
      else
        this.Statements(attempt.Body.Statements, inner);
      this._tries.RemoveAt(this._tries.Count - 1);
      var body = new RsBlock(inner, null);
      var marks = RustSyntaxRewriter.DescendantsAndSelf(body).OfType<RsBreak>().Where(exit => exit.Label == frame.Label).Select(Mark).ToHashSet(StringComparer.Ordinal);
      var completes = (CanComplete(attempt.Body) || attempt.Catches.Any(clause => CanComplete(clause.Body))) && !(inner.Count > 0 && Diverges(inner[^1]));
      var ways = marks.Count(mark => mark != JumpedMark) + frame.Jumps.Count;
      var final = attempt.Finally!.Statements;
      if (ways == 0) {
        into.AddRange(Spliced(body).Statements);
        this.Statements(final, into);
        return;
      }

      if (!CanComplete(attempt.Finally!)) {
        // A finally that never completes - it throws - makes no way out of the body: each only leaves the body, what it carries evaluated.
        body = Spliced(RustSyntaxRewriter.Rewrite(body, node => node is RsBreak { Value: RsCall { Callee: RsPath { Name: ReturnedMark or ThrownMark or JumpedMark } } marked } exit && exit.Label == frame.Label
          ? new RsBlockExpression(new RsBlock([
            .. marked.Arguments.Where(argument => Mark(exit) != JumpedMark && argument is not (RsPath or RsLiteral)).Select(argument => new RsLet("_", false, null, argument)),
            new RsExpressionStatement(new RsBreak(frame.Label, null))
          ], null))
          : node));
        into.Add(new RsExpressionStatement(new RsLabeledBlock(frame.Label, body)));
        this.Statements(final, into);
        return;
      }

      var returnType = this._function.ReturnType.IsAsync ? this._function.ReturnType.Element! : this._function.ReturnType;
      var returns = returnType != IrType.Void;
      if (ways == 1 && !completes) {
        body = Spliced(RustSyntaxRewriter.Rewrite(body, node => node is RsBreak { Value: RsCall { Callee: RsPath { Name: ReturnedMark or ThrownMark or JumpedMark } } marked } exit && exit.Label == frame.Label
          ? exit with { Value = marked.Arguments.Count > 0 && Mark(exit) != JumpedMark ? marked.Arguments[0] : null }
          : node));
        if (body is { Tail: null, Statements: [.., RsExpressionStatement { Expression: RsBreak last }] } && last.Label == frame.Label)
          body = new RsBlock([.. body.Statements.Take(body.Statements.Count - 1)], last.Value);
        RsExpression block = RustSyntaxRewriter.DescendantsAndSelf(body).Any(node => node is RsBreak other && other.Label == frame.Label) ? new RsLabeledBlock(frame.Label, body)
          : body.Statements.Count == 0 && body.Tail is { } only ? only
          : new RsBlockExpression(body);
        var thrown = marks.Contains(ThrownMark);
        var value = thrown ? this.Temporary("thrown") : marks.Contains(ReturnedMark) && returns ? this.Temporary("value") : null;
        if (value is null && block is RsBlockExpression { Block: { Tail: null } plain })
          into.AddRange(plain.Statements);
        else
          into.Add(value is null ? new RsExpressionStatement(block) : new RsLet(value, false, null, block));
        this.Statements(final, into);
        into.AddRange(thrown ? [new RsExpressionStatement(this.Escape(new RsPath(value!), true))]
          : marks.Contains(ReturnedMark) ? [new RsExpressionStatement(this.Leaving(value is null ? null : new RsPath(value) { Type = returnType }))]
          : this.Replay(frame.Jumps[0]));
        return;
      }

      var returned = marks.Contains(ReturnedMark) ? this.Temporary("returned") : null;
      var thrownError = marks.Contains(ThrownMark) ? this.Temporary("thrown") : null;
      var jumped = frame.Jumps.Count > 0 ? this.Temporary("jumped") : null;
      if (returned is not null)
        into.Add(new RsLet(returned, true, null, new RsPath(returns ? "None" : "false")));
      if (thrownError is not null)
        into.Add(new RsLet(thrownError, true, null, new RsPath("None")));
      if (jumped is not null)
        into.Add(new RsLet(jumped, true, null, new RsLiteral(frame.Jumps.Count == 1 ? "false" : "0")));
      body = Spliced(RustSyntaxRewriter.Rewrite(body, node => {
        if (node is not RsBreak { Value: RsCall { Callee: RsPath { Name: ReturnedMark or ThrownMark or JumpedMark } } marked } exit || exit.Label != frame.Label)
          return node;

        var (place, set) = Mark(exit) switch {
          ReturnedMark => (returned!, returns ? new RsCall(new RsPath("Some"), [marked.Arguments[0]]) : new RsLiteral("true")),
          ThrownMark => (thrownError!, new RsCall(new RsPath("Some"), [marked.Arguments[0]])),
          _ => (jumped!, frame.Jumps.Count == 1 ? new RsLiteral("true") : (RsExpression)new RsLiteral((int.Parse(((RsLiteral)marked.Arguments[0]).Text, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)))
        };
        return new RsBlockExpression(new RsBlock([new RsExpressionStatement(new RsAssign(new RsPath(place), "=", set)), new RsExpressionStatement(new RsBreak(frame.Label, null))], null));
      }));
      into.Add(new RsExpressionStatement(new RsLabeledBlock(frame.Label, body)));
      this.Statements(final, into);
      if (thrownError is not null) {
        var error = this.Temporary("error");
        into.Add(new RsExpressionStatement(new RsIf(new RsLetCondition($"Some({error})", new RsPath(thrownError)), new RsBlock([new RsExpressionStatement(this.Escape(new RsPath(error), true))], null), null)));
      }

      if (returned is not null) {
        var value = returns ? this.Temporary("value") : null;
        RsExpression condition = value is null ? new RsPath(returned) : new RsLetCondition($"Some({value})", new RsPath(returned));
        into.Add(new RsExpressionStatement(new RsIf(condition, new RsBlock([new RsExpressionStatement(this.Leaving(value is null ? null : new RsPath(value) { Type = returnType }))], null), null)));
      }

      if (jumped is not null) {
        if (frame.Jumps.Count == 1)
          into.Add(new RsExpressionStatement(new RsIf(new RsPath(jumped), new RsBlock([.. this.Replay(frame.Jumps[0])], null), null)));
        else
          into.Add(new RsExpressionStatement(new RsMatch(new RsPath(jumped), [
            .. frame.Jumps.Select((jump, index) => new RsMatchArm([(index + 1).ToString(CultureInfo.InvariantCulture)], new RsBlockExpression(new RsBlock([.. this.Replay(jump)], null)))),
            new RsMatchArm(["_"], new RsBlockExpression(RsBlock.Empty))
          ])));
      }

      if (!completes)
        into.Add(new RsExpressionStatement(new RsMacro("unreachable", [])));
    }

    /// <summary>The way out of the body of a <c>try</c> with a <c>finally</c> a break carries: <see cref="ReturnedMark"/>, <see cref="ThrownMark"/> or <see cref="JumpedMark"/>.</summary>
    private static string Mark(RsBreak exit) => ((RsPath)((RsCall)exit.Value!).Callee).Name;

    /// <summary>A jump out of the body of a <c>try</c> with a <c>finally</c>, made after the <c>finally</c>: the <c>break</c> or <c>continue</c> as it would have been.</summary>
    private IEnumerable<RsStatement> Replay((Breakable Target, bool Continues) jump)
      => jump.Continues ? this.ContinueTo(this._breakables.IndexOf(jump.Target)) : [new RsExpressionStatement(this.BreakTo(jump.Target))];

    /// <summary>The innermost <c>try</c> with a <c>finally</c> a jump to the loop or switch at an index of the breakables leaves, or <see langword="null"/>.</summary>
    private TryFrame? Crossed(int index) => this._tries.LastOrDefault(frame => frame.IsFinally && frame.Breakables > index);

    /// <summary>A jump out of the body of a <c>try</c> with a <c>finally</c>: the break out of the body, marked with the number of the jump, which is made after the <c>finally</c>.</summary>
    private static RsBreak Jump(TryFrame frame, Breakable target, bool continues) {
      var index = frame.Jumps.IndexOf((target, continues));
      if (index < 0) {
        index = frame.Jumps.Count;
        frame.Jumps.Add((target, continues));
      }

      frame.Used = true;
      return new RsBreak(frame.Label, new RsCall(new RsPath(JumpedMark), [new RsLiteral(index.ToString(CultureInfo.InvariantCulture))]));
    }

    /// <summary>
    /// A block with the blocks among its statements that end in a way out - the choice of a clause where
    /// an exception is thrown, a way out of a <c>finally</c>'s body with the variable it sets - written
    /// as statements of the block itself, at every depth.
    /// </summary>
    private static RsBlock Spliced(RsBlock block) => RustSyntaxRewriter.Rewrite(block, node => node is RsBlock inner && inner.Statements.Any(Splices)
      ? inner with { Statements = [.. inner.Statements.SelectMany(statement => Splices(statement) ? ((RsBlockExpression)((RsExpressionStatement)statement).Expression).Block.Statements : [statement])] }
      : node);

    private static bool Splices(RsStatement statement) => statement is RsExpressionStatement { Expression: RsBlockExpression { Block: { Tail: null, Statements: [.., var last] } } } && Diverges(last);

    /// <summary><c>return v</c>; in a function that propagates <c>return Ok(v)</c>.</summary>
    private RsReturn Returned(RsExpression? value) {
      if (!this._throwing.Contains(this._function.Name) || this._closureResults.Count > 0)
        return new RsReturn(value);

      // A value whose error is handed on is the result itself: return f(x), not return Ok(f(x)?) (clippy's needless_question_mark).
      return new RsReturn(value is RsTry handed ? handed.Operand : new RsCall(new RsPath("Ok"), [value ?? new RsTuple([])]));
    }

    /// <summary>A <c>return</c> inside the body of a <c>try</c> with a <c>finally</c>: the break out of the innermost such body, marked, so that the <c>finally</c> runs first.</summary>
    private RsExpression? FinallyReturn(RsExpression? value) {
      if (this._tries.LastOrDefault(frame => frame.IsFinally) is not { } frame)
        return null;

      frame.Used = true;
      return new RsBreak(frame.Label, new RsCall(new RsPath(ReturnedMark), value is null ? [] : [value]));
    }

    /// <summary>Whether a statement or an expression traps in no way: what a catch of a class of a trap may stand around.</summary>
    private bool TrapFree(IrBlock block) => IrClosures.Within(block).All(this.TrapFree);

    private bool TrapFree(IrStmt statement) => statement switch {
      IrBlock or IrIf or IrWhile or IrDoWhile or IrFor or IrDeclare or IrAssign or IrExprStmt or IrReturn or IrRethrow or IrTry or IrBreak or IrContinue
        => IrTree.Expressions(statement).All(this.TrapFree),
      IrThrow thrown => this.ThrowTrapFree(thrown.Exception),
      // A global behind a lock traps in no way: its lock is poisoned only by a panic while it was held, which no handler sees.
      // A static of each thread is taken to: its cell is borrowed, and a thread that ends has none (#1422).
      IrGlobalAssign assign => !IsThreadLocalType(assign.Global.Type) && this.TrapFree(assign.Value),
      _ => false
    };

    private bool TrapFree(IrExpr expression) => expression switch {
      IrConst or IrLocalRef or IrStrConst or IrNull or IrLambda => true,
      IrGlobalRef { Global: var global } => !IsThreadLocalType(global.Type),
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when binary.Type.IsInteger && binary.Right is not IrConst { Value: not 0 and not -1 } => false,
      IrBinary { Op: IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked } => false,
      IrBinary binary => this.TrapFree(binary.Left) && this.TrapFree(binary.Right),
      IrUnary { Op: IrUnaryOp.LogicalNot or IrUnaryOp.BitNot or IrUnaryOp.Negate } unary => this.TrapFree(unary.Operand),
      IrConvert { Contract: not IrConversion.Trap } convert => this.TrapFree(convert.Operand),
      IrConditional choice => this.TrapFree(choice.Condition) && this.TrapFree(choice.WhenTrue) && this.TrapFree(choice.WhenFalse),
      IrNew creation when IsCatchable(creation.Type) => creation.Arguments.All(this.TrapFree),
      IrThrowExpression thrown => this.ThrowTrapFree(thrown.Exception),
      IrFieldLoad { Target: IrLocalRef, Field: var field } when field == IrExceptionClass.Message => true,
      IrIntrinsic { Op: IrIntrinsicOp.StrConcat or IrIntrinsicOp.StrEquals or IrIntrinsicOp.StrLength or IrIntrinsicOp.StrFromCodeUnit } operation => operation.Arguments.All(this.TrapFree),
      IrCall call => call.Arguments.All(this.TrapFree) && this.TrapFreeFunction(call.Function),
      _ => false
    };

    /// <summary>Whether the throw of an exception traps in no way: one that propagates as a value, made of what traps in no way.</summary>
    private bool ThrowTrapFree(IrExpr exception) => this.Propagates(exception) && (exception is IrNew creation ? creation.Arguments.All(this.TrapFree) : this.TrapFree(exception));

    /// <summary>Whether a function of the module may run a <c>finally</c> block while an exception leaves it: it has one, or calls one that may; a recursive one taken to.</summary>
    private bool RunsFinally(string name) {
      if (this._runsFinally.TryGetValue(name, out var known))
        return known;

      this._runsFinally[name] = true;
      var function = this._module.Functions.FirstOrDefault(candidate => candidate.Name == name);
      var runs = function is null || IrClosures.Within(function.Body).Any(statement => statement is IrTry { Finally: not null })
        || IrTree.Nodes(function.Body).Any(node => node is IrCall call && this._throwing.Contains(call.Function) && this.RunsFinally(call.Function));
      this._runsFinally[name] = runs;
      return runs;
    }

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
