// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The inverse hyperbolic tangent, correctly rounded, converted from CORE-MATH's <c>atanh.c</c> and
/// <c>atanhf.c</c> (MIT); see <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseHyperbolic.txt</c>.
/// </summary>
public static partial class FloatOperations {

  // (atanh(x) - x - x^3/3)/x^5 on [0x1.d12ed0af1a27fp-27, 1/4]: the coefficients of x^0 to x^16.
  private static readonly double[] _atanhSmall = [
    0.2, 0.14285714285712647, 0.11111111111808246, 0.09090908976892513,
    0.07692317014814237, 0.06666239776817569, 0.058937769446109164, 0.05086774199149965,
    0.06193697662487306
  ];

  // log(1/r1[i])/2 and log(1/r2[i])/2 as pairs (low, high), and log(1 + 2dx)/2 - dx: the coefficients of dx^2 to dx^6.
  private static readonly double[] _atanhLog1 = [
    0.0, 0.0, -9.871208922975747e-09, 0.010830521583557129,
    1.3453568972982813e-08, 0.02166074514389038, -1.7084502262101118e-08, 0.032491087913513184,
    1.5698263938102453e-08, 0.04332190752029419, -1.671891107703922e-08, 0.05415236949920654,
    -2.1845342408440063e-08, 0.06498253345489502, 2.3660032545105287e-09, 0.07581323385238647,
    5.288016377571965e-09, 0.0866432785987854, -6.316107148327332e-09, 0.09747374057769775,
    1.8628000211266603e-08, 0.10830408334732056, -2.6873783820024656e-08, 0.11913460493087769,
    -2.1017129245497694e-08, 0.1299648880958557, -1.538281573565239e-08, 0.14079540967941284,
    -1.8127970847384786e-08, 0.1516261100769043, 7.063755214677201e-09, 0.16245633363723755,
    1.5312246149188764e-08, 0.17328691482543945, 2.061758236576142e-08, 0.18411743640899658,
    -3.5826536832621874e-09, 0.19494760036468506, 7.229242637350691e-09, 0.2057778239250183,
    -2.2185768352698428e-08, 0.21660882234573364, 1.4178063371453042e-08, 0.2274390459060669,
    9.13514981197187e-09, 0.23826944828033447, 2.6864391352040105e-08, 0.24909943342208862,
    1.525284481318405e-08, 0.25993019342422485, 4.200522780278068e-09, 0.27076059579849243,
    -1.3818738763968028e-08, 0.2815912365913391, 1.8968018735124444e-08, 0.2924211025238037,
    1.3079079145408268e-08, 0.3032519817352295, 2.7239006731205124e-08, 0.31408220529556274,
    2.9570002426091143e-08, 0.3249129056930542, 3.847761879035919e-09, 0.3357435464859009,
    -9.52327149978884e-10, 0.3465735912322998
  ];

  private static readonly double[] _atanhLog2 = [
    0.0, 0.0, -4.538453752710559e-09, 0.00033867359161376953,
    -1.8592929347341828e-08, 0.0006771087646484375, 1.678207547391043e-08, 0.0010152459144592285,
    -1.6990022539300162e-08, 0.0013536810874938965, -3.8991599157167486e-10, 0.0016922950744628906,
    5.347627793512943e-09, 0.002030670642852783, 1.8255038234734458e-09, 0.0023692846298217773,
    -1.3235142692435877e-08, 0.00270766019821167, 2.2021009198476792e-08, 0.0030462145805358887,
    -1.4543757241441997e-08, 0.0033845901489257812, -8.208584999761009e-10, 0.00372314453125,
    6.221239517398987e-12, 0.004061460494995117, -8.51376999386749e-09, 0.004400014877319336,
    2.8993721461347812e-08, 0.004738271236419678, -2.481555907586912e-09, 0.0050768256187438965,
    1.13875054970999e-08, 0.005415081977844238, 1.5846424534524563e-08, 0.005753576755523682,
    1.12054880161235e-08, 0.0060923099517822266, -8.397113761312394e-09, 0.00643080472946167,
    1.5972895955931864e-08, 0.006769001483917236, -2.873838540302273e-08, 0.00710749626159668,
    -2.301151667260987e-08, 0.007446169853210449, 2.5982010731256716e-08, 0.007784545421600342,
    -1.6401115173016543e-09, 0.008122742176055908, 2.079535933616071e-08, 0.0084611177444458,
    -2.561085775086092e-08, 0.00879979133605957, 2.947064369689996e-08, 0.009138107299804688,
    1.567393061410335e-08, 0.009476721286773682, -1.6538891301948348e-08, 0.009815096855163574,
    1.5418477156619673e-09, 0.010153651237487793, 5.101138927865693e-10, 0.01049196720123291,
    -9.871208922975747e-09, 0.010830521583557129
  ];

