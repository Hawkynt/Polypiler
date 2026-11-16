using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Backends.Testing;

/// <summary>
/// The Rust backend as a test calls it: an IR module through the pipeline <c>translate</c> runs -
/// the optimizer, legalization with the precomputed libraries, the passes over the legalized module,
/// the emitter with its target passes - into the files of a crate.
/// </summary>
internal static class RustCrates {

  /// <summary>Imports C# methods wrapped into the class <c>Snippet</c>.</summary>
  public static IrModule Import(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  /// <summary>A module as <c>translate --to rust</c> prepares it, without emitting it: optimized and legalized for the profile.</summary>
  public static IrModule Prepared(IrModule module, RustProfile? profile = null, OptimizationLevel level = OptimizationLevel.Default, RustCapabilities? capabilities = null, IReadOnlySet<string>? disabled = null) {
    var options = new OptimizerOptions { Level = level, Target = new RuleTarget(false) };
    var optimized = Optimizer.Run(module, options).Module;
    var lowered = RustEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile ?? RustProfile.Default, capabilities);
    return RustEmitter.Check(lowered, profile ?? RustProfile.Default, capabilities).Count > 0 ? lowered
      : IrPassPipeline.Default.Run(lowered, new OptimizerOptions { Level = level, Disabled = disabled ?? new HashSet<string>() }).Module;
  }

  /// <summary>Why a module cannot be emitted for a profile once prepared; empty when it can.</summary>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, RustProfile? profile = null, RustCapabilities? capabilities = null)
    => RustEmitter.Check(Prepared(module, profile, capabilities: capabilities), profile ?? RustProfile.Default, capabilities);

  /// <summary>The files of the crate of a module: <c>Cargo.toml</c>, <c>src/lib.rs</c> and the module's file.</summary>
  /// <param name="disabled">The passes of the pipeline after legalization not to run, as <c>--disable-opt</c> names them.</param>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, RustProfile? profile = null, RustEmitOptions? options = null, OptimizationLevel level = OptimizationLevel.Default, RustCapabilities? capabilities = null, IReadOnlySet<string>? disabled = null)
    => RustEmitter.Emit(Prepared(module, profile, level, capabilities, disabled), profile ?? RustProfile.Default, capabilities,
      level == OptimizationLevel.None ? TargetPassOptions.None : TargetPassOptions.Default, options).Files;

  /// <summary>The text of the module's file of a crate.</summary>
  public static string ModuleText(IReadOnlyList<GeneratedFile> files) => files.Single(file => file.Path.StartsWith("src/", StringComparison.Ordinal) && file.Path is not ("src/lib.rs" or "src/error.rs")).Content;

  /// <summary>The text of a file of a crate.</summary>
  public static string Text(IReadOnlyList<GeneratedFile> files, string path) => files.Single(file => file.Path == path).Content;

  /// <summary>The files of a crate as a plan of the runner carries them.</summary>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Package(IReadOnlyList<GeneratedFile> files)
    => files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Content), StringComparer.Ordinal);

}
