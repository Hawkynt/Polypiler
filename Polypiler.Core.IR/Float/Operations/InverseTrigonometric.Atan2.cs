using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The arc tangent of a quotient, correctly rounded in both formats. Converted from CORE-MATH's
/// <c>src/binary64/atan2/atan2.c</c> with its 192-bit arithmetic <c>tint.h</c>, and from
/// <c>src/binary32/atan2/atan2f.c</c> (MIT); see <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseTrigonometric.txt</c>.
/// A 192-bit number of the accurate path is a <c>ulong[5]</c>: the significand from the most
/// significant word (<see cref="Atan2High"/>, <see cref="Atan2Middle"/>, <see cref="Atan2Low"/>), whose
/// top bit is set unless it is zero, the exponent <see cref="Atan2Exponent"/> as the bits of a
/// <c>long</c>, and the sign <see cref="Atan2Sign"/>; it stands for
/// <c>(-1)^sign (high/2^64 + middle/2^128 + low/2^192) 2^exponent</c>.
/// </summary>
public static partial class FloatOperations {

  private const int Atan2High = 0;
  private const int Atan2Middle = 1;
  private const int Atan2Low = 2;
  private const int Atan2Exponent = 3;
  private const int Atan2Sign = 4;

  /// <summary><c>j/64</c> for <c>j</c> from 0 to 64, the points the fast path of <see cref="Atan2"/> reduces to.</summary>
  private static readonly double[] _atan2Points = [
    0.0, 0.015625, 0.03125, 0.046875, 0.0625, 0.078125, 0.09375, 0.109375,
    0.125, 0.140625, 0.15625, 0.171875, 0.1875, 0.203125, 0.21875, 0.234375,
    0.25, 0.265625, 0.28125, 0.296875, 0.3125, 0.328125, 0.34375, 0.359375,
    0.375, 0.390625, 0.40625, 0.421875, 0.4375, 0.453125, 0.46875, 0.484375,
    0.5, 0.515625, 0.53125, 0.546875, 0.5625, 0.578125, 0.59375, 0.609375,
    0.625, 0.640625, 0.65625, 0.671875, 0.6875, 0.703125, 0.71875, 0.734375,
    0.75, 0.765625, 0.78125, 0.796875, 0.8125, 0.828125, 0.84375, 0.859375,
    0.875, 0.890625, 0.90625, 0.921875, 0.9375, 0.953125, 0.96875, 0.984375,
    1.0
  ];

  /// <summary><c>atan(j/64)</c> for <c>j</c> from 0 to 64, as double-doubles with the low part first.</summary>
  private static readonly double[] _atan2Arctangents = [
    0.0, 0.0, -1.7569843108731414e-16, 0.015623728620477007,
    1.2718109451069599e-16, 0.03123983343026815, -1.5282123363018451e-16, 0.04684071291596981,
    -1.5420474151678852e-16, 0.0624188099959575, -3.582881155030001e-17, 0.07796663383154234,
    2.0188234451767475e-16, 0.09347678115858926, 4.846007563068433e-17, 0.10894195698986575,
    1.9116370516694847e-16, 0.12435499454676124, 1.9133104288467082e-16, 0.13970887428916345,
    -1.0143688686840133e-16, 0.15499674192394108, -1.700746177735756e-16, 0.17021192528547457,
    5.969184350010091e-17, 0.1853479499956947, 1.6967340798095792e-16, 0.20039855382587834,
    5.59849672442657e-17, 0.215357699697738, 1.7884685822291619e-16, 0.23021958727684355,
    -7.256797122815228e-17, 0.24497866312686423, 1.3026105738713097e-16, 0.2596296294082574,
    8.261353575163773e-18, 0.2741674511196588, -1.2530600203628822e-16, 0.2885873618940775,
    2.1103377702202993e-16, 0.3028848683749712, 9.208301321958923e-17, 0.3170557532091469,
    1.0306969208672186e-16, 0.331096076704132, 8.808349770693735e-17, 0.345002177207105,
    3.088733564861919e-17, 0.3587706702705722, -3.5898839726412174e-17, 0.37239844667675426,
    -1.4274522636885407e-16, 0.3858826693980739, 2.246598105617042e-17, 0.39922076957525254,
    -1.8240997597148038e-16, 0.4124104415973875, -1.9872907418313842e-16, 0.4254496373700425,
    -2.494277030626541e-17, 0.43833655985795783, 3.2807356001837356e-17, 0.45106965598852344,
    -3.281237377829614e-17, 0.46364760900080615, -2.0739011759277459e-16, 0.47606933032276144,
    -1.1373236189329585e-17, 0.48833395105640554, -4.7181675085518756e-17, 0.5004408131472942,
    -2.5462781472855804e-17, 0.5123894603107377, -2.1652451080538965e-16, 0.5241796287829135,
    1.069585067790331e-16, 0.5358112379604636, -6.17852057485531e-17, 0.547284380987437,
    -5.4556305485916264e-18, 0.5585993153435624, -2.0978954283997712e-16, 0.5697564534829787,
    9.660765868058498e-17, 0.5807563535676703, -1.728396503881636e-16, 0.5915997103351116,
    -8.151799509023163e-17, 0.6022873461349643, 1.904925430764451e-16, 0.6128202021652411,
    2.672403885140095e-17, 0.6231993299340659, 1.9475383748901603e-16, 0.6334258829691444,
    1.2685708751395994e-16, 0.6435011087932843, 3.5800634857340095e-17, 0.6534263411807619,
    -1.4178285110681214e-16, 0.6632029927060934, -1.300154525596627e-16, 0.6728325475937633,
    6.943223671560008e-18, 0.6823165548747481, 2.1392745373274551e-16, 0.6916566218531996,
    -1.308985648058738e-16, 0.7008544078844503, 6.505063795666678e-17, 0.7099116184635248,
    -2.1478388444456983e-17, 0.7188299996216245, -1.0845297676512382e-16, 0.7276113326265108,
    -1.8730522844203674e-16, 0.7362574289814283, -7.393914397116018e-17, 0.7447701257160753,
    8.676536780333358e-17, 0.7531512809621943, 1.2087233279526847e-16, 0.7614027698055783,
    -3.704991905602721e-17, 0.7695264804056583, 1.953681139730868e-16, 0.7775243103733476,
    3.061616997868383e-17, 0.7853981633974483
  ];

  /// <summary>The offset of each octant, by sign of x, sign of y and whether |y| &gt; |x|, as double-doubles.</summary>
  private static readonly double[] _atan2Octants = [
    0.0, 0.0, 1.5707963267948966, 6.123233995736766e-17,
    0.0, 0.0, -1.5707963267948966, -6.123233995736766e-17,
    3.141592653589793, 1.2246467991473532e-16, 1.5707963267948966, 6.123233995736766e-17,
    -3.141592653589793, -1.2246467991473532e-16, -1.5707963267948966, -6.123233995736766e-17
  ];

