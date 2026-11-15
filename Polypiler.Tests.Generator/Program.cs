using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Polypiler.TestGenerator;

/// <summary>
/// The generator of the rebuild-check tests. Every verb takes the output directory as its last argument
/// and creates it; a command line it does not understand exits with 2.
/// <list type="bullet">
///   <item><c>stable</c> writes <c>a.txt</c> (<c>hello\n</c>) and an empty <c>b/c.txt</c>.</item>
///   <item><c>timestamp</c> also writes <c>stamp.txt</c> with the current tick count.</item>
///   <item><c>unordered</c> also writes ten keys to <c>keys.txt</c>, in an order taken from fresh GUIDs.</item>
///   <item><c>extra</c> also writes a file named after a fresh GUID.</item>
///   <item><c>manifest-volatile</c> also writes a <c>polypiler.manifest.json</c> whose <c>/verification/durationMs</c> is the tick count.</item>
///   <item><c>manifest-drift</c> also writes a <c>polypiler.manifest.json</c> whose <c>/outputs/0/sha256</c> is fresh.</item>
///   <item><c>empty</c> creates the directory and nothing in it.</item>
///   <item><c>sleep</c> creates the directory and waits a minute.</item>
/// </list>
/// </summary>
internal static class Program {

  private const string HelloSha256 = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";

  private static int Main(string[] args) {
    if (args.Length != 2)
      return Usage();

    var output = args[1];
    Directory.CreateDirectory(output);
    switch (args[0]) {
      case "stable":
        Stable(output);
        return 0;
      case "timestamp":
        Stable(output);
        Write(output, "stamp.txt", Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return 0;
      case "unordered":
        Stable(output);
        Write(output, "keys.txt", string.Concat(Enumerable.Range(0, 10).OrderBy(_ => Guid.NewGuid()).Select(key => $"key{key}\n")));
        return 0;
      case "extra":
        Stable(output);
        Write(output, $"{Guid.NewGuid():N}.txt", "extra\n");
        return 0;
      case "manifest-volatile":
        Stable(output);
        Write(output, "polypiler.manifest.json", Manifest(HelloSha256, Stopwatch.GetTimestamp()));
        return 0;
      case "manifest-drift":
        Stable(output);
        Write(output, "polypiler.manifest.json", Manifest(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()), 0));
        return 0;
      case "empty":
        return 0;
      case "sleep":
        Thread.Sleep(TimeSpan.FromMinutes(1));
        return 0;
      default:
        return Usage();
    }
  }

  private static int Usage() {
    Console.Error.WriteLine("usage: polypiler-generator stable | timestamp | unordered | extra | manifest-volatile | manifest-drift | empty | sleep <output directory>");
    return 2;
  }

  private static void Stable(string output) {
    Write(output, "a.txt", "hello\n");
    Write(output, "b/c.txt", "");
  }

  private static string Manifest(string sha256, long durationMs)
    => $$$"""{"schemaVersion":1,"outputs":[{"path":"a.txt","sha256":"{{{sha256}}}","role":"source"}],"verification":{"oracle":"generator","durationMs":{{{durationMs}}}}}""" + "\n";

  private static void Write(string output, string name, string content) {
    var path = Path.Combine(output, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
  }

}
