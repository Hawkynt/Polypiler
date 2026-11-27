using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// A binary accepted into the frontend: the merged image, its address map, the contracts of its
/// entries and the observation projection, which stays the JSON the manifest wrote.
/// </summary>
/// <param name="Image">The image, the manifest's entries and imports merged in.</param>
/// <param name="AddressMap">The memory map of the manifest, or else one range per section.</param>
/// <param name="Entries">The contracts of the manifest's entries.</param>
/// <param name="Observe">The observation projection, or <see langword="null"/>.</param>
/// <param name="Manifest">The manifest, or <see langword="null"/> when the container stated everything.</param>
public sealed record LoadedBinary(BinaryImage Image, AddressMap AddressMap, ImmutableArray<EntryContract> Entries, JsonElement? Observe, BinaryManifest? Manifest) {

  /// <summary>Whether discovery may decode the byte at an address: it lies in an executable section and, when the manifest restricts decoding, in one of its code ranges.</summary>
  /// <param name="address">The address.</param>
  /// <returns>Whether it may.</returns>
  public bool IsDecodable(ulong address)
    => this.Image.SectionAt(address) is { Executable: true } && this.Image.ModeAt(address) != "data"
      && (this.Manifest is null || this.Manifest.CodeRanges.IsEmpty || this.Manifest.CodeRanges.Any(range => range.Contains(address)));

  /// <inheritdoc/>
  public bool Equals(LoadedBinary? other)
    => other is not null && this.Image == other.Image && this.AddressMap.Ranges.SequenceEqual(other.AddressMap.Ranges) && this.Entries.SequenceEqual(other.Entries)
      && ReferenceEquals(this.Manifest, other.Manifest);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Image, this.Entries.Length);

}

/// <summary>
/// Loads a binary with its manifest: checks the hash, selects the codec, merges what the manifest and
/// the container state and refuses every contradiction. The container supplies sections, symbols,
/// relocations and imports; the manifest may add imports and ranges and must not contradict machine,
/// mode or byte order. Bytes are never changed.
/// </summary>
public static class ImageBuilder {

  /// <summary>Loads the image the manifest names, resolved against the manifest's directory.</summary>
  /// <param name="manifest">The manifest.</param>
  /// <param name="registry">The codecs, or <see langword="null"/> for <see cref="BinaryCodecRegistry.CreateDefault"/>.</param>
  /// <param name="catalog">The machine profiles the ISA is checked against, or <see langword="null"/> to accept any ISA name.</param>
  /// <returns>The binary, or every problem found.</returns>
  public static BinaryResult<LoadedBinary> Load(BinaryManifest manifest, BinaryCodecRegistry? registry = null, MachineProfileCatalog? catalog = null) {
    ArgumentNullException.ThrowIfNull(manifest);
    return Load(manifest.ResolvedImagePath, manifest, registry, catalog);
  }

  /// <summary>Loads an image.</summary>
  /// <param name="imagePath">The image file.</param>
  /// <param name="manifest">The manifest, or <see langword="null"/> when the container states everything.</param>
  /// <param name="registry">The codecs, or <see langword="null"/> for <see cref="BinaryCodecRegistry.CreateDefault"/>.</param>
  /// <param name="catalog">The machine profiles the ISA is checked against, or <see langword="null"/> to accept any ISA name.</param>
  /// <returns>
  /// The binary; or <c>PLY0004</c> for a missing image, <c>PLY4056</c> for a hash mismatch, <c>PLY4059</c>
  /// for a container no codec reads, <c>PLY4055</c> for an ISA without a profile, <c>PLY4057</c> for each
  /// contradiction and <c>PLY4058</c> for each entry or range outside the code.
  /// </returns>
  public static BinaryResult<LoadedBinary> Load(string imagePath, BinaryManifest? manifest, BinaryCodecRegistry? registry = null, MachineProfileCatalog? catalog = null) {
    ArgumentNullException.ThrowIfNull(imagePath);
    if (!File.Exists(imagePath))
      return BinaryResult<LoadedBinary>.Failure(BinaryDiagnostics.Unreadable(imagePath));
    var file = File.ReadAllBytes(imagePath);

    IsaProfile? profile = null;
    if (manifest is not null) {
      var actual = Convert.ToHexStringLower(SHA256.HashData(file));
      if (actual != manifest.Sha256)
        return BinaryResult<LoadedBinary>.Failure(Diagnostic.Create(BinaryDiagnostics.HashMismatch, null, imagePath, actual, manifest.Sha256));
      if (catalog is not null) {
        profile = catalog.Find(manifest.Isa);
        if (profile is null)
          return BinaryResult<LoadedBinary>.Failure(Diagnostic.Create(BinaryDiagnostics.InvalidManifest, null, manifest.Source, $"isa '{manifest.Isa}' has no machine profile"));
      }
    }

    var codec = (registry ?? BinaryCodecRegistry.CreateDefault()).Select(file, manifest?.Container, imagePath);
    if (!codec.Succeeded)
      return BinaryResult<LoadedBinary>.Failure(codec.Problems);
    var read = codec.Value.Read(file, manifest, imagePath);
    if (!read.Succeeded)
      return BinaryResult<LoadedBinary>.Failure(read.Problems);

    var built = manifest is null ? Accept(read.Value, null, profile) : Merge(read.Value, manifest, profile);
    return built.Succeeded && !read.Problems.IsEmpty ? BinaryResult<LoadedBinary>.Success(built.Value, read.Problems) : built;
  }