  /// <summary>The odd polynomial of the fast path, from <c>z^3</c> on.</summary>
  private static readonly double[] _atan2Fast = [
    -0.33333333333332604, 0.19999999943116925, -0.14284292170535642
  ];

  /// <summary>The odd polynomial of the second fast path, from <c>z^3</c> on.</summary>
  private static readonly double[] _atan2Second = [
    -0.3333333333333333, 0.19999999999974255, -0.14285713816496304, 0.11107620301764512
  ];

  /// <summary>1 and -1, by a sign bit.</summary>
  private static readonly double[] _atan2Signs = [1.0, -1.0];

  /// <summary>
  /// The rational approximation <c>z P(z) / Q(z)</c> of <c>atan(z)</c> on <c>[0, 1]</c>: in rows 0 to 29
  /// the coefficients of <c>P</c>, in rows 30 to 59 those of <c>Q</c>, each from the constant term on, as
  /// the three significand words and the sign.
  /// </summary>
  private static readonly ulong[] _atan2Rational = [
    0x82703f8b53112ecaUL, 0x71ce6279d467aa45UL, 0x019c9b28466b6fc0UL, 0UL,
    0xb3cf74b427d53e03UL, 0xee590b839e751890UL, 0xf2570b12bcb2381aUL, 0UL,
    0xb08498b17b88c39aUL, 0x11e37821d784d8faUL, 0x2795b14b84b9d78cUL, 0UL,
    0xfd4baa7bb52b0d83UL, 0xb25425beb0a04dc3UL, 0x92008d9fb34b68ffUL, 0UL,
    0x96541948b85fb386UL, 0xe0b815ace425a859UL, 0xbeca17c69bb4f7a0UL, 0UL,
    0x96aaa5d4baffc590UL, 0xf23db783b6e4ce55UL, 0x2c54e054d3b69f7eUL, 0UL,
    0x83cd41b5fa862b11UL, 0xad8f9d6e3eb9d833UL, 0x4ef470d801fa03caUL, 0UL,
    0xcbbc7752197cc106UL, 0xbc66cfb3edefa6e3UL, 0x568b2f9e4feb8910UL, 0UL,
    0x8cf8f7c585767042UL, 0xcd92a5b9ac13d8c8UL, 0x043bd4a023dbe04dUL, 0UL,
    0xafdc36d6b36139bfUL, 0x11df63474e3859d4UL, 0x42b30b2c089a2b6cUL, 0UL,
    0xc6f8fb1463822519UL, 0x2f9f04cf55c10377UL, 0x1c44675c6d483606UL, 0UL,
    0xcce79f10f754e315UL, 0x91ce47edcbc8a4eeUL, 0xb2a6f721ac656b42UL, 0UL,
    0xc090384dbd77132eUL, 0x1dfb4d9a1d595de7UL, 0xe506ca899a394f70UL, 0UL,
    0xa556ac9eee25e867UL, 0xd84dafa25141d203UL, 0xf1c7c2a5156b3ec3UL, 0UL,
    0x81bd2677ddfcbc38UL, 0x1df33f27dbb60939UL, 0xcb4c2c73286a6c29UL, 0UL,
    0xb9ed12169d72e8d9UL, 0xc81d95dcef13c227UL, 0x974b95796b93c332UL, 0UL,
    0xf2df6da875b666faUL, 0xc29dd3a69c274c65UL, 0x83b9260976c8f0c5UL, 0UL,
    0x902d34d4db910ed8UL, 0x6f3ecddfd6af0a9fUL, 0x8e950d7e70a8e9b2UL, 0UL,
    0x9aefa41961ce1207UL, 0xacd0317666753751UL, 0x3eaf42a53fcc6534UL, 0UL,
    0x95db9977c5e6e263UL, 0x85367a63fab8a720UL, 0xfc2b63bd115998d8UL, 0UL,
    0x817cb6d8236d5f4dUL, 0x70c7815f28909d9bUL, 0x0c54e97155d12158UL, 0UL,
    0xc5eec4228c352098UL, 0x789e56be1dee9b67UL, 0xba25bda81292ccc1UL, 0UL,
    0x840c38ccfcb5dc64UL, 0xb55a44095bf7f312UL, 0x6668b8792d4ecd08UL, 0UL,
    0x97074af7b286b19eUL, 0xc20aac21f3130f1dUL, 0x369e9fdbdd48528aUL, 0UL,
    0x9054011c32d4b7a5UL, 0xc68d3c5d525f4af4UL, 0xeeb3c98615dee188UL, 0UL,
    0xdddb4a2eaab2bb15UL, 0x753761a8f0e9aa8dUL, 0x3f20620cd3e075bbUL, 0UL,
    0x80ef9a1b3e59abe5UL, 0x18e6afe0651fdf49UL, 0xd1c0329e6a702d9dUL, 0UL,
    0xca16dd49cd30854cUL, 0xbad8ca379401027cUL, 0x1fc7f47985486f16UL, 0UL,
    0xa0ba01b8742ba801UL, 0xe6dc14a9516d7f04UL, 0x1f5303d878905d2fUL, 0UL,
    0x9c0440896ce6515cUL, 0x0cf3470fa7f10ac3UL, 0xa9dc80682c111a9dUL, 1UL,
    0x82703f8b53112ecaUL, 0x71ce6279d467aa45UL, 0x019c9b28466b6fc0UL, 0UL,
    0xb3cf74b427d53e03UL, 0xee590b839e751890UL, 0xf2570b12bcb238cfUL, 0UL,
    0xb33c44af0d43c9e8UL, 0xf4426f84614701dbUL, 0x979e49dc5b858442UL, 0UL,
    0x8264d256f0bf4d61UL, 0xee21431cc0f29764UL, 0xce0cc1b593ac9952UL, 0UL,
    0x9d97c4947687b2eeUL, 0xef4087c49dde5229UL, 0x742bc6e02bfb6df8UL, 0UL,
    0xa0f888dc7aab949eUL, 0xf623f19ac413db23UL, 0x854744a164f70ecdUL, 0UL,
    0x8fd9c7172ab0828eUL, 0x1a9f72fadafc3dfbUL, 0xaf96a180ea297178UL, 0UL,
    0xe38168b78a51ee15UL, 0x604a47815c8091ebUL, 0x9faaa42312e8eb30UL, 0UL,
    0xa164ccdbb65ee133UL, 0xe623433c5e086402UL, 0xcb228afd335a245fUL, 0UL,
    0xced0c0cbe44fac2fUL, 0xd7c2adfc7c5dda9eUL, 0xa22b4a311cf5a5b1UL, 0UL,
    0xf0e6660438c90371UL, 0x3dca1a0f58f0d943UL, 0x8a92b9de68c4488cUL, 0UL,
    0x8000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL, 0UL,
    0xf8ea68f9ad849103UL, 0xb257e4bfc606967fUL, 0x818fa2816928c4aaUL, 0UL,
    0xddc64eeee3506234UL, 0x47a33ea40a2b22e4UL, 0xa6a2c129ace2470aUL, 0UL,
    0xb52ddfba2d37c8e2UL, 0x24551a9b19fa064aUL, 0x3d78c68b54bc3850UL, 0UL,
    0x87ac113d26b683a1UL, 0x4e97756c38785b0bUL, 0xf75f0a4506931ba8UL, 0UL,
    0xba065070b900f3d5UL, 0xe49d8f9f238f1eebUL, 0x6805d48d67f1dcd9UL, 0UL,
    0xe902d2a4bfc22441UL, 0x945cfe6c83a5b828UL, 0x519ebb457f8ccca0UL, 0UL,
    0x84e405c915f7769bUL, 0x0faaf34c3c51dd39UL, 0xa97fa0ae99382c93UL, 0UL,
    0x896da46c7e6cebb3UL, 0xe0afc0b6db3dbbf5UL, 0x061ccff6e78efff4UL, 0UL,
    0x801a0c1a0dfefc92UL, 0xf8e0a97902e08d47UL, 0x81898e0b7d8ef0bcUL, 0UL,
    0xd59d088930df07dbUL, 0xa8cb00021849fbafUL, 0xf80cc91aa0bddf89UL, 0UL,
    0x9db56a6984b77231UL, 0xcf7c2074e0480959UL, 0x3316ecaa6aa4604eUL, 0UL,
    0xcb7450d9d62d2e68UL, 0x9787b4265977d004UL, 0xba615cee00a841d7UL, 0UL,
    0xe12a52e02b64b23eUL, 0x97c9139a8ced716cUL, 0x70a176ad2b5f16d2UL, 0UL,
    0xd057b6d0900ed30dUL, 0x5e04bb17eb879a5eUL, 0x23ed135b60bea070UL, 0UL,
    0x9b1f1ca22fef7ae5UL, 0x19a687f0a4073eeeUL, 0x05a9ccbc8c14578bUL, 0UL,
    0xaeba710f053aac3bUL, 0x30ea5d6fb116f775UL, 0x8a9bcfef5698f8f1UL, 0UL,
    0x84b9ada076cd007dUL, 0x13e55006073f76b1UL, 0x4cf4259968228f3eUL, 0UL,
    0xcca4b9046786937dUL, 0x233c1e691c127c1fUL, 0xf493e0b7a3de459aUL, 0UL
  ];

