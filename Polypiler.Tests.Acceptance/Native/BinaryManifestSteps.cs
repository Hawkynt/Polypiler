using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.BinaryFormats;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Native;

/// <summary>
/// The steps of <c>tests/features/native/mach-bin-manifest.feature</c>: the fixtures of
/// <c>tests/fixtures/native/mach-bin-manifest</c>, varied in a scratch directory and loaded in process
/// with <see cref="ImageBuilder"/>.
/// </summary>
[Binding, Scope(Feature = "Binary input manifest")]
public sealed class BinaryManifestSteps : IDisposable {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-bin-manifest");

  private readonly string _directory = Path.Combine(Path.GetTempPath(), "polypiler-manifest-" + Guid.NewGuid().ToString("N"));
  private string? _image;
  private BinaryManifest? _manifest;
  private BinaryResult<LoadedBinary>? _result;

  /// <summary>Creates the scratch directory.</summary>
  public BinaryManifestSteps() => Directory.CreateDirectory(this._directory);

  private BinaryResult<LoadedBinary> Result => this._result ?? throw new AssertionException("ImageBuilder.Load did not run");

  private static byte[] Bytes(string text) => [.. text.Split(' ').Select(pair => byte.Parse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))];

  private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  private static JsonObject Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(_fixtures, name)))!.AsObject();

  private void Manifest(JsonObject json) {
    var path = Path.Combine(this._directory, "manifest.json");
    File.WriteAllText(path, json.ToJsonString());
    var result = BinaryManifest.Load(path);
    this._manifest = result.Value ?? throw new AssertionException(string.Join("; ", result.Problems));
  }

  private string Image(byte[] bytes) {
    this._image = Path.Combine(this._directory, "blob.bin");
    File.WriteAllBytes(this._image, bytes);
    return this._image;
  }

  private Diagnostic Failure(string code) {
    Assert.That(this.Result.Succeeded, Is.False);
    var problem = this.Result.Problems.Single();
    Assert.That(problem.Code, Is.EqualTo(code));
    return problem;
  }

  /// <summary>The fixture blob, checked to hold the bytes.</summary>
  [Given("^\"blob.bin\" containing the bytes ((?:[0-9A-F]{2} ?)+)$")]
  public void Blob(string bytes) {
    var fixture = File.ReadAllBytes(Path.Combine(_fixtures, "blob.bin"));
    Assert.That(fixture, Is.EqualTo(Bytes(bytes)));
    this.Image(fixture);
  }

  /// <summary>The fixture <c>min.json</c>, checked to state the values.</summary>
  [Given("^\"min.json\" with isa " + StepText.Quoted + ", mode " + StepText.Quoted + ", loadAddress " + StepText.Quoted + ", one entry " + StepText.Quoted + " at " + StepText.Quoted + " with abi " + StepText.Quoted + " and the SHA-256 of blob.bin$")]
  public void Min(string isa, string mode, string loadAddress, string entry, string address, string abi) {
    this.Manifest(Fixture("min.json"));
    var manifest = this._manifest!;
    Assert.Multiple(() => {
      Assert.That((manifest.Isa, manifest.Mode, BinaryDiagnostics.Hex(manifest.LoadAddress!.Value)), Is.EqualTo((isa, mode, loadAddress)));
      Assert.That(manifest.Entries.Single(), Is.EqualTo(new ManifestEntry(entry, ulong.Parse(address[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture), AbiSignature.Parse(abi)!, [])));
      Assert.That(manifest.Sha256, Is.EqualTo(Sha256(File.ReadAllBytes(Path.Combine(_fixtures, "blob.bin")))));
    });
  }

  /// <summary><c>min.json</c> with a blob whose last byte is changed.</summary>
  [Given("^\"min.json\" and a copy of blob.bin whose last byte is ([0-9A-F]{2}) instead of ([0-9A-F]{2})$")]
  public void ChangedBlob(string now, string before) {
    var bytes = File.ReadAllBytes(Path.Combine(_fixtures, "blob.bin"));
    Assert.That(bytes[^1], Is.EqualTo(Bytes(before)[0]));
    bytes[^1] = Bytes(now)[0];
    this.Manifest(Fixture("min.json"));
    this.Image(bytes);
  }

  /// <summary><c>min.json</c> with its entry moved.</summary>
  [Given("^\"min.json\" with the entry address changed to " + StepText.Quoted + "$")]
  public void MovedEntry(string address) {
    var json = Fixture("min.json");
    json["entries"]![0]!["address"] = address;
    this.Image(File.ReadAllBytes(Path.Combine(_fixtures, "blob.bin")));
    this.Manifest(json);
  }

  /// <summary>A file with a magic and a manifest that leaves the container to detection.</summary>
  [Given("^a file starting with the bytes ((?:[0-9A-F]{2} ?)+) and a manifest without the field " + StepText.Quoted + "$")]
  public void Magic(string bytes, string field) {
    var file = Bytes(bytes).Concat(new byte[12]).ToArray();
    var json = Fixture("min.json");
    json.Remove(field);
    json["image"]!["sha256"] = Sha256(file);
    this.Image(file);
    this.Manifest(json);
  }

  /// <summary>Loads the image with the manifest.</summary>
  [When("^ImageBuilder.Load runs$")]
  public void Load() => this._result = ImageBuilder.Load(this._image!, this._manifest);

  /// <summary>The one section.</summary>
  [Then("^the image has one executable section at 0x([0-9A-F]+) of (\\d+) bytes$")]
  public void OneSection(string address, int length) {
    var section = this.Result.Value!.Image.Sections.Single();
    Assert.That((section.Executable, BinaryDiagnostics.Hex(section.Address), section.Bytes.Length), Is.EqualTo((true, "0x" + address, length)));
  }

  /// <summary>The entry contract.</summary>
  [Then("^the entry contract of " + StepText.Quoted + " is convention " + StepText.Quoted + " with one (\\w+) parameter and a (\\w+) result$")]
  public void Contract(string name, string convention, string parameter, string result) {
    var contract = this.Result.Value!.Entries.Single(entry => entry.Name == name);
    Assert.That((contract.Convention, contract.Parameters.Single(), contract.Result), Is.EqualTo((convention, parameter, result)));
  }

  /// <summary><c>PLY4056</c> with the image's and the manifest's hash.</summary>
  [Then("^it fails with PLY4056 showing both hashes$")]
  public void HashMismatch() {
    var problem = this.Failure("PLY4056");
    Assert.That(problem.Message, Does.Contain(Sha256(File.ReadAllBytes(this._image!))).And.Contain(this._manifest!.Sha256));
  }

  /// <summary><c>PLY4058</c> naming the entry and its address.</summary>
  [Then("^it fails with PLY4058 naming " + StepText.Quoted + " and (0x[0-9A-F]+)$")]
  public void OutsideCode(string name, string address)
    => Assert.That(this.Failure("PLY4058").Message, Does.Contain($"'{name}'").And.Contain(address));

  /// <summary><c>PLY4059</c> naming the format.</summary>
  [Then("^it fails with PLY4059 naming " + StepText.Quoted + "$")]
  public void UnknownContainer(string format) => Assert.That(this.Failure("PLY4059").Message, Does.Contain(format));

  /// <summary>Removes the scratch directory.</summary>
  public void Dispose() => Directory.Delete(this._directory, recursive: true);

}
