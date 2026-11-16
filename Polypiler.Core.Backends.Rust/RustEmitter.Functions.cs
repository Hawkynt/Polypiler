using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The Rust name of every variable of the function being written.</summary>
    private Dictionary<IrLocal, string> _locals = [];

    /// <summary>The declarations whose binding is assigned more than once on some path, or again in a loop: <c>let mut</c>.</summary>
    private HashSet<IrDeclare> _mutable = new(ReferenceEqualityComparer.Instance);

    /// <summary>The variables of the function some expression reads.</summary>
    private HashSet<IrLocal> _read = [];

    /// <summary>The loops and switches around the statement being written, innermost last.</summary>
    private readonly List<Breakable> _breakables = [];

    private int _labels;

    /// <summary>A loop, a switch or a labelled block the statements inside may leave.</summary>
    /// <param name="Kind">What it is.</param>
    /// <param name="Label">The label it has once a <c>break</c> or <c>continue</c> named it.</param>
    /// <param name="Step">The statements a <c>continue</c> of a <c>for</c> runs first, already written.</param>
    private sealed class Breakable(BreakableKind kind, string label, IReadOnlyList<RsStatement>? step = null) {

      public BreakableKind Kind { get; } = kind;

      public string Label { get; } = label;

      public IReadOnlyList<RsStatement>? Step { get; } = step;

      /// <summary>Whether a statement named the label, so that the node has to carry it.</summary>
      public bool Used { get; set; }

      /// <summary>Whether a <c>continue</c> of the loop left the labelled block of a <c>do</c>-<c>while</c> body.</summary>
      public bool Continued { get; set; }

    }

    private enum BreakableKind { Loop, Switch, ContinueBlock }

    /// <summary>A function: its signature, the parameters it reassigns bound again as <c>let mut</c>, its body with the value of its last statement as the tail.</summary>
    private RsFunction Function(IrFunction function) {
      this._construct = $"the function '{function.Name}'";
      this._labels = 0;
      this._breakables.Clear();
      this._function = function;
      this._definition = this._definitions.GetValueOrDefault(function.Name);
      this._standIns = this._definition?.Parameters.ToDictionary(parameter => parameter.StandIn, parameter => parameter.Name) ?? [];
      this._arrayFacts = this._arrays.GetValueOrDefault(function.Name) ?? new();
      this._arrayParameters = function.Parameters.Select((parameter, index) => (parameter, index)).Where(pair => pair.parameter.Type.IsArray).ToDictionary(pair => pair.parameter, pair => pair.index);
      if (this._arrayFacts.Problem is { } problem)
        this.Refuse(problem);

      this.Analyse(function);
      this.FieldAliases(function);
      var name = this._functions[function.Name];
      var member = this._members.GetValueOrDefault(function.Name);
      if (member is { Kind: MemberKind.Instance })
        this._locals[function.Parameters[0]] = "self";

      if (this._bodiless.Contains(function.Name))
        return new RsFunction(name, "", [], null, RsBlock.Empty);
      if (IrSequences.IsIterator(function))
        return this.Iterator(function, name);

      var parameters = function.Parameters.Select((parameter, index) => new RsParameter(this._locals[parameter], this.ParameterType(function, index))).ToList();
      var generics = "";
      if (this._free.TryGetValue(function.Name, out var owner)) {
        // The object of a method of a hierarchy: a reference to its struct, or to any type of its class's trait.
        var polymorphic = this._traits.Contains(owner);
        parameters[0] = parameters[0] with { TypeName = polymorphic ? "&T" : "&" + this.StructName(owner) };
        generics = polymorphic ? $"<T: {this._types[owner]} + ?Sized>" : "";
      }

      if (member is { Kind: MemberKind.Maker })
        return this.Maker(new RsFunction(name, "pub", parameters, null, new RsBlock([], null)), member);

      var returns = this.ResultType(function);
      if (this._throwing.Contains(function.Name))
        returns = Fallible(returns);
      var statements = new List<RsStatement>();
      foreach (var parameter in function.Parameters.Where(this._reassigned.Contains)) {
        // A parameter given a string or a new array of its own holds a copy it owns from the start.
        var copied = parameter.Type.IsString || parameter.Type.IsArray && !this.IsReference(parameter);
        var nullable = copied && this.IsNullableArray(parameter);
        var type = parameter.Type.IsString ? OwnedString : nullable ? this.NullableArrayType(parameter.Type, false) : parameter.Type.IsArray && copied ? $"Vec<{this.ElementType(parameter.Type)}>" : null;
        var own = parameter.Type.IsObject && !this.MayBeNull(parameter);
        statements.Add(new RsLet(this._locals[parameter], true, type,
          nullable ? new RsMethodCall(new RsPath(this._locals[parameter]), "map", [new RsPath($"<[{this.ElementType(parameter.Type)}]>::to_vec")])
          : copied ? new RsMethodCall(new RsPath(this._locals[parameter]), "to_vec", [])
          : own ? this.Cloned(new RsPath(this._locals[parameter]))
          : new RsPath(this._locals[parameter])));
      }

      foreach (var parameter in function.Parameters.Where(this._shared.Contains))
        statements.Add(this.SharedDeclaration(parameter, new RsPath(this._locals[parameter]) { Type = parameter.Type }));
      statements.AddRange(this.NestedFunctions());
      this.Statements(function.Body.Statements, statements);
      if (this._throwing.Contains(function.Name) && function.ReturnType == IrType.Void && !(statements.Count > 0 && Diverges(statements[^1])))
        statements.Add(new RsExpressionStatement(this.Returned(null)));
      var body = Tidy.Function(new RsBlock(statements, null), function.ReturnType == IrType.Void && !this._throwing.Contains(function.Name));
      var lifetime = Lifetime(function, this._arrayFacts).Length > 0 ? "'a" : "";
      return new RsFunction(name, function.IsPublic || this._free.ContainsKey(function.Name) ? "pub" : "", parameters, returns, body) {
        Generics = generics.Length > 0 ? generics : this._definition?.Generics(lifetime) ?? (lifetime.Length > 0 ? $"<{lifetime}>" : ""),
        Where = this._definition?.Where ?? []
      };
    }

    /// <summary>The function being written.</summary>
    private IrFunction _function = new("", false, IrType.Void, [], [], new IrBlock([]));

    /// <summary>The parameters the body assigns.</summary>
    private HashSet<IrLocal> _reassigned = [];

    /// <summary>
    /// Names the variables of a function and finds out which are read and which bindings need
    /// <c>mut</c>: a binding is assigned at most once on every path through the function unless an
    /// assignment stands in a loop that the declaration is outside of, or two assignments follow one
    /// another. A variable nothing reads takes a leading underscore, as a binding Rust need not warn about.
    /// </summary>
    private void Analyse(IrFunction function) {
      this._read = [];
      this._mutable = new(ReferenceEqualityComparer.Instance);
      this._reassigned = [];
      var bindings = new Dictionary<IrLocal, Binding>();
      foreach (var parameter in function.Parameters)
        bindings[parameter] = new Binding(null, 0) { Count = 1 };

      var parameterBindings = function.Parameters.ToDictionary(parameter => parameter, parameter => bindings[parameter]);
      this.Count(function.Body.Statements, bindings, 0);
      foreach (var (parameter, binding) in parameterBindings)
        if (binding.Count > 1 || binding.Looped)
          this._reassigned.Add(parameter);

      // The bodies of the lambdas, whose own bindings need mut as a function's do.
      this._lambdaReassigned = [];
      foreach (var lambda in Lambdas(function.Body)) {
        var lambdaParameters = lambda.Parameters.ToDictionary(parameter => parameter, _ => new Binding(null, 0) { Count = 1 });
        foreach (var (parameter, binding) in lambdaParameters)
          bindings[parameter] = binding;

        this.Count(lambda.Body.Statements, bindings, 0);
        foreach (var (parameter, binding) in lambdaParameters)
          if (binding.Count > 1 || binding.Looped)
            this._lambdaReassigned.Add(parameter);
      }

      this.Closures(function);
      this._reassigned.ExceptWith(this._shared);

      this._ownedStringParameters = [.. this._reassigned.Where(parameter => parameter.Type.IsString)];
      this._fieldWritten = FieldWrites(function);
      foreach (var parameter in function.Parameters.Where(this._fieldWritten.Contains))
        this._reassigned.Add(parameter);

      // A variable named as a function the body calls would hide the function: rustc then calls the variable (E0618).
      var taken = ArrayOwnership.Statements(function.Body).SelectMany(IrTree.Nodes).OfType<IrCall>()
        .Select(call => this._functions.GetValueOrDefault(call.Function)).OfType<string>().ToHashSet(StringComparer.Ordinal);
      this._locals = [];
      var declared = function.Parameters.Concat(function.Locals).Concat(Lambdas(function.Body).SelectMany(lambda => lambda.Parameters.Concat(lambda.Locals))).Distinct().ToList();
      foreach (var local in declared)
        this._locals[local] = RustNames.Unique([local.Name], name => (this._read.Contains(local) ? "" : "_") + RustNames.Snake(name, this.Edition).Replace("r#", "", StringComparison.Ordinal), taken)[0];

      this._locals = this._locals.ToDictionary(pair => pair.Key, pair => RustNames.IsKeyword(pair.Value, this.Edition) ? "r#" + pair.Value : pair.Value);
      this._taken = taken;
    }

    /// <summary>One binding of a variable: its declaration and how often it is assigned on the worst path.</summary>
    private sealed class Binding(IrDeclare? declaration, int depth) {

      public IrDeclare? Declaration { get; } = declaration;

      public int Depth { get; } = depth;

      public int Count { get; set; }

      public bool Looped { get; set; }

    }

    /// <summary>Counts the assignments of the bindings along a list of statements, one after the other.</summary>
    private void Count(IReadOnlyList<IrStmt> statements, Dictionary<IrLocal, Binding> bindings, int depth) {
      foreach (var statement in statements)
        this.Count(statement, bindings, depth);
    }

    private void Count(IrStmt statement, Dictionary<IrLocal, Binding> bindings, int depth) {
      switch (statement) {
        case IrBlock block:
          this.Count(block.Statements, bindings, depth);
          break;
        case IrDeclare declare: {
          if (declare.Initializer is { } initializer)
            this.CountIn(initializer, bindings, depth);
          var binding = new Binding(declare, depth) { Count = declare.Initializer is null ? 0 : 1 };
          bindings[declare.Local] = binding;
          break;
        }
        case IrAssign assign:
          this.CountIn(assign.Value, bindings, depth);
          this.Assigned(assign.Local, bindings, depth);
          break;
        case IrExprStmt expression:
          this.CountIn(expression.Expression, bindings, depth);
          break;
        case IrStore store:
          this.CountIn(store.Array, bindings, depth);
          this.CountIn(store.Index, bindings, depth);
          this.CountIn(store.Value, bindings, depth);
          break;
        case IrFieldStore store:
          this.CountIn(store.Target, bindings, depth);
          this.CountIn(store.Value, bindings, depth);
          break;
        case IrIf choice: {
          this.CountIn(choice.Condition, bindings, depth);
          var before = Snapshot(bindings);
          this.Count(choice.Then.Statements, bindings, depth);
          var afterThen = Snapshot(bindings);
          Restore(bindings, before);
          if (choice.Else is { } otherwise)
            this.Count(otherwise.Statements, bindings, depth);
          Merge(bindings, afterThen);
          break;
        }
        case IrSwitch choice: {
          this.CountIn(choice.Value, bindings, depth);
          var before = Snapshot(bindings);
          var worst = Snapshot(bindings);
          foreach (var section in choice.Sections) {
            Restore(bindings, before);
            this.Count(section.Body.Statements, bindings, depth);
            Merge(worst, bindings);
          }

          Restore(bindings, worst);
          break;
        }
        case IrWhile loop:
          this.CountIn(loop.Condition, bindings, depth + 1);
          this.Count(loop.Body.Statements, bindings, depth + 1);
          break;
        case IrDoWhile loop:
          this.Count(loop.Body.Statements, bindings, depth + 1);
          this.CountIn(loop.Condition, bindings, depth + 1);
          break;
        case IrFor loop:
          this.CountIn(loop.Condition, bindings, depth + 1);
          this.Count(loop.Body.Statements, bindings, depth + 1);
          this.Count(loop.Step.Statements, bindings, depth + 1);
          break;
        case IrReturn { Value: { } value }:
          this.CountIn(value, bindings, depth);
          break;
        case IrThrow thrown:
          this.CountIn(thrown.Exception, bindings, depth);
          break;
        case IrGlobalAssign assign:
          this.CountIn(assign.Value, bindings, depth);
          break;
        case IrTry attempt: {
          // A catch clause or a finally may run after any part of the body: every path is taken as the worst.
          this.Count(attempt.Body.Statements, bindings, depth);
          foreach (var clause in attempt.Catches) {
            if (clause.Filter is { } filter)
              this.CountIn(filter, bindings, depth);
            this.Count(clause.Body.Statements, bindings, depth);
          }

          if (attempt.Finally is { } final)
            this.Count(final.Statements, bindings, depth);
          break;
        }
        case IrForEachSequence loop:
          this.CountIn(loop.Sequence, bindings, depth);
          bindings[loop.Element] = new Binding(null, depth + 1) { Count = 1 };
          this.Count(loop.Body.Statements, bindings, depth + 1);
          break;
        case IrYield produced:
          this.CountIn(produced.Value, bindings, depth);
          break;
        case IrYieldFrom delegated:
          this.CountIn(delegated.Sequence, bindings, depth);
          break;
        case IrForEachCollection loop:
          this.CountIn(loop.Collection, bindings, depth);
          if (loop.Element is { } loopElement)
            bindings[loopElement] = new Binding(null, depth + 1) { Count = 1 };
          if (loop.Value is { } loopValue)
            bindings[loopValue] = new Binding(null, depth + 1) { Count = 1 };
          this.Count(loop.Body.Statements, bindings, depth + 1);
          break;
      }
    }

    private static Dictionary<Binding, (int, bool)> Snapshot(Dictionary<IrLocal, Binding> bindings)
      => bindings.Values.Distinct().ToDictionary(binding => binding, binding => (binding.Count, binding.Looped));

    private static void Restore(Dictionary<IrLocal, Binding> bindings, Dictionary<Binding, (int Count, bool Looped)> snapshot) {
      foreach (var (binding, (count, looped)) in snapshot) {
        binding.Count = count;
        binding.Looped = looped;
      }
    }

    /// <summary>Takes for every binding the larger count of the current state and a snapshot: the worse of two paths.</summary>
    private static void Merge(Dictionary<IrLocal, Binding> bindings, Dictionary<Binding, (int Count, bool Looped)> other) {
      foreach (var (binding, (count, looped)) in other) {
        binding.Count = Math.Max(binding.Count, count);
        binding.Looped |= looped;
      }
    }

    private static void Merge(Dictionary<Binding, (int Count, bool Looped)> worst, Dictionary<IrLocal, Binding> bindings) {
      foreach (var binding in bindings.Values.Distinct())
        worst[binding] = worst.TryGetValue(binding, out var known) ? (Math.Max(known.Count, binding.Count), known.Looped | binding.Looped) : (binding.Count, binding.Looped);
    }

    /// <summary>An assignment to a variable: one more on this path, and a loop's if the declaration is outside the loop.</summary>
    private void Assigned(IrLocal local, Dictionary<IrLocal, Binding> bindings, int depth) {
      if (!bindings.TryGetValue(local, out var binding)) {
        binding = new Binding(null, 0);
        bindings[local] = binding;
      }

      binding.Count++;
      if (depth > binding.Depth)
        binding.Looped = true;

      if (binding.Declaration is { } declaration && (binding.Count > 1 || binding.Looped))
        this._mutable.Add(declaration);
    }

    /// <summary>The reads and the assignments (steps) of an expression.</summary>
    private void CountIn(IrExpr expression, Dictionary<IrLocal, Binding> bindings, int depth) {
      foreach (var node in Nodes(expression)) {
        if (node is IrLocalRef reference)
          this._read.Add(reference.Local);
        if (node is IrStep { Target: IrLocalRef target })
          this.Assigned(target.Local, bindings, depth);
        if (node is IrBind bind)
          this.Assigned(bind.Local, bindings, depth);
      }
    }

    /// <summary>Every expression below an expression and the expression itself.</summary>
    private static IEnumerable<IrExpr> Nodes(IrExpr expression) => IrTree.Nodes(expression);

    /// <summary>A new label of the function, <c>l0</c>, <c>s1</c>, …</summary>
    private string NewLabel(string prefix) => prefix + this._labels++;

  }

}
