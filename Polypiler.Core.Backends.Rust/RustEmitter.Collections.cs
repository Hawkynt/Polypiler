using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>
  /// What a lazy sequence is refused as: an iterator function suspends at each <c>yield</c> and goes on
  /// when the next element is asked for, which needs a generator or a state machine - Rust has no stable
  /// generators, and the backend writes no state machine of an iterator function yet. A sequence made
  /// all at once would change what the source does: its effects, the order of a trace, an infinite one.
  /// </summary>

  private sealed partial class Writer {

    /// <summary>The functions of the module, by name, whose collection parameters they change (a fixed point over the calls).</summary>
    private Dictionary<string, HashSet<int>> _changedCollections = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds the collection parameters every function changes: by an operation that writes it, or by
    /// passing it on to a function that changes its parameter.
    /// </summary>
    private void CollectionParameters(IrModule module) {
      this._changedCollections = module.Functions.ToDictionary(function => function.Name, _ => new HashSet<int>(), StringComparer.Ordinal);
      for (var changed = true; changed;) {
        changed = false;
        foreach (var function in module.Functions)
          for (var index = 0; index < function.Parameters.Count; ++index)
            if (function.Parameters[index].Type.IsCollection && !this._changedCollections[function.Name].Contains(index) && this.ChangesCollection(function.Body, function.Parameters[index]))
              changed |= this._changedCollections[function.Name].Add(index);
      }
    }

    /// <summary>Whether statements change a collection variable: an operation that writes it, or a call that changes the parameter it is passed as.</summary>
    private bool ChangesCollection(IrBlock body, IrLocal local) {
      bool Names(IrExpr expression) => expression is IrLocalRef reference && reference.Local == local;
      return IrTree.Nodes(body).Any(node => node is IrCollection operation && !IrCollections.Constructs(operation.Op) && IrCollections.Mutates(operation.Op) && Names(operation.Arguments[0])
        || node is IrCall call && this._changedCollections.TryGetValue(call.Function, out var changed) && changed.Any(position => position < call.Arguments.Count && Names(call.Arguments[position])));
    }

    /// <summary>Whether the function being written changes a collection variable of its own.</summary>
    private bool IsChangedCollection(IrLocal local) => local.Type.IsCollection && this.ChangesCollection(this._function.Body, local);

    /// <summary>
    /// The Rust type of a collection: a list a <c>Vec&lt;T&gt;</c>; a map or a set in .NET's order of
    /// slots a vector of slots, <c>Vec&lt;Result&lt;(K, V), u32&gt;&gt;</c>, an entry <c>Ok</c> and a slot a
    /// removal freed <c>Err(n)</c>, the n-th freed, which the next new key takes - so that enumeration
    /// meets the keys in the order .NET's <c>Dictionary</c> and <c>HashSet</c> do. A key is compared by
    /// <c>==</c>: an integer, a truth value, an enum or a string; any other key is refused.
    /// </summary>
    private string CollectionType(IrType type) {
      var element = type.Element!;
      if (type.IsList)
        return IsValue(element) || element.IsString ? $"Vec<{this.Type(element)}>" : ((RsPath)this.Refuse($"a list of {element}")).Name;

      if (type.Order != MapOrder.SlotReuse)
        return ((RsPath)this.Refuse($"a collection in the order of insertion ({type})")).Name;

      var key = type.Key ?? element;
      if (!(key.IsInteger || key == IrType.Bool || key.IsEnum || key.IsString) || !(IsValue(element) || element.IsString))
        return ((RsPath)this.Refuse($"the collection {type}")).Name;

      return type.Kind == IrTypeKind.Map ? $"Vec<Result<({this.Type(key)}, {this.Type(element)}), u32>>" : $"Vec<Result<{this.Type(element)}, u32>>";
    }

    /// <summary>The type of a collection parameter: a slice it only reads, <c>&amp;[T]</c>, or the vector it changes, <c>&amp;mut Vec&lt;T&gt;</c>.</summary>
    private string CollectionParameterType(IrFunction function, int index) {
      var vector = this.CollectionType(function.Parameters[index].Type);
      return this._changedCollections.GetValueOrDefault(function.Name)?.Contains(index) == true ? "&mut " + vector : $"&[{vector[4..^1]}]";
    }

    /// <summary>A collection as the receiver of an operation: the variable or the value.</summary>
    private RsExpression CollectionPlace(IrExpr collection) => collection switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = collection.Type },
      _ => this.Operand(this.Expression(collection), 15)
    };

    /// <summary>A collection where a helper takes it: a slice of it or the vector, borrowed as such unless the variable is the borrow itself.</summary>
    private RsExpression CollectionArgument(IrExpr collection, bool mutable) {
      var place = this.CollectionPlace(collection);
      return collection is IrLocalRef { Local: var local } && this._function.Parameters.Contains(local) ? place : new RsReference(mutable, place);
    }

    /// <summary>A collection where a function takes it: the borrow its parameter is.</summary>
    private RsExpression CollectionPassed(IrExpr collection, bool mutable) {
      if (collection is not IrLocalRef)
        return this.Refuse("a collection passed on that no variable holds");

      return this.CollectionArgument(collection, mutable);
    }

    /// <summary>A collection where a variable or a result takes it: a new one, the result of a call, or a variable moved for good.</summary>
    private RsExpression CollectionValue(IrExpr value) => value switch {
      IrCollection or IrCall => this.Expression(value),
      IrLocalRef { Local: var local } reference when !this._function.Parameters.Contains(local) && this.IsLastUse(reference) => new RsPath(this._locals[local]) { Type = value.Type },
      _ => this.Refuse("a collection held by two variables")
    };

    /// <summary>
    /// Whether a variable is read for the last time here, so that the collection it holds can be moved:
    /// it is what a function returns outside every <c>try</c>, or no reference to it follows and no loop
    /// around the reference that the variable outlives reads it again on its next round.
    /// </summary>
    private bool IsLastUse(IrLocalRef reference) {
      var local = reference.Local;
      bool Refers(IrExpr node) => node is IrLocalRef other && other.Local == local;
      var statements = ArrayOwnership.Statements(this._function.Body).ToList();
      if (!statements.Any(statement => statement is IrTry) && statements.Any(statement => statement is IrReturn { Value: var returned } && ReferenceEquals(returned, reference)))
        return true;

      if (!ReferenceEquals(IrTree.Nodes(this._function.Body).LastOrDefault(Refers), reference))
        return false;

      return !statements
        .Where(statement => statement is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence)
        .Where(loop => !ArrayOwnership.Statements(new IrBlock([loop])).Any(statement => statement is IrDeclare declaration && declaration.Local == local))
        .Select(loop => IrTree.Nodes(loop).Where(Refers).ToList())
        .Any(inside => inside.Any(node => ReferenceEquals(node, reference)) && inside.Count > 1);
    }

    /// <summary>An element, key or value where a collection takes it: a string as a vector of its own, any other value as it is.</summary>
    private RsExpression Entry(IrExpr value) => value.Type.IsString ? this.StringValue(value) : this.Expression(value);

    /// <summary>A key where a helper compares it: borrowed, a string variable the function owns in place.</summary>
    private RsExpression Key(IrExpr key) => key is IrLocalRef { Type.IsString: true } local && !this.IsBorrowedString(local)
      ? new RsReference(false, new RsPath(this._locals[local.Local]) { Type = key.Type }) { Type = key.Type }
      : new RsReference(false, this.Operand(this.Entry(key), 13)) { Type = key.Type };

    /// <summary>
    /// The operands of a change of a collection that read the collection itself, bound before the
    /// change takes it exclusively: <c>{ let value = ply_map_get(&amp;m, &amp;k)…; ply_map_put(&amp;mut m, k, value) }</c>.
    /// </summary>
    private RsExpression Settled(IrCollection operation, Func<IrCollection, RsExpression> write) {
      if (IrCollections.Constructs(operation.Op) || operation.Arguments[0] is not IrLocalRef { Local: var local } || !IrCollections.Mutates(operation.Op))
        return write(operation);

      var statements = new List<RsStatement>();
      var arguments = new List<IrExpr> { operation.Arguments[0] };
      foreach (var argument in operation.Arguments.Skip(1)) {
        if (!IrTree.Nodes(argument).Any(node => node is IrLocalRef reference && reference.Local == local)) {
          arguments.Add(argument);
          continue;
        }

        var bound = new IrLocal("$" + this._labels++ + ":value", argument.Type);
        this._locals[bound] = this.Temporary("value");
        statements.Add(new RsLet(this._locals[bound], false, null, this.Entry(argument)));
        arguments.Add(new IrLocalRef(bound));
      }

      var written = write(operation with { Arguments = arguments });
      return statements.Count == 0 ? written : new RsBlockExpression(new RsBlock(statements, written)) { Type = operation.Type };
    }

    private RsExpression Helper(string name, IrType type, params IReadOnlyList<RsExpression> arguments)
      => new RsCall(new RsPath(this.StringHelper(name)), arguments) { Type = type };

    /// <summary>An operation on a list, a map or a set.</summary>
    private RsExpression Collection(IrCollection operation) {
      var arguments = operation.Arguments;
      var type = operation.Type;
      if (operation.Op is IrCollectionOp.ListNew or IrCollectionOp.MapNew or IrCollectionOp.SetNew) {
        this.CollectionType(type);
        return new RsCall(new RsPath("Vec::new"), []) { Type = type };
      }

      if (operation.Op == IrCollectionOp.ListFromArray) {
        this.CollectionType(type);
        return new RsMethodCall(this.Receiver(this.Borrow(arguments[0], false)), "to_vec", []) { Type = type };
      }

      var collection = arguments[0];
      this.CollectionType(collection.Type);
      var element = collection.Type.Element!;
      var place = this.CollectionPlace(collection);
      RsExpression Call(string method, params IReadOnlyList<RsExpression> rest) => new RsMethodCall(place, method, rest) { Type = type };
      RsExpression Count() => new RsMethodCall(place, "len", []);
      RsExpression Index(IrExpr index, string helper) => this.Helper(helper, IrType.U64, this.Expression(index), Count());
      switch (operation.Op) {
        case IrCollectionOp.ListCount:
          return this.Cast(Count(), IrType.I32);
        case IrCollectionOp.MapCount or IrCollectionOp.SetCount:
          return this.Cast(new RsMethodCall(new RsMethodCall(new RsMethodCall(place, "iter", []), "flatten", []), "count", []), IrType.I32);
        case IrCollectionOp.ListGet: {
          RsExpression read = new RsIndex(place, Index(arguments[1], "ply_list_index")) { Type = element };
          return element.IsString ? new RsMethodCall(read, "clone", []) { Type = element } : read;
        }
        case IrCollectionOp.ListAdd:
          return Call("push", this.Entry(arguments[1]));
        case IrCollectionOp.ListInsert:
          return Call("insert", Index(arguments[1], "ply_list_insert_index"), this.Entry(arguments[2]));
        case IrCollectionOp.ListRemoveAt:
          return Call("remove", Index(arguments[1], "ply_list_index"));
        case IrCollectionOp.ListRemoveLast:
          return Call("pop");
        case IrCollectionOp.ListClear or IrCollectionOp.MapClear or IrCollectionOp.SetClear:
          return Call("clear");
        case IrCollectionOp.ListContains when element.IsFloat:
          return new RsMethodCall(new RsMethodCall(place, "iter", []), "any", [this.SameValue(element, arguments[1])]) { Type = IrType.Bool };
        case IrCollectionOp.ListContains:
          return Call("contains", this.Key(arguments[1]));
        case IrCollectionOp.ListIndexOfEquals: {
          var found = new RsMethodCall(new RsMethodCall(place, "iter", []), "position", [element.IsFloat ? this.SameValue(element, arguments[1])
            : new RsClosure(false, [new RsParameter("element", "")], this.Binary(new RsPath("element"), "==", this.Key(arguments[1]), IrType.Bool))]);
          return new RsMethodCall(found, "map_or", [Literal(new IrConst(IrType.I32, -1)), new RsClosure(false, [new RsParameter("at", "")], new RsCast(new RsPath("at"), "i32"))]) { Type = IrType.I32 };
        }
        case IrCollectionOp.ListCopyToArray:
          return new RsMethodCall(place, "to_vec", []) { Type = type };
        case IrCollectionOp.MapGet:
          return this.Checked(this.Helper("ply_map_get", type, this.CollectionArgument(collection, false), this.Key(arguments[1])), IrTrap.KeyNotFound);
        case IrCollectionOp.MapTryGet when IsOptionalValue(type):
          return this.Helper("ply_map_get", type, this.CollectionArgument(collection, false), this.Key(arguments[1]));
        case IrCollectionOp.MapContainsKey:
          return new RsMethodCall(this.Helper("ply_map_find", IrType.U64, this.CollectionArgument(collection, false), this.Key(arguments[1])), "is_some", []) { Type = IrType.Bool };
        case IrCollectionOp.MapPut:
          return this.Helper("ply_map_put", type, this.CollectionArgument(collection, true), this.Entry(arguments[1]), this.Entry(arguments[2]));
        case IrCollectionOp.MapAdd:
          return this.Helper("ply_map_add", type, this.CollectionArgument(collection, true), this.Entry(arguments[1]), this.Entry(arguments[2]));
        case IrCollectionOp.MapTryAdd:
          return this.Helper("ply_map_try_add", type, this.CollectionArgument(collection, true), this.Entry(arguments[1]), this.Entry(arguments[2]));
        case IrCollectionOp.MapRemove:
          return this.Helper("ply_map_remove", type, this.CollectionArgument(collection, true), this.Key(arguments[1]));
        case IrCollectionOp.SetAdd:
          return this.Helper("ply_set_add", type, this.CollectionArgument(collection, true), this.Entry(arguments[1]));
        case IrCollectionOp.SetRemove:
          return this.Helper("ply_set_remove", type, this.CollectionArgument(collection, true), this.Key(arguments[1]));
        case IrCollectionOp.SetContains:
          return new RsMethodCall(this.Helper("ply_set_find", IrType.U64, this.CollectionArgument(collection, false), this.Key(arguments[1])), "is_some", []) { Type = IrType.Bool };
        default:
          return this.Refuse($"the collection operation {operation.Op}");
      }
    }

    /// <summary><c>|element| ply_same_value_f64(*element, v)</c>: the equality of floating-point numbers a list's search has, under which a NaN is found.</summary>
    private RsClosure SameValue(IrType element, IrExpr value)
      => new(false, [new RsParameter("element", "")], new RsCall(new RsPath(this.StringHelper(element == IrType.F32 ? "ply_same_value_f32" : "ply_same_value_f64")),
        [new RsUnary("*", new RsPath("element")), this.Expression(value)]) { Type = IrType.Bool });

    /// <summary>
    /// <c>list[i] = v</c>: the index checked first, as the IR evaluates it, then the value; the element written.
    /// </summary>
    private void ListSet(IrCollection operation, List<RsStatement> into) {
      var collection = operation.Arguments[0];
      this.CollectionType(collection.Type);
      var place = this.CollectionPlace(collection);
      var at = this.Temporary("index");
      into.Add(new RsLet(at, false, null, this.Helper("ply_list_index", IrType.U64, this.Expression(operation.Arguments[1]), new RsMethodCall(place, "len", []))));
      into.Add(new RsExpressionStatement(new RsAssign(new RsIndex(place, new RsPath(at)) { Type = operation.Arguments[2].Type }, "=", this.Entry(operation.Arguments[2]))));
    }

    /// <summary>
    /// A loop over a list, a map or a set: <c>for &amp;x in &amp;list</c>, the entries of a map or a set
    /// through <c>iter().flatten()</c>, which skips the freed slots, each element copied into the loop's
    /// variable. A body that changes the collection is refused: .NET traps at the next step, and Rust
    /// would not borrow the collection for the change.
    /// </summary>
    private void ForEachCollection(IrForEachCollection loop, List<RsStatement> into) {
      var collection = loop.Collection;
      this.CollectionType(collection.Type);
      if (collection is not IrLocalRef { Local: var local }) {
        into.Add(new RsExpressionStatement(this.Refuse("a loop over a collection that no variable holds")));
        return;
      }

      if (this.ChangesCollection(loop.Body, local)) {
        into.Add(new RsExpressionStatement(this.Refuse("a collection changed in a loop over it")));
        return;
      }

      var place = new RsPath(this._locals[local]) { Type = collection.Type };
      var iterated = (RsExpression)new RsMethodCall(place, "iter", []);
      if (!collection.Type.IsList)
        iterated = new RsMethodCall(iterated, "flatten", []);

      var entry = this.Temporary("entry");
      var statements = new List<RsStatement>();
      RsExpression Copied(RsExpression value, IrType type) => type.IsString ? new RsMethodCall(value, "clone", []) { Type = type } : new RsUnary("*", value) { Type = type };
      if (collection.Type.Kind == IrTypeKind.Map) {
        RsExpression Part(string field, IrType type) => type.IsString ? new RsMethodCall(new RsField(new RsPath(entry), field), "clone", []) { Type = type } : new RsField(new RsPath(entry), field) { Type = type };
        if (loop.Element is { } key)
          statements.Add(new RsLet(this._locals[key], Assigns(loop.Body.Statements, key), null, Part("0", key.Type)));
        if (loop.Value is { } value)
          statements.Add(new RsLet(this._locals[value], Assigns(loop.Body.Statements, value), null, Part("1", value.Type)));
      } else if (loop.Element is { } element) {
        statements.Add(new RsLet(this._locals[element], Assigns(loop.Body.Statements, element), null, Copied(new RsPath(entry), element.Type)));
      }

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      this.Statements(loop.Body.Statements, statements);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      into.Add(new RsExpressionStatement(new RsFor(breakable.Used ? label : null, entry, iterated, new RsBlock(statements, null))));
    }

  }

}
