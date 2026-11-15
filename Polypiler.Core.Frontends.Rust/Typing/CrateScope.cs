using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// The module tree of a crate and its names (The Rust Reference, "Modules", "Use declarations",
/// "Paths", "Visibility and privacy"): the root file and every module in it or in a file of its own
/// (<c>mod m;</c> is <c>m.rs</c> or <c>m/mod.rs</c> beside the file that declares it, or in the
/// folder of a non-root file named after it), the items each declares, and the names each
/// <c>use</c> brings in, renamed, by glob, re-exported. A path is resolved from a module through
/// <c>crate</c>, <c>self</c> and <c>super</c>, the module's own items, its imports and the prelude.
/// </summary>
internal sealed class CrateScope {

  private readonly Action<string, string, Span> _report;
  private readonly Func<string, string?>? _read;

  private CrateScope(Action<string, string, Span> report, Func<string, string?>? read) {
    this._report = report;
    this._read = read;
  }

  /// <summary>The root module.</summary>
  public ModuleScope Root { get; private set; } = null!;

  /// <summary>Every module, the root first, then in source order.</summary>
  public List<ModuleScope> Modules { get; } = [];

  /// <summary>The functions in declaration order with the module that declares them; methods follow their <c>impl</c>.</summary>
  public List<(FunctionItem Item, ModuleScope Module, ImplItem? Impl)> Functions { get; } = [];

  /// <summary>The constants and statics with their modules.</summary>
  public List<(ConstItem Item, ModuleScope Module)> Constants { get; } = [];

  /// <summary>The structs and enums with their modules.</summary>
  public List<(Item Item, ModuleScope Module)> Types { get; } = [];

  /// <summary>The impl blocks with their modules.</summary>
  public List<(ImplItem Item, ModuleScope Module)> Impls { get; } = [];

  /// <summary>Builds the module tree of a crate whose root file has been parsed.</summary>
  /// <param name="file">The root file.</param>
  /// <param name="path">Its path, from which file modules are found.</param>
  /// <param name="report">Where refusals and errors go.</param>
  /// <param name="read">Reads another file of the crate, <see langword="null"/> when there is none; without it, a file module is refused.</param>
  public static CrateScope Build(SourceFile file, string path, Action<string, string, Span> report, Func<string, string?>? read) {
    var scope = new CrateScope(report, read);
    scope.Root = new ModuleScope("crate", null, path);
    scope.Modules.Add(scope.Root);
    scope.Declare(scope.Root, file.Items, true);
    foreach (var module in scope.Modules)
      scope.ResolveUses(module);

    scope.MarkExported(scope.Root);
    return scope;
  }

  // ---------------------------------------------------------------- declarations

  private void Declare(ModuleScope module, IReadOnlyList<Item> items, bool isRootFile) {
    foreach (var item in items) {
      switch (item) {
        case RefusedItem refused:
          this._report(refused.Refusal.Code, refused.Refusal.Construct, refused.Refusal.At);
          break;
        case FunctionItem function:
          this.Add(module, function.Name, function, function);
          this.Functions.Add((function, module, null));
          break;
        case ConstItem constant:
          this.Add(module, constant.Name, constant, constant);
          this.Constants.Add((constant, module));
          break;
        case StructItem structure:
          this.Add(module, structure.Name, structure, structure);
          this.Types.Add((structure, module));
          break;
        case EnumItem enumeration:
          this.Add(module, enumeration.Name, enumeration, enumeration);
          this.Types.Add((enumeration, module));
          break;
        case ImplItem impl:
          this.Impls.Add((impl, module));
          foreach (var member in impl.Items) {
            if (member is RefusedItem refusedMember)
              this._report(refusedMember.Refusal.Code, refusedMember.Refusal.Construct, refusedMember.Refusal.At);
            else if (member is FunctionItem method)
              this.Functions.Add((method, module, impl));
          }

          break;
        case UseItem use:
          foreach (var used in use.Paths)
            module.Uses.Add((used, use.IsPublic, use.IsVisible));

          break;
        case ModuleItem child:
          this.Child(module, child, isRootFile);
          break;
      }
    }
  }

  private void Add(ModuleScope module, string name, Item item, object target) {
    if (name == "_")
      return;

    if (module.Items.ContainsKey(name)) {
      this._report(RustFrontend.InvalidSource, $"E0428: the name `{name}` is defined multiple times", item.At);
      return;
    }

    module.Items[name] = new Symbol(target, item.IsPublic, item.IsVisible, module);
  }

