using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Polypiler.Evidence.Adr0004;

internal static class Checks {
  internal delegate int Runner(string[] args, TextWriter output, TextWriter error);

  // PRD §10, verbatim apart from line continuations.
  private static readonly string[] _prd = [
    "translate Cipher/algorithms/checksum/<algorithm>.js --from javascript --source-profile node-es2022 --to csharp --target-profile net9 --export <algorithm> --opt speed --verify --output generated/cipher-csharp",
    "translate path/to/algorithm.bas --from powerbasic --source-profile pb35 --to csharp --target-profile net9 --verify --output generated/pb-csharp",
    "translate path/to/Deinterlace.kt --from kotlin --to perl --target-profile perl5 --export <entry> --verify --output generated/deinterlace-perl",
    "translate path/to/native/ncnn_kernel.cpp --from cpp --to rust --strategy native-adapter --native-provider ncnn --verify --output generated/ncnn-rust",
    "translate BitPerm/mapping.json --from bitmapping --to rust --target-profile x86_64-v3 --verify --output generated/bitperm-rust",
    "recover source/to/algorithm.asm --from mos6502 --cpu mos6502 --entry 0x8000 --memory-map profiles/c64.json --to csharp --verify --output generated/6502-csharp",
    "translate algorithm.rs --from rust --to mos6502 --cpu mos6502 --target-profile c64 --verify-emulator vice --output generated/6502-prg",
    "translate PROGRAM.PRG --from c64-basic-v2 --to csharp --verify --output generated/c64-csharp",
    "translate source.dll --from msil --export Namespace.Checksum --to php --target-profile php8 --verify --output generated/msil-php",
    "translate Example.class --from jvm --to delphi --verify --output generated/jvm-delphi",
    "translate input.bc --from llvm --llvm-version pinned --to csharp --target-profile net9 --verify --output generated/llvm-csharp",
    "translate algorithm.cs --from csharp --to llvm --target-triple x86_64-unknown-linux-gnu --verify --output generated/algorithm.ll",
    "translate algorithm.cs --from csharp --to x86-16 --target-profile dos-real-mode --verify-emulator dosbox-x --output generated/algorithm.exe",
    "inspect path/to/source --from javascript --show types,ranges,effects,patterns,ir,proofs",
    "capabilities --from kotlin --to perl --source-profile jvm --target-profile perl5",
    "verify generated/cipher-csharp --against corpus/cipher/<algorithm>.json",
    "search \"Floyd-Steinberg\" --language rust",
    "generate color.dithering.floyd-steinberg --to rust --verify --output generated/dithering",
  ];

  // Each group must render one canonical line.
  private static readonly string[][] _equivalent = [
    [
      "translate a.cs --from csharp --to javascript --output out --opt speed --disable-opt O0100,O0001 --export B,A",
      "translate a.cs --to=javascript --from=csharp --output=out --disable-opt O0001 --disable-opt O0100 --disable-opt O0001 --opt=speed --export A --export B",
      "--quiet translate --export A,B --disable-opt O0001,O0100 --opt speed --output elsewhere --force --to javascript --from csharp a.cs",
      "translate --diagnostics json a.cs --from csharp --to javascript --output out --opt speed --disable-opt O0100 --disable-opt O0001 --export B --export A --quiet",
      "--diagnostics=json --quiet translate --from csharp --to javascript --opt speed --disable-opt O0001,O0100 --export A,B --output out -- a.cs",
    ],
    [
      "translate a.cs --from csharp --to javascript --output out",
      "translate a.cs --from csharp --to javascript --output out --opt balanced",
    ],
    [
      "inspect a.cs --from csharp --assume 'Add.b in [0, 15]' --assume 'Add.a in [0, 15]' --show ir,types",
      "inspect a.cs --show types --show ir --from csharp --assume 'Add.a in [0, 15]' --assume 'Add.b in [0, 15]' --format text",
    ],
    [
      "inspect a.cs --from csharp",
      "inspect a.cs --from csharp --show ir",
    ],
  ];

