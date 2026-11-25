using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Jvm.Bytecode;
using Polypiler.Core.Frontends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm.Lowering;

internal static class JvmModuleLifter {

  public static JvmImportResult Lift(
    string fileName,
    IReadOnlyList<JvmClassFile> classes,
    JvmImportOptions options,
    List<Diagnostic> diagnostics) {
    var moduleName = SanitizeModuleName(fileName, classes);
    var usedNames = new HashSet<string>(StringComparer.Ordinal);

    var fieldMap = new Dictionary<(string Owner, string Name, string Descriptor), JvmFieldRef>();
    var globals = new List<IrGlobal>();
    var arrayInitializers = new Dictionary<(string Owner, string Name, string Descriptor), IrExpr>();

    foreach (var cls in classes) {
      var clinit = cls.Methods.FirstOrDefault(m => m.Name == "<clinit>" && m.Descriptor == "()V");
      if (clinit?.Code is { Code.Length: > 0 })
        ExtractStaticArrayInitializers(cls, clinit.Code, arrayInitializers, diagnostics);
    }

    if (diagnostics.Count > 0)
      return new(null, diagnostics);

    foreach (var cls in classes) {
      var simpleClass = SimpleName(cls.ThisClass);
      foreach (var field in cls.Fields) {
        if ((field.AccessFlags & JvmAccessFlags.Static) == 0)
          continue;

        var jvmType = JvmDescriptor.TryParseField(field.Descriptor);
        if (jvmType is null) {
          diagnostics.Add(JvmFrontend.UnsupportedDescriptor.At(
            $"unsupported static field descriptor '{field.Descriptor}' on '{cls.ThisClass}.{field.Name}'", cls.SourceName));
          return new(null, diagnostics);
        }

        var globalName = UniqueName(classes.Count == 1 ? field.Name : $"{simpleClass}_{field.Name}", usedNames);
        var init = BuildFieldInitializer(cls, field, jvmType, arrayInitializers);
        var global = new IrGlobal(globalName, jvmType.IrType, init);
        globals.Add(global);
        fieldMap[(cls.ThisClass, field.Name, field.Descriptor)] = new(global, jvmType);
      }
    }

    var allMethodInfos = new List<(JvmClassFile Cls, JvmMethodInfo Method)>();
    foreach (var cls in classes) {
      foreach (var method in cls.Methods) {
        if (method.Name == "<init>" || method.Name == "<clinit>" || (method.AccessFlags & (JvmAccessFlags.Synthetic | JvmAccessFlags.Bridge)) != 0)
          continue;

        if ((method.AccessFlags & (JvmAccessFlags.Abstract | JvmAccessFlags.Native)) != 0) {
          diagnostics.Add(JvmFrontend.UnsupportedMethodBody.At(
            $"method '{cls.ThisClass}.{method.Name}{method.Descriptor}' is abstract or native and cannot be lifted", cls.SourceName));
          return new(null, diagnostics);
        }

        if ((method.AccessFlags & JvmAccessFlags.Static) == 0)
          continue;

        allMethodInfos.Add((cls, method));
      }
    }

    var nameCounts = allMethodInfos.GroupBy(x => x.Method.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    var methodMap = new Dictionary<(string Owner, string Name, string Descriptor), JvmMethodRef>();
    var orderedMethods = new List<JvmMethodRef>();

    foreach (var (cls, method) in allMethodInfos) {
      var descriptor = JvmDescriptor.TryParseMethod(method.Descriptor);
      if (descriptor is null) {
        diagnostics.Add(JvmFrontend.UnsupportedDescriptor.At(
          $"unsupported method descriptor '{method.Descriptor}' on '{cls.ThisClass}.{method.Name}'", cls.SourceName));
        return new(null, diagnostics);
      }

      var baseName = nameCounts[method.Name] == 1
        ? method.Name
        : classes.Count == 1
          ? $"{method.Name}_{string.Join("_", descriptor.Parameters.Select(p => JvmDescriptor.TypeTag(p.IrType)))}"
          : $"{SimpleName(cls.ThisClass)}_{method.Name}";
      var irName = UniqueName(baseName, usedNames);
      var methodRef = new JvmMethodRef(irName, descriptor, cls, method);
      methodMap[(cls.ThisClass, method.Name, method.Descriptor)] = methodRef;
      orderedMethods.Add(methodRef);
    }

    var exported = new HashSet<JvmMethodRef>();
    if (options.Exports is { Count: > 0 }) {
      foreach (var requested in options.Exports) {
        var matched = FindExport(requested, orderedMethods);
        if (matched is null) {
          diagnostics.Add(JvmFrontend.ExportNotFound.At($"export '{requested}' was not found in '{fileName}'", fileName));
          return new(null, diagnostics);
        }

        exported.Add(matched);
      }
    } else {
      foreach (var methodRef in orderedMethods)
        if ((methodRef.Method.AccessFlags & JvmAccessFlags.Public) != 0)
          exported.Add(methodRef);
    }

    if (orderedMethods.Count == 0) {
      diagnostics.Add(JvmFrontend.UnsupportedMethodBody.At($"'{fileName}' contains no liftable static methods", fileName));
      return new(null, diagnostics);
    }

    var intrinsics = new JvmIntrinsics();
    var ctx = new JvmLiftContext(methodMap, fieldMap, intrinsics, diagnostics);
    var lifted = new Dictionary<JvmMethodRef, IrFunction>();
    var queue = new Queue<JvmMethodRef>(orderedMethods.Where(exported.Contains));

    bool DrainQueue() {
      while (queue.Count > 0) {
        var current = queue.Dequeue();
        if (lifted.ContainsKey(current))
          continue;

        var lifter = new JvmMethodLifter(current.OwnerClass, current.Method, current.Descriptor, current.IrName, exported.Contains(current), ctx);
        var irFunc = lifter.Lift();
        if (irFunc is null)
          return false;

        lifted[current] = irFunc;
        foreach (var referenced in ctx.ReferencedMethods)
          if (!lifted.ContainsKey(referenced))
            queue.Enqueue(referenced);
      }

      return true;
    }

    if (!DrainQueue())
      return new(null, diagnostics);

    var clinitFunctions = new List<IrFunction>();
    foreach (var cls in classes) {
      var clinit = cls.Methods.FirstOrDefault(m => m.Name == "<clinit>" && m.Descriptor == "()V");
      if (clinit is null)
        continue;

      var clinitName = UniqueName($"__clinit_{SimpleName(cls.ThisClass)}", usedNames);
      var clinitDesc = new JvmMethodDescriptor([], new(IrType.Void, 0));
      var lifter = new JvmMethodLifter(cls, clinit, clinitDesc, clinitName, isPublic: false, ctx, skipArrayGlobalPutStatic: true);
      var irClinit = lifter.Lift();
      if (irClinit is null)
        return new(null, diagnostics);

      foreach (var referenced in ctx.ReferencedMethods)
        if (!lifted.ContainsKey(referenced))
          queue.Enqueue(referenced);

      if (!DrainQueue())
        return new(null, diagnostics);

      clinitFunctions.Add(irClinit);
    }

    string? initializerName = null;
    var finalFunctions = new List<IrFunction>();
    foreach (var m in orderedMethods)
      if (lifted.TryGetValue(m, out var fn))
        finalFunctions.Add(fn);

    finalFunctions.AddRange(intrinsics.Helpers);
    if (clinitFunctions.Count == 1) {
      initializerName = clinitFunctions[0].Name;
      finalFunctions.Add(clinitFunctions[0]);
    } else if (clinitFunctions.Count > 1) {
      finalFunctions.AddRange(clinitFunctions);
      initializerName = UniqueName("__clinit_all", usedNames);
      var calls = clinitFunctions.Select(fn => (IrStmt)new IrExprStmt(new IrCall(fn.Name, IrType.Void, []))).ToList();
      finalFunctions.Add(new IrFunction(initializerName, false, IrType.Void, [], [], new IrBlock(calls)));
    }

    var module = new IrModule(moduleName, finalFunctions) {
      Globals = globals,
      Initializer = initializerName
    };
    return new(module, []);
  }

  private static void ExtractStaticArrayInitializers(
    JvmClassFile cls,
    JvmCodeAttribute codeAttr,
    Dictionary<(string Owner, string Name, string Descriptor), IrExpr> arrayInitializers,
    List<Diagnostic> diagnostics) {
    var insts = JvmBytecodeDecoder.Decode(codeAttr.Code, cls.SourceName, diagnostics);
    if (insts is null)
      return;

    for (var i = 1; i < insts.Count; ++i) {
      if (insts[i].Opcode is not (JvmOpcodes.Newarray or JvmOpcodes.Anewarray))
        continue;

      if (!TryReadIntConst(insts[i - 1], cls.ConstantPool, out var length) || length < 0)
        continue;

      IrType? arrayType = insts[i].Opcode == JvmOpcodes.Newarray
        ? JvmMethodLifter.MapNewArrayType(insts[i].Operand1)
        : cls.ConstantPool.TryGetClassName(insts[i].Operand1) == "java/lang/String"
          ? IrType.Array(IrType.String)
          : null;
      if (arrayType is null)
        continue;

      var elemType = arrayType.Element!;
      var elements = new IrExpr[length];
      var filled = new bool[length];
      var j = i + 1;
      while (j + 3 < insts.Count && insts[j].Opcode == JvmOpcodes.Dup
          && TryReadIntConst(insts[j + 1], cls.ConstantPool, out var elemIdx)
          && elemIdx >= 0 && elemIdx < length
          && TryReadTypedConst(insts[j + 2], elemType, cls.ConstantPool, out var elemExpr)
          && insts[j + 3].Opcode is >= JvmOpcodes.Iastore and <= JvmOpcodes.Sastore) {
        elements[elemIdx] = elemExpr!;
        filled[elemIdx] = true;
        j += 4;
      }

      if (j < insts.Count && insts[j].Opcode == JvmOpcodes.Putstatic
          && cls.ConstantPool.TryGetMemberRef(insts[j].Operand1) is { } member) {
        if (length > 0 && filled.All(x => x))
          arrayInitializers[member] = new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrayType, elements);
        else if (elemType.IsNumeric)
          arrayInitializers[member] = new IrNewArray(arrayType, new IrConst(IrType.I32, length));
        else
          arrayInitializers[member] = new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrayType,
            Enumerable.Range(0, length).Select(k => filled[k] ? elements[k] : (IrExpr)new IrStrConst("")).ToArray());
      }
    }
  }

  private static JvmCpEntry? EntryOf(JvmConstantPool pool, int index, JvmConstantTag tag) {
    var entry = pool.GetEntry(index);
    return entry?.Tag == tag ? entry : null;
  }

  private static bool TryReadIntConst(JvmInstruction inst, JvmConstantPool pool, out int value) {
    if (inst.Opcode is >= JvmOpcodes.IconstM1 and <= JvmOpcodes.Iconst5) {
      value = inst.Opcode - JvmOpcodes.Iconst0;
      return true;
    }

    if (inst.Opcode is JvmOpcodes.Bipush or JvmOpcodes.Sipush) {
      value = inst.Operand1;
      return true;
    }

    if (inst.Opcode is JvmOpcodes.Ldc or JvmOpcodes.LdcW && EntryOf(pool, inst.Operand1, JvmConstantTag.Integer) is { } entry) {
      value = (int)entry.IntValue;
      return true;
    }

    value = 0;
    return false;
  }

  private static bool TryReadTypedConst(JvmInstruction inst, IrType elemType, JvmConstantPool pool, out IrExpr? expr) {
    expr = elemType.Kind switch {
      IrTypeKind.String => EntryOf(pool, inst.Operand1, JvmConstantTag.String) is { } s ? new IrStrConst(pool.TryGetUtf8(s.Index1)!) : null,
      IrTypeKind.I64 => inst.Opcode is JvmOpcodes.Lconst0 or JvmOpcodes.Lconst1
        ? new IrConst(IrType.I64, inst.Opcode - JvmOpcodes.Lconst0)
        : EntryOf(pool, inst.Operand1, JvmConstantTag.Long) is { } l ? new IrConst(IrType.I64, l.IntValue) : null,
      IrTypeKind.F32 => inst.Opcode is >= JvmOpcodes.Fconst0 and <= JvmOpcodes.Fconst2
        ? new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, inst.Opcode - JvmOpcodes.Fconst0))
        : EntryOf(pool, inst.Operand1, JvmConstantTag.Float) is { } f ? new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, f.FloatValue)) : null,
      IrTypeKind.F64 => inst.Opcode is JvmOpcodes.Dconst0 or JvmOpcodes.Dconst1
        ? new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, inst.Opcode - JvmOpcodes.Dconst0))
        : EntryOf(pool, inst.Operand1, JvmConstantTag.Double) is { } d ? new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, d.FloatValue)) : null,
      _ => TryReadIntConst(inst, pool, out var iv) ? new IrConst(elemType, IrOperators.Wrap(elemType, iv)) : null
    };
    return expr is not null;
  }

  private static IrExpr? BuildFieldInitializer(
    JvmClassFile cls,
    JvmFieldInfo field,
    JvmType jvmType,
    Dictionary<(string Owner, string Name, string Descriptor), IrExpr> arrayInitializers) {
    if (arrayInitializers.TryGetValue((cls.ThisClass, field.Name, field.Descriptor), out var arrInit))
      return arrInit;

    if (field.ConstantValue is { } cv) {
      return cv.Tag switch {
        JvmConstantTag.Integer => jvmType.IrType == IrType.Bool
          ? new IrConst(IrType.Bool, cv.IntValue != 0 ? 1 : 0)
          : new IrConst(jvmType.IrType, IrOperators.Wrap(jvmType.IrType, cv.IntValue)),
        JvmConstantTag.Long => new IrConst(IrType.I64, cv.IntValue),
        JvmConstantTag.Float => new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, cv.FloatValue)),
        JvmConstantTag.Double => new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, cv.FloatValue)),
        _ => new IrStrConst(cls.ConstantPool.TryGetUtf8(cv.Index1) ?? "")
      };
    }

    if (jvmType.IrType == IrType.String)
      return new IrStrConst("");

    if (jvmType.IrType.IsArray)
      return jvmType.IrType.Element!.IsNumeric
        ? new IrNewArray(jvmType.IrType, new IrConst(IrType.I32, 0))
        : new IrIntrinsic(IrIntrinsicOp.ArrayOf, jvmType.IrType, []);

    return null;
  }

  private static JvmMethodRef? FindExport(string requested, IReadOnlyList<JvmMethodRef> methods) {
    foreach (var m in methods)
      if (string.Equals(m.IrName, requested, StringComparison.Ordinal)
          || string.Equals(m.Method.Name, requested, StringComparison.Ordinal)
          || string.Equals($"{SimpleName(m.OwnerClass.ThisClass)}.{m.Method.Name}", requested, StringComparison.Ordinal)
          || string.Equals($"{m.OwnerClass.ThisClass}.{m.Method.Name}", requested, StringComparison.Ordinal)
          || string.Equals($"{m.OwnerClass.ThisClass.Replace('/', '.')}.{m.Method.Name}", requested, StringComparison.Ordinal))
        return m;

    return null;
  }

  private static string SimpleName(string internalName) {
    var slash = internalName.LastIndexOf('/');
    return slash >= 0 ? internalName[(slash + 1)..] : internalName;
  }

  private static string SanitizeModuleName(string fileName, IReadOnlyList<JvmClassFile> classes) {
    var stem = Path.GetFileNameWithoutExtension(fileName);
    if (string.IsNullOrWhiteSpace(stem))
      stem = SimpleName(classes[0].ThisClass);

    var chars = stem.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
    var cleaned = new string(chars);
    return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
  }

  private static string UniqueName(string candidate, HashSet<string> used) {
    var name = candidate;
    var suffix = 1;
    while (!used.Add(name))
      name = $"{candidate}_{suffix++}";

    return name;
  }

}
