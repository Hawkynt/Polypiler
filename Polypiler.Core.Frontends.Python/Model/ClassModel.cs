using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>What kind of class a <c>class</c> statement declares.</summary>
internal enum ClassKind {

  /// <summary>A class without bases whose <c>__init__</c> assigns the attributes of its objects.</summary>
  Plain,

  /// <summary>A <c>@dataclass</c>: its fields are its annotated names, its <c>__init__</c> and <c>__eq__</c> are made.</summary>
  Dataclass,

  /// <summary>A <c>typing.NamedTuple</c>: an immutable record of annotated fields.</summary>
  NamedTuple,

  /// <summary>An <c>enum.Enum</c> of members with integer values; a member equals only itself.</summary>
  Enum,

  /// <summary>An <c>enum.IntEnum</c>: a member is also the integer it stands for.</summary>
  IntEnum,

  /// <summary>An exception class of the module: a body of <c>pass</c> or a docstring, derived from a built-in exception.</summary>
  Exception,

  /// <summary>A built-in exception of Python: an exception class of the IR's library, or one the import declares beside them.</summary>
  BuiltinException

}

/// <summary>An attribute of the objects of a class: a dataclass field, or a name <c>__init__</c> assigns to <c>self</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Node">Where it is declared: the annotation of a dataclass field, or the first assignment in <c>__init__</c>.</param>
internal sealed record FieldModel(string Name, PyNode Node) {

  /// <summary>The type: of the annotation of a dataclass field, of the values assigned to it otherwise.</summary>
  public PyType Type { get; set; } = PyType.Unknown;

  /// <summary>The default value of a dataclass field, or <see langword="null"/>.</summary>
  public PyNode? Default { get; init; }

  /// <summary>The field of the IR class, once the types are known.</summary>
  public IrField? Ir { get; set; }

}

/// <summary>A class of the module, or a built-in exception, with what the import knows of it.</summary>
internal sealed class ClassModel {

  /// <summary>Describes a class.</summary>
  /// <param name="name">The name.</param>
  /// <param name="kind">What kind of class it is.</param>
  /// <param name="node">The <c>ClassDef</c>; <see langword="null"/> for a built-in exception.</param>
  public ClassModel(string name, ClassKind kind, PyNode? node) {
    this.Name = name;
    this.Kind = kind;
    this.Node = node;
  }

  /// <summary>The name.</summary>
  public string Name { get; }

  /// <summary>What kind of class it is.</summary>
  public ClassKind Kind { get; }

  /// <summary>The <c>ClassDef</c>; <see langword="null"/> for a built-in exception.</summary>
  public PyNode? Node { get; }

  /// <summary>Whether the attributes of an object cannot be assigned after <c>__init__</c>: a frozen dataclass or a named tuple.</summary>
  public bool IsFrozen { get; init; }

  /// <summary>Whether <c>==</c> compares the fields: a dataclass with <c>eq=True</c> or a named tuple.</summary>
  public bool ComparesFields { get; init; }

  /// <summary>The attributes of the objects, in declaration order.</summary>
  public List<FieldModel> Fields { get; } = [];

  /// <summary>The parameters a construction binds its arguments to: of <c>__init__</c> without its receiver, or the fields of a dataclass with their defaults.</summary>
  public List<Variable> InitParameters { get; } = [];

  /// <summary>The methods, by name: <c>__init__</c> and the special methods among them.</summary>
  public Dictionary<string, FunctionModel> Methods { get; } = [];

  /// <summary>The members of an enum, with their values, in definition order.</summary>
  public List<(string Name, long Value, PyNode Node)> Members { get; } = [];

  /// <summary>The exception class of the module or the built-in exception a class of exceptions derives from.</summary>
  public ClassModel? Base { get; init; }

  /// <summary>The IR class of objects and exceptions; <see langword="null"/> for an enum.</summary>
  public IrClassType? Ir { get; set; }

  /// <summary>The IR enum of an enum; otherwise <see langword="null"/>.</summary>
  public IrEnumType? EnumType { get; set; }

  /// <summary>The constructor of the IR class, which <c>__init__</c> is.</summary>
  public IrConstructor? Constructor { get; set; }

  /// <summary>The methods of the IR class, by their Python names.</summary>
  public Dictionary<string, IrMethod> IrMethods { get; } = [];

  /// <summary>The type of a reference to an object of the class.</summary>
  public IrType Reference => IrType.Object(this.Ir!);

  /// <summary>Whether the class is an enum.</summary>
  public bool IsEnum => this.Kind is ClassKind.Enum or ClassKind.IntEnum;

  /// <summary>Whether objects of the class are exceptions.</summary>
  public bool IsException => this.Kind is ClassKind.Exception or ClassKind.BuiltinException;

  /// <summary>Whether objects of the class are records: a plain class, a dataclass or a named tuple.</summary>
  public bool IsRecord => this.Kind is ClassKind.Plain or ClassKind.Dataclass or ClassKind.NamedTuple;

  /// <summary>An attribute by its name, or <see langword="null"/>.</summary>
  public FieldModel? Field(string name) => this.Fields.FirstOrDefault(field => field.Name == name);

  /// <summary>A method by its name, or <see langword="null"/>.</summary>
  public FunctionModel? Method(string name) => this.Methods.GetValueOrDefault(name);

  /// <summary>Whether the class is the class or derives from it.</summary>
  public bool DerivesFrom(ClassModel other) {
    for (var current = this; current is not null; current = current.Base)
      if (ReferenceEquals(current, other))
        return true;

    return false;
  }

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>
/// The structs of the tuples of an import: one per sequence of item types, made the first time a
/// tuple of those types is lowered and declared by the module. An import runs on one thread; the
/// registry of the import running is <see cref="Current"/>.
/// </summary>
internal sealed class Tuples {

  [System.ThreadStatic]
  private static Tuples? _current;

  private readonly List<(PyType Type, IrStructType Struct)> _made = [];

  /// <summary>The registry of the import running on this thread.</summary>
  public static Tuples Current {
    get => _current ??= new();
    set => _current = value;
  }

  /// <summary>The structs made so far, in the order they were made.</summary>
  public IReadOnlyList<IrStructType> Structs => [.. this._made.Select(made => made.Struct)];

  /// <summary>The struct of a tuple type of the import running.</summary>
  public static IrStructType Of(PyType tuple) => Current.Struct(tuple);

  private IrStructType Struct(PyType tuple) {
    foreach (var (type, made) in this._made)
      if (type == tuple)
        return made;

    var name = "Tuple" + (this._made.Count + 1);
    var fields = tuple.Items.Select((item, index) => new IrField("Item" + index, PyTypes.Ir(item), true)).ToList();
    var declared = new IrStructType(name, fields) { IsEquatable = true };
    this._made.Add((tuple, declared));
    return declared;
  }

}
