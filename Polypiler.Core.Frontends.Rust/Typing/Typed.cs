using System.Collections.Generic;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>A name a scope binds: a parameter or local, or a constant.</summary>
internal sealed class Binding(string name, RType type, bool mutable, ConstItem? constant = null) {

  public string Name { get; } = name;

  public RType Type { get; } = type;

  public bool Mutable { get; } = mutable;

  /// <summary>The constant the name stands for, or <see langword="null"/> for a variable.</summary>
  public ConstItem? Constant { get; } = constant;

  /// <summary>Whether a variable has been given a value: a <c>let</c> without one may be assigned once.</summary>
  public bool Initialized { get; set; }

  /// <summary>How many closures enclose the declaration: a name a closure uses from a smaller depth is captured.</summary>
  public int Depth { get; init; }

  /// <summary>The name of the IR global of an immutable <c>static</c> of an array type, or <see langword="null"/>.</summary>
  public string? Global { get; set; }

}

/// <summary>The signature of a function of the crate: a free function, a nested one, or a method.</summary>
internal sealed record FunctionSignature(FunctionItem Item, IReadOnlyList<RType> Parameters, RType Result) {

  /// <summary>The name of the IR function: the crate path, its separators replaced, made unique in the module.</summary>
  public string IrName { get; set; } = Item.Name;

  /// <summary>The type of the receiver of a method, which the parameters do not list; <see langword="null"/> for any other function.</summary>
  public RType? Receiver { get; init; }

  /// <summary>The module the function is declared in.</summary>
  public ModuleScope? Module { get; init; }

  /// <summary>The crate path, <c>crate::hash::fnv1a</c>.</summary>
  public string CratePath { get; init; } = Item.Name;

  /// <summary>Whether the crate exports the function: it is reachable as <c>pub</c> from the crate root.</summary>
  public bool IsExported { get; set; }

  /// <summary>The type parameters of a generic function's template; none for any other function.</summary>
  public IReadOnlyList<GenericParameter> Generics { get; set; } = [];

  /// <summary>The instances of a template, by the list of its type arguments.</summary>
  public Dictionary<string, FunctionSignature> Instances { get; } = new(System.StringComparer.Ordinal);

  /// <summary>The types an instance's type parameters stand for; <see langword="null"/> for any other function.</summary>
  public IReadOnlyDictionary<string, RType>? Substitution { get; set; }

}

/// <summary>
/// A module of the crate: its path, its parent and the names its items and <c>use</c> declarations
/// bring into its scope.
/// </summary>
internal sealed class ModuleScope(string name, ModuleScope? parent, string file) {

  public string Name { get; } = name;

  public ModuleScope? Parent { get; } = parent;

  /// <summary>The file the module's items are in.</summary>
  public string File { get; } = file;

  /// <summary>The path from the crate root, empty for the root.</summary>
  public IReadOnlyList<string> Path { get; } = parent is null ? [] : [.. parent.Path, name];

  /// <summary>The crate path as Rust writes it: <c>crate</c> or <c>crate::a::b</c>.</summary>
  public string CratePath => this.Path.Count == 0 ? "crate" : "crate::" + string.Join("::", this.Path);

  /// <summary>The items the module declares, by name.</summary>
  public Dictionary<string, Symbol> Items { get; } = [];

  /// <summary>The <c>use</c> declarations, resolved when the items of every module are known.</summary>
  public List<(UsePath Path, bool IsPublic, bool IsVisible)> Uses { get; } = [];

  /// <summary>The names <c>use</c> brought in, by name.</summary>
  public Dictionary<string, Symbol> Imported { get; } = [];

  /// <summary>The modules whose public names a glob <c>use</c> brought in.</summary>
  public List<ModuleScope> Globs { get; } = [];

  /// <summary>The child modules in source order.</summary>
  public List<ModuleScope> Children { get; } = [];

  /// <summary>Whether the module is compiled for tests only.</summary>
  public bool TestOnly { get; init; }

  /// <summary>Whether the module itself is reachable as <c>pub</c> from the crate root.</summary>
  public bool IsExported { get; set; }

