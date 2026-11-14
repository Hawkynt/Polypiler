using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The branches of the exponential functions that the reference vectors do not reach: the arguments
/// of CORE-MATH's lists of exceptional cases, worst cases of its <c>*.wc</c> files that take the
/// accurate paths (subnormal results, exact powers of two in the reduction, a low part that is a
/// power of two, the range where <c>2^x</c> rounds to <c>1 + x/2</c>) and the binary32 arguments of
/// the slower paths. The expected results are computed with GNU MPFR 4.2.2, rounding to nearest.
/// </summary>
[TestFixture]
public sealed class ExponentialTests {

  /// <summary>A binary64 argument and its correctly rounded result, as bits.</summary>
  private sealed record Case64(string Method, ulong Argument, ulong Result);

  /// <summary>A binary32 argument and its correctly rounded result, as bits.</summary>
  private sealed record Case32(string Method, uint Argument, uint Result);

  private static readonly Case64[] _cases64 = [
    new("Exp", 0xc0862ebafa9785c3UL, 0x0003c5bb67fbe511UL),
    new("Exp", 0xc08640cb9ae402daUL, 0x000064f6e435e070UL),
    new("Exp", 0x3ca0000000000001UL, 0x3ff0000000000001UL),
    new("Exp", 0x40100091a4a0dae5UL, 0x404b50726fd1ccb3UL),
    new("Exp", 0xc038f80e06f3a04cUL, 0x3daf80aafa92b498UL),
    new("Exp", 0xc01daf3afee51991UL, 0x3f439c6240f78d70UL),
    new("Exp", 0xbfd07e05e6e03b83UL, 0x3fe8bb0d03b36f1cUL),
    new("Exp", 0xbfbdaf693d64fadaUL, 0x3fec7f14af0a08ebUL),
    new("Exp", 0xbfba8f783d749a8fUL, 0x3fecd8abd4de5c33UL),
    new("Exp", 0xbf940a990279c2c0UL, 0x3fef613a44b93cdcUL),
    new("Exp", 0xbf2a2fefefd580dfUL, 0x3feffe5d0bb7eabfUL),
    new("Exp", 0xbf1664716b68a409UL, 0x3fefff4cde6a0bfbUL),
    new("Exp", 0xbe30401ae48409b5UL, 0x3feffffffdf7fca3UL),
    new("Exp", 0xbe1ff171507f8ba5UL, 0x3fefffffff007475UL),
    new("Exp", 0xbe185e60704a3a9cUL, 0x3fefffffff3d0cfdUL),
    new("Exp", 0xbe04bd46601ae1efUL, 0x3fefffffffad0ae7UL),
    new("Exp", 0x3ca0000000000000UL, 0x3ff0000000000001UL),
    new("Exp", 0x3dbffff7fffe0001UL, 0x3ff000000001ffffUL),
    new("Exp", 0x3dd8cd99fffb319cUL, 0x3ff0000000063367UL),
    new("Exp", 0x3e0413f33fe6ce07UL, 0x3ff00000002827e7UL),
    new("Exp", 0x3e1f8e165f8388f7UL, 0x3ff00000007e3859UL),
    new("Exp", 0x3eb282aae3169ee7UL, 0x3ff00001282ab8e7UL),
    new("Exp", 0x3ebe5ba92aa9b1efUL, 0x3ff00001e5baaf77UL),
    new("Exp", 0x3f76a4d1af9cc989UL, 0x3ff016b4df3299d7UL),
    new("Exp", 0x3ffaca7ae8da5a7bUL, 0x401557d4acd7e557UL),
    new("Exp2", 0x3ca71547652b818dUL, 0x3ff0000000000000UL),
    new("Exp2", 0x3ca71547652b8190UL, 0x3ff0000000000000UL),
    new("Exp2", 0xc09086889d7252abUL, 0x0000000000014a0fUL),
    new("Exp2", 0xc090707400d180a9UL, 0x00000000003b2ab3UL),
    new("Exp2", 0x3e7df73a53a21272UL, 0x3ff0000014c54a48UL),
    new("Exp2", 0xbfecef4c143b5adfUL, 0x3fe11930594f1671UL),
    new("Exp2", 0x3e4b014253a7dd6bUL, 0x3ff000000256fcffUL),
    new("Exp2", 0xbe606922ce606443UL, 0x3feffffff49ffe97UL),
    new("Exp2", 0x3f5e4596526bf94dUL, 0x3ff0053fc2ec2b53UL),
    new("Exp2", 0x3f5e76049073067fUL, 0x3ff005482a22d63bUL),
    new("Exp2", 0x3f6755aa6fa428cdUL, 0x3ff008185c263cb5UL),
    new("Exp2", 0x3f679015ce2843d7UL, 0x3ff0082ca5944825UL),
    new("Exp2", 0x3f7f99afefa30d65UL, 0x3ff015f65d104a36UL),
    new("Exp2", 0x3f98d040898b73f5UL, 0x3ff04560ec7b6c8dUL),
    new("Exp2", 0x3fb673a7779d5293UL, 0x3ff100b8e43797e2UL),
    new("Exp2", 0x3fb8859f5e252908UL, 0x3ff11930594f1671UL),
    new("Exp2", 0x3fbfa18dfad6e466UL, 0x3ff16e4292aa1157UL),
    new("Exp2", 0x3fc6c4175ea0c6e1UL, 0x3ff21969738ee035UL),
    new("Exp2", 0x3fc926961243babaUL, 0x3ff255a2a884ee79UL),
    new("Exp2", 0x3fe3e34fa6ab969eUL, 0x3ff89d948a94fe17UL),
    new("Exp2", 0x3feb32a6c92d1185UL, 0x3ffcd6b37edeceafUL),
    new("Exp2", 0x3ff9f1a7d355cb4fUL, 0x40089d948a94fe17UL),
    new("Exp2", 0xbf243c1cea9bd4d9UL, 0x3fefff1f99c85951UL),
    new("Exp2", 0xbf277970470a37edUL, 0x3feffefbad547a89UL),
    new("Exp2", 0xbf27d44c7c8229a6UL, 0x3feffef7bdc8c569UL),
    new("Exp2", 0xbf395a914543eab7UL, 0x3feffdcdb5e85f4fUL),
    new("Exp2", 0xbf399be01d01064aUL, 0x3feffdc80db880b3UL),
    new("Exp2", 0xbf413f898b1e4f28UL, 0x3feffd02fc3a41baUL),
    new("Exp2", 0xbf468e7a49000b1cUL, 0x3feffc179913a66fUL),
    new("Exp2", 0xbf486d2a6e5e8368UL, 0x3feffbc4b058134bUL),
    new("Exp2", 0xbf5120d3bdfb6ed8UL, 0x3feffa10e29bccc2UL),
    new("Exp2", 0xbf53ec814d260d02UL, 0x3feff9190b6000bdUL),
    new("Exp2", 0xbf647b667916c4b2UL, 0x3feff1d0b30a8261UL),
    new("Exp2", 0xbf6899e0474ba2d5UL, 0x3fefeef72f67c668UL),
    new("Exp2", 0xbf6ba84c6ebfb038UL, 0x3fefecda0c87a816UL),
    new("Exp2", 0xbf7111bc29ccdbb1UL, 0x3fefe85f006adb08UL),
    new("Exp2", 0xbf71fb57e1996e26UL, 0x3fefe71c1c5fe84fUL),
    new("Exp2", 0xbf772e40977492c3UL, 0x3fefdfed7e7210e2UL),
    new("Exp2", 0xbf7ebf8cf367fcb8UL, 0x3fefd57c0e169707UL),
    new("Exp2", 0xbf807f812303f10aUL, 0x3fefd262977413fcUL),
    new("Exp2", 0xbf9234ada2403885UL, 0x3fef9baa4e60c96fUL),
    new("Exp2", 0xbf935dd739305031UL, 0x3fef954f4f26d1ddUL),
    new("Exp2", 0xbfa526ce079b05a5UL, 0x3fef18bf8b031dbdUL),
    new("Exp2", 0xbfb3ea95a5c16e4aUL, 0x3fee51f0ed267222UL),
    new("Exp2", 0xbfc33564db4bb9ecUL, 0x3fecd6b37edeceafUL),
    new("Exp2", 0xbfcd4854d9f87fcaUL, 0x3feb4ebe40c95a01UL),
    new("Exp2", 0xbfcfe89353e31cbfUL, 0x3feaec09a0cd9ea4UL),
    new("Exp2", 0xbfd83960b2a8d2c4UL, 0x3fe89d948a94fe17UL),
    new("Exp2", 0xbfde242801b45d0dUL, 0x3fe71669b16f3e50UL),
    new("Exp2", 0xbfecef4c143b5adfUL, 0x3fe11930594f1671UL),
    new("Exp2", 0xbff60e582caa34b1UL, 0x3fd89d948a94fe17UL),
    new("Exp10", 0x3f000292f9da1150UL, 0x3ff00049bb4b81edUL),
    new("Exp10", 0x3d1003bd6aafe8b0UL, 0x3ff0000000000094UL),
    new("Exp10", 0x3de0fc218301ba95UL, 0x3ff0000000138df9UL),
    new("Exp10", 0x3d116d12aac2e244UL, 0x3ff00000000000a1UL),
    new("Exp10", 0xc073d6728d1a02b3UL, 0x00000000000c3624UL),
    new("Exp10", 0xc073ca57bab22a57UL, 0x000000000045b6abUL),
    new("Exp10", 0x401033492c1c4dc5UL, 0x40c5eb2cd3f3bce4UL),
    new("Exp10", 0x3fd12e02aa997af2UL, 0x3ffdafa4a686c17fUL),
    new("Exp10", 0x3f4821e0f2afb970UL, 0x3ff006f3a519f98aUL),
    new("Exp10", 0x3f57c3ddd23ac8caUL, 0x3ff00db40291e4f5UL),
    new("Exp10", 0x3f5a2d7c1699e82dUL, 0x3ff00f18ca21b5f4UL),
    new("Exp10", 0x3f7ec65645edc394UL, 0x3ff0477a5e0c9213UL),
    new("Exp10", 0x3f890d7373b3a546UL, 0x3ff07502eb858f1fUL),
    new("Exp10", 0x3f97e3c84f2cb9b5UL, 0x3ff0e20cd9bd42a9UL),
    new("Exp10", 0x3fa25765968ecd68UL, 0x3ff1602fbb34d8aeUL),
    new("Exp10", 0x3fa9aa6fd4d21a47UL, 0x3ff1f525a9f09337UL),
    new("Exp10", 0x3fae7b525705edefUL, 0x3ff259ccebe317bfUL),
    new("Exp10", 0x3fd12e02aa997af2UL, 0x3ffdafa4a686c17fUL),
    new("Exp10", 0x3fdc414aa8bd83b1UL, 0x40061bfb59b7b127UL),
    new("Exp10", 0x3fdd7d271ab4eeb4UL, 0x40071ce472eb84c8UL),
    new("Exp10", 0x3fe1fe5f30572361UL, 0x400d33582532e5b0UL),
    new("Exp10", 0x3fe522c9f19cc202UL, 0x40124df069ec6c7bUL),
    new("Exp10", 0x3ff1daf94cf0bd01UL, 0x402a1efc3ce340d2UL),
    new("Exp10", 0x3ff75f49c6ad3badUL, 0x403ce41d8fa665faUL),
    new("Exp10", 0x3ffa3c782d4f54fcUL, 0x4045d05adfa6a8a1UL),
    new("Exp10", 0x3ffcc30b915ec8c4UL, 0x404f60165d5bc3e1UL),
    new("Exp10", 0x400ee9674267e65fUL, 0x40bc8ed39b9d8a37UL),
    new("Exp10", 0x4012d5494eb1dd13UL, 0x40e8f189a48a6a87UL),
    new("Exp10", 0x40389063309f3004UL, 0x45083fe31fe6a9d6UL),
    new("Exp10", 0x4052a59b82b6fc5eUL, 0x4f6b5f92b2a65f97UL),
    new("Exp10", 0x406cde37694f4d10UL, 0x6fe2210fd5a164b7UL),
    new("Exp10", 0xbf045ddb10382e3fUL, 0x3fefff446cc3068dUL),
    new("Exp10", 0xbf0485426a688467UL, 0x3fefff4301df6b37UL),
    new("Exp10", 0xbf06506061aae6f7UL, 0x3fefff327da56828UL),
    new("Exp10", 0xbf0898a8c3990624UL, 0x3fefff1d78c1eafbUL),
    new("Exp10", 0xbf117362e953393bUL, 0x3feffebe92022beaUL),
    new("Exp10", 0xbf1e40231e216cadUL, 0x3feffdd2d6abfcf2UL),
    new("Exp10", 0xbf27a7f33cc3fd0bUL, 0x3feffc98a8e780fbUL),
    new("Exp10", 0xbf363df14c04ab23UL, 0x3feff999cab1babbUL),
    new("Exp10", 0xbf3a1b18d3a28957UL, 0x3feff87d54fafc19UL),
    new("Exp10", 0xbf3e12494018e44cUL, 0x3feff7596cdd0f69UL),
    new("Exp10", 0xbf44c7a2be09b10eUL, 0x3feff40c0192f170UL),
    new("Exp10", 0xbf4de686910f4f52UL, 0x3fefeece539c5bdaUL),
    new("Exp10", 0xbf5ebb11d32c9493UL, 0x3fefdcb23b597a6fUL),
    new("Exp10", 0xbf7f6f96f005fd47UL, 0x3fef708106038d25UL),
    new("Exp10", 0xbf8b44e17164ce91UL, 0x3fef08a757b7706eUL),
    new("Exp10", 0xbf93b95082297ea7UL, 0x3fee9c9ca3499b0dUL),
    new("Exp10", 0xbf95b25114a07a72UL, 0x3fee79ed6c92103cUL),
    new("Exp10", 0xbfba9cf11e5adbc5UL, 0x3fe9301d2902eba7UL),
    new("Exp10", 0xbfcc360cdde773f7UL, 0x3fe343a134e572ceUL),
    new("Exp10", 0xbfd56ff305822f26UL, 0x3fdd9862d4913979UL),
    new("Exp10", 0xbfdc03419f51b93eUL, 0x3fd75c468428b7c0UL),
    new("Exp10", 0xbfe1416c72a588a6UL, 0x3fd27d838f22d0a0UL),
    new("Exp10", 0xbfed18176754aac7UL, 0x3fbf8db9b16b1432UL),
    new("Exp10", 0xc01aa5575135e2d3UL, 0x3e8d43c2f5ee8437UL),
    new("Exp10", 0xc034cd4af2fca2b4UL, 0x3b9dce2dfaaf67c9UL),
    new("Exp10", 0xc05da5b10d8689fdUL, 0x2750a267404067fbUL),
    new("ExpM1", 0x3dd095702237abdcUL, 0x3dd095702239d1e6UL),
    new("ExpM1", 0x3e30b5d6cc46b3f8UL, 0x3e30b5d6ccd251f9UL),
    new("ExpM1", 0x40100091a4a0dae5UL, 0x404ad0726fd1ccb3UL),
    new("ExpM1", 0x3fd10ad990e7a497UL, 0x3fd386eb84b0d94bUL),
    new("ExpM1", 0x401273c188aa7b14UL, 0x4058f295a96ec6ebUL),
    new("ExpM1", 0xbfd175693a03b590UL, 0xbfce8f4dbc80de43UL),
    new("ExpM1", 0x3fbe923c188ea79bUL, 0x3fc03c5a420857cfUL),
    new("ExpM1", 0x3fd1a0408712e00aUL, 0x3fd44acc499153ccUL),
    new("ExpM1", 0x3fd1c38132777b26UL, 0x3fd4794729ba52adUL),
    new("ExpM1", 0x3fd27f4980d511ffUL, 0x3fd5728eea2bbdddUL),
    new("ExpM1", 0x3fd8172a0e02f90eUL, 0x3fdd404e97601d65UL),
    new("ExpM1", 0x3fd8bbe2fb45c151UL, 0x3fde3186ba9d4d49UL),
    new("ExpM1", 0x3fdbcab27d05abdeUL, 0x3fe166ce703b05e9UL),
    new("ExpM1", 0x3fe005ae04256babUL, 0x3fe4cbb1357e7a3dUL),
    new("ExpM1", 0x3feaccfbe46b4ef0UL, 0x3ff4f85c9783dce1UL),
    new("ExpM1", 0x3fed086543694c5aUL, 0x3ff7a417a07cafafUL),
    new("ExpM1", 0x401273c188aa7b14UL, 0x4058f295a96ec6ebUL),
    new("ExpM1", 0x40183d4bcdebb3f4UL, 0x407ab50b409c8aeeUL),
    new("ExpM1", 0x40308f51434652c3UL, 0x416daac439b157e5UL),
    new("ExpM1", 0x4031d5c2daebe367UL, 0x418a8c02e174c315UL),
    new("ExpM1", 0x403c44ce0d716a1aUL, 0x427b890ca8636ae2UL),
    new("ExpM1", 0x4042ee70220fb1c5UL, 0x4358aa92bc84ff91UL),
    new("ExpM1", 0x40489d56a0c38e6fUL, 0x4460410c95b580b9UL),
    new("ExpM1", 0x4057a60ee15e3e9dUL, 0x48762e4dc3bbf53fUL),
    new("ExpM1", 0x4061f0da93354198UL, 0x4ce0bd73b73fc74cUL),
    new("ExpM1", 0x40654cd1fea7663aUL, 0x4f4c90810d354618UL),
    new("ExpM1", 0x406556c678d5e976UL, 0x4f537e7ac4e7f9b3UL),
    new("ExpM1", 0x4072da9e5e6af0b0UL, 0x5b227d6fe867d6f6UL),
    new("ExpM1", 0x4079e7b643238a14UL, 0x654f5da7fe652978UL),
    new("ExpM1", 0x407d6479eba7c971UL, 0x6a562a88613629b6UL),
    new("ExpM1", 0x4080bc04af1b09f5UL, 0x7037b1d97c902985UL),
    new("ExpM1", 0xbfbab86cb1743b75UL, 0xbfb95f8998ae5a65UL),
    new("ExpM1", 0xbfd119aae6072d39UL, 0xbfce033b6b48a1fbUL),
    new("ExpM1", 0xbfd175693a03b590UL, 0xbfce8f4dbc80de43UL),
    new("ExpM1", 0xbfd474d4de7c14bbUL, 0xbfd182619ac31282UL),
    new("ExpM1", 0xbfd789d025948efaUL, 0xbfd3b1ee1f952dcdUL),
    new("ExpM1", 0xbfd82b5dfaf59b4cUL, 0xbfd4213802eb28ffUL),
    new("ExpM1", 0xbfd9d871e078ebceUL, 0xbfd54385966ef295UL),
    new("ExpM1", 0xbfe1397add4538acUL, 0xbfdaa3b02b31d93eUL),
    new("ExpM1", 0xbfe22e24fa3d5cf9UL, 0xbfdbbd1d708f42adUL),
    new("ExpM1", 0xbfedc2b5df1f7d3dUL, 0xbfe35fe01788d71cUL),
    new("ExpM1", 0xbff0a54d87783d6fUL, 0xbfe4b1887d4d477cUL),
    new("ExpM1", 0xbff2a9cad9998262UL, 0xbfe60870b262bf1cUL),
    new("ExpM1", 0xbffe42a2abb1bf0fUL, 0xbfeb2bfd0f3a2cfbUL),
  ];

