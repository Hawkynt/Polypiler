using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>
  /// The environment of the passes of one loop at ECMAScript 5, whose <c>var</c> belongs to the whole
  /// function: an object made anew every pass, whose properties are the variables of the pass that a
  /// closure captures.
  /// </summary>
  /// <param name="Name">The variable that holds the object of the current pass.</param>
  /// <param name="Variables">Each variable of the pass with its property; those of every pass of a for are copied from the loop's variable.</param>
  private sealed record Environment(string Name, List<(IrLocal Local, string Key, bool PerIteration)> Variables);

  private sealed partial class Writer {

    /// <summary>The variable that holds <c>this</c> for the function expressions of a method at ECMAScript 5.</summary>
    private const string Self = "$self";

    /// <summary>The locals of the function being written that hold one lambda for good, written as a function of their own.</summary>
    private IReadOnlyDictionary<IrLocal, IrLambda> _held = new Dictionary<IrLocal, IrLambda>();

    /// <summary>The environments of the loops of the function being written, by loop.</summary>
    private readonly Dictionary<IrStmt, Environment> _environments = new(ReferenceEqualityComparer.Instance);

    /// <summary>The variables that live in the environment of a pass while its body is written, with the environment and the property; two locals of one name and type are two variables here.</summary>
    private readonly Dictionary<IrLocal, (string Environment, string Key)> _inEnvironment = new(ReferenceEqualityComparer.Instance);

    /// <summary>The loops of variables of every pass whose variables the loop's own header declares.</summary>
    private readonly HashSet<IrFor> _headed = new(ReferenceEqualityComparer.Instance);

    /// <summary>How many function expressions around the expression being written stand in for lambdas.</summary>
    private int _functionDepth;

    /// <summary>Whether a function expression of the method being written reads <c>this</c>.</summary>
    private bool _self;

    /// <summary>Whether the level has arrow functions, or writes function expressions in their place.</summary>
    private bool Arrows => this.Native("=>");

    /// <summary>
    /// What the function about to be written needs for its function values: the locals written as
    /// functions of their own, and at ECMAScript 5 the environments of the loops whose variables of a
    /// pass a closure captures - a variable declared in the body, or a variable of every pass of a for.
    /// </summary>
    private void Prepare(IrBlock body) {
      this._environments.Clear();
      this._inEnvironment.Clear();
      this._headed.Clear();
      this._self = false;
      this._functionDepth = 0;
      var captured = IrClosures.Captured(body);
      if (captured.Count > 0 && !this.Native("let per-iteration binding"))
        this.Environments(body, null, captured);

      var placed = this._environments.Values.SelectMany(environment => environment.Variables).Select(variable => variable.Local).ToHashSet();
      this._held = IrClosures.Held(body).Where(pair => !placed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private void Environments(IrStmt statement, IrStmt? loop, IReadOnlySet<IrLocal> captured) {
      foreach (var lambda in IrTree.Expressions(statement).SelectMany(IrClosures.Outermost))
        this.Environments(lambda.Body, null, captured);

      switch (statement) {
        case IrWhile or IrDoWhile:
          foreach (var block in IrTree.Blocks(statement))
            this.Environments(block, statement, captured);

          return;
        case IrFor { } counted:
          foreach (var variable in counted.PerIteration.Where(captured.Contains))
            this.Place(counted, variable, true);

          this.Environments(counted.Body, counted, captured);
          this.Environments(counted.Step, loop, captured);
          return;
        case IrDeclare declare when loop is not null && captured.Contains(declare.Local):
          this.Place(loop, declare.Local, false);
          return;
      }

      foreach (var block in statement is IrBlock own ? [own] : IrTree.Blocks(statement))
        foreach (var inner in block.Statements)
          this.Environments(inner, loop, captured);
    }

    private void Place(IrStmt loop, IrLocal variable, bool perIteration) {
      if (!this._environments.TryGetValue(loop, out var environment))
        this._environments[loop] = environment = new($"$c{this._environments.Count + 1}", []);

      var key = this.Name(variable.Name);
      for (var number = 1; environment.Variables.Any(existing => existing.Key == key); ++number)
        key = $"{this.Name(variable.Name)}{number}";

      environment.Variables.Add((variable, key, perIteration));
    }

    /// <summary>What a function declares first for its function values: the environments of its loops, and <c>this</c> for its function expressions.</summary>
    private IEnumerable<JsStatement> Prologue() {
      if (this._self)
        yield return new JsVariableDeclaration("var", [new JsDeclarator(Self, new JsThis())]);

      if (this._environments.Count > 0)
        yield return new JsVariableDeclaration("var", [.. this._environments.Values.Select(environment => new JsDeclarator(environment.Name, null))]);
    }

    /// <summary>The name a variable is read and written with while it lives in the environment of a pass, or <see langword="null"/>.</summary>
    private string? InEnvironment(IrLocal local)
      => this._inEnvironment.TryGetValue(local, out var place) ? $"{place.Environment}.{place.Key}" : null;

    /// <summary>
    /// The statements of function values: a local that holds one lambda for good declared with it -
    /// <c>const f = (…) =&gt; …</c>, at ECMAScript 5 <c>var f = function f(…) { … }</c> - where the IR
    /// gives it the lambda; a variable of a function type declared without a value as <c>null</c>; a
    /// variable of a pass in its environment. <see langword="false"/> for any other statement.
    /// </summary>
    private bool FunctionStatement(IrStmt statement, List<JsStatement> statements) {
      switch (statement) {
        case IrDeclare declare when this.InEnvironment(declare.Local) is { } place:
          if (declare.Initializer is not null)
            statements.Add(new JsExpressionStatement(new JsAssignment(Dotted(place), "=", this.Value(declare.Initializer))));

          return true;
        case IrDeclare declare when this._held.ContainsKey(declare.Local):
          var name = this.Declare(declare.Local);
          if (declare.Initializer is IrLambda lambda)
            statements.Add(this.Held(name, lambda));

          return true;
        case IrAssign { Value: IrLambda assigned } assign when this._held.ContainsKey(assign.Local):
          statements.Add(this.Held(this.Local(assign.Local), assigned));
          return true;
        case IrDeclare { Initializer: null, Local.Type.IsFunction: true } declare:
          statements.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(declare.Local), new JsNull())]));
          return true;
        default:
          return false;
      }
    }

    private JsVariableDeclaration Held(string name, IrLambda lambda) {
      var (node, _) = this.Lambda(lambda);
      if (node is JsFunctionExpression function)
        node = function with { Name = name };

      return new JsVariableDeclaration(this.Native("const") ? "const" : "var", [new JsDeclarator(name, node)]);
    }

    /// <summary>
    /// A lambda: an arrow function where the level has one - an expression body for one return -
    /// else a function expression, which reads <c>this</c> of a method as <c>$self</c>. At ECMAScript 5
    /// a closure over variables of a pass is made by a function that takes the environments of their
    /// passes, so that it keeps the environment of the pass it was made in.
    /// </summary>
    private (JsExpression Node, bool Atomic) Lambda(IrLambda lambda) {
      var outer = this._taken.Keys.ToHashSet();
      var arrows = this.Arrows;
      var parameters = lambda.Parameters.Select(this.Declare).ToList();
      ++this._functionDepth;
      JsExpression node;
      if (arrows && lambda.Body.Statements is [IrReturn { Value: { } value }])
        node = new JsArrowFunction(parameters, null, this.Value(value));
      else if (arrows)
        node = new JsArrowFunction(parameters, new JsBlock(this.Statements(lambda.Body)), null);
      else
        node = new JsFunctionExpression(null, parameters, new JsBlock(this.Statements(lambda.Body)));

      --this._functionDepth;
      foreach (var name in this._taken.Keys.Where(name => !outer.Contains(name)).ToList())
        this._taken.Remove(name);

      var environments = lambda.Captures.Where(this._inEnvironment.ContainsKey).Select(capture => this._inEnvironment[capture].Environment).Distinct().ToList();
      if (environments.Count == 0)
        return (node, false);

      var maker = new JsFunctionExpression(null, environments, new JsBlock([new JsReturn(node)]));
      return (new JsCall(Parenthesized(maker), [.. environments.Select(Id)]), true);
    }

    /// <summary>
    /// The nodes of function values: a lambda, a function of the module as its name, a call of a
    /// function value, and a bound method as the closure over its receiver evaluated once,
    /// <c>((o) =&gt; (a) =&gt; o.m(a))(receiver)</c>, which <c>bind</c> is not used for.
    /// </summary>
    private (JsExpression Node, bool Atomic) FunctionValue(IrExpr expression) {
      switch (expression) {
        case IrLambda lambda:
          return this.Lambda(lambda);
        case IrFunctionRef reference:
          return (Id(this.Name(reference.Function)), true);
        case IrInvoke invocation:
          var callee = this.Operand(invocation.Callee);
          return (new JsCall(callee, [.. invocation.Arguments.Select(this.Value)]), true);
        default:
          return this.Write(Closure((IrBoundMethod)expression));
      }
    }

    /// <summary>A bound method as the closures of the IR that mean it: a function of the receiver that returns a function of the arguments calling the method.</summary>
    private static IrExpr Closure(IrBoundMethod bound) {
      var signature = bound.Type.Signature!;
      var arguments = signature.Parameters.Select((type, index) => new IrLocal($"a{index + 1}", type)).ToList();
      var receiver = bound.Receiver is null ? null : new IrLocal("o", bound.Receiver.Type);
      var call = new IrMethodCall(signature.Result, bound.Method, receiver is null ? null : new IrLocalRef(receiver), [.. arguments.Select(argument => new IrLocalRef(argument))]) { Kind = bound.Kind };
      var inner = IrLambda.Of(arguments, signature.Result, [], new IrBlock([signature.Result == IrType.Void ? new IrExprStmt(call) : new IrReturn(call)]));
      if (receiver is null)
        return inner;

      var outer = IrLambda.Of([receiver], bound.Type, [], new IrBlock([new IrReturn(inner)]));
      return new IrInvoke(bound.Type, outer, [bound.Receiver!]);
    }

    /// <summary>
    /// The body of a loop. At ECMAScript 5 a loop with an environment makes a new one every pass, the
    /// variables of every pass copied in from the loop's own; where the level has <c>let</c>, a for
    /// whose header cannot declare its variables of every pass copies them into variables of the
    /// body. A variable of every pass the body writes is copied back in a <c>finally</c>, which runs
    /// before the step however the pass ends.
    /// </summary>
    private List<JsStatement> LoopBody(IrStmt loop, IrBlock body) {
      if (this._environments.TryGetValue(loop, out var environment))
        return this.InEnvironment(environment, body);

      if (loop is not IrFor { PerIteration.Count: > 0 } counted || this._headed.Contains(counted) || !this.Native("let per-iteration binding"))
        return this.Statements(body);

      var outer = this._taken.Keys.ToHashSet();
      var names = counted.PerIteration.ToDictionary(variable => variable, this.Local);
      var copies = new List<JsStatement>();
      var back = new List<JsStatement>();
      foreach (var variable in counted.PerIteration) {
        var inner = this.Declare(variable);
        copies.Add(new JsVariableDeclaration("let", [new JsDeclarator(inner, Id(names[variable]))]));
        if (CSharpLikeWrites(body, variable))
          back.Add(new JsExpressionStatement(new JsAssignment(Id(names[variable]), "=", Id(inner))));
      }

      var statements = this.Statements(body);
      foreach (var variable in counted.PerIteration)
        this._renamed[variable] = names[variable];

      foreach (var name in this._taken.Keys.Where(name => !outer.Contains(name)).ToList())
        this._taken.Remove(name);

      return back.Count == 0 ? [.. copies, .. statements] : [.. copies, new JsTry(new JsBlock(statements), null, null, new JsBlock(back))];
    }

    private List<JsStatement> InEnvironment(Environment environment, IrBlock body) {
      var properties = environment.Variables.Where(variable => variable.PerIteration).Select(variable => new JsProperty(variable.Key, this.Variable(variable.Local))).ToList();
      // A variable lives in the environment of the innermost loop that declares it, which is no other loop's.
      foreach (var (local, key, _) in environment.Variables)
        this._inEnvironment[local] = (environment.Name, key);

      var statements = this.Statements(body);
      foreach (var (local, _, _) in environment.Variables)
        this._inEnvironment.Remove(local);

      var back = environment.Variables.Where(variable => variable.PerIteration && CSharpLikeWrites(body, variable.Local))
        .Select(variable => (JsStatement)new JsExpressionStatement(new JsAssignment(this.Variable(variable.Local), "=", new JsMember(Id(environment.Name), variable.Key)))).ToList();
      var made = new JsExpressionStatement(new JsAssignment(Id(environment.Name), "=", new JsObject(properties)));
      return back.Count == 0 ? [made, .. statements] : [made, new JsTry(new JsBlock(statements), null, null, new JsBlock(back))];
    }

    /// <summary>Whether a body writes a variable: assigns it or steps it, also inside a lambda.</summary>
    private static bool CSharpLikeWrites(IrBlock body, IrLocal variable)
      => IrClosures.Within(body).Any(statement => statement is IrAssign assign && assign.Local == variable)
        || IrTree.Nodes(body).Any(node => node is IrStep { Target: IrLocalRef reference } && reference.Local == variable);

    /// <summary>
    /// Where the level has <c>let</c>: a for of variables of every pass whose declarations stand right
    /// in front of it, and that nothing after it in the block names, declares them in its header -
    /// <c>for (let i = 0; …)</c>, which is exactly a variable of every pass. Returns how many
    /// statements of the block it wrote, or 0.
    /// </summary>
    private int HeadedFor(IrBlock block, int index, List<JsStatement> statements) {
      if (!this.Native("let per-iteration binding"))
        return 0;

      var count = 0;
      while (index + count < block.Statements.Count && block.Statements[index + count] is IrDeclare)
        ++count;

      if (count == 0 || index + count >= block.Statements.Count || block.Statements[index + count] is not IrFor { PerIteration.Count: > 0 } loop)
        return 0;

      var declares = block.Statements.Skip(index).Take(count).Cast<IrDeclare>().ToList();
      var tail = declares.Skip(declares.Count - loop.PerIteration.Count).ToList();
      if (tail.Count != loop.PerIteration.Count || !tail.Select(declare => declare.Local).ToHashSet().SetEquals(loop.PerIteration)
          || block.Statements.Skip(index + count + 1).SelectMany(IrClosures.Named).Any(loop.PerIteration.Contains))
        return 0;

      foreach (var declare in declares.Take(declares.Count - tail.Count))
        this.Statement(declare, statements);

      var declarators = tail.Select(declare => {
        var initializer = declare.Initializer is null ? null : this.Value(declare.Initializer);
        return new JsDeclarator(this.Declare(declare.Local), initializer);
      }).ToList();
      this._headed.Add(loop);
      this.For(loop, statements, new JsVariableDeclaration("let", declarators));
      return count + 1;
    }

  }

}