  private static readonly double[] _atanhLogPolynomial = [
    -1.0, 1.333333333333325, -1.9999999999999787, 3.2000005485719383,
    -5.333334430663483
  ];

  // The accurate atanh(x)/x - 1 near zero: double-double coefficients of x^2 to x^26, then doubles of x^28 to x^36.
  private static readonly double[] _atanhZeroHigh = [
    0.3333333333333333, 1.850371707708594e-17, 0.2, -1.1102230246229284e-17,
    0.14285714285714285, 7.930164427506819e-18, 0.1111111111111111, 6.167926368992028e-18,
    0.09090909090909091, -2.5298429906136707e-18, 0.07692307692307693, -2.985467266913514e-18,
    0.0666666666666665, 2.2639864550633525e-18, 0.05882352941177954, -3.4181660383881804e-18,
    0.052631578946404994, 9.70086346040831e-19, 0.04761904766540985, -7.243272668421443e-19,
    0.043478259192632006, 2.406803221946237e-18, 0.04000004590716563, -9.100057887980383e-19,
    0.03703608620183014, 5.333875183633964e-19
  ];

  private static readonly double[] _atanhZeroLow = [
    0.03449753297471624, 0.03208910526814049, 0.03167530391844863, 0.02116642652779962,
    0.05003849976370781
  ];

  // The logarithms of float, halved: approximations of 1/(1 + (i + 1/2)/64), the logarithms of their
  // inverses over 2, n*log(2)/2 for n = 1 to 24, and the coefficients of log(1 + z)/2.
  private static readonly double[] _atanhFloatInverse = [
    0.9922480620443821, 0.9770992361009121, 0.9624060150235891, 0.9481481481343508,
    0.9343065693974495, 0.9208633098751307, 0.9078014176338911, 0.8951048944145441,
    0.8827586211264133, 0.8707483001053333, 0.8590604029595852, 0.8476821184158325,
    0.8366013076156378, 0.8258064519613981, 0.815286623314023, 0.8050314467400312,
    0.7950310558080673, 0.78527607396245, 0.7757575754076242, 0.7664670664817095,
    0.757396450266242, 0.7485380116850138, 0.7398843932896852, 0.7314285710453987,
    0.7231638412922621, 0.7150837983936071, 0.7071823198348284, 0.6994535513222218,
    0.6918918918818235, 0.6844919789582491, 0.677248677238822, 0.6701570674777031,
    0.6632124353200197, 0.6564102564007044, 0.6497461926192045, 0.6432160809636116,
    0.6368159204721451, 0.6305418722331524, 0.6243902444839478, 0.6183574870228767,
    0.6124401912093163, 0.606635071337223, 0.6009389664977789, 0.5953488368541002,
    0.5898617506027222, 0.5844748858362436, 0.5791855212301016, 0.5739910323172808,
    0.5688888896256685, 0.5638766512274742, 0.5589519646018744, 0.5541125535964966,
    0.5493562240153551, 0.5446808505803347, 0.5400843881070614, 0.5355648528784513,
    0.5311203319579363, 0.5267489720135927, 0.5224489793181419, 0.5182186234742403,
    0.5140562243759632, 0.5099601596593857, 0.5059288535267115, 0.5019607841968536
  ];