  /// <summary>The exponents of the rows of <see cref="_atan2Rational"/>.</summary>
  private static readonly long[] _atan2RationalExponent = [
    -10, -8, -6, -5, -3, -2, -1, -1, 0, 0,
    0, 0, 0, 0, 0, -1, -2, -2, -3, -4,
    -5, -7, -8, -10, -12, -15, -17, -21, -25, -59,
    -10, -8, -6, -4, -3, -2, -1, -1, 0, 0,
    0, 1, 0, 0, 0, 0, -1, -2, -2, -3,
    -4, -6, -7, -9, -11, -13, -15, -18, -21, -26
  ];

  /// <summary>
  /// The arc tangent of <c>y/x</c> in radians, in the quadrant of the point <c>(x, y)</c>, correctly
  /// rounded; the arguments in the order of C's <c>atan2</c>. The signed zeros and the infinities give
  /// the results of IEEE 754 and C. Otherwise the ratio of the smaller magnitude to the larger one is
  /// reduced by the nearest <c>j/64</c> and a short odd polynomial added to <c>atan(j/64)</c> and the
  /// octant's offset; if that does not round safely, the reduction is redone in double-double, and
  /// finally <c>atan</c> is evaluated by a rational approximation in 192-bit arithmetic.
  /// </summary>
  /// <param name="y">The ordinate.</param>
  /// <param name="x">The abscissa.</param>
  /// <returns>The angle in <c>[-pi, pi]</c>.</returns>
  public static double Atan2(double y, double x) {
    const ulong Magnitude = 0x7fffffffffffffffUL;
    var iy = (ulong)BitConverter.DoubleToInt64Bits(y);
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    var aiy = iy & Magnitude;
    var aix = ix & Magnitude;
    if (aiy == 0 || aiy >= 0x7ffUL << 52 || aix == 0 || aix >= 0x7ffUL << 52)
      return Atan2Special(y, x);

    var absX = Math.Abs(x);
    var absY = Math.Abs(y);
    var large = Math.Max(absX, absY);
    var small = Math.Min(absX, absY);
    var sy = iy >> 63;
    var sx = ix >> 63;
    var gt = aix < aiy ? 1UL : 0UL;
    var dxy = unchecked((aix - aiy) ^ (0 - gt));
    if (dxy >= 53UL << 52)
      return Atan2Accurate(y, x);

    // The sign of the reduced argument, the octant, and j = round(64 small/large).
    var sign = _atan2Signs[(int)(gt ^ sx ^ sy)];
    var kw = (int)(sx << 2 | sy << 1 | gt);
    var jj = (ulong)BitConverter.DoubleToInt64Bits(small / large + (2 + 1 / 128.0));
    var jt = (int)((jj >> (52 - 7)) & 127);
    var fh = _atan2Arctangents[2 * jt + 1] * sign;
    var fl = _atan2Arctangents[2 * jt] * sign;
    fh += _atan2Octants[2 * kw];
    fl += _atan2Octants[2 * kw + 1];
    if (large < 1.1282464849155185e-277) {
      large *= 8.863311460481781e+276;
      small *= 8.863311460481781e+276;
    }

    if (large > 4.49423283715579e+307 && jt != 0) {
      large *= 0.5;
      small *= 0.5;
    }

    // z = (small - t0 large) / (large + t0 small) = tan(atan(small/large) - atan(t0)).
    var t0 = _atan2Points[jt];
    var zn = Math.FusedMultiplyAdd(-t0, large, small);
    var zd = Math.FusedMultiplyAdd(t0, small, large);
    var z = zn / zd;
    var z2 = z * z;
    z *= sign;
    var dz = (z * z2) * (_atan2Fast[0] + z2 * (_atan2Fast[1] + z2 * _atan2Fast[2]));
    var eps = Math.Abs(z) * 4.528712474471952e-16 + 8.077935669463161e-28;
    var rh = fh + z;
    z -= rh - fh;
    var rl = (fl + dz) + z;
    var lb = rh + (rl - eps);
    var ub = rh + (rl + eps);
    if (lb != ub) {
      // The same reduction with z in double-double: zh + zl = (nh + nl) / (dh + dl).
      var dh = small * t0;
      var dl = Math.FusedMultiplyAdd(small, t0, -dh);
      var sum = large + dh;
      var e = dh - (sum - large);
      dh = sum;
      var rdh = 1 / dh;
      dl += e;
      var nh = large * t0;
      var nl = Math.FusedMultiplyAdd(large, t0, -nh);
      var dt = small - nh;
      var y1 = dt + nh;
      var addend = y1 == small ? -nl : (small - y1) - nl;
      sum = dt + addend;
      nl = addend - (sum - dt);
      nh = sum;
      var zh = nh * rdh;
      z2 = zh * zh;
      var zl = rdh * (Math.FusedMultiplyAdd(dh, -zh, nh) + (nl - (nh * rdh) * dl));
      zl += zh * z2 * ((_atan2Second[0] + z2 * _atan2Second[1]) + (z2 * z2) * (_atan2Second[2] + z2 * _atan2Second[3]));
      zh *= sign;
      zl *= sign;
      eps = 1.1102230246251565e-15 * (Math.Abs(zh) * z2 + 4.440892098500626e-16);
      sum = fh + zh;
      var low = zh - (sum - fh);
      fl = (fl + zl) + low;
      fh = sum;
      lb = fh + (fl - eps);
      ub = fh + (fl + eps);
      if (lb != ub)
        return Atan2Accurate(y, x);
    }

    return ub;
  }

