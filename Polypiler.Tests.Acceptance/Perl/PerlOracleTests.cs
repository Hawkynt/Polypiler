using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Perl;

/// <summary>
/// The Perl frontend's own lexer and parser against perl itself, the differential oracle of the Perl
/// layer (tooling/ci/layers/pyperl.yaml): <c>Perl/Oracle/B/PolypilerOutline.pm</c>, a compiler backend
/// of the pinned perl, gives perl's verdict on a file and, where perl compiles it, the outline the
/// frontend gives as well - the subs the file defines and the lexicals of every pad - and
/// <c>Perl/Oracle/shape.pl</c> gives the shape of perl's op tree for an expression. On the frontend's
/// fixtures, on the tests (<c>t/</c>) and modules (<c>lib/</c>) of the pinned perl distribution and on
/// truncated copies of them, the frontend's verdict and outline are perl's wherever it gives one; on a
/// corpus of expressions over every level of perlop, the shapes are perl's. The frontend never runs
/// perl; only this test does.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PerlOracleTests {

  /// <summary>The messages perl gives for a file it cannot parse, as opposed to one it refuses for another reason (a missing module, strict, a failing BEGIN block).</summary>
  private static readonly string[] _grammarErrors = [
    "syntax error", "Missing right curly", "Unmatched right curly", "Can't find string terminator", "Unrecognized character",
    "Missing $ on loop variable", "Illegal declaration", "Unterminated <> operator", "pattern not terminated", "replacement not terminated",
    "Missing right brace on", "Unterminated delimiter for here document", "Indentation on line", "Can't use global", "Missing control char name",
    "Illegal octal digit", "Illegal binary digit", "Missing name in", "Final $ should be", "Missing comma after first argument",
    "Can't declare", "Prototype after", "Missing braces on", "is not supported by Perl 5.42", "required--this is only v5"
  ];

  private sealed record PerlVerdict(string Kind, string Outline, string Error);

  private static string Oracle => Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Tests.Acceptance", "Perl", "Oracle");

  /// <summary>
  /// perl's verdict on a file, compiled from <paramref name="workingDirectory"/> under <c>timeout</c>:
  /// a test's BEGIN code may run for long or start processes, so neither output goes through a pipe.
  /// </summary>
  private static PerlVerdict AskPerl(string file, string workingDirectory) {
    var stem = Path.Combine(Path.GetTempPath(), "polypiler-perl-oracle-" + Guid.NewGuid().ToString("N"));
    var outline = stem + ".outline";
    var errors = stem + ".stderr";
    var start = new ProcessStartInfo("sh") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true };
    start.ArgumentList.Add("-c");
    start.ArgumentList.Add("exec timeout -k 5 30 perl \"-I$1\" \"-MO=PolypilerOutline,$2\" \"$3\" >/dev/null 2>\"$4\" </dev/null");
    start.ArgumentList.Add("sh");
    start.ArgumentList.Add(Oracle);
    start.ArgumentList.Add(outline);
    start.ArgumentList.Add(file);
    start.ArgumentList.Add(errors);
    using (var process = Process.Start(start)!) {
      process.StandardInput.Close();
      process.WaitForExit();
      if (process.ExitCode is 124 or 137)
        return new("timeout", "", "");
    }
    try {
      if (File.Exists(outline))
        return new("valid", File.ReadAllText(outline, Encoding.UTF8), "");
      var error = File.Exists(errors) ? File.ReadAllText(errors) : "";
      return new(_grammarErrors.Any(error.Contains) ? "invalid" : "other", "", error);
    } finally {
      File.Delete(outline);
      File.Delete(errors);
    }
  }

  private static string ReadSource(string file) {
    var bytes = File.ReadAllBytes(file);
    if (bytes is [0xFE, 0xFF, ..])
      return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
    if (bytes is [0xFF, 0xFE, ..])
      return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    return new UTF8Encoding(false).GetString(bytes);
  }

  private sealed class Tally {
    public int Valid;
    public int Invalid;
    public int Unsupported;
    public int OutsideGrammar;
    public ConcurrentBag<string> Disagreements { get; } = [];
    public int Decided => this.Valid + this.Invalid;
  }

  /// <summary>Compares the frontend with perl on files, each compiled from <paramref name="workingDirectory"/>.</summary>
  private static Tally Compare(IEnumerable<string> files, string workingDirectory) {
    var tally = new Tally();
    System.Threading.ThreadPool.GetMinThreads(out var workers, out var ports);
    System.Threading.ThreadPool.SetMinThreads(Math.Max(workers, 2 * Environment.ProcessorCount), ports);
    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, file => {
      var ours = PerlFrontend.Parse(file, ReadSource(file));
      if (ours.Verdict == Core.Frontends.Perl.PerlVerdict.Unsupported) {
        System.Threading.Interlocked.Increment(ref tally.Unsupported);
        return;
      }
      var theirs = AskPerl(file, workingDirectory);
      if (theirs.Kind is "other" or "timeout") {
        System.Threading.Interlocked.Increment(ref tally.OutsideGrammar);
        return;
      }
      var message = ours.Diagnostics.Select(d => d.ToString()).FirstOrDefault();
      if ((ours.Verdict == Core.Frontends.Perl.PerlVerdict.Valid) != (theirs.Kind == "valid")) {
        tally.Disagreements.Add($"{file}: frontend {ours.Verdict} ({message}), perl {theirs.Kind} ({theirs.Error.Trim()})");
        return;
      }
      if (ours.Verdict == Core.Frontends.Perl.PerlVerdict.Valid && ours.Outline != theirs.Outline) {
        tally.Disagreements.Add($"{file}: outline\n  frontend: {ours.Outline.Replace("\n", "\n            ", StringComparison.Ordinal)}\n  perl:     {theirs.Outline.Replace("\n", "\n            ", StringComparison.Ordinal)}");
        return;
      }
      if (ours.Verdict == Core.Frontends.Perl.PerlVerdict.Valid)
        System.Threading.Interlocked.Increment(ref tally.Valid);
      else
        System.Threading.Interlocked.Increment(ref tally.Invalid);
    });
    return tally;
  }

  private static void Report(string name, Tally tally) {
    TestContext.Out.WriteLine($"| {name} | {tally.Decided + tally.Disagreements.Count} | {tally.Valid} | {tally.Invalid} | {tally.Unsupported} | {tally.OutsideGrammar} | {tally.Disagreements.Count} |");
    foreach (var disagreement in tally.Disagreements.Take(20))
      TestContext.Out.WriteLine(disagreement);
  }

  private static void ReportHeader() {
    TestContext.Out.WriteLine("| Corpus | Decided | Valid, agreed | Invalid, agreed | Outside the parser | Refused by perl outside the grammar | Disagreements |");
    TestContext.Out.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
  }

  /// <summary>The frontend's fixtures - the imported programs and modules of its tests and of the layer - are decided as perl decides them.</summary>
  [Test]
  [RequiresToolchain("perl")]
  public void TheFixtures_AreDecidedAsPerlDecidesThem() {
    var root = PrototypeFixture.RepositoryRoot;
    var files = new[] { Path.Combine(root, "tests", "fixtures", "perl"), Path.Combine(root, "tests", "fixtures", "infra", "ci", "pyperl") }
      .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
      .Where(file => Path.GetExtension(file) is ".pl" or ".pm" or ".t").Order(StringComparer.Ordinal).ToList();
    var tally = Compare(files, Path.Combine(root, "tests", "fixtures", "infra", "ci", "pyperl"));
    ReportHeader();
    Report("fixtures", tally);
    Assert.That(tally.Disagreements, Is.Empty);
    Assert.That(tally.Decided, Is.GreaterThan(files.Count / 2), "most fixtures are decided");
  }

  /// <summary>
  /// The tests and modules of the pinned perl distribution (its sources unpacked where
  /// <c>POLYPILER_PERL_DIST</c> names, as the layer job does), and copies of them cut in the middle or
  /// given an extra closing brace: every verdict and outline the frontend gives is perl's.
  /// </summary>
  [Test]
  [RequiresToolchain("perl")]
  public void ThePerlDistribution_ParsesAsPerlCompilesIt() {
    var dist = Environment.GetEnvironmentVariable("POLYPILER_PERL_DIST");
    if (string.IsNullOrEmpty(dist) || !Directory.Exists(Path.Combine(dist, "t")))
      throw new InconclusiveException("POLYPILER_PERL_DIST names no unpacked perl distribution");
    var run = Directory.CreateTempSubdirectory("polypiler-perl-dist-").FullName;
    try {
      var tests = Path.Combine(run, "t");
      CopyDirectory(Path.Combine(dist, "t"), tests);
      LinkInstalledLibrary(Path.Combine(run, "lib"));
      var testFiles = Directory.EnumerateFiles(tests, "*.t", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
      var moduleFiles = Directory.EnumerateFiles(Path.Combine(dist, "lib"), "*.pm", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

      var originals = Compare(testFiles.Concat(moduleFiles), tests);
      var mutants = WriteMutants(testFiles.Concat(moduleFiles), dist, run);
      var cut = Compare(mutants, tests);
      ReportHeader();
      Report($"perl {Path.GetFileName(dist)}: t/**/*.t and lib/**/*.pm", originals);
      Report("the same, cut in the middle or with an extra closing brace", cut);
      Assert.That(originals.Disagreements.Concat(cut.Disagreements).Take(20), Is.Empty);
      Assert.That(originals.Decided, Is.GreaterThan(400), "the parser decides most of the distribution");
      Assert.That(cut.Invalid, Is.GreaterThan(200), "the cut copies are refused as perl refuses them");
    } finally {
      Directory.Delete(run, true);
    }
  }

  private static void CopyDirectory(string from, string to) {
    Directory.CreateDirectory(to);
    foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
      Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
      File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
  }

  /// <summary>The installed perl's library as <c>../lib</c> of the copied tests, which set their <c>@INC</c> to it.</summary>
  private static void LinkInstalledLibrary(string lib) {
    Directory.CreateDirectory(lib);
    var start = new ProcessStartInfo("perl") { RedirectStandardOutput = true, UseShellExecute = false };
    start.ArgumentList.Add("-MConfig");
    start.ArgumentList.Add("-e");
    start.ArgumentList.Add("print qq($Config{archlib}\\n$Config{privlib}\\n)");
    using var process = Process.Start(start)!;
    var folders = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    process.WaitForExit();
    foreach (var folder in folders)
      foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) {
        var target = Path.Combine(lib, Path.GetFileName(entry));
        if (!Path.Exists(target))
          File.CreateSymbolicLink(target, entry);
      }
  }

  private static List<string> WriteMutants(IEnumerable<string> files, string dist, string run) {
    var mutants = new List<string>();
    foreach (var file in files) {
      var source = ReadSource(file);
      var end = source.Length;
      foreach (var marker in new[] { "\n__END__", "\n__DATA__" })
        if (source.IndexOf(marker, StringComparison.Ordinal) is var at and >= 0)
          end = Math.Min(end, at);
      var lines = source[..end].Split('\n');
      if (lines.Length < 4)
        continue;
      var variants = new Dictionary<string, string> {
        ["half"] = string.Join('\n', lines[..(lines.Length / 2)]) + "\n",
        ["brace"] = source[..end] + "\n}\n"
      };
      foreach (var (name, text) in variants) {
        var target = file.StartsWith(run, StringComparison.Ordinal)
          ? file + ".cut-" + name + ".t"
          : Path.Combine(run, "libcut", Path.GetRelativePath(dist, file) + ".cut-" + name + ".pm");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text, new UTF8Encoding(false));
        mutants.Add(target);
      }
    }
    return mutants;
  }

  /// <summary>Expressions over every level of perlop, the named unary and list operators and the term/operator decisions, each with the shape of perl's op tree.</summary>
  [Test]
  [RequiresToolchain("perl")]
  public void TheExpressions_HavePerlsShape() {
    var expressions = ExpressionCorpus().Distinct(StringComparer.Ordinal).ToList();
    var start = new ProcessStartInfo("perl") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
    start.ArgumentList.Add(Path.Combine(Oracle, "shape.pl"));
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
      foreach (var expression in expressions)
        input.WriteLine(expression);
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, errors.Result);
    var shapes = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That(shapes, Has.Length.EqualTo(expressions.Count));

    var disagreements = new List<string>();
    var refused = 0;
    for (var i = 0; i < expressions.Count; ++i) {
      var ours = PerlFrontend.ExpressionShape(expressions[i]);
      if (shapes[i].StartsWith("error:", StringComparison.Ordinal)) {
        ++refused;
        if (!ours.StartsWith("error:", StringComparison.Ordinal))
          disagreements.Add($"{expressions[i]}\n  frontend: {ours}\n  perl:     {shapes[i]}");
        continue;
      }
      if (ours != shapes[i])
        disagreements.Add($"{expressions[i]}\n  frontend: {ours}\n  perl:     {shapes[i]}");
    }
    TestContext.Out.WriteLine($"{expressions.Count} expressions, {expressions.Count - refused} compared with perl's op tree, {refused} refused by perl and the frontend, {disagreements.Count} disagreements");
    Assert.That(disagreements.Take(20), Is.Empty);
    Assert.That(refused, Is.LessThan(expressions.Count / 10), "perl compiles the corpus");
  }

  private static readonly string[] _binaryOperators = [
    "**", "*", "/", "%", "x", "+", "-", "<<", ">>", "<", ">=", "lt", "ge", "==", "!=", "<=>", "eq", "cmp", "&", "|", "^", "&&", "||", "//", "and", "or", "xor"
  ];

  private static readonly string[] _namedUnary = ["ref", "defined", "lc", "uc", "length", "abs", "int", "ord", "chr", "hex", "oct", "sqrt", "-e", "-s", "-d"];

  private static IEnumerable<string> ExpressionCorpus() {
    foreach (var left in _binaryOperators)
      foreach (var right in _binaryOperators)
        yield return $"$a {left} $b {right} $c";
    foreach (var op in _binaryOperators) {
      yield return $"-$a {op} $b";
      yield return $"!$a {op} $b";
      yield return $"~$a {op} $b";
      yield return $"$a++ {op} --$b";
      yield return $"$a {op} -$b";
      yield return $"not $a {op} $b";
      yield return $"$x = $a {op} $b";
      yield return $"$a ? $b {op} $c : $d";
      yield return $"$a {op} $b ? $c : $d";
      foreach (var named in _namedUnary) {
        yield return $"{named} $a {op} $b";
        yield return $"$a {op} {named} $b";
        yield return $"{named}($a) {op} $b";
      }
    }
    foreach (var named in _namedUnary) {
      yield return $"!{named} $a";
      yield return $"{named} -$a";
      yield return $"{named} $a ? $b : $c";
    }
    yield return "$a ** $b ** $c";
    yield return "-$a ** -$b";
    yield return "$a = $b = $c";
    yield return "$a += $b *= $c";
    yield return "$a ? $b : $c ? $d : $e";
    yield return "$a ? $b = $c : $d";
    yield return "$a =~ /x/ && $b !~ /y/";
    yield return "$a =~ s/x/y/ || $b";
    yield return "join $a, $b, $c or $d";
    yield return "join($a, $b, $c), $d";
    yield return "push @a, $b, $c and $d";
    yield return "time / 2";
    yield return "time - $a";
    yield return "$a / 2 / $b";
    yield return "split /,/, $a";
    yield return "split /,/, $a, $b + 1";
    yield return "-e $a && -d _";
    yield return "-e -f $a";
    yield return "defined $a && ref $b eq 'HASH'";
    yield return "$a x $b x $c";
    yield return "!!$a";
    yield return "not not $a";
    yield return "$a || $b && $c // $d";
    yield return "$a and $b or $c and $d";
    yield return "$a or $b xor $c";
  }

}
