using System.Collections.Generic;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for native calls (#1332). <c>DllImportAttribute</c> is in every framework since
/// .NET Framework 1.0, so every profile writes it. The rest follow the "Applies to" tables of the .NET API reference,
/// checked against the reference assemblies of every profile on 2026-11-14: <c>LibraryImportAttribute</c> and
/// <c>Marshal.GetLastPInvokeError</c> came with .NET 7 and .NET 6 (net8.0 in the list of profiles);
/// <c>UnmanagedType.LPUTF8Str</c> with .NET Framework 4.7 and .NET Standard 2.1, so it is in net4.8, netstandard2.1 and
/// later, and not in the <c>netstandard.dll</c> of .NET Standard 2.0.
/// </summary>
internal static class CSharpInteropRows {

  /// <summary>The name of the row of <c>[LibraryImport]</c>.</summary>
  public const string LibraryImport = "LibraryImportAttribute";

  /// <summary>The name of the row of <c>UnmanagedType.LPUTF8Str</c>.</summary>
  public const string Utf8String = "UnmanagedType.LPUTF8Str";

  /// <summary>The name of the row of <c>Marshal.GetLastPInvokeError</c>.</summary>
  public const string LastPInvokeError = "Marshal.GetLastPInvokeError";

  /// <summary>The name of the row of <c>[DllImport]</c>.</summary>
  public const string DllImport = "DllImportAttribute";

  /// <summary>Every row of the native calls.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfApi(DllImport, CSharpProfile.Net20),
    CSharpCapability.OfApi(LibraryImport, CSharpProfile.Net80),
    CSharpCapability.OfApi(Utf8String, CSharpProfile.Net48, [CSharpProfile.NetStandard20]),
    CSharpCapability.OfApi(LastPInvokeError, CSharpProfile.Net80)
  ];

}
