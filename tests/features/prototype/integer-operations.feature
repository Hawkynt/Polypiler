@EndToEnd
Feature: The complete integer and bit operation set
  Every integer and bit operation of the IR on every width translates from C# and JavaScript and is
  written by both backends in every profile, as a library call where the profile has one and as a
  helper otherwise. The snippets that use AsSpan stand in a file with "using System;".

  Scenario Outline: A whole algorithm built on the new operations translates in both directions and every profile
    Given the fixture "<fixture>"
    When it is translated from <from> to <to> for every profile of <to>
    Then every output compiles or parses for its profile
    And <call> returns <result> in every one of them

    Examples:
      | fixture              | from       | to         | call                                              | result               |
      | bits/SipHash24.cs    | csharp     | javascript | Hash(key 00..0f, bytes 00..0e)                    | 0xa129ca6149be45e5   |
      | bits/XxHash32.cs     | csharp     | javascript | Hash(empty, 0)                                    | 0x02CC5D05           |
      | bits/crc16.js        | javascript | csharp     | compute(bytes of "123456789")                     | 0xBB3D               |
      | bits/SipHash24.cs    | csharp     | csharp     | Hash(key 00..0f, bytes 00..0e)                    | 0xa129ca6149be45e5   |

  Scenario: A byte swap is a library call where the library has it
    Given the C# method "public static uint F(uint x) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);"
    Then the "net10.0" output calls "BinaryPrimitives.ReverseEndianness"
    And the "net2.0" output declares a private helper and names no "BinaryPrimitives"
    And the "es5" output declares a helper
    And all of them return 0x78563412 for F(0x12345678)

  Scenario: Rotating a 16-bit value by more than its width wraps the count
    Given the C# method "public static ushort F(ushort x, int n) => ushort.RotateLeft(x, n);"
    Then F(0x8001, 17) returns 0x0003 in the interpreter and in every profile of both backends

  Scenario: Counting zeros of zero gives the width
    Then LeadingZeroCount and TrailingZeroCount of 0 return 8, 16, 32 and 64 for the four widths in every profile

  Scenario: A load outside the array traps before anything happens
    Given the C# method "public static uint F(byte[] b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));"
    Then F(new byte[6], 3) traps with an index out of range in the interpreter and in every profile of both backends

  Scenario: An unmapped member is refused by name
    Given the C# method "public static int F(int x) => System.Numerics.BitOperations.Crc32C(0u, (uint)x) > 0 ? 1 : 0;"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1002" and "Crc32C"
