using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The collection types whose values are shared: an <c>Rc&lt;RefCell&lt;Vec&lt;…&gt;&gt;&gt;</c> every holder reads and changes.</summary>
    private HashSet<IrType> _sharedCollections = [];

    /// <summary>
    /// Finds the collection types a module shares (#1422). A collection of .NET is an object: a field, a
    /// global, an element of another collection or an array, an optional value or a function value that
    /// holds one hands on the same collection to everyone who reads it, and a change through any of them
    /// is seen by all. Such a type - and every collection type named inside it - is shared: its values are
    /// <c>Rc&lt;RefCell&lt;Vec&lt;T&gt;&gt;&gt;</c>, cloned as the <c>Rc</c> where a place takes one. A
    /// collection only variables hold stays the vector one variable owns.
    /// </summary>
    private void SharedCollections(IrModule module) {
      var shared = new HashSet<IrType>();
      void Mark(IrType type) {
        if (type.IsCollection) {
          if (!shared.Add(type))
            return;

          Mark(type.Element!);
          if (type.Key is { } key)
            Mark(key);
        } else {
          Inside(type);
        }
      }

      // The collection types named inside a type: those of its elements, of an optional, of a function's signature.
      void Inside(IrType type) {
        if (type.IsCollection) {
          Mark(type.Element!);
          if (type.Key is { } key)
            Mark(key);
        } else if (type.IsArray && type.Element is { } element) {
          Mark(element);
        } else if (type.IsOptional && type.Underlying is { } underlying) {
          Mark(underlying);
        } else if (type.Signature is { } signature) {
          foreach (var parameter in signature.Parameters)
            Mark(parameter);
          Mark(signature.Result);
        }
      }

      foreach (var declaration in module.Types) {
        if (declaration is IrClassType classType)
          foreach (var field in classType.Fields)
            Mark(field.Type);
        else if (declaration is IrStructType structType)
          foreach (var field in structType.Fields)
            Mark(field.Type);
      }

      foreach (var global in module.Globals)
        Mark(global.Type);

      foreach (var function in module.Functions) {
        foreach (var local in function.Parameters.Concat(function.Locals))
          Inside(local.Type);
        Inside(function.ReturnType);
        foreach (var node in IrTree.Nodes(function.Body))
          Inside(node.Type);
      }

      this._sharedCollections = shared;
    }

    /// <summary>Whether values of a type are shared collections.</summary>
    private bool IsSharedCollection(IrType type) => type.IsCollection && this._sharedCollections.Contains(type);

    /// <summary>The Rust type of a shared collection: <c>Rc&lt;RefCell&lt;Vec&lt;T&gt;&gt;&gt;</c>.</summary>
    private string SharedCollectionType(IrType type) {
      this._uses.Add("std::rc::Rc");
      this._uses.Add("std::cell::RefCell");
      return $"Rc<RefCell<{this.VectorType(type)}>>";
    }

    /// <summary>A new shared collection around a vector: <c>Rc::new(RefCell::new(v))</c>.</summary>
    private RsExpression SharedCollection(RsExpression vector, IrType type) {
      this.SharedCollectionType(type);
      return new RsCall(new RsPath("Rc::new"), [new RsCall(new RsPath("RefCell::new"), [vector])]) { Type = type };
    }

    /// <summary>
    /// An element of a list made of its elements, <c>vec![a, b]</c>, whose type nothing around it says:
    /// an object of a trait converted to the trait object, <c>LinkItemObject::new(..) as Rc&lt;dyn LinkItem&gt;</c>.
    /// </summary>
    private RsExpression Listed(IrExpr element, IrType list) {
      var entry = this.Entry(element, list);
      if (!element.Type.IsObject || !this.IsPolymorphic(list.Element!) || entry is RsCast || element is IrNull)
        return entry;

      var optional = this._nullness.ElementsMayBeNull(list);
      if (optional && entry is RsCall { Callee: RsPath { Name: "Some" }, Arguments: [var some] })
        return new RsCall(new RsPath("Some"), [some is RsCast ? some : this.Upcast(some, false, list.Element!)]) { Type = element.Type };

      return this.Upcast(entry, optional, list.Element!);
    }

    /// <summary>
    /// The array a list holds at an index, in place (#1422): <c>list.borrow()[ply_list_index(i, len)]</c>,
    /// read where an element of it is, never copied. Written in place only in a list of the function's own:
    /// the index of a shared one would borrow it while the change holds it.
    /// </summary>
    private RsExpression ListElement(IrCollection element, bool mutable) {
      var collection = element.Arguments[0];
      if (mutable && this.IsSharedCollection(collection.Type))
        return this.Refuse("an array a shared list holds written in place");

      var at = this.Helper("ply_list_index", IrType.U64, this.Expression(element.Arguments[1]), new RsMethodCall(this.CollectionPlace(collection), "len", []));
      return new RsIndex(this.CollectionPlace(collection, mutable), at) { Type = element.Type };
    }

    /// <summary>
    /// Whether a global array is a table the module never writes - no element stored, filled or copied
    /// into, no array of its type written anywhere it might be shared to - so that a copy of it cannot be
    /// told apart from the array itself (#1422).
    /// </summary>
    private bool IsUnwrittenTable(IrGlobal global) {
      if (!IsLocked(global) || !global.Type.IsArray)
        return false;

      bool Shares(IrExpr target) => target is IrGlobalRef { Global: var other } && other == global || target.Type == global.Type && target is IrFieldLoad or IrLocalRef;
      return !this._module.Functions.SelectMany(function => IrClosures.Within(function.Body)).Any(statement => statement is IrStore store && Shares(store.Array))
        && !this._module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).Any(node => node switch {
          IrStep { Target: IrArrayLoad load } => Shares(load.Array),
          IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy } copy => Shares(copy.Arguments[2]),
          IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig } fill => Shares(fill.Arguments[0]),
          _ => false
        });
    }

    /// <summary>A copy of a global table that nothing writes: <c>ply_with(&amp;TABLE, |table| table.to_vec())</c>.</summary>
    private RsExpression LockedCopy(IrGlobal global) => this.WithLocked(global, table => new RsMethodCall(table, "to_vec", []) { Type = global.Type });

    /// <summary>The <c>Rc</c> of a shared collection where an operation borrows it: a variable as it is, any other value as it is made.</summary>
    private RsExpression SharedHolder(IrExpr collection) => collection switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = collection.Type },
      _ => this.Operand(this.Expression(collection), 15)
    };

    /// <summary>The vector of a shared collection for one operation: <c>c.borrow()</c>, or <c>c.borrow_mut()</c> where it changes it.</summary>
    private RsExpression SharedVector(IrExpr collection, bool mutable)
      => new RsMethodCall(this.SharedHolder(collection), mutable ? "borrow_mut" : "borrow", []) { Type = collection.Type };

    /// <summary>A shared collection where a place takes it: a variable's <c>Rc</c> cloned, any other value as it is.</summary>
    private RsExpression SharedCollectionValue(IrExpr value) => value switch {
      IrLocalRef reference => this.Cloned(new RsReference(false, new RsPath(this._locals[reference.Local]) { Type = value.Type })),
      IrConditional choice => this.Choice(choice, this.SharedCollectionValue),
      _ => this.Expression(value)
    };

    /// <summary>
    /// The arguments of a change of a shared collection bound before it borrows the collection: any value
    /// that may read a collection - through a call, a field, another collection - would otherwise meet
    /// the exclusive borrow of the change.
    /// </summary>
    private RsExpression SharedSettled(IrCollection operation, System.Func<IrCollection, RsExpression> write) {
      var statements = new List<RsStatement>();
      var collection = operation.Arguments[0].Type;
      var arguments = new List<IrExpr> { operation.Arguments[0] };
      foreach (var (argument, position) in operation.Arguments.Select((argument, position) => (argument, position)).Skip(1)) {
        if (argument is IrConst or IrStrConst or IrLocalRef or IrNull) {
          arguments.Add(argument);
          continue;
        }

        // The index of a list is a number; every other argument is an element, a key or a value, bound in the form the collection holds it.
        var index = position == 1 && operation.Op is IrCollectionOp.ListInsert or IrCollectionOp.ListSet or IrCollectionOp.ListRemoveAt;
        var bound = new IrLocal("$" + this._labels++ + ":value", argument.Type);
        this._locals[bound] = this.Temporary(index ? "index" : "value");
        statements.Add(new RsLet(this._locals[bound], false, null, index ? this.Expression(argument) : this.Entry(argument, collection)));
        if (!index)
          this._settledEntries.Add(bound);
        arguments.Add(new IrLocalRef(bound));
      }

      var written = write(operation with { Arguments = arguments });
      return statements.Count == 0 ? written : new RsBlockExpression(new RsBlock(statements, written)) { Type = operation.Type };
    }

  }

}
