using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares a compiled C# kernel with the DOS batch file produced by Polypiler.</summary>
[Binding, Scope(Feature = "Emit MS-DOS 6.22 scalar Batch")]
public sealed class DosBatchBackendSteps {

  private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory();
  private string _source = "";
  private string _errors = "";
  private string _trace = "";
  private int _status;

  private string Output => Path.Combine(this._work.FullName, "out");

  [Given("a C# kernel adds one to an initialized Int32.MaxValue local")]
  public void UncheckedAddition() => this._source = File.ReadAllText(DosCommandOracle.Fixture("DosWrap.cs"));

  [Given("""^the closed C# DOS kernel named "([^"]+)"$""")]
  public void Kernel(string name) {
    var body = name switch {
      "ordinary addition" => "int x = 18; int y = x; return y + 24;",
      "carry out" => "int x = -1; x = unchecked(x + 1); return x;",
      "mixed signs" => "int x = -46; x = 2 + x; return x;",
      "nested operands" => "int x = 1; int y = 255; return (x + 16) + (y + 1);",
      "loops and branches" => """
        int n = 0, sum = 0;
        for (int i = 0; i != 5; i = i + 1) {
          if (i == 1) continue;
          if (i == 4) break;
          sum = sum + i;
        }
        do { n = n + 1; } while (n != 2);
        while (n != 3) n = n + 1;
        if (sum == 5) return sum + n;
        else return 42;
        """,
      "unequal branch" => "int x = -1; if (x == 1) return x; else return 42;",
      _ => throw new ArgumentException(name, nameof(name))
    };
    this._source = $"public static class Kernel {{ public static int Run() {{ {body} }} }}";
  }

  [Given("the corresponding C# kernel uses checked addition")]
  public void CheckedAddition() {
    this.UncheckedAddition();
    this._source = this._source.Replace("unchecked", "checked", StringComparison.Ordinal);
  }

  [When("it is translated to dos622")]
  public void Translate() {
    var input = Path.Combine(this._work.FullName, "Kernel.cs");
    File.WriteAllText(input, this._source);
    using var errors = new StringWriter();
    this._status = Program.Run(["translate", input, "--from", "csharp", "--to", "batch", "--profile", "dos622",
      "--opt", "none", "--output", this.Output], TextWriter.Null, errors);
    this._errors = errors.ToString();
  }

  [When("it is translated to dos622 and run by the pinned COMMAND.COM")]
  public void TranslateAndRun() {
    this.Translate();
    Assert.That(this._status, Is.Zero, this._errors);
    var generated = Directory.EnumerateFiles(this.Output, "*.bat").Single();
    var result = DosCommandOracleTests.Guest().Run(DosCommandOracle.Fixture("EMITDRV.BAT"),
      [new(generated, "KERNEL.BAT")], TimeSpan.FromSeconds(20));
    this._trace = result.Trace;
    TestContext.Out.Write(this._trace);
    Assert.That(result.TimedOut, Is.False);
  }

  [Then("^the DOS result record is PLYRESULT=([0-9A-F]{8})$")]
  public void ExactRecord(string bits) => Assert.That(this._trace, Is.EqualTo($"PLYRESULT={bits}\r\nCALLER=CALLER\r\nPLY_DONE\r\n"));

  [Then("interpreting those bits as Int32 yields the compiled C# return value")]
  public void MatchesCSharp() {
    var expected = GeneratedCSharp.Compile(this._source, checkOverflow: true).Method("Run").Invoke(null, null);
    Assert.That(int.Parse(this._trace.AsSpan("PLYRESULT=".Length, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
      Is.EqualTo(expected));
  }

  [Then("it reports an unsupported operation and writes no batch file")]
  public void Refusal() {
    Assert.That(this._status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), this._errors);
    Assert.That(this._errors, Does.Contain("PLY2851").And.Contain("AddChecked"));
    Assert.That(Directory.Exists(this.Output), Is.False);
  }

  [AfterScenario]
  public void CleanUp() => this._work.Delete(recursive: true);

}