  private void Child(ModuleScope parent, ModuleItem item, bool parentIsRootFile) {
    var items = item.Items;
    var file = parent.File;
    if (items is null) {
      var found = this.ModuleFile(parent, item.Name, parentIsRootFile);
      if (found is null) {
        this._report(RustFrontend.InvalidSource, $"E0583: file not found for module `{item.Name}`", item.At);
        return;
      }

      try {
        items = Parser.Parse(found.Value.Source).Items;
      } catch (RustSyntaxException error) {
        this._report(RustFrontend.InvalidSource, $"{error.Message} (in {Path.GetFileName(found.Value.Path)} at {error.At.Line}:{error.At.Column})", item.At);
        return;
      }

      file = found.Value.Path;
      parentIsRootFile = Path.GetFileName(file) == "mod.rs";
    } else {
      parentIsRootFile = false;
    }

    var module = new ModuleScope(item.Name, parent, file) { TestOnly = item.TestOnly || parent.TestOnly };
    this.Add(parent, item.Name, item, module);
    parent.Children.Add(module);
    this.Modules.Add(module);
    this.Declare(module, items, parentIsRootFile && items != item.Items);
  }

  /// <summary>The file of <c>mod name;</c>: <c>name.rs</c> or <c>name/mod.rs</c> in the folder the declaring file's modules live in.</summary>
  private (string Path, string Source)? ModuleFile(ModuleScope parent, string name, bool parentOwnsFolder) {
    if (this._read is null)
      return null;

    var directory = Path.GetDirectoryName(parent.File) ?? "";
    var stem = Path.GetFileNameWithoutExtension(parent.File);
    // A root file (lib.rs, main.rs, a single file) and a mod.rs own their folder; any other file `a.rs` owns `a/`.
    var folder = parent.Parent is null || parentOwnsFolder || stem == "mod" ? directory : Path.Combine(directory, stem);
    foreach (var candidate in new[] { Path.Combine(folder, name + ".rs"), Path.Combine(folder, name, "mod.rs") })
      if (this._read(candidate) is { } source)
        return (candidate, source);

    return null;
  }

  // ---------------------------------------------------------------- uses

  private void ResolveUses(ModuleScope module) {
    foreach (var (path, isPublic, isVisible) in module.Uses) {
      var segments = path.Segments;
      if (segments.Count > 0 && segments[0] is "std" or "core") {
        this.UseLibrary(module, path, isPublic, isVisible);
        continue;
      }

      if (path.Glob) {
        if (this.Resolve(module, segments, path.At, false)?.Target is ModuleScope target)
          module.Globs.Add(target);
        else
          this.Unresolved(segments, path.At);

        continue;
      }

      // `use m::{self}` names the module itself.
      if (segments.Count > 1 && segments[^1] == "self")
        segments = segments.Take(segments.Count - 1).ToList();

      var symbol = this.Resolve(module, segments, path.At, false);
      if (symbol is null) {
        this.Unresolved(segments, path.At);
        continue;
      }

      var name = path.Alias ?? segments[^1];
      if (name == "_")
        continue;

      module.Imported[name] = symbol with { IsPublic = isPublic && symbol.IsPublic, IsVisible = isVisible, Owner = module };
    }
  }

  /// <summary>A <c>use</c> of <c>core</c>: <c>cmp::min</c>, <c>cmp::max</c>, <c>mem::swap</c> or their modules; anything else is outside the subset.</summary>
  private void UseLibrary(ModuleScope module, UsePath path, bool isPublic, bool isVisible) {
    var segments = path.Segments;
    var joined = string.Join("::", segments.Skip(1));
    if (path.Glob && segments.Count == 2 && segments[1] is "cmp" or "mem") {
      foreach (var function in Typing.TypeChecker.LibraryFunctions.Where(function => function.StartsWith(segments[1] + "::", StringComparison.Ordinal)))
        module.Imported[function.Split("::")[1]] = new Symbol(function, isPublic, isVisible, module);

      return;
    }

    if (!path.Glob && Typing.TypeChecker.LibraryFunctions.Contains(joined)) {
      module.Imported[path.Alias ?? segments[^1]] = new Symbol(joined, isPublic, isVisible, module);
      return;
    }

    if (!path.Glob && segments.Count == 2 && segments[1] is "cmp" or "mem") {
      module.Imported[path.Alias ?? segments[1]] = new Symbol(new LibraryModule(segments[1]), isPublic, isVisible, module);
      return;
    }

    this._report(RustFrontend.ForeignPath, string.Join("::", segments) + (path.Glob ? "::*" : ""), path.At);
  }

