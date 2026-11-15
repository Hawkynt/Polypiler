using System.Collections.Generic;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Backends.Testing;

/// <summary>
/// The C# backend as a test calls it on a module it built or imported: the helpers of the library the
/// profile needs are bound into the module first (<see cref="CSharpEmitter.LegalizeHelpers"/>), as
/// <c>translate</c> does; the floating-point operations stay for the test to legalize or not.
/// </summary>
internal static class LegalizedCSharp {

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, string? @namespace = null)
    => CSharpEmitter.Emit(Helpers(module, CSharpProfile.Default, null), @namespace);

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CSharpProfile profile, string? @namespace = null, CSharpCapabilities? capabilities = null)
    => CSharpEmitter.Emit(Helpers(module, profile, capabilities), profile, @namespace, capabilities);

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace = null, CSharpCapabilities? capabilities = null)
    => CSharpEmitter.Emit(Helpers(module, profile, capabilities), profile, mode, @namespace, capabilities);

  public static TargetEmission Emit(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace, CSharpCapabilities? capabilities, TargetPassOptions passes)
    => CSharpEmitter.Emit(Helpers(module, profile, capabilities), profile, mode, @namespace, capabilities, passes);

  public static TargetPipelineResult<Polypiler.Core.Backends.CSharp.Syntax.CsCompilationUnit> Build(
    IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace, CSharpCapabilities? capabilities, TargetPassOptions passes)
    => CSharpEmitter.Build(Helpers(module, profile, capabilities), profile, mode, @namespace, capabilities, passes);

  public static IReadOnlyList<Diagnostic> Check(IrModule module) => Check(module, CSharpProfile.Default);

  public static IReadOnlyList<Diagnostic> Check(IrModule module, CSharpProfile profile, CSharpCapabilities? capabilities = null)
    => CSharpEmitter.Check(Helpers(module, profile, capabilities), profile, capabilities);

  /// <summary>The module with the helpers bound in; a module the backend cannot read or the verifier refuses is left as it is, for the backend to report.</summary>
  public static IrModule Helpers(IrModule module, CSharpProfile profile, CSharpCapabilities? capabilities) {
    if (IrVerifier.Verify(module).Count > 0)
      return module;

    try {
      return CSharpEmitter.LegalizeHelpers(module, () => PrecomputedImplementations.Module, profile, capabilities);
    } catch (System.Exception exception) when (exception is System.ArgumentException or System.InvalidCastException or System.InvalidOperationException) {
      return module;
    }
  }

}

/// <summary>The JavaScript backend as a test calls it on a module it built or imported, the helpers of the library bound in first (<see cref="JavaScriptEmitter.LegalizeHelpers"/>).</summary>
internal static class LegalizedJavaScript {

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, JavaScriptProfile.Default);

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null)
    => JavaScriptEmitter.Emit(Helpers(module, profile, capabilities), profile, capabilities);

  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities = null)
    => JavaScriptEmitter.Emit(Helpers(module, profile, capabilities), profile, mode, capabilities);

  public static TargetEmission Emit(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities, TargetPassOptions passes)
    => JavaScriptEmitter.Emit(Helpers(module, profile, capabilities), profile, mode, capabilities, passes);

  public static TargetPipelineResult<Polypiler.Core.Backends.JavaScript.Syntax.JsProgram> Build(
    IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities, TargetPassOptions passes)
    => JavaScriptEmitter.Build(Helpers(module, profile, capabilities), profile, mode, capabilities, passes);

  public static IReadOnlyList<Diagnostic> Check(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null)
    => JavaScriptEmitter.Check(Helpers(module, profile, capabilities), profile, capabilities);

  /// <summary>The module with the helpers bound in; a module the verifier refuses is left as it is, for the backend to report.</summary>
  public static IrModule Helpers(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities? capabilities) {
    if (IrVerifier.Verify(module).Count > 0)
      return module;

    try {
      return JavaScriptEmitter.LegalizeHelpers(module, () => PrecomputedImplementations.Module, profile, capabilities);
    } catch (System.Exception exception) when (exception is System.ArgumentException or System.InvalidCastException or System.InvalidOperationException) {
      return module;
    }
  }

}
