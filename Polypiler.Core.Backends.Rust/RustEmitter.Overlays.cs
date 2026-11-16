using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

/// <summary>One use of unsafe code in a crate: the spelling function that holds it, the function that needed it and why.</summary>
/// <param name="Helper">The spelling function, such as <c>ply_rs_overlay_bytes_mut_u32</c>.</param>
/// <param name="Function">The IR function whose operation needed it.</param>
/// <param name="Construct">The lowering row: the IR operation and what it overlays.</param>
public sealed record RustUnsafeUse(string Helper, string Function, string Construct);

public static partial class RustEmitter {

  /// <summary>The code for a mutable overlay view, which only unsafe Rust can write, where the profile allows no unsafe code.</summary>
  public const string OverlayNeedsUnsafe = "PLY1567";

  /// <summary>The registered diagnostic of <see cref="OverlayNeedsUnsafe"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OverlayNeedsUnsafeDescriptor = new(OverlayNeedsUnsafe, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.unsafe");

  /// <summary>
  /// The unsafe ledger of a module (#460): every use of unsafe code its crate holds, each with the function and
  /// the lowering row that needed it. Empty unless the profile allows unsafe code; the header of
  /// <c>src/lib.rs</c> lists the same uses.
  /// </summary>
  /// <param name="module">The module, legalized for the profile.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <returns>The uses, in the order the functions are written.</returns>
  public static IReadOnlyList<RustUnsafeUse> UnsafeLedger(IrModule module, RustProfile profile, RustCapabilities? capabilities = null) {
    System.ArgumentNullException.ThrowIfNull(module);
    var writer = new Writer(profile, capabilities ?? RustCapabilities.Default, RustEmitOptions.Default);
    writer.Crate(module);
    return writer.Unsafe;
  }

  private sealed partial class Writer {

    private readonly List<RustUnsafeUse> _unsafe = [];

    /// <summary>The uses of unsafe code the crate holds.</summary>
    public IReadOnlyList<RustUnsafeUse> Unsafe => this._unsafe;

    /// <summary>
    /// <c>Reinterpret</c>: a view of the bytes of one view as elements of another number type, the lowest byte
    /// first on every host. One only read is a copy taken where it is made, as the IR allows (a copy differs only
    /// where the source changes while the result is read, so the source must be memory the function never
    /// writes): <c>&amp;ply_overlay_u8_u32(source)</c>, built safely from <c>from_le_bytes</c> and
    /// <c>to_le_bytes</c>. One that is written must share the memory of its source, which safe Rust cannot:
    /// refused with <c>PLY1567</c> unless the profile allows unsafe code, and then only as bytes, whose alignment
    /// is 1, through the spelling function <c>ply_rs_overlay_bytes_mut_&lt;t&gt;</c>, the one unsafe block.
    /// </summary>
    private RsExpression Overlay(IrIntrinsic overlay, bool mutable) {
      var source = overlay.Arguments[0];
      var (from, to) = (source.Type.Element!, overlay.Type.Element!);
      if (!mutable) {
        if (this.ViewRootOf(source) is { } root && this.IsWrittenMemory(root))
          return this.Refuse("an overlay view of memory the function writes");

        return new RsReference(false, new RsCall(new RsPath(this.OverlayCopy(from, to)), [this.ViewSource(source, false)]) { Type = overlay.Type }) { Type = overlay.Type };
      }

      if (!this._profile.AllowUnsafe) {
        var what = $"mutable overlay view in '{this._function.Name}'";
        if (this._refused.Add(what))
          this._problems.Add(OverlayNeedsUnsafeDescriptor.At($"{what} requires unsafe Rust; the target profile sets allowUnsafe to false"));
        return new RsPath("_refused");
      }

      if (to != IrType.U8)
        return this.Refuse($"a writable overlay of {from} as {to}: only bytes have no alignment to keep");

      var helper = this.OverlayBytes(from);
      if (!this._unsafe.Any(use => use.Helper == helper && use.Function == this._function.Name))
        this._unsafe.Add(new RustUnsafeUse(helper, this._function.Name, $"Reinterpret of a writable view of {from} as bytes"));
      return new RsCall(new RsPath(helper), [this.ViewSource(source, true)]) { Type = overlay.Type };
    }

