using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// Finds machine profiles by ID as <c>&lt;directory&gt;/&lt;id&gt;.yaml</c> in a list of directories,
/// the first that has the file winning, and keeps each one loaded. <see cref="Find"/> is the resolver
/// <see cref="MachineSyntax.Parse"/> takes.
/// </summary>
/// <param name="directories">The directories, searched in order; normally the repository's <c>spec/machine</c>.</param>
public sealed class MachineProfileCatalog(IEnumerable<string> directories) {

  private readonly IReadOnlyList<string> _directories = directories?.ToList() ?? throw new ArgumentNullException(nameof(directories));
  private readonly ConcurrentDictionary<string, IsaProfile?> _loaded = new(StringComparer.Ordinal);

  /// <summary>The profile of an ID, or <see langword="null"/> when no directory has its file.</summary>
  /// <param name="id">The ID.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="MachineException">The file exists but breaks a rule (<c>PLY4003</c>).</exception>
  public IsaProfile? Find(string id) {
    ArgumentNullException.ThrowIfNull(id);
    return this._loaded.GetOrAdd(id, key => {
      if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        return null;
      var path = this._directories.Select(directory => Path.Combine(directory, key + ".yaml")).FirstOrDefault(File.Exists);
      return path is null ? null : IsaProfile.Load(path);
    });
  }

}