  // Must be refused with exit code 2, empty stdout and the text on stderr.
  private static readonly (string Line, string Expect)[] _refused = [
    ("frobnicate", "PLY0002"),
    ("translate a.cs --from csharp --to javascript --output out --nope", "unknown option '--nope'"),
    ("translate a.cs --from csharp --to javascript --output", "requires a value"),
    ("translate a.cs --from csharp --to javascript --output out --opt fastest", "is not a valid value for '--opt'"),
    ("translate a.cs --from csharp --to javascript --output out --opt speed --opt size", "given more than once"),
    ("translate a.cs --From csharp --to javascript --output out", "unknown option '--From'"),
    ("translate a.cs --to javascript --output out", "'--from' is required"),
    ("translate --from csharp --to javascript --output out", "missing argument <input>"),
    ("translate a.cs b.cs --from csharp --to javascript --output out", "unexpected argument 'b.cs'"),
    ("translate a.cs --from csharp --to javascript --output out --force=true", "does not take a value"),
    ("translate a.cs --from csharp --to javascript --output out --force true", "unexpected argument 'true'"),
    ("translate a.cs --from csharp --to javascript --output out --export A,,B", "empty list element"),
    ("translate a.cs --from csharp --to javascript --output --force", "requires a value"),
    ("translate a.cs --from csharp --to javascript --out out", "unknown option '--out'"),
    ("translate a.cs --from csharp --to javascript --output out -f", "unknown option '-f'"),
    ("translate a.cs --from csharp --to javascript --output:out", "unknown option"),
    ("translate a.cs --from csharp --to javascript --output out @args.rsp", "unexpected argument '@args.rsp'"),
    ("--opt speed translate a.cs --from csharp --to javascript --output out", "unknown option '--opt'"),
    ("search adler --opt speed", "unknown option '--opt'"),
    ("--diagnostics xml search adler", "is not a valid value for '--diagnostics'"),
    ("translate a.cs --from csharp --to javascript --output out /?", "unexpected argument '/?'"),
    ("-?", "unknown option '-?'"),
    ("--version extra", "unknown option '--version'"),
    ("--quiet", "no command given"),
    ("translate a.cs --from csharp --to javascript --output out --quiet --quiet", "given more than once"),
    ("translate a.cs --from csharp --to javascript --output out --export=", "empty list element"),
    ("translate a.cs --from csharp --to javascript --output out --export ''", "empty list element"),
  ];

  // Accepted, with the canonical line they must give.
  private static readonly (string Line, string Canonical)[] _accepted = [
    ("translate a.cs --from csharp --to javascript --output out --assume=", "translate a.cs --from csharp --to javascript --opt balanced --assume \"\""),
    ("translate a.cs --from csharp --to javascript --output out --assume ''", "translate a.cs --from csharp --to javascript --opt balanced --assume \"\""),
    ("translate a.cs --from csharp --to javascript --output out --assume=Sum.n:foo --vectors=C:/v.json", "translate a.cs --from csharp --to javascript --vectors C:/v.json --opt balanced --assume Sum.n:foo"),
    ("translate 'a $b `c` \"d\".cs' --from csharp --to javascript --output out", "translate \"a \\$b \\`c\\` \\\"d\\\".cs\" --from csharp --to javascript --opt balanced"),
    ("translate - --from csharp --to javascript --output out", "translate - --from csharp --to javascript --opt balanced"),
    ("search -- -.-", "search -.- --format text --limit 20"),
    ("search adler --limit=-1", "search adler --format text --limit -1"),
    ("translate --from csharp --to javascript --output out -- --weird", "translate --weird --from csharp --to javascript --opt balanced"),
    ("translate dir\\a.cs --from csharp --to javascript --output out", "translate dir/a.cs --from csharp --to javascript --opt balanced"),
    ("translate 'my file.cs' --from csharp --to javascript --output out", "translate \"my file.cs\" --from csharp --to javascript --opt balanced"),
  ];

  internal static string[] Sh(string line) {
    var result = new List<string>();
    var current = new StringBuilder();
    var quote = '\0';
    var open = false;
    foreach (var c in line) {
      if (quote != '\0') {
        if (c == quote) quote = '\0';
        else current.Append(c);
      } else if (c is '"' or '\'') {
        quote = c;
        open = true;
      } else if (c == ' ') {
        if (open || current.Length > 0) result.Add(current.ToString());
        current.Clear();
        open = false;
      } else
        current.Append(c);
    }
    if (open || current.Length > 0) result.Add(current.ToString());
    return result.ToArray();
  }

  private static (int Exit, string Out, string Err) Invoke(Runner run, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exit = run(args, output, error);
    return (exit, output.ToString(), error.ToString());
  }

  internal static int RunAll(Runner run, TextWriter log) {
    var failures = 0;
    void Check(string name, bool ok, string detail = "") {
      if (!ok) ++failures;
      log.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  -> " + detail.Replace("\n", " | "))}");
    }

