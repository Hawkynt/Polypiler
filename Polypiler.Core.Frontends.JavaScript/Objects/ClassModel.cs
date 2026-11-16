using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>What a function of a class is to it.</summary>
internal enum MemberKind {

  /// <summary>No member: a function of the module.</summary>
  None,

  /// <summary>The constructor: of a class, or a constructor function of ECMAScript 5.</summary>
  Constructor,

  /// <summary>A method, on the prototype or, when static, on the class.</summary>
  Method,

  /// <summary>A getter.</summary>
  Getter,

  /// <summary>A setter.</summary>
  Setter

}

/// <summary>An instance field of a class: its key as the source spells it, with <c>#</c> when it is private.</summary>
/// <param name="Key">The key.</param>
/// <param name="Variable">What the inference knows of its values: every value stored into it.</param>
/// <param name="Initializer">The value of a field declaration, which the constructor stores first; <see langword="null"/> for none.</param>
internal sealed record ClassField(string Key, Variable Variable, Node? Initializer) {

  /// <summary>The type the JSDoc <c>@type</c> in front of the statement that first assigns the field states, as it is spelled; <see langword="null"/> for none.</summary>
  public string? Stated { get; init; }

  /// <summary>Whether the field is private, <c>#name</c>.</summary>
  public bool IsPrivate => this.Key.StartsWith('#');

  /// <summary>Whether a field declaration of the class body declares the field, which defines it on the object rather than storing through the prototype.</summary>
  public bool IsDeclared { get; init; }

  /// <summary>
  /// The getters and setters derived classes declare over the field (#1402). Where there are any, the
  /// field is a virtual property of its class, whose accessors read and write <see cref="Ir"/>, a private
  /// field of its own; every read and store of the key runs the accessor of the object's class.
  /// </summary>
  public List<Candidate> Accessors { get; } = [];

  /// <summary>Whether derived classes declare accessors over the field, so that it is a virtual property.</summary>
  public bool IsVirtual => this.Accessors.Count > 0;

  /// <summary>The getter of the virtual property of the field in the IR, once the types are known; <see langword="null"/> for a field that is no property.</summary>
  public IrMethod? Getter { get; set; }

  /// <summary>The setter of the virtual property of the field in the IR, once the types are known; <see langword="null"/> for a field that is no property.</summary>
  public IrMethod? Setter { get; set; }

  /// <summary>The field in the IR, once the types are known.</summary>
  public IrField? Ir { get; set; }

}

/// <summary>
/// A class of the file: an ECMAScript 2015 class, or a constructor function of ECMAScript 5 with the
/// methods and accessors the top level of the file puts on its prototype. Its instance fields are
/// those its field declarations and its constructor assign through <c>this</c>; its static fields are
/// variables of the module that the class owns.
/// </summary>
/// <param name="name">The name.</param>
/// <param name="declaration">The class declaration or the constructor function.</param>
/// <param name="library">The library class of the IR the class is - <c>Error</c> is <c>Exception</c> - or <see langword="null"/> for a class of the file.</param>
internal sealed class ClassModel(string name, Node declaration, IrClassType? library = null) {

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>The class declaration or the constructor function, which a diagnostic about the class points at.</summary>
  public Node Declaration { get; } = declaration;

  /// <summary>Whether the file makes the class visible to others.</summary>
  public bool IsPublic { get; set; }

  /// <summary>The class in the IR, declared before any type is known so that a type can name it.</summary>
  public IrClassType Ir { get; } = library ?? new(name);

  /// <summary>Whether the class is one of the library's (<see cref="ErrorLibrary"/>), which no file declares.</summary>
  public bool IsLibrary => this.Ir.IsLibrary;

  /// <summary>The instance fields, in the order the source first declares or assigns them.</summary>
  public List<ClassField> Fields { get; } = [];

  /// <summary>The static fields, by key: variables of the module named <c>Class.key</c>.</summary>
  public Dictionary<string, Variable> Statics { get; } = [];

  /// <summary>The constructor, or <see langword="null"/> for a class without one.</summary>
  public Candidate? Constructor { get; set; }

  /// <summary>The methods and accessors, in source order.</summary>
  public List<Candidate> Members { get; } = [];

