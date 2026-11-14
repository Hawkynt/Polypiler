using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Cli;

/// <summary>The public types of the command-line foundation: the registry, the shared options, the option rules, the context and the path checks.</summary>
[TestFixture]
public sealed class CliTypesTests {

  private sealed class Named(string name) : ICliCommand {

    public string Name => name;

    public string Description => "";

    public void Configure(Command command) => command.Arguments.Add(new Argument<string[]>("files") { Arity = ArgumentArity.ZeroOrMore });

    public int Execute(ParseResult parse, CliContext context) => 0;

  }

  [Test]
  public void Registry_ListsItsCommandsSortedByName() {
    var registry = new CommandRegistry();
    registry.Add(new Named("zeta"));
    registry.Add(new Named("alpha-2"));
    registry.Add(new Named("beta"));
    Assert.That(registry.Commands.Select(command => command.Name), Is.EqualTo(new[] { "alpha-2", "beta", "zeta" }));
    Assert.That(registry.Find("beta")!.Name, Is.EqualTo("beta"));
    Assert.That(registry.Find("Beta"), Is.Null);
  }

  [TestCase("Translate")]
  [TestCase("two--hyphens")]
  [TestCase("-lead")]
  [TestCase("trail-")]
  [TestCase("9lives")]
  [TestCase("a_b")]
  [TestCase("")]
  [TestCase(null)]
  public void Registry_RefusesANameThatIsNoWord(string? name)
    => Assert.That(() => new CommandRegistry().Add(new Named(name!)), Throws.ArgumentException.With.Message.Contain("is not a command name"));

  [Test]
  public void Registry_RefusesHelpAndATwiceRegisteredName() {
    var registry = new CommandRegistry();
    Assert.That(() => registry.Add(new Named("help")), Throws.ArgumentException.With.Message.Contain("'help' is reserved"));
    registry.Add(new Named("echo"));
    Assert.That(() => registry.Add(new Named("echo")), Throws.ArgumentException.With.Message.Contain("registered already"));
    Assert.That(() => registry.Add(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Help_WritesAnOptionalRepeatableOperandInBrackets() {
    var registry = new CommandRegistry();
    registry.Add(new Named("files"));
    using var output = new StringWriter();
    Program.Run(["files", "--help"], output, TextWriter.Null, registry, ".");
    Assert.That(output.ToString(), Does.Contain("usage: polypiler files [<files>...] [options]"));
  }

  [Test]
  public void AnOptionalRepeatableOperand_TakesAnyNumber() {
    var registry = new CommandRegistry();
    registry.Add(new Named("files"));
    Assert.That(Program.Run(["files"], TextWriter.Null, TextWriter.Null, registry, "."), Is.Zero);
    Assert.That(Program.Run(["files", "a", "b", "c"], TextWriter.Null, TextWriter.Null, registry, "."), Is.Zero);
  }

  [Test]
  public void CommonOptions_HaveTheirNamesAndKinds() {
    Assert.That(new Option[] { CommonOptions.Output, CommonOptions.Force, CommonOptions.From, CommonOptions.To, CommonOptions.SourceProfile, CommonOptions.TargetProfile, CommonOptions.Export }
      .Select(option => (option.Name, option.ValueType)), Is.EqualTo(new[] {
        ("--output", typeof(string)), ("--force", typeof(bool)), ("--from", typeof(string)), ("--to", typeof(string)),
        ("--source-profile", typeof(string)), ("--target-profile", typeof(string)), ("--export", typeof(string[]))
      }));
    var first = CommonOptions.From;
    Assert.That(CommonOptions.From, Is.Not.SameAs(first), "a new option for every command");
  }

  [Test]
  public void OptionRules_RefuseNothingToAllowAndNull() {
    var option = new Option<string>("--mode");
    Assert.That(() => OptionRules.AcceptOnly(option), Throws.ArgumentException);
    Assert.That(() => OptionRules.AcceptOnly<Option<string>>(null!, "a"), Throws.ArgumentNullException);
    Assert.That(() => OptionRules.AcceptOnly(option, null!), Throws.ArgumentNullException);
    Assert.That(OptionRules.AcceptOnly(option, "a"), Is.SameAs(option));
    var command = new Command("c");
    Assert.That(() => OptionRules.Exclusive(null!, option, option), Throws.ArgumentNullException);
    Assert.That(() => OptionRules.Exclusive(command, null!, option), Throws.ArgumentNullException);
    Assert.That(() => OptionRules.Exclusive(command, option, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Context_RefusesNull() {
    Assert.That(() => new CliContext(null!, TextWriter.Null, "", DiagnosticsFormat.Text, false, "."), Throws.ArgumentNullException);
    Assert.That(() => new CliContext(TextWriter.Null, null!, "", DiagnosticsFormat.Text, false, "."), Throws.ArgumentNullException);
    Assert.That(() => new CliContext(TextWriter.Null, TextWriter.Null, null!, DiagnosticsFormat.Text, false, "."), Throws.ArgumentNullException);
    Assert.That(() => new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, null!), Throws.ArgumentNullException);
    Assert.That(() => new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, ".").Report(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Context_WithoutACommandLine_LeavesTheCommandOfADiagnosticAlone() {
    var context = new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, ".");
    context.Report(Diagnostic.Create(TestCommands.TestWarning, null) with { Command = "kept" });
    Assert.That(context.Diagnostics.Items.Single().Command, Is.EqualTo("kept"));
  }

  [TestCase(DiagnosticsFormat.Text, false, true)]
  [TestCase(DiagnosticsFormat.Text, true, false)]
  [TestCase(DiagnosticsFormat.Json, false, false)]
  public void Context_WritesANoteOnlyAsTextAndNotQuiet(DiagnosticsFormat format, bool quiet, bool written) {
    using var error = new StringWriter();
    new CliContext(TextWriter.Null, error, "", format, quiet, ".").Note("a note");
    Assert.That(error.ToString(), Is.EqualTo(written ? "a note" + Environment.NewLine : ""));
  }

  [Test]
  public void Paths_RefuseNull() {
    var context = new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, ".");
    Assert.That(() => CliPaths.RequireInput(null!, context), Throws.ArgumentNullException);
    Assert.That(() => CliPaths.RequireInput("a", null!), Throws.ArgumentNullException);
    Assert.That(() => CliPaths.PrepareOutputDirectory(null!, false, context), Throws.ArgumentNullException);
    Assert.That(() => CliPaths.PrepareOutputDirectory("a", false, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Paths_ResolveAgainstTheWorkingDirectory() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-paths-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      File.WriteAllText(Path.Combine(directory, "in.txt"), "x");
      var context = new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, directory);
      Assert.That(CliPaths.RequireInput("in.txt", context), Is.True);
      Assert.That(CliPaths.PrepareOutputDirectory("out", false, context), Is.True);
      Assert.That(Directory.Exists(Path.Combine(directory, "out")), Is.True);
      Assert.That(CliPaths.PrepareOutputDirectory("bad\0name", false, context), Is.False);
      Assert.That(context.Diagnostics.Items.Single().Code, Is.EqualTo("PLY0005"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

}