  private static readonly double[] _atanhFloatLog = [
    0.0038910702064755593, 0.011583529917253579, 0.01915943215834426, 0.0266222572666821,
    0.033975330925150045, 0.041221834321774946, 0.04836481367310897, 0.05540718355577081,
    0.06235173900309606, 0.0692011609784502, 0.07595802085284992, 0.08262478691331487,
    0.08920382848175064, 0.09569742628881196, 0.10210777126004227, 0.1084369690266159,
    0.11468705059063057, 0.12085996822529385, 0.1269576052160364, 0.1329817738483913,
    0.13893422513065432, 0.14481664629879729, 0.15063066513628579, 0.15637785526388293,
    0.16205973468362792, 0.1676777713025389, 0.17323338410238578, 0.17872794488290744,
    0.18416278058662977, 0.1895391762128262, 0.19485837557728855, 0.20012158250006382,
    0.20532996242715057, 0.21048464732934077, 0.21558673262018843, 0.22063727996588015,
    0.22563732201152162, 0.23058785731370252, 0.23548985714373422, 0.2403442654004717,
    0.24515199416811606, 0.24991393457449784, 0.25463095142604886, 0.2593038824023371,
    0.2639335452760825, 0.26852073295571777, 0.2730662180496442, 0.2775707529626195,
    0.2820350684948413, 0.28645987745028084, 0.29084587023204084, 0.29519372376674946,
    0.29950409405906614, 0.3037776255561043, 0.3080149386805166, 0.31221664463895504,
    0.31638333477824293, 0.32051558893193816, 0.3246139735744894, 0.32867903636145596,
    0.3327113167818623, 0.33671133731504505, 0.34067961262950625, 0.3446166407358198
  ];

  private static readonly double[] _atanhFloatLog2 = [
    0.34657359016697264, 0.6931471804469453, 1.039720770726918, 1.3862943610068905,
    1.7328679512868632, 2.079441541566836, 2.4260151318468086, 2.7725887221267813,
    3.119162312406754, 3.4657359026867267, 3.8123094929666994, 4.158883083246672,
    4.505456673526645, 4.8520302638066175, 5.19860385408659, 5.545177444366563,
    5.891751034646535, 6.238324624926507, 6.58489821520648, 6.931471805486453,
    7.2780453957664255, 7.624618986046398, 7.971192576326371, 8.317766166606344
  ];

  private static readonly double[] _atanhFloatLogPolynomial = [
    0.4999999998193859, -0.2500075119752796, 0.1666756818257912
  ];

  // (atanh(x) - x)/x^3 of float near zero: the coefficients of x^0 to x^6; and the accurate log(1 + z)/2.
  private static readonly double[] _atanhFloatSmall = [
    0.33333333333333076, 0.20000000005949592, 0.14285692689117752, 0.11136190102118979
  ];

  private static readonly double[] _atanhFloatAccurate = [
    0.5, -0.2500000000000015, 0.16666666666666946, -0.12499999980249463,
    0.0999999997586265, -0.08333990646672795, 0.07143514433126309
  ];