  /// <summary>The method in the IR of every member but the constructor, once the types are known.</summary>
  public Dictionary<Candidate, IrMethod> Methods { get; } = [];

  /// <summary>
  /// The constructor a <c>new</c> of the class runs the parameters of: its own, or for a class without
  /// one the nearest of a class it derives from, which JavaScript's implicit
  /// <c>constructor(...args) { super(...args); }</c> passes the arguments on to (#1369); <see langword="null"/> where none declares one.
  /// </summary>
  public Candidate? Initial => this.Lineage.Select(type => type.Constructor).FirstOrDefault(constructor => constructor is not null);

  /// <summary>The constructor in the IR, once the types are known.</summary>
  public IrConstructor? Creator { get; set; }

  /// <summary>
  /// For the class of the members of a frozen object of records (#1371), the values of each member, in
  /// the order of the fields; <see langword="null"/> for a class of the file. Its fields are read-only,
  /// and its constructor takes one parameter per field.
  /// </summary>
  public IReadOnlyDictionary<string, IReadOnlyList<Expression>>? Frozen { get; init; }

  /// <summary>
  /// Whether the class is that of a <c>@typedef {Object}</c> (#1409): its fields are read-only, its
  /// constructor takes one parameter per field, and an object literal where it is stated is a new object of it.
  /// </summary>
  public bool IsTypedef { get; init; }

  /// <summary>Whether the fields are read-only and the constructor takes one value per field: the class of a frozen object's members or of a typedef.</summary>
  public bool IsValue => this.Frozen is not null || this.IsTypedef;

  /// <summary>Why the class cannot be imported whatever its members are, or <see langword="null"/>.</summary>
  public string? Problem { get; set; }

  /// <summary>The name of the class this one extends - <c>extends A</c>, or <c>Object.create(A.prototype)</c> - or <see langword="null"/>.</summary>
  public string? BaseName { get; set; }

  /// <summary>The class of the file this one derives from, once the file is read; <see langword="null"/> for none.</summary>
  public ClassModel? Base { get; set; }

  /// <summary>The class and the classes it derives from, the class first; a cycle ends at its first repetition.</summary>
  public IEnumerable<ClassModel> Lineage {
    get {
      var seen = new HashSet<ClassModel>();
      for (var type = this; type is not null && seen.Add(type); type = type.Base)
        yield return type;
    }
  }

  /// <summary>The instance field of a key, of the class or of a class it derives from, or <see langword="null"/>.</summary>
  public ClassField? Field(string key) => this.Lineage.Select(type => type.OwnField(key)).FirstOrDefault(field => field is not null);

  /// <summary>The instance field of a key the class declares itself, or <see langword="null"/>.</summary>
  public ClassField? OwnField(string key) => this.Fields.FirstOrDefault(field => field.Key == key);

  /// <summary>The member of a kind and a key, an instance member also of a class it derives from, or <see langword="null"/>.</summary>
  public Candidate? Member(MemberKind kind, string key, bool isStatic = false)
    => (isStatic ? [this] : this.Lineage).Select(type => type.OwnMember(kind, key, isStatic)).FirstOrDefault(member => member is not null);

  /// <summary>The member of a kind and a key the class declares itself, or <see langword="null"/>.</summary>
  public Candidate? OwnMember(MemberKind kind, string key, bool isStatic = false)
    => this.Members.FirstOrDefault(member => member.Member == kind && member.Key == key && member.IsStatic == isStatic);

  /// <summary>Whether a key names an instance member of any kind, of the class or of a class it derives from.</summary>
  public bool HasInstanceMember(string key) => this.Field(key) is not null || this.Lineage.Any(type => type.Members.Any(member => member.Key == key && !member.IsStatic));

  /// <summary>The method in the IR of a member of the class or of a class it derives from.</summary>
  public static IrMethod MethodOf(Candidate member) => member.Class!.Methods[member];

  /// <summary>The name of a key in the IR: a private <c>#name</c> is <c>_name</c>, the convention of C#.</summary>
  public static string IrName(string key) => key.StartsWith('#') ? "_" + key[1..] : key;

}
