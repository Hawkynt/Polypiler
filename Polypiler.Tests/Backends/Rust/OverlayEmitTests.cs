using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Overlay views as Rust (#460): a view of the bytes of another view as other numbers. One only read is a
/// safe copy built from <c>from_le_bytes</c>/<c>to_le_bytes</c>; one that is written shares its source's memory,
/// which needs unsafe Rust - refused with <c>PLY1567</c> unless the profile allows unsafe code, and then the one
/// unsafe block of the crate, with its <c>SAFETY:</c> reason, listed in the unsafe ledger. The unsafe audit:
/// a crate without <c>allowUnsafe</c> holds no <c>unsafe</c> and forbids it.
/// </summary>
[TestFixture]
public sealed partial class OverlayEmitTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => $"{problem.Code} {problem.Message}")];

  /// <summary>
  /// <c>Overlay()</c>: the <c>u32</c> 0xAABBCC00 in an array, a writable view of its bytes, 1 written at byte 0 and
  /// the number read back - 0xAABBCC01 where the bytes are the number's, the lowest first.
  /// </summary>
  internal static IrModule WritableOverlay() {
    var words = IrType.Array(IrType.U32);
    var a = new IrLocal("a", words);
    var bytes = new IrLocal("bytes", IrType.Span(IrType.U8));
    var view = new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U32), [new IrLocalRef(a)]);
    var body = new IrBlock([
      new IrDeclare(a, new IrIntrinsic(IrIntrinsicOp.ArrayOf, words, [new IrConst(IrType.U32, 0xAABBCC00)])),
      new IrDeclare(bytes, new IrIntrinsic(IrIntrinsicOp.Reinterpret, IrType.Span(IrType.U8), [view])),
      new IrStore(new IrLocalRef(bytes), new IrConst(IrType.I32, 0), new IrConst(IrType.U8, 1)),
      new IrReturn(new IrArrayLoad(new IrLocalRef(a), new IrConst(IrType.I32, 0)))
    ]);
    return new IrModule("Overlays", [new IrFunction("Overlay", true, IrType.U32, [], [a, bytes], body)], []);
  }

  /// <summary>The same module with the overlay as other numbers than bytes, which no profile writes.</summary>
  private static IrModule WritableWordOverlay() {
    var bytesType = IrType.Array(IrType.U8);
    var a = new IrLocal("a", bytesType);
    var words = new IrLocal("words", IrType.Span(IrType.U32));
    var view = new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.U8), [new IrLocalRef(a)]);
    var body = new IrBlock([
      new IrDeclare(a, new IrNewArray(bytesType, new IrConst(IrType.I32, 8))),
      new IrDeclare(words, new IrIntrinsic(IrIntrinsicOp.Reinterpret, IrType.Span(IrType.U32), [view])),
      new IrStore(new IrLocalRef(words), new IrConst(IrType.I32, 0), new IrConst(IrType.U32, 1)),
      new IrReturn(new IrArrayLoad(new IrLocalRef(a), new IrConst(IrType.I32, 0)))
    ]);
    return new IrModule("Overlays", [new IrFunction("Overlay", true, IrType.U8, [], [a, words], body)], []);
  }

  private static readonly RustProfile _unsafe = RustProfile.Rust2024.WithUnsafe(true);

  [Test]
  public void ReadOnlyOverlayOfBytes_IsACopyOfWholeElements() {
    var text = Emitted("public static uint Sum(byte[] d, int n) { System.ReadOnlySpan<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(new System.ReadOnlySpan<byte>(d, 0, n)); uint s = 0; for (int i = 0; i < words.Length; i++) s += words[i]; return s; }");
    Assert.That(text, Does.Contain("fn ply_overlay_u8_u32(source: &[u8]) -> Vec<u32> {"));
    Assert.That(text, Does.Contain(".chunks_exact(4)"));
    Assert.That(text, Does.Contain("u32::from_le_bytes(chunk.try_into().expect(\"4 bytes\"))"));
    Assert.That(text, Does.Contain("let words: &[u32] = &ply_overlay_u8_u32(ply_view(d, 0, n));"));
  }

  [Test]
  public void ReadOnlyOverlayAsBytes_IsTheLittleEndianBytesOfEachElement() {
    var text = Emitted("public static int Bytes(int[] a) { System.ReadOnlySpan<byte> b = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new System.ReadOnlySpan<int>(a)); return b.Length + b[1]; }");
    Assert.That(text, Does.Contain("fn ply_overlay_i32_u8(source: &[i32]) -> Vec<u8> {"));
    Assert.That(text, Does.Contain("        .flat_map(|element: &i32| element.to_le_bytes())\n        .collect::<Vec<u8>>()"));
    Assert.That(text, Does.Contain("let b: &[u8] = &ply_overlay_i32_u8(a);"));
  }

  [Test]
  public void ReadOnlyOverlayBetweenWideTypes_GoesThroughTheBytes() {
    var text = Emitted("public static float First(uint[] a) { System.ReadOnlySpan<float> f = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, float>(new System.ReadOnlySpan<uint>(a)); return f[0]; }");
    Assert.That(text, Does.Contain("let bytes: Vec<u8> = source\n        .iter()\n        .flat_map(|element: &u32| element.to_le_bytes())"));
    Assert.That(text, Does.Contain("f32::from_le_bytes"));
  }

  [Test]
  public void ReadOnlyOverlayOfMemoryTheFunctionWrites_IsRefused() {
    var problems = Problems("public static uint W(byte[] d) { System.ReadOnlySpan<uint> w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(new System.ReadOnlySpan<byte>(d)); d[0] = 1; return w[0]; }");
    Assert.That(problems, Has.Some.Contain("'an overlay view of memory the function writes'"));
  }

  [Test]
  public void WritableOverlayWithoutAllowUnsafe_IsPly1567AndNoCrate() {
    var problems = RustEmitter.Check(WritableOverlay(), RustProfile.Rust2024);
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { RustEmitter.OverlayNeedsUnsafe }));
    Assert.That(problems[0].Message, Is.EqualTo("mutable overlay view in 'Overlay' requires unsafe Rust; the target profile sets allowUnsafe to false"));
    Assert.That(() => RustEmitter.Emit(WritableOverlay(), RustProfile.Rust2024), Throws.ArgumentException);
  }

  [Test]
  public void WritableOverlayWithAllowUnsafe_IsTheOneUnsafeBlock_WithItsSafetyComment() {
    var files = RustEmitter.Emit(WritableOverlay(), _unsafe);
    var module = files.Single(file => file.Path == "src/overlays.rs").Content;
    var root = files.Single(file => file.Path == "src/lib.rs").Content;
    Assert.That(UnsafeToken().Matches(module), Has.Count.EqualTo(1));
    Assert.That(module, Does.Contain("#[allow(unsafe_code)]\n#[cfg(target_endian = \"little\")]\nfn ply_rs_overlay_bytes_mut_u32(p: &mut [u32]) -> &mut [u8] {\n    // SAFETY: "));
    Assert.That(module, Does.Contain("    unsafe {\n        core::slice::from_raw_parts_mut(p.as_mut_ptr().cast::<u8>(), core::mem::size_of_val(p))\n    }\n}"));
    Assert.That(module, Does.Contain("let bytes: &mut [u8] = ply_rs_overlay_bytes_mut_u32(&mut a);"));
    Assert.That(module, Does.Contain("let mut a: Vec<u32> = vec![2864434176];"));
    Assert.That(root, Does.Contain("#![deny(unsafe_code)]"));
    Assert.That(root, Does.Not.Contain("forbid(unsafe_code)"));
    Assert.That(root, Does.Contain("//! Unsafe code: ply_rs_overlay_bytes_mut_u32, for the Reinterpret of a writable view of u32 as bytes in the function 'Overlay'; the crate passes Miri."));
    Assert.That(root, Does.Contain("unsafe code allowed"));
  }

  [Test]
  public void UnsafeLedger_ListsTheUseWithItsFunction_AndNothingWithoutAllowUnsafe() {
    Assert.That(RustEmitter.UnsafeLedger(WritableOverlay(), _unsafe), Is.EqualTo(new[] {
      new RustUnsafeUse("ply_rs_overlay_bytes_mut_u32", "Overlay", "Reinterpret of a writable view of u32 as bytes")
    }));
    Assert.That(RustEmitter.UnsafeLedger(WritableOverlay(), RustProfile.Rust2024), Is.Empty);
  }

  [Test]
  public void WritableOverlayAsOtherNumbersThanBytes_IsRefusedEvenWithAllowUnsafe() {
    var problems = RustEmitter.Check(WritableWordOverlay(), _unsafe);
    Assert.That(problems.Select(problem => problem.Message), Has.Some.Contain("'a writable overlay of u8 as u32: only bytes have no alignment to keep'"));
  }

  [Test]
  public void UnsafeCrate_ReadsBackToItsText() {
    foreach (var file in RustEmitter.Emit(WritableOverlay(), _unsafe).Where(file => file.Path.EndsWith(".rs", StringComparison.Ordinal)))
      Assert.That(RustPrinter.Print(RustSyntaxReader.Read(file.Content)), Is.EqualTo(file.Content), file.Path);
  }

  [Test]
  public void ProfileWithUnsafe_KeepsItsVersion() {
    var profile = RustProfile.Rust2021.WithRustVersion(new Version(1, 77)).WithUnsafe(true).WithRustVersion(new Version(1, 80));
    Assert.That(profile.AllowUnsafe, Is.True);
    Assert.That(profile.RustVersionName, Is.EqualTo("1.80"));
    Assert.That(RustProfile.Rust2024.AllowUnsafe, Is.False);
    Assert.That(profile.WithUnsafe(false).AllowUnsafe, Is.False);
  }

  /// <summary>
  /// The unsafe audit over every prototype fixture the backend writes: without <c>allowUnsafe</c> no generated file
  /// holds the token <c>unsafe</c> and the crate root forbids unsafe code.
  /// </summary>
  [Test]
  [Category("Slow")]
  public void EveryCrateWithoutAllowUnsafe_HoldsNoUnsafeCode() {
    foreach (var name in Polypiler.Prototype.Testing.PrototypeFixture.Names) {
      var module = Polypiler.Prototype.Testing.PrototypeFixture.Imported(name);
      if (RustCrates.Check(module).Count > 0)
        continue;

      var files = RustCrates.Emit(module);
      Assert.That(files.Where(file => file.Path.EndsWith(".rs", StringComparison.Ordinal)).Where(file => UnsafeToken().IsMatch(file.Content)).Select(file => file.Path), Is.Empty, name);
      Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("#![forbid(unsafe_code)]"), name);
    }
  }

  [GeneratedRegex(@"\bunsafe\b(?!_code)")]
  private static partial Regex UnsafeToken();

}
