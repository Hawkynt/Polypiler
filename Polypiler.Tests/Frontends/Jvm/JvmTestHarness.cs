using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Frontends.Jvm;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Jvm;

internal static class JvmTestHarness {

  private static string FindJdkTool(string tool) {
    var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
    foreach (var envVar in new[] { "JAVA_HOME_21_X64", "JAVA_HOME_21_ARM64", "JAVA_HOME_25_X64", "JAVA_HOME" }) {
      var home = Environment.GetEnvironmentVariable(envVar);
      if (string.IsNullOrWhiteSpace(home))
        continue;
      var candidate = Path.Combine(home, "bin", exe);
      if (File.Exists(candidate))
        return candidate;
    }

    return tool;
  }

  public static byte[] CompileClass(string className, string javaSource)
    => CompileClasses((className + ".java", javaSource)).Single(pair => pair.EntryName.EndsWith(".class", StringComparison.Ordinal)).Bytes;

  public static IReadOnlyList<(string EntryName, byte[] Bytes)> CompileClasses(params (string FileName, string Source)[] files) {
    var dir = Path.Combine(Path.GetTempPath(), "polypiler-jvm-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var paths = new List<string>(files.Length);
      foreach (var (name, source) in files) {
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, source);
        paths.Add(full);
      }

      var psi = new ProcessStartInfo(FindJdkTool("javac")) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
      psi.ArgumentList.Add("-d");
      psi.ArgumentList.Add(dir);
      foreach (var path in paths)
        psi.ArgumentList.Add(path);

      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit();
      Assert.That(proc.ExitCode, Is.Zero, $"javac failed:\n{stdout}\n{stderr}");

      return Directory.EnumerateFiles(dir, "*.class", SearchOption.AllDirectories)
        .Select(file => (Path.GetRelativePath(dir, file).Replace('\\', '/'), File.ReadAllBytes(file)))
        .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
        .ToList();
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  public static byte[] PackJar(IEnumerable<(string EntryName, byte[] Bytes)> entries) {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      foreach (var (name, data) in entries) {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
      }
    }

    return ms.ToArray();
  }

  public static IrModule ImportAndVerify(string fileName, byte[] bytes, JvmImportOptions? options = null) {
    var result = JvmFrontend.Import(fileName, bytes, options);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(result.Module, Is.Not.Null);
    var problems = IrVerifier.Verify(result.Module!);
    Assert.That(problems, Is.Empty, string.Join("\n", problems));
    return result.Module!;
  }

