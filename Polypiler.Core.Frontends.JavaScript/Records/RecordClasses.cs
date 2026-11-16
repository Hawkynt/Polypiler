using System;
using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The classes synthesized where object literals meet objects of a class (#1388), as the Cipher
/// framework keeps its test vectors: a field of <c>TestCase</c> objects that the algorithms fill with
/// literals, which <c>_processTestVector</c> turns into <c>TestCase</c> objects, copying the keys the
/// class lacks onto them.
/// <para>
/// A record shape that meets objects of a class at one place is an object of a class synthesized for
/// its keys, derived from the nearest class of the file every class of the place derives from. Its own
/// fields are the keys that class lacks, in the record's order; the keys it has are its inherited
/// fields, their types joined; a field the record does not have holds <c>undefined</c>. Making it runs
/// no constructor of the lineage, and <c>instanceof</c> of a class it derives from is false for it.
/// </para>
/// <para>
/// <c>r[key] = v</c> in a loop over the keys of <c>o</c>, of a key the class of <c>r</c> lacks, where
/// <c>r</c> is a <c>const</c> that holds the object one <c>new C(…)</c> made, makes that <c>new</c> an
/// object of a class synthesized from <c>C</c> with those keys as fields: a genuine <c>C</c>. The keys
/// copied are those of the class <c>o</c> is of, so there is one such class per list of keys, and the
/// <c>new</c> makes the one of the class of <c>o</c>.
/// </para>
/// </summary>
internal sealed class RecordClasses {

  private readonly Dictionary<string, ClassModel> _records = [];
  private readonly Dictionary<NewExpression, Extension> _extensions = [];
  private readonly List<ClassModel> _made = [];
  private bool _changed;

  /// <summary>Whether a name is taken by a class of the file.</summary>
  public Func<string, bool> IsTaken { get; set; } = _ => false;

  /// <summary>Every class synthesized, in the order it was made.</summary>
  public IReadOnlyList<ClassModel> Made => this._made;

  /// <summary>Whether a class was made or given another base since the last call: the types computed from the old ones are stale.</summary>
  public bool TakeChanged() {
    var changed = this._changed;
    this._changed = false;
    return changed;
  }

  /// <summary>The class of the object literals of a shape's keys, or <see langword="null"/> while none of them has met a class.</summary>
  public ClassModel? RecordOf(IReadOnlyList<string> keys) => this._records.GetValueOrDefault(Signature(keys));

  /// <summary>The classes synthesized for the object a <c>new</c> makes, or <see langword="null"/> for a <c>new</c> nothing extends.</summary>
  public Extension? ExtensionsOf(NewExpression creation) => this._extensions.GetValueOrDefault(creation);

  /// <summary>Every <c>new</c> keys are copied into, with its classes.</summary>
  public IEnumerable<Extension> Extensions => this._extensions.Values;

  /// <summary>
  /// The class of the literals of a shape that meet objects of a class: made the first time, and moved
  /// up to the nearest class both derive from when they meet objects of another; <see langword="null"/>
  /// where there is none, or the class is a frozen object's or one of the library's.
  /// </summary>
  /// <param name="shape">The shape.</param>
  /// <param name="met">The class of the objects.</param>
  /// <returns>The class.</returns>
  public ClassModel? Meet(RecordShape shape, ClassModel met) {
    if (met.Lineage.Any(type => type.IsLibrary || type.IsValue))
      return null;

    var root = met.Lineage.First(type => !type.IsSynthesized);
    var signature = Signature(shape.Keys);
    if (this._records.TryGetValue(signature, out var known)) {
      if (root.Lineage.Contains(known.Base!))
        return known;

      if (known.Base!.Lineage.FirstOrDefault(root.Lineage.Contains) is not { } common)
        return null;

      known.Base = common;
      this._changed = true;
      return known;
    }

    var made = new ClassModel(this.Unique(root.Name + "Literal"), root.Declaration) { RecordKeys = shape.Keys, Base = root, BaseName = root.Name };
    this._records[signature] = made;
    this._made.Add(made);
    this._changed = true;
    return made;
  }

  /// <summary>
  /// The class synthesized for the object a <c>new</c> of a class makes when the keys of an object of
  /// another class are copied into it: one per list of keys the class lacks, made the first time.
  /// </summary>
  /// <param name="creation">The <c>new</c>.</param>
  /// <param name="created">The class it names.</param>
  /// <param name="source">The object whose keys are copied: the variable a loop over its keys names.</param>
  /// <param name="from">The class of that object.</param>
  /// <param name="keys">The keys of that class that <paramref name="created"/> lacks, in their order.</param>
  /// <returns>The class.</returns>
  public ClassModel Extend(NewExpression creation, ClassModel created, Node source, ClassModel from, IReadOnlyList<string> keys) {
    if (!this._extensions.TryGetValue(creation, out var extension))
      this._extensions[creation] = extension = new Extension(creation, created, source);

    var signature = Signature(keys);
    if (!extension.Classes.TryGetValue(signature, out var made)) {
      made = new ClassModel(this.Unique(created.Name + "Extended"), creation) { IsExtension = true, Base = created, BaseName = created.Name };
      extension.Classes[signature] = made;
      this._made.Add(made);
      this._changed = true;
    }

    extension.Made[from] = made;
    return made;
  }

  /// <summary>
  /// The classes an object of a class may be of whose keys differ: every synthesized class below it,
  /// the classes further down first, and the class itself last.
  /// </summary>
  public IReadOnlyList<ClassModel> Below(ClassModel type)
    => [.. this._made.Where(made => made != type && made.Lineage.Contains(type)).OrderByDescending(made => made.Lineage.Count()), type];

  /// <summary>The record classes below a class, whose objects <c>instanceof</c> of it does not take.</summary>
  public IEnumerable<ClassModel> RecordsBelow(ClassModel type) => this._made.Where(made => made.RecordKeys is not null && made.Lineage.Contains(type));

  private static string Signature(IReadOnlyList<string> keys) => string.Join(",", keys);

  /// <summary>The classes synthesized for the object one <c>new</c> makes (#1388).</summary>
  /// <param name="Creation">The <c>new</c>.</param>
  /// <param name="Created">The class it names.</param>
  /// <param name="Source">The variable of the object whose keys are copied into it.</param>
  internal sealed record Extension(NewExpression Creation, ClassModel Created, Node Source) {

    /// <summary>The class made for each list of keys copied.</summary>
    public Dictionary<string, ClassModel> Classes { get; } = [];

    /// <summary>The class the <c>new</c> makes for an object of each class its keys are copied from.</summary>
    public Dictionary<ClassModel, ClassModel> Made { get; } = [];

  }

  /// <summary>A name no class of the file nor another synthesized class has: the name itself, then with 2, 3 and so on appended.</summary>
  private string Unique(string name) {
    var chosen = name;
    for (var number = 2; this.IsTaken(chosen) || this._made.Exists(made => made.Name == chosen); ++number)
      chosen = name + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    return chosen;
  }

}