  /// <summary>
  /// The inverse hyperbolic tangent <c>log((1 + x)/(1 - x))/2</c>, correctly rounded to nearest. Near
  /// zero an odd polynomial in double-double; elsewhere the logarithm of the double-double quotient
  /// from tables, and where its error bound does not decide the rounding, an accurate evaluation in
  /// triple-double.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic tangent; the infinity of its sign for 1 and -1, NaN beyond and for NaN.</returns>
  public static double Atanh(double x) {
    var ax = Math.Abs(x);
    var aix = (ulong)BitConverter.DoubleToInt64Bits(ax);
    if (aix >= 0x3ff0000000000000UL) {
      // |x| >= 1
      if (aix == 0x3ff0000000000000UL)
        return CopySign(1, x) / 0.0;

      if (aix > 0x7ff0000000000000UL)
        return x + x;

      return double.NaN;
    }

    var low = new double[1];
    if (aix < 0x3fd0000000000000UL) {
      // |x| < 1/4; below 0x1.d12ed0af1a27fp-27 the result rounds to x.
      if (aix < 0x3e4d12ed0af1a27fUL)
        return Math.FusedMultiplyAdd(x, 2.7755575615628914e-17, x); // 0x1p-55

      // x + (1/3) x^3 + x^5 c(x^2), the x^3 term in double-double
      var x2 = x * x;
      var dx2 = Math.FusedMultiplyAdd(x, x, -x2);
      var x4 = x2 * x2;
      var x3 = x2 * x;
      var x8 = x4 * x4;
      var dx3 = Math.FusedMultiplyAdd(x2, x, -x3) + dx2 * x;
      var p = (_atanhSmall[0] + x2 * _atanhSmall[1]) + x4 * (_atanhSmall[2] + x2 * _atanhSmall[3])
        + x8 * ((_atanhSmall[4] + x2 * _atanhSmall[5]) + x4 * (_atanhSmall[6] + x2 * _atanhSmall[7]) + x8 * _atanhSmall[8]);
      var t = Math.FusedMultiplyAdd(x2, p, 1.850371707708594e-17); // 0x1.5555555555555p-56
      var ph = InverseHyperbolicFastTwoSum(0.3333333333333333, t, low); // 0x1.5555555555555p-2
      ph = AtanhMulDD(ph, low[0], x3, dx3, low);
      var pl = low[0];
      ph = InverseHyperbolicFastTwoSum(x, ph, low);
      pl += low[0];
      var eps = x * (x4 * 2.0122792321330962e-16 + 9.860761315262648e-32); // 0x1.dp-53, 0x1p-103
      var lb = ph + (pl - eps);
      var ub = ph + (pl + eps);
      if (lb == ub)
        return lb;

      return AtanhZero(x);
    }

    // (th, tl) = (1 + |x|)/(1 - |x|) in double-double; its logarithm as for asinh, halved.
    var qh = 1 - ax;
    var ql = (1 - qh) - ax;
    var phigh = InverseHyperbolicFastTwoSum(1, ax, low);
    var plow = low[0];
    var iqh = 1 / qh;
    var th = phigh * iqh;
    var tl = Math.FusedMultiplyAdd(phigh, iqh, -th) + (plow + phigh * (Math.FusedMultiplyAdd(-qh, iqh, 1) - ql * iqh)) * iqh;
    var tb = (ulong)BitConverter.DoubleToInt64Bits(th);
    var e = (int)(tb >> 52) - 0x3ff;
    tb &= ~0UL >> 12;
    double ed = e;
    var i = (int)(tb >> 47);
    var d = (long)(tb & ~0UL >> 17);
    var j = unchecked(tb + ((ulong)_inverseHyperbolicB0[i] << 33) + (ulong)(_inverseHyperbolicB1[i] * (d >> 16))) >> 42;
    tb |= 0x3ff0000000000000UL;
    var tf = BitConverter.Int64BitsToDouble((long)tb);
    var i1 = (int)(j >> 5);
    var i2 = (int)(j & 0x1f);
    var r = (0.5 * _inverseHyperbolicR1[i1]) * _inverseHyperbolicR2[i2];
    var dx = Math.FusedMultiplyAdd(r, tf, -0.5);
    var dxx = dx * dx;
    var rx = r * tf;
    var dxl = Math.FusedMultiplyAdd(r, tf, -rx);
    var f = dxx * ((_atanhLogPolynomial[0] + dx * _atanhLogPolynomial[1])
      + dxx * (_atanhLogPolynomial[2] + dx * _atanhLogPolynomial[3] + dxx * _atanhLogPolynomial[4]));
    const double Log2High = 0.3465735902799736; // 0x1.62e42fefa3ap-2
    const double Log2Low = -9.320943368621517e-16; // -0x1.0ca86c3898dp-50
    var lh = (_atanhLog1[2 * i1 + 1] + _atanhLog2[2 * i2 + 1]) + Log2High * ed;
    lh = InverseHyperbolicFastTwoSum(lh, rx - 0.5, low);
    var ll = low[0];
    ll += Log2Low * ed + (_atanhLog1[2 * i1] + _atanhLog2[2 * i2]) + dxl + 0.5 * tl / th;
    ll += f;
    var sign = CopySign(1, x);
    lh *= sign;
    ll *= sign;
    var bound = 38e-24 + dxx * 1.7763568394002505e-15; // 0x1p-49
    var lower = lh + (ll - bound);
    var upper = lh + (ll + bound);
    if (lower == upper)
      return lower;

    th = InverseHyperbolicFastTwoSum(th, tl, low);
    return AtanhRefine(x, th, low[0], 2.8853900817779268 * Math.Abs(lh + ll)); // 0x1.71547652b82fep+1, about 2/log(2)
  }

