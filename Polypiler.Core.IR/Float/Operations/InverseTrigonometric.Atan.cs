// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The arc tangent, correctly rounded in both formats. Converted from CORE-MATH's
/// <c>src/binary64/atan/atan.c</c> and <c>src/binary32/atan/atanf.c</c> (MIT); see
/// <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseTrigonometric.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// For <c>j</c> from 0 to 128: <c>tan(j pi/256)</c> rounded, then <c>atan</c> of that minus
  /// <c>j pi/256</c>; the last row is zero.
  /// </summary>
  private static readonly double[] _atanTangents = [
    0.0, 0.0, 0.012272462379566276, 3.555765684818314e-19,
    0.024548622108925444, 5.834854161337051e-20, 0.03683218099484564, 2.1067819371007694e-18,
    0.049126849769467254, -9.075861541377107e-20, 0.061436352581593766, 1.7816411675633553e-18,
    0.07376443152244928, -5.278932630666519e-18, 0.0861148511976279, -1.5678193105312258e-18,
    0.09849140335716425, -5.259051390942549e-18, 0.11089791159591303, 4.337394303627698e-18,
    0.12333823613673868, 1.8297202500785388e-18, 0.13581627870938773, 3.4130533520298278e-18,
    0.14833598753834742, -3.9912431039375455e-18, 0.16090136245348916, -1.085108372224721e-17,
    0.17351646013785577, 5.445710695806514e-18, 0.18618539952758373, -3.3242042151979862e-18,
    0.198912367379658, -8.07240081828309e-19, 0.21170162402398335, -5.036514114884015e-18,
    0.2245575093171293, -8.524537892459856e-18, 0.23748444881607017, -1.2885716860577882e-17,
    0.25048696019130545, -8.83555859166591e-18, 0.263569659899918, -9.610855418635516e-18,
    0.2767372701404143, -1.1577675320243282e-17, 0.2899946261126061, -8.101130151610375e-19,
    0.3033466836073424, 1.1691177889169384e-17, 0.3167985269526038, 1.285806508222281e-17,
    0.3303553773443339, -2.1827616435782727e-17, 0.3440226015924263, -1.0384601646900657e-17,
    0.3578057213145241, -9.056713942825596e-18, 0.37171042261274345, 2.354338854400786e-17,
    0.38574256627112125, 8.757134942172942e-18, 0.3999081985145372, -1.485057909629851e-17,
    0.41421356237309503, -1.2247952842778023e-17, 0.4286651096994995, 9.563183748944196e-18,
    0.44326951389086433, -2.895975402054566e-18, 0.4580336833706724, -1.0789951953407575e-17,
    0.4729647758913199, -1.423462408803522e-17, 0.48807021372286286, -1.8748963792094843e-18,
    0.5033576997992942, -2.3181776247031463e-17, 0.5188352348999757, 1.7981389592709095e-18,
    0.5345111359507917, 4.0932084625426755e-17, 0.5503940555372641, 2.781630117382078e-17,
    0.566493002730344, -4.42039855975346e-18, 0.5828173653349761, -1.4827496899423528e-17,
    0.5993769336819238, 2.175212986778241e-17, 0.6161819260948661, 3.557374281725727e-17,
    0.6332430161775692, 3.279927931291694e-17, 0.6505713620801533, 1.4944554437080147e-17,
    0.6681786379192989, -2.837423353933172e-17, 0.6860770675448629, -3.709036277500186e-17,
    0.7042794608650442, -1.1726428260896895e-17, 0.7227992529642059, -3.057415347618549e-17,
    0.7416505462720354, 2.982922573366765e-17, 0.7608481560702512, -3.1210538940343614e-17,
    0.7804076596539437, 1.2693028137126565e-17, 0.8003454494993201, -1.8667355083286445e-17,
    0.8206787908286604, 1.3697615920272132e-17, 0.8414258840072548, -1.3781600473511285e-18,
    0.8626059322567399, -1.1431434768209771e-17, 0.8842392152253499, 2.3060634500897344e-18,
    0.9063471690191471, -1.1839022437919416e-17, 0.9289524733703675, -4.0486446058344625e-19,
    0.9520791467009253, -2.2825930096084175e-17, 0.9757526499323765, 2.2758940764856317e-18,
    1.0, 0.0, 1.0248498941502273, -5.192502513549596e-17,
    1.0503328462398598, -3.397402228340294e-17, 1.076481336415266, 3.330404344066333e-17,
    1.1033299757334756, -3.95499878138913e-17, 1.1309156874988273, 3.997022789196374e-17,
    1.1592779073334347, -1.1986546386526189e-17, 1.1884588042829662, -4.2345963685969836e-17,
    1.2185035255879764, 8.55006801533348e-18, 1.2494604681335788, 2.439868444909347e-17,
    1.2813815800365544, -4.140089560728253e-17, 1.31432269635108, -2.720740369445052e-17,
    1.3483439134867201, -1.392875322075665e-17, 1.3835100076528737, -3.258872748382433e-17,
    1.4198909034940925, 2.421912180525479e-17, 1.4575622000871054, -1.1018931582399467e-17,
    1.496605762665489, -2.1526140115556586e-17, 1.5371103898618819, -2.8751398359022675e-17,
    1.5791725679602089, -2.8596003249434097e-17, 1.6228973256934547, 2.8067634844374685e-17,
    1.668399205583507, 6.289659338573465e-18, 1.7158033707956641, 2.015230798772186e-17,
    1.7652468700941915, -1.4024935317617867e-17, 1.8168800878924023, 3.981317068068186e-18,
    1.8708684117893895, -6.5460218324344e-18, 1.9273941566300643, 1.6589883917528817e-17,
    1.986658792343365, -9.623226887484272e-18, 2.04888553303075, -1.3947227916996688e-17,
    2.1143223575486405, -1.5098616889246607e-17, 2.183245547884152, -9.178521891131575e-18,
    2.2559638519291587, 1.0580493190911606e-17, 2.3328234031013504, 3.1332035083654735e-17,
    2.414213562373095, -1.836025624937195e-17, 2.5005738909942568, -2.207868765763124e-17,
    2.5924025177380727, 2.172588189518176e-17, 2.690266237279613, 2.3386835224767844e-17,
    2.7948127724904768, -2.637365777071167e-18, 2.9067857616655353, -1.850484486824546e-17,
    3.027043204317774, -4.6444931832965504e-18, 3.1565803339407887, 4.4892674471274045e-18,
    3.2965582089383205, 8.0329653691558e-18, 3.448339762033024, -2.650011172948169e-18,
    3.6135356813074284, -1.4498116294014568e-18, 3.794063400088301, 6.2559686487820676e-18,
    3.9922237837700845, 2.5219389745244403e-18, 4.2108020335028, -2.0627827669039212e-17,
    4.453202224414411, 1.0293977616949511e-17, 4.723629327882301, 2.9204606579431894e-18,
    5.027339492125848, -1.1242200673498819e-18, 5.370990435003729, 3.684977524078479e-18,
    5.76314200511881, 6.917964102580692e-18, 6.214987771089038, 3.640640587118668e-18,
    6.7414524054149885, 4.0170380453361145e-18, 7.362887641324245, 7.368009079543718e-18,
    8.107785803676908, 6.09083500396278e-18, 9.017302360424734, 4.926430357629524e-18,
    10.15317038760886, -5.151761162135913e-18, 11.612398861435247, 5.516144024233879e-18,
    13.556669242352426, -1.6893253257870288e-18, 16.27700795993541, 6.325118845392731e-18,
    20.355467624987188, 5.728461403643232e-20, 27.150170665699697, -9.090111443989645e-20,
    40.7354838720833, -1.0077222670348913e-18, 81.48324020654617, 1.277782632177919e-19,
    0.0, 0.0
  ];

  /// <summary>
  /// A crude piecewise quadratic approximation of <c>atan</c> in fixed point that picks the index
  /// into <see cref="_atanTangents"/>: three coefficients per binade-half from 1 to 30.
  /// </summary>
  private static readonly ulong[] _atanIndex = [
    419, 81, 0, 500, 81, 0, 582, 163, 0, 745, 163, 0,
    908, 326, 0, 1234, 326, 0, 1559, 651, 0, 2210, 650, 1,
    2860, 1299, 3, 4156, 1293, 4, 5444, 2569, 24, 7989, 2520, 32,
    10476, 4917, 168, 15224, 4576, 200, 19601, 8341, 838, 27105, 6648, 731,
    33036, 10210, 1998, 41266, 6292, 1117, 46469, 7926, 2048, 52375, 4038, 849,
    55587, 4591, 1291, 58906, 2172, 479, 60612, 2390, 688, 62325, 1107, 247,
    63192, 1207, 349, 64056, 556, 124, 64491, 605, 175, 64923, 278, 62,
    65141, 303, 88, 65358, 139, 31, 65467, 151, 44
  ];

  /// <summary>The odd polynomial of the fast path, in <c>h^2</c>.</summary>
  private static readonly double[] _atanFast = [
    1.0, -0.333333333333331, 0.1999999997326265, -0.1428473928322857
  ];

  /// <summary>The odd polynomial of the fast path for <c>|x| &lt; 0x1.b21c475e6362ap-8</c>, from <c>x^3</c> on.</summary>
  private static readonly double[] _atanSmall = [
    -0.3333333333333333, 0.199999999999994, -0.1428571424743179, 0.11110113960028833
  ];

  /// <summary>The first three coefficients of <c>(atan(h) - h) / h^3</c> in <c>h^2</c>, as double-doubles.</summary>
  private static readonly double[] _atanAccurate = [
    -0.3333333333333333, -1.850371707708594e-17, 0.2, -1.1102230246265417e-17,
    -0.14285714285714285, -7.930161522310759e-18
  ];

  /// <summary>The next four coefficients of <see cref="_atanAccurate"/>, in plain doubles.</summary>
  private static readonly double[] _atanAccurateTail = [
    0.1111111111111111, -0.09090909090907358, 0.07692307637971313, -0.06665779689246536
  ];

  /// <summary>The inputs whose arc tangent the refinement cannot round: <c>|x|</c>, then the result as a sum.</summary>
  private static readonly double[] _atanExceptions = [
    0.008233142193297766, 0.008232956174029788, 4.336808689942018e-19,
    0.007384971208758445, 0.007384836959791079, 2.168404344971009e-19,
    0.7414903477293427, 0.6380326489758434, 1.5407439555097887e-33,
    0.2076186991593222, 0.20471038425931956, -1.925929944387236e-34,
    0.006759920994341059, 0.006759818028849137, -2.407412430484045e-35,
    0.007037202196066859, 0.007037086033572287, -1.925929944387236e-34,
    0.015267686677464106, 0.015266500532616923, -1.925929944387236e-34,
    0.0077692739126573535, 0.00776911759633952, -1.925929944387236e-34,
    0.021734483175341882, 0.021731061777092855, 4.81482486096809e-35,
    0.7414903477293427, 0.6380326489758434, 7.703719777548943e-34,
    0.23017978867661545, 0.22623913698634193, -7.703719777548943e-34,
    0.006759920994341059, 0.006759818028849137, -2.407412430484045e-35
  ];

  /// <summary>The numerator of the rational approximation in <see cref="AtanF"/>, odd, from <c>x</c> on.</summary>
  private static readonly double[] _atanFNumerator = [
    0.33000489885804146, 0.8269936260181494, 0.7536692267812706, 0.3041250206581639,
    0.052585465033265374, 0.0030928116297212196, 2.6680447001914062e-05
  ];

  /// <summary>The denominator of the rational approximation in <see cref="AtanF"/>, even.</summary>
  private static readonly double[] _atanFDenominator = [
    0.3300048988580414, 0.9369952589708292, 1.0, 0.4972028591750377,
    0.1155090060414157, 0.0109022453539874, 0.00027322693677761577
  ];

  /// <summary>
  /// The arc tangent of <c>x</c> in radians, correctly rounded. Tiny arguments by a short series, huge
  /// ones to <c>pi/2</c> or by <c>pi/2 - atan(1/x)</c>; the others reduced by the nearest
  /// <c>tan(j pi/256)</c> through <c>atan(x) = j pi/256 + atan((x - t)/(1 + x t))</c> and a short odd
  /// polynomial. When that does not round safely, the same reduction in double-double arithmetic
  /// decides, and the few inputs even that leaves open come from a table.
  /// </summary>
  /// <param name="x">The tangent.</param>
  /// <returns>The angle in <c>[-pi/2, pi/2]</c>.</returns>
  public static double Atan(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var at = bits & ~0UL >> 1;
    var i = (long)(at >> 51) - 2030;
    if (at < 0x3f7b21c475e6362aUL) {
      // |x| < 0x1.b21c475e6362ap-8.
      if (at == 0)
        return x;

      // Below 2^-27, x - x * 2^-54 rounds like atan(x).
      if (at < 0x3e40000000000000UL)
        return Math.FusedMultiplyAdd(-5.551115123125783e-17, x, x);

      var x2 = x * x;
      var x3 = x * x2;
      var x4 = x2 * x2;
      var f = x3 * ((_atanSmall[0] + x2 * _atanSmall[1]) + x4 * (_atanSmall[2] + x2 * _atanSmall[3]));
      var epsp = f * 1.2212453270876722e-15;
      var epsm = f * 6.106226635438361e-16;
      var upper = (f + epsp) + x;
      var lower = (f - epsm) + x;
      if (upper == lower)
        return upper;

      return AtanRefine(x, upper);
    }

    double h;
    double ah;
    double al;
    if (at > 0x4062ded8e34a9035UL) {
      // |x| > 0x1.2ded8e34a9035p+7: atan(x) = sign(x) pi/2 + atan(-1/x).
      ah = CopySign(1.5707963267948966, x);
      al = CopySign(6.123233995736766e-17, x);
      if (at >= 0x434d02967c31cdb5UL) {
        // |x| >= 0x1.d02967c31cdb5p+53, where pi/2 is the rounded result; or a NaN.
        if (at > 0x7ffUL << 52)
          return x + x;

        return ah + al;
      }

      h = -1.0 / x;
    } else {
      // 1 <= i <= 30: the quadratic of the half binade gives j, then h = (x - t)/(1 + x t), t = tan(j pi/256).
      var u = bits & ~0UL >> 13;
      var ut = u >> (51 - 16);
      var ut2 = ut * ut >> 16;
      var row = (int)i * 3;
      var j = (int)(((_atanIndex[row] << 16) + ut * _atanIndex[row + 1] - ut2 * _atanIndex[row + 2]) >> (16 + 9));
      var ta = CopySign(1.0, x) * _atanTangents[2 * j];
      var id = CopySign(1.0, x) * (double)j;
      al = CopySign(1.0, x) * _atanTangents[2 * j + 1] + 4.2111741079360304e-17 * id;
      h = (x - ta) / (1 + x * ta);
      ah = 0.012271846303085088 * id;
    }

    var h2 = h * h;
    var h4 = h2 * h2;
    var p = (_atanFast[0] + h2 * _atanFast[1]) + h4 * (_atanFast[2] + h2 * _atanFast[3]);
    al = Math.FusedMultiplyAdd(h, p, al);
    var e = h * 8.743006318923108e-16;
    var ub = (al + e) + ah;
    var lb = (al - e) + ah;
    if (ub == lb)
      return ub;

    return AtanRefine(x, ub);
  }

  /// <summary>
  /// The accurate path of <see cref="Atan"/> for <c>|x| &gt;= 2^-27</c>, with <paramref name="a"/> the
  /// fast approximation: the reduction by <c>tan(i pi/256)</c> with <c>i = round(|a| 256/pi)</c> and the
  /// series of the remainder, both in double-double arithmetic.
  /// </summary>
  private static double AtanRefine(double x, double a) {
    // 256.5 < phi < 384.5, whose bits from 44 on hold i = round(|a| * 256/pi), 0 <= i <= 128.
    var phi = (ulong)BitConverter.DoubleToInt64Bits(Math.Abs(a) * 81.48733086305042 + 256.5);
    var i = (int)((phi >> (52 - 8)) & 0xff);
    double h;
    double hl;
    if (i == 128) {
      h = -1.0 / x;
      hl = Math.FusedMultiplyAdd(h, x, 1) * h;
    } else {
      // h + hl = (x - ta) / (1 + x ta) in double-double.
      var ta = CopySign(_atanTangents[2 * i], x);
      var zta = x * ta;
      var ztal = Math.FusedMultiplyAdd(x, ta, -zta);
      var zmta = x - ta;
      var v = 1 + zta;
      var d = 1 - v;
      var ev = (d + zta) - ((d + v) - 1) + ztal;
      var r = 1.0 / v;
      var rl = (Math.FusedMultiplyAdd(r, -v, 1.0) - ev * r) * r;
      h = r * zmta;
      hl = Math.FusedMultiplyAdd(r, zmta, -h) + rl * zmta;
    }

    // h2 = (h + hl)^2, h3 = (h + hl)^3 in double-double; the low parts come back in low[0].
    var low = new double[1];
    var h2 = AtanMultiply(h, hl, h, hl, low);
    var h2l = low[0];
    var h4 = h2 * h2;
    var h3 = AtanMultiply(h, hl, h2, h2l, low);
    var h3l = low[0];

    // The series atan(h) - h = h^3 * P(h^2), the tail in doubles, the head in double-double by Horner.
    var fl = h2 * ((_atanAccurateTail[0] + h2 * _atanAccurateTail[1]) + h4 * (_atanAccurateTail[2] + h2 * _atanAccurateTail[3]));
    var f = _atanAccurate[4] + fl;
    fl = (_atanAccurate[4] - f) + fl + _atanAccurate[5];
    for (var k = 1; k >= 0; k--) {
      f = AtanMultiply(h2, h2l, f, fl, low);
      fl = low[0];
      var th = f + _atanAccurate[2 * k];
      var tl = (_atanAccurate[2 * k] - th) + f;
      f = th;
      fl += tl + _atanAccurate[2 * k + 1];
    }

    f = AtanMultiply(h3, h3l, f, fl, low);
    fl = low[0];

    // ah + al + at = i pi/256 + atan(A[i]) - i pi/256 correction + h + series.
    double ah;
    double al;
    double at;
    if (i == 0) {
      ah = h;
      al = f;
      at = fl;
    } else {
      double df = 0;
      if (i < 128)
        df = CopySign(1.0, x) * _atanTangents[2 * i + 1];

      var id = CopySign((double)i, x);
      ah = 0.012271846303085088 * id;
      al = 4.21117410793604e-17 * id;
      at = -9.794523599166037e-32 * id;
      al = AtanAdd(al, at, df, 0, low);
      at = low[0];
      al = AtanAdd(al, at, h, hl, low);
      at = low[0];
      al = AtanAdd(al, at, f, fl, low);
      at = low[0];
    }

    var v0 = ah + al;
    var v2 = al - (v0 - ah);
    var v1 = v2 + at;
    v2 = at - (v1 - v2);

    // A tie or a result far too close to one: the exceptions, else nudge v1 towards v2.
    var ax = Math.Abs(x);
    var t0 = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var t1 = (ulong)BitConverter.DoubleToInt64Bits(v1);
    if (((t1 + 1) & (~0UL >> 12)) <= 2 || ((t0 >> 52) & 0x7ff) - ((t1 >> 52) & 0x7ff) > 103) {
      for (var j = 0; j < _atanExceptions.Length; j += 3)
        if (ax == _atanExceptions[j])
          return CopySign(_atanExceptions[j + 1], x) + CopySign(1.0, x) * _atanExceptions[j + 2];

      if ((t1 & (~0UL >> 12)) == 0) {
        var w = (ulong)BitConverter.DoubleToInt64Bits(v2);
        if (((w ^ t1) >> 63) != 0)
          t1--;
        else
          t1++;

        v1 = BitConverter.Int64BitsToDouble((long)t1);
      }
    }

    return v1 + v0;
  }

  /// <summary>
  /// <c>(xh + xl)(ch + cl)</c> in double-double: returns the high part, the low part goes to
  /// <c>low[0]</c>.
  /// </summary>
  private static double AtanMultiply(double xh, double xl, double ch, double cl, double[] low) {
    var ahlh = ch * xl;
    var alhh = cl * xh;
    var ahhh = ch * xh;
    var ahhl = Math.FusedMultiplyAdd(ch, xh, -ahhh);
    ahhl += alhh + ahlh;
    var s = ahhh + ahhl;
    low[0] = ahhl - (s - ahhh);
    return s;
  }

  /// <summary>
  /// <c>(xh + xl) + (ch + cl)</c> in double-double: returns the high part, the low part goes to
  /// <c>low[0]</c>.
  /// </summary>
  private static double AtanAdd(double xh, double xl, double ch, double cl, double[] low) {
    var s = xh + ch;
    var d = s - xh;
    low[0] = ((ch - d) + (xh + (d - s))) + (xl + cl);
    return s;
  }

  /// <summary>
  /// The arc tangent of a <c>float</c> in radians, correctly rounded: a rational approximation in
  /// doubles of <c>atan(x)</c> or, from 1 on, of <c>atan(1/x)</c> subtracted from <c>pi/2</c>; tiny
  /// arguments by <c>x - x^3/3</c>, huge ones to <c>pi/2</c>.
  /// </summary>
  /// <param name="x">The tangent.</param>
  /// <returns>The angle in <c>[-pi/2, pi/2]</c>.</returns>
  public static float AtanF(float x) {
    const double HalfPi = 1.5707963267948966;
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    var e = (int)((bits >> 23) & 0xff);
    var gt = e >= 127;
    var ta = bits & 0x7fffffffu;
    if (ta >= 0x4c700518u) {
      // |x| >= 0x1.e00a3p+25: pi/2 rounded, or a NaN.
      if (ta > 0x7f800000u)
        return x + x;

      return (float)CopySign(HalfPi, (double)x);
    }

    if (e < 127 - 13) {
      // |x| < 2^-13.
      if (e < 127 - 25) {
        if (bits << 1 == 0)
          return x;

        return MathF.FusedMultiplyAdd(-x, MathF.Abs(x), x);
      }

      return MathF.FusedMultiplyAdd(-0.3333333432674408f * x, x * x, x);
    }

    // |x| >= 2^-13.
    var z = (double)x;
    if (gt)
      z = 1 / z;

    var z2 = z * z;
    var z4 = z2 * z2;
    var z8 = z4 * z4;
    var cn0 = _atanFNumerator[0] + z2 * _atanFNumerator[1];
    var cn2 = _atanFNumerator[2] + z2 * _atanFNumerator[3];
    var cn4 = _atanFNumerator[4] + z2 * _atanFNumerator[5];
    var cn6 = _atanFNumerator[6];
    cn0 += z4 * cn2;
    cn4 += z4 * cn6;
    cn0 += z8 * cn4;
    cn0 *= z;
    var cd0 = _atanFDenominator[0] + z2 * _atanFDenominator[1];
    var cd2 = _atanFDenominator[2] + z2 * _atanFDenominator[3];
    var cd4 = _atanFDenominator[4] + z2 * _atanFDenominator[5];
    var cd6 = _atanFDenominator[6];
    cd0 += z4 * cd2;
    cd4 += z4 * cd6;
    cd0 += z8 * cd4;
    var r = cn0 / cd0;
    if (!gt)
      return (float)r;

    // atan(x) = sign(x) pi/2 - atan(1/x), with pi/2 split in a short high part and the rest.
    r = (CopySign(0.008296326794896619, z) - r) + CopySign(1.5625, z);
    return (float)r;
  }

}
