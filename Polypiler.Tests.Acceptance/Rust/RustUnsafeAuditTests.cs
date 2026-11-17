using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The unsafe code of a generated crate (#460): a mutable overlay view, the one lowering that needs it, emitted
/// under <c>allowUnsafe</c> - the crate builds, passes clippy with <c>-D warnings</c> and <c>cargo fmt --check</c>,
/// its generated test gives the value the bytes say, and it passes Miri on the dated nightly <c>rust-miri</c>,
/// which the CI layer installs; where the nightly is missing the Miri test is ignored, naming it. The audit: no
/// crate the backend writes without <c>allowUnsafe</c> needs Miri.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class RustUnsafeAuditTests {

  /// <summary>The <c>u32</c> 0xAABBCC00, 1 written to its lowest byte through a writable view of its bytes, read back.</summary>
  private static IrModule Overlay() {
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

  /// <summary>The crate of the overlay with unsafe code allowed and a test that the write reached the number: 0xAABBCC01 on a little-endian host.</summary>
  private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> UnsafeCrate()
    => RustCrates.Package(RustEmitter.Emit(Overlay(), RustProfile.Rust2024.WithUnsafe(true), null, Polypiler.Core.Optimize.Target.TargetPassOptions.Default,
      new RustEmitOptions { Tests = [new RustTestCase("overlay_writes_through_the_bytes", "Overlay", [], new IrConst(IrType.U32, 0xAABBCC01))] }).Files);

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task UnsafeCrate_BuildsLintsFormats_AndItsTestPasses() {
    var crate = UnsafeCrate();
    Assert.That(RustCommands.NeedsMiri(crate), Is.True);
    var commands = RustLayerRun.Commands;
    var result = await RustLayerRun.RunAsync(crate, commands.Test(), commands.Format, commands.Clippy);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), RustLayerRun.Stderr(result));
    Assert.That(result.ExitCode, Is.Zero, RustLayerRun.Stderr(result));
    Assert.That(RustCommands.Passed(RustLayerRun.Stdout(result)), Is.EqualTo(1));
  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task UnsafeCrate_PassesMiri() {
    RustLayerRun.Require(RustCommands.MiriToolchain);
    var result = await RustLayerRun.RunAsync(UnsafeCrate(), RustLayerRun.Commands.Miri());
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), RustLayerRun.Stderr(result));
    Assert.That(result.ExitCode, Is.Zero, RustLayerRun.Stderr(result));
    Assert.That(RustCommands.Passed(RustLayerRun.Stdout(result)), Is.EqualTo(1));
    Assert.That(RustCommands.Failed(RustLayerRun.Stdout(result)), Is.Zero);
  }

  /// <summary>Every prototype fixture and pointer fixture the backend writes, as the CI layer would decide: none needs Miri.</summary>
  [Test]
  public void CrateWithoutAllowUnsafe_NeverNeedsMiri() {
    var modules = PrototypeFixture.Names.Select(PrototypeFixture.Imported).Append(PointerFixtures.Imported("Fnv1aPointer"));
    foreach (var module in modules.Where(module => RustCrates.Check(module).Count == 0))
      Assert.That(RustCommands.NeedsMiri(RustCrates.Package(RustCrates.Emit(module))), Is.False, module.Name);
  }

}
