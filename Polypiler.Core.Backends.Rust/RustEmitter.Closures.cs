using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The signatures of the function types the module may hold <c>null</c> in: an <c>Option</c> around their <c>Rc</c>.</summary>
    private readonly HashSet<IrFunctionType> _nullableSignatures = [];

    /// <summary>
    /// The variables of the function being written that a closure captures and that are assigned
    /// after they are declared: one place shared by the function and its closures, an
    /// <c>Rc&lt;Cell&lt;T&gt;&gt;</c>.
    /// </summary>
    private HashSet<IrLocal> _shared = [];

    /// <summary>The local functions of the function being written that capture nothing but each other: a <c>fn</c> declared in its body.</summary>
    private Dictionary<IrLocal, IrLambda> _nested = [];

    /// <summary>The captured variables the closure being written holds as its own: an <c>Rc</c>, a vector or a string it owns, where the function borrowed it.</summary>
    private HashSet<IrLocal> _owned = [];

    /// <summary>The result types of the closures being written, innermost last.</summary>
    private readonly List<IrType> _closureResults = [];

    /// <summary>Finds the function types of a module that may hold <c>null</c>: those it writes <c>null</c> of or makes an array of, whose elements start as <c>null</c>.</summary>
    private void Signatures(IrModule module) {
      foreach (var node in module.Functions.SelectMany(function => IrTree.Nodes(function.Body))) {
        if (node is IrNull { Type.Signature: { } signature })
          this._nullableSignatures.Add(signature);
        if (node is IrNewArray { Type.Element.Signature: { } element })
          this._nullableSignatures.Add(element);
      }
    }

    /// <summary>Whether a value of a function type may be <c>null</c>, which its type then says.</summary>
    private bool IsNullable(IrType type) => type.Signature is { } signature && this._nullableSignatures.Contains(signature);

    /// <summary><c>Rc&lt;dyn Fn(A, B) -&gt; R&gt;</c>, in an <c>Option</c> where the module may hold <c>null</c> of the type.</summary>
    private string FunctionType(IrType type) {
      var signature = type.Signature!;
      this._uses.Add("std::rc::Rc");
      var parameters = string.Join(", ", signature.Parameters.Select(this.SignatureType));
      var result = signature.Result == IrType.Void ? "" : " -> " + this.SignatureType(signature.Result);
      var shared = $"Rc<dyn Fn({parameters}){result}>";
      return this.IsNullable(type) ? $"Option<{shared}>" : shared;
    }

    /// <summary>The type of a parameter or the result of a function value: a value that is copied, or another function value.</summary>
    private string SignatureType(IrType type)
      => IsValue(type) || type.IsFunction ? this.Type(type) : ((RsPath)this.Refuse($"a function value that takes or gives {type}")).Name;

    /// <summary>
    /// Finds out how the function being written holds the variables its closures capture: a local
    /// function that captures nothing but such functions is a <c>fn</c> of its own; a variable assigned
    /// after its declaration, by the function or by a closure, is one place both share; every other
    /// one is copied into the closure, which sees the one value it ever has.
    /// </summary>
    private void Closures(IrFunction function) {
      this._nested = Nested(function.Body);
      this._owned = [];
      var captured = IrClosures.Captured(function.Body);
      var assigned = new HashSet<IrLocal>();
      foreach (var statement in IrClosures.Within(function.Body)) {
        switch (statement) {
          case IrAssign assign:
            assigned.Add(assign.Local);
            break;
          case IrDeclare { Initializer: null } declare:
            assigned.Add(declare.Local);
            break;
          case IrFor loop when loop.PerIteration.Any(captured.Contains):
            this.Refuse("a closure that captures the variable of a loop copied for each pass");
            break;
        }

        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes))
          if (node is IrStep { Target: IrLocalRef target })
            assigned.Add(target.Local);
      }

      this._shared = [.. captured.Where(local => assigned.Contains(local) && !this._nested.ContainsKey(local))];
      foreach (var local in this._shared.Where(local => !IsValue(local.Type)))
        this.Refuse($"a captured variable of the type {local.Type} that is assigned");
    }

    /// <summary>The local functions of a body that capture nothing but one another, the source's local functions without a closure.</summary>
    private static Dictionary<IrLocal, IrLambda> Nested(IrBlock body) {
      var held = IrClosures.Held(body).ToDictionary(pair => pair.Key, pair => pair.Value);
      for (var changed = true; changed;) {
        changed = false;
        foreach (var (local, lambda) in held.ToList())
          if (lambda.Captures.Any(capture => !held.ContainsKey(capture))) {
            held.Remove(local);
            changed = true;
          }
      }

      return held;
    }

    /// <summary>Every lambda of a body, those inside lambdas included.</summary>
    private static IEnumerable<IrLambda> Lambdas(IrBlock body) => IrTree.Nodes(body).OfType<IrLambda>();

    /// <summary>The local functions of the function being written, each a <c>fn</c> at the start of its body, where every closure of it sees them.</summary>
    private IEnumerable<RsStatement> NestedFunctions() {
      foreach (var (local, lambda) in this._nested.OrderBy(pair => this._locals[pair.Key], StringComparer.Ordinal)) {
        var parameters = lambda.Parameters.Select(parameter => new RsParameter(this._locals[parameter], this.SignatureType(parameter.Type))).ToList();
        var owned = this._owned;
        this._owned = [];
        var body = this.ClosureBody(lambda);
        this._owned = owned;
        yield return new RsItemStatement(new RsFunction(this._locals[local], "", parameters, lambda.ReturnType == IrType.Void ? null : this.SignatureType(lambda.ReturnType), body));
      }
    }

    /// <summary>The body of a closure or a local function, its value as the tail.</summary>
    private RsBlock ClosureBody(IrLambda lambda) {
      var breakables = this._breakables.ToList();
      this._breakables.Clear();
      this._closureResults.Add(lambda.ReturnType);
      var statements = new List<RsStatement>();
      // A parameter the body assigns is bound again as one it may change.
      foreach (var parameter in lambda.Parameters.Where(this._lambdaReassigned.Contains))
        statements.Add(new RsLet(this._locals[parameter], true, null, new RsPath(this._locals[parameter])));
      this.Statements(lambda.Body.Statements, statements);
      this._closureResults.RemoveAt(this._closureResults.Count - 1);
      this._breakables.AddRange(breakables);
      return Tidy.Function(new RsBlock(statements, null), lambda.ReturnType == IrType.Void);
    }

    /// <summary>The parameters of the lambdas of the function being written that their bodies assign.</summary>
    private HashSet<IrLocal> _lambdaReassigned = [];

    /// <summary>
    /// A lambda as a closure behind an <c>Rc</c>: <c>Rc::new(move |a: T| body)</c>. What it captures
    /// it takes as its own, bound first in a block around it: a shared variable's <c>Rc</c>, a function
    /// value or an object cloned, an array or a string nothing writes copied; a value is copied by the <c>move</c> itself.
    /// </summary>
    private RsExpression Closure(IrLambda lambda) {
      var bindings = new List<RsStatement>();
      var owned = new HashSet<IrLocal>(this._owned);
      var captures = lambda.Captures.Where(capture => !this._nested.ContainsKey(capture)).ToList();
      foreach (var capture in captures) {
        var name = this._locals[capture];
        if (name == "self") {
          this.Refuse("a closure that captures the object of a method");
          continue;
        }

        var place = new IrLocalRef(capture);
        RsExpression? value = null;
        if (this._shared.Contains(capture))
          value = this.Cloned(new RsReference(false, new RsPath(name) { Type = capture.Type }));
        else if (capture.Type.IsFunction)
          value = this.FunctionValue(place);
        else if (capture.Type.IsObject)
          value = this.ObjectValue(place, this.MayBeNull(capture));
        else if (capture.Type.IsArray)
          value = this.WritesArray(capture) ? this.Refuse("a closure that captures an array that is written")
            : !this.StaysInCall(lambda) ? this.Refuse("a closure that captures an array and may outlive the call it is made for")
            : this.Vector(place, false);
        else if (capture.Type.IsString)
          value = this.StringValue(place);
        else if (!IsValue(capture.Type))
          value = this.Refuse($"a closure that captures a variable of the type {capture.Type}");

        if (value is null)
          continue;

        bindings.Add(new RsLet(name, false, null, value));
        owned.Add(capture);
      }

      return this.MadeClosure(lambda, bindings, owned, captures.Count > 0);
    }

    /// <summary>A closure of a lambda behind an <c>Rc</c>, what it captures bound before it: a block that gives the closure.</summary>
    private RsExpression MadeClosure(IrLambda lambda, List<RsStatement> bindings, HashSet<IrLocal> owned, bool move) {
      var parameters = lambda.Parameters.Select(parameter => new RsParameter(this._locals[parameter], this.SignatureType(parameter.Type))).ToList();
      var outer = this._owned;
      this._owned = owned;
      var body = this.ClosureBody(lambda);
      this._owned = outer;
      var closure = new RsClosure(move, parameters, body is { Statements.Count: 0, Tail: { } tail } ? tail : new RsBlockExpression(body)) { Type = lambda.Type };
      RsExpression made = bindings.Count == 0 ? closure : new RsBlockExpression(new RsBlock(bindings, closure));
      return this.FunctionObject(lambda.Type, made);
    }

    /// <summary><c>Rc::new(f)</c> of a closure or a function, in <c>Some</c> where the type may be <c>null</c>.</summary>
    private RsExpression FunctionObject(IrType type, RsExpression function) {
      this._uses.Add("std::rc::Rc");
      RsExpression shared = new RsCall(new RsPath("Rc::new"), [function]) { Type = type };
      return this.IsNullable(type) ? new RsCall(new RsPath("Some"), [shared]) { Type = type } : shared;
    }

    /// <summary>
    /// Whether a lambda is only the argument of a call whose callee does nothing with it but call it: the
    /// closure, which holds a copy of an array it captures, ends with the call, before anyone could
    /// write the array the source's closure shares.
    /// </summary>
    private bool StaysInCall(IrLambda lambda) {
      var call = IrTree.Nodes(this._function.Body).OfType<IrCall>().FirstOrDefault(candidate => candidate.Arguments.Any(argument => ReferenceEquals(argument, lambda)));
      if (call is null || this._module.Functions.FirstOrDefault(function => function.Name == call.Function) is not { } callee)
        return false;

      var parameter = callee.Parameters[call.Arguments.ToList().FindIndex(argument => ReferenceEquals(argument, lambda))];
      var uses = IrTree.Nodes(callee.Body).Count(node => node is IrLocalRef reference && reference.Local == parameter);
      var called = IrTree.Nodes(callee.Body).Count(node => node is IrInvoke { Callee: IrLocalRef reference } && reference.Local == parameter);
      return uses == called && !IrClosures.Captured(callee.Body).Contains(parameter);
    }

    /// <summary>Whether the function being written, or a closure of it, writes an array variable or passes it on to be written.</summary>
    private bool WritesArray(IrLocal local) {
      bool Names(IrExpr expression) => expression is IrLocalRef reference && reference.Local == local;
      foreach (var statement in IrClosures.Within(this._function.Body)) {
        if (statement is IrStore store && Names(store.Array) || statement is IrAssign assign && assign.Local == local)
          return true;

        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
          if (node is IrStep { Target: IrArrayLoad load } && Names(load.Array)
            || node is IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy } copy && Names(copy.Arguments[2])
            || node is IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear } fill && Names(fill.Arguments[0])
            || node is IrCall call && this._arrays.TryGetValue(call.Function, out var callee) && callee.MutableParameters.Any(position => position < call.Arguments.Count && Names(call.Arguments[position])))
            return true;
        }
      }

      return false;
    }

    /// <summary>
    /// A function value where a place takes it: a closure, a call's result as it is; a variable or an
    /// element cloned, its <c>Rc</c> shared; a local function as <c>Rc::new(f)</c>.
    /// </summary>
    private RsExpression FunctionValue(IrExpr value) {
      switch (value) {
        case IrLocalRef { Local: var local } when this._nested.ContainsKey(local):
          return this.FunctionObject(value.Type, new RsPath(this._locals[local]) { Type = value.Type });
        case IrLocalRef or IrArrayLoad: {
          var place = this.Callee(value);
          return this.IsNullable(value.Type) ? new RsMethodCall(this.Receiver(place), "clone", []) { Type = value.Type } : this.Cloned(new RsReference(false, place));
        }
        case IrConditional choice:
          return this.Choice(choice, this.FunctionValue);
        default:
          return this.Expression(value);
      }
    }

    /// <summary>A function value as the callee of a call: a variable or an element in place, any other value as it is made.</summary>
    private RsExpression Callee(IrExpr callee) => callee switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = callee.Type },
      IrArrayLoad load => new RsIndex(this.ArrayTarget(load.Array), this.Index(load.Index)) { Type = callee.Type },
      _ => this.Expression(callee)
    };

    /// <summary>A value as an argument of a call or a function value: a function value shared, any other as it is.</summary>
    private RsExpression Argument(IrExpr argument) => argument.Type.IsFunction ? this.FunctionValue(argument) : this.Expression(argument);

    /// <summary>
    /// A call of a function value: <c>f(a, b)</c>, a local function by its name. A value that may be
    /// <c>null</c> is called through <c>ply_non_null(&amp;f)</c>, which traps with <c>NullReference</c>
    /// - after the arguments, as the IR orders it: arguments that are more than variables and constants
    /// are bound first, the callee before them.
    /// </summary>
    private RsExpression Invoke(IrInvoke invoke) {
      if (invoke.Callee is IrLocalRef { Local: var local } && this._nested.ContainsKey(local))
        return new RsCall(new RsPath(this._locals[local]), [.. invoke.Arguments.Select(this.Argument)]) { Type = invoke.Type };

      var callee = this.Callee(invoke.Callee);
      if (!this.IsNullable(invoke.Callee.Type))
        return new RsCall(this.Operand(callee, 15), [.. invoke.Arguments.Select(this.Argument)]) { Type = invoke.Type };

      var nonNull = this.StringHelper("ply_non_null");
      if (invoke.Arguments.All(argument => argument is IrLocalRef or IrConst))
        return new RsCall(new RsCall(new RsPath(nonNull), [new RsReference(false, callee)]), [.. invoke.Arguments.Select(this.Argument)]) { Type = invoke.Type };

      var calleeName = this.Temporary("callee");
      var statements = new List<RsStatement> { new RsLet(calleeName, false, null, new RsReference(false, callee)) };
      var arguments = new List<RsExpression>();
      foreach (var argument in invoke.Arguments) {
        var name = this.Temporary("argument");
        statements.Add(new RsLet(name, false, null, this.Argument(argument)));
        arguments.Add(new RsPath(name) { Type = argument.Type });
      }

      return new RsBlockExpression(new RsBlock(statements, new RsCall(new RsCall(new RsPath(nonNull), [new RsPath(calleeName)]), arguments) { Type = invoke.Type })) { Type = invoke.Type };
    }

    /// <summary>A function of the module as a value: <c>Rc::new(f)</c>, a static method of a class by its path.</summary>
    private RsExpression FunctionReference(IrFunctionRef reference) {
      if (!this._functions.TryGetValue(reference.Function, out var name))
        return this.Refuse($"a reference to the function '{reference.Function}'");
      if (this._throwing.Contains(reference.Function))
        return this.Refuse("a function that lets an exception out as a function value");

      if (this._members.TryGetValue(reference.Function, out var member)) {
        if (member.Kind != MemberKind.Static || this._hierarchy.Contains(member.Class))
          return this.Refuse("a method of a class as a function value without its object");

        name = $"{this.StructName(member.Class)}::{member.Name}";
      }

      var function = this._module.Functions.First(candidate => candidate.Name == reference.Function);
      if (function.Parameters.Any(parameter => !IsValue(parameter.Type)) || function.ReturnType != IrType.Void && !IsValue(function.ReturnType))
        return this.Refuse("a function that takes or gives more than values as a function value");

      return this.FunctionObject(reference.Type, new RsPath(name) { Type = reference.Type });
    }

    /// <summary>
    /// A method bound to its object: the object evaluated now, a null one trapping, then held by a
    /// closure that calls the method on it, <c>{ let receiver = …; Rc::new(move |v: i32| receiver.apply(v)) }</c>.
    /// </summary>
    private RsExpression BoundMethod(IrBoundMethod bound) {
      if (bound.Receiver is null)
        return this.FunctionReference(new IrFunctionRef(bound.Type, bound.Method.Function));
      if (bound.Kind != IrMethodCallKind.Direct)
        return this.Refuse("a virtual method bound to its object");

      var signature = bound.Type.Signature!;
      var method = this._module.Functions.FirstOrDefault(function => function.Name == bound.Method.Function);
      if (method is null)
        return this.Refuse($"a reference to the method '{bound.Method.Function}'");

      // Variables of their own, whose names no variable of the IR has: an IR variable is its name and its type.
      var receiver = new IrLocal("$receiver", bound.Receiver.Type);
      this._locals[receiver] = this.Temporary("receiver");
      var parameters = method.Parameters.Skip(1).Select((parameter, index) => new IrLocal($"${index}:{parameter.Name}", parameter.Type)).ToList();
      foreach (var (parameter, source) in parameters.Zip(method.Parameters.Skip(1)))
        this._locals[parameter] = this.Temporary(Naming.RustNames.Snake(source.Name, this.Edition).Replace("r#", "", StringComparison.Ordinal));

      var call = new IrCall(method.Name, signature.Result, [new IrLocalRef(receiver), .. parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter))]);
      var lambda = new IrLambda(bound.Type, [receiver], parameters, [], new IrBlock([signature.Result == IrType.Void ? new IrExprStmt(call) : new IrReturn(call)]));
      var bindings = new List<RsStatement> { new RsLet(this._locals[receiver], false, null, this.ObjectValue(bound.Receiver, false)) };
      return this.MadeClosure(lambda, bindings, [.. this._owned, receiver], true);
    }

    /// <summary>
    /// A test of a function value for <c>null</c>: <c>f.is_none()</c>, <c>false</c> of a variable whose
    /// type never holds <c>null</c> or that is a local function, a <c>fn</c> item that is never <c>null</c>.
    /// </summary>
    private RsExpression FunctionIsNull(IrExpr value) {
      if (value is IrNull)
        return new RsLiteral("true") { Type = IrType.Bool };
      if (!this.IsNullable(value.Type) || value is IrLocalRef { Local: var local } && this._nested.ContainsKey(local))
        return value is IrLocalRef ? new RsLiteral("false") { Type = IrType.Bool } : this.Refuse("a test for null of a function value that is made");

      return new RsMethodCall(this.Receiver(this.Callee(value)), "is_none", []) { Type = IrType.Bool };
    }

    /// <summary>A test of a function value for a value: <c>f.is_some()</c>, <c>true</c> of a variable whose type never holds <c>null</c>.</summary>
    private RsExpression FunctionIsSome(IrExpr value) => this.FunctionIsNull(value) switch {
      RsLiteral { Text: var truth } => new RsLiteral(truth == "true" ? "false" : "true") { Type = IrType.Bool },
      RsMethodCall { Name: "is_none" } test => test with { Name = "is_some" },
      var refused => refused
    };

    /// <summary>The identity of two function values; only a comparison with <c>null</c> has one in Rust.</summary>
    private RsExpression FunctionEquals(IrExpr left, IrExpr right)
      => left is IrNull ? this.FunctionIsNull(right)
        : right is IrNull ? this.FunctionIsNull(left)
        : this.Refuse("the identity of two function values");

    /// <summary>A read of a shared variable: <c>x.get()</c>.</summary>
    private RsExpression SharedRead(IrLocal local)
      => new RsMethodCall(new RsPath(this._locals[local]) { Type = local.Type }, "get", []) { Type = local.Type };

    /// <summary>A write of a shared variable: <c>x.set(v)</c>.</summary>
    private RsExpression SharedWrite(IrLocal local, RsExpression value)
      => new RsMethodCall(new RsPath(this._locals[local]) { Type = local.Type }, "set", [value]);

    /// <summary>The declaration of a shared variable: <c>let x = Rc::new(Cell::new(v));</c>, a variable declared without a value starting at its zero.</summary>
    private RsLet SharedDeclaration(IrLocal local, RsExpression? value) {
      this._uses.Add("std::rc::Rc");
      this._uses.Add("std::cell::Cell");
      var initial = value ?? (local.Type.IsStruct || local.Type.IsEnum ? new RsCall(new RsPath(this.Type(local.Type) + "::default"), []) : Literal(new IrConst(local.Type, 0)));
      var cell = new RsCall(new RsPath("Cell::new"), [Suffixed(initial)]);
      return new RsLet(this._locals[local], false, null, new RsCall(new RsPath("Rc::new"), [cell]) { Type = local.Type });
    }

    /// <summary>A step of a shared variable: as a statement <c>x.set(x.get() + 1)</c>; as an expression a block that gives the old or the new value.</summary>
    private RsExpression SharedStep(IrLocal local, IrStep step, bool statement) {
      if (statement)
        return this.SharedWrite(local, this.Stepped(this.SharedRead(local), step));
      if (!step.Postfix)
        return new RsBlockExpression(new RsBlock([new RsExpressionStatement(this.SharedWrite(local, this.Stepped(this.SharedRead(local), step)))], this.SharedRead(local))) { Type = local.Type };

      var old = this.Temporary("old");
      var oldValue = new RsPath(old) { Type = local.Type };
      return new RsBlockExpression(new RsBlock([new RsLet(old, false, null, this.SharedRead(local)), new RsExpressionStatement(this.SharedWrite(local, this.Stepped(oldValue, step)))], oldValue)) { Type = local.Type };
    }

  }

}
