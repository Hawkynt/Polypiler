@EndToEnd @Layer.core
Feature: Bounded pointers from unsafe C#

  Pre-Span C# walks memory with byte*, fixed and *(uint*)(p + i). The IR has such pointers as bounded
  pointers (#1331) - a region and an offset in it - which every C# profile keeps as unsafe code and every
  JavaScript profile lowers to a typed array and an index. Whatever depends on a machine address is
  refused. The bytes d are 0, 1, ..., 15 throughout.

  Scenario Outline: An unsafe kernel runs in every profile of both backends
    Given the fixture "pointers/Pointers.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Checksum(d) returns 3838341128, Word(d, 1) returns 67305985, Distance(new int[10], 2, 7) returns 5 and Distance(new int[10], 7, 2) returns -5
    And Stack(3) returns 29, Local() returns 7, EmptyIsNull() returns true and Bits() returns 1.5

    Examples:
      | to         | profile        |
      | csharp     | net2.0         |
      | csharp     | net4.8         |
      | csharp     | net10.0        |
      | javascript | es5            |
      | javascript | es2015         |
      | javascript | es2022         |

  Scenario: The C# output stays unsafe code and says so
    When "pointers/Pointers.cs" is translated with "--to csharp --profile net2.0"
    Then Word is declared "unsafe" and contains "*(uint*)(", the class is not, and the header names "AllowUnsafeBlocks"
    And it compiles against the .NET Framework 2.0 reference assemblies at C# 2 with AllowUnsafeBlocks and without diagnostics

  Scenario: JavaScript reads a reinterpreted pointer through a DataView
    When "pointers/Pointers.cs" is translated with "--to javascript --profile es2022"
    Then Word contains "getUint32(" with "true" and the output contains no "Uint32Array("

  Scenario: Reading past the region traps outside C#
    Given the C# method "public unsafe static int F(int[] a) { fixed (int* p = a) return p[a.Length]; }"
    Then F(new int[4]) traps with "IndexOutOfRange" in the interpreter and in node at es5 and es2022

  Scenario: Every call agrees with .NET in bounds, and every trap of the IR is JavaScript's too
    Given the fixture "pointers/Pointers.cs"
    Then every in-bounds call of the fixture computes what .NET computes in every profile of both backends
    And every trapping call of the fixture traps as in the interpreter in every JavaScript profile

  Scenario: FNV-1a over a pinned array is the published value everywhere
    Given the fixture "pointers/Fnv1aPointer.cs"
    Then Hash("a") is 0xE40C292C in .NET, in the interpreter and in every profile of both backends

  Scenario Outline: A pointer whose meaning is a machine address is refused
    Given the C# method "<method>"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1002" and "<named>"

    Examples:
      | method                                                                   | named     |
      | unsafe static byte F() { byte* p = (byte*)0x1000; return *p; }           | byte*     |
      | unsafe static long F(byte[] a) { fixed (byte* p = a) return (long)p; }   | long      |
      | unsafe static int F(delegate*<int> f) => f();                            | delegate* |