  /// <summary>The arc tangent of <c>y/x</c> when one of them is zero, infinite or a NaN.</summary>
  private static double Atan2Special(double y, double x) {
    const ulong Infinity = 0x7ffUL << 53;
    var iy = (ulong)BitConverter.DoubleToInt64Bits(y);
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    var aiy = iy << 1;
    var aix = ix << 1;
    if (aiy >= Infinity || aix >= Infinity) {
      if (aiy > Infinity || aix > Infinity)
        return y + x;

      // Both infinite: +/-3pi/4 for x = -Inf, +/-pi/4 for x = +Inf.
      if (aiy == Infinity && aix == Infinity) {
        if (ix >> 63 != 0)
          return CopySign(2.356194490192345, y) + CopySign(5.551115123125783e-17, y);

        return CopySign(0.7853981633974483, y) + CopySign(2.7755575615628914e-17, y);
      }

      // x infinite: +/-pi for x = -Inf, +/-0 for x = +Inf.
      if (aix == Infinity) {
        if (x < 0)
          return CopySign(3.141592653589793, y) + CopySign(1.2246467991473532e-16, y);

        return CopySign(0, y);
      }

      // y infinite: +/-pi/2.
      return CopySign(1.5707963267948966, y) + CopySign(6.123233995736766e-17, y);
    }

    // Now y or x is a zero.
    if (aiy == 0 && aix == 0) {
      // atan2(+/-0, +0) = +/-0, atan2(+/-0, -0) = +/-pi.
      if (ix == 0)
        return y;

      return iy == 0 ? 3.141592653589793 + 1.2246467991473532e-16 : -3.141592653589793 - 1.2246467991473532e-16;
    }

    if (aiy == 0) {
      // atan2(+/-0, x) = +/-0 for x > 0, +/-pi for x < 0.
      if (x > 0)
        return y / x;

      return iy >> 63 == 0 ? 3.141592653589793 + 1.2246467991473532e-16 : -3.141592653589793 - 1.2246467991473532e-16;
    }

    // atan2(y, +/-0) = +/-pi/2 by the sign of y.
    return y > 0 ? 1.5707963267948966 + 6.123233995736766e-17 : -1.5707963267948966 - 6.123233995736766e-17;
  }

  /// <summary>
  /// The accurate path of <see cref="Atan2"/>: an exact tiny quotient directly, otherwise <c>z</c>, the
  /// quotient of the smaller magnitude by the larger one, in 192 bits, <c>atan(z)</c> by a rational
  /// approximation of type [29, 29], the quadrant applied, and the result rounded from the bound on
  /// its error.
  /// </summary>
  private static double Atan2Accurate(double y, double x) {
    // For |t| <= 0x1.d12ed0af1a27fp-27, atan(t) rounds like t - t * 2^-54 when t = y/x is exact.
    var t = y / x;
    if (t == 0) {
      if (x > 0)
        return t;

      return y > 0 ? 3.141592653589793 + 1.2246467991473532e-16 : -3.141592653589793 - 1.2246467991473532e-16;
    }

    var corr = Math.FusedMultiplyAdd(t, x, -y);
    if (corr == 0 && x > 0 && Math.Abs(t) <= 1.3538603431225864e-08) {
      if (Math.Abs(y) >= 3.610388751729659e-276)
        return Math.FusedMultiplyAdd(t, -5.551115123125783e-17, t);

      // For |y| < 2^-915, t x - y may underflow: check the exactness with both scaled by 2^105.
      corr = Math.FusedMultiplyAdd(t * 4.056481920730334e+31, x, -y * 4.056481920730334e+31);
      if (corr == 0)
        return Math.FusedMultiplyAdd(t, -5.551115123125783e-17, t);
    }

    var inv = Math.Abs(y) > Math.Abs(x);
    var z = new ulong[5];
    if (inv)
      Atan2DivideDoubles(z, x, y);
    else
      Atan2DivideDoubles(z, y, x);

    // Below 2^-96, atan(y/x) rounds like y/x moved a little towards zero.
    if (!inv && x > 0 && (long)z[Atan2Exponent] <= -96) {
      z[Atan2Low] -= 2;
      z[Atan2Middle] -= z[Atan2Low] < 2 ? 1UL : 0UL;
      z[Atan2High] -= z[Atan2Middle] < 1 ? 1UL : 0UL;
      return Atan2ToDouble(z, 1);
    }

    // z P(z) / Q(z) for |z|, Horner's scheme in 192 bits.
    var sz = z[Atan2Sign];
    z[Atan2Sign] = 0;
    var p = new ulong[5];
    var q = new ulong[5];
    var coefficient = new ulong[5];
    Atan2Coefficient(p, 29);
    Atan2Coefficient(q, 30 + 29);
    for (var i = 28; i >= 0; i--) {
      Atan2Multiply(p, p, z);
      Atan2Multiply(q, q, z);
      Atan2Coefficient(coefficient, i);
      Atan2Add(p, p, coefficient);
      Atan2Coefficient(coefficient, 30 + i);
      Atan2Add(q, q, coefficient);
    }

    Atan2Multiply(p, p, z);
    Atan2DivideTints(z, p, q);

    // The bound on the error in ulps of the 192-bit result: 662 for |y| <= |x| and x > 0.
    ulong err = 662;
    z[Atan2Sign] = sz;
    var pi = new ulong[5];
    if (inv) {
      // atan(y/x) = +/-pi/2 - atan(x/y).
      Atan2Set(pi, 0xc90fdaa22168c234UL, 0xc4c6628b80dc1cd1UL, 0x29024e088a67cc74UL, 1);
      if (z[Atan2Sign] == 0) {
        z[Atan2Sign] = 1;
        Atan2Add(z, pi, z);
      } else {
        Atan2Add(z, pi, z);
        z[Atan2Sign] = 1;
      }

      err = 524;
    }

    if (x < 0) {
      // To the opposite quadrant: subtract pi from a positive angle, add it to a negative one.
      Atan2Set(pi, 0xc90fdaa22168c234UL, 0xc4c6628b80dc1cd1UL, 0x29024e088a67cc74UL, 2);
      if (z[Atan2Sign] == 0) {
        z[Atan2Sign] = 1;
        Atan2Add(z, pi, z);
        z[Atan2Sign] = 1;
      } else {
        Atan2Add(z, pi, z);
      }

      err = 266;
    }

    return Atan2ToDouble(z, err);
  }

