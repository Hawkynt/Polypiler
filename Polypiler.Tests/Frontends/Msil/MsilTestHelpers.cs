using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.Msil;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Msil;

/// <summary>Helpers compiling C# sources to real PE32/PE32+ assemblies and synthesizing raw IL test images.</summary>
internal static class MsilTestHelpers {

  private static readonly Lazy<MetadataReference[]> _references = new(() => {
    var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
    var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "System.Private.CoreLib.dll",
      "System.Runtime.dll",
      "System.Memory.dll",
      "System.Console.dll",
      "System.Runtime.InteropServices.dll"
    };
    return [
      .. tpa.Split(Path.PathSeparator)
        .Where(p => wanted.Contains(Path.GetFileName(p)))
        .Select(p => MetadataReference.CreateFromFile(p))
    ];
  });

  /// <summary>Compiles C# source to a PE32 or PE32+ managed assembly in memory.</summary>
  public static byte[] CompileCSharp(
    string source,
    OptimizationLevel optimization = OptimizationLevel.Release,
    Platform platform = Platform.AnyCpu,
    bool checkOverflow = false,
    bool allowUnsafe = false,
    string assemblyName = "TestAsm") {
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create(
      assemblyName,
      [tree],
      _references.Value,
      new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: optimization,
        platform: platform,
        checkOverflow: checkOverflow,
        allowUnsafe: allowUnsafe));

    using var ms = new MemoryStream();
    var emitResult = compilation.Emit(ms);
    if (!emitResult.Success) {
      var errors = string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
      throw new InvalidOperationException($"Test C# compilation failed:{Environment.NewLine}{errors}{Environment.NewLine}{source}");
    }

    return ms.ToArray();
  }

  /// <summary>Imports assembly bytes, verifies IR cleanly, and checks JavaScript IIFE emission.</summary>
  public static IrModule ImportAndVerify(byte[] peBytes, MsilImportOptions? options = null, string fileName = "test.dll", bool emitJs = true) {
    var result = MsilFrontend.Import(fileName, peBytes, options);
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    Assert.That(result.Module, Is.Not.Null);
    var verifyErrors = IrVerifier.Verify(result.Module!);
    Assert.That(verifyErrors, Is.Empty, () => string.Join("; ", verifyErrors.Select(d => d.Message)));

    if (emitJs) {
      var iife = JavaScriptProfile.Parse("es2022", "iife", "M");
      var legalized = JavaScriptEmitter.Legalize(result.Module!, () => Polypiler.Cli.PrecomputedImplementations.Module, iife, FloatMode.Accurate);
      var emitted = JavaScriptEmitter.Emit(legalized, iife);
      Assert.That(emitted, Has.Count.EqualTo(1));
      Assert.That(emitted[0].Content, Does.Contain("M"));
    }

    return result.Module!;
  }

  /// <summary>Builds a minimal valid PE/CLI assembly containing a single static method with raw IL bytes.</summary>
  public static byte[] BuildRawIlAssembly(
    string methodName,
    byte[] ilBytes,
    Action<SignatureTypeEncoder>? encodeReturn = null,
    Action<ParametersEncoder>? encodeParams = null,
    int paramCount = 0,
    Action<LocalVariablesEncoder>? encodeLocals = null,
    int localCount = 0,
    Action<ExceptionRegionEncoder>? encodeRegions = null,
    int regionCount = 0,
    CorFlags corFlags = CorFlags.ILOnly,
    bool isAssembly = true,
    MethodAttributes methodAttrs = MethodAttributes.Public | MethodAttributes.Static,
    MethodImplAttributes implAttrs = MethodImplAttributes.IL | MethodImplAttributes.Managed,
    bool omitBody = false) {
    var metadata = new MetadataBuilder();
    var ilBuilder = new BlobBuilder();
    var bodyStream = new MethodBodyStreamEncoder(ilBuilder);

    metadata.AddModule(0, metadata.GetOrAddString("Raw.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    if (isAssembly)
      metadata.AddAssembly(metadata.GetOrAddString("RawAsm"), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.None);

    var coreLibRef = metadata.AddAssemblyReference(
      metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default, default, default, default);
    var objectRef = metadata.AddTypeReference(coreLibRef, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

    metadata.AddTypeDefinition(
      default, default, metadata.GetOrAddString("<Module>"), default,
      MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

    StandaloneSignatureHandle localSigHandle = default;
    if (localCount > 0 && encodeLocals is not null) {
      var localSig = new BlobBuilder();
      encodeLocals(new BlobEncoder(localSig).LocalVariableSignature(localCount));
      localSigHandle = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localSig));
    }

    var methodSig = new BlobBuilder();
    new BlobEncoder(methodSig).MethodSignature(isInstanceMethod: false)
      .Parameters(paramCount,
        ret => {
          if (encodeReturn is not null)
            encodeReturn(ret.Type());
          else
            ret.Void();
        },
        p => encodeParams?.Invoke(p));

    var rva = -1;
    if (!omitBody) {
      if (regionCount > 0 && encodeRegions is not null) {
        var enc = bodyStream.AddMethodBody(ilBytes.Length, maxStack: 8, exceptionRegionCount: regionCount, hasSmallExceptionRegions: false, localVariablesSignature: localSigHandle, attributes: MethodBodyAttributes.InitLocals);
        new BlobWriter(enc.Instructions).WriteBytes(ilBytes);
        encodeRegions(enc.ExceptionRegions);
        rva = enc.Offset;
      } else {
        var enc = bodyStream.AddMethodBody(ilBytes.Length, maxStack: 8, exceptionRegionCount: 0, hasSmallExceptionRegions: false, localVariablesSignature: localSigHandle, attributes: MethodBodyAttributes.InitLocals);
        new BlobWriter(enc.Instructions).WriteBytes(ilBytes);
        rva = enc.Offset;
      }
    }

    metadata.AddMethodDefinition(
      methodAttrs,
      implAttrs,
      metadata.GetOrAddString(methodName),
      metadata.GetOrAddBlob(methodSig),
      rva,
      MetadataTokens.ParameterHandle(1));

    metadata.AddTypeDefinition(
      TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed,
      metadata.GetOrAddString("Raw"),
      metadata.GetOrAddString("Host"),
      objectRef,
      MetadataTokens.FieldDefinitionHandle(1),
      MetadataTokens.MethodDefinitionHandle(1));

    var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
    var peBuilder = new ManagedPEBuilder(peHeaderBuilder, new MetadataRootBuilder(metadata), ilBuilder, flags: corFlags);
    var peBlob = new BlobBuilder();
    peBuilder.Serialize(peBlob);
    return peBlob.ToArray();
  }

  /// <summary>Builds a valid PE32 image with no CLI header (native DLL).</summary>
  public static byte[] BuildNativePeWithoutCli() {
    // Take a valid managed PE and zero out the CLI data directory (offset in Optional Header).
    var managed = CompileCSharp("public static class C { public static int F() => 1; }");
    var peOffset = BitConverter.ToInt32(managed, 0x3C);
    var magic = BitConverter.ToUInt16(managed, peOffset + 24);
    // DataDirectory[14] (COM Descriptor / CLI Header) offset: PE32 = 96 + 14*8 = 208, PE32+ = 112 + 14*8 = 224.
    var cliDirOffset = peOffset + 24 + (magic == 0x20B ? 224 : 208);
    Array.Clear(managed, cliDirOffset, 8);
    return managed;
  }

}
