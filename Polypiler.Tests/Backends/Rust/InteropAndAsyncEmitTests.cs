using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Cli;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Emission, syntax round-trip, and live <c>rustc</c> verification for <see cref="IrUsing"/>,
/// <see cref="IrAwait"/>/<see cref="IrAsync"/>, <see cref="IrWriteLine"/>, and
/// <see cref="IrForeignCall"/>/<see cref="IrForeignFunction"/> across all Rust profiles.
/// </summary>
[TestFixture]
public sealed class InteropAndAsyncEmitTests {

  private static string RepoRoot {
    get {
      var directory = AppContext.BaseDirectory;
      while (directory is not null && !File.Exists(Path.Combine(directory, "Polypiler.slnx")))
        directory = Path.GetDirectoryName(directory);
      return directory ?? throw new InvalidOperationException("Could not locate Polypiler.slnx.");
    }
  }

  private static IrModule ImportShowcase(string fileName) {
    var path = Path.Combine(RepoRoot, "docs", "showcase", fileName);
    var source = File.ReadAllText(path);
    return PrototypeFixture.Import(fileName, source);
  }

  private static IrModule Legalize(IrModule module, RustProfile profile)
    => RustEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile);

  private static (string LibRs, string ModuleRs, string ModuleName, string CargoToml) EmitAll(IrModule module, RustProfile profile) {
    var legalized = Legalize(module, profile);
    Assert.That(RustEmitter.Check(legalized, profile), Is.Empty, profile.Name);
    var files = RustEmitter.Emit(legalized, profile, null, TargetPassOptions.Default, null).Files;
    var cargo = files.First(f => f.Path == "Cargo.toml").Content;
    var lib = files.First(f => f.Path == "src/lib.rs").Content;
    var modFile = files.First(f => f.Path.StartsWith("src/", StringComparison.Ordinal) && f.Path != "src/lib.rs");
    var modName = Path.GetFileNameWithoutExtension(modFile.Path);
    // Every emitted Rust module must round-trip through RustSyntaxReader.
    var parsed = RustSyntaxReader.Read(modFile.Content);
    Assert.That(parsed.Items, Is.Not.Empty, profile.Name);
    Assert.That(cargo, Does.Not.Contain("[dependencies]"), profile.Name);
    return (lib, modFile.Content, modName, cargo);
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string arguments, string workingDirectory) {
    using var process = new Process {
      StartInfo = new ProcessStartInfo {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      }
    };
    process.Start();
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
  }

  private static void CompileAndOptionallyRun(string libRs, string moduleRs, string moduleName, RustProfile profile, string? driverMain, Action<string>? assertOutput = null) {
    var dir = Path.Combine(Path.GetTempPath(), "ply-rs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(dir, "src"));
    try {
      File.WriteAllText(Path.Combine(dir, "src", "lib.rs"), libRs);
      File.WriteAllText(Path.Combine(dir, "src", moduleName + ".rs"), moduleRs);
      var libPath = Path.Combine(dir, "libcrate_under_test.rlib");
      var compileLib = RunProcess("rustc", $"--edition {profile.Edition} --crate-type lib --crate-name crate_under_test -D warnings -o \"{libPath}\" \"{Path.Combine(dir, "src", "lib.rs")}\"", dir);
      Assert.That(compileLib.ExitCode, Is.Zero, $"rustc lib failed ({profile.Name}):\n{compileLib.Stderr}\n--- lib.rs ---\n{libRs}\n--- {moduleName}.rs ---\n{moduleRs}");

      if (driverMain is null)
        return;

      var mainRs = Path.Combine(dir, "main.rs");
      var exePath = Path.Combine(dir, "driver");
      File.WriteAllText(mainRs, driverMain);
      var compileBin = RunProcess("rustc", $"--edition {profile.Edition} --extern crate_under_test=\"{libPath}\" -D warnings -o \"{exePath}\" \"{mainRs}\"", dir);
      Assert.That(compileBin.ExitCode, Is.Zero, $"rustc bin failed ({profile.Name}):\n{compileBin.Stderr}");

      var run = RunProcess(exePath, "", dir);
      Assert.That(run.ExitCode, Is.Zero, $"binary failed ({profile.Name}):\n{run.Stderr}\nstdout:\n{run.Stdout}");
      assertOutput?.Invoke(run.Stdout);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase15_ExceptionsAndUsing_CompilesAndRunsOnAllProfiles() {
    var module = ImportShowcase("15-exceptions-and-using.cs");
    foreach (var profile in RustProfile.All) {
      var (lib, modRs, modName, _) = EmitAll(module, profile);
      Assert.That(lib, Does.Contain("#![forbid(unsafe_code)]"), profile.Name);
      var driver = $$"""
        fn main() {
            let normal = crate_under_test::{{modName}}::evaluate(5);
            let caught = crate_under_test::{{modName}}::evaluate(-3);
            assert_eq!(normal, 11);
            assert_eq!(caught, 0);
            println!("{} {}", normal, caught);
        }
        """;
      CompileAndOptionallyRun(lib, modRs, modName, profile, driver, stdout => Assert.That(stdout.Trim(), Is.EqualTo("11 0")));
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase16_TasksAndAsync_CompilesAndRunsOnAllProfiles() {
    var module = ImportShowcase("16-tasks-and-async.cs");
    foreach (var profile in RustProfile.All) {
      var (lib, modRs, modName, _) = EmitAll(module, profile);
      Assert.That(lib, Does.Contain("#![forbid(unsafe_code)]"), profile.Name);
      Assert.That(modRs, Does.Contain("pub async fn combine_async"), profile.Name);
      Assert.That(modRs, Does.Contain(".await"), profile.Name);
      var driver = $$"""
        use std::future::Future;
        use std::pin::Pin;
        use std::ptr;
        use std::task::{Context, Poll, RawWaker, RawWakerVTable, Waker};

        fn block_on<F: Future>(mut fut: F) -> F::Output {
            fn no_op(_: *const ()) {}
            fn clone(p: *const ()) -> RawWaker { RawWaker::new(p, &VTABLE) }
            static VTABLE: RawWakerVTable = RawWakerVTable::new(clone, no_op, no_op, no_op);
            let waker = unsafe { Waker::from_raw(RawWaker::new(ptr::null(), &VTABLE)) };
            let mut cx = Context::from_waker(&waker);
            let mut pinned = unsafe { Pin::new_unchecked(&mut fut) };
            loop {
                if let Poll::Ready(val) = pinned.as_mut().poll(&mut cx) {
                    return val;
                }
            }
        }

        fn main() {
            let result = block_on(crate_under_test::{{modName}}::combine_async(3, 4));
            assert_eq!(result, 27);
            println!("{}", result);
        }
        """;
      CompileAndOptionallyRun(lib, modRs, modName, profile, driver, stdout => Assert.That(stdout.Trim(), Is.EqualTo("27")));
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase17_Stdio_CompilesAndRunsOnAllProfiles() {
    var module = ImportShowcase("17-stdio.cs");
    foreach (var profile in RustProfile.All) {
      var (lib, modRs, modName, _) = EmitAll(module, profile);
      Assert.That(lib, Does.Contain("#![forbid(unsafe_code)]"), profile.Name);
      Assert.That(modRs, Does.Contain("println!"), profile.Name);
      var driver = $$"""
        fn main() {
            let result = crate_under_test::{{modName}}::run();
            assert_eq!(result, 14);
        }
        """;
      CompileAndOptionallyRun(lib, modRs, modName, profile, driver, stdout => {
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n');
        Assert.That(lines, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }), profile.Name);
      });
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase19_NativeInterop_CompilesWithZeroWarningsOnAllProfiles() {
    var module = ImportShowcase("19-native-interop.cs");
    foreach (var profile in RustProfile.All) {
      var (lib, modRs, modName, _) = EmitAll(module, profile);
      Assert.That(lib, Does.Contain("#![deny(unsafe_code)]"), profile.Name);
      Assert.That(lib, Does.Contain("Unsafe code:"), profile.Name);
      Assert.That(modRs, Does.Contain("#[repr(C)]"), profile.Name);
      Assert.That(modRs, Does.Contain("#[link(name = \"geom\")]"), profile.Name);
      Assert.That(modRs, Does.Contain(profile.Edition >= 2024 ? "unsafe extern \"C\"" : "extern \"C\""), profile.Name);
      Assert.That(modRs, Does.Contain("#[allow(unsafe_code)]"), profile.Name);
      CompileAndOptionallyRun(lib, modRs, modName, profile, null);
    }
  }

  [Test]
  public void IrUsing_HandlesNestedAndControlFlowAndNullableResources() {
    var source = """
      using System;
      using System.Collections.Generic;

      public sealed class Tracker : IDisposable {
        public int Count;
        public void Dispose() => this.Count += 1;
      }

      public sealed class CustomException : Exception {
        public CustomException(string m) : base(m) { }
      }

      public static class UsingEdges {
        private static IEnumerable<int> Seq() {
          yield return 1;
        }

        public static int NestedAndEarlyExit(int n) {
          var a = new Tracker();
          var b = new Tracker();
          if (n == 0) {
            using (a) {
              using (b) {
                return a.Count + b.Count;
              }
            }
          }
          var i = 0;
          while (i < 2) {
            ++i;
            using (a) {
              if (i == 1) continue;
              break;
            }
          }
          do {
            using (b) { break; }
          } while (false);
          for (var j = 0; j < 1; ++j) {
            using (b) { }
          }
          switch (n) {
            case 1:
              using (a) { break; }
            default:
              using (b) { break; }
          }
          try {
            using (a) {
              if (n == 99)
                throw new CustomException("fail");
            }
          } catch (CustomException) {
            using (b) { }
          } finally {
            using (b) { }
          }
          Tracker? maybe = n > 10 ? a : null;
          using (maybe) { }
          var list = new List<int> { 1 };
          foreach (var x in list) {
            using (a) { }
          }
          foreach (var y in Seq()) {
            using (b) { }
          }
          return a.Count + b.Count;
        }
      }
      """;
    var module = PrototypeFixture.Import("UsingEdges.cs", source);
    var (_, modRs, _, _) = EmitAll(module, RustProfile.Rust2024);
    Assert.That(modRs, Does.Contain("dispose"), modRs);
  }

  [Test]
  public void IrWriteLine_HandlesLiteralsNullsNullableStringsAndLoneSurrogates() {
    var msg = new IrLocal("msg", IrType.String);
    var nullable = new IrLocal("nullable", IrType.String);
    var run = new IrFunction("Run", true, IrType.Void, [msg], [nullable], new IrBlock([
      new IrWriteLine(new IrNull(IrType.String)),
      new IrWriteLine(new IrStrConst("")),
      new IrWriteLine(new IrStrConst("hello {world}")),
      new IrWriteLine(new IrLocalRef(msg)),
      new IrDeclare(nullable, new IrNull(IrType.String)),
      new IrWriteLine(new IrLocalRef(nullable)),
      new IrWriteLine(new IrCall("MaybeText", IrType.String, [])),
      new IrReturn(null)
    ]));
    var maybeText = new IrFunction("MaybeText", false, IrType.String, [], [], new IrBlock([new IrReturn(new IrNull(IrType.String))]));
    var module = new IrModule("WriteEdges", [run, maybeText]);
    var (_, modRs, _, _) = EmitAll(module, RustProfile.Rust2024);
    Assert.That(modRs, Does.Contain("println!();"));
    Assert.That(modRs, Does.Contain("println!(\"{}\", \"hello {world}\");"));
    Assert.That(modRs, Does.Contain("String::from_utf16_lossy"));
    Assert.That(modRs, Does.Contain("unwrap_or_default()"));

    // Unpaired surrogate in a WriteLine literal is refused.
    var bad = new IrModule("BadWrite", [new IrFunction("Bad", true, IrType.Void, [], [], new IrBlock([
      new IrWriteLine(new IrStrConst("\uD800")),
      new IrReturn(null)
    ]))]);
    Assert.That(RustEmitter.Check(bad, RustProfile.Rust2024), Has.Some.Matches<Core.Diagnostic>(d => d.Code == RustEmitter.Inexpressible));
  }

  [Test]
  public void Async_CoversCompletedFromResultWhenAllVoidEmptyAndThrowing() {
    var source = """
      using System;
      using System.Threading.Tasks;

      public sealed class FailException : Exception {
        public FailException(string m) : base(m) { }
      }

      public sealed class Box {
        public int V;
        public Box(int v) => this.V = v;
      }

      public static class AsyncEdges {
        private static async Task PingAsync() {
          await Task.CompletedTask;
        }

        private static async Task<int> PureAsync(int x)
          => await Task.FromResult(x * 2);

        private static async Task<int> MayFailAsync(int x) {
          if (x < 0)
            throw new FailException("bad");
          return await Task.FromResult(x + 1);
        }

        private static async Task<string> StrAsync(string s)
          => await Task.FromResult(s);

        private static async Task<int[]> ArrayAsync(int n)
          => await Task.FromResult(new int[] { n, n + 1 });

        private static async Task<Box?> BoxAsync(bool ok) {
          Box? result = ok ? new Box(7) : null;
          return await Task.FromResult(result);
        }

        public static async Task<int> RunAllAsync(int x) {
          await Task.WhenAll(PingAsync(), PingAsync());
          int[] empty = await Task.WhenAll<int>();
          int[] arr = await ArrayAsync(x);
          string s = await StrAsync("hi");
          Box? b = await BoxAsync(x > 0);
          int[] pair = await Task.WhenAll(PureAsync(x), PureAsync(x + 1));
          int sum = empty.Length + arr[0] + s.Length + (b != null ? b.V : 0) + pair[0] + pair[1];
          try {
            int checkedVal = await MayFailAsync(x);
            return sum + checkedVal;
          } catch (FailException) {
            return -1;
          }
        }
      }
      """;
    var module = PrototypeFixture.Import("AsyncEdges.cs", source);
    var (_, modRs, _, _) = EmitAll(module, RustProfile.Rust2024);
    Assert.That(modRs, Does.Contain("async fn ping_async"));
    Assert.That(modRs, Does.Contain("Vec::new()"));
    Assert.That(modRs, Does.Contain(".await"));

    // Nullable string returned through await Task.FromResult(null)
    var nullStrAsync = new IrModule("NullStrAsync", [
      new IrFunction("Maybe", true, IrType.Async(IrType.String), [], [], new IrBlock([
        new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.FromResult, IrType.Async(IrType.String), [new IrNull(IrType.String)])))
      ]))
    ]);
    var (_, nullStrRs, _, _) = EmitAll(nullStrAsync, RustProfile.Rust2024);
    Assert.That(nullStrRs, Does.Contain("Option<Vec<u16>>"));
  }

  [Test]
  [Category("ExternalInterop")]
  public void ForeignMarshalling_CoversAllKindsAndConventionsAndCompiles() {
    var pointType = new IrStructType("Pt", [new IrField("X", IrType.I32, false), new IrField("Y", IrType.I32, false)]) {
      Layout = new IrStructLayout(IrLayoutKind.Sequential, 0, 0, [])
    };
    var ptIr = IrType.Struct(pointType);

    var fAll = new IrForeignFunction("AllKinds", true, "nativelib", IrType.Bool, [
      new IrForeignParameter("n", IrType.I32, IrMarshal.Blittable),
      new IrForeignParameter("ns", IrType.I64, new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }),
      new IrForeignParameter("nu", IrType.U64, new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }),
      new IrForeignParameter("pt", ptIr, new IrMarshal(IrMarshalKind.Struct)),
      new IrForeignParameter("h", IrType.I64, new IrMarshal(IrMarshalKind.Handle)),
      new IrForeignParameter("b4", IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool }),
      new IrForeignParameter("bu1", IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }),
      new IrForeignParameter("bi1", IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 }),
      new IrForeignParameter("arrIn", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.In)),
      new IrForeignParameter("arrOut", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.Out)),
      new IrForeignParameter("refIn", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.In)),
      new IrForeignParameter("refOut", IrType.Array(ptIr), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out)),
      new IrForeignParameter("refNsIn", IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.In) { NativeSize = true }),
      new IrForeignParameter("refNsOut", IrType.Array(IrType.U64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out) { NativeSize = true }),
      new IrForeignParameter("refNsInOut", IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.InOut) { NativeSize = true }),
      new IrForeignParameter("sUni", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Unicode }),
      new IrForeignParameter("sUtf8", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Utf8 }),
      new IrForeignParameter("sAnsi", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi })
    ]) { EntryPoint = "c_all_kinds", Result = new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool } };

    var fVoid = new IrForeignFunction("DoWork", false, "nativelib", IrType.Void, [
      new IrForeignParameter("outRef", IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out) { NativeSize = true })
    ]) { Convention = IrCallingConvention.Cdecl };

    var fHandle = new IrForeignFunction("MakeHandle", true, "nativelib", IrType.I64, []) {
      Result = new IrMarshal(IrMarshalKind.Handle),
      Convention = IrCallingConvention.StdCall
    };

    var fNativeRet = new IrForeignFunction("GetSize", true, "otherlib", IrType.U64, []) {
      Result = new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true },
      Convention = IrCallingConvention.FastCall
    };

    var fSignedNativeRet = new IrForeignFunction("GetOffset", true, "otherlib", IrType.I64, []) {
      Result = new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }
    };

    var fStructRet = new IrForeignFunction("GetPt", true, "otherlib", ptIr, []) {
      Result = new IrMarshal(IrMarshalKind.Struct),
      Convention = IrCallingConvention.ThisCall
    };

    var fUnused = new IrForeignFunction("NeverCalled", false, "unusedlib", IrType.I32, []);

    var aIn = new IrLocal("aIn", IrType.Array(IrType.I32));
    var aOut = new IrLocal("aOut", IrType.Array(IrType.I32));
    var rIn = new IrLocal("rIn", IrType.Array(IrType.I32));
    var rPt = new IrLocal("rPt", IrType.Array(ptIr));
    var rNs = new IrLocal("rNs", IrType.Array(IrType.I64));
    var rNu = new IrLocal("rNu", IrType.Array(IrType.U64));
    var rIo = new IrLocal("rIo", IrType.Array(IrType.I64));
    var s = new IrLocal("s", IrType.String);
    var caller = new IrFunction("CallAll", true, IrType.Bool, [aIn, aOut, s], [rIn, rPt, rNs, rNu, rIo], new IrBlock([
      new IrDeclare(rIn, new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 1))),
      new IrDeclare(rPt, new IrNewArray(IrType.Array(ptIr), new IrConst(IrType.I32, 1))),
      new IrDeclare(rNs, new IrNewArray(IrType.Array(IrType.I64), new IrConst(IrType.I32, 1))),
      new IrDeclare(rNu, new IrNewArray(IrType.Array(IrType.U64), new IrConst(IrType.I32, 1))),
      new IrDeclare(rIo, new IrNewArray(IrType.Array(IrType.I64), new IrConst(IrType.I32, 1))),
      new IrExprStmt(new IrForeignCall("DoWork", IrType.Void, [new IrLocalRef(rNs)])),
      new IrReturn(new IrForeignCall("AllKinds", IrType.Bool, [
        new IrConst(IrType.I32, 1), new IrConst(IrType.I64, 2), new IrConst(IrType.U64, 3),
        new IrStructNew(ptIr, [new IrConst(IrType.I32, 4), new IrConst(IrType.I32, 5)]),
        new IrConst(IrType.I64, 6),
        new IrConst(IrType.Bool, 1), new IrConst(IrType.Bool, 0), new IrConst(IrType.Bool, 1),
        new IrLocalRef(aIn), new IrLocalRef(aOut),
        new IrLocalRef(rIn), new IrLocalRef(rPt),
        new IrLocalRef(rNs), new IrLocalRef(rNu), new IrLocalRef(rIo),
        new IrLocalRef(s), new IrLocalRef(s), new IrLocalRef(s)
      ]))
    ]));

    var module = new IrModule("ForeignAll", [caller]) {
      Types = [pointType],
      ForeignFunctions = [fAll, fVoid, fHandle, fNativeRet, fSignedNativeRet, fStructRet, fUnused]
    };

    var (lib, modRs, modName, _) = EmitAll(module, RustProfile.Rust2024);
    Assert.That(modRs, Does.Not.Contain("never_called"));
    Assert.That(modRs, Does.Contain("link_name = \"c_all_kinds\""));
    Assert.That(modRs, Does.Contain("extern \"C\""));
    Assert.That(modRs, Does.Contain("extern \"stdcall\""));
    Assert.That(modRs, Does.Contain("extern \"fastcall\""));
    Assert.That(modRs, Does.Contain("extern \"thiscall\""));
    Assert.That(modRs, Does.Contain("*const u16"));
    Assert.That(modRs, Does.Contain("*const u8"));
    Assert.That(RustEmitter.UnsafeLedger(module, RustProfile.Rust2024), Is.Not.Empty);

    // Compile the C-ABI subset on linux (stdcall/fastcall/thiscall are non-x86_64 ABIs on linux).
    var cOnly = module with { ForeignFunctions = [fAll, fVoid, fUnused] };
    var (cLib, cMod, cModName, _) = EmitAll(cOnly, RustProfile.Rust2024);
    CompileAndOptionallyRun(cLib, cMod, cModName, RustProfile.Rust2024, null);
    var (cLib21, cMod21, cModName21, _) = EmitAll(cOnly, RustProfile.Rust2021);
    CompileAndOptionallyRun(cLib21, cMod21, cModName21, RustProfile.Rust2021, null);
  }

  [Test]
  public void ForeignRefusals_ReportPLY1550ForLastErrorSetLastErrorAndPointerParam() {
    var lastErrMod = new IrModule("LastErr", [
      new IrFunction("Get", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrLastError(true))]))
    ]);
    Assert.That(RustEmitter.Check(lastErrMod, RustProfile.Rust2024), Has.Some.Matches<Core.Diagnostic>(d => d.Code == RustEmitter.Inexpressible && d.Message.Contains("LastError")));

    var setLastErr = new IrForeignFunction("Open", true, "kernel32", IrType.I32, []) { SetLastError = true };
    var setLastMod = new IrModule("SetLast", []) { ForeignFunctions = [setLastErr] };
    Assert.That(RustEmitter.Check(setLastMod, RustProfile.Rust2024), Has.Some.Matches<Core.Diagnostic>(d => d.Code == RustEmitter.Inexpressible && d.Message.Contains("SetLastError")));

    var ptrFn = new IrForeignFunction("Raw", true, "clib", IrType.Void, [new IrForeignParameter("p", IrType.Pointer(IrType.U8), IrMarshal.Blittable)]);
    var ptrMod = new IrModule("PtrForeign", []) { ForeignFunctions = [ptrFn] };
    Assert.That(RustEmitter.Check(ptrMod, RustProfile.Rust2024), Has.Some.Matches<Core.Diagnostic>(d => d.Code == RustEmitter.Inexpressible && d.Message.Contains("pointer")));
  }

  [Test]
  public void RustSyntax_RoundTripsAsyncFnAwaitAndExternBlocks() {
    var externEmpty = new RsExternBlock("C", false, []);
    Assert.That(RustPrinter.Print(externEmpty), Is.EqualTo("extern \"C\" {}\n"));

    var externUnsafe = new RsExternBlock("C", true, [
      new RsFunction("raw_op", "", [new RsParameter("p", "*const u8"), new RsParameter("q", "*mut i32")], "i32", new RsBlock([], null) { IsDeclaration = true })
    ]) { Attributes = ["link(name = \"c\")", "allow(unsafe_code)"] };
    var printedExtern = RustPrinter.Print(externUnsafe);
    Assert.That(printedExtern, Does.Contain("unsafe extern \"C\""));
    var reparsedExtern = RustSyntaxReader.Read(printedExtern);
    Assert.That(reparsedExtern.Items.Single(), Is.TypeOf<RsExternBlock>());

    var awaited = new RsAwait(new RsCall(new RsPath("work"), [new RsCast(new RsPath("p"), "*const u8")]));
    var longAwaited = new RsAwait(new RsCall(new RsPath("very_long_async_function_name_to_force_line_breaking_in_printer"), [
      new RsPath("first_argument_value"), new RsPath("second_argument_value"), new RsPath("third_argument_value")
    ]));
    var asyncFn = new RsFunction("driver", "pub", [new RsParameter("p", "*const u8")], "i32", new RsBlock([
      new RsExpressionStatement(new RsIf(new RsAwait(new RsStructLiteral("Fut", [])), RsBlock.Empty, null)),
      new RsExpressionStatement(longAwaited)
    ], awaited)) { IsAsync = true };
    var printedFn = RustPrinter.Print(asyncFn);
    Assert.That(printedFn, Does.Contain("pub async fn driver"));
    Assert.That(printedFn, Does.Contain(".await"));
    var parsedFn = RustSyntaxReader.Read(printedFn);
    Assert.That(((RsFunction)parsedFn.Items.Single()).IsAsync, Is.True);
    // Exercise RustSyntaxRewriter.Children and Rebuild over both RsExternBlock and RsAwait.
    var rewrittenExtern = RustSyntaxRewriter.Rewrite(externUnsafe, node => node is RsFunction fn ? fn with { Name = fn.Name + "_v2" } : node);
    Assert.That(rewrittenExtern.Items[0].Name, Is.EqualTo("raw_op_v2"));
    var rewrittenAsync = RustSyntaxRewriter.Rewrite(asyncFn, node => node is RsPath { Name: "work" } ? new RsPath("work_v2") : node);
    Assert.That(RustPrinter.Print(rewrittenAsync), Does.Contain("work_v2("));
  }

}