  public static string EmitPython(IrModule module) {
    var files = PythonEmitter.Emit(module);
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Content, Is.Not.Empty);
    return files[0].Content;
  }

  public sealed class SyntheticClassBuilder {

    private readonly List<byte[]> _cpEntries = [];
    private readonly List<byte[]> _fields = [];
    private readonly List<byte[]> _methods = [];
    private readonly List<byte[]> _classAttrs = [];
    private ushort _major = 61;
    private ushort _minor;
    private ushort _accessFlags = 0x0021;
    private ushort _thisClass;
    private ushort _superClass;

    public SyntheticClassBuilder SetVersion(ushort major, ushort minor = 0) {
      this._major = major;
      this._minor = minor;
      return this;
    }

    public SyntheticClassBuilder SetClasses(ushort thisClass, ushort superClass) {
      this._thisClass = thisClass;
      this._superClass = superClass;
      return this;
    }

    public ushort AddUtf8(string text) {
      var raw = Encoding.UTF8.GetBytes(text);
      return this.AddCpRaw(1, buf => {
        WriteU2(buf, (ushort)raw.Length);
        buf.AddRange(raw);
      });
    }

    public ushort AddRawUtf8(byte[] raw)
      => this.AddCpRaw(1, buf => {
        WriteU2(buf, (ushort)raw.Length);
        buf.AddRange(raw);
      });

    public ushort AddInt(int value)
      => this.AddCpRaw(3, buf => WriteU4(buf, unchecked((uint)value)));

    public ushort AddFloat(float value)
      => this.AddCpRaw(4, buf => WriteU4(buf, unchecked((uint)BitConverter.SingleToInt32Bits(value))));

    public ushort AddLong(long value) {
      var idx = this.AddCpRaw(5, buf => WriteU8(buf, unchecked((ulong)value)));
      this._cpEntries.Add([]);
      return idx;
    }

    public ushort AddDouble(double value) {
      var idx = this.AddCpRaw(6, buf => WriteU8(buf, unchecked((ulong)BitConverter.DoubleToInt64Bits(value))));
      this._cpEntries.Add([]);
      return idx;
    }

    public ushort AddClass(string name) {
      var utf = this.AddUtf8(name);
      return this.AddCpRaw(7, buf => WriteU2(buf, utf));
    }

    public ushort AddString(string text) {
      var utf = this.AddUtf8(text);
      return this.AddCpRaw(8, buf => WriteU2(buf, utf));
    }

    public ushort AddRef(byte tag, ushort classIdx, ushort natIdx)
      => this.AddCpRaw(tag, buf => {
        WriteU2(buf, classIdx);
        WriteU2(buf, natIdx);
      });

    public ushort AddNameAndType(string name, string desc) {
      var n = this.AddUtf8(name);
      var d = this.AddUtf8(desc);
      return this.AddCpRaw(12, buf => {
        WriteU2(buf, n);
        WriteU2(buf, d);
      });
    }

    public ushort AddMethodHandle(byte kind, ushort refIdx)
      => this.AddCpRaw(15, buf => {
        buf.Add(kind);
        WriteU2(buf, refIdx);
      });

    public ushort AddSingleIndexCp(byte tag, ushort idx)
      => this.AddCpRaw(tag, buf => WriteU2(buf, idx));

    public ushort AddDoubleIndexCp(byte tag, ushort idx1, ushort idx2)
      => this.AddCpRaw(tag, buf => {
        WriteU2(buf, idx1);
        WriteU2(buf, idx2);
      });

    public ushort AddCpRaw(byte tag, Action<List<byte>> writePayload) {
      var buf = new List<byte> { tag };
      writePayload(buf);
      this._cpEntries.Add([.. buf]);
      return (ushort)this._cpEntries.Count;
    }

    public void AddField(ushort flags, ushort nameIdx, ushort descIdx, params (ushort AttrNameIdx, byte[] Payload)[] attrs) {
      var buf = new List<byte>();
      WriteU2(buf, flags);
      WriteU2(buf, nameIdx);
      WriteU2(buf, descIdx);
      WriteU2(buf, (ushort)attrs.Length);
      foreach (var (an, payload) in attrs) {
        WriteU2(buf, an);
        WriteU4(buf, (uint)payload.Length);
        buf.AddRange(payload);
      }

      this._fields.Add([.. buf]);
    }

    public void AddMethod(ushort flags, ushort nameIdx, ushort descIdx, params (ushort AttrNameIdx, byte[] Payload)[] attrs) {
      var buf = new List<byte>();
      WriteU2(buf, flags);
      WriteU2(buf, nameIdx);
      WriteU2(buf, descIdx);
      WriteU2(buf, (ushort)attrs.Length);
      foreach (var (an, payload) in attrs) {
        WriteU2(buf, an);
        WriteU4(buf, (uint)payload.Length);
        buf.AddRange(payload);
      }

      this._methods.Add([.. buf]);
    }

    public static byte[] BuildCodeAttribute(
      ushort maxStack,
      ushort maxLocals,
      byte[] code,
      IReadOnlyList<(ushort Start, ushort End, ushort Handler, ushort CatchType)>? exceptions = null,
      IReadOnlyList<(ushort AttrNameIdx, byte[] Payload)>? subAttrs = null) {
      var buf = new List<byte>();
      WriteU2(buf, maxStack);
      WriteU2(buf, maxLocals);
      WriteU4(buf, (uint)code.Length);
      buf.AddRange(code);
      exceptions ??= [];
      WriteU2(buf, (ushort)exceptions.Count);
      foreach (var (s, e, h, c) in exceptions) {
        WriteU2(buf, s);
        WriteU2(buf, e);
        WriteU2(buf, h);
        WriteU2(buf, c);
      }

      subAttrs ??= [];
      WriteU2(buf, (ushort)subAttrs.Count);
      foreach (var (an, payload) in subAttrs) {
        WriteU2(buf, an);
        WriteU4(buf, (uint)payload.Length);
        buf.AddRange(payload);
      }

      return [.. buf];
    }

    public byte[] Build(ushort? overrideCpCount = null) {
      var buf = new List<byte>();
      WriteU4(buf, 0xCAFEBABEu);
      WriteU2(buf, this._minor);
      WriteU2(buf, this._major);
      WriteU2(buf, overrideCpCount ?? (ushort)(this._cpEntries.Count + 1));
      foreach (var entry in this._cpEntries)
        buf.AddRange(entry);

      WriteU2(buf, this._accessFlags);
      WriteU2(buf, this._thisClass);
      WriteU2(buf, this._superClass);
      WriteU2(buf, 0);
      WriteU2(buf, (ushort)this._fields.Count);
      foreach (var f in this._fields)
        buf.AddRange(f);

      WriteU2(buf, (ushort)this._methods.Count);
      foreach (var m in this._methods)
        buf.AddRange(m);

      WriteU2(buf, (ushort)this._classAttrs.Count);
      foreach (var a in this._classAttrs)
        buf.AddRange(a);

      return [.. buf];
    }

    public static void WriteU2(List<byte> buf, ushort value) {
      Span<byte> tmp = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16BigEndian(tmp, value);
      buf.Add(tmp[0]);
      buf.Add(tmp[1]);
    }

    public static void WriteU4(List<byte> buf, uint value) {
      Span<byte> tmp = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(tmp, value);
      buf.Add(tmp[0]);
      buf.Add(tmp[1]);
      buf.Add(tmp[2]);
      buf.Add(tmp[3]);
    }

    public static void WriteU8(List<byte> buf, ulong value) {
      Span<byte> tmp = stackalloc byte[8];
      BinaryPrimitives.WriteUInt64BigEndian(tmp, value);
      for (var i = 0; i < 8; ++i)
        buf.Add(tmp[i]);
    }

  }

}
