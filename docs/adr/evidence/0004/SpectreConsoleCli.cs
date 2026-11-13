using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Polypiler.Evidence.Adr0004;

public sealed class TranslateSettings : CommandSettings {
  [CommandArgument(0, "<input>")] public string Input { get; set; } = "";
  [CommandOption("--from <LANGUAGE>")] public string? From { get; set; }
  [CommandOption("--to <LANGUAGE>")] public string? To { get; set; }
  [CommandOption("--source-profile <ID>")] public string? SourceProfile { get; set; }
  [CommandOption("--target-profile <ID>")] public string? TargetProfile { get; set; }
  [CommandOption("--export <NAME>")] public string[] Export { get; set; } = [];
  [CommandOption("--output <DIRECTORY>")] public string? Output { get; set; }
  [CommandOption("--opt <LEVEL>")] [DefaultValue("balanced")] public string Opt { get; set; } = "balanced";
  [CommandOption("--verify")] public bool Verify { get; set; }
  [CommandOption("--force")] public bool Force { get; set; }
}

public sealed class TranslateCommand : Command<TranslateSettings> {
  public override int Execute(CommandContext context, TranslateSettings settings, CancellationToken cancellationToken) {
    Program.Output!.WriteLine($"translate {settings.Input} --from {settings.From} --to {settings.To} --export {string.Join(',', settings.Export)} --output {settings.Output} --opt {settings.Opt} verify={settings.Verify} force={settings.Force}");
    return 0;
  }
}

public static class Program {
  internal static TextWriter? Output;

  public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

  public static int Run(string[] args, TextWriter output, TextWriter error) {
    Output = output;
    var app = new CommandApp();
    app.Configure(config => {
      config.SetApplicationName("polypiler");
      config.SetApplicationVersion("0.1.0");
      config.Settings.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });
      config.AddCommand<TranslateCommand>("translate");
    });
    return app.Run(args);
  }
}
