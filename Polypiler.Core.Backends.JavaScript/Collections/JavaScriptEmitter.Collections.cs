using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Whether the lists of the module count their changes in a property <c>$v</c>: where a checked
    /// loop over a list has a body that may change a list - by an operation on one or through a call -
    /// every change of every list goes through a helper that counts, and the loop compares the count
    /// before every step.
    /// </summary>
    private bool? _versioned;

    private bool Versioned => this._versioned ??= this._module is { } module && module.Functions
      .SelectMany(function => IrTree.Blocks(function.Body).SelectMany(Loops))
      .Any(loop => loop is { Mode: EnumerationMode.Checked, Collection.Type.IsList: true } && MayChangeAList(loop.Body));

    private static IEnumerable<IrForEachCollection> Loops(IrStmt statement)
      => (statement is IrForEachCollection loop ? [loop] : Enumerable.Empty<IrForEachCollection>())
        .Concat((statement is IrBlock block ? block.Statements : IrTree.Blocks(statement)).SelectMany(Loops));

    /// <summary>Whether a block changes a list itself or calls what may.</summary>
    private static bool MayChangeAList(IrBlock body) => IrTree.Nodes(body).Any(node =>
      node is IrCall or IrMethodCall or IrNew || node is IrCollection { Arguments: [{ Type.IsList: true }, ..] } operation && IrCollections.Mutates(operation.Op));

    /// <summary>
    /// An operation on a list as an <c>Array</c>: <c>length</c>, <c>push</c>, <c>pop</c>,
    /// <c>indexOf</c>, and helpers for the operations that check an index - which trap with the
    /// <c>ArgumentOutOfRangeError</c> - and for the searches JavaScript has no method of.
    /// </summary>
    private (JsExpression Node, bool Atomic) Collection(IrCollection operation) {
      if (IrCollections.Kind(operation.Op) != IrTypeKind.List)
        return this.Keyed(operation);

      var arguments = operation.Arguments;
      var element = (IrCollections.Constructs(operation.Op) ? operation.Type : arguments[0].Type).Element!;
      var versioned = this.Versioned;
      switch (operation.Op) {
        case IrCollectionOp.ListNew:
          return (new JsArray([]), true);
        case IrCollectionOp.ListFromArray when element.StructType is { } copied:
          return (Invoke("Array.prototype.map.call", this.Text(arguments[0]), Id(this.StructHelper("copy", copied))), true);
        case IrCollectionOp.ListFromArray:
          return (Invoke("Array.prototype.slice.call", this.Text(arguments[0])), true);
        case IrCollectionOp.ListCount:
          return (new JsMember(this.Operand(arguments[0]), "length"), true);
        case IrCollectionOp.ListGet:
          return (this.Helper("$lget", arguments[0], arguments[1]), true);
        case IrCollectionOp.ListSet:
          return (this.Helper(versioned ? "$lvset" : "$lset", [this.Text(arguments[0]), this.Text(arguments[1]), this.Value(arguments[2])]), true);
        case IrCollectionOp.ListAdd when versioned:
          return (this.Helper("$lvadd", [this.Text(arguments[0]), this.Value(arguments[1])]), true);
        case IrCollectionOp.ListAdd:
          return (new JsCall(new JsMember(this.Operand(arguments[0]), "push"), [this.Value(arguments[1])]), true);
        case IrCollectionOp.ListInsert:
          return (this.Helper(versioned ? "$lvinsert" : "$linsert", [this.Text(arguments[0]), this.Text(arguments[1]), this.Value(arguments[2])]), true);
        case IrCollectionOp.ListRemoveAt:
          return (this.Helper(versioned ? "$lvremoveAt" : "$lremoveAt", arguments[0], arguments[1]), true);
        case IrCollectionOp.ListRemoveLast when versioned:
          return (this.Helper("$lvpop", arguments[0]), true);
        case IrCollectionOp.ListRemoveLast:
          return (new JsCall(new JsMember(this.Operand(arguments[0]), "pop"), []), true);
        case IrCollectionOp.ListClear when versioned:
          return (this.Helper("$lvclear", arguments[0]), true);
        case IrCollectionOp.ListClear:
          return (new JsAssignment(new JsMember(this.Operand(arguments[0]), "length"), "=", Number("0")), false);
        case IrCollectionOp.ListCopyToArray when element.StructType is { } copied:
          return (new JsCall(new JsMember(this.Operand(arguments[0]), "map"), [Id(this.StructHelper("copy", copied))]), true);
        case IrCollectionOp.ListCopyToArray when !element.IsString && !element.IsObject && this.Native(Storage(element)):
          return (new JsNew(Id(Storage(element)), [this.Text(arguments[0])]), true);
        case IrCollectionOp.ListCopyToArray:
          return (new JsCall(new JsMember(this.Operand(arguments[0]), "slice"), []), true);
        case IrCollectionOp.ListIndexOfStrict:
          return (new JsCall(new JsMember(this.Operand(arguments[0]), "indexOf"), [this.Value(arguments[1])]), true);
      }

      // The searches by Equals: a NaN found where the elements are floating-point numbers, the fields compared where they are structs or record classes.
      // JavaScript's includes is SameValueZero: a reference is found by identity only.
      var found = this.IndexOfEquals(element, arguments[0], arguments[1]);
      if (operation.Op == IrCollectionOp.ListIndexOfEquals)
        return (found, true);

      if (operation.Op == IrCollectionOp.ListIncludesSameValueZero)
        return this.Native(".includes of an array")
          ? (new JsCall(new JsMember(this.Operand(arguments[0]), "includes"), [this.Value(arguments[1])]), true)
          : (this.Helper("$lincludes", arguments[0], arguments[1]), true);

      return (Infix(found, ">=", Number("0")), false);
    }

    /// <summary>The index of the first element that equals a value by <c>Equals</c>, or -1.</summary>
    private JsExpression IndexOfEquals(IrType element, IrExpr list, IrExpr value) {
      if (element.IsFloat)
        return this.Helper("$lindexEq", list, value);

      if (element.Declaration is IrStructType or IrClassType { IsRecord: true })
        return this.Helper("$lindexBy", [this.Text(list), this.Value(value), Id(this.StructHelper("equals", element.Declaration))]);

      return new JsCall(new JsMember(this.Operand(list), "indexOf"), [this.Value(value)]);
    }

    /// <summary>
    /// A loop over a list by index: the list evaluated once, its <c>length</c> read before every pass.
    /// A checked loop of a module whose lists count their changes compares the count before every
    /// step, the one that ends the loop included, and traps with the <c>InvalidOperationError</c>.
    /// </summary>
    private void ForEachCollection(IrForEachCollection loop, List<JsStatement> statements) {
      var outer = this._taken.Keys.ToHashSet();
      var target = new List<JsStatement>();
      JsExpression list;
      if (loop.Collection is IrLocalRef reference) {
        list = this.Variable(reference.Local);
      } else {
        var temporary = this.Declare(new IrLocal("list", loop.Collection.Type));
        target.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(temporary, this.Text(loop.Collection))]));
        list = Id(temporary);
      }

      if (!loop.Collection.Type.IsList) {
        this.KeyedLoop(loop, target, list);
        statements.Add(target.Count == 1 ? target[0] : new JsBlock(target));
        foreach (var declared in this._taken.Keys.Where(taken => !outer.Contains(taken)).ToList())
          this._taken.Remove(declared);

        return;
      }

      var index = this.Declare(new IrLocal("index", IrType.I32));
      var checks = loop.Mode == EnumerationMode.Checked && this.Versioned;
      var declarators = new List<JsDeclarator> { new(index, Number("0")) };
      var body = new List<JsStatement>();
      JsExpression? test = Infix(Id(index), "<", new JsMember(list, "length"));
      if (checks) {
        var version = this.Declare(new IrLocal("version", IrType.I32));
        declarators.Add(new JsDeclarator(version, Infix(new JsMember(list, "$v"), "|", Number("0"))));
        body.Add(new JsExpressionStatement(this.Helper("$lvcheck", [list, Id(version)])));
        body.Add(new JsIf(Infix(Id(index), ">=", new JsMember(list, "length")), new JsBreak(), null));
        test = null;
      }

      var element = loop.Element!;
      var name = this.Declare(element);
      JsExpression read = new JsIndex(list, Id(index));
      if (element.Type.StructType is { } copied)
        read = Invoke(this.StructHelper("copy", copied), read);

      body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(name, read)]));
      body.AddRange(this.Statements(loop.Body));
      target.Add(new JsFor(new JsVariableDeclaration(this.Let, declarators), test, new JsUpdate("++", false, Id(index)), new JsBlock(body)));
      statements.Add(target.Count == 1 ? target[0] : new JsBlock(target));
      foreach (var taken in this._taken.Keys.Where(taken => !outer.Contains(taken)).ToList())
        this._taken.Remove(taken);
    }

  }

}
