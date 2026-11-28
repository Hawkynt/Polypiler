using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Polypiler.Core.Frontends.Go.Syntax;

static class Program {
  static Func<byte[], bool> HandParser() {
    var asm = typeof(Polypiler.Core.Frontends.Go.GoFrontend).Assembly;
    var source = asm.GetType("Polypiler.Core.Frontends.Go.Syntax.GoSource", true)!;
    var parser = asm.GetType("Polypiler.Core.Frontends.Go.Syntax.GoParser", true)!;
    var ctor = source.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(byte[])])!;
    var parse = parser.GetMethod("ParseFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    return bytes => {
      try { parse.Invoke(null, [ctor.Invoke([bytes])]); return true; } catch (TargetInvocationException) { return false; }
    };
  }

  static bool Peg(byte[] bytes) {
    try { return new GoPegParser(new GoSource(bytes)).Accepts(); } catch (GoSyntaxException) { return false; }
  }

  static void Run(string[] args) {
    if (args[0] == "-d") {
      foreach (var f in args.Skip(1)) {
        var b = File.ReadAllBytes(f); var src = new GoSource(b); var pp = new GoPegParser(src);
        var ok = pp.Accepts(); var tok = pp.At(pp.Farthest); var (line, col) = src.Position(tok.Offset);
        Console.WriteLine($"{f}: {ok} farthest {line}:{col} {tok.Kind} '{tok.Literal}'");
      }
      return;
    }
    var root = args[0];
    var rounds = args.Length > 1 ? int.Parse(args[1]) : 3;
    var files = Directory.EnumerateFiles(root, "*.go", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
    var bytes = files.Select(File.ReadAllBytes).ToList();
    long total = bytes.Sum(b => (long)b.Length);
    Console.WriteLine($"corpus {root}: {files.Count} files, {total / 1048576.0:F1} MiB");
    var hand = HandParser();
    var handOk = new bool[files.Count];
    var pegOk = new bool[files.Count];
    for (var round = 0; round <= rounds; ++round) {
      GC.Collect(); GC.WaitForPendingFinalizers();
      var h0 = GC.GetTotalAllocatedBytes(true);
      var sw = Stopwatch.StartNew();
      for (var i = 0; i < files.Count; ++i) handOk[i] = hand(bytes[i]);
      var h = sw.Elapsed.TotalMilliseconds;
      var halloc = (GC.GetTotalAllocatedBytes(true) - h0) / 1048576.0;
      GC.Collect(); GC.WaitForPendingFinalizers();
      var alloc0 = GC.GetTotalAllocatedBytes(true);
      sw.Restart();
      for (var i = 0; i < files.Count; ++i) pegOk[i] = Peg(bytes[i]);
      var a = sw.Elapsed.TotalMilliseconds;
      var alloc = (GC.GetTotalAllocatedBytes(true) - alloc0) / 1048576.0;
      Console.WriteLine($"{(round == 0 ? "cold" : "warm " + round)}: hand {h:F0} ms ({total / 1048576.0 / (h / 1000):F1} MiB/s, {halloc:F0} MiB alloc), peg {a:F0} ms ({total / 1048576.0 / (a / 1000):F1} MiB/s, {alloc:F0} MiB alloc), ratio {a / h:F2}x");
    }
    var idx = Enumerable.Range(0, files.Count).ToList();
    Console.WriteLine($"verdicts: both accept {idx.Count(i => handOk[i] && pegOk[i])}, both refuse {idx.Count(i => !handOk[i] && !pegOk[i])}, only hand accepts {idx.Count(i => handOk[i] && !pegOk[i])}, only peg accepts {idx.Count(i => !handOk[i] && pegOk[i])}");
    foreach (var i in idx.Where(i => handOk[i] != pegOk[i]).Take(60))
      Console.WriteLine($"  {(handOk[i] ? "peg refuses" : "peg accepts")}: {Path.GetRelativePath(root, files[i])}");
  }

  static void Main(string[] args) {
    var t = new Thread(() => Run(args), 256 * 1024 * 1024);
    t.Start();
    t.Join();
  }
}