    log.WriteLine("== existing seam tests (Polypiler.Tests/CliTests.cs) ==");
    foreach (var h in new[] { "--help", "-h", "help" }) {
      var r = Invoke(run, h);
      Check($"Help_PrintsProductNameAndSucceeds({h})", r.Exit == 0 && r.Out.StartsWith("polypiler "), r.Out + r.Err);
    }
    Check("NoArguments_BehavesLikeHelp", Invoke(run).Out == Invoke(run, "--help").Out);
    var v = Invoke(run, "--version");
    Check("Version_PrintsTheCoreVersion", v.Exit == 0 && v.Out.Trim() == Grammar.Version, v.Out + v.Err);
    var u = Invoke(run, "frobnicate");
    Check("UnknownCommand_FailsWithUsageError", u.Exit == 2 && u.Out.Length == 0 && u.Err.Contains("unknown command 'frobnicate'"), u.Out + u.Err);
    Check("NearMiss(--HELP)", Invoke(run, "--HELP").Exit == 2);
    Check("NearMiss(empty)", Invoke(run, "").Exit == 2);
    var originalOut = Console.Out;
    using (var captured = new StringWriter()) {
      Console.SetOut(captured);
      int exit;
      try { exit = run(["--version"], Console.Out, Console.Error); } finally { Console.SetOut(originalOut); }
      Check("Main_WritesToTheConsole", exit == 0 && captured.ToString().Trim() == Grammar.Version);
    }

    log.WriteLine("== PRD section 10 invocations ==");
    foreach (var line in _prd) {
      var r = Invoke(run, Sh(line));
      Check(line, r.Exit == 0 && r.Err.Length == 0, r.Err);
      if (r.Exit == 0) log.WriteLine("      = " + r.Out.Trim());
    }

    log.WriteLine("== equivalent invocations give one canonical line ==");
    foreach (var group in _equivalent) {
      var results = group.Select(l => Invoke(run, Sh(l))).ToArray();
      var ok = results.All(r => r.Exit == 0) && results.Select(r => r.Out).Distinct().Count() == 1;
      Check($"{group.Length} spellings -> {results[0].Out.Trim()}", ok, string.Join(" || ", results.Select(r => r.Out.Trim() + r.Err.Trim())));
    }

    log.WriteLine("== accepted edge cases ==");
    foreach (var (line, canonical) in _accepted) {
      var r = Invoke(run, Sh(line));
      Check(line, r.Exit == 0 && r.Out.Trim() == canonical, r.Out + r.Err);
    }

    log.WriteLine("== refused command lines ==");
    foreach (var (line, expect) in _refused) {
      var r = Invoke(run, Sh(line));
      Check($"{line}  [{expect}]", r.Exit == 2 && r.Out.Length == 0 && r.Err.Contains(expect), $"exit {r.Exit}: {r.Out}{r.Err}");
    }

    log.WriteLine("== help ==");
    var ch = Invoke(run, "translate", "--help");
    Check("translate --help", ch.Exit == 0 && ch.Out.Contains("--from") && ch.Err.Length == 0, ch.Out + ch.Err);
    var hc = Invoke(run, "help", "translate");
    Check("help translate == translate --help", hc.Exit == 0 && hc.Out == ch.Out, hc.Out + hc.Err);
    foreach (var line in new[] { "translate --nope --help", "--nope translate -h", "--diagnostics json translate a.cs --help", "translate --from --help" }) {
      var r = Invoke(run, Sh(line));
      Check($"{line} == translate --help", r.Exit == 0 && r.Out == ch.Out && r.Err.Length == 0, r.Out + r.Err);
    }
    var rh = Invoke(run, "--help");
    foreach (var line in new[] { "--nope --help", "--quiet -h" }) {
      var r = Invoke(run, Sh(line));
      Check($"{line} == --help", r.Exit == 0 && r.Out == rh.Out, r.Out + r.Err);
    }
    var uh = Invoke(run, "frobnicate", "--help");
    Check("frobnicate --help  [PLY0002]", uh.Exit == 2 && uh.Out.Length == 0 && uh.Err.Contains("PLY0002"), uh.Out + uh.Err);
    var nh = Invoke(run, "search", "--", "--help");
    Check("search -- --help searches for '--help'", nh.Exit == 0 && nh.Out.Trim() == "search --help --format text --limit 20", nh.Out + nh.Err);
    var hu = Invoke(run, "help", "frobnicate");
    Check("help frobnicate  [PLY0002]", hu.Exit == 2 && hu.Err.Contains("PLY0002"), hu.Out + hu.Err);

    log.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
    return failures == 0 ? 0 : 1;
  }
}