  /// <summary>The product of two double-doubles, not normalized.</summary>
  private static double AtanhMulDD(double xh, double xl, double ch, double cl, double[] low) {
    var ahlh = ch * xl;
    var alhh = cl * xh;
    var ahhh = ch * xh;
    var ahhl = Math.FusedMultiplyAdd(ch, xh, -ahhh);
    low[0] = ahhl + (alhh + ahlh);
    return ahhh;
  }

  /// <summary>The product of a double-double and a double, not normalized.</summary>
  private static double AtanhMulDDD(double xh, double xl, double ch, double[] low) {
    var hh = xh * ch;
    low[0] = Math.FusedMultiplyAdd(ch, xh, -hh) + xl * ch;
    return hh;
  }

  /// <summary>The accurate atanh(x) near zero in double-double, <c>x + x^3 * p(x^2)</c>.</summary>
  private static double AtanhZero(double x) {
    var low = new double[1];
    var x2 = x * x;
    var x2l = Math.FusedMultiplyAdd(x, x, -x2);
    var y2 = x2 * (_atanhZeroLow[0] + x2 * (_atanhZeroLow[1] + x2 * (_atanhZeroLow[2] + x2 * (_atanhZeroLow[3] + x2 * _atanhZeroLow[4]))));

    // Horner's rule in double-double, y2 a correction to the highest coefficient.
    var k = _atanhZeroHigh.Length / 2 - 1;
    var y1 = InverseHyperbolicFastTwoSum(_atanhZeroHigh[2 * k], y2, low);
    y2 = low[0] + _atanhZeroHigh[2 * k + 1];
    while (--k >= 0) {
      y1 = InverseHyperbolicMulDDAccurate(x2, x2l, y1, y2, low);
      y2 = low[0];
      y1 = InverseHyperbolicFastTwoSum(_atanhZeroHigh[2 * k], y1, low);
      y2 += low[0] + _atanhZeroHigh[2 * k + 1];
    }

    y1 = AtanhMulDDD(y1, y2, x, low);
    y1 = InverseHyperbolicMulDDAccurate(x2, x2l, y1, low[0], low);
    y2 = low[0];
    var y0 = InverseHyperbolicFastTwoSum(x, y1, low);
    y1 = InverseHyperbolicFastTwoSum(low[0], y2, low);
    y1 = InverseHyperbolicOffPowerOfTwo(y1, low[0]);
    return y0 + y1;
  }

