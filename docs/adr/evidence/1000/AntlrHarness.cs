using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;

static class Program {
  sealed class CountingListener : BaseErrorListener, IAntlrErrorListener<int> {
    public int Errors;
    public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) => Errors++;
    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) => Errors++;
  }

  static Func<byte[], bool> HandParser() {
    var asm = typeof(Polypiler.Core.Frontends.Go.GoFrontend).Assembly;
    var source = asm.GetType("Polypiler.Core.Frontends.Go.Syntax.GoSource", true);
    var parser = asm.GetType("Polypiler.Core.Frontends.Go.Syntax.GoParser", true);
    var ctor = source.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(byte[])]);
    var parse = parser.GetMethod("ParseFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    return bytes => {
      try { parse.Invoke(null, [ctor.Invoke([bytes])]); return true; } catch (TargetInvocationException) { return false; }
    };
  }

  static bool Antlr(string text, bool sllOnly, out bool usedLl) {
    usedLl = false;
    var lexer = new GoLexer(new AntlrInputStream(text));
    lexer.RemoveErrorListeners();
    var lexErrors = new CountingListener();
    lexer.AddErrorListener(lexErrors);
    var tokens = new CommonTokenStream(lexer);
    var parser = new GoParser(tokens);
    parser.RemoveErrorListeners();
    parser.Interpreter.PredictionMode = PredictionMode.SLL;
    parser.ErrorHandler = new BailErrorStrategy();
    try {
      parser.sourceFile();
      return lexErrors.Errors == 0;
    } catch (ParseCanceledException) {
      if (sllOnly) return false;
    }
    usedLl = true;
    tokens.Seek(0);
    parser.Reset();
    var errors = new CountingListener();
    parser.AddErrorListener(errors);
    parser.ErrorHandler = new DefaultErrorStrategy();
    parser.Interpreter.PredictionMode = PredictionMode.LL;
    parser.sourceFile();
    return errors.Errors == 0 && lexErrors.Errors == 0;
  }

  static void Main(string[] args) {
    var root = args[0];
    var files = Directory.EnumerateFiles(root, "*.go", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
    var bytes = files.Select(File.ReadAllBytes).ToList();
    var texts = bytes.Select(b => System.Text.Encoding.UTF8.GetString(b)).ToList();
    long total = bytes.Sum(b => (long)b.Length);
    Console.WriteLine($"corpus {root}: {files.Count} files, {total / 1024.0 / 1024.0:F1} MiB");
    var hand = HandParser();

    var handOk = new bool[files.Count];
    var antlrOk = new bool[files.Count];
    var ll = 0;
    const int rounds = 3;
    for (var round = 0; round <= rounds; ++round) {
      GC.Collect(); GC.WaitForPendingFinalizers();
      var sw = Stopwatch.StartNew();
      for (var i = 0; i < files.Count; ++i) handOk[i] = hand(bytes[i]);
      var h = sw.Elapsed.TotalMilliseconds;
      GC.Collect(); GC.WaitForPendingFinalizers();
      sw.Restart();
      ll = 0;
      for (var i = 0; i < files.Count; ++i) {
        antlrOk[i] = Antlr(texts[i], false, out var usedLl);
        if (usedLl) ++ll;
      }
      var a = sw.Elapsed.TotalMilliseconds;
      Console.WriteLine($"{(round == 0 ? "cold" : "warm " + round)}: hand {h:F0} ms ({total / 1048576.0 / (h / 1000):F1} MiB/s), antlr {a:F0} ms ({total / 1048576.0 / (a / 1000):F1} MiB/s), ratio {a / h:F1}x, LL fallbacks {ll}");
    }
    var both = Enumerable.Range(0, files.Count).Count(i => handOk[i] && antlrOk[i]);
    var neither = Enumerable.Range(0, files.Count).Count(i => !handOk[i] && !antlrOk[i]);
    var onlyHand = Enumerable.Range(0, files.Count).Where(i => handOk[i] && !antlrOk[i]).ToList();
    var onlyAntlr = Enumerable.Range(0, files.Count).Where(i => !handOk[i] && antlrOk[i]).ToList();
    Console.WriteLine($"verdicts: both accept {both}, both refuse {neither}, only hand accepts {onlyHand.Count}, only antlr accepts {onlyAntlr.Count}");
    foreach (var i in onlyHand.Take(12)) Console.WriteLine($"  antlr refuses: {Path.GetRelativePath(root, files[i])}");
    foreach (var i in onlyAntlr.Take(12)) Console.WriteLine($"  antlr accepts, hand refuses: {Path.GetRelativePath(root, files[i])}");

    // single largest files, per-file latency
    foreach (var i in Enumerable.Range(0, files.Count).OrderByDescending(i => bytes[i].Length).Take(3)) {
      var sw = Stopwatch.StartNew();
      for (var k = 0; k < 5; ++k) hand(bytes[i]);
      var h = sw.Elapsed.TotalMilliseconds / 5;
      sw.Restart();
      for (var k = 0; k < 5; ++k) Antlr(texts[i], false, out _);
      var a = sw.Elapsed.TotalMilliseconds / 5;
      Console.WriteLine($"largest {Path.GetRelativePath(root, files[i])} {bytes[i].Length / 1024} KiB: hand {h:F1} ms, antlr {a:F1} ms");
    }
  }
}
