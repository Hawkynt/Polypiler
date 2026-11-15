// Native functions that are not there, for #1332: a library no system has, and an entry point the C library of
// each operating system lacks. .NET throws at the first call - DllNotFoundException and EntryPointNotFoundException -
// and the translated C# keeps that. Written for this repository.
using System.Runtime.InteropServices;

namespace Polypiler.Fixtures.Prototype.Interop;

public static class Missing {

  [DllImport("polypiler-no-such-library")]
  public static extern int NoLibrary();

  [DllImport("libc.so.6", EntryPoint = "polypiler_no_such_function")]
  public static extern int NoEntryLinux();

  [DllImport("msvcrt", EntryPoint = "polypiler_no_such_function")]
  public static extern int NoEntryWindows();

}
