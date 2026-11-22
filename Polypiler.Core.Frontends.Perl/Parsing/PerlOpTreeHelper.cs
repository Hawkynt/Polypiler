using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Polypiler.Core.Frontends.Perl.Parsing;

/// <summary>
/// Compiles a Perl source file with the installed 64-bit Perl interpreter using the embedded
/// <c>B::PolypilerDump</c> compiler backend and returns the JSON op-tree dump.
/// </summary>
internal static class PerlOpTreeHelper {

  public const string InterpreterVariable = "POLYPILER_PERL";
  public const string ParserRelativePath = "B/PolypilerDump.pm";

  public static byte[] ParserSource { get; } = ReadResource();

  public static IReadOnlyList<string> Candidates(string? named) {
    if (!string.IsNullOrWhiteSpace(named))
      return [named];
    var list = new List<string>();
    var env = Environment.GetEnvironmentVariable(InterpreterVariable);
    if (!string.IsNullOrWhiteSpace(env))
      list.Add(env);
    list.Add("perl");
    return list;
  }

  public static (byte[]? Dump, string? Error, int Line) Compile(string file, string source, string? interpreter) {
    var failures = new List<string>();
    foreach (var candidate in Candidates(interpreter)) {
      var (dump, error, line, started) = RunWith(candidate, file, source);
      if (started)
        return (dump, error, line);
      failures.Add(error!);
    }
    return (null, $"no Perl interpreter could be started ({string.Join("; ", failures)}); set {InterpreterVariable} to one", 1);
  }

  private static (byte[]? Dump, string? Error, int Line, bool Started) RunWith(string perl, string file, string source) {
    var temp = Directory.CreateTempSubdirectory("polypiler-perl-");
    try {
      var dumpDir = Path.Combine(temp.FullName, "B");
      Directory.CreateDirectory(dumpDir);
      File.WriteAllBytes(Path.Combine(dumpDir, "PolypilerDump.pm"), ParserSource);
      var mooseDir = Path.Combine(temp.FullName, "Moose");
      Directory.CreateDirectory(mooseDir);
      File.WriteAllText(Path.Combine(temp.FullName, "Moose.pm"), MooseCollectorSource, new UTF8Encoding(false));
      File.WriteAllText(Path.Combine(mooseDir, "Role.pm"), MooseRoleCollectorSource, new UTF8Encoding(false));
      var inputPath = Path.Combine(temp.FullName, "polypiler_input.pl");
      File.WriteAllBytes(inputPath, new UTF8Encoding(false).GetBytes(source));

      var start = new ProcessStartInfo(perl) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = temp.FullName,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
      };
      start.ArgumentList.Add("-I" + temp.FullName);
      start.ArgumentList.Add("-MO=PolypilerDump");
      start.ArgumentList.Add(inputPath);

      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception ex) {
        return (null, $"{perl}: {ex.Message}", 1, false);
      }