  /// <summary>
  /// The accurate atanh(x) as <c>log(zh + zl) / 2</c>, where <c>a</c> approximates twice the result
  /// divided by <c>log(2)</c>: the argument is reduced by four table factors, the logarithm of the
  /// rest evaluated in double-double and the sum carried in triple-double.
  /// </summary>
  private static double AtanhRefine(double x, double zh, double zl, double a) {
    var t = (ulong)BitConverter.DoubleToInt64Bits(zh);
    var e = (int)(t >> 52) - 0x3ff;
    t &= ~0UL >> 12;
    t |= 0x3ff0000000000000UL;
    var tf = BitConverter.Int64BitsToDouble((long)t);
    double ed = e;
    var v = (ulong)BitConverter.DoubleToInt64Bits(a - ed + 1.0000076293945312); // 0x1.00008p+0
    var i = (v - 0x3ff0000000000000UL) >> 36;
    var i1 = (int)(i >> 12);
    var i2 = (int)((i >> 8) & 0xf);
    var i3 = (int)((i >> 4) & 0xf);
    var i4 = (int)(i & 0xf);
    const double L20 = 0.3465735902799736; // 0x1.62e42fefa3ap-2
    const double L21 = -9.320943368621517e-16; // -0x1.0ca86c3898dp-50
    const double L22 = 9.735225461903722e-32; // 0x1.f97b57a079ap-104
    var el2 = L22 * ed;
    var el1 = L21 * ed;
    var el0 = L20 * ed;
    var k1 = 3 * i1;
    var k2 = 3 * (17 + i2);
    var k3 = 3 * (34 + i3);
    var k4 = 3 * (51 + i4);
    var l0 = _inverseHyperbolicRefineLog[k1] + _inverseHyperbolicRefineLog[k2] + (_inverseHyperbolicRefineLog[k3] + _inverseHyperbolicRefineLog[k4]) + el0;
    var l1 = _inverseHyperbolicRefineLog[k1 + 1] + _inverseHyperbolicRefineLog[k2 + 1] + (_inverseHyperbolicRefineLog[k3 + 1] + _inverseHyperbolicRefineLog[k4 + 1]);
    var l2 = _inverseHyperbolicRefineLog[k1 + 2] + _inverseHyperbolicRefineLog[k2 + 2] + (_inverseHyperbolicRefineLog[k3 + 2] + _inverseHyperbolicRefineLog[k4 + 2]);
    var t12 = _inverseHyperbolicRefineT1[i1] * _inverseHyperbolicRefineT2[i2];
    var t34 = _inverseHyperbolicRefineT3[i3] * _inverseHyperbolicRefineT4[i4];
    var th = t12 * t34;
    var tl = Math.FusedMultiplyAdd(t12, t34, -th);
    var dh = th * tf;
    var dl = Math.FusedMultiplyAdd(th, tf, -dh);
    var sh = tl * tf;
    var sl = Math.FusedMultiplyAdd(tl, tf, -sh);
    var low = new double[1];
    var xh = InverseHyperbolicFastTwoSum(dh - 1, dl, low);
    var xl = low[0];

    // zl / 2^e
    var w = (ulong)BitConverter.DoubleToInt64Bits(zl);
    w = unchecked(w - (ulong)((long)e << 52));
    xl += th * BitConverter.Int64BitsToDouble((long)w);
    xh = InverseHyperbolicAddDD(xh, xl, sh, sl, low);
    xl = low[0];
    sh = InverseHyperbolicRefineLog1P(xh, xl, low);
    sh = InverseHyperbolicAddDD(sh, low[0], el1, el2, low);
    sh = InverseHyperbolicAddDD(sh, low[0], l1, l2, low);
    sl = low[0];
    var v0 = InverseHyperbolicFastTwoSum(l0, sh, low);
    var v1 = InverseHyperbolicFastTwoSum(low[0], sl, low);
    // Where v1 is a power of two, the rest has its sign: one unit away from zero.
    v1 = InverseHyperbolicOffPowerOfTwo(v1, v1);
    var b1 = (ulong)BitConverter.DoubleToInt64Bits(v1);
    var b0 = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var er = (b1 + 1) & ~0UL >> 12;
    var de = ((b0 >> 52) & 0x7ff) - ((b1 >> 52) & 0x7ff);
    var sign = CopySign(1, x);
    v0 *= sign;
    v1 *= sign;
    var res = v0 + v1;
    if (de > 104 || er < 3)
      return InverseHyperbolicDatabase(AtanhDatabaseFirst, AtanhDatabaseRows, x, res);

    return res;
  }

