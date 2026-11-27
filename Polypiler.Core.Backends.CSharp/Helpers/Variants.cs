// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the IR's dynamic type, which is a C# <c>object</c>: <c>Empty</c> is <c>null</c>,
/// <c>Null</c> is <c>System.DBNull.Value</c> and every other tag is the boxed .NET value of its payload
/// type, so the tag is the type of the box. They are C# 2 for .NET Framework 2.0.
/// </summary>
public static class CSharpVariantHelpers {

  /// <summary>The tag of a value: the <c>VARENUM</c> number of the type of its box; an object of no tag's type is an invalid cast.</summary>
  public static CSharpHelper Tag { get; } = new("VariantTag", "int", "object value", [
    "if (value == null) {",
    "  return 0;",
    "}",
    "if (value is global::System.DBNull) {",
    "  return 1;",
    "}",
    "if (value is short) {",
    "  return 2;",
    "}",
    "if (value is int) {",
    "  return 3;",
    "}",
    "if (value is float) {",
    "  return 4;",
    "}",
    "if (value is double) {",
    "  return 5;",
    "}",
    "if (value is string) {",
    "  return 8;",
    "}",
    "if (value is bool) {",
    "  return 11;",
    "}",
    "if (value is byte) {",
    "  return 17;",
    "}",
    "if (value is long) {",
    "  return 20;",
    "}",
    "throw new global::System.InvalidCastException();"
  ]);

  /// <summary>
  /// A value that has a payload, which the caller's cast then unboxes - and which throws the invalid
  /// cast the IR traps with for a box of another type; <c>Empty</c> and <c>Null</c>, which a cast would
  /// let through or throw another exception for, are an invalid cast here.
  /// </summary>
  public static CSharpHelper Payload { get; } = new("VariantPayload", "object", "object value", [
    "if (value == null || value is global::System.DBNull) {",
    "  throw new global::System.InvalidCastException();",
    "}",
    "return value;"
  ]);

}