    /// <summary>Whether the function writes the memory of a variable: an array parameter or local it writes, a view it writes through.</summary>
    private bool IsWrittenMemory(IrLocal root)
      => root.Type.IsView ? this.WritesView(root)
        : this._arrayParameters.TryGetValue(root, out var position) ? this._arrayFacts.MutableParameters.Contains(position)
        : this._arrayFacts.MutableLocals.Contains(root);

    /// <summary>
    /// The spelling function of a read-only overlay, <c>ply_overlay_&lt;t&gt;_&lt;u&gt;(source: &amp;[T]) -&gt; Vec&lt;U&gt;</c>:
    /// the bytes of the elements, the lowest first, then as many whole elements of the other type as they hold. Each
    /// <c>collect</c> names its vector, so that the body still has its type where O0500 inlines it.
    /// </summary>
    private string OverlayCopy(IrType from, IrType to) {
      var (fromName, toName) = (TypeName(from)!, TypeName(to)!);
      var name = $"ply_overlay_{fromName}_{toName}";
      this.Spell(name, () => {
        var source = new RsPath("source");
        RsExpression Bytes() => new RsMethodCall(new RsMethodCall(new RsMethodCall(source, "iter", []), "flat_map", [new RsClosure(false, [new RsParameter("element", $"&{fromName}")], new RsMethodCall(new RsPath("element"), "to_le_bytes", []))]), "collect::<Vec<u8>>", []);
        RsExpression Elements(RsExpression bytes) {
          var width = Width(to).ToString(CultureInfo.InvariantCulture);
          var array = new RsMethodCall(new RsMethodCall(new RsPath("chunk"), "try_into", []), "expect", [new RsString($"{width} bytes")]);
          var element = new RsClosure(false, [new RsParameter("chunk", "&[u8]")], new RsCall(new RsPath(toName + "::from_le_bytes"), [array]));
          return new RsMethodCall(new RsMethodCall(new RsMethodCall(bytes, "chunks_exact", [new RsLiteral(width)]), "map", [element]), $"collect::<Vec<{toName}>>", []);
        }

        var body = from == IrType.U8 ? new RsBlock([], Elements(source))
          : to == IrType.U8 ? new RsBlock([], Bytes())
          : new RsBlock([new RsLet("bytes", false, "Vec<u8>", Bytes())], Elements(new RsPath("bytes")));
        return new RsFunction(name, "", [new RsParameter("source", $"&[{fromName}]")], $"Vec<{toName}>", body);
      });
      return name;
    }

    /// <summary>
    /// The spelling function of a writable overlay of bytes, <c>ply_rs_overlay_bytes_mut_&lt;t&gt;(p: &amp;mut [T]) -&gt; &amp;mut [u8]</c>:
    /// the one unsafe block of a crate, which allows it alone (<c>#[allow(unsafe_code)]</c> under the root's
    /// <c>#![deny(unsafe_code)]</c>), with its <c>SAFETY:</c> reason, and only on a little-endian target, where the
    /// native bytes are the IR's.
    /// </summary>
    private string OverlayBytes(IrType element) {
      var type = TypeName(element)!;
      var name = "ply_rs_overlay_bytes_mut_" + type;
      this.Spell(name, () => {
        var pointer = new RsMethodCall(new RsMethodCall(new RsPath("p"), "as_mut_ptr", []), "cast::<u8>", []);
        var view = new RsCall(new RsPath("core::slice::from_raw_parts_mut"), [pointer, new RsCall(new RsPath("core::mem::size_of_val"), [new RsPath("p")])]);
        return new RsFunction(name, "", [new RsParameter("p", $"&mut [{type}]")], "&mut [u8]", new RsBlock([
          new RsComment($"SAFETY: the bytes of `p` are {type} elements, which have no padding and no invalid bit pattern, and"),
          new RsComment("u8 has alignment 1; the result borrows `p` exclusively for its lifetime and covers exactly its bytes.")
        ], new RsUnsafe(new RsBlock([], view)))) {
          Attributes = ["allow(unsafe_code)", "cfg(target_endian = \"little\")"]
        };
      });
      return name;
    }

  }

}
