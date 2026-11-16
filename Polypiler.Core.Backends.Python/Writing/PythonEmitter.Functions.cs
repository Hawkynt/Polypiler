using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The locals of the function being written that a closure captures.</summary>
    private readonly HashSet<IrLocal> _captured = [];

    /// <summary>
    /// The captured locals that every pass of a loop has a variable of its own of: a variable declared
    /// in the body of a loop, the element of a loop over a sequence or a collection, a variable of every
    /// pass of a <c>for</c>. Python's variables belong to the whole function, so each of these is a list
    /// of one element, made anew where the IR makes the variable anew, which a closure binds when it is made.
    /// </summary>
    private readonly HashSet<IrLocal> _cells = [];

    /// <summary>The locals that hold one lambda for good, written as a nested function of that name.</summary>
    private IReadOnlyDictionary<IrLocal, IrLambda> _held = new Dictionary<IrLocal, IrLambda>();

    /// <summary>The nested functions the expressions of the statement being written need, declared in front of it.</summary>
    private List<PyStatement> _pending = [];

    /// <summary>How many lambdas around the expression being written are nested functions.</summary>
    private int _lambdaDepth;

    /// <summary>Whether a local is a cell: a list of one element that holds its value.</summary>
    private bool IsCell(IrLocal local) => this._cells.Contains(local);

    /// <summary>Takes note of the closures of the function about to be written: what they capture, and which captured variables are cells.</summary>
    private void Prepare(IrBlock body) {
      this._captured.Clear();
      this._cells.Clear();
      this._captured.UnionWith(IrClosures.Captured(body));
      this._held = IrClosures.Held(body);
      if (this._captured.Count > 0)
        this.Cells(body, false);

      // A local whose address is taken is the region of its pointers: a cell, written through them.
      foreach (var address in IrTree.Nodes(body).OfType<IrIntrinsic>().Where(node => node.Op == IrIntrinsicOp.AddressOf))
        if (address.Arguments[0] is IrLocalRef { Local: var local })
          this._cells.Add(local);
    }

    private void Cells(IrStmt statement, bool inLoop) {
      foreach (var lambda in IrTree.Expressions(statement).SelectMany(IrClosures.Outermost))
        this.Cells(lambda.Body, false);

      switch (statement) {
        case IrDeclare declare when inLoop && this._captured.Contains(declare.Local):
          this._cells.Add(declare.Local);
          break;
        case IrFor loop:
          this._cells.UnionWith(loop.PerIteration.Where(this._captured.Contains));
          break;
        case IrForEachSequence loop when this._captured.Contains(loop.Element):
          this._cells.Add(loop.Element);
          break;
        case IrForEachCollection loop:
          foreach (var element in new[] { loop.Element, loop.Value }.OfType<IrLocal>().Where(this._captured.Contains))
            this._cells.Add(element);

          break;
      }

      var loops = statement is IrWhile or IrDoWhile or IrFor or IrForEachSequence or IrForEachCollection;
      foreach (var block in statement is IrBlock own ? [own] : IrTree.Blocks(statement))
        foreach (var inner in block.Statements)
          this.Cells(inner, inLoop || loops);
    }

    /// <summary>The value of a local: its name, or the element of its cell.</summary>
    private PyExpression Read(IrLocal local)
      => this.IsCell(local) ? new PySubscript(Name(this.LocalName(local)), Integer(0)) : Name(this.LocalName(local));

    /// <summary>The place a local is assigned to: its name, or the element of its cell.</summary>
    private PyExpression Target(IrLocal local) => this.Read(local);

    /// <summary>
    /// The statement that gives a local a new variable: a cell made anew, holding the value; for any
    /// other local an assignment, annotated where the local is first written.
    /// </summary>
    private PyStatement Declare(IrLocal local, PyExpression value) {
      var name = this.LocalName(local);
      if (this.IsCell(local))
        return new PyAssign(Name(name), new PyList([value]));

      return this.Assign(name, value, local.Type);
    }

    /// <summary>
    /// The body of a function: its statements, and for an iterator function, whose sequence restarts
    /// on every enumeration, a generator of its own that a sequence object calls anew every time.
    /// </summary>
    private List<PyStatement> FunctionBody(IrFunction function) => this.IteratorBody(function) ?? this.Statements(function.Body.Statements);

    /// <summary>
    /// A lambda: <c>lambda a: expression</c> where its body returns one expression and it writes no
    /// variable it captures, otherwise a nested function declared in front of the statement that makes
    /// it. A captured cell is bound when the function is made, as a default of a parameter.
    /// </summary>
    private PyExpression Lambda(IrLambda lambda, string? name = null) {
      var cells = lambda.Captures.Where(this.IsCell).ToList();
      var assigned = this.AssignedCaptures(lambda).ToList();
      if (name is null && cells.Count == 0 && assigned.Count == 0 && lambda.Body.Statements is [IrReturn { Value: { } returned }]
        && !IrTree.Nodes(returned).Any(node => node is IrLambda or IrBind) && !lambda.Parameters.Any(this.IsCell)) {
        var outer = this._pending;
        this._pending = [];
        ++this._lambdaDepth;
        var value = this.Expression(returned);
        --this._lambdaDepth;
        var hoisted = this._pending;
        this._pending = outer;
        if (hoisted.Count == 0)
          return new PyLambda([.. lambda.Parameters.Select(this.LocalName)], value);
      }

      var function = this.NestedFunction(lambda, name ?? this.Temporary("_lambda"), cells, assigned);
      this._pending.Add(function);
      return Name(function.Name);
    }

    /// <summary>The captured locals a lambda assigns, not counting cells, which it changes in place.</summary>
    private IEnumerable<IrLocal> AssignedCaptures(IrLambda lambda) {
      var captures = lambda.Captures.ToHashSet();
      var writes = new HashSet<IrLocal>();
      foreach (var statement in lambda.Body.Statements)
        Written(statement, writes);

      foreach (var bind in IrTree.Nodes(lambda.Body).OfType<IrBind>())
        writes.Add(bind.Local);

      return lambda.Captures.Where(local => writes.Contains(local) && captures.Contains(local) && !this.IsCell(local)).Distinct();
    }

    /// <summary>A lambda as a nested function: its cells bound by the defaults of parameters after its own, <c>nonlocal</c> what it assigns.</summary>
    private PyFunctionDef NestedFunction(IrLambda lambda, string name, IReadOnlyList<IrLocal> cells, IReadOnlyList<IrLocal> assigned) {
      ++this._lambdaDepth;
      var frames = this._frames.ToList();
      this._frames.Clear();
      try {
        var parameters = lambda.Parameters.Select(parameter => {
          var local = this.LocalName(parameter);
          this._annotated.Add(local);
          return new PyParameter(local, this.Annotation(parameter.Type));
        }).ToList();
        foreach (var cell in cells)
          parameters.Add(new PyParameter(this.LocalName(cell), null) { Default = Name(this.LocalName(cell)) });

        var body = new List<PyStatement>();
        if (assigned.Count > 0)
          body.Add(new PyNonlocal([.. assigned.Select(this.LocalName).Distinct().Order(StringComparer.Ordinal)]));

        foreach (var parameter in lambda.Parameters.Where(this.IsCell))
          body.Add(new PyAssign(Name(this.LocalName(parameter)), new PyList([Name(this.LocalName(parameter))])));

        var annotated = this._annotated.ToList();
        body.AddRange(this.Statements(lambda.Body.Statements));
        if (lambda.Type.Signature!.Result == IrType.Void && body.Count > 0 && body[^1] is PyReturn { Value: null })
          body.RemoveAt(body.Count - 1);

        return new PyFunctionDef(name, parameters, this.Annotation(lambda.Type.Signature!.Result), body);
      } finally {
        this._frames.AddRange(frames);
        --this._lambdaDepth;
      }
    }

    /// <summary>A variable of the function's own with a name that says what it holds.</summary>
    private string Temporary(string prefix) {
      var name = PythonNaming.Free(prefix, candidate => this._localNames.Contains(candidate) || this._moduleNames.Contains(candidate));
      this._localNames.Add(name);
      return name;
    }

    /// <summary>The expressions of function values; <see langword="null"/> for any other.</summary>
    private PyExpression? FunctionExpression(IrExpr expression) {
      switch (expression) {
        case IrLambda lambda:
          return this.Lambda(lambda);
        case IrInvoke invoke:
          var callee = this.Expression(invoke.Callee);
          return new PyCall(callee, [.. invoke.Arguments.Select(this.Expression)]);
        case IrFunctionRef reference when this._functions.TryGetValue(reference.Function, out var function):
          return Name(function);
        case IrBoundMethod bound:
          if (bound.Receiver is null)
            return new PyAttribute(Name(this.TypeNameOf(this._owners[bound.Method.Function])), this.MethodName(bound.Method));

          return new PyAttribute(this.Place(bound.Receiver), this.MethodName(bound.Method));
        case IrBind bind:
          if (this.IsCell(bind.Local))
            return this.Helper(PythonHelpers.Put, Name(this.LocalName(bind.Local)), this.Expression(bind.Value));

          return new PyNamedExpression(this.LocalName(bind.Local), this.Expression(bind.Value));
        default:
          return null;
      }
    }

    /// <summary>
    /// A declaration of a local that holds one lambda for good: the nested function of the local's own
    /// name, which a recursive local function calls by it.
    /// </summary>
    private bool HeldFunction(IrStmt statement, List<PyStatement> written) {
      switch (statement) {
        case IrDeclare { Initializer: IrLambda lambda } declare when this._held.TryGetValue(declare.Local, out var held) && held == lambda && !this.IsCell(declare.Local):
          written.Add(this.NestedFunction(lambda, this.LocalName(declare.Local), lambda.Captures.Where(this.IsCell).ToList(), this.AssignedCaptures(lambda).ToList()));
          return true;
        case IrDeclare { Initializer: null } declare when this._held.ContainsKey(declare.Local) && !this.IsCell(declare.Local):
          return true;
        case IrAssign { Value: IrLambda lambda } assign when this._held.TryGetValue(assign.Local, out var held) && held == lambda && !this.IsCell(assign.Local):
          written.Add(this.NestedFunction(lambda, this.LocalName(assign.Local), lambda.Captures.Where(this.IsCell).ToList(), this.AssignedCaptures(lambda).ToList()));
          return true;
        default:
          return false;
      }
    }

  }

}
