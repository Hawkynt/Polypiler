using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust;

/// <summary>A module of a crate besides the one of the IR module: its path from the crate root and its items.</summary>
/// <param name="Name">The path of the module from the crate root, <c>util::bits</c>; its file is <c>src/util/bits.rs</c>.</param>
/// <param name="File">The items.</param>
public sealed record RustModule(string Name, RsFile File) {

  /// <summary>The file of the module in the package, <c>src/util/bits.rs</c>.</summary>
  public string FilePath => "src/" + this.Name.Replace("::", "/", StringComparison.Ordinal) + ".rs";

}

public static partial class RustEmitter {

  /// <summary>
  /// Places functions of a crate into modules of their own, one file each: the layout
  /// <see cref="RustEmitOptions.Modules"/> asks for, such as the modules of a Rust crate the source
  /// was. A placed function moves into the file of its module, <c>src/util/bits.rs</c> for
  /// <c>util::bits</c>, which its parent declares - <c>src/lib.rs</c> a module below the root, the
  /// parent's file one below another, a parent without functions of its own being a file of
  /// declarations. Everything else - the functions placed nowhere, the helpers and spelling functions,
  /// the constants, globals and types, the tests - stays in the module of the IR module, or in
  /// <c>src/lib.rs</c> where the IR module is the crate root. A function that names a type of the crate
  /// stays there too: a type's fields and methods are private to the module that declares it. Each
  /// file names what it uses of another by a <c>use crate::&lt;module&gt;::&lt;item&gt;;</c> line, one per
  /// item, sorted, and only what it uses, as Rust warns about any other; an item another file uses is
  /// <c>pub(crate)</c> at least. The tests module, whose code is compiled only for tests, has lines of
  /// its own. A module is declared <c>pub</c> where it or a module below it has a public item.
  /// </summary>
  private static class Modules {

    /// <summary>A string or character literal, or a comment: text that names nothing.</summary>
    private static readonly Regex _quoted = new("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])'|//[^\\n]*", RegexOptions.CultureInvariant);

    /// <summary>A name at the start of a path: not after a <c>.</c> (a method or field), a <c>::</c> (a path's later segment) or a <c>'</c> (a label).</summary>
    private static readonly Regex _name = new("(?<![\\w.:'#])(?:r#)?[A-Za-z_]\\w*", RegexOptions.CultureInvariant);

    /// <summary>
    /// The crate with its functions placed and, where the IR module is the crate root, what stays in
    /// <c>src/lib.rs</c>; the crate itself where nothing moves.
    /// </summary>
    /// <param name="crate">The crate.</param>
    /// <param name="placement">The module of each function to place, by its Rust name: its path from the crate root.</param>
    /// <param name="inRoot">Whether what stays is written in the crate root rather than a module of its own.</param>
    public static RustCrate Split(RustCrate crate, IReadOnlyDictionary<string, string> placement, bool inRoot) {
      var main = inRoot ? "" : crate.ModuleName;
      var types = crate.Module.Items.Where(item => item is RsStruct or RsTrait).Select(Declared).OfType<string>().ToHashSet(StringComparer.Ordinal);
      var placed = new SortedDictionary<string, List<RsItem>>(StringComparer.Ordinal);
      var kept = new List<RsItem>();
      foreach (var item in crate.Module.Items)
        if (item is RsFunction { IsHelper: false } function && placement.TryGetValue(function.Name, out var module) && module != main && !Named([item]).Overlaps(types)) {
          if (!placed.TryGetValue(module, out var items))
            placed[module] = items = [];

          items.Add(item);
        } else {
          kept.Add(item);
        }

      if (placed.Count == 0 && !inRoot)
        return crate;

      // A parent of a placed module that has no functions of its own is a file of declarations.
      foreach (var path in placed.Keys.ToList())
        for (var parent = Parent(path); parent.Length > 0 && parent != main; parent = Parent(parent))
          placed.TryAdd(parent, []);

      var uses = kept.OfType<RsUse>().ToList();
      var files = new List<(string Path, List<RsItem> Items)> { (main, kept.Where(item => item is not RsUse).ToList()) };
      files.AddRange(placed.Select(pair => (pair.Key, pair.Value)));
      var owners = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (path, items) in files)
        foreach (var declared in items.Select(Declared).OfType<string>())
          owners[declared] = path;

      var shared = new HashSet<string>(StringComparer.Ordinal);
      var written = new List<(string Path, List<RsItem> Items)>();
      foreach (var (path, items) in files) {
        var imports = Imports(Named(items.Where(item => item is not RsInlineModule).ToList()), path, owners, uses, shared).ToList();
        var rest = items.Select(item => item is RsInlineModule tests ? tests with { Items = TestImports(tests.Items, path, owners, imports, shared) } : item);
        written.Add((path, [.. imports, .. rest]));
      }

      RsItem Opened(RsItem item) => item switch {
        RsFunction { Visibility: "" } function when shared.Contains(function.Name) => function with { Visibility = "pub(crate)" },
        RsConst { Visibility: "" } constant when shared.Contains(constant.Name) => constant with { Visibility = "pub(crate)" },
        _ => item
      };

      bool Public(string path) => written.Any(file => (file.Path == path || file.Path.StartsWith(path + "::", StringComparison.Ordinal))
        && file.Items.Any(item => item is RsFunction { Visibility: "pub" } or RsConst { Visibility: "pub" } or RsStruct { Visibility: "pub" } or RsTrait { Visibility: "pub" }));

