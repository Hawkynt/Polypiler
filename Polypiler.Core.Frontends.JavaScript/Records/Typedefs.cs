using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The <c>@typedef {Object}</c> records of a file (#1409): each one that JSDoc names somewhere is a class
/// of read-only fields, one per <c>@property</c> in source order, an optional key - <c>[key]</c> or
/// <c>[key=default]</c> - of its type or <c>undefined</c>, which is what a value that leaves it out holds:
/// the default JSDoc writes down documents what the function does, it is not what JavaScript stores. An
/// object literal where the typedef is stated is a new object of the class. A value of it is never
/// written; it is a class, not a struct of the IR, because a struct has no field that may be absent.
/// </summary>
/// <param name="anchor">The node a diagnostic about a typedef points at: the file.</param>
/// <param name="declared">The typedefs of the file by name; a later one of a name replaces an earlier one.</param>
internal sealed class Typedefs(Node anchor, IReadOnlyDictionary<string, JsDocTypedef> declared) {

  private readonly Dictionary<string, ClassModel?> _made = [];

  /// <summary>The class of every typedef JSDoc has named, in the order they were first named.</summary>
  public List<ClassModel> Used { get; } = [];

  /// <summary>Reads the typedefs of the <c>/** … */</c> comments of a file.</summary>
  /// <param name="anchor">The node a diagnostic about a typedef points at.</param>
  /// <param name="source">The source text.</param>
  /// <param name="comments">The comments of the file.</param>
  public static Typedefs Read(Node anchor, string source, IEnumerable<Comment> comments) {
    var declared = new Dictionary<string, JsDocTypedef>();
    foreach (var comment in comments.Where(comment => comment.Kind == CommentKind.Block))
      if (JsDoc.Typedef(source[comment.Start..comment.End]) is { } typedef)
        declared[typedef.Name] = typedef;

    return new(anchor, declared);
  }

  /// <summary>
  /// The class of the typedef of a name, made the first time it is asked for; <see langword="null"/> for
  /// a name no typedef declares, a typedef of another form than <c>{Object}</c>, and one with a property
  /// of no supported type - its own name among them.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="named">The class of the file or the typedef a name of a property's type names.</param>
  public ClassModel? Resolve(string name, System.Func<string, ClassModel?> named) {
    if (this._made.TryGetValue(name, out var made))
      return made;

    if (!declared.TryGetValue(name, out var typedef) || !typedef.IsObject)
      return null;

    // A typedef that names itself is being made: no class yet, so the property has no type.
    this._made[name] = null;
    var owner = new ClassModel(name, anchor) { IsTypedef = true };
    foreach (var property in typedef.Properties) {
      if (JsDoc.Stated(property.Spelling, named) is not { Ir: not null } stated || owner.OwnField(property.Key) is not null)
        return null;

      var type = property.IsOptional ? stated.OrUndefined() : stated;
      owner.Fields.Add(new ClassField(property.Key, new Variable(ClassModel.IrName(property.Key), anchor, false) { Type = type, Reason = $"@property {{{property.Spelling}}}" }, null));
    }

    this._made[name] = owner;
    this.Used.Add(owner);
    return owner;
  }

  /// <summary>The class of a typedef that has an IR class, or <see langword="null"/>.</summary>
  public ClassModel? Of(Polypiler.Core.IR.IrClassType? type) => type is null ? null : this.Used.Find(owner => owner.Ir == type);

}
