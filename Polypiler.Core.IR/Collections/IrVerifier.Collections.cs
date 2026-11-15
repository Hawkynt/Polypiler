using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// An operation on a collection: the collection is one of the kind the operation takes, the
    /// operands have the types its signature gives and the node has the type of its value.
    /// </summary>
    private void Collection(IrCollection operation) {
      foreach (var argument in operation.Arguments)
        this.Expression(argument);

      var kind = IrCollections.Kind(operation.Op);
      var constructs = IrCollections.Constructs(operation.Op);
      var collection = constructs ? operation.Type : operation.Arguments.FirstOrDefault()?.Type;
      if (collection is null || collection.Kind != kind) {
        this.Require(false, $"{operation.Op} is applied to a value of the type {collection?.ToString() ?? "nothing"}, which is no {kind.ToString().ToLowerInvariant()}");
        return;
      }

      this.Require(collection.IsStorable, $"the collection type {collection} holds elements no collection holds");
      this.Declared(collection);
      var (operands, result) = IrCollections.Signature(operation.Op, collection);
      var given = operation.Arguments.Skip(constructs ? 0 : 1).ToList();
      this.Require(given.Count == operands.Count, $"{operation.Op} takes {operands.Count} operands, not {given.Count}");
      for (var index = 0; index < operands.Count && index < given.Count; ++index)
        this.Require(given[index].Type.IsAssignableTo(operands[index]), $"operand {index + 1} of {operation.Op} has the type {given[index].Type}, expected {operands[index]}");

      this.Require(result is not null, $"{operation.Op} of a {collection}, whose elements have no absent value");
      this.Require(result is null || operation.Type == result, $"{operation.Op} has the type {operation.Type}, expected {result}");
    }

    /// <summary>
    /// A loop over a collection: the element - the key of a map - and the value are locals of the
    /// function of the types the collection holds, in scope in the body only; only a map has a value.
    /// </summary>
    private void ForEachCollection(IrForEachCollection loop) {
      this.Expression(loop.Collection);
      var type = loop.Collection.Type;
      if (!type.IsCollection) {
        this.Require(false, $"a loop over a value of the type {type}, which is no collection");
        return;
      }

      var map = type.Kind == IrTypeKind.Map;
      this.Require(loop.Element is not null || (map && loop.Value is not null), "a loop over a collection without an element");
      this.Require(map || loop.Value is null, $"a loop over a {type} with a value");
      if (loop.Element is { } element)
        this.Require(element.Type == (type.Key ?? type.Element), $"the element '{element.Name}' of a loop over a {type} has the type {element.Type}");

      if (loop.Value is { } value)
        this.Require(value.Type == type.Element, $"the value '{value.Name}' of a loop over a {type} has the type {value.Type}");

      var declared = new[] { loop.Element, loop.Value }.OfType<IrLocal>().ToList();
      foreach (var local in declared) {
        this.Require(function.Locals.Contains(local), $"local '{local.Name}' is not among the locals of the function");
        this.Require(this._visible.Add(local), $"local '{local.Name}' is declared twice");
      }

      this.Loop(loop.Body);
      this._visible.ExceptWith(declared);
    }

  }

}