  /// <summary>Sets <paramref name="r"/> to the positive number with the significand words and the exponent.</summary>
  private static void Atan2Set(ulong[] r, ulong high, ulong middle, ulong low, long exponent) {
    r[Atan2High] = high;
    r[Atan2Middle] = middle;
    r[Atan2Low] = low;
    r[Atan2Exponent] = (ulong)exponent;
    r[Atan2Sign] = 0;
  }

  /// <summary>Sets <paramref name="r"/> to the coefficient in row <paramref name="row"/> of <see cref="_atan2Rational"/>.</summary>
  private static void Atan2Coefficient(ulong[] r, int row) {
    Atan2Set(r, _atan2Rational[4 * row], _atan2Rational[4 * row + 1], _atan2Rational[4 * row + 2], _atan2RationalExponent[row]);
    r[Atan2Sign] = _atan2Rational[4 * row + 3];
  }

  /// <summary>
  /// <c>r = a * b</c> with an error below 10 ulps; <paramref name="r"/> may be one of the operands. The
  /// partial products below the third word are left out.
  /// </summary>
  private static void Atan2Multiply(ulong[] r, ulong[] a, ulong[] b) {
    var ex = (long)a[Atan2Exponent] + (long)b[Atan2Exponent];
    var sgn = a[Atan2Sign] ^ b[Atan2Sign];
    var ah = a[Atan2High];
    var am = a[Atan2Middle];
    var al = a[Atan2Low];
    var bh = b[Atan2High];
    var bm = b[Atan2Middle];
    var bl = b[Atan2Low];

    // ah bh in full.
    var h = MultiplyHigh(ah, bh);
    var m = unchecked(ah * bh);

    // ah bm.
    var l = unchecked(ah * bm);
    var carry = MultiplyHigh(ah, bm);
    m = unchecked(m + carry);
    h += m < carry ? 1UL : 0UL;

    // am bh.
    var part = unchecked(am * bh);
    carry = MultiplyHigh(am, bh);
    l = unchecked(l + part);
    var cm = l < part ? 1UL : 0UL;
    m = unchecked(m + carry);
    h += m < carry ? 1UL : 0UL;

    // The high words of ah bl, am bm and al bh.
    var first = MultiplyHigh(ah, bl);
    var rest = unchecked(first + MultiplyHigh(am, bm));
    cm += rest < first ? 1UL : 0UL;
    var third = MultiplyHigh(al, bh);
    rest = unchecked(rest + third);
    cm += rest < third ? 1UL : 0UL;
    l = unchecked(l + rest);
    cm += l < rest ? 1UL : 0UL;

    m = unchecked(m + cm);
    h += m < cm ? 1UL : 0UL;

    // Normalize; a zero operand leaves a zero.
    if (h >> 63 == 0) {
      h = h << 1 | m >> 63;
      m = m << 1 | l >> 63;
      l <<= 1;
      ex--;
    }

    r[Atan2High] = h;
    r[Atan2Middle] = m;
    r[Atan2Low] = l;
    r[Atan2Exponent] = (ulong)ex;
    r[Atan2Sign] = sgn;
  }

  /// <summary>
  /// -1, 0 or 1 as <c>|a|</c> is below, equal to or above <c>|b|</c>, for a nonzero <paramref name="a"/>:
  /// the first operand of every sum of the accurate path is (a power series of positive terms, 1, a
  /// reciprocal, a dividend, <c>pi</c>), only the second can be zero.
  /// </summary>
  private static int Atan2CompareMagnitudes(ulong[] a, ulong[] b) {
    if (b[Atan2High] == 0)
      return 1;

    var ea = (long)a[Atan2Exponent];
    var eb = (long)b[Atan2Exponent];
    if (ea != eb)
      return ea > eb ? 1 : -1;

    for (var i = Atan2High; i <= Atan2Low; i++)
      if (a[i] != b[i])
        return a[i] > b[i] ? 1 : -1;

    return 0;
  }

  /// <summary>The significand of <paramref name="b"/> shifted right by <paramref name="k"/> bits into <paramref name="a"/>.</summary>
  private static void Atan2ShiftRight(ulong[] a, ulong[] b, int k) {
    var h = b[Atan2High];
    var m = b[Atan2Middle];
    var l = b[Atan2Low];
    if (k == 0) {
      a[Atan2High] = h;
      a[Atan2Middle] = m;
      a[Atan2Low] = l;
    } else if (k < 64) {
      a[Atan2High] = h >> k;
      a[Atan2Middle] = h << (64 - k) | m >> k;
      a[Atan2Low] = m << (64 - k) | l >> k;
    } else if (k == 64) {
      a[Atan2High] = 0;
      a[Atan2Middle] = h;
      a[Atan2Low] = m;
    } else if (k < 128) {
      a[Atan2High] = 0;
      a[Atan2Middle] = h >> (k - 64);
      a[Atan2Low] = h << (128 - k) | m >> (k - 64);
    } else if (k < 192) {
      a[Atan2High] = 0;
      a[Atan2Middle] = 0;
      a[Atan2Low] = h >> (k - 128);
    } else {
      a[Atan2High] = 0;
      a[Atan2Middle] = 0;
      a[Atan2Low] = 0;
    }
  }

  /// <summary>The significand of <paramref name="b"/> shifted left by <paramref name="k"/> bits (below 192) into <paramref name="a"/>.</summary>
  private static void Atan2ShiftLeft(ulong[] a, ulong[] b, int k) {
    var h = b[Atan2High];
    var m = b[Atan2Middle];
    var l = b[Atan2Low];
    if (k == 0) {
      a[Atan2High] = h;
      a[Atan2Middle] = m;
      a[Atan2Low] = l;
    } else if (k < 64) {
      a[Atan2High] = h << k | m >> (64 - k);
      a[Atan2Middle] = m << k | l >> (64 - k);
      a[Atan2Low] = l << k;
    } else if (k == 64) {
      a[Atan2High] = m;
      a[Atan2Middle] = l;
      a[Atan2Low] = 0;
    } else if (k < 128) {
      a[Atan2High] = m << (k - 64) | l >> (128 - k);
      a[Atan2Middle] = l << (k - 64);
      a[Atan2Low] = 0;
    } else {
      // k < 192: the shift normalizes a nonzero difference.
      a[Atan2High] = l << (k - 128);
      a[Atan2Middle] = 0;
      a[Atan2Low] = 0;
    }
  }

