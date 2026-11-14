using System;
using System.CommandLine;
using System.IO;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tests.Cli.Support;

/// <summary>
/// <c>raise [&lt;code&gt;...] [--throw &lt;type&gt;[:&lt;message&gt;]] [--return &lt;n&gt;]</c>, the test-only
/// command of the exit-code scenarios (issue #23): reports the diagnostics named on its command line,
/// in order, each with the severity, exit code, message and rule <c>spec/diagnostics.yaml</c>
/// registers for it, so that a code whose owning issue is still open can be raised; then throws a
/// <c>System</c> exception of the given type, or returns the given number. It prints nothing.
/// </summary>
/// <param name="registry">The registry the codes are looked up in.</param>
/// <param name="extra">Descriptors the registry does not hold, such as <see cref="TestCommands.TestInfo"/>; looked up first.</param>
public sealed class RaiseCommand(DiagnosticRegistry registry, params DiagnosticDescriptor[] extra) : ICliCommand {

  private static readonly Lazy<DiagnosticRegistry> _repositoryRegistry = new(()
    => DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, DiagnosticRegistry.RepositoryPath)));

  /// <summary>The registry of the repository, <c>spec/diagnostics.yaml</c>, read once.</summary>
  public static DiagnosticRegistry RepositoryRegistry => _repositoryRegistry.Value;

  /// <summary>
  /// A registry with <c>raise</c> over the repository's registry and <see cref="TestCommands.TestInfo"/>,
  /// beside the <c>echo</c> and <c>demand</c> of <see cref="TestCommands"/>, whose <c>--left</c> and
  /// <c>--right</c> are a pair of exclusive options.
  /// </summary>
  public static CommandRegistry Registry() {
    var (commands, _, _) = TestCommands.Registry();
    commands.Add(new RaiseCommand(RepositoryRegistry, TestCommands.TestInfo));
    return commands;
  }

  /// <inheritdoc/>
  public string Name => "raise";

  /// <inheritdoc/>
  public string Description => "report the diagnostics named, then throw or return";

  /// <inheritdoc/>
  public void Configure(Command command) {
    command.Arguments.Add(new Argument<string[]>("code") { Description = "a registered code", Arity = ArgumentArity.ZeroOrMore });
    command.Options.Add(new Option<string>("--throw") { Description = "throw a System exception of this type, with the message after ':'", HelpName = "type[:message]" });
    command.Options.Add(new Option<int>("--return") { Description = "return this number", HelpName = "n" });
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    foreach (var code in parse.GetValue<string[]>("code") ?? [])
      context.Report(this.Diagnose(code));

    if (parse.GetValue<string>("--throw") is { } thrown)
      throw Thrown(thrown);

    return parse.GetValue<int>("--return");
  }

  /// <summary>The diagnostic of a code with the message its registry records.</summary>
  /// <exception cref="ArgumentException">Neither <paramref name="extra"/> nor the registry knows the code.</exception>
  private Diagnostic Diagnose(string code) {
    foreach (var descriptor in extra)
      if (descriptor.Code == code)
        return Diagnostic.Create(descriptor, null);

    var entry = registry.Find(code) ?? throw new ArgumentException($"'{code}' is not registered", nameof(code));
    return new Diagnostic(entry.Code, entry.Severity, entry.Message) { ExitCode = entry.ExitCode, Rule = entry.Rule };
  }

  /// <summary>An exception of a <c>System</c> type named <c>&lt;type&gt;[:&lt;message&gt;]</c>.</summary>
  private static Exception Thrown(string text) {
    var colon = text.IndexOf(':');
    var name = colon < 0 ? text : text[..colon];
    var type = typeof(Exception).Assembly.GetType("System." + name, throwOnError: true)!;
    return colon < 0 ? (Exception)Activator.CreateInstance(type)! : (Exception)Activator.CreateInstance(type, text[(colon + 1)..])!;
  }

}
