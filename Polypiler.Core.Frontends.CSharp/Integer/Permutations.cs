using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  private const string Bmi2 = "System.Runtime.Intrinsics.X86.Bmi2";
  private const string Everywhere = "computed on every processor, where .NET throws PlatformNotSupportedException without BMI2";

  /// <summary>The parallel bit extract and deposit of BMI2, of <c>uint</c> and, in <c>Bmi2.X64</c>, of <c>ulong</c>.</summary>
  private static IEnumerable<CSharpIntegerMember> Permutations => [
    new(Bmi2, "ParallelBitExtract", "ParallelBitExtract", Everywhere),
    new(Bmi2, "ParallelBitDeposit", "ParallelBitDeposit", Everywhere),
    new(Bmi2 + ".X64", "ParallelBitExtract", "ParallelBitExtract", Everywhere),
    new(Bmi2 + ".X64", "ParallelBitDeposit", "ParallelBitDeposit", Everywhere)
  ];

}