  /// <summary>Whether this module is the given one or one of its descendants.</summary>
  public bool IsWithin(ModuleScope ancestor) {
    for (var scope = this; scope is not null; scope = scope.Parent)
      if (ReferenceEquals(scope, ancestor))
        return true;

    return false;
  }

}

/// <summary>A name of a module: what it stands for and who may see it.</summary>
/// <param name="Target">A <see cref="FunctionSignature"/>, a constant <see cref="Binding"/>, a <see cref="StructInfo"/>, an <see cref="EnumInfo"/> or a <see cref="ModuleScope"/>.</param>
/// <param name="IsPublic">Whether it is <c>pub</c>.</param>
/// <param name="IsVisible">Whether other modules of the crate see it: <c>pub</c> or <c>pub(crate)</c>.</param>
/// <param name="Owner">The module that declares it.</param>
internal sealed record Symbol(object Target, bool IsPublic, bool IsVisible, ModuleScope Owner);

/// <summary>
/// A <c>for</c> over an integer range: the bounds, whether the end is included, and the adapters in
/// source order, which do not commute (<c>(0..11).step_by(3).rev()</c> yields 9, 6, 3, 0 and
/// <c>(0..11).rev().step_by(3)</c> 10, 7, 4, 1).
/// </summary>
internal sealed record RangeLoop(Expr Start, Expr End, bool Inclusive, bool Reversed, Expr? Step, bool StepBeforeReverse);

/// <summary>What the checker found out about the functions of a file, for the lowering to read.</summary>
internal sealed class Typed {

