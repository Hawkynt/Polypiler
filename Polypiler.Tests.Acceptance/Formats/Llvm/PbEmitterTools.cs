using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Formats.Llvm;

/// <summary>
/// Runs the pinned <c>pbc</c> and the LLVM tools for the characterization. The executables are the
/// ones the probes of <c>tooling/toolchains.lock</c> found: <c>pbc</c> for <c>pb-compiler</c>, and
/// <c>llvm-as</c> and <c>opt</c> beside the <c>llvm-config</c> of <c>llvm</c>. A test that uses this is
/// marked <c>RequiresToolchain</c> for both keys, so it never gets here with either missing.
/// </summary>
/// <param name="pbc">The <c>pbc</c> executable.</param>
/// <param name="llvmBin">The folder of <c>llvm-as</c> and <c>opt</c>.</param>
public sealed class PbEmitterTools(string pbc, string llvmBin) {

  private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
  private static readonly Lazy<PbEmitterTools> _fromLock = new(FromLock);

  /// <summary>The tools the repository's lock locates on this machine.</summary>
  public static PbEmitterTools Installed => _fromLock.Value;

  private static PbEmitterTools FromLock() {
    var locator = new ToolchainLocator(ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath)), new ProcessRunner());
    string PathOf(string key) => locator.Probe(key).Path ?? throw new InvalidOperationException($"toolchain '{key}' is not installed; the test should have required it");
    return new(PathOf("pb-compiler"), Path.GetDirectoryName(PathOf("llvm"))!);
  }

  /// <summary>Runs <c>pbc --emit-llvm</c> in the source's folder on the bare file name, so <c>; ModuleID</c> holds the name alone.</summary>
  /// <param name="source">The BASIC file.</param>
  public CommandResult EmitLlvm(string source)
    => Run(pbc, ["--emit-llvm", Path.GetFileName(source)], Path.GetDirectoryName(Path.GetFullPath(source))!, null);

  /// <summary>Runs <c>llvm-as</c> on a module, discarding the bitcode.</summary>
  /// <param name="module">The module text.</param>
  public CommandResult Assemble(string module) => Run(this.Tool("llvm-as"), ["-", "-o", NullDevice], null, module);

  /// <summary>Runs <c>opt -passes=verify</c> on a module, discarding the output.</summary>
  /// <param name="module">The module text.</param>
  public CommandResult Verify(string module) => Run(this.Tool("opt"), ["-passes=verify", "-disable-output", "-"], null, module);

  /// <summary>Runs <c>pbc</c>, and when it printed a module both LLVM tools.</summary>
  /// <param name="source">The BASIC file.</param>
  public Observation Observe(string source) {
    var pbcRun = this.EmitLlvm(source);
    return Characterization.OutcomeOf(pbcRun) == Characterization.Emitted
      ? new(pbcRun, this.Assemble(pbcRun.Stdout), this.Verify(pbcRun.Stdout))
      : new(pbcRun, null, null);
  }

  private string Tool(string name) => Path.Combine(llvmBin, OperatingSystem.IsWindows() ? name + ".exe" : name);

  private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

  private static CommandResult Run(string executable, IReadOnlyList<string> arguments, string? directory, string? input) {
    var info = new ProcessStartInfo(executable) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = directory ?? ""
    };
    foreach (var argument in arguments)
      info.ArgumentList.Add(argument);

    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (input != null)
      process.StandardInput.Write(input);
    process.StandardInput.Close();
    if (!process.WaitForExit(_timeout)) {
      process.Kill(true);
      throw new TimeoutException($"{executable} {string.Join(' ', arguments)} ran longer than {_timeout}");
    }

    process.WaitForExit();
    return new(process.ExitCode, output.Result, error.Result);
  }

}

/// <summary>
/// The files of a characterization at the PB-Compiler pin. A corpus file is read from
/// <c>corpus/upstream</c> of the repository when <c>polypiler-tooling corpus import</c> put it there;
/// otherwise it is imported once per test run into a scratch folder, from a local checkout of
/// PB-Compiler beside the repository when one holds the pin, else from a shallow fetch.
/// </summary>
public static class PbCorpus {

  private static readonly Regex _corpusSource = new(@"^(?<repository>[^@:]+/[^@:]+)@(?<commit>[0-9a-f]{40}):(?<path>.+)$", RegexOptions.CultureInvariant);
  private static readonly Lazy<string> _scratch = new(() => {
    var folder = Scratch.Create("polypiler-pb-corpus-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Scratch.Delete(folder);
    return folder;
  });
  private static readonly object _importing = new();

  private static string Root => PrototypeFixture.RepositoryRoot;

  /// <summary>Whether a source names a corpus file rather than a file beside the characterization.</summary>
  /// <param name="source">The source of an entry.</param>
  public static bool IsCorpusFile(string source) => _corpusSource.IsMatch(source);

  /// <summary>The local path of the source of an entry, importing it first when it is a corpus file not yet imported.</summary>
  /// <param name="source">The source of an entry.</param>
  public static string Resolve(string source) {
    var match = _corpusSource.Match(source);
    if (!match.Success)
      return Path.Combine(Characterization.Folder, source.Replace('/', Path.DirectorySeparatorChar));

    var (repository, commit, path) = (match.Groups["repository"].Value, match.Groups["commit"].Value, match.Groups["path"].Value);
    var imported = CorpusImporter.FilePath(Root, repository, commit, path);
    if (File.Exists(imported))
      return imported;

    lock (_importing) {
      var scratch = CorpusImporter.FilePath(_scratch.Value, repository, commit, path);
      if (File.Exists(scratch))
        return scratch;

      var manifest = CorpusManifest.Load(Path.Combine(Root, CorpusManifest.RepositoryPath));
      var fixture = manifest.Fixtures.FirstOrDefault(entry => entry.Repository == repository && entry.Path == path)
        ?? throw new InvalidOperationException($"{source} is no fixture of {CorpusManifest.RepositoryPath}");
      Importer().Import(manifest, CorpusLock.Load(Path.Combine(Root, CorpusLock.RepositoryPath)), [fixture.Id], false);
      return scratch;
    }
  }

  /// <summary>The text of a file of a manifest repository at its pin, read from git without importing it.</summary>
  /// <param name="repository">The repository, <c>owner/name</c>.</param>
  /// <param name="path">The file below the repository's root.</param>
  public static string ReadPinned(string repository, string path) {
    var manifest = CorpusManifest.Load(Path.Combine(Root, CorpusManifest.RepositoryPath));
    var pinned = manifest.Repository(repository) ?? throw new InvalidOperationException($"{repository} is no repository of {CorpusManifest.RepositoryPath}");
    var git = new GitProcess();
    string directory;
    lock (_importing)
      directory = Importer().Locate(pinned);
    var read = git.ReadFile(directory, pinned.Commit, path);
    return read.Succeeded ? Encoding.UTF8.GetString(read.Output) : throw new InvalidOperationException($"{repository}@{pinned.Commit}:{path} cannot be read: {read.Error}");
  }

  private static CorpusImporter Importer() => new(new GitProcess(), _scratch.Value, LocalCheckouts());

  /// <summary>The folders beside the repository or beside a folder above it whose name starts with <c>PB-Compiler</c>.</summary>
  private static List<string> LocalCheckouts() {
    var found = new List<string>();
    for (var directory = new DirectoryInfo(Root).Parent; directory != null; directory = directory.Parent)
      found.AddRange(directory.EnumerateDirectories("PB-Compiler*", new EnumerationOptions { IgnoreInaccessible = true }).Select(candidate => candidate.FullName));
    return found;
  }

}
