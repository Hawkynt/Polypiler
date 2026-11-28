using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>The value of a key of a dictionary of values as an optional value, none where the key is absent.</summary>
  internal static CSharpHelper FindValue { get; } = new("FindValue", "TValue?", CollectionsNamespace + "Dictionary<TKey, TValue> map, TKey key", [
    "TValue value;",
    "if (map.TryGetValue(key, out value)) {",
    "  return value;",
    "}",
    "return null;"
  ], "<TKey, TValue>", "where TValue : struct");

  /// <summary>The value of a key of a dictionary of references, <c>null</c> where the key is absent.</summary>
  internal static CSharpHelper FindReference { get; } = new("FindReference", "TValue", CollectionsNamespace + "Dictionary<TKey, TValue> map, TKey key", [
    "TValue value;",
    "map.TryGetValue(key, out value);",
    "return value;"
  ], "<TKey, TValue>", "where TValue : class");

  /// <summary>The value of a key of the slots of a map of values as an optional value.</summary>
  internal static CSharpHelper FindSlotValue { get; } = new("FindSlotValue", "TValue?", CSharpCollectionClasses.KeyedSlotsName + "<TKey, TValue> map, TKey key", [
    "TValue value;",
    "if (map.TryGetValue(key, out value)) {",
    "  return value;",
    "}",
    "return null;"
  ], "<TKey, TValue>", "where TValue : struct");

  /// <summary>The value of a key of the slots of a map of references, <c>null</c> where the key is absent.</summary>
  internal static CSharpHelper FindSlotReference { get; } = new("FindSlotReference", "TValue", CSharpCollectionClasses.KeyedSlotsName + "<TKey, TValue> map, TKey key", [
    "TValue value;",
    "map.TryGetValue(key, out value);",
    "return value;"
  ], "<TKey, TValue>", "where TValue : class");

  /// <summary><c>Dictionary.TryAdd</c> where the framework lacks it: a key present keeps its value.</summary>
  internal static CSharpHelper TryAdd { get; } = new("TryAdd", "bool", CollectionsNamespace + "Dictionary<TKey, TValue> map, TKey key, TValue value", [
    "if (map.ContainsKey(key)) {",
    "  return false;",
    "}",
    "map.Add(key, value);",
    "return true;"
  ], "<TKey, TValue>");

  private sealed partial class Writer {

    /// <summary>Whether every map and set of the module stands in the slots of the nested class, by its order.</summary>
    private Dictionary<MapOrder, bool>? _slotted;

    /// <summary>The nested classes the module's maps and sets use, in the order they are written.</summary>
    private readonly SortedSet<string> _classes = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Whether the maps and sets of an order stand in the slots of the nested class rather than in
    /// <c>Dictionary</c> and <c>HashSet</c>. Those enumerate the slots of .NET Core 3.0's rules: in a
    /// profile that can run on .NET Framework, whose enumerator also refuses a removal, a checked loop
    /// whose body may change a map or a set needs the slots; JavaScript's order needs them where a
    /// removal is reachable, which changes the order of .NET's slots, or a live loop's body may change
    /// what it walks, which .NET's enumerator refuses.
    /// </summary>
    private bool Slotted(IrType type) {
      this._slotted ??= this.Slotting();
      return this._slotted[type.Order] || (type.Kind == IrTypeKind.Set && !this.HasApi("HashSet<T>"));
    }

    private Dictionary<MapOrder, bool> Slotting() {
      var loops = this._module.Functions.SelectMany(function => KeyedLoops(function.Body)).ToList();
      var nodes = this._module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrCollection>().ToList();
      var framework = !this.HasApi("Dictionary<TKey,TValue> enumeration of .NET Core 3.0");
      // A live loop that may change what it walks, which .NET's enumerator refuses, and a checked one on .NET Framework, which also refuses a removal.
      bool Changing(MapOrder order) => loops.Any(loop => loop.Collection.Type.Order == order && (loop.Mode == EnumerationMode.Live || framework) && (MayChangeKeyed(loop.Body) || IrSequences.Suspends(loop.Body)));
      var removes = nodes.Any(node => node.Op is IrCollectionOp.MapRemove or IrCollectionOp.SetRemove && node.Arguments[0].Type.Order == MapOrder.Insertion);
      return new() { [MapOrder.SlotReuse] = Changing(MapOrder.SlotReuse), [MapOrder.Insertion] = removes || Changing(MapOrder.Insertion) };
    }

    private static IEnumerable<IrForEachCollection> KeyedLoops(IrStmt statement)
      => (statement is IrForEachCollection { Collection.Type.Kind: IrTypeKind.Map or IrTypeKind.Set } loop ? [loop] : Enumerable.Empty<IrForEachCollection>())
        .Concat((statement is IrBlock block ? block.Statements : IrTree.Blocks(statement)).SelectMany(KeyedLoops));

    /// <summary>Whether a block changes a map or a set itself or calls what may.</summary>
    private static bool MayChangeKeyed(IrBlock body) => IrTree.Nodes(body).Any(node =>
      node is IrCall or IrMethodCall or IrNew || node is IrCollection { Arguments: [{ Type.Kind: IrTypeKind.Map or IrTypeKind.Set }, ..] } operation && IrCollections.Mutates(operation.Op));

    /// <summary>Whether the profile has an API of the table.</summary>
    private bool HasApi(string name) => this._table.Rows.First(row => row.Name == name).IsIn(this._profile);

    /// <summary>The names of the nested classes of the collections in this class, by their own names.</summary>
    private Dictionary<string, string> _nested = [];

    /// <summary>A nested class the module uses, by name, as code reaches it.</summary>
    private string Nested(string name) {
      this._classes.Add(name);
      var spelled = this._nested[name];
      return this._inClass ? $"{this._outer}.{spelled}" : spelled;
    }

    /// <summary>A text with the names of the nested classes as this class spells them.</summary>
    private string Renamed(string text) => this._nested.Where(pair => pair.Key != pair.Value)
      .Aggregate(text, (renamed, pair) => System.Text.RegularExpressions.Regex.Replace(renamed, $@"\b{pair.Key}\b", pair.Value));

    /// <summary>The declaration of a helper, which may take a nested class of the collections.</summary>
    private CsMethod Nesting(CSharpHelper helper)
      => helper == FindSlotValue || helper == FindSlotReference
        ? (CsMethod)CSharpFragments.Member(this.Renamed(string.Join('\n', helper.Lines(helper.Name))))
        : HelperDeclaration(helper);

    /// <summary>The comparer a map or set of floating-point keys needs where the framework's hashes the NaNs and the zeros apart, or <see langword="null"/>.</summary>
    private CsExpression? KeyComparer(IrType key)
      => key.IsFloat && !this.HasApi("EqualityComparer<double>.Default normalising NaN and -0")
        ? new CsObjectCreation(new CsType(this.Nested(key == IrType.F32 ? CSharpCollectionClasses.SingleKeysName : CSharpCollectionClasses.FloatKeysName)), [])
        : null;

    /// <summary>The C# type of a map or a set: <c>Dictionary</c>, <c>HashSet</c>, or the slots of the nested class.</summary>
    private string KeyedTypeName(IrType type) {
      var key = this.TypeName(type.Key ?? type.Element!);
      var value = type.Kind == IrTypeKind.Map ? this.TypeName(type.Element!) : "bool";
      if (this.Slotted(type))
        return $"{this.Nested(CSharpCollectionClasses.KeyedSlotsName)}<{key}, {value}>";

      return type.Kind == IrTypeKind.Map ? $"{CollectionsNamespace}Dictionary<{key}, {value}>" : $"{CollectionsNamespace}HashSet<{key}>";
    }

    /// <summary>An operation on a map or a set, as a member of its C# type.</summary>
    private (CsExpression Node, int Binding) Keyed(IrCollection operation) {
      var arguments = operation.Arguments;
      var type = IrCollections.Constructs(operation.Op) ? operation.Type : arguments[0].Type;
      var slotted = this.Slotted(type);
      if (operation.Op is IrCollectionOp.MapNew or IrCollectionOp.SetNew) {
        var comparer = this.KeyComparer(type.Key ?? type.Element!);
        List<CsExpression> parameters = slotted ? [new CsBooleanLiteral(type.Order == MapOrder.SlotReuse), comparer ?? new CsNullLiteral()] : comparer is null ? [] : [comparer];
        return (new CsObjectCreation(new CsType(this.TypeName(type)), parameters), Primary);
      }

      var target = this.Operand(arguments[0], Primary);
      var operands = arguments.Skip(1).Select(this.Stored).ToList();
      CsExpression Call(string method, IEnumerable<CsExpression> given) => new CsInvocation(new CsMemberAccess(target, method), [.. given]);
      CsExpression Helper(CSharpHelper helper) => new CsInvocation(CSharpFragments.Name(this.Helper(helper)), [this.Stored(arguments[0]), .. operands]);
      switch (operation.Op) {
        case IrCollectionOp.MapCount or IrCollectionOp.SetCount:
          return (new CsMemberAccess(target, "Count"), Primary);
        case IrCollectionOp.MapGet:
          return (slotted ? Call("Get", operands) : new CsElementAccess(target, operands[0]), Primary);
        case IrCollectionOp.MapPut:
          return slotted ? (Call("Put", operands), Primary) : (new CsAssignment(new CsElementAccess(target, operands[0]) { Type = type.Element }, "=", operands[1]), Loose);
        case IrCollectionOp.MapTryGet: {
          // The type arguments are written out: a constant key is a literal of another C# type, from which the compiler infers no TKey.
          var references = type.Element!.IsObject;
          var helper = slotted ? references ? FindSlotReference : FindSlotValue : references ? FindReference : FindValue;
          var name = $"{this.Helper(helper)}<{this.TypeName(type.Key!)}, {this.TypeName(type.Element!)}>";
          return (new CsInvocation(CSharpFragments.Name(name), [this.Stored(arguments[0]), .. operands]), Primary);
        }
        case IrCollectionOp.MapTryAdd when !slotted && !this.HasApi("Dictionary<TKey,TValue>.TryAdd"):
          return (Helper(TryAdd), Primary);
        case IrCollectionOp.SetAdd when slotted:
          return (Call("TryAdd", [.. operands, new CsBooleanLiteral(true)]), Primary);
        case IrCollectionOp.SetContains when slotted:
          return (Call("ContainsKey", operands), Primary);
      }

      var method = operation.Op switch {
        IrCollectionOp.MapAdd or IrCollectionOp.SetAdd => "Add",
        IrCollectionOp.MapTryAdd => "TryAdd",
        IrCollectionOp.MapContainsKey => "ContainsKey",
        IrCollectionOp.SetContains => "Contains",
        IrCollectionOp.MapRemove or IrCollectionOp.SetRemove => "Remove",
        _ => "Clear"
      };
      return (Call(method, operands), Primary);
    }

    /// <summary>
    /// A loop over a map or a set: a <c>foreach</c> over a <c>Dictionary</c> - its entries, its keys or
    /// its values - or a <c>HashSet</c>; over the slots of the nested class a loop by slot that skips the
    /// empty ones and, when checked, compares the version of the map before every step, also the last.
    /// </summary>
    private void KeyedLoop(IrForEachCollection loop, List<CsStatement> statements, CsExpression collection) {
      var type = loop.Collection.Type;
      var key = loop.Element;
      var value = loop.Value;
      if (!this.Slotted(type)) {
        if (type.Kind == IrTypeKind.Set || value is null || key is null) {
          var single = (key ?? value)!;
          var source = type.Kind == IrTypeKind.Set ? collection : new CsMemberAccess(collection, key is null ? "Values" : "Keys");
          var name = this.Declare(single);
          statements.Add(new CsForEach(new CsType(this.TypeName(single.Type)), name, source, new CsBlock(this.Body(loop.Body, null))));
          return;
        }

        var entry = this.Fresh("entry");
        var entryType = $"{CollectionsNamespace}KeyValuePair<{this.TypeName(key.Type)}, {this.TypeName(value.Type)}>";
        CsStatement[] parts = [
          new CsLocalDeclaration(new CsType(this.TypeName(key.Type)), this.Declare(key), new CsMemberAccess(new CsName(entry), "Key")),
          new CsLocalDeclaration(new CsType(this.TypeName(value.Type)), this.Declare(value), new CsMemberAccess(new CsName(entry), "Value"))
        ];
        statements.Add(new CsForEach(new CsType(entryType), entry, collection, new CsBlock([.. parts, .. this.Body(loop.Body, null)])));
        return;
      }

      // The slots of the nested class, walked by index: the collection evaluated once.
      var map = loop.Collection is IrLocalRef ? collection : new CsName(this.Fresh("map")) { Type = type };
      if (map != collection)
        statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(type)), ((CsName)map).Identifier, collection));

      var slot = this.Fresh("slot");
      var checks = loop.Mode == EnumerationMode.Checked;
      var body = new List<CsStatement>();
      CsExpression? condition = new CsBinary(new CsName(slot), "<", new CsMemberAccess(map, "Slots"));
      if (checks) {
        var version = this.Fresh("version");
        statements.Add(new CsLocalDeclaration(new CsType("int"), version, new CsMemberAccess(map, "Version")));
        body.Add(new CsExpressionStatement(new CsInvocation(new CsMemberAccess(map, "Check"), [new CsName(version)])));
        body.Add(new CsIf(new CsBinary(new CsName(slot), ">=", new CsMemberAccess(map, "Slots")), new CsBreak(), null));
        condition = null;
      }

      body.Add(new CsIf(new CsPrefixUnary("!", new CsInvocation(new CsMemberAccess(map, "IsUsed"), [new CsName(slot)])), new CsContinue(), null));
      if (key is not null)
        body.Add(new CsLocalDeclaration(new CsType(this.TypeName(key.Type)), this.Declare(key), new CsInvocation(new CsMemberAccess(map, "KeyAt"), [new CsName(slot)])));

      if (value is not null)
        body.Add(new CsLocalDeclaration(new CsType(this.TypeName(value.Type)), this.Declare(value), new CsInvocation(new CsMemberAccess(map, "ValueAt"), [new CsName(slot)])));

      body.AddRange(this.Body(loop.Body, null));
      statements.Add(new CsFor(new CsLocalDeclaration(new CsType("int"), slot, new CsIntegerLiteral(0)), condition, [new CsPrefixUnary("++", new CsName(slot))], new CsBlock(body)));
    }

    /// <summary>The nested classes the module uses, as members of the static class.</summary>
    private IEnumerable<CsMember> CollectionClasses() => this._classes.Select(name => CSharpFragments.Member(this.Renamed(name switch {
      CSharpCollectionClasses.KeyedSlotsName => CSharpCollectionClasses.KeyedSlots,
      CSharpCollectionClasses.FloatKeysName => CSharpCollectionClasses.FloatKeys,
      CSharpCollectionClasses.AssertionExceptionName => CSharpCollectionClasses.AssertionException,
      CSharpCollectionClasses.UninitializedName => CSharpCollectionClasses.Uninitialized,
      CSharpCollectionClasses.VariantDateName => CSharpCollectionClasses.VariantDate,
      _ => CSharpCollectionClasses.SingleKeys
    })) with { Leading = CsTrivia.Blank });

  }

}