  private static BinaryResult<LoadedBinary> Merge(BinaryImage container, BinaryManifest manifest, IsaProfile? profile) {
    var problems = ImmutableArray.CreateBuilder<Diagnostic>();
    void Agree(string field, string stated, string other, string? value) {
      if (value is not null && value != stated)
        problems.Add(Diagnostic.Create(BinaryDiagnostics.Contradicted, null, field, stated, other, value));
    }

    Agree("isa", manifest.Isa, "the container", container.Isa);
    Agree("mode", manifest.Mode, "the container", container.Mode);
    if (manifest.ByteOrder is { } order) {
      Agree("byteOrder", BinaryManifest.Name(order), "the container", BinaryManifest.Name(container.ByteOrder));
      Agree("byteOrder", BinaryManifest.Name(order), $"machine profile '{profile?.Id}'", profile is null ? null : BinaryManifest.Name(profile.ByteOrder));
    }

    var imports = container.Imports.ToList();
    foreach (var import in manifest.Imports) {
      var known = imports.FirstOrDefault(existing => existing.SlotAddress == import.Slot);
      if (known is null)
        imports.Add(new ImageImport(import.Slot, import.Symbol, import.Library, null));
      else
        Agree($"import at {BinaryDiagnostics.Hex(import.Slot)}", import.Symbol, "the container", known.Symbol);
    }

    var image = container with {
      Relocations = manifest.Relocations ?? container.Relocations,
      Imports = [.. imports],
      Entries = [.. container.Entries.Concat(manifest.Entries.Select(entry => entry.Address)).Distinct()]
    };

    foreach (var range in manifest.CodeRanges.Concat(manifest.DataRanges))
      if (image.SectionAt(range.Start) is not { } section || !section.Contains(range.End - 1))
        problems.Add(BinaryDiagnostics.RangeOutside(range.Start, range.End));

    var entries = ImmutableArray.CreateBuilder<EntryContract>();
    foreach (var entry in manifest.Entries) {
      var inCode = image.SectionAt(entry.Address) is { Executable: true } && image.ModeAt(entry.Address) != "data"
        && (manifest.CodeRanges.IsEmpty || manifest.CodeRanges.Any(range => range.Contains(entry.Address)));
      if (!inCode)
        problems.Add(Diagnostic.Create(BinaryDiagnostics.OutsideCode, null, entry.Name, BinaryDiagnostics.Hex(entry.Address)));
      entries.Add(new EntryContract(entry.Name, entry.Address, entry.Abi.Convention, entry.Abi.Parameters, entry.Abi.Result, entry.Buffers));
    }

    if (problems.Count > 0)
      return BinaryResult<LoadedBinary>.Failure(problems.ToImmutable());
    return Accept(image, manifest, profile, entries.ToImmutable());
  }

  private static BinaryResult<LoadedBinary> Accept(BinaryImage image, BinaryManifest? manifest, IsaProfile? profile, ImmutableArray<EntryContract> entries = default) {
    IEnumerable<AddressRange> ranges = manifest?.MemoryMap is { } map
      ? map
      : image.Sections.Where(section => section.IsMapped && section.Size > 0)
        .Select(section => new AddressRange(section.Name, section.Address, section.Address + (section.Size - 1), section.Writable ? AddressKind.Ram : AddressKind.Rom, section.Executable));
    try {
      var addressMap = new AddressMap(ranges, profile?.AddressBits ?? 64, image.Isa);
      return BinaryResult<LoadedBinary>.Success(new LoadedBinary(image, addressMap, entries.IsDefault ? [] : entries, manifest?.Observe, manifest));
    } catch (MachineException exception) {
      return BinaryResult<LoadedBinary>.Failure(exception.Diagnostic);
    }
  }

}