  /// <summary>The number of leading zero bits of a 192-bit significand.</summary>
  private static int Atan2LeadingZeros(ulong[] a) {
    if (a[Atan2High] != 0)
      return BitOperations.LeadingZeroCount(a[Atan2High]);

    if (a[Atan2Middle] != 0)
      return 64 + BitOperations.LeadingZeroCount(a[Atan2Middle]);

    return 128 + BitOperations.LeadingZeroCount(a[Atan2Low]);
  }

  /// <summary><c>t = a - t</c> on the significands, modulo <c>2^192</c>.</summary>
  private static void Atan2SubtractFrom(ulong[] t, ulong[] a) {
    var l = unchecked(a[Atan2Low] - t[Atan2Low]);
    var borrow = l > a[Atan2Low] ? 1UL : 0UL;
    var m = unchecked(a[Atan2Middle] - t[Atan2Middle]);
    var h = unchecked(a[Atan2High] - t[Atan2High] - (a[Atan2Middle] < t[Atan2Middle] ? 1UL : 0UL));
    h = unchecked(h - (m < borrow ? 1UL : 0UL));
    m = unchecked(m - borrow);
    t[Atan2High] = h;
    t[Atan2Middle] = m;
    t[Atan2Low] = l;
  }

  /// <summary><c>r = a + b</c> with an error below 2 ulps; <paramref name="r"/> may be one of the operands.</summary>
  private static void Atan2Add(ulong[] r, ulong[] a, ulong[] b) {
    var order = Atan2CompareMagnitudes(a, b);
    if (order == 0) {
      // |a| = |b|: 0 or 2a, exactly.
      if ((a[Atan2Sign] ^ b[Atan2Sign]) != 0) {
        Atan2Set(r, 0, 0, 0, -1076);
        return;
      }

      var exponent = (long)a[Atan2Exponent] + 1;
      var sign = a[Atan2Sign];
      Atan2Set(r, a[Atan2High], a[Atan2Middle], a[Atan2Low], exponent);
      r[Atan2Sign] = sign;
      return;
    }

    // From here |a| > |b|.
    if (order < 0) {
      var swap = a;
      a = b;
      b = swap;
    }

    var ea = (long)a[Atan2Exponent];
    var sa = a[Atan2Sign];
    var sh = (ulong)(ea - (long)b[Atan2Exponent]);
    var t = new ulong[5];
    Atan2ShiftRight(t, b, (int)sh);
    long ex;
    if ((sa ^ b[Atan2Sign]) != 0) {
      // A subtraction, exact when nothing of b was shifted out or the cancellation is at most a bit.
      Atan2SubtractFrom(t, a);
      var cancelled = (ulong)Atan2LeadingZeros(t);
      if (cancelled <= 1 || sh == 0) {
        Atan2ShiftLeft(r, t, (int)cancelled);
        ex = ea - (long)cancelled;
      } else {
        // Only for sh = 1: subtract again with both shifted left, so that no bit of b is lost.
        Atan2ShiftLeft(t, b, (int)(cancelled - sh));
        Atan2ShiftLeft(r, a, (int)cancelled);
        Atan2SubtractFrom(t, r);
        var further = (ulong)Atan2LeadingZeros(t);
        Atan2ShiftLeft(r, t, (int)further);
        ex = ea - (long)(cancelled + further);
      }
    } else {
      // An addition; a carry out of the top word shifts the sum right by one bit.
      var ah = a[Atan2High];
      var am = a[Atan2Middle];
      var al = a[Atan2Low];
      var l = unchecked(al + t[Atan2Low]);
      var cl = l < al ? 1UL : 0UL;
      var m = unchecked(am + t[Atan2Middle]);
      var h = unchecked(ah + t[Atan2High] + (m < am ? 1UL : 0UL));
      var ch = h < ah || (h == ah && m < am) ? 1UL : 0UL;
      var before = m;
      m = unchecked(m + cl);
      h = unchecked(h + (m < before ? 1UL : 0UL));
      ch += h == 0 && m < cl ? 1UL : 0UL;
      if (ch != 0) {
        ex = ea + 1;
        l = m << 63 | l >> 1;
        m = h << 63 | m >> 1;
        h = ch << 63 | h >> 1;
      } else {
        ex = ea;
      }

      r[Atan2High] = h;
      r[Atan2Middle] = m;
      r[Atan2Low] = l;
    }

    r[Atan2Exponent] = (ulong)ex;
    r[Atan2Sign] = sa;
  }

  /// <summary>Sets <paramref name="a"/> to the finite nonzero double <paramref name="x"/>, exactly.</summary>
  private static void Atan2FromDouble(ulong[] a, double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = bits & 0x7fffffffffffffffUL;
    var e = (long)(ax >> 52);
    if (e != 0) {
      Atan2Set(a, 1UL << 63 | ax << 11, 0, 0, e - 0x3fe);
    } else {
      var shift = BitOperations.LeadingZeroCount(ax);
      Atan2Set(a, ax << shift, 0, 0, -0x3f2 - shift);
    }

    a[Atan2Sign] = bits >> 63;
  }

  /// <summary>
  /// <paramref name="a"/> rounded to a double, where <paramref name="err"/> bounds the error of the
  /// lowest word in ulps; 0 converts a number that is a double, or truncates.
  /// </summary>
  private static double Atan2ToDouble(ulong[] a, ulong err) {
    var ex = (long)a[Atan2Exponent];
    var s = _atan2Signs[(int)a[Atan2Sign]];

    // Only the tiny quotients get here, between 2^-1075 and 2^-1074 and never on either end.
    if (ex <= -1074)
      return s * 5e-324 * 0.75;

    var hh = a[Atan2High];
    var mm = a[Atan2Middle];
    var ll = a[Atan2Low];
    var low = hh & 0x7ff;
    if (ex <= -1022) {
      // A subnormal result: shift the significand right by 1 to 52 bits, with a sticky bit.
      var sh = (int)(-1021 - ex);
      ll = mm << (64 - sh) | ll >> sh | (ll > 0 ? 1UL : 0UL);
      mm = hh << (64 - sh) | mm >> sh;
      hh >>= sh;
      low = hh & 0x7ff;
      ex += sh;
    }

    // The 53 bits of the result, and what the rest is worth: below or above half an ulp. An exact
    // half (the low 11 bits 0x400 and nothing below) with an error bound is a case the error bound
    // cannot decide, which upstream reports as an unexpected worst case and aborts on; it does not occur.
    var h = (double)(hh >> 11);
    double l;
    if (err == 0)
      l = 0;
    else if (low < 0x400)
      l = 0.25;
    else
      l = 0.75;

    h = Math.FusedMultiplyAdd(l, s, s * h);
    h *= 2.220446049250313e-16;
    return h * BitConverter.Int64BitsToDouble(0x3ff0000000000000L + ((ex - 1) << 52));
  }

