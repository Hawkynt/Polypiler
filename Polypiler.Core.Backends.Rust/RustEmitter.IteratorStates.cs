using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The variables an iterator keeps in its struct: every parameter its body names, every variable
    /// named in more than one state, and every variable a finally block pending at a resume point
    /// names, which the struct's <c>drop</c> runs too.
    /// </summary>
    private HashSet<IrLocal> Kept(IrFunction function, List<State> states, Dictionary<int, IReadOnlyList<Region>> pending) {
      var own = function.Parameters.Concat(function.Locals).ToHashSet();
      var first = new Dictionary<IrLocal, int>();
      var kept = new HashSet<IrLocal>();
      for (var index = 0; index < states.Count; ++index)
        foreach (var local in LocalsOf(states[index]).Where(own.Contains))
          if (!first.TryAdd(local, index) && first[local] != index)
            kept.Add(local);

      kept.UnionWith(function.Parameters.Where(first.ContainsKey));
      foreach (var final in pending.Values.SelectMany(regions => regions).Select(region => region.Finally).OfType<IrBlock>()) {
        var declared = Walk(final).OfType<IrDeclare>().Select(declare => declare.Local).ToHashSet();
        kept.UnionWith(LocalsIn(final).Where(local => own.Contains(local) && !declared.Contains(local)));
      }

      return kept;
    }

    /// <summary>The variables a state names.</summary>
    private static IEnumerable<IrLocal> LocalsOf(State state) {
      foreach (var piece in state.Pieces) {
        var locals = piece switch {
          Run run => LocalsIn(run.Statement),
          Unless jump => Referenced(jump.Condition),
          Open open => Referenced(open.Sequence),
          Pull pull => [pull.Element],
          _ => []
        };
        foreach (var local in locals)
          yield return local;
      }

      if (state.Exit is Produce produced)
        foreach (var local in Referenced(produced.Value))
          yield return local;
    }

    /// <summary>The variables a statement declares, assigns or reads, those of its lambdas included.</summary>
    private static IEnumerable<IrLocal> LocalsIn(IrStmt statement)
      => Walk(statement).SelectMany(inner => inner switch {
        IrDeclare declare => [declare.Local],
        IrAssign assign => [assign.Local],
        IrForEachSequence loop => [loop.Element],
        IrForEachCollection loop => new[] { loop.Element, loop.Value }.OfType<IrLocal>(),
        _ => []
      }).Concat(Walk(statement).SelectMany(ExpressionsOf).SelectMany(Referenced));

    /// <summary>The variables an expression reads or assigns.</summary>
    private static IEnumerable<IrLocal> Referenced(IrExpr expression)
      => IrTree.Nodes(expression).Select(node => node switch {
        IrLocalRef reference => reference.Local,
        IrBind bind => bind.Local,
        _ => null
      }).OfType<IrLocal>();

    /// <summary>The type of a variable the struct keeps: refused where it has no first value the struct could start with.</summary>
    private string KeptType(IrLocal local) {
      if (local.Type.IsArray || local.Type.IsCollection || local.Type.IsLazySequence)
        return ((RsPath)this.RefuseIterator($"{Article(local.Type)} kept across a yield")).Name;

      return this.LocalType(local)!;
    }

    /// <summary>The first value of a variable the struct keeps: the zero of its type, <c>None</c>; refused where it has none.</summary>
    private RsExpression Zero(IrLocal local, string type) {
      if (type.StartsWith("Option<", StringComparison.Ordinal))
        return new RsPath("None");
      if (TypeName(local.Type) is not null)
        return Literal(new IrConst(local.Type, 0));
      if (local.Type.IsString)
        return new RsCall(new RsPath("Vec::new"), []);
      if (local.Type.IsEnum || local.Type.IsStruct && local.Type.Declaration is IrStructType { HasDefault: true })
        return new RsCall(new RsPath(type + "::default"), []);

      return this.RefuseIterator($"a variable of the type {local.Type} kept across a yield, which has no first value");
    }

    /// <summary>
    /// The type of a parameter of an iterator: an array or a string borrowed for a lifetime, <c>&amp;'a [T]</c>;
    /// in the struct an object as its own <c>Rc</c>; anything else as the function takes it.
    /// </summary>
    private string KeptParameterType(IrFunction function, IrLocal parameter, string lifetime, bool taken = false) {
      var borrowed = lifetime.Length == 0 ? "&" : $"&{lifetime} ";
      return parameter.Type.IsArray ? $"{borrowed}[{this.ElementType(parameter.Type)}]"
        : parameter.Type.IsString ? $"{borrowed}[u16]"
        : parameter.Type.IsObject && !taken ? this.ObjectType(parameter.Type, this.MayBeNull(parameter))
        : this.ParameterType(function, function.Parameters.ToList().IndexOf(parameter));
    }

    /// <summary>The value of a field of a parameter: the parameter, an object borrowed as <c>&amp;Rc</c> cloned.</summary>
    private RsExpression KeptParameterValue(IrFunction function, IrLocal parameter, string name)
      => parameter.Type.IsObject && this.ParameterType(function, function.Parameters.ToList().IndexOf(parameter)).StartsWith('&')
        ? new RsCall(new RsPath("Rc::clone"), [new RsPath(name)])
        : new RsPath(name);

    /// <summary>Whether a variable is a field of the struct of the iterator being written.</summary>
    private bool IsKept(IrLocal local) => this._locals[local].StartsWith("self.", StringComparison.Ordinal);

    /// <summary>The field of a cursor: <c>self.cursor0</c>.</summary>
    private RsExpression CursorField(int cursor) => new RsField(new RsPath("self"), this._cursors[cursor]);

    /// <summary>
    /// A jump to a state: <c>self.state = n; continue;</c>, where <c>next</c> loops; to the end
    /// <c>self.state = end; return None;</c>.
    /// </summary>
    private List<RsStatement> Jump(int target) => [
      new RsExpressionStatement(new RsAssign(new RsField(new RsPath("self"), StateField), "=", new RsLiteral(target.ToString(CultureInfo.InvariantCulture)))),
      new RsExpressionStatement(target == this._end ? new RsReturn(new RsPath("None")) : new RsContinue(null))
    ];

    /// <summary>The arm of a state: its pieces one after the other, then its exit.</summary>
    private RsBlock Arm(State state) {
      var into = new List<RsStatement>();
      var runs = new List<IrStmt>();
      var ended = false;
      void Flush() {
        if (runs.Count == 0)
          return;

        this.Statements([.. runs], into);
        runs.Clear();
        ended = into.Count > 0 && Diverges(into[^1]);
      }

      foreach (var piece in state.Pieces) {
        if (piece is Run { Statement: IrDeclare declare } && this.IsKept(declare.Local)) {
          if (declare.Initializer is { } initial)
            runs.Add(new IrAssign(declare.Local, initial));
          continue;
        }

        if (piece is Run run) {
          runs.Add(run.Statement);
          continue;
        }

        Flush();
        if (ended)
          break;

        switch (piece) {
          case Unless jump:
            into.Add(new RsExpressionStatement(new RsIf(this.Not(jump.Condition), new RsBlock(this.Jump(jump.Target), null), null)));
            break;
          case Open open:
            into.Add(new RsExpressionStatement(new RsAssign(this.CursorField(open.Cursor), "=", new RsCall(new RsPath("Some"), [this.SequenceValue(open.Sequence, true)]))));
            break;
          case Close close:
            into.Add(new RsExpressionStatement(new RsAssign(this.CursorField(close.Cursor), "=", new RsPath("None"))));
            break;
          default:
            into.Add(this.Pulled((Pull)piece));
            break;
        }
      }

      Flush();
      if (ended)
        return new RsBlock(into, null);

      var self = new RsField(new RsPath("self"), StateField);
      var (next, value) = state.Exit switch {
        Goto jump => (jump.Target, null),
        Produce produced => (produced.Resume, new RsCall(new RsPath("Some"), [this.Produced(produced.Value)])),
        _ => (this._end, (RsExpression)new RsPath("None"))
      };
      if (next == this._end && value is null)
        value = new RsPath("None");

      into.Add(new RsExpressionStatement(new RsAssign(self, "=", new RsLiteral(next.ToString(CultureInfo.InvariantCulture)))));
      if (value is null)
        return new RsBlock(into, null);
      if (this._looping) {
        into.Add(new RsExpressionStatement(new RsReturn(value)));
        return new RsBlock(into, null);
      }

      return new RsBlock(into, value);
    }

    /// <summary>
    /// The next value of a cursor into its variable: <c>let v = match self.cursor0.as_mut().and_then(Iterator::next) { Some(value) =&gt; value, None =&gt; { …jump } };</c>.
    /// </summary>
    private RsStatement Pulled(Pull pull) {
      var source = new RsMethodCall(new RsMethodCall(this.CursorField(pull.Cursor), "as_mut", []), "and_then", [new RsPath("Iterator::next")]);
      var next = new RsMatch(source, [
        new RsMatchArm(["Some(value)"], new RsPath("value")),
        new RsMatchArm(["None"], new RsBlockExpression(new RsBlock(this.Jump(pull.Exit), null)))
      ]);
      if (this.IsKept(pull.Element))
        return new RsExpressionStatement(new RsAssign(new RsPath(this._locals[pull.Element]), "=", next));

      return new RsLet(this._locals[pull.Element], Assigns(this._function.Body.Statements, pull.Element), null, next);
    }

    /// <summary>A value an iterator gives: a string or an object as its own, any other value as it is.</summary>
    private RsExpression Produced(IrExpr value) => value.Type switch {
      { IsString: true } => this.StringValue(value, true),
      { IsObject: true } => value is IrNull ? this.RefuseIterator("a sequence that gives null") : this.ObjectValue(value, false),
      { IsFunction: true } => this.FunctionValue(value),
      _ => this.Expression(value)
    };

    /// <summary>
    /// The body of <c>drop</c>: for every resume point where a finally block is pending, the cursors and
    /// finally blocks pending there, innermost first; <see langword="null"/> where no finally block ever pends.
    /// </summary>
    private RsBlock? Dropped(Dictionary<int, IReadOnlyList<Region>> pending) {
      var finals = new List<IrBlock>();
      string Key(IReadOnlyList<Region> regions) => string.Join(",", regions.Select(region => region.Finally is { } final
        ? "f" + (finals.FindIndex(known => ReferenceEquals(known, final)) is var at and >= 0 ? at : Added(final))
        : "c" + region.Cursor.ToString(CultureInfo.InvariantCulture)));
      int Added(IrBlock final) {
        finals.Add(final);
        return finals.Count - 1;
      }

      var groups = pending.Where(pair => pair.Value.Any(region => region.Finally is not null)).OrderBy(pair => pair.Key).GroupBy(pair => Key(pair.Value)).ToList();
      if (groups.Count == 0)
        return null;

      var self = new RsField(new RsPath("self"), StateField);
      var arms = new List<RsMatchArm>();
      foreach (var group in groups) {
        var statements = new List<RsStatement>();
        foreach (var region in group.First().Value.Reverse()) {
          if (region.Finally is { } final)
            this.Statements(final.Statements, statements);
          else
            statements.Add(new RsExpressionStatement(new RsAssign(this.CursorField(region.Cursor), "=", new RsPath("None"))));
        }

        arms.Add(new RsMatchArm([.. group.Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture))], new RsBlockExpression(new RsBlock(statements, null))));
      }

      if (arms.Count == 1) {
        var test = arms[0].Patterns.Select(pattern => (RsExpression)new RsBinary(self, "==", new RsLiteral(pattern)) { Type = IrType.Bool })
          .Aggregate((left, right) => new RsBinary(left, "||", right) { Type = IrType.Bool });
        return Callable(new RsBlock([new RsExpressionStatement(new RsIf(test, ((RsBlockExpression)arms[0].Body).Block, null))], null));
      }

      arms.Add(new RsMatchArm(["_"], new RsBlockExpression(RsBlock.Empty)));
      return Callable(new RsBlock([new RsExpressionStatement(new RsMatch(self, arms))], null));
    }

    /// <summary>A block whose calls of a function value in a field are written <c>(self.f)(x)</c>, which Rust would read as a method.</summary>
    private static RsBlock Callable(RsBlock block)
      => RustSyntaxRewriter.Rewrite(block, node => node is RsCall { Callee: RsPath { Name: var name } callee } call && name.StartsWith("self.", StringComparison.Ordinal)
        ? call with { Callee = new RsParenthesized(callee) }
        : node);

  }

}
