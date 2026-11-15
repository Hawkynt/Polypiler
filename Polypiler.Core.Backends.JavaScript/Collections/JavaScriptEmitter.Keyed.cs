using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>Whether the maps and sets of an order stand in the slots of <c>$Keyed</c> rather than in <c>Map</c> and <c>Set</c>.</summary>
    private Dictionary<MapOrder, bool>? _keyedSlots;

    /// <summary>
    /// Whether a map or a set is a <c>$Keyed</c>: at a level without <c>Map</c>; where its keys are
    /// pairs of halves, which a <c>Map</c> tells apart by identity; and in .NET's order of slots - which
    /// a <c>Map</c> keeps only while nothing is removed - where a removal is reachable, where its keys
    /// are floating-point numbers, of which a <c>Map</c> keeps <c>+0</c> where .NET keeps the first key,
    /// and where a checked loop's body may change a map or a set, which a <c>Map</c> does not count.
    /// </summary>
    private bool Slotted(IrType type) {
      var key = type.Key ?? type.Element!;
      if (!this.Native(type.Kind == IrTypeKind.Map ? "Map" : "Set") || key.IsLong && this.Halves)
        return true;

      this._keyedSlots ??= this.KeyedSlots();
      return (type.Order == MapOrder.SlotReuse && key.IsFloat) || this._keyedSlots[type.Order];
    }

    private Dictionary<MapOrder, bool> KeyedSlots() {
      var functions = this._module!.Functions;
      var removes = functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrCollection>()
        .Any(node => node.Op is IrCollectionOp.MapRemove or IrCollectionOp.SetRemove && node.Arguments[0].Type.Order == MapOrder.SlotReuse);
      var loops = functions.SelectMany(function => IrTree.Blocks(function.Body).SelectMany(Loops)).ToList();
      // A checked loop that may change what it walks, whose count of insertions a Map does not keep.
      bool Changing(MapOrder order) => loops.Any(loop => loop is { Mode: EnumerationMode.Checked, Collection.Type.Kind: IrTypeKind.Map or IrTypeKind.Set }
        && loop.Collection.Type.Order == order && MayChangeKeyed(loop.Body));
      return new() { [MapOrder.SlotReuse] = removes || Changing(MapOrder.SlotReuse), [MapOrder.Insertion] = Changing(MapOrder.Insertion) };
    }

    /// <summary>Whether a block changes a map or a set itself or calls what may.</summary>
    private static bool MayChangeKeyed(IrBlock body) => IrTree.Nodes(body).Any(node =>
      node is IrCall or IrMethodCall or IrNew || node is IrCollection { Arguments: [{ Type.Kind: IrTypeKind.Map or IrTypeKind.Set }, ..] } operation && IrCollections.Mutates(operation.Op));

    /// <summary>
    /// An operation on a map or a set, with the methods <c>Map</c>, <c>Set</c> and <c>$Keyed</c> share,
    /// and helpers for the ones that trap or say whether they inserted.
    /// </summary>
    private (JsExpression Node, bool Atomic) Keyed(IrCollection operation) {
      var arguments = operation.Arguments;
      if (operation.Op is IrCollectionOp.MapNew or IrCollectionOp.SetNew) {
        var type = operation.Type;
        var key = type.Key ?? type.Element!;
        if (!this.Slotted(type))
          return (new JsNew(Id(type.Kind == IrTypeKind.Map ? "Map" : "Set"), []), true);

        var kind = key.IsObject ? 1 : key.IsLong && this.Halves ? 2 : 0;
        return (new JsNew(Id(this.Use("$Keyed")), [new JsBoolean(type.Order == MapOrder.SlotReuse), Number(kind.ToString(System.Globalization.CultureInfo.InvariantCulture))]), true);
      }

      var target = this.Operand(arguments[0]);
      var operands = arguments.Skip(1).Select(this.Value).ToList();
      JsExpression Call(string method) => new JsCall(new JsMember(target, method), [.. operands]);
      return operation.Op switch {
        IrCollectionOp.MapCount or IrCollectionOp.SetCount => (new JsMember(target, "size"), true),
        IrCollectionOp.MapGet => (this.Helper("$mget", [this.Text(arguments[0]), .. operands]), true),
        IrCollectionOp.MapTryGet => (Call("get"), true),
        IrCollectionOp.MapContainsKey or IrCollectionOp.SetContains => (Call("has"), true),
        IrCollectionOp.MapPut => (Call("set"), true),
        IrCollectionOp.MapAdd => (this.Helper("$madd", [this.Text(arguments[0]), .. operands]), true),
        IrCollectionOp.MapTryAdd => (this.Helper("$mtryadd", [this.Text(arguments[0]), .. operands]), true),
        IrCollectionOp.SetAdd => (this.Helper("$sadd", [this.Text(arguments[0]), .. operands]), true),
        IrCollectionOp.MapRemove or IrCollectionOp.SetRemove => (Call("delete"), true),
        _ => (Call("clear"), true)
      };
    }

    /// <summary>The name of a helper the module uses.</summary>
    private string Use(string helper) {
      this._used.Add(helper);
      return helper;
    }

    /// <summary>
    /// A loop over a map or a set. A <c>Map</c> or <c>Set</c> is walked by its iterator, which is live;
    /// a <c>$Keyed</c> by its slots, skipping the empty ones, reading their count before every pass and,
    /// when checked, comparing its count of insertions before every step, also the last.
    /// </summary>
    private void KeyedLoop(IrForEachCollection loop, List<JsStatement> target, JsExpression map) {
      var type = loop.Collection.Type;
      var key = loop.Element;
      var value = loop.Value;
      var body = new List<JsStatement>();
      if (!this.Slotted(type)) {
        var iterator = this.Declare(new IrLocal("iterator", IrType.I32));
        var step = this.Declare(new IrLocal("step", IrType.I32));
        var entries = type.Kind == IrTypeKind.Set ? "values" : key is null ? "values" : value is null ? "keys" : "entries";
        var current = new JsMember(Id(step), "value");
        var paired = entries == "entries";
        if (key is not null)
          body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(key), paired ? new JsIndex(current, Number("0")) : current)]));

        if (value is not null)
          body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(value), this.Copied(value.Type, paired ? new JsIndex(current, Number("1")) : current))]));

        body.AddRange(this.Statements(loop.Body));
        var next = new JsUnary("!", new JsMember(new JsParenthesized(new JsAssignment(Id(step), "=", new JsCall(new JsMember(Id(iterator), "next"), []))), "done"));
        var declaration = new JsVariableDeclaration(this.Let, [new JsDeclarator(iterator, new JsCall(new JsMember(map, entries), [])), new JsDeclarator(step, null)]);
        target.Add(new JsFor(declaration, next, null, new JsBlock(body)));
        return;
      }

      var slot = this.Declare(new IrLocal("slot", IrType.I32));
      var declarators = new List<JsDeclarator> { new(slot, Number("0")) };
      JsExpression? test = Infix(Id(slot), "<", new JsMember(new JsMember(map, "$u"), "length"));
      if (loop.Mode == EnumerationMode.Checked) {
        var version = this.Declare(new IrLocal("version", IrType.I32));
        declarators.Add(new JsDeclarator(version, new JsMember(map, "$v")));
        body.Add(new JsExpressionStatement(this.Helper("$mvcheck", [map, Id(version)])));
        body.Add(new JsIf(Infix(Id(slot), ">=", new JsMember(new JsMember(map, "$u"), "length")), new JsBreak(), null));
        test = null;
      }

      body.Add(new JsIf(new JsUnary("!", new JsIndex(new JsMember(map, "$u"), Id(slot))), new JsContinue(), null));
      if (key is not null)
        body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(key), new JsIndex(new JsMember(map, "$k"), Id(slot)))]));

      if (value is not null)
        body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(value), this.Copied(value.Type, new JsIndex(new JsMember(map, "$w"), Id(slot))))]));

      body.AddRange(this.Statements(loop.Body));
      target.Add(new JsFor(new JsVariableDeclaration(this.Let, declarators), test, new JsUpdate("++", false, Id(slot)), new JsBlock(body)));
    }

    /// <summary>A value read out of a collection: a struct copied with its copy helper, any other value as it is.</summary>
    private JsExpression Copied(IrType type, JsExpression value) => type.StructType is { } copied ? Invoke(this.StructHelper("copy", copied), value) : value;

  }

}
