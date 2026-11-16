using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust;

/// <summary>A module of a crate besides the one of the IR module: its name, which is also its file's, and its items.</summary>
/// <param name="Name">The name of the module, <c>src/&lt;name&gt;.rs</c>.</param>
/// <param name="File">The items.</param>
public sealed record RustModule(string Name, RsFile File);

public static partial class RustEmitter {

  /// <summary>
  /// Places functions of a crate into modules of their own, one file each: the layout
  /// <see cref="RustEmitOptions.Modules"/> asks for, such as the modules of a Rust crate the source
  /// was. A placed function moves into <c>src/&lt;module&gt;.rs</c>; everything else - the functions
  /// placed nowhere, the helpers and spelling functions, the constants, globals and types, the tests -
  /// stays in the module of the IR module. Each file names what it uses of another by a
  /// <c>use crate::&lt;module&gt;::&lt;item&gt;;</c> line, one per item, sorted, and only what it uses, as
  /// Rust warns about any other; an item another file uses is <c>pub(crate)</c> at least. The tests
  /// module, whose code is compiled only for tests, has lines of its own. A function placed in a
  /// module of its own that names a type of the crate is refused: the type's fields and methods are
  /// private to the module that declares it.
  /// </summary>
  private static class Modules {

    /// <summary>A string or character literal, or a comment: text that names nothing.</summary>
    private static readonly Regex _quoted = new("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])'|//[^\\n]*", RegexOptions.CultureInvariant);

    /// <summary>A name at the start of a path: not after a <c>.</c> (a method or field), a <c>::</c> (a path's later segment) or a <c>'</c> (a label).</summary>
    private static readonly Regex _name = new("(?<![\\w.:'#])(?:r#)?[A-Za-z_]\\w*", RegexOptions.CultureInvariant);

    /// <summary>
    /// The crate with its functions placed: the IR module's file without them, a file per module that
    /// has one, the declarations of the modules in <c>src/lib.rs</c> sorted by name - <c>pub mod</c>
    /// where a module has a public item, else <c>mod</c>. The crate itself where nothing is placed.
    /// </summary>
    /// <param name="crate">The crate.</param>
    /// <param name="placement">The module of each function to place, by its Rust name.</param>
    /// <param name="refuse">Records what cannot be placed.</param>
    public static RustCrate Split(RustCrate crate, IReadOnlyDictionary<string, string> placement, Action<string> refuse) {
      var placed = new SortedDictionary<string, List<RsItem>>(StringComparer.Ordinal);
      var kept = new List<RsItem>();
      foreach (var item in crate.Module.Items)
        if (item is RsFunction { IsHelper: false } function && placement.TryGetValue(function.Name, out var module) && module != crate.ModuleName) {
          if (!placed.TryGetValue(module, out var items))
            placed[module] = items = [];

          items.Add(item);
        } else {
          kept.Add(item);
        }

      if (placed.Count == 0)
        return crate;

      var uses = kept.OfType<RsUse>().ToList();
      var files = new List<(string Name, List<RsItem> Items)> { (crate.ModuleName, kept.Where(item => item is not RsUse).ToList()) };
      files.AddRange(placed.Select(pair => (pair.Key, pair.Value)));
      var owners = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (name, items) in files)
        foreach (var declared in items.Select(Declared).OfType<string>())
          owners[declared] = name;

      var types = files[0].Items.Where(item => item is RsStruct or RsTrait).Select(Declared).OfType<string>().ToHashSet(StringComparer.Ordinal);
      var shared = new HashSet<string>(StringComparer.Ordinal);
      var written = new List<(string Name, List<RsItem> Items)>();
      foreach (var (name, items) in files) {
        var code = items.Where(item => item is not RsInlineModule).ToList();
        var named = Named(code);
        if (name != crate.ModuleName)
          foreach (var type in types.Where(named.Contains).Order(StringComparer.Ordinal))
            refuse($"a function placed in the module '{name}' that names the type '{type}' of the crate");

        var imports = Imports(named, name, owners, uses, shared);
        var rest = items.Select(item => item is RsInlineModule tests ? tests with { Items = WithImports(tests.Items, Imports(Named(tests.Items), name, owners, [], shared)) } : item);
        written.Add((name, [.. imports, .. rest]));
      }

      RsItem Opened(RsItem item) => item switch {
        RsFunction { Visibility: "" } function when shared.Contains(function.Name) => function with { Visibility = "pub(crate)" },
        RsConst { Visibility: "" } constant when shared.Contains(constant.Name) => constant with { Visibility = "pub(crate)" },
        _ => item
      };

      var declarations = written.Where(file => file.Name != crate.ModuleName || file.Items.Count > 0)
        .Select(file => new RsModuleDeclaration(file.Name, file.Items.Any(item => item is RsFunction { Visibility: "pub" } or RsConst { Visibility: "pub" } or RsStruct { Visibility: "pub" } or RsTrait { Visibility: "pub" }) ? "pub" : ""))
        .OrderBy(declaration => declaration.Name, StringComparer.Ordinal);
      var root = crate.Root with { Items = [.. crate.Root.Items.Where(item => item is not RsModuleDeclaration), .. declarations] };
      return crate with {
        Root = root,
        Module = crate.Module with { Items = [.. written[0].Items.Select(Opened)] },
        Modules = [.. written.Skip(1).Select(file => new RustModule(file.Name, new RsFile([], [], [.. file.Items.Select(Opened)])))]
      };
    }

    /// <summary>The <c>use</c> lines a file needs: of the standard library what it names, of another file of the crate each item it names, sorted.</summary>
    private static IEnumerable<RsUse> Imports(HashSet<string> named, string file, Dictionary<string, string> owners, IReadOnlyList<RsUse> uses, HashSet<string> shared) {
      var lines = uses.Where(use => named.Contains(use.Path[(use.Path.LastIndexOf(':') + 1)..])).Select(use => use.Path).ToList();
      foreach (var name in named.Where(name => owners.TryGetValue(name, out var owner) && owner != file)) {
        shared.Add(name);
        lines.Add($"crate::{owners[name]}::{name}");
      }

      return lines.Order(StringComparer.Ordinal).Select(path => new RsUse(path));
    }

    /// <summary>The items of the tests module with the lines it needs after its <c>use super::*;</c>.</summary>
    private static IReadOnlyList<RsItem> WithImports(IReadOnlyList<RsItem> items, IEnumerable<RsUse> imports) => [items[0], .. imports, .. items.Skip(1)];

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