      // The declarations each file makes of the modules right below it, sorted by name.
      IEnumerable<RsItem> Declarations(string parent) => written
        .Where(file => file.Path.Length > 0 && Parent(file.Path) == parent && (file.Path != main || file.Items.Count > 0))
        .Select(file => file.Path[(file.Path.LastIndexOf(':') + 1)..])
        .Order(StringComparer.Ordinal)
        .Select(name => new RsModuleDeclaration(name, Public(parent.Length == 0 ? name : parent + "::" + name) ? "pub" : ""));

      // The module of the errors among the others, in the order of their names.
      IEnumerable<RsModuleDeclaration> modules = [.. crate.Root.Items.OfType<RsModuleDeclaration>().Where(item => crate.Errors is not null && item.Name == "error"), .. Declarations("").Cast<RsModuleDeclaration>()];
      var root = crate.Root with { Items = [.. modules.OrderBy(item => item.Name, StringComparer.Ordinal), .. crate.Root.Items.Where(item => item is not RsModuleDeclaration), .. inRoot ? written[0].Items.Select(Opened) : []] };
      var own = inRoot ? [] : written[0].Items.Select(Opened);
      return crate with {
        Root = root,
        Module = crate.Module with { Items = [.. Declarations(main.Length == 0 ? "\0" : main), .. own] },
        Modules = [.. written.Skip(1).Select(file => new RustModule(file.Path, new RsFile([], [], [.. Declarations(file.Path), .. file.Items.Select(Opened)])))],
        InRoot = inRoot
      };
    }

    /// <summary>The path of a module's parent, empty for one right below the crate root.</summary>
    private static string Parent(string path) => path.Contains("::", StringComparison.Ordinal) ? path[..path.LastIndexOf("::", StringComparison.Ordinal)] : "";

    /// <summary>The <c>use</c> lines a file needs: of the standard library what it names, of another file of the crate each item it names, sorted.</summary>
    private static IEnumerable<RsUse> Imports(HashSet<string> named, string file, Dictionary<string, string> owners, IReadOnlyList<RsUse> uses, HashSet<string> shared) {
      var lines = uses.Where(use => named.Contains(use.Path[(use.Path.LastIndexOf(':') + 1)..])).Select(use => use.Path).ToList();
      // The crate root re-exports what it imports from the crate's own modules (pub use error::Error), so it needs no use of them.
      if (file.Length == 0)
        lines.RemoveAll(path => path.StartsWith("crate::", StringComparison.Ordinal) && path.IndexOf("::", 7, StringComparison.Ordinal) < 0);
      foreach (var name in named.Where(name => owners.TryGetValue(name, out var owner) && owner != file)) {
        shared.Add(name);
        lines.Add(owners[name].Length == 0 ? $"crate::{name}" : $"crate::{owners[name]}::{name}");
      }

      return lines.Order(StringComparer.Ordinal).Select(path => new RsUse(path));
    }

    /// <summary>
    /// The items of the tests module with the <c>use</c> lines it needs: <c>use super::*;</c> where it
    /// names what its file declares or imports, which the glob brings in, and a line of its own for each
    /// item of another file its file does not import.
    /// </summary>
    private static IReadOnlyList<RsItem> TestImports(IReadOnlyList<RsItem> items, string file, Dictionary<string, string> owners, IReadOnlyList<RsUse> fileImports, HashSet<string> shared) {
      var code = items.Skip(1).ToList();
      var named = Named(code);
      var imported = fileImports.Select(use => use.Path[(use.Path.LastIndexOf(':') + 1)..]).ToHashSet(StringComparer.Ordinal);
      var throughParent = named.Any(name => imported.Contains(name) || owners.TryGetValue(name, out var owner) && owner == file);
      named.ExceptWith(imported);
      return [.. throughParent ? [items[0]] : Array.Empty<RsItem>(), .. Imports(named, file, owners, [], shared), .. code];
    }

    /// <summary>The name an item declares, or <see langword="null"/> for one that declares none a path can name.</summary>
    private static string? Declared(RsItem item) => item switch {
      RsFunction function => function.Name,
      RsConst constant => constant.Name,
      RsStruct type => type.Name,
      RsTrait trait => trait.Name,
      _ => null
    };

    /// <summary>
    /// The names items use at the start of a path, as their printed text shows them - neither the
    /// texts, the comments, a method or field after a <c>.</c> nor a later segment of a path - less
    /// the names of the variables, parameters and closure parameters they bind, which a path of the
    /// same name would mean.
    /// </summary>
    private static HashSet<string> Named(IReadOnlyList<RsItem> items) {
      var text = _quoted.Replace(string.Concat(items.Select(RustPrinter.Print)), " ");
      var names = _name.Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
      foreach (var node in items.SelectMany(RustSyntaxRewriter.DescendantsAndSelf))
        names.ExceptWith(node switch {
          RsLet binding => [binding.Name],
          RsFunction function => function.Parameters.Select(parameter => parameter.Name),
          RsClosure closure => closure.Parameters.Select(parameter => parameter.Name.TrimStart('&')),
          RsFor loop => [loop.Pattern.TrimStart('&')],
          _ => []
        });

      return names;
    }

  }

}