  /// <summary>The type of every expression checked.</summary>
  public Dictionary<Expr, RType> Types { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The binding every path, identifier pattern and parameter names.</summary>
  public Dictionary<object, Binding> Bindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The loop or labelled block every <c>break</c> and <c>continue</c> leaves.</summary>
  public Dictionary<Expr, Expr> Targets { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The range every <c>for</c> over a plain range iterates over.</summary>
  public Dictionary<ForExpr, RangeLoop> Ranges { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The iteration every other <c>for</c> and every consuming iterator method runs.</summary>
  public Dictionary<Expr, Iteration> Iterations { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The function every call of a function of the crate calls, and every path to one names.</summary>
  public Dictionary<Expr, FunctionSignature> Callees { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>What a path that is no variable names: a unit struct, a variant, <c>None</c>.</summary>
  public Dictionary<object, object> Resolutions { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The closure every closure expression is, and the closure every call of a closure variable calls.</summary>
  public Dictionary<object, ClosureInfo> Closures { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The library method every method call of the subset's standard library calls, by its name in <see cref="Library"/>.</summary>
  public Dictionary<MethodCallExpr, string> Methods { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The type of the value every pattern matches, references looked through by the default binding mode.</summary>
  public Dictionary<Pattern, RType> PatternTypes { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The signatures of the functions of the crate, in declaration order.</summary>
  public List<FunctionSignature> Functions { get; } = [];

  /// <summary>The structs of the crate, in declaration order.</summary>
  public List<StructInfo> Structs { get; } = [];

  /// <summary>The enums of the crate, in declaration order.</summary>
  public List<EnumInfo> Enums { get; } = [];

  /// <summary>The modules of the crate, the root first.</summary>
  public List<ModuleScope> Modules { get; } = [];

  /// <summary>The immutable statics of the crate.</summary>
  public List<Binding> Statics { get; } = [];

  /// <summary>Whether <c>debug_assert*!</c> asserts: <c>rustc</c>'s <c>-C debug-assertions</c>.</summary>
  public bool DebugAssertions { get; set; }

  /// <summary>The method of a trait impl each operator, unary or compound assignment on a struct or an enum calls.</summary>
  public Dictionary<Expr, FunctionSignature> Operators { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The pieces of each <c>format!</c>.</summary>
  public Dictionary<MacroExpr, IReadOnlyList<FormatPiece>> Formats { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The type of an expression, resolved.</summary>
  public RType TypeOf(Expr expression) => this.Types[expression].Resolved;

}

/// <summary>What an iteration starts from.</summary>
internal enum SourceKind {

  /// <summary>An integer range, with its <c>rev()</c> and <c>step_by()</c> in <see cref="Iteration.Range"/>.</summary>
  Range,

  /// <summary>The elements of an array, a slice or a <c>Vec</c>, each read where it is.</summary>
  Elements,

  /// <summary><c>chunks_exact(n)</c> and <c>chunks_exact_mut(n)</c>: views of <c>n</c> elements.</summary>
  Chunks,

  /// <summary><c>chars()</c>, <c>bytes()</c> and <c>split(p)</c> of text: the elements of the array they make.</summary>
  Text

}

/// <summary>How an element of an iteration reaches the loop: as a value, as a shared reference, as an exclusive one.</summary>
internal enum ElementAccess {

  /// <summary>The value itself, copied.</summary>
  Value,

  /// <summary><c>&amp;T</c> to the element where it is.</summary>
  Shared,

  /// <summary><c>&amp;mut T</c> to the element where it is.</summary>
  Exclusive

}

/// <summary>A source of an iteration over elements: the expression of the container, its element type and how its elements are reached.</summary>
internal sealed record ElementSource(Expr Container, RType Element, ElementAccess Access) {

  /// <summary>The size of a chunk of <see cref="SourceKind.Chunks"/>.</summary>
  public Expr? ChunkSize { get; init; }

  /// <summary>The method that makes a <see cref="SourceKind.Text"/> source: <c>chars</c>, <c>bytes</c>, <c>split</c>.</summary>
  public string? TextMethod { get; init; }

  /// <summary>The pattern of <c>split</c>.</summary>
  public Expr? Separator { get; init; }

}

/// <summary>One adapter of an iteration after its source, in source order.</summary>
/// <param name="Name">The method: <c>rev</c>, <c>step_by</c>, <c>zip</c>, <c>enumerate</c>, <c>copied</c>, <c>cloned</c>, <c>map</c>, <c>filter</c>.</param>
/// <param name="Call">The call of the adapter.</param>
/// <param name="Zipped">The other side of a <c>zip</c>.</param>
/// <param name="Function">The closure, closure variable or function <c>map</c> and <c>filter</c> call.</param>
internal sealed record Adapter(string Name, MethodCallExpr Call, ElementSource? Zipped, Expr? Function);

/// <summary>
/// An iteration: a source, its adapters, and - for a consuming method - the method; the item type
/// after the adapters is the type of the loop variable or of what the method consumes.
/// </summary>
internal sealed record Iteration(SourceKind Kind, RangeLoop? Range, ElementSource? Source, IReadOnlyList<Adapter> Adapters, RType Item) {

  /// <summary>The consuming method: <c>sum</c>, <c>product</c>, <c>count</c>, <c>fold</c>, <c>any</c>, <c>all</c>, <c>for_each</c>; <see langword="null"/> for a <c>for</c>.</summary>
  public string? Terminal { get; init; }

  /// <summary>The closure, closure variable or function the consuming method takes.</summary>
  public Expr? TerminalFunction { get; init; }

  /// <summary>The call of the consuming method.</summary>
  public MethodCallExpr? TerminalCall { get; init; }

  /// <summary>The element type of a range source.</summary>
  public RType? RangeElement { get; init; }

}

/// <summary>
/// A piece of a format string: literal text, or an argument with its format spec (The Rust
/// Standard Library, <c>std::fmt</c>, "Syntax").
/// </summary>
internal sealed record FormatPiece(string? Text, Expr? Argument) {

  /// <summary>The fill character, a space by default.</summary>
  public int Fill { get; init; } = ' ';

  /// <summary><c>&lt;</c>, <c>^</c> or <c>&gt;</c>; <c>\0</c> for the default of the type.</summary>
  public char Align { get; init; }

  /// <summary><c>+</c>: a sign for every number.</summary>
  public bool Plus { get; init; }

  /// <summary><c>#</c>: the radix prefix.</summary>
  public bool Alternate { get; init; }

  /// <summary><c>0</c>: zeros after the sign up to the width.</summary>
  public bool Zero { get; init; }

  /// <summary>The minimum width in chars.</summary>
  public int Width { get; init; }

  /// <summary>The trait: <c>\0</c> for Display, <c>?</c>, <c>x</c>, <c>X</c>, <c>o</c>, <c>b</c>.</summary>
  public char Kind { get; init; }

}
