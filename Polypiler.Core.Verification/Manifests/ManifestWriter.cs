using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// Serializes a <see cref="Manifest"/> canonically, so that the same manifest always gives the same
/// bytes (PRD §13.1): UTF-8 without BOM, LF, two-space indent, a final line break, properties in the
/// order of <c>spec/schemas/manifest.schema.json</c>, the lists <c>inputs</c>, <c>outputs</c>,
/// <c>intrinsics</c>, <c>helpers</c> and <c>toolchains</c> sorted ordinally by path, ID, symbol and key,
/// every other list in its own order, SHA-256 values in lower case, paths with <c>/</c>, and absent
/// optional sections left out rather than written as <c>null</c>. It adds nothing the model does not
/// hold: no timestamp, no host name.
/// </summary>
public static class ManifestWriter {

  private static readonly UTF8Encoding _utf8 = new(false);

  /// <summary>The text of a manifest.</summary>
  /// <param name="manifest">The manifest.</param>
  /// <returns>The canonical text, ending in a line break.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
  public static string Serialize(Manifest manifest) {
    ArgumentNullException.ThrowIfNull(manifest);
    return CanonicalJson.Serialize(Tree(manifest)) + "\n";
  }

  /// <summary>
  /// Writes <see cref="Manifest.FileName"/> into a directory through a temporary file that is renamed
  /// over it, so that a failed write leaves no partial manifest behind.
  /// </summary>
  /// <param name="manifest">The manifest.</param>
  /// <param name="directory">The output directory, which must exist.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="IOException">The directory cannot be written.</exception>
  /// <exception cref="UnauthorizedAccessException">The directory cannot be written.</exception>
  public static void Write(Manifest manifest, string directory) {
    ArgumentNullException.ThrowIfNull(directory);
    var bytes = _utf8.GetBytes(Serialize(manifest));
    var target = Path.Combine(directory, Manifest.FileName);
    var temporary = Path.Combine(directory, $".{Manifest.FileName}.{Guid.NewGuid():N}.tmp");
    try {
      File.WriteAllBytes(temporary, bytes);
      File.Move(temporary, target, true);
    } catch {
      if (File.Exists(temporary))
        File.Delete(temporary);

      throw;
    }
  }

