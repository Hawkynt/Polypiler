using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// Reads a manifest, all or nothing: the text must be JSON, pass the schema (<c>PLY0053</c> per error,
/// with its JSON pointer) and claim exactly the state its evidence supports (<c>PLY0054</c> for a
/// higher one, <c>PLY0053</c> at <c>/state</c> for a lower one). A manifest that fails any of these is
/// never repaired or partially loaded. Re-writing a manifest that was read gives its bytes back when
/// they were canonical.
/// </summary>
public static class ManifestReader {

  private static readonly JsonDocumentOptions _strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };

  /// <summary>Reads a manifest file and checks it against <see cref="ManifestSchema.Default"/>.</summary>
  /// <param name="path">The file; the diagnostics name it as given.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <returns>The manifest, or <see langword="null"/> when it is invalid.</returns>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static Manifest? Read(string path, IDiagnosticSink sink) => Read(path, sink, ManifestSchema.Default);

  /// <summary>Reads a manifest file and checks it against a schema.</summary>
  /// <param name="path">The file; the diagnostics name it as given.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <param name="schema">The schema.</param>
  /// <returns>The manifest, or <see langword="null"/> when it is invalid.</returns>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static Manifest? Read(string path, IDiagnosticSink sink, ManifestSchema schema) {
    ArgumentNullException.ThrowIfNull(path);
    return Parse(File.ReadAllText(path, Encoding.UTF8), path, sink, schema);
  }

  /// <summary>Reads a manifest from its text.</summary>
  /// <param name="text">The text.</param>
  /// <param name="path">The name the diagnostics give the manifest.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <param name="schema">The schema.</param>
  /// <returns>The manifest, or <see langword="null"/> when it is invalid.</returns>
  public static Manifest? Parse(string text, string path, IDiagnosticSink sink, ManifestSchema schema) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(sink);
    ArgumentNullException.ThrowIfNull(schema);
    JsonNode root;
    IReadOnlyList<(string Pointer, string Reason)> errors;
    try {
      using var document = JsonDocument.Parse(text, _strict);
      errors = schema.Evaluate(document.RootElement);
      root = JsonNode.Parse(text, documentOptions: _strict)!;
    } catch (JsonException exception) {
      var line = (int)(exception.LineNumber ?? 0) + 1;
      var column = (int)(exception.BytePositionInLine ?? 0) + 1;
      sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, path, "/", $"not JSON: {exception.Message}"), path, line, column));
      return null;
    }

    foreach (var (pointer, reason) in errors)
      sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, path, pointer, reason), path));
    if (errors.Count > 0)
      return null;

    var manifest = Model(root.AsObject());
    var written = ManifestStateRule.Parse(root["state"]!.GetValue<string>());
    var (derived, missing) = ManifestStateRule.Derive(manifest.Build, manifest.Execution, manifest.Verification);
    if (written > derived) {
      sink.Report(ManifestDiagnostics.StateUnsupported.At(
        Message(ManifestDiagnostics.StateUnsupported, path, ManifestStateRule.Name(written), ManifestStateRule.Name(derived), missing), path));
      return null;
    }

    if (written < derived) {
      sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, path, "/state",
        $"'{ManifestStateRule.Name(written)}' is lower than the '{ManifestStateRule.Name(derived)}' the evidence supports"), path));
      return null;
    }

    return manifest;
  }

  private static string Message(DiagnosticDescriptor descriptor, params object[] arguments)
    => string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.MessageFormat, arguments);

  /// <summary>The model of a tree the schema accepted; every member it reads is there with its type.</summary>
  private static Manifest Model(JsonObject root) {
    var compiler = root["compiler"]!;
    var frontend = root["frontend"]!;
    var target = root["target"]!;
    var licensing = root["licensing"]!;
    var optimization = root["optimization"]!;
    var commands = root["commands"]!;
    return new Manifest {
      Compiler = new(Text(compiler, "name"), Text(compiler, "version"), Int(compiler, "irVersion")),
      Command = Strings(root["command"]),
      Inputs = List(root["inputs"], input => new ManifestInput(Text(input, "path"), Text(input, "sha256"),
        input["provenance"] is { } provenance ? new(Text(provenance, "repository"), Text(provenance, "commit"), Text(provenance, "path")) : null)),
      Frontend = new(Text(frontend, "language"), OptionalText(frontend, "profile")),
      Target = new(Text(target, "language"), Text(target, "profile"), Text(target, "runtime"), Text(target, "platform")),
      Outputs = List(root["outputs"], output => new ManifestOutput(Text(output, "path"), Text(output, "sha256"), Role(Text(output, "role")))),
      Api = List(root["api"], symbol => new ManifestApiSymbol(Text(symbol, "name"), Text(symbol, "signature")) {
        Shape = symbol["shape"]?.DeepClone(),
        AdapterOf = symbol["adapterOf"]?.DeepClone(),
        Representation = symbol["representation"]?.DeepClone()
      }),
      Intrinsics = Strings(root["intrinsics"]),
      FloatingPoint = root["floatingPoint"] is { } floating
        ? new(Text(floating, "mode"), List(floating["natives"], native => new ManifestNativeFunction(
          Text(native, "operation"), Text(native, "format"), Text(native, "accuracy"), Text(native, "runtime"), Int(native, "slack"), Int(native, "bound"))))
        : null,
      Helpers = List(root["helpers"], helper => new ManifestHelper(
        Text(helper, "symbol"), Text(helper, "license"), Text(helper, "origin"), Int(helper, "callSites"), Int(helper, "size"), Strings(helper["dependsOn"]))),
      NativeDependencies = List(root["nativeDependencies"], dependency => new ManifestNativeDependency(Text(dependency, "name"), Text(dependency, "version"), Text(dependency, "licence"))),
      Licensing = new(Text(licensing, "outputLicence"), Strings(licensing["obligations"]), Strings(licensing["modelRestrictions"])),
      Caveats = List(root["caveats"], Message),
      Optimization = new(Text(optimization, "level"), Strings(optimization["disabled"]),
        List(optimization["passes"], pass => new ManifestPass(Text(pass, "id"), Text(pass, "proofClass")))),
      Warnings = List(root["warnings"], Message),
      Toolchains = List(root["toolchains"], toolchain => new ManifestToolchain(Text(toolchain, "key"), Text(toolchain, "version"))),
      Build = root["build"] is { } build ? new(Text(build, "toolchain"), Text(build, "command"), Int(build, "exitCode")) : null,
      Execution = root["execution"] is { } execution ? new(Text(execution, "command"), Int(execution, "exitCode")) : null,
      Verification = root["verification"] is { } verification ? new(
        Text(verification, "oracle"), Text(verification, "producer"), Exactness(verification["exactness"]!),
        Long(verification, "total"), Long(verification, "passed"), Long(verification, "failed")) : null,
      Benchmarks = root["benchmarks"]?.DeepClone().AsArray(),
      Commands = new(Text(commands, "build"), OptionalText(commands, "run")),
      Assumptions = root["assumptions"]?.DeepClone().AsArray(),
      Resources = root["resources"]?.DeepClone().AsObject(),
      ObjectModel = root["objectModel"]?.DeepClone().AsObject(),
      Profile = root["profile"]?.DeepClone().AsObject(),
      Budget = root["budget"]?.DeepClone().AsObject()
    };
  }

  private static ManifestExactness Exactness(JsonNode exactness) {
    var observed = exactness["observed"]!;
    return new(
      Text(exactness, "profile"), Text(exactness, "mode"), Text(exactness, "specialValues"),
      List(exactness["operations"], operation => new ManifestToleranceOperation(
        Text(operation, "operation"), Text(operation, "accuracy"), Text(operation, "policy"), Text(operation, "mode"), Double(operation, "bound"))),
      List(exactness["declared"], declared => new ManifestDeclaredBound(Text(declared, "output"), Text(declared, "mode"), Double(declared, "bound"), Text(declared, "reason"))),
      new(Long(observed, "identical"), Long(observed, "withinTolerance"), Double(observed, "maxDistance"), OptionalText(observed, "worstVector")));
  }

  private static ManifestMessage Message(JsonNode message) => new(Text(message, "code"), Text(message, "message"));

  private static ManifestOutputRole Role(string name) => name switch {
    "source" => ManifestOutputRole.Source,
    "test" => ManifestOutputRole.Test,
    "helper" => ManifestOutputRole.Helper,
    "build" => ManifestOutputRole.Build,
    _ => ManifestOutputRole.Notice
  };

  private static string Text(JsonNode node, string name) => node[name]!.GetValue<string>();

  private static string? OptionalText(JsonNode node, string name) => node[name]?.GetValue<string>();

  private static int Int(JsonNode node, string name) => (int)Long(node, name);

  private static long Long(JsonNode node, string name) => (long)Double(node, name);

  private static double Double(JsonNode node, string name) => node[name]!.GetValue<double>();

  private static IReadOnlyList<string> Strings(JsonNode? list) => [.. list!.AsArray().Select(item => item!.GetValue<string>())];

  private static IReadOnlyList<T> List<T>(JsonNode? list, Func<JsonNode, T> item) => [.. list!.AsArray().Select(node => item(node!))];

}