      using (process) {
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        if (process.ExitCode != 0) {
          var err = stderrTask.Result.Trim().Replace(inputPath, file, StringComparison.Ordinal);
          var match = Regex.Match(err, @"\bline ([0-9]+)\b", RegexOptions.CultureInvariant);
          var line = match.Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 1;
          return (null, err.Length > 0 ? err : $"{perl} exited with {process.ExitCode}", line, true);
        }

        return (Encoding.UTF8.GetBytes(stdoutTask.Result), null, 1, true);
      }
    } finally {
      try { temp.Delete(recursive: true); } catch (IOException) { }
    }
  }

  private static byte[] ReadResource() {
    using var stream = typeof(PerlOpTreeHelper).Assembly.GetManifestResourceStream("Polypiler.Core.Frontends.Perl.PolypilerDump.pm")
      ?? throw new InvalidOperationException("the embedded B::PolypilerDump resource is missing");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private const string MooseCollectorSource = """
    package Moose;
    use strict;
    use warnings;

    sub _entry {
        my ($pkg, $kind) = @_;
        $B::PolypilerDump::MOOSE{$pkg} ||= {
            kind => $kind,
            extends => [],
            with => [],
            requires => [],
            attributes => [],
        };
        return $B::PolypilerDump::MOOSE{$pkg};
    }

    sub _install_stub {
        my ($pkg, $name) = @_;
        no strict 'refs';
        $B::PolypilerDump::GENERATED_SUBS{"${pkg}::$name"} = 1;
        *{"${pkg}::$name"} = sub { } unless defined &{"${pkg}::$name"};
    }

    sub _record_has {
        my ($pkg, $kind, $names, %spec) = @_;
        my $entry = _entry($pkg, $kind);
        my @list = ref($names) eq 'ARRAY' ? @$names : ($names);
        for my $name (@list) {
            my ($def_kind, $def_val) = ('none', '');
            if (exists $spec{default}) {
                my $d = ref($spec{default}) eq 'CODE' ? eval { $spec{default}->() } : $spec{default};
                if (!defined $d) {
                    $def_kind = 'undef';
                } elsif (ref($d) eq 'ARRAY') {
                    $def_kind = 'array';
                } elsif (ref($d) eq 'HASH') {
                    $def_kind = 'hash';
                } elsif ($d =~ /\A-?[0-9]+\z/) {
                    $def_kind = 'integer';
                    $def_val = "$d";
                } elsif ($d =~ /\A-?(?:[0-9]+\.[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z/) {
                    $def_kind = 'float';
                    $def_val = "$d";
                } else {
                    $def_kind = 'string';
                    $def_val = "$d";
                }
            }
            push @{$entry->{attributes}}, {
                name => $name,
                is => (defined $spec{is} ? $spec{is} : 'rw'),
                isa => (defined $spec{isa} ? $spec{isa} : ''),
                writer => (defined $spec{writer} ? $spec{writer} : ''),
                reader => (defined $spec{reader} ? $spec{reader} : ''),
                defaultKind => $def_kind,
                defaultValue => $def_val,
            };
            _install_stub($pkg, $name);
            _install_stub($pkg, $spec{writer}) if defined $spec{writer} && length($spec{writer});
            _install_stub($pkg, $spec{reader}) if defined $spec{reader} && length($spec{reader});
        }
    }

    sub import {
        my $caller = caller;
        strict->import;
        warnings->import;
        _entry($caller, 'class');
        no strict 'refs';
        for my $kw (qw(has extends with requires meta new)) {
            $B::PolypilerDump::GENERATED_SUBS{"${caller}::$kw"} = 1;
        }
        *{"${caller}::has"} = sub { _record_has($caller, 'class', @_); };
        *{"${caller}::extends"} = sub { push @{_entry($caller, 'class')->{extends}}, @_; };
        *{"${caller}::with"} = sub { push @{_entry($caller, 'class')->{with}}, @_; };
        *{"${caller}::requires"} = sub { push @{_entry($caller, 'class')->{requires}}, @_; };
        *{"${caller}::meta"} = sub { bless {}, 'Moose::_Meta'; };
        *{"${caller}::new"} = sub { bless {}, $_[0]; } unless defined &{"${caller}::new"};
    }

    package Moose::_Meta;
    sub make_immutable { return 1; }

    1;
    """;

  private const string MooseRoleCollectorSource = """
    package Moose::Role;
    use strict;
    use warnings;
    use Moose ();

    sub import {
        my $caller = caller;
        strict->import;
        warnings->import;
        Moose::_entry($caller, 'role');
        no strict 'refs';
        for my $kw (qw(has with requires)) {
            $B::PolypilerDump::GENERATED_SUBS{"${caller}::$kw"} = 1;
        }
        *{"${caller}::has"} = sub { Moose::_record_has($caller, 'role', @_); };
        *{"${caller}::with"} = sub { push @{Moose::_entry($caller, 'role')->{with}}, @_; };
        *{"${caller}::requires"} = sub { push @{Moose::_entry($caller, 'role')->{requires}}, @_; };
    }

    1;
    """;

}