  /// <summary>The SHA-256 of bytes as the manifest writes it: 64 lower-case hexadecimal digits.</summary>
  /// <param name="bytes">The bytes.</param>
  /// <returns>The hash.</returns>
  public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  /// <summary>A path as the manifest writes it: <c>/</c> as the separator.</summary>
  /// <param name="path">A relative path.</param>
  /// <returns>The path with every <c>\</c> replaced.</returns>
  public static string PortablePath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    return path.Replace('\\', '/');
  }

  private static JsonObject Tree(Manifest manifest) {
    var root = new JsonObject {
      ["schemaVersion"] = Manifest.SchemaVersion,
      ["compiler"] = new JsonObject {
        ["name"] = manifest.Compiler.Name,
        ["version"] = manifest.Compiler.Version,
        ["irVersion"] = manifest.Compiler.IrVersion
      },
      ["command"] = Strings(manifest.Command),
      ["inputs"] = List(manifest.Inputs.OrderBy(input => PortablePath(input.Path), StringComparer.Ordinal), input => new JsonObject {
        ["path"] = PortablePath(input.Path),
        ["sha256"] = input.Sha256.ToLowerInvariant(),
        ["provenance"] = input.Provenance is { } provenance
          ? new JsonObject { ["repository"] = provenance.Repository, ["commit"] = provenance.Commit, ["path"] = PortablePath(provenance.Path) }
          : null
      }),
      ["frontend"] = new JsonObject { ["language"] = manifest.Frontend.Language, ["profile"] = manifest.Frontend.Profile },
      ["target"] = new JsonObject {
        ["language"] = manifest.Target.Language,
        ["profile"] = manifest.Target.Profile,
        ["runtime"] = manifest.Target.Runtime,
        ["platform"] = manifest.Target.Platform
      },
      ["outputs"] = List(manifest.Outputs.OrderBy(output => PortablePath(output.Path), StringComparer.Ordinal), output => new JsonObject {
        ["path"] = PortablePath(output.Path),
        ["sha256"] = output.Sha256.ToLowerInvariant(),
        ["role"] = RoleName(output.Role)
      }),
      ["api"] = List(manifest.Api, Symbol),
      ["intrinsics"] = Strings(manifest.Intrinsics.Order(StringComparer.Ordinal))
    };
    if (manifest.FloatingPoint is { } floating)
      root["floatingPoint"] = new JsonObject {
        ["mode"] = floating.Mode,
        ["natives"] = List(floating.Natives, native => new JsonObject {
          ["operation"] = native.Operation,
          ["format"] = native.Format,
          ["accuracy"] = native.Accuracy,
          ["runtime"] = native.Runtime,
          ["slack"] = native.Slack,
          ["bound"] = native.Bound
        })
      };

    root["helpers"] = List(manifest.Helpers.OrderBy(helper => helper.Symbol, StringComparer.Ordinal), helper => new JsonObject {
      ["symbol"] = helper.Symbol,
      ["license"] = helper.License,
      ["origin"] = helper.Origin,
      ["callSites"] = helper.CallSites,
      ["size"] = helper.Size,
      ["dependsOn"] = Strings(helper.DependsOn)
    });
    root["nativeDependencies"] = List(manifest.NativeDependencies, dependency => new JsonObject {
      ["name"] = dependency.Name, ["version"] = dependency.Version, ["licence"] = dependency.Licence
    });
    root["licensing"] = new JsonObject {
      ["outputLicence"] = manifest.Licensing.OutputLicence,
      ["obligations"] = Strings(manifest.Licensing.Obligations),
      ["modelRestrictions"] = Strings(manifest.Licensing.ModelRestrictions)
    };
    root["caveats"] = List(manifest.Caveats, Message);
    root["optimization"] = new JsonObject {
      ["level"] = manifest.Optimization.Level,
      ["disabled"] = Strings(manifest.Optimization.Disabled),
      ["passes"] = List(manifest.Optimization.Passes, pass => new JsonObject { ["id"] = pass.Id, ["proofClass"] = pass.ProofClass })
    };
    root["warnings"] = List(manifest.Warnings, Message);
    root["toolchains"] = List(manifest.Toolchains.OrderBy(toolchain => toolchain.Key, StringComparer.Ordinal), toolchain => new JsonObject {
      ["key"] = toolchain.Key, ["version"] = toolchain.Version
    });
    if (manifest.Build is { } build)
      root["build"] = new JsonObject { ["toolchain"] = build.Toolchain, ["command"] = build.Command, ["exitCode"] = build.ExitCode };
    if (manifest.Execution is { } execution)
      root["execution"] = new JsonObject { ["command"] = execution.Command, ["exitCode"] = execution.ExitCode };
    if (manifest.Verification is { } verification)
      root["verification"] = new JsonObject {
        ["oracle"] = verification.Oracle,
        ["producer"] = verification.Producer,
        ["exactness"] = Exactness(verification.Exactness),
        ["total"] = verification.Total,
        ["passed"] = verification.Passed,
        ["failed"] = verification.Failed
      };
    if (manifest.Benchmarks is { } benchmarks)
      root["benchmarks"] = benchmarks.DeepClone();

    root["commands"] = new JsonObject { ["build"] = manifest.Commands.Build, ["run"] = manifest.Commands.Run };
    root["state"] = ManifestStateRule.Name(manifest.State);
    Section(root, "assumptions", manifest.Assumptions);
    Section(root, "resources", manifest.Resources);
    Section(root, "objectModel", manifest.ObjectModel);
    Section(root, "profile", manifest.Profile);
    Section(root, "budget", manifest.Budget);
    return root;
  }

  private static void Section(JsonObject root, string name, JsonNode? section) {
    if (section is not null)
      root[name] = section.DeepClone();
  }

  private static JsonObject Symbol(ManifestApiSymbol symbol) {
    var node = new JsonObject { ["name"] = symbol.Name, ["signature"] = symbol.Signature };
    Section(node, "shape", symbol.Shape);
    Section(node, "adapterOf", symbol.AdapterOf);
    Section(node, "representation", symbol.Representation);
    return node;
  }

  private static JsonObject Exactness(ManifestExactness exactness) {
    var observed = new JsonObject {
      ["identical"] = exactness.Observed.Identical,
      ["withinTolerance"] = exactness.Observed.WithinTolerance,
      ["maxDistance"] = exactness.Observed.MaxDistance
    };
    if (exactness.Observed.WorstVector is { } worst)
      observed["worstVector"] = worst;

    return new JsonObject {
      ["profile"] = exactness.Profile,
      ["mode"] = exactness.Mode,
      ["specialValues"] = exactness.SpecialValues,
      ["operations"] = List(exactness.Operations, operation => new JsonObject {
        ["operation"] = operation.Operation,
        ["accuracy"] = operation.Accuracy,
        ["policy"] = operation.Policy,
        ["mode"] = operation.Mode,
        ["bound"] = operation.Bound
      }),
      ["declared"] = List(exactness.Declared, declared => new JsonObject {
        ["output"] = declared.Output, ["mode"] = declared.Mode, ["bound"] = declared.Bound, ["reason"] = declared.Reason
      }),
      ["observed"] = observed
    };
  }

  private static JsonObject Message(ManifestMessage message) => new() { ["code"] = message.Code, ["message"] = message.Message };

  private static JsonArray Strings(IEnumerable<string> items) => [.. items.Select(item => (JsonNode)JsonValue.Create(item))];

  private static JsonArray List<T>(IEnumerable<T> items, Func<T, JsonNode> node) => [.. items.Select(node)];

  /// <summary>The name of a role as the manifest writes it.</summary>
  /// <param name="role">The role.</param>
  /// <returns><c>source</c>, <c>test</c>, <c>helper</c>, <c>build</c> or <c>notice</c>.</returns>
  public static string RoleName(ManifestOutputRole role) => role switch {
    ManifestOutputRole.Source => "source",
    ManifestOutputRole.Test => "test",
    ManifestOutputRole.Helper => "helper",
    ManifestOutputRole.Build => "build",
    _ => "notice"
  };

}