  /// <summary>
  /// The inverse hyperbolic tangent of <c>float</c>, correctly rounded to nearest: an odd polynomial
  /// near zero, elsewhere <c>(log(1 + |x|) - log(1 - |x|))/2</c>, each logarithm from a table of 64
  /// entries, both evaluated again more accurately where the result lies too close to a rounding boundary.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic tangent; the infinity of its sign for 1 and -1, NaN beyond and for NaN.</returns>
  public static float AtanhF(float x) {
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    var ax = ux << 1;
    if (ax < 0x7a300000u || ax >= 0x7f000000u) {
      // |x| < 0x1.5a3116p-5, |x| >= 1 or NaN
      if (ax >= 0x7f000000u)
        return AtanhSpecialF(x);

      if (ax < 0x73713744u) {
        // |x| < 0x1.713744p-12
        if (ax == 0)
          return x;

        return MathF.FusedMultiplyAdd(x, 2.9802322387695312e-08f, x); // 0x1p-25f
      }

      double z = x;
      var z2 = z * z;
      var z4 = z2 * z2;
      var p = _atanhFloatSmall[0] + z2 * _atanhFloatSmall[1] + z4 * (_atanhFloatSmall[2] + z2 * _atanhFloatSmall[3]);
      return (float)(z + (z * z2) * p);
    }

    // 1 + |x| = 2^(nz - 1) * (1 + mn/2^32) and 1 - |x| = md/2^32, both in fixed point.
    var sgn = ux >> 31 == 0 ? 1.0 : -1.0;
    var e = ax >> 24;
    var md = ((ux << 8) | 0x80000000u) >> (int)(126 - e);
    var mn = unchecked(0u - md);
    var nz = System.Numerics.BitOperations.LeadingZeroCount(mn) + 1;
    mn <<= nz;
    var jn = (int)(mn >> 26);
    var jd = (int)(md >> 26);
    var tn = BitConverter.Int64BitsToDouble((long)mn << 20 | 1023L << 52);
    var td = BitConverter.Int64BitsToDouble((long)md << 20 | 1023L << 52);
    var zn = tn * _atanhFloatInverse[jn] - 1;
    var zd = td * _atanhFloatInverse[jd] - 1;
    var zn2 = zn * zn;
    var zd2 = zd * zd;
    var rn = ((_atanhFloatLog[jn] - _atanhFloatLog2[nz - 1]) + zn * _atanhFloatLogPolynomial[0])
      + zn2 * (_atanhFloatLogPolynomial[1] + zn * _atanhFloatLogPolynomial[2]);
    var rd = (_atanhFloatLog[jd] + zd * _atanhFloatLogPolynomial[0]) + zd2 * (_atanhFloatLogPolynomial[1] + zd * _atanhFloatLogPolynomial[2]);
    var r = sgn * (rd - rn);
    var ub = (float)r;
    var lb = (float)(r + sgn * 0.226e-9);
    if (ub == lb)
      return ub;

    var zn4 = zn2 * zn2;
    var zd4 = zd2 * zd2;
    var fn = zn * (((_atanhFloatAccurate[0] + zn * _atanhFloatAccurate[1]) + zn2 * (_atanhFloatAccurate[2] + zn * _atanhFloatAccurate[3]))
      + zn4 * ((_atanhFloatAccurate[4] + zn * _atanhFloatAccurate[5]) + zn2 * _atanhFloatAccurate[6]));
    fn += 9.320943368621517e-16 * nz; // 0x1.0ca86c3898dp-50
    fn += _atanhFloatLog[jn];
    var en = nz * 0.3465735902799736; // 0x1.62e42fefa3ap-2
    var fd = zd * (((_atanhFloatAccurate[0] + zd * _atanhFloatAccurate[1]) + zd2 * (_atanhFloatAccurate[2] + zd * _atanhFloatAccurate[3]))
      + zd4 * ((_atanhFloatAccurate[4] + zd * _atanhFloatAccurate[5]) + zd2 * _atanhFloatAccurate[6]));
    fd += _atanhFloatLog[jd];
    r = fd - fn + en;
    return (float)(sgn * r);
  }

  /// <summary>atanh of a <c>float</c> with <c>|x| &gt;= 1</c> or NaN: the infinity of its sign for 1 and -1, the argument for NaN, NaN for the rest.</summary>
  private static float AtanhSpecialF(float x) {
    var ax = (uint)BitConverter.SingleToInt32Bits(x) << 1;
    if (ax == 0x7f000000u)
      return x / 0.0f;

    if (ax > 0xff000000u)
      return x + x;

    return float.NaN;
  }

}
