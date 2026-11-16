using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// The type of an object literal: a record of its keys in source order and the types of their
  /// values. A literal the subset does not take - a computed or repeated key, a key that is no name,
  /// a spread, a method, a getter or a setter - has no type, and the lowering names what it is.
  /// </summary>
  private JsType Record(ObjectExpression literal) {
    var keys = new List<string>();
    var types = new List<JsType>();
    foreach (var node in literal.Properties) {
      if (Plain(node) is not { } property || keys.Contains(property.Key))
        return JsType.Unknown;

      keys.Add(property.Key);
      types.Add(this.Of(property.Value));
    }

    // A literal whose shape has met objects of a class is an object of the class made for it (#1388).
    if (!this._literals.Exists(seen => seen.Literal == literal))
      this._literals.Add((this._scope, literal));

    return this.Shapes.Classes.RecordOf(keys) is { } made ? JsType.Object(made) : JsType.Record(new RecordShape(this.Shapes, keys, types));
  }

  /// <summary>Every object literal of the file the subset takes, with the function it stands in.</summary>
  private readonly List<(FunctionModel? Scope, ObjectExpression Literal)> _literals = [];

  /// <summary>
  /// Lets the values of the literals of a record class reach its fields (#1388): a key the class it
  /// derives from lacks is a field of its own, made in the order of the keys; a key it has is the
  /// inherited field, which the value joins; an inherited field the literal lacks holds undefined.
  /// Says whether a type changed - a class made or moved by a join among them.
  /// </summary>
  private bool RecordFields() {
    var changed = false;
    if (this.Shapes.Classes.TakeChanged()) {
      this._known.Clear();
      changed = true;
    }

    var scope = this._scope;
    foreach (var (model, literal) in this._literals.ToList()) {
      var properties = literal.Properties.Select(Plain).OfType<(string Key, Node Value)>().ToList();
      if (this.Shapes.Classes.RecordOf([.. properties.Select(property => property.Key)]) is not { } made)
        continue;

      this._scope = model;
      foreach (var (key, value) in properties) {
        var field = made.Field(key);
        if (field is null) {
          field = new ClassField(key, new Variable(key, value, false), null);
          made.Fields.Add(field);
        }

        changed |= this.Reach(field.Variable, this.Of(value).Stored(), value);
      }

      foreach (var lacking in made.Base!.Lineage.SelectMany(type => type.Fields).Where(field => !field.IsPrivate && properties.TrueForAll(property => property.Key != field.Key)))
        changed |= this.Reach(lacking.Variable, JsType.UndefinedValue, literal);
    }

    this._scope = scope;
    return changed;
  }

  /// <summary>
  /// <c>r[key] = v</c> in a loop over the keys of <c>o</c> (#1388): where <c>r</c> holds the one object
  /// a <c>new C(…)</c> made, a key of any class <c>o</c> may be of that <c>C</c> lacks is a field of the
  /// class synthesized for that <c>new</c>; the value is <c>o[key]</c>'s field of that class, or what
  /// <c>v</c> is. Says whether a type changed.
  /// </summary>
  private bool KeyedStore(Flow flow) {
    if (flow.Target!.Creation is not { } creation || this.Names.ClassNamed(creation.Callee) is not { Frozen: null, IsLibrary: false } created
        || this.Of(flow.Source!) is not { Kind: JsKind.Object, IsAbsent: false, Class: { } source })
      return false;

    var store = (AssignmentExpression)flow.Node;
    var copied = store.Right is MemberExpression { Computed: true, Object: Identifier read, Property: Identifier } && flow.Source is Identifier origin && read.Name == origin.Name;
    var changed = false;
    foreach (var type in this.Shapes.Classes.Below(source)) {
      var lacking = type.Keys.Where(key => created.Field(key) is null && !created.HasInstanceMember(key)).ToList();
      if (lacking.Count == 0)
        continue;

      var extension = this.Shapes.Classes.Extend(creation, created, flow.Source!, type, lacking);
      foreach (var key in lacking) {
        var field = extension.OwnField(key);
        if (field is null) {
          field = new ClassField(key, new Variable(key, store, false), null);
          extension.Fields.Add(field);
        }

        var value = copied ? type.Field(key)?.Variable.Type ?? JsType.Unknown : this.Of(store.Right);
        changed |= this.Reach(field.Variable, value.Stored(), store.Right);
      }
    }

    return changed;
  }

  /// <summary>A property of an object literal or a pattern that is a name and a value, or <see langword="null"/>.</summary>
  public static (string Key, Node Value)? Plain(Node node) => node is Property { Kind: PropertyKind.Init, Computed: false, Method: false, Key: Identifier { Name: var key }, Value: var value }
    ? (key, value)
    : null;

}
