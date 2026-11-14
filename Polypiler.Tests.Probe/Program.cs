using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Polypiler.TestProbe;

/// <summary>
/// The probe of the runner tests: one verb per behaviour a sandbox has to allow or stop. A verb that
/// tries something the sandbox should prevent exits with 1 when it is prevented and 0 when it is not;
/// a command line it does not understand exits with 2.
/// <list type="bullet">
///   <item><c>echo</c> copies standard input to standard output, byte for byte.</item>
///   <item><c>sleep &lt;ms&gt;</c> waits.</item>
///   <item><c>alloc &lt;MiB&gt;</c> allocates and touches that much memory, then holds it for 10 s.</item>
///   <item><c>spew &lt;bytes&gt; [stderr]</c> writes that many bytes to standard output, or to standard error.</item>
///   <item><c>connect &lt;host&gt; &lt;port&gt;</c> opens a TCP connection.</item>
///   <item><c>write &lt;path&gt;</c> creates a file.</item>
///   <item><c>spawn &lt;n&gt; [detach]</c> starts n copies of itself that sleep 1 s and waits for them, exiting
///   with 1 when one cannot start or fails; with <c>detach</c> the copies sleep 60 s and the probe exits at once.</item>
///   <item><c>exit &lt;code&gt;</c> exits with that code.</item>
/// </list>
/// </summary>
internal static class Program {

  private const int PageSize = 4096;
  private const int Mebibyte = 1 << 20;

  private static int Main(string[] args) {
    if (args.Length == 0)
      return Usage();

    return args[0] switch {
      "echo" when args.Length == 1 => Echo(),
      "sleep" when args.Length == 2 => Sleep(Number(args[1])),
      "alloc" when args.Length == 2 => Allocate(Number(args[1])),
      "spew" when args.Length == 2 => Spew(Number(args[1]), Console.OpenStandardOutput()),
      "spew" when args.Length == 3 && args[2] == "stderr" => Spew(Number(args[1]), Console.OpenStandardError()),
      "connect" when args.Length == 3 => Connect(args[1], (int)Number(args[2])),
      "write" when args.Length == 2 => Write(args[1]),
      "spawn" when args.Length == 2 => Spawn((int)Number(args[1]), false),
      "spawn" when args.Length == 3 && args[2] == "detach" => Spawn((int)Number(args[1]), true),
      "exit" when args.Length == 2 => (int)Number(args[1]),
      _ => Usage()
    };
  }

  private static int Usage() {
    Console.Error.WriteLine("usage: polypiler-probe echo | sleep <ms> | alloc <MiB> | spew <bytes> [stderr] | connect <host> <port> | write <path> | spawn <n> [detach] | exit <code>");
    return 2;
  }

  private static long Number(string text) => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

  private static int Echo() {
    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    input.CopyTo(output);
    return 0;
  }

  private static int Sleep(long milliseconds) {
    Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds));
    return 0;
  }

  private static int Allocate(long mebibytes) {
    var blocks = new List<byte[]>();
    for (var index = 0L; index < mebibytes; ++index) {
      var block = new byte[Mebibyte];
      for (var offset = 0; offset < block.Length; offset += PageSize)
        block[offset] = 1;
      blocks.Add(block);
    }

    Thread.Sleep(TimeSpan.FromSeconds(10));
    GC.KeepAlive(blocks);
    return 0;
  }

  private static int Spew(long count, Stream stream) {
    using (stream) {
      var chunk = new byte[64 * 1024];
      Array.Fill(chunk, (byte)'x');
      for (var left = count; left > 0; left -= chunk.Length)
        stream.Write(chunk, 0, (int)Math.Min(left, chunk.Length));
    }

    return 0;
  }

  private static int Connect(string host, int port) {
    try {
      using var client = new TcpClient();
      if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)))
        return 1;

      Console.Out.Write("connected\n");
      return 0;
    } catch (Exception exception) when (exception is SocketException or AggregateException) {
      Console.Error.WriteLine(exception.GetBaseException().Message);
      return 1;
    }
  }

  private static int Write(string path) {
    try {
      File.WriteAllText(path, "written by the probe\n");
      return 0;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static int Spawn(int count, bool detach) {
    var children = new List<Process>();
    var failed = false;
    for (var index = 0; index < count; ++index) {
      var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
      info.ArgumentList.Add(typeof(Program).Assembly.Location);
      info.ArgumentList.Add("sleep");
      info.ArgumentList.Add(detach ? "60000" : "1000");
      try {
        children.Add(Process.Start(info)!);
      } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) {
        Console.Error.WriteLine(exception.Message);
        failed = true;
      }
    }

    if (detach)
      return failed ? 1 : 0;

    foreach (var child in children) {
      child.WaitForExit();
      failed |= child.ExitCode != 0;
    }

    return failed ? 1 : 0;
  }

}
