@EndToEnd
Feature: Views of arrays in both languages and every profile

  Byte-oriented code works on views: a Span<T> of C#, a subarray of JavaScript. Both import as one view
  type of the IR (#1325) whose slicing, indexing, copying and reinterpretation have one stated trap
  behaviour, and both backends write it in the form each profile has. The bytes d are 0, 1, ..., 15 throughout.

  Scenario Outline: A C# span translates and runs in every profile of both backends
    Given the fixture "spans/Spans.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Sum(d, 4, 4) returns 22, Word(d) returns 84148994, CastXor(d, 8) returns 604242948 and Tail(d) returns 54
    And Sum(d, 13, 4) and Sum(d, -1, 2) trap with "ArgumentOutOfRange" and Sum(d, 16, 0) returns 0
    And every call of the fixture computes what .NET computes

    Examples:
      | to         | profile        |
      | csharp     | net2.0         |
      | csharp     | netstandard2.0 |
      | csharp     | netstandard2.1 |
      | csharp     | net10.0        |
      | javascript | es5            |
      | javascript | es2015         |
      | javascript | es2022         |

  Scenario: A JavaScript subarray keeps its clamping in C#
    Given the fixture "spans/views.mjs"
    When it is translated with "--to csharp --profile net4.8"
    Then word(d) returns 84148994 and tail(d) returns 54 as in node
    And lengths(d) returns "3,0,4,11", although .NET's Slice(13, 4) on 16 bytes throws
    And sliceVsSub(d) returns "0,99": slice copies, subarray aliases

  Scenario: Adler-32 over a slice is the published value in both directions
    Given the fixtures "spans/Adler32Span.cs" and "spans/adler32-view.mjs"
    Then Adler-32 of "Wikipedia" inside a larger buffer is 0x11E60398 in .NET, in node and in every profile of both backends

  Scenario: An overlapping copy moves like memmove everywhere
    Given the C# method that copies d[0..8] of the bytes 0 to 9 to index 2
    Then the array is "0,1,0,1,2,3,4,5,6,7" in .NET, in the interpreter, in node at es5 without typed arrays and at es2022, and in C# for net2.0 and net10.0

  Scenario: Sequence equality follows .NET, not ===
    Given the static C# method "bool F() => new double[] { double.NaN, 0.0 }.AsSpan().SequenceEqual(new double[] { double.NaN, -0.0 });"
    Then it returns true in .NET and in every profile of both backends

  Scenario: Reinterpretation is little-endian and needs no alignment in JavaScript
    Given the static C# method "uint F(byte[] d) => MemoryMarshal.Read<uint>(d.AsSpan(1));"
    When it is translated to JavaScript with "--profile es2022"
    Then the output reads through a DataView with getUint32(…, true) and constructs no Uint32Array
    And F(d) returns 67305985, as in .NET

  Scenario: A ref into a span is refused
    Given the static C# method "int F(int[] a) { ref int r = ref MemoryMarshal.GetReference(a.AsSpan()); return r; }"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1002" and "MemoryMarshal.GetReference"

  Scenario: A host-order typed-array reinterpretation is refused
    Given the JavaScript function "export function f(b) { return new Uint32Array(b.buffer, 4, 1)[0]; }" with b a Uint8Array
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1102" and "Uint32Array"