  /// <summary><c>r = 1/a</c> for a nonzero <paramref name="a"/>, by one Newton step from the double reciprocal.</summary>
  private static void Atan2Inverse(ulong[] r, ulong[] a) {
    var d = Atan2ToDouble(a, 0);
    var subnormal = Math.Abs(d) < 2.2250738585072014e-308;
    if (subnormal)
      d *= 9007199254740992.0;

    Atan2FromDouble(r, 1.0 / d);
    if (subnormal)
      r[Atan2Exponent] = (ulong)((long)r[Atan2Exponent] + 53);

    // r + r (1 - a r).
    var q = new ulong[5];
    var one = new ulong[5];
    Atan2Set(one, 1UL << 63, 0, 0, 1);
    Atan2Multiply(q, a, r);
    q[Atan2Sign] = 1 - q[Atan2Sign];
    Atan2Add(q, one, q);
    Atan2Multiply(q, r, q);
    Atan2Add(r, r, q);
  }

  /// <summary><c>r = b/a</c> for a nonzero <paramref name="a"/>, with a relative error below <c>2^-185.53</c>.</summary>
  private static void Atan2DivideTints(ulong[] r, ulong[] b, ulong[] a) {
    // Karp and Markstein: r = b y + y (b - a b y) for y = 1/a.
    var y = new ulong[5];
    var z = new ulong[5];
    Atan2Inverse(y, a);
    Atan2Multiply(r, y, b);
    Atan2Multiply(z, a, r);
    z[Atan2Sign] = 1 - z[Atan2Sign];
    Atan2Add(z, b, z);
    Atan2Multiply(z, y, z);
    Atan2Add(r, r, z);
  }

  /// <summary><c>r = b/a</c> for finite nonzero doubles; see <see cref="Atan2DivideTints"/>.</summary>
  private static void Atan2DivideDoubles(ulong[] r, double b, double a) {
    var divisor = new ulong[5];
    var dividend = new ulong[5];
    Atan2FromDouble(divisor, a);
    Atan2FromDouble(dividend, b);
    Atan2DivideTints(r, dividend, divisor);
  }

  /// <summary>The numerator of the rational approximation in <see cref="Atan2F"/>, even, from the constant term on.</summary>
  private static readonly double[] _atan2FNumerator = [
    1.0, 2.506848521335565, 2.2855336234350774, 0.9227540611112051,
    0.15965700667756133, 0.0093982071883745, 8.116266383809054e-05
  ];

  /// <summary>The denominator of the rational approximation in <see cref="Atan2F"/>, even.</summary>
  private static readonly double[] _atan2FDenominator = [
    1.0, 2.840181854668896, 3.03226090832491, 1.5083284691366383,
    0.35061013533424623, 0.03311601651598859, 0.0008307046818566012
  ];

  /// <summary>
  /// The offset of each octant of <see cref="Atan2F"/>, by sign of y, sign of x and whether
  /// <c>|y| &gt; |x|</c>: 0, <c>pi/2</c> and <c>pi</c> as doubles.
  /// </summary>
  private static readonly double[] _atan2FOctants = [
    0.0, 1.5707963267948966, 3.141592653589793, 1.5707963267948966,
    -0.0, -1.5707963267948966, -3.141592653589793, -1.5707963267948966
  ];

  /// <summary>The rest of each offset in <see cref="_atan2FOctants"/>, the multiples of <c>0x1.1a62633145c07p-54</c>.</summary>
  private static readonly double[] _atan2FOctantRests = [
    0.0, 6.123233995736766e-17, 1.2246467991473532e-16, 6.123233995736766e-17,
    -0.0, -6.123233995736766e-17, -1.2246467991473532e-16, -6.123233995736766e-17
  ];

  /// <summary>The series of <c>atan(z)/z</c> in <c>z^2</c> for the accurate path of <see cref="Atan2F"/>, as double-doubles.</summary>
  private static readonly double[] _atan2FAccurate = [
    1.0, -9.999371390980276e-27, -0.3333333333333333, -1.8503696081891694e-17,
    0.2, -1.1109570448589454e-17, -0.14285714285714285, -6.906377603268134e-18,
    0.11111111111111104, -6.04986207547041e-19, -0.0909090909090874, -4.813186107306423e-18,
    0.07692307692296789, 6.0635784048294865e-18, -0.06666666666423005, 5.357368392422845e-19,
    0.05882352937094788, -3.0234229312063074e-18, -0.052631578418319884, 7.121906314230013e-19,
    0.047619042181978474, 3.2606056478649954e-18, -0.04347821570541953, 2.1474249882470664e-18,
    0.039999692056834395, -1.4496021955960109e-18, -0.037035291887394496, 8.394614713005368e-19,
    0.03447445331733282, 3.57321674047701e-19, -0.03222458593057924, -6.05919930630252e-19,
    0.030187892413408284, 8.550359777848272e-19, -0.028231467664296316, 8.645756239029875e-19,
    0.026160406443643782, -1.1013132206776929e-18, -0.02372363694711401, 5.82053126354201e-19,
    0.0206884675894418, 8.78953163493978e-19, -0.016981732349686016, 9.51948710384928e-19,
    0.012817485672589527, -7.742239346647683e-19, -0.008687313963361743, -5.63071564570556e-19,
    0.005163618439611815, 3.2806307955849715e-19, -0.00262691892479603, -1.373777510443008e-19,
    0.0011135875725313154, -6.618746076431752e-20, -0.0003808088264492978, 1.5620808802045265e-20,
    0.00010056717515235685, -5.194627331100907e-21, -1.919552508092125e-05, 8.202890655529531e-22,
    2.35167210652331e-06, -1.3883775330103416e-22, -1.3863591848022874e-07, -1.0947593470915086e-23
  ];