  private static readonly Case32[] _cases32 = [
    new("ExpF", 0x37ca7f60u, 0x3f8000cbu),
    new("Exp2F", 0x33b8aa3bu, 0x3f800001u),
    new("Exp2F", 0x3b429d37u, 0x3f804385u),
    new("Exp2F", 0xbcf3a937u, 0x3f7ac6b1u),
    new("Exp2F", 0xb8d3d026u, 0x3f7ffb69u),
    new("Exp10F", 0x3fc00000u, 0x41fcfb72u),
    new("Exp10F", 0x3f900000u, 0x41555d0au),
    new("Exp10F", 0x395701a6u, 0x3f800f7au),
  ];

  private static IEnumerable<TestCaseData> Cases64 => _cases64.Select(item
    => new TestCaseData(item.Method, item.Argument, item.Result).SetArgDisplayNames(item.Method, item.Argument.ToString("x16")));

  private static IEnumerable<TestCaseData> Cases32 => _cases32.Select(item
    => new TestCaseData(item.Method, item.Argument, item.Result).SetArgDisplayNames(item.Method, item.Argument.ToString("x8")));

  [TestCaseSource(nameof(Cases64))]
  public void Binary64_IsCorrectlyRounded(string method, ulong argument, ulong result) {
    var actual = FloatImplementations.Method(method)!.Invoke(null, [BitConverter.Int64BitsToDouble((long)argument)]);
    Assert.That(BitConverter.DoubleToInt64Bits((double)actual!), Is.EqualTo((long)result));
  }

  [TestCaseSource(nameof(Cases32))]
  public void Binary32_IsCorrectlyRounded(string method, uint argument, uint result) {
    var actual = FloatImplementations.Method(method)!.Invoke(null, [BitConverter.Int32BitsToSingle((int)argument)]);
    Assert.That(BitConverter.SingleToInt32Bits((float)actual!), Is.EqualTo((int)result));
  }

  [TestCaseSource(nameof(Cases64))]
  public void Binary64_Imported_IsCorrectlyRounded(string method, ulong argument, ulong result)
    => Assert.That(FloatImplementations.Interpreted(method, IrType.F64, [(long)argument]), Is.EqualTo((long)result));

  [TestCaseSource(nameof(Cases32))]
  public void Binary32_Imported_IsCorrectlyRounded(string method, uint argument, uint result)
    => Assert.That(FloatImplementations.Interpreted(method[..^1], IrType.F32, [argument]), Is.EqualTo((long)result));

}
