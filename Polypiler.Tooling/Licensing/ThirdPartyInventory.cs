using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tooling.Licensing;

/// <summary>A notice file of the repository and the third-party works it records.</summary>
/// <param name="Path">The notice, relative to the repository root.</param>
/// <param name="Components">The works the notice covers: a name and an SPDX expression each.</param>
public sealed record ThirdPartyNotice(string Path, IReadOnlyList<(string Name, string Licence)> Components);

/// <summary>A NuGet package the repository's projects reference.</summary>
/// <param name="Id">The package ID.</param>
/// <param name="Version">The version the projects reference.</param>
/// <param name="Licence">The licence expression of the package's manifest.</param>
public sealed record ThirdPartyPackage(string Id, string Version, string Licence);

/// <summary>
/// <c>tooling/licensing/third-party.yaml</c>: every third-party work the repository itself contains or
/// builds on — the code converted from somebody else's (recorded by a <c>THIRD-PARTY-NOTICE.*</c> or
/// <c>THIRD_PARTY_NOTICES.*</c> file beside it) and every NuGet package a project references — with
/// its licence. <see cref="Check"/> holds it against the repository: a notice file or a package the
/// inventory does not list cannot be judged (<c>PLY0029</c>), an entry nothing in the repository
/// matches is stale (<c>PLY0026</c>), and every licence must be allowed in a distribution
/// (<c>PLY0024</c>), since the repository's projects ship.
/// </summary>
public sealed class ThirdPartyInventory {

  /// <summary>The inventory's path below the repository root.</summary>
  public const string RepositoryPath = "tooling/licensing/third-party.yaml";

  private const string What = "licence inventory";

  /// <summary>The folders the repository walk does not enter at any depth: build output, installed packages, and every folder whose name starts with a dot.</summary>
  private static readonly string[] _skipped = ["bin", "obj", "node_modules"];

  /// <summary>The folders of the repository root the walk does not enter: imported, generated and planning files.</summary>
  private static readonly string[] _skippedAtRoot = ["artifacts", "corpus", "backlog"];

  private ThirdPartyInventory(IReadOnlyList<ThirdPartyNotice> notices, IReadOnlyList<ThirdPartyPackage> packages) {
    this.Notices = notices;
    this.Packages = packages;
  }

  /// <summary>The notice files, in file order.</summary>
  public IReadOnlyList<ThirdPartyNotice> Notices { get; }

  /// <summary>The packages, in file order.</summary>
  public IReadOnlyList<ThirdPartyPackage> Packages { get; }