  /// <summary>A path of a <c>use</c> that names nothing: an item of another crate, or of this one that does not exist (<c>E0432</c>).</summary>
  private void Unresolved(IReadOnlyList<string> segments, Span at) {
    if (segments[0] is not ("crate" or "self" or "super") && !this.Root.Items.ContainsKey(segments[0]))
      this._report(RustFrontend.ForeignPath, string.Join("::", segments), at);
    else
      this._report(RustFrontend.InvalidSource, $"E0432: unresolved import `{string.Join("::", segments)}`", at);
  }

  /// <summary>Marks every module and function reachable as <c>pub</c> from the root, directly or through a <c>pub use</c>.</summary>
  private void MarkExported(ModuleScope root) {
    root.IsExported = true;
    var queue = new Queue<ModuleScope>([root]);
    var seen = new HashSet<ModuleScope>(ReferenceEqualityComparer.Instance) { root };
    while (queue.Count > 0) {
      var module = queue.Dequeue();
      foreach (var symbol in module.Items.Values.Concat(module.Imported.Values).Where(symbol => symbol.IsPublic)) {
        if (symbol.Target is ModuleScope child && !child.TestOnly && seen.Add(child)) {
          child.IsExported = true;
          queue.Enqueue(child);
        }
      }
    }
  }

  // ---------------------------------------------------------------- paths

  /// <summary>
  /// The symbol a path names from a module: <c>crate::</c>, <c>self::</c> and <c>super::</c> first,
  /// then the first segment in the module's scope, every further one in the module the one before
  /// names. <see langword="null"/> when it names nothing of the crate.
  /// </summary>
  /// <param name="from">The module the path is written in.</param>
  /// <param name="segments">The segments.</param>
  /// <param name="at">Where the path is, for a privacy error.</param>
  /// <param name="checkPrivacy">Whether a private item of another module is an error (<c>E0603</c>).</param>
  public Symbol? Resolve(ModuleScope from, IReadOnlyList<string> segments, Span at, bool checkPrivacy) {
    if (segments.Count == 0)
      return null;

    var index = 0;
    ModuleScope? current = null;
    // `::other` is another crate, which names nothing of this one.
    if (segments[0] == "")
      return null;

    while (index < segments.Count && segments[index] is "crate" or "self" or "super") {
      current = segments[index] switch {
        "crate" => this.Root,
        "self" => current ?? from,
        _ => (current ?? from).Parent
      };
      if (current is null)
        return null;

      ++index;
    }

    if (index == segments.Count)
      return current is null ? null : new Symbol(current, true, true, current.Parent ?? current);

    Symbol? symbol = current is null ? this.Lookup(from, segments[index], new HashSet<ModuleScope>(ReferenceEqualityComparer.Instance)) : Member(current, segments[index], from);
    for (++index; symbol is not null && index < segments.Count; ++index) {
      if (symbol.Target is not ModuleScope module)
        return null;

      var next = Member(module, segments[index], from);
      if (next is not null && checkPrivacy && !next.IsVisible && !from.IsWithin(next.Owner)) {
        this._report(RustFrontend.InvalidSource, $"E0603: `{segments[index]}` is private", at);
        return null;
      }

      symbol = next;
    }

    return symbol;
  }

  /// <summary>A name in the scope of a module: its own items, its imports, its globs.</summary>
  public Symbol? Lookup(ModuleScope module, string name, HashSet<ModuleScope> seen) {
    if (!seen.Add(module))
      return null;

    if (module.Items.TryGetValue(name, out var item))
      return item;

    if (module.Imported.TryGetValue(name, out var imported))
      return imported;

    foreach (var glob in module.Globs)
      if (Member(glob, name, module) is { } found && (found.IsVisible || module.IsWithin(found.Owner)))
        return found;

    return null;
  }

  /// <summary>A member of a module seen from another: an item or an import.</summary>
  private static Symbol? Member(ModuleScope module, string name, ModuleScope from) {
    if (module.Items.TryGetValue(name, out var item))
      return item;

    if (module.Imported.TryGetValue(name, out var imported) && (imported.IsVisible || from.IsWithin(module)))
      return imported;

    foreach (var glob in module.Globs)
      if (glob.Items.TryGetValue(name, out var found) && found.IsVisible)
        return found;

    return null;
  }

}

/// <summary>A module of <c>core</c> a <c>use</c> brought in: <c>cmp</c> or <c>mem</c>.</summary>
/// <param name="Name">The name.</param>
internal sealed record LibraryModule(string Name);
