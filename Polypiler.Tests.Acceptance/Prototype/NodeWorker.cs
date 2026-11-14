using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>A module, or the <see cref="NodeWorker"/> that ran it, failed; the message says how.</summary>
internal sealed class NodeFailure(string message) : Exception(message);

/// <summary>
/// One <c>node</c> that stays alive and runs module after module, so that a test pays for loading the
/// module and not for starting <c>node</c>.
/// <para>
/// The protocol is JSON lines. A request on standard input is one object: <c>module</c>, the URL of
/// an ES module or a CommonJS file; <c>calls</c>, the calls as <see cref="Toolchain.Codec"/> reads them;
/// <c>zero</c> and <c>mode</c> as there. The worker loads the module afresh - a new instance of an ES
/// module, a CommonJS file evaluated again - runs the calls and answers with one line on standard
/// output: the marker <see cref="Marker"/> and an object, <c>{ok: true, lines}</c> with one outcome
/// per call or <c>{ok: false, error}</c> with what the module threw while loading or running. Anything
/// else the module writes to standard output fails the request, as it fails the driver that prints
/// one outcome per line.
/// </para>
/// <para>
/// A worker that exits while running a request, answers with something that is not the protocol, or
/// does not answer within <see cref="Timeout"/> fails that request and is stopped; the next request
/// needs a new worker. The worker ends when its standard input does.
/// </para>
/// </summary>
internal sealed class NodeWorker : IDisposable {

  /// <summary>What precedes every answer on standard output.</summary>
  public const string Marker = "\u0001polypiler-worker\u0001";

  /// <summary>How long a request may take by default.</summary>
  public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

  private const string Script = """
    import { createInterface } from 'node:readline';
    import { createRequire } from 'node:module';
    import { join } from 'node:path';
    import { fileURLToPath } from 'node:url';

    """ + Toolchain.Codec + """

    const require = createRequire(join(process.cwd(), 'worker.mjs'));
    const answer = response => process.stdout.write('\u0001polypiler-worker\u0001' + JSON.stringify(response) + '\n');
    let serial = 0;
    for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
      try {
        const request = JSON.parse(line);
        delete require.cache[fileURLToPath(request.module)];
        const module = await import(request.module + '?run=' + ++serial);
        answer({ ok: true, lines: outcomes(module, request.calls, request.zero, request.mode) });
      } catch (error) {
        answer({ ok: false, error: String(error && error.stack || error) });
      }
    }
    """;

  private readonly Process _process;
  private readonly StringBuilder _errors = new();

  private NodeWorker(Process process, TimeSpan timeout) {
    this._process = process;
    this.Timeout = timeout;
  }

  /// <summary>How long a request may take before the worker is stopped.</summary>
  public TimeSpan Timeout { get; }

  /// <summary>Whether the worker can take another request.</summary>
  public bool IsAlive => !this._process.HasExited;

  /// <summary>Starts a worker; fails the test when there is no <c>node</c>.</summary>
  /// <param name="timeout">How long a request may take; <see cref="DefaultTimeout"/> when not given.</param>
  public static NodeWorker Start(TimeSpan? timeout = null) {
    var start = new ProcessStartInfo("node") {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      StandardInputEncoding = new UTF8Encoding(false),
      StandardOutputEncoding = Encoding.UTF8
    };
    foreach (var argument in new[] { "--input-type=module", "--eval", Script })
      start.ArgumentList.Add(argument);

    Process process;
    try {
      process = Process.Start(start)!;
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to run the generated JavaScript but could not be started: {exception.Message}");
      throw;
    }

    var worker = new NodeWorker(process, timeout ?? DefaultTimeout);
    process.ErrorDataReceived += (_, line) => {
      if (line.Data is not null)
        lock (worker._errors)
          worker._errors.AppendLine(line.Data);
    };
    process.BeginErrorReadLine();
    return worker;
  }

  /// <summary>Runs calls through a module and returns one outcome per call.</summary>
  /// <param name="moduleUrl">The <c>file:</c> URL of the module.</param>
  /// <param name="calls">The calls as the codec reads them.</param>
  /// <param name="zero"><c>exact</c> to tell -0 from 0, <c>integer</c> not to.</param>
  /// <param name="mode"><c>native</c>, <c>halves</c> or <c>plain</c>.</param>
  /// <exception cref="NodeFailure">
  /// The module threw something other than a <c>RangeError</c> or wrote to standard output, or the
  /// worker exited, answered outside the protocol or did not answer in time; in the last three cases
  /// it is stopped.
  /// </exception>
  public IReadOnlyList<string> Outcomes(string moduleUrl, object?[][] calls, string zero, string mode) {
    if (!this.IsAlive)
      throw new NodeFailure("the node worker has stopped");

    try {
      this._process.StandardInput.WriteLine(JsonSerializer.Serialize(new { module = moduleUrl, calls, zero, mode }));
      this._process.StandardInput.Flush();
    } catch (IOException) {
      throw new NodeFailure($"the node worker exited with code {this.ExitCode()}: {this.Errors()}");
    }

    var stray = new StringBuilder();
    var deadline = Stopwatch.StartNew();
    while (true) {
      var reading = this._process.StandardOutput.ReadLineAsync();
      var remaining = this.Timeout - deadline.Elapsed;
      if (remaining <= TimeSpan.Zero || !reading.Wait(remaining)) {
        this.Stop();
        throw new NodeFailure($"the node worker did not answer within {this.Timeout.TotalSeconds:0.###} s and was stopped");
      }

      var line = reading.Result;
      if (line is null) {
        throw new NodeFailure($"the node worker exited with code {this.ExitCode()}: {this.Errors()}");
      }

      var marker = line.IndexOf(Marker, StringComparison.Ordinal);
      if (marker < 0) {
        stray.AppendLine(line);
        continue;
      }

      stray.Append(line[..marker]);
      var response = Parse(line[(marker + Marker.Length)..]);
      if (stray.Length > 0)
        throw new NodeFailure($"the module wrote to standard output: {stray}");

      if (!response.Ok)
        throw new NodeFailure(response.Error ?? "the module failed without saying why");

      return response.Lines!;
    }
  }

  /// <summary>Stops the worker.</summary>
  public void Dispose() {
    this.Stop();
    this._process.Dispose();
  }

  private Response Parse(string json) {
    try {
      var response = JsonSerializer.Deserialize<Response>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
      if (response is { Ok: true, Lines: not null } or { Ok: false })
        return response;
    } catch (JsonException) {
    }

    this.Stop();
    throw new NodeFailure($"the node worker answered outside the protocol and was stopped: {json}");
  }

  private void Stop() {
    try {
      if (!this._process.HasExited) {
        this._process.Kill(true);
        this._process.WaitForExit();
      }
    } catch (InvalidOperationException) {
      // The process ended between the question and the kill.
    }
  }

  private int ExitCode() {
    this._process.WaitForExit();
    return this._process.ExitCode;
  }

  private string Errors() {
    this._process.WaitForExit();
    lock (this._errors)
      return this._errors.ToString().Trim();
  }

  private sealed record Response(bool Ok, string[]? Lines, string? Error);

}