  /// <summary>Reads an inventory file.</summary>
  /// <exception cref="CorpusException">The file cannot be read or is invalid (<c>PLY0026</c>).</exception>
  public static ThirdPartyInventory Load(string path) {
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"cannot read '{path}': {exception.Message}"));
    }

    return Parse(text, path);
  }

  /// <summary>Checks the text of an inventory.</summary>
  /// <exception cref="CorpusException">The text is invalid (<c>PLY0026</c>).</exception>
  public static ThirdPartyInventory Parse(string text, string file) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"not YAML: {exception.Message}", file, (int)exception.Start.Line, (int)exception.Start.Column));
    }

    if (stream.Documents.Count != 1
        || stream.Documents[0].RootNode is not YamlMappingNode root
        || root.Children.Count != 3
        || Child(root, "schema") is not YamlScalarNode { Value: "1" }
        || Child(root, "notices") is not YamlSequenceNode notices
        || Child(root, "packages") is not YamlSequenceNode packages)
      throw new CorpusException(LicenceDiagnostics.Invalid(What, "the file must be a mapping of exactly 'schema: 1' and the lists 'notices' and 'packages'", file));

    var problems = new List<Diagnostic>();
    var noticeList = new List<ThirdPartyNotice>();
    foreach (var item in notices.Children) {
      var components = Child(item, "components") as YamlSequenceNode;
      var named = components?.Children.Select(component => (Name: Scalar(component, "name"), Licence: Scalar(component, "licence"))).ToList();
      if (item is YamlMappingNode { Children.Count: 2 } && Scalar(item, "path") is { } path && named is { Count: > 0 } && named.All(entry => entry.Name is not null && entry.Licence is not null))
        noticeList.Add(new(path, [.. named.Select(entry => (entry.Name!, entry.Licence!))]));
      else
        problems.Add(Problem(file, item, "a notice must be a mapping of 'path' and a non-empty list 'components' of 'name' and 'licence'"));
    }

    var packageList = new List<ThirdPartyPackage>();
    foreach (var item in packages.Children) {
      if (item is YamlMappingNode { Children.Count: 3 } && Scalar(item, "id") is { } id && Scalar(item, "version") is { } version && Scalar(item, "licence") is { } licence)
        packageList.Add(new(id, version, licence));
      else
        problems.Add(Problem(file, item, "a package must be a mapping of exactly 'id', 'version' and 'licence'"));
    }

    if (problems.Count > 0)
      throw new CorpusException(problems);
    return new(noticeList, packageList);
  }

  private static YamlNode? Child(YamlNode node, string name)
    => node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;

  private static string? Scalar(YamlNode node, string name)
    => Child(node, name) is YamlScalarNode { Value.Length: > 0 } scalar ? scalar.Value : null;

  private static Diagnostic Problem(string file, YamlNode node, string reason)
    => LicenceDiagnostics.Invalid(What, reason, file, (int)node.Start.Line, (int)node.Start.Column);

  /// <summary>Whether a file name is a third-party notice's.</summary>
  public static bool IsNotice(string name)
    => name.StartsWith("THIRD-PARTY-NOTICE", StringComparison.Ordinal) || name.StartsWith("THIRD_PARTY_NOTICES", StringComparison.Ordinal);

  /// <summary>Holds the inventory against the repository below a root and the policy.</summary>
  /// <returns>Every problem, in the order: unlisted or stale notices, unlisted or stale packages, licences.</returns>
  public IReadOnlyList<Diagnostic> Check(string root, LicencePolicy policy) {
    var problems = new List<Diagnostic>();
    var files = RepositoryFiles(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToList();

    var notices = files.Where(file => IsNotice(Path.GetFileName(file))).ToList();
    foreach (var notice in notices.Where(notice => this.Notices.All(entry => entry.Path != notice)))
      problems.Add(LicenceDiagnostics.Unknown(notice, $"the notice is not listed in {RepositoryPath}"));
    foreach (var entry in this.Notices.Where(entry => !notices.Contains(entry.Path)))
      problems.Add(LicenceDiagnostics.Invalid(What, $"notice '{entry.Path}' is listed but does not exist"));

    var references = new List<(string Id, string Version, string Project)>();
    foreach (var project in files.Where(file => file.EndsWith(".csproj", StringComparison.Ordinal) || file.EndsWith(".props", StringComparison.Ordinal) || file.EndsWith(".targets", StringComparison.Ordinal)))
      references.AddRange(PackageReferences(root, project, problems));
    foreach (var reference in references.DistinctBy(reference => (reference.Id, reference.Version)).Where(reference => !this.Packages.Any(entry => entry.Id == reference.Id && entry.Version == reference.Version)))
      problems.Add(LicenceDiagnostics.Unknown($"{reference.Id} {reference.Version}", $"the package, referenced by '{reference.Project}', is not listed in {RepositoryPath}"));
    foreach (var entry in this.Packages.Where(entry => !references.Any(reference => reference.Id == entry.Id && reference.Version == entry.Version)))
      problems.Add(LicenceDiagnostics.Invalid(What, $"package '{entry.Id} {entry.Version}' is listed but no project references it"));

    var licences = this.Notices.SelectMany(notice => notice.Components.Select(component => ($"{notice.Path}: {component.Name}", component.Licence)))
      .Concat(this.Packages.Select(package => ($"{package.Id} {package.Version}", package.Licence)));
    foreach (var (name, licence) in licences) {
      LicenceStatus status;
      try {
        status = policy.Evaluate(SpdxExpression.Parse(licence), LicenceMode.Distribution);
      } catch (FormatException exception) {
        problems.Add(LicenceDiagnostics.Unknown(name, $"'{licence}' is not an SPDX expression: {exception.Message}"));
        continue;
      }

      if (status != LicenceStatus.Allowed)
        problems.Add(LicenceDiagnostics.Refused($"'{name}': licence '{licence}' is denied in distribution mode"));
    }

    return problems;
  }

  /// <summary>The <c>PackageReference</c> items of an MSBuild file that name a version.</summary>
  private static IEnumerable<(string Id, string Version, string Project)> PackageReferences(string root, string project, List<Diagnostic> problems) {
    XDocument document;
    try {
      document = XDocument.Load(Path.Combine(root, project));
    } catch (XmlException exception) {
      problems.Add(LicenceDiagnostics.Invalid(What, $"'{project}' is not XML: {exception.Message}"));
      return [];
    }

    return document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
      .Select(element => (Id: (string?)element.Attribute("Include"), Version: (string?)element.Attribute("Version") ?? (string?)element.Element(element.Name.Namespace + "Version")))
      .Where(reference => reference.Id is not null && reference.Version is not null)
      .Select(reference => (reference.Id!, reference.Version!, project));
  }

  /// <summary>Every file below a root, relative with <c>/</c> separators, outside the skipped folders, in ordinal order.</summary>
  private static IEnumerable<string> RepositoryFiles(string root) {
    var pending = new Stack<string>([root]);
    var files = new List<string>();
    while (pending.Count > 0) {
      var folder = pending.Pop();
      files.AddRange(Directory.EnumerateFiles(folder).Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')));
      foreach (var child in Directory.EnumerateDirectories(folder)) {
        var name = Path.GetFileName(child);
        if (!name.StartsWith('.') && !_skipped.Contains(name) && !(folder == root && _skippedAtRoot.Contains(name)))
          pending.Push(child);
      }
    }

    return files.Order(StringComparer.Ordinal);
  }

}