  /// <summary>
  /// The arc tangent of <c>y/x</c> for <c>float</c>s, in radians, correctly rounded; the arguments in the
  /// order of C's <c>atan2f</c>. A rational approximation in doubles of <c>atan</c> of the ratio of the
  /// smaller magnitude to the larger one, added to the octant's offset; near a rounding boundary a
  /// series in double-double arithmetic decides, and a tiny ratio goes by <c>z - z^3/3</c>.
  /// </summary>
  /// <param name="y">The ordinate.</param>
  /// <param name="x">The abscissa.</param>
  /// <returns>The angle in <c>[-pi, pi]</c>.</returns>
  public static float Atan2F(float y, float x) {
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    var uy = (uint)BitConverter.SingleToInt32Bits(y);
    var ax = ux & 0x7fffffffu;
    var ay = uy & 0x7fffffffu;
    var ySign = _atan2Signs[(int)(uy >> 31)];
    if (ay >= 0xffu << 23 || ax >= 0xffu << 23) {
      // A NaN, or y or x infinite.
      if (ay > 0xffu << 23 || ax > 0xffu << 23)
        return x + y;

      var yInfinite = ay == 0xffu << 23;
      var xInfinite = ax == 0xffu << 23;
      if (yInfinite && xInfinite)
        return (float)((ux >> 31 != 0 ? 2.356194490192345 : 0.7853981633974483) * ySign);

      if (xInfinite)
        return (float)((ux >> 31 != 0 ? 3.141592653589793 : 0.0) * ySign);

      return (float)(1.5707963267948966 * ySign);
    }

    if (ay == 0) {
      if (ax == 0) {
        var octant = (int)((uy >> 31) * 4 + (ux >> 31) * 2);
        if (ux >> 31 != 0)
          return (float)(_atan2FOctants[octant] + _atan2FOctantRests[octant]);

        return (float)_atan2FOctants[octant];
      }

      if (ux >> 31 == 0)
        return (float)(0.0 * ySign);
    }

    var gt = ay > ax ? 1 : 0;
    var i = (int)((uy >> 31) * 4 + (ux >> 31) * 2) + gt;

    // z = x/y if |y| > |x|, y/x otherwise; written as upstream with the factors 0 and 1, which fixes the sign of a zero.
    var zx = (double)x;
    var zy = (double)y;
    var z = (_atan2FChoice[gt] * zx + _atan2FChoice[1 - gt] * zy) / (_atan2FChoice[gt] * zy + _atan2FChoice[1 - gt] * zx);
    double r;
    var d = (int)ax - (int)ay;
    if (d < 27 << 23 && d > -(27 << 23)) {
      var z2 = z * z;
      var z4 = z2 * z2;
      var z8 = z4 * z4;
      var cn0 = _atan2FNumerator[0] + z2 * _atan2FNumerator[1];
      var cn2 = _atan2FNumerator[2] + z2 * _atan2FNumerator[3];
      var cn4 = _atan2FNumerator[4] + z2 * _atan2FNumerator[5];
      var cn6 = _atan2FNumerator[6];
      cn0 += z4 * cn2;
      cn4 += z4 * cn6;
      cn0 += z8 * cn4;
      var cd0 = _atan2FDenominator[0] + z2 * _atan2FDenominator[1];
      var cd2 = _atan2FDenominator[2] + z2 * _atan2FDenominator[3];
      var cd4 = _atan2FDenominator[4] + z2 * _atan2FDenominator[5];
      var cd6 = _atan2FDenominator[6];
      cd0 += z4 * cd2;
      cd4 += z4 * cd6;
      cd0 += z8 * cd4;
      r = cn0 / cd0;
    } else {
      r = 1;
    }

    z *= _atan2Signs[gt];
    r = z * r + _atan2FOctants[i];
    var res = (ulong)BitConverter.DoubleToInt64Bits(r);
    if (((res + 8) & 0xfffffff) <= 16) {
      // Close to a rounding boundary of float.
      if (ay < ax && (ax - ay) >> 23 >= 25)
        return Atan2FTiny(y, x);

      double zh;
      double zl;
      if (gt == 0) {
        zh = zy / zx;
        zl = Math.FusedMultiplyAdd(zh, -zx, zy) / zx;
      } else {
        zh = zx / zy;
        zl = Math.FusedMultiplyAdd(zh, -zy, zx) / zy;
      }

      var low = new double[1];
      var z2h = Atan2FMultiply(zh, zl, zh, zl, low);
      var z2l = low[0];

      // The series by Horner's scheme in double-double.
      var ph = _atan2FAccurate[62];
      var pl = _atan2FAccurate[63];
      for (var k = 30; k >= 0; k--) {
        ph = Atan2FMultiply(z2h, z2l, ph, pl, low);
        pl = low[0];
        var th = ph + _atan2FAccurate[2 * k];
        var tl = (_atan2FAccurate[2 * k] - th) + ph;
        ph = th;
        pl += tl + _atan2FAccurate[2 * k + 1];
      }

      zh *= _atan2Signs[gt];
      zl *= _atan2Signs[gt];
      ph = Atan2FMultiply(zh, zl, ph, pl, low);
      pl = low[0];
      var sh = ph + _atan2FOctants[i];
      var sl = ((_atan2FOctants[i] - sh) + ph) + pl + _atan2FOctantRests[i];

      // Round to float, the rest moved off an exact midpoint of float.
      var head = (double)(float)sh;
      var dh = sh - head;
      var tm = dh + sl;
      if (head + head * 8.673617379884035e-19 == head - head * 8.673617379884035e-19) {
        var limit = ((ulong)BitConverter.DoubleToInt64Bits(head) & 0x7ffUL << 52) - (24UL << 52);
        if (Math.Abs(tm) > BitConverter.Int64BitsToDouble((long)limit))
          tm *= 1.25;
        else
          tm *= 0.75;
      }

      r = head + tm;
    }

    return (float)r;
  }

  /// <summary>The factors that pick the numerator and the denominator of the ratio in <see cref="Atan2F"/>.</summary>
  private static readonly double[] _atan2FChoice = [0.0, 1.0];

  /// <summary>
  /// <c>(xh + xl)(ch + cl)</c> in double-double for <see cref="Atan2F"/>: returns the high part, the low
  /// part goes to <c>low[0]</c>.
  /// </summary>
  private static double Atan2FMultiply(double xh, double xl, double ch, double cl, double[] low) {
    var ahlh = ch * xl;
    var alhh = cl * xh;
    var ahhh = ch * xh;
    var ahhl = Math.FusedMultiplyAdd(ch, xh, -ahhh);
    ahhl += alhh + ahlh;
    var high = ahhh + ahhl;
    low[0] = (ahhh - high) + ahhl;
    return high;
  }

  /// <summary>
  /// <see cref="Atan2F"/> for <c>|y/x| &lt; 2^-24</c> or so, where <c>atan(z) = z - z^3/3 + ...</c> rounds
  /// like the quotient <c>z</c> moved a little towards zero: the double quotient, taken off a midpoint
  /// of float so that the conversion rounds once.
  /// </summary>
  private static float Atan2FTiny(float y, float x) {
    var z = (double)y / (double)x;

    // Upstream also computes e = (y - z x)/x - z^3/3 and moves z by the sign of z * e. But a quotient
    // of two floats that rounds to a double of at most 25 bits is that double exactly: then e is
    // -z^3/3, of the other sign (or zero), and z always moves towards zero.
    var t = (ulong)BitConverter.DoubleToInt64Bits(z);
    if ((t & 0xfffffffUL) == 0)
      t--;

    return (float)BitConverter.Int64BitsToDouble((long)t);
  }

}
