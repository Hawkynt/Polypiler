using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Polypiler.Core.Verification.Images;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Images.Support;

/// <summary>A row of <c>tests/fixtures/verify/images/conformance.json</c>.</summary>
/// <param name="Name">The row's name.</param>
/// <param name="Type">The image type.</param>
/// <param name="Value">The value.</param>
/// <param name="Hex">The image, derived by hand and with Python, never with the codec.</param>
public sealed record ConformanceRow(string Name, ImageType Type, ImageValue Value, string Hex) {

  /// <inheritdoc />
  public override string ToString() => this.Name;

}

/// <summary>Reads the value notation of the conformance file (its <c>description</c> says it).</summary>
internal static class ImageJson {

  /// <summary>The conformance file.</summary>
  public static string ConformancePath => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "verify", "images", "conformance.json");

  /// <summary>The folder of the invalid vector files.</summary>
  public static string InvalidFolder => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "verify", "images", "invalid");

  /// <summary>The rows of the conformance file.</summary>
  public static IReadOnlyList<ConformanceRow> Conformance() {
    var root = JsonNode.Parse(File.ReadAllText(ConformancePath))!;
    return [.. root["rows"]!.AsArray().Select(row => {
      var type = ImageType.Parse((string)row!["type"]!);
      return new ConformanceRow((string)row["name"]!, type, Read(type, row["value"]), (string)row["hex"]!);
    })];
  }

  /// <summary>A value of a type in the conformance notation.</summary>
  public static ImageValue Read(ImageType type, JsonNode? node) => type.Kind switch {
    ImageKind.Integer or ImageKind.Char16 or ImageKind.Enum or ImageKind.BigInt => new ImageValue.Integer(BigInteger.Parse(node!.ToJsonString().Trim('"'), CultureInfo.InvariantCulture)),
    ImageKind.Boolean => new ImageValue.Boolean((bool)node!),
    ImageKind.Float => new ImageValue.FloatBits(ulong.Parse(((string)node!)[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
    ImageKind.Bytes => new ImageValue.Bytes(Convert.FromHexString((string)node!)),
    ImageKind.Utf8 or ImageKind.Utf16 => new ImageValue.Text((string)node!),
    ImageKind.Sequence => new ImageValue.Sequence([.. node!.AsArray().Select(item => Read(type.Element!, item))]),
    ImageKind.Optional => node is null ? ImageValue.Optional.Absent : new ImageValue.Optional(Read(type.Element!, node["some"])),
    ImageKind.Record => new ImageValue.Record([.. type.Fields.Select(field => Read(field.Type, node![field.Name]))]),
    ImageKind.Tuple => new ImageValue.Tuple([.. type.Fields.Select((field, index) => Read(field.Type, node![index]))]),
    _ => Union(type, node!)
  };

  private static ImageValue.Union Union(ImageType type, JsonNode node) {
    var tag = (int)node["tag"]!;
    var alternative = type.Fields[tag].Type;
    return new(tag, alternative.Kind == ImageKind.Null ? null : Read(alternative, node["value"]));
  }

}
