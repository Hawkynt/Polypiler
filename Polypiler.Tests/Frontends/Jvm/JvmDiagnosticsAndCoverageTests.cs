using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Jvm;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Jvm;

[TestFixture]
public sealed class JvmDiagnosticsAndCoverageTests {

  private static void AssertError(string fileName, byte[] bytes, string expectedCode, JvmImportOptions? options = null) {
    var result = JvmFrontend.Import(fileName, bytes, options);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(d => d.Code), Has.Some.EqualTo(expectedCode), string.Join("\n", result.Diagnostics));
  }

  [Test]
  public void InvalidFormatAndVersion_ReportPLY3100_PLY3101_PLY3102() {
    AssertError("empty.class", [], JvmFrontend.NotClassOrJarCode);
    AssertError("bad.class", [0xDE, 0xAD, 0xBE, 0xEF], JvmFrontend.NotClassOrJarCode);
    AssertError("bad.jar", [0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF], JvmFrontend.CorruptClassFileCode);

    // Empty JAR with no .class entries
    var emptyJar = JvmTestHarness.PackJar([("META-INF/MANIFEST.MF", [1, 2, 3])]);
    AssertError("empty.jar", emptyJar, JvmFrontend.CorruptClassFileCode);

    // JAR containing a non-CAFEBABE .class entry
    var badEntryJar = JvmTestHarness.PackJar([("Bad.class", [1, 2, 3, 4])]);
    AssertError("badentry.jar", badEntryJar, JvmFrontend.NotClassOrJarCode);

    // Java 5 (major = 49) -> PLY3102
    var java5 = new JvmTestHarness.SyntheticClassBuilder().SetVersion(49).Build();
    AssertError("Java5.class", java5, JvmFrontend.UnsupportedClassVersionCode);

    // Truncated class file -> PLY3101
    AssertError("trunc.class", [0xCA, 0xFE, 0xBA, 0xBE, 0x00], JvmFrontend.CorruptClassFileCode);

    // Invalid constant_pool_count = 0 -> PLY3101
    var zeroCp = new JvmTestHarness.SyntheticClassBuilder().Build(overrideCpCount: 0);
    AssertError("zerocp.class", zeroCp, JvmFrontend.CorruptClassFileCode);

    // Unknown CP tag 99 -> PLY3101
    var badTag = new JvmTestHarness.SyntheticClassBuilder();
    badTag.AddCpRaw(99, _ => { });
    AssertError("badtag.class", badTag.Build(), JvmFrontend.CorruptClassFileCode);

    // Long overflowing CP count -> PLY3101
    var overflowLong = new JvmTestHarness.SyntheticClassBuilder();
    overflowLong.AddCpRaw(5, b => JvmTestHarness.SyntheticClassBuilder.WriteU8(b, 1));
    AssertError("longovf.class", overflowLong.Build(), JvmFrontend.CorruptClassFileCode);

    // Double overflowing CP count -> PLY3101
    var overflowDouble = new JvmTestHarness.SyntheticClassBuilder();
    overflowDouble.AddCpRaw(6, b => JvmTestHarness.SyntheticClassBuilder.WriteU8(b, 1));
    AssertError("dblovf.class", overflowDouble.Build(), JvmFrontend.CorruptClassFileCode);

    // Invalid Modified UTF-8 -> PLY3101
    var badUtf8 = new JvmTestHarness.SyntheticClassBuilder();
    badUtf8.AddRawUtf8([0xFF]);
    AssertError("badutf8.class", badUtf8.Build(), JvmFrontend.CorruptClassFileCode);

    // Invalid this_class and super_class -> PLY3101
    var badThis = new JvmTestHarness.SyntheticClassBuilder();
    badThis.AddUtf8("A");
    badThis.SetClasses(99, 0);
    AssertError("badthis.class", badThis.Build(), JvmFrontend.CorruptClassFileCode);

    var badSuper = new JvmTestHarness.SyntheticClassBuilder();
    var tc = badSuper.AddClass("A");
    badSuper.SetClasses(tc, 99);
    AssertError("badsuper.class", badSuper.Build(), JvmFrontend.CorruptClassFileCode);
  }

  [Test]
  public void MissingExport_AbstractNativeEmptyBody_AndInvokeDynamic_ReportExpectedCodes() {
    var simple = JvmTestHarness.CompileClass("Simple", """
      public class Simple {
        public static int id(int x) { return x; }
      }
      """);
    AssertError("Simple.class", simple, JvmFrontend.ExportNotFoundCode, new JvmImportOptions { Exports = ["doesNotExist"] });

    var abstractCls = JvmTestHarness.CompileClass("AbsCls", """
      public abstract class AbsCls {
        public abstract int f(int x);
      }
      """);
    AssertError("AbsCls.class", abstractCls, JvmFrontend.UnsupportedMethodBodyCode);

    var nativeCls = JvmTestHarness.CompileClass("NatCls", """
      public class NatCls {
        public static native int f(int x);
      }
      """);
    AssertError("NatCls.class", nativeCls, JvmFrontend.UnsupportedMethodBodyCode);

    var noStatics = JvmTestHarness.CompileClass("OnlyInstance", """
      public class OnlyInstance {
        public int f(int x) { return x; }
      }
      """);
    AssertError("OnlyInstance.class", noStatics, JvmFrontend.UnsupportedMethodBodyCode);

    // Empty Code attribute -> PLY3108
    var emptyCode = new JvmTestHarness.SyntheticClassBuilder();
    var clsIdx = emptyCode.AddClass("EmptyCode");
    var objIdx = emptyCode.AddClass("java/lang/Object");
    var codeAttr = emptyCode.AddUtf8("Code");
    var fnName = emptyCode.AddUtf8("f");
    var fnDesc = emptyCode.AddUtf8("()V");
    emptyCode.SetClasses(clsIdx, objIdx);
    emptyCode.AddMethod(0x0009, fnName, fnDesc, (codeAttr, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [])));
    AssertError("EmptyCode.class", emptyCode.Build(), JvmFrontend.UnsupportedMethodBodyCode);

    // invokedynamic -> PLY3107
    var indy = new JvmTestHarness.SyntheticClassBuilder();
    var icls = indy.AddClass("Indy");
    var iobj = indy.AddClass("java/lang/Object");
    var icode = indy.AddUtf8("Code");
    var iname = indy.AddUtf8("f");
    var idesc = indy.AddUtf8("()V");
    var nat = indy.AddNameAndType("m", "()V");
    var indyCp = indy.AddDoubleIndexCp(18, 0, nat);
    indy.SetClasses(icls, iobj);
    indy.AddMethod(0x0009, iname, idesc, (icode, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [0xBA, (byte)(indyCp >> 8), (byte)indyCp, 0, 0, 0xB1])));
    AssertError("Indy.class", indy.Build(), JvmFrontend.InvokeDynamicUnsupportedCode);
  }

  [Test]
  public void UnsupportedOpcodesDescriptorsAndStackMapErrors_ReportPLY3104_PLY3105_PLY3106() {
    // Unsupported descriptor (Object param) -> PLY3105
    var badDesc = JvmTestHarness.CompileClass("BadDesc", """
      public class BadDesc {
        public static int f(Object o) { return 0; }
      }
      """);
    AssertError("BadDesc.class", badDesc, JvmFrontend.UnsupportedDescriptorCode);

    // Unsupported static field descriptor -> PLY3105
    var badField = JvmTestHarness.CompileClass("BadField", """
      public class BadField {
        public static Object OBJ = null;
        public static int f() { return 0; }
      }
      """);
    AssertError("BadField.class", badField, JvmFrontend.UnsupportedDescriptorCode);

    // Unsupported opcode (monitorenter 0xC2) -> PLY3104
    var badOp = new JvmTestHarness.SyntheticClassBuilder();
    var c1 = badOp.AddClass("BadOp");
    var o1 = badOp.AddClass("java/lang/Object");
    var ca1 = badOp.AddUtf8("Code");
    var n1 = badOp.AddUtf8("f");
    var d1 = badOp.AddUtf8("()V");
    badOp.SetClasses(c1, o1);
    badOp.AddMethod(0x0009, n1, d1, (ca1, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [0x01, 0xC2, 0xB1])));
    AssertError("BadOp.class", badOp.Build(), JvmFrontend.UnsupportedOpcodeCode);

    // Unsupported wide inner opcode -> PLY3104
    var badWide = new JvmTestHarness.SyntheticClassBuilder();
    var c2 = badWide.AddClass("BadWide");
    var o2 = badWide.AddClass("java/lang/Object");
    var ca2 = badWide.AddUtf8("Code");
    var n2 = badWide.AddUtf8("f");
    var d2 = badWide.AddUtf8("()V");
    badWide.SetClasses(c2, o2);
    badWide.AddMethod(0x0009, n2, d2, (ca2, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 1, [0xC4, 0x00, 0x00, 0x00, 0xB1])));
    AssertError("BadWide.class", badWide.Build(), JvmFrontend.UnsupportedOpcodeCode);

    // StackMapTable: reserved tag 200 -> PLY3106
    AssertError("BadSmt1.class", BuildWithStackMap([0x00, 0x01, 200]), JvmFrontend.InvalidStackMapCode);
    // StackMapTable: offset >= code.Length -> PLY3106
    AssertError("BadSmt2.class", BuildWithStackMap([0x00, 0x01, 50]), JvmFrontend.InvalidStackMapCode);
    // StackMapTable: invalid verification_type tag 9 -> PLY3106
    AssertError("BadSmt3.class", BuildWithStackMap([0x00, 0x01, 64, 9]), JvmFrontend.InvalidStackMapCode);
    // StackMapTable: Object verification_type with invalid cpool index -> PLY3106
    AssertError("BadSmt4.class", BuildWithStackMap([0x00, 0x01, 64, 7, 0x00, 0xFF]), JvmFrontend.InvalidStackMapCode);
    // StackMapTable: Uninitialized offset >= code.Length -> PLY3106
    AssertError("BadSmt5.class", BuildWithStackMap([0x00, 0x01, 64, 8, 0x00, 0xFF]), JvmFrontend.InvalidStackMapCode);
    // StackMapTable: truncated -> PLY3106
    AssertError("BadSmt6.class", BuildWithStackMap([0x00, 0x05]), JvmFrontend.InvalidStackMapCode);

    // Exception table out of bounds -> PLY3101
    var badEx = new JvmTestHarness.SyntheticClassBuilder();
    var c3 = badEx.AddClass("BadEx");
    var o3 = badEx.AddClass("java/lang/Object");
    var ca3 = badEx.AddUtf8("Code");
    var n3 = badEx.AddUtf8("f");
    var d3 = badEx.AddUtf8("()V");
    badEx.SetClasses(c3, o3);
    badEx.AddMethod(0x0009, n3, d3, (ca3, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [0xB1], [(0, 10, 0, 0)])));
    AssertError("BadEx.class", badEx.Build(), JvmFrontend.CorruptClassFileCode);
  }

  [Test]
  public void SyntheticBytecode_CoversStackOpsWideGotoWAndAllStackMapFrameTypes() {
    var builder = new JvmTestHarness.SyntheticClassBuilder();
    var clsIdx = builder.AddClass("SynthOps");
    var objIdx = builder.AddClass("java/lang/Object");
    var codeAttr = builder.AddUtf8("Code");
    var smtAttr = builder.AddUtf8("StackMapTable");
    var lineAttr = builder.AddUtf8("LineNumberTable");
    // Exercise remaining constant pool tags (MethodHandle, MethodType, Dynamic, Module, Package, InterfaceMethodref).
    var nat = builder.AddNameAndType("x", "()V");
    var imr = builder.AddRef(11, clsIdx, nat);
    builder.AddMethodHandle(6, imr);
    builder.AddSingleIndexCp(16, builder.AddUtf8("()V"));
    builder.AddDoubleIndexCp(17, 0, nat);
    builder.AddSingleIndexCp(19, builder.AddUtf8("mod"));
    builder.AddSingleIndexCp(20, builder.AddUtf8("pkg"));
    // Exercise 2-byte and 3-byte Modified UTF-8 sequences.
    builder.AddUtf8("caf\u00E9\u2603");
    builder.SetClasses(clsIdx, objIdx);

    // Method 1: stack manipulation ops (dup_x1, dup_x2 cat1/cat2, dup2 cat1/cat2, dup2_x1 cat1/cat2, dup2_x2 all 4 forms, swap, pop, pop2 cat1/cat2, nop, wide, goto_w).
    var m1Name = builder.AddUtf8("stackOps");
    var m1Desc = builder.AddUtf8("(I)I");
    byte[] m1Code = [
      0x00,                   // nop
      0x04, 0x05, 0x5F,       // iconst_1, iconst_2, swap -> [2, 1]
      0x5A, 0x57,             // dup_x1 -> [1, 2, 1], pop -> [1, 2]
      0x06, 0x5B, 0x57, 0x57, 0x57, 0x57, // iconst_3, dup_x2(cat1), pop*4 -> []
      0x09, 0x04, 0x5B, 0x57, 0x58, 0x57, // lconst_0, iconst_1, dup_x2(cat2), pop, pop2(cat2), pop -> []
      0x04, 0x05, 0x5C, 0x58, 0x58,       // iconst_1, iconst_2, dup2(cat1), pop2(cat1), pop2(cat1) -> []
      0x09, 0x5C, 0x58, 0x58,             // lconst_0, dup2(cat2), pop2(cat2), pop2(cat2) -> []
      0x04, 0x05, 0x06, 0x5D, 0x58, 0x57, 0x58, // dup2_x1(cat1) -> clear
      0x04, 0x09, 0x5D, 0x58, 0x57, 0x58,       // dup2_x1(cat2) -> clear
      0x09, 0x0A, 0x5E, 0x58, 0x58, 0x58,       // dup2_x2(cat2, cat2) -> clear
      0x04, 0x05, 0x09, 0x5E, 0x58, 0x58, 0x58, // dup2_x2(cat2, cat1, cat1) -> clear
      0x09, 0x04, 0x05, 0x5E, 0x58, 0x58, 0x58, // dup2_x2(cat1, cat1, cat2) -> clear
      0x04, 0x05, 0x06, 0x07, 0x5E, 0x58, 0x58, 0x58, // dup2_x2(cat1*4) -> clear
      0xC4, 0x15, 0x00, 0x00,             // wide iload 0
      0xC4, 0x36, 0x00, 0x02,             // wide istore 2
      0xC4, 0x84, 0x00, 0x02, 0x00, 0x0A, // wide iinc 2, +10
      0xC8, 0x00, 0x00, 0x00, 0x05,       // goto_w +5 (next instruction)
      0xC4, 0x15, 0x00, 0x02,             // wide iload 2
      0xAC                                // ireturn
    ];

    // Valid StackMapTable exercising all 7 frame types (same, same_locals_1, same_locals_1_ext, chop, same_ext, append, full_frame) & verification types.
    var smt = new List<byte>();
    JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 7);
    smt.Add(0);                                              // 0..63: same_frame (pc=0)
    smt.Add(64); smt.Add(1);                                 // 64..127: same_locals_1_stack_item (Integer) (pc=1)
    smt.Add(247); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); smt.Add(7); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, objIdx); // same_locals_1_ext (Object)
    smt.Add(248); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); // chop_frame
    smt.Add(251); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); // same_frame_extended
    smt.Add(252); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); smt.Add(8); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); // append (Uninitialized)
    smt.Add(255); JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0); // full_frame
    JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 1); smt.Add(1);
    JvmTestHarness.SyntheticClassBuilder.WriteU2(smt, 0);

    var lnt = new List<byte>();
    JvmTestHarness.SyntheticClassBuilder.WriteU2(lnt, 1);
    JvmTestHarness.SyntheticClassBuilder.WriteU2(lnt, 0);
    JvmTestHarness.SyntheticClassBuilder.WriteU2(lnt, 10);

    builder.AddMethod(0x0009, m1Name, m1Desc,
      (codeAttr, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(8, 4, m1Code, subAttrs: [(smtAttr, [.. smt]), (lineAttr, [.. lnt])])));

    var mod = JvmTestHarness.ImportAndVerify("SynthOps.class", builder.Build());
    Assert.That(IrInterpreter.Call(mod, "stackOps", [32]), Is.EqualTo(42));
    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void ComplexControlFlow_MultiExitLoopsSwitchBreakAndDispatcher_WorkAndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("ComplexCfg", """
      public class ComplexCfg {
        public static int labeledBreak(int n, int m) {
          int total = 0;
          outer:
          for (int i = 0; i < n; i++) {
            switch (i) {
              case 1:
                total += 10;
                break;
              case 3:
                total += 30;
                break outer;
              default:
                for (int j = 0; j < m; j++) {
                  if (j == 2) break outer;
                  total += 1;
                }
                break;
            }
          }
          return total;
        }
        public static int multiExitLoop(int a, int b) {
          int i = 0;
          int r = 0;
          outer:
          while (i < a) {
            i++;
            if (i == b) {
              r = 100;
              break outer;
            }
            if (i == 7) {
              r = 200;
              break;
            }
            r += i;
          }
          return r + i;
        }
        public static int voidHelperCaller(int x) {
          noop(x);
          return x + 1;
        }
        private static void noop(int x) {
          if (x < 0) return;
        }
        public static int exceptionNullAndRefEq(boolean flag) {
          RuntimeException a = flag ? new IllegalArgumentException("a") : null;
          RuntimeException b = null;
          int r = 0;
          if (a == b) r += 1;
          if (a != b) r += 2;
          if (null == b) r += 4;
          return r;
        }
        public static int reuseLocalSlot(int x) {
          int r;
          {
            int a = x + 1;
            r = a;
          }
          {
            double b = x * 2.0;
            r += (int)b;
          }
          return r;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("123-Complex.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "labeledBreak", [5, 1]), Is.EqualTo(1 + 10 + 1 + 30));
    Assert.That(IrInterpreter.Call(mod, "labeledBreak", [5, 5]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(mod, "multiExitLoop", [10, 4]), Is.EqualTo(104));
    Assert.That(IrInterpreter.Call(mod, "multiExitLoop", [10, 20]), Is.EqualTo(207));
    Assert.That(IrInterpreter.Call(mod, "voidHelperCaller", [5]), Is.EqualTo(6));
    Assert.That(IrInterpreter.Call(mod, "exceptionNullAndRefEq", [true]), Is.EqualTo(6));
    Assert.That(IrInterpreter.Call(mod, "exceptionNullAndRefEq", [false]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(mod, "reuseLocalSlot", [5]), Is.EqualTo(16));
    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void AdditionalErrorBranches_DecoderDescriptorsAndLifter_ReportExpectedCodes() {
    // Truncated bytecode instruction -> PLY3101
    AssertError("TruncCode.class", BuildMethod([0x10]), JvmFrontend.CorruptClassFileCode);
    // Invalid tableswitch bounds -> PLY3101
    AssertError("BadTable.class", BuildMethod([0xAA, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 1]), JvmFrontend.CorruptClassFileCode);
    // Invalid lookupswitch negative npairs -> PLY3101
    AssertError("BadLookup.class", BuildMethod([0xAB, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]), JvmFrontend.CorruptClassFileCode);
    // Invalid ldc index -> PLY3101
    AssertError("BadLdc.class", BuildMethod([0x12, 0xFF, 0xB1]), JvmFrontend.CorruptClassFileCode);
    // Invalid getstatic index -> PLY3101
    AssertError("BadGetstatic.class", BuildMethod([0xB2, 0x00, 0xFF, 0xB1]), JvmFrontend.CorruptClassFileCode);
    // Invalid newarray atype -> PLY3101
    AssertError("BadNewarray.class", BuildMethod([0x04, 0xBC, 0x63, 0xB1]), JvmFrontend.CorruptClassFileCode);
    // Invalid invoke index -> PLY3101
    AssertError("BadInvoke.class", BuildMethod([0xB8, 0x00, 0xFF, 0xB1]), JvmFrontend.CorruptClassFileCode);
    // Malformed method descriptor -> PLY3105
    AssertError("MalformedDesc1.class", BuildMethod([0xB1], desc: "I"), JvmFrontend.UnsupportedDescriptorCode);
    AssertError("MalformedDesc2.class", BuildMethod([0xB1], desc: "(I"), JvmFrontend.UnsupportedDescriptorCode);
    AssertError("MalformedDesc3.class", BuildMethod([0xB1], desc: "([)V"), JvmFrontend.UnsupportedDescriptorCode);
    AssertError("MalformedDesc4.class", BuildMethod([0xB1], desc: "([[I)V"), JvmFrontend.UnsupportedDescriptorCode);
    AssertError("MalformedDesc5.class", BuildMethod([0xB1], desc: "(Ljava/lang/String)V"), JvmFrontend.UnsupportedDescriptorCode);
    AssertError("MalformedDesc6.class", BuildMethod([0xB1], desc: "(V)V"), JvmFrontend.UnsupportedDescriptorCode);

    // Unsupported ldc class constant -> PLY3104
    var bLdcCls = new JvmTestHarness.SyntheticClassBuilder();
    var cc = bLdcCls.AddClass("LdcCls");
    var oo = bLdcCls.AddClass("java/lang/Object");
    var ca = bLdcCls.AddUtf8("Code");
    bLdcCls.SetClasses(cc, oo);
    bLdcCls.AddMethod(0x0009, bLdcCls.AddUtf8("f"), bLdcCls.AddUtf8("()V"),
      (ca, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(2, 0, [0x12, (byte)cc, 0x57, 0xB1])));
    AssertError("LdcCls.class", bLdcCls.Build(), JvmFrontend.UnsupportedOpcodeCode);

    // Unsupported new java/lang/Object -> PLY3104
    var bNewObj = new JvmTestHarness.SyntheticClassBuilder();
    var c2 = bNewObj.AddClass("NewObj");
    var o2 = bNewObj.AddClass("java/lang/Object");
    var ca2 = bNewObj.AddUtf8("Code");
    bNewObj.SetClasses(c2, o2);
    bNewObj.AddMethod(0x0009, bNewObj.AddUtf8("f"), bNewObj.AddUtf8("()V"),
      (ca2, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(2, 0, [0xBB, 0, (byte)o2, 0x57, 0xB1])));
    AssertError("NewObj.class", bNewObj.Build(), JvmFrontend.UnsupportedOpcodeCode);

    // Unsupported anewarray Object -> PLY3105
    var bAnew = new JvmTestHarness.SyntheticClassBuilder();
    var c3 = bAnew.AddClass("AnewObj");
    var o3 = bAnew.AddClass("java/lang/Object");
    var ca3 = bAnew.AddUtf8("Code");
    bAnew.SetClasses(c3, o3);
    bAnew.AddMethod(0x0009, bAnew.AddUtf8("f"), bAnew.AddUtf8("()V"),
      (ca3, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(2, 0, [0x04, 0xBD, 0, (byte)o3, 0x57, 0xB1])));
    AssertError("AnewObj.class", bAnew.Build(), JvmFrontend.UnsupportedDescriptorCode);

    // Dynamic String[] allocation -> PLY3104
    var dynStr = JvmTestHarness.CompileClass("DynStr", """
      public class DynStr {
        public static String[] make(int n) { return new String[n]; }
      }
      """);
    AssertError("DynStr.class", dynStr, JvmFrontend.UnsupportedOpcodeCode);

    // Reassigning static array field outside <clinit> -> PLY3104
    var reassignArr = JvmTestHarness.CompileClass("ReassignArr", """
      public class ReassignArr {
        public static int[] DATA = new int[] { 1, 2 };
        public static void replace() { DATA = new int[] { 3, 4 }; }
      }
      """);
    AssertError("ReassignArr.class", reassignArr, JvmFrontend.UnsupportedOpcodeCode);

    // Unresolved invokestatic / invokevirtual -> PLY3104
    var unresolvedStatic = JvmTestHarness.CompileClass("UnresolvedCall", """
      public class UnresolvedCall {
        public static long time() { return System.nanoTime(); }
      }
      """);
    AssertError("UnresolvedCall.class", unresolvedStatic, JvmFrontend.UnsupportedOpcodeCode);

    var unresolvedVirtual = JvmTestHarness.CompileClass("UnresolvedVirt", """
      public class UnresolvedVirt {
        public static int hash(String s) { return s.hashCode(); }
      }
      """);
    AssertError("UnresolvedVirt.class", unresolvedVirtual, JvmFrontend.UnsupportedOpcodeCode);

    // Corrupt field/method CP ref and invalid catch_type -> PLY3101
    var badFieldCp = new JvmTestHarness.SyntheticClassBuilder();
    var cf = badFieldCp.AddClass("BadFieldCp");
    var of = badFieldCp.AddClass("java/lang/Object");
    badFieldCp.SetClasses(cf, of);
    badFieldCp.AddField(0x0009, 99, 99);
    AssertError("BadFieldCp.class", badFieldCp.Build(), JvmFrontend.CorruptClassFileCode);

    var badMethodCp = new JvmTestHarness.SyntheticClassBuilder();
    var cm = badMethodCp.AddClass("BadMethodCp");
    var om = badMethodCp.AddClass("java/lang/Object");
    badMethodCp.SetClasses(cm, om);
    badMethodCp.AddMethod(0x0009, 99, 99);
    AssertError("BadMethodCp.class", badMethodCp.Build(), JvmFrontend.CorruptClassFileCode);

    var badCatch = new JvmTestHarness.SyntheticClassBuilder();
    var ck = badCatch.AddClass("BadCatch");
    var ok = badCatch.AddClass("java/lang/Object");
    var cak = badCatch.AddUtf8("Code");
    badCatch.SetClasses(ck, ok);
    badCatch.AddMethod(0x0009, badCatch.AddUtf8("f"), badCatch.AddUtf8("()V"),
      (cak, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [0x00, 0xB1], [(0, 1, 1, 99)])));
    AssertError("BadCatch.class", badCatch.Build(), JvmFrontend.CorruptClassFileCode);

    // Unsupported invokeinterface / multianewarray -> PLY3104
    AssertError("Iface.class", BuildMethod([0xB9, 0, 1, 1, 0, 0xB1]), JvmFrontend.UnsupportedOpcodeCode);
    AssertError("MultiArr.class", BuildMethod([0xC5, 0, 1, 2, 0xB1]), JvmFrontend.UnsupportedOpcodeCode);
  }

  private static byte[] BuildMethod(byte[] code, string desc = "()V") {
    var b = new JvmTestHarness.SyntheticClassBuilder();
    var c = b.AddClass("MTest");
    var o = b.AddClass("java/lang/Object");
    var ca = b.AddUtf8("Code");
    b.SetClasses(c, o);
    b.AddMethod(0x0009, b.AddUtf8("f"), b.AddUtf8(desc),
      (ca, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(4, 2, code)));
    return b.Build();
  }

  private static byte[] BuildWithStackMap(byte[] stackMapPayload) {
    var b = new JvmTestHarness.SyntheticClassBuilder();
    var c = b.AddClass("SmtTest");
    var o = b.AddClass("java/lang/Object");
    var codeAttr = b.AddUtf8("Code");
    var smtAttr = b.AddUtf8("StackMapTable");
    var n = b.AddUtf8("f");
    var d = b.AddUtf8("()V");
    b.SetClasses(c, o);
    b.AddMethod(0x0009, n, d, (codeAttr, JvmTestHarness.SyntheticClassBuilder.BuildCodeAttribute(1, 0, [0x00, 0xB1], subAttrs: [(smtAttr, stackMapPayload)])));
    return b.Build();
  }

}
