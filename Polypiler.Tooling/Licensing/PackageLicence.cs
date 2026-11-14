using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Polypiler.Core;

namespace Polypiler.Tooling.Licensing;

/// <summary>
/// The licence check of a generated package (<see cref="LicenceScanner.ScanPackage"/>): what its
/// manifest states, what its closure makes it, and every problem found. Written as
/// <c>artifacts/licenses/effective.json</c>: <c>{"effectiveLicence","class","because":[],"statedLicence"}</c>,
/// two-space indent, LF line ends and a trailing line break.
/// </summary>
/// <param name="Stated">The manifest's <c>licensing.outputLicence</c>, or <see langword="null"/> when the manifest is invalid.</param>
/// <param name="Effective">The effective licence, or <see langword="null"/> when a member of the closure cannot be part of an output.</param>
/// <param name="Diagnostics">Every problem, in the order found.</param>
public sealed record PackageLicence(string? Stated, EffectiveLicenceResult? Effective, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>The name of the file written beside the scan report.</summary>
  public const string FileName = "effective.json";

  /// <summary>The check as its JSON text, or <see langword="null"/> when there is no effective licence to write.</summary>
  public string? ToJson() {
    if (this.Effective is not { } effective)
      return null;

    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteString("effectiveLicence", effective.Licence);
      writer.WriteString("class", LicenceClasses.Name(effective.Class));
      writer.WriteStartArray("because");
      foreach (var origin in effective.Because)
        writer.WriteStringValue(origin);
      writer.WriteEndArray();
      writer.WriteString("statedLicence", this.Stated);
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
  }

}
