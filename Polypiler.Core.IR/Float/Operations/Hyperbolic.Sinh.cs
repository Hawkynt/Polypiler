using System;

namespace Polypiler.Core.IR;

public static partial class FloatOperations {

  // 2^(i/64), i in 0..63, as {low, high} pairs; shared by Sinh, Cosh and Tanh.
  private static readonly double[] _sinhExpCoarse = [
    0.0, 1.0, -1.523477860336858e-17, 1.0108892860517005,
    5.1092250289734445e-17, 1.0218971486541166, 7.60083887402709e-18, 1.0330248790212284,
    8.551889705537964e-17, 1.0442737824274138, 1.7593257387720916e-18, 1.0556451783605572,
    -7.899853966841582e-17, 1.0671404006768237, -6.656660436056593e-17, 1.0787607977571199,
    -3.046782079812471e-17, 1.0905077326652577, 5.2660368715706944e-17, 1.102382583307841,
    1.0410278456845571e-16, 1.1143867425958924, 5.165856758795456e-17, 1.1265216186082418,
    8.912812676025407e-17, 1.1387886347566916, 3.250710218863828e-17, 1.1511892299529827,
    3.829204836924094e-17, 1.1637248587775775, 5.554203254218078e-17, 1.1763969916502812,
    3.982015231465646e-17, 1.189207115002721, 6.644981499252301e-17, 1.202156731452703,
    -7.712630692681488e-17, 1.215247359980469, -1.8987816313025296e-17, 1.22848053610687,
    4.658027591836937e-17, 1.241857812073484, -6.7113898212968784e-18, 1.255380757024691,
    2.667932131342186e-18, 1.2690509571917332, 1.713594918243561e-17, 1.2828700160787783,
    2.5382502794888315e-17, 1.2968395546510096, -7.181536135519455e-17, 1.3109612115247644,
    -2.858731210038861e-17, 1.3252366431597413, 8.927282594831731e-17, 1.339667524053303,
    7.700948379802988e-17, 1.3542555469368927, 9.593797919118849e-17, 1.3690024229745905,
    -6.770511658794785e-17, 1.383909881963832, -9.614213209051323e-17, 1.3989796725383112,
    -9.667293313452913e-17, 1.4142135623730951, -1.2031642489053654e-17, 1.42961333839197,
    -3.023758134993988e-17, 1.4451808069770467, -5.600377186075217e-17, 1.460917794180647,
    -3.4839945568927964e-17, 1.4768261459394993, 1.4192920154284036e-17, 1.4929077282912648,
    -1.016455327754295e-16, 1.5091644275934228, -1.1024941712342561e-16, 1.5255981507445384,
    7.94983480969762e-17, 1.5422108254079407, 3.7812070533575275e-17, 1.559004400237837,
    -1.0136916471278304e-17, 1.5759808451078865, -1.0094406542311962e-16, 1.593142151342267,
    2.4707192569797888e-17, 1.6104903319492543, -6.712955084707083e-17, 1.6280274218573478,
    -1.0125679913674774e-16, 1.645755478153965, 5.890992696713101e-17, 1.6636765803267364,
    8.199010020581498e-17, 1.681792830507429, -8.0237193703977e-18, 1.7001063537185235,
    -1.8513804182631107e-17, 1.718619298122478, 3.164389299292957e-17, 1.7373338352737062,
    2.960140695448874e-17, 1.7562521603732995, 6.429731796556571e-17, 1.7753764925265212,
    1.822745842791209e-17, 1.7947090750031072, -9.969531538920349e-17, 1.8142521755003989,
    3.2831072242456266e-17, 1.8340080864093424, 9.761887490727594e-17, 1.8539791250833855,
    -6.122763413004143e-17, 1.8741676341103, 3.40340353521653e-17, 1.8945759815869656,
    -1.0619946056195964e-16, 1.9152065613971474, 1.0332385960676326e-16, 1.9360617934922943,
    8.960767791036667e-17, 1.9571441241754002, 4.0388753109278167e-17, 1.978456026387951
  ];

  // 2^(i/4096), i in 0..63, as {low, high} pairs; shared by Sinh, Cosh and Tanh.
  private static readonly double[] _sinhExpFine = [
    0.0, 1.0, 9.336185335478462e-17, 1.0001692397053021,
    -5.141333931318957e-18, 1.0003385080526823, 6.962424022020573e-17, 1.0005078050469876,
    -5.115123297685667e-17, 1.0006771306930664, 8.422990024586488e-17, 1.0008464849957674,
    -2.8245220747761684e-17, 1.001015867959941, -7.180424565592133e-17, 1.0011852795904375,
    -1.8973728416792996e-17, 1.0013547198921082, 9.06044106726912e-17, 1.0015241888698057,
    -7.17327634990032e-17, 1.0016936865283832, -1.3307196246722662e-17, 1.0018632128726943,
    2.572692594322112e-17, 1.002032767907594, -3.929937785484517e-17, 1.002202351637938,
    8.461377247994717e-17, 1.0023719640685822, -4.19488324163994e-17, 1.0025416052043845,
    -3.6366159286922646e-17, 1.0027112750502025, -2.610944063243938e-17, 1.0028809736108952,
    1.7530784779823324e-17, 1.0030507008913223, 5.753923525628267e-17, 1.0032204568963443,
    -8.684922005117958e-18, 1.0033902416308227, 9.490035430981776e-17, 1.0035600550996193,
    -8.710380605818421e-17, 1.0037298973075977, 3.4958916958571545e-17, 1.003899768259621,
    9.753787549840241e-17, 1.004069667960554, -1.0576221196292857e-16, 1.0042395964152628,
    4.209188738127126e-17, 1.0044095536286128, -1.6700166857554785e-17, 1.0045795396054717,
    -1.6231463554124514e-17, 1.0047495543507072, 2.3028539278028114e-17, 1.004919597869188,
    1.6418046976773032e-17, 1.0050896701657839, 3.726698431828413e-17, 1.005259771245365,
    9.499186535455033e-17, 1.0054299011128027, -8.680931314444582e-17, 1.0056000597729693,
    4.0005474910301175e-17, 1.005770247230737, 7.190499111509974e-17, 1.00594046349098,
    -1.3908068671065786e-17, 1.006110708558573, -8.14020864257305e-17, 1.006280982438391,
    -5.762151043749534e-17, 1.00645128513531, 6.745278477310458e-17, 1.0066216166542072,
    1.8998557240346293e-17, 1.0067919769999607, -9.637430032316406e-17, 1.006962366177449,
    -1.2528654462453979e-17, 1.0071327841915512, 3.020578887843694e-17, 1.007303231047148,
    -4.8693942586085655e-17, 1.0074737067491204, 5.224029937687454e-17, 1.0076442113023503,
    -9.361543551478456e-17, 1.0078147447117207, -8.65251323306195e-17, 1.007985306982115,
    -3.252058756084308e-17, 1.0081558981184175, -9.917232268060916e-17, 1.0083265181255139,
    -7.136047404162522e-17, 1.0084971670082898, -1.7268683712243217e-17, 1.0086678447716324,
    -6.619954693673941e-17, 1.0088385514204294, 3.56545690151302e-17, 1.0090092869595693,
    3.717310013708818e-17, 1.0091800513939415, 7.062572406825526e-17, 1.0093508447284363,
    -1.432141230342882e-17, 1.0095216669679448, 1.566818801313411e-17, 1.0096925181173586,
    -1.1043695780393687e-16, 1.0098633981815708, -5.767317427160398e-17, 1.0100343071654745,
    4.8354849784403835e-18, 1.0102052450739643, 7.015121289715441e-17, 1.0103762119119353,
    7.161802873619573e-17, 1.0105472076842836, 1.0504659134084051e-16, 1.010718232395906
  ];

  // e^x - 1 ~ x + x^2/2 + x^3/6 + x^4/24 for the reduced argument; shared by Sinh and Cosh.
  private static readonly double[] _sinhExpFast = [1.0, 0.5, 0.1666666668218872, 0.041666666640865824];

  // The {high, low} coefficients of the double-double polynomials of SinhPolyDD, which starts at
  // one of the offsets below (in pairs): the accurate e^x - 1 of degree 0 to 2 (shared by Sinh and
  // Cosh), the P of SinhZero, and the degree 2 to 8 of CoshZero.
  private const int SinhExpPolynomial = 0;
  private const int SinhZeroPolynomial = 3;
  private const int CoshZeroPolynomial = 8;

  private static readonly double[] _sinhDoubleDouble = [
    1.0, 7.180385960686095e-29, 0.5, -1.523885870582321e-28,
    0.16666666666666666, 9.213309146126262e-18,
    0.16666666666666666, 9.251858538542912e-18, 0.008333333333333333, 1.1564823185149052e-19,
    0.0001984126984126984, 1.720553633109976e-22, 2.7557319223985893e-06, -1.8069132505534607e-22,
    2.5052108385441396e-08, -7.738469464826113e-25,
    0.5, -6.859765205627812e-34, 0.041666666666666664, 2.3129646346369136e-18,
    0.001388888888888889, -5.3005440312980423e-20, 2.48015873015873e-05, 2.1765137868287872e-23
  ];

  // sinh(x) = x + x^3 * P(x^2) for 0.25 > |x| >= 0x1.7137449123ef7p-26.
  private static readonly double[] _sinhSmall = [
    0.16666666666666666, 0.008333333333335132, 0.00019841269832592963,
    2.7557330544555877e-06, 2.5059397415904265e-08
  ];

  // The arguments whose double-double result still rounds the wrong way: {x, high, low} by x.
  private static readonly double[] _sinhExceptions = [
    0.30299001755646715, 0.3076472398520004, -2.1377822382698318e-32,
    0.31388071487577524, 0.3190601423468649, -1.3877787807814457e-17,
    0.34514152056570635, 0.35203481330285225, 6.2592723192585165e-34,
    0.4201333991401245, 0.43260271067188394, -2.465190328815662e-32,
    0.4296251699028713, 0.4429642251623777, -4.930380657631324e-32,
    0.45947351342208725, 0.47581205771350993, -1.7333369499485123e-33,
    0.4775966244944248, 0.49596134150989307, -6.066679324819793e-33,
    0.8202857465751042, 0.9154214485698429, 1.1555579666323415e-32,
    0.8324843825057703, 0.9320279933884077, 1.8970409952214273e-32,
    0.8773838938338423, 0.9943656656548412, 3.5629703971163863e-32,
    1.185016339536633, 1.4824995398105343, -5.700752635386218e-32,
    1.2499400805943286, 1.601805929940096, 1.0785207688568521e-32,
    1.26938426471358, 1.638828050846657, -9.860761315262648e-32,
    1.4503004414888254, 2.01494785001618, 1.9721522630525295e-31,
    1.714397862380115, 2.6866292599310433, -1.9721522630525295e-31,
    1.8606010575067835, 3.1360099384712994, 8.743721947518051e-32,
    1.9656974021850588, 3.499916026812682, 8.088905766426391e-32,
    2.4011654868547234, 5.47270947657074, 3.13541394946242e-31,
    2.6925353234887788, 7.350681906936157, 1.9721522630525295e-31,
    3.0940598027467026, 11.010582167899472, -3.4358590207868288e-31,
    3.4705864249662364, 16.062247485775032, 1.0877652325899108e-30,
    8.315635076388645, 2043.6400519132815, 3.2343297114061484e-29,
    9.960078282661085, 10582.226207531126, 8.077935669463161e-28,
    10.114268934199528, 12346.424080601417, -8.077935669463161e-28,
    17.388333161299133, 17808492.010525286, -6.462348535570529e-25,
    18.05532709027637, 34697559.45436618, 2.6560252481194873e-24,
    25.165468674093525, 42480946450.22329, 6.776263578034403e-21,
    26.11839399977529, 110165191726.13094, -1.4690727678941772e-21,
    28.033688490833303, 747904632603.6691, 2.117582368135751e-21,
    30.26039953391218, 6932574447934.166, 3.091670257478196e-19,
    32.607090268654645, 72451833254484.55, 7.331917191433224e-18,
    36.21680092007135, 2677483456477309.5, 2.2941717969793274e-16,
    39.248194185756915, 5.549376156977022e+16, 1.2698175844150228e-15,
    39.567072919844186, 7.633635844414822e+16, -2.0539125955565396e-15,
    60.06174039698168, 6.073687348575258e+25, 5.960464477539063e-08,
    62.89131983565637, 1.028781553530319e+27, -3.0517578125e-05,
    89.11403663154066, 2.5159637154374956e+38, -1081344.0,
    94.5946582241399, 6.038208269509039e+40, 905969664.0,
    143.5266815223847, 1.0760087311882896e+62, 6.833429016855299e+30,
    170.40063412375622, 5.046904851182803e+73, 4.900066083661514e+40,
    170.71172754077605, 6.888604824123322e+73, 2.809371221299268e+42,
    188.63649973560388, 4.1952797428635125e+81, -1.8487995712235922e+50,
    301.663664262519, 5.126663166814058e+130, 1.1363451031099242e+99,
    340.8075257062713, 5.126226715920517e+147, -1.785403405774125e+116,
    414.48199762232366, 5.084103930744147e+179, 2.557336412418861e+148,
    455.12664958584196, 2.2801451094916737e+197, -1.3532732693985935e+165,
    470.2797657541442, 8.687148032216733e+203, -1.2076679759428932e+171,
    491.59797410203873, 1.5749162775397027e+213, -3.3455469274103006e+181,
    535.5022871124662, 1.839324449670326e+232, 4.0903762017695584e+200,
    589.8601769026294, 7.447147019077431e+255, -2.4652512253939255e+224,
    661.0515291649695, 6.165969531701819e+286, -2.2067993803420066e+255
  ];

  /// <summary>
  /// The hyperbolic sine of a <c>double</c>, correctly rounded. Below <c>0.25</c> an odd polynomial,
  /// above it <c>(e^|x| - e^-|x|) / 2</c> with <c>e^x = 2^(k/64) * 2^(j/4096) * e^r</c>; from about
  /// 36.7 the second exponential no longer matters, and beyond <c>0x1.633ce8fb9f87dp+9</c> the result
  /// overflows. When the fast result cannot be rounded safely, both exponentials are evaluated again
  /// in double-double arithmetic, and the few arguments that remain are looked up.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>sinh(x)</c>.</returns>
  public static double Sinh(double x) {
    const double Scale = 5909.278887481194; // 4096 / ln 2
    var lo = new double[1];

    // v0 has k = round(|x| * 4096 / ln 2) in its integer bits, t is k.
    var ax = Math.Abs(x);
    var v0 = Math.FusedMultiplyAdd(ax, Scale, 100663296.5);
    var jt = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var t = BitConverter.Int64BitsToDouble((long)(jt & 0xfffffffffc000000UL)) - 100663296.0;
    var aix = (ulong)BitConverter.DoubleToInt64Bits(ax);
    if (aix < 0x3fd0000000000000UL) {
      // |x| < 0.25
      if (aix < 0x3e57137449123ef7UL)
        return Math.FusedMultiplyAdd(x, 2.7755575615628914e-17, x);

      var x2 = x * x;
      var x3 = x2 * x;
      var x4 = x2 * x2;
      var p = x3 * ((_sinhSmall[0] + x2 * _sinhSmall[1]) + x4 * ((_sinhSmall[2] + x2 * _sinhSmall[3]) + x4 * _sinhSmall[4]));
      var e = x3 * 1.942890293094024e-16;
      var lb = x + (p - e);
      var ub = x + (p + e);
      if (lb == ub)
        return lb;

      return SinhZero(x);
    }

    if (aix > 0x408633ce8fb9f87dUL) {
      // |x| > 0x1.633ce8fb9f87dp+9: NaN, an infinity, or an overflow.
      if (aix >= 0x7ff0000000000000UL)
        return x + x;

      return CopySign(8.98846567431158e+307, x) * 2.0;
    }

    // 0.25 <= |x| <= 0x1.633ce8fb9f87dp+9: k = 4096 * ie + 64 * i0 + i1 and -k likewise.
    var il = (long)((jt << 14) >> 40);
    var jl = -il;
    var i1 = (int)(il & 0x3f);
    var i0 = (int)(il >> 6 & 0x3f);
    var ie = il >> 12;
    var j1 = (int)(jl & 0x3f);
    var j0 = (int)(jl >> 6 & 0x3f);
    var je = jl >> 12;
    var sp = BitConverter.Int64BitsToDouble((long)((ulong)(1022 + ie) << 52));
    var sm = BitConverter.Int64BitsToDouble((long)((ulong)(1022 + je) << 52));
    var t0h = _sinhExpCoarse[2 * i0 + 1];
    var t0l = _sinhExpCoarse[2 * i0];
    var t1h = _sinhExpFine[2 * i1 + 1];
    var t1l = _sinhExpFine[2 * i1];
    var th = t0h * t1h;
    var tl = t0h * t1l + t1h * t0l + Math.FusedMultiplyAdd(t0h, t1h, -th);
    const double Ln2High = 0.00016922538588914904;
    const double Ln2Low = 1.0256140314162804e-14;
    var dx = (ax - Ln2High * t) + Ln2Low * t;
    var dx2 = dx * dx;
    var mx = -dx;
    var pp = dx * ((_sinhExpFast[0] + dx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + dx * _sinhExpFast[3]));
    double rh;
    double rl;
    if (aix > 0x4014000000000000UL) {
      // |x| > 5
      if (aix > 0x40425e4f7b2737faUL) {
        // |x| > 0x1.25e4f7b2737fap+5: e^-|x| is below the last bit.
        sp = BitConverter.Int64BitsToDouble((1021 + ie) << 52);
        rh = th;
        rl = tl + th * pp;
        rh *= CopySign(1.0, x);
        rl *= CopySign(1.0, x);
        var e = 1.2001398071409368e-19 * th;
        var lb = rh + (rl - e);
        var ub = rh + (rl + e);
        if (lb == ub)
          return lb * sp * 2;

        th = SinhExpAccurate(ax, t, th, tl, lo);
        tl = lo[0];
        th = SinhFastTwoSum(th, tl, lo);
        tl = lo[0];
        th *= CopySign(1.0, x);
        tl *= CopySign(1.0, x);
        var tml = (long)(unchecked((ulong)BitConverter.DoubleToInt64Bits(tl) + 8) & 0xfffffffffffffUL);
        th += tl;
        th *= 2;
        th *= sp;
        if (tml <= 16)
          return SinhException(x, th);

        return th;
      }

      // 5 < |x| <= 0x1.25e4f7b2737fap+5: e^-|x| in doubles only.
      var q0h = _sinhExpCoarse[2 * j0 + 1];
      var q1h = _sinhExpFine[2 * j1 + 1];
      var qh = q0h * q1h;
      th *= sp;
      tl *= sp;
      qh *= sm;
      var pm = mx * ((_sinhExpFast[0] + mx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + mx * _sinhExpFast[3]));
      var em = qh + qh * pm;
      rh = th;
      rl = (tl - em) + th * pp;
      rh *= CopySign(1.0, x);
      rl *= CopySign(1.0, x);
      var e5 = 1.2202568396382264e-19 * rh;
      var lb5 = rh + (rl - e5);
      var ub5 = rh + (rl + e5);
      if (lb5 == ub5)
        return lb5;

      th = SinhExpAccurate(ax, t, th, tl, lo);
      tl = lo[0];
      if (aix > 0x403f666666666666UL) {
        // |x| > 31.4
        rh = th - qh;
        rl = ((th - rh) - qh) + tl;
      } else {
        qh = q0h * q1h;
        var q0l = _sinhExpCoarse[2 * j0];
        var q1l = _sinhExpFine[2 * j1];
        var ql = q0h * q1l + q1h * q0l + Math.FusedMultiplyAdd(q0h, q1h, -qh);
        qh *= sm;
        ql *= sm;
        qh = SinhExpAccurate(-ax, -t, qh, ql, lo);
        ql = lo[0];
        rh = th - qh;
        rl = (((th - rh) - qh) - ql) + tl;
      }
    } else {
      // 0.25 <= |x| <= 5
      var q0h = _sinhExpCoarse[2 * j0 + 1];
      var q0l = _sinhExpCoarse[2 * j0];
      var q1h = _sinhExpFine[2 * j1 + 1];
      var q1l = _sinhExpFine[2 * j1];
      var qh = q0h * q1h;
      var ql = q0h * q1l + q1h * q0l + Math.FusedMultiplyAdd(q0h, q1h, -qh);
      th *= sp;
      tl *= sp;
      qh *= sm;
      ql *= sm;
      var pm = mx * ((_sinhExpFast[0] + mx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + mx * _sinhExpFast[3]));
      var fph = th;
      var fpl = tl + th * pp;
      var fmh = qh;
      var fml = ql + qh * pm;
      rh = fph - fmh;
      rl = ((fph - rh) - fmh) - fml + fpl;
      rh *= CopySign(1.0, x);
      rl *= CopySign(1.0, x);
      var e = 3.800001559619605e-19 * rh;
      var lb = rh + (rl - e);
      var ub = rh + (rl + e);
      if (lb == ub)
        return lb;

      th = SinhExpAccurate(ax, t, th, tl, lo);
      tl = lo[0];
      qh = SinhExpAccurate(-ax, -t, qh, ql, lo);
      ql = lo[0];
      rh = th - qh;
      rl = ((th - rh) - qh) - ql + tl;
    }

    rh = SinhFastTwoSum(rh, rl, lo);
    rl = lo[0];
    // The low part close to a multiple of 2^-52 of its binade means a halfway case or nearly one.
    var ml = (long)(unchecked((ulong)BitConverter.DoubleToInt64Bits(rl) + 8) & 0xfffffffffffffUL);
    rh *= CopySign(1.0, x);
    rl *= CopySign(1.0, x);
    rh += rl;
    if (ml <= 16)
      return SinhException(x, rh);

    return rh;
  }

  /// <summary>sinh(x) for 0.25 > |x| >= 0x1.7137449123ef7p-26 in double-double arithmetic.</summary>
  private static double SinhZero(double x) {
    var lo = new double[1];
    var x2 = x * x;
    var x2l = Math.FusedMultiplyAdd(x, x, -x2);
    lo[0] = x2 * (1.6059043837926057e-10 + x2 * (7.647161643548583e-13 + x2 * 2.8135131492069468e-15));
    var y1 = SinhPolyDD(x2, x2l, SinhZeroPolynomial, 5, lo);
    var y2 = lo[0];
    y1 = SinhMulDDD(y1, y2, x, lo);
    y2 = lo[0];
    y1 = SinhMulDD(y1, y2, x2, x2l, lo);
    y2 = lo[0];
    var y0 = SinhFastTwoSum(x, y1, lo);
    y1 = lo[0];
    y1 = SinhFastTwoSum(y1, y2, lo);
    y2 = lo[0];
    // A middle part with an all-zero significand hides a halfway case: move it towards y2.
    var bits = BitConverter.DoubleToInt64Bits(y1);
    if ((bits & 0xfffffffffffffL) == 0) {
      if ((BitConverter.DoubleToInt64Bits(y2) ^ bits) < 0)
        bits--;
      else
        bits++;

      y1 = BitConverter.Int64BitsToDouble(bits);
    }

    return y0 + y1;
  }

  /// <summary>The result of an argument of <c>_sinhExceptions</c>, otherwise <paramref name="f"/>.</summary>
  private static double SinhException(double x, double f) {
    var a = 0;
    var b = _sinhExceptions.Length / 3 - 1;
    var m = (a + b) / 2;
    var ax = Math.Abs(x);
    while (a <= b) {
      if (_sinhExceptions[3 * m] < ax)
        a = m + 1;
      else if (_sinhExceptions[3 * m] == ax)
        return CopySign(1.0, x) * _sinhExceptions[3 * m + 1] + CopySign(1.0, x) * _sinhExceptions[3 * m + 2];
      else
        b = m - 1;

      m = (a + b) / 2;
    }

    return f;
  }

  /// <summary>
  /// <c>e^x</c> as a double-double, high part returned and low part in <paramref name="lo"/>, from
  /// <c>2^(t/4096) = th + tl</c> and the remainder <c>x - t * ln 2 / 4096</c>; shared by Sinh and Cosh.
  /// </summary>
  private static double SinhExpAccurate(double x, double t, double th, double tl, double[] lo) {
    const double Ln2High = 0.00016922538588914904;
    const double Ln2Low = 1.0256140314162804e-14;
    const double Ln2LowLow = 3.2042720746546034e-31;
    var dx = x - Ln2High * t;
    var dxl = Ln2Low * t;
    var dxll = Ln2LowLow * t + Math.FusedMultiplyAdd(Ln2Low, t, -dxl);
    var dxh = dx + dxl;
    dxl = ((dx - dxh) + dxl) + dxll;
    lo[0] = dxh * (0.041666666666666664 + dxh * (0.008333333338507347 + dxh * 0.0013888888882434365));
    var fh = SinhPolyDD(dxh, dxl, SinhExpPolynomial, 3, lo);
    fh = SinhMulDD(dxh, dxl, fh, lo[0], lo);
    fh = SinhMulDD(th, tl, fh, lo[0], lo);
    var fl = lo[0];
    var zh = th + fh;
    var zl = (th - zh) + fh;
    var uh = zh + tl;
    var ul = ((zh - uh) + tl) + zl;
    var vh = uh + fl;
    lo[0] = ((uh - vh) + fl) + ul;
    return vh;
  }

  /// <summary>
  /// The double-double polynomial of the <paramref name="n"/> coefficients of <c>_sinhDoubleDouble</c>
  /// from pair <paramref name="first"/> at <c>xh + xl</c> by Horner's rule; <paramref name="lo"/>
  /// brings the sum of the higher terms in doubles and takes the low part of the result.
  /// </summary>
  private static double SinhPolyDD(double xh, double xl, int first, int n, double[] lo) {
    var i = n - 1;
    var ch = SinhFastTwoSum(_sinhDoubleDouble[2 * (first + i)], lo[0], lo);
    var cl = lo[0] + _sinhDoubleDouble[2 * (first + i) + 1];
    while (--i >= 0) {
      ch = SinhMulDD(xh, xl, ch, cl, lo);
      cl = lo[0];
      ch = SinhFastTwoSum(_sinhDoubleDouble[2 * (first + i)], ch, lo);
      cl = (cl + _sinhDoubleDouble[2 * (first + i) + 1]) + lo[0];
    }

    lo[0] = cl;
    return ch;
  }

  /// <summary><c>(xh + xl) * (ch + cl)</c>, high part returned and low part in <paramref name="lo"/>.</summary>
  private static double SinhMulDD(double xh, double xl, double ch, double cl, double[] lo) {
    var h = xh * ch;
    lo[0] = Math.FusedMultiplyAdd(xh, ch, -h) + xh * cl + xl * ch;
    return h;
  }

  /// <summary><c>(xh + xl) * c</c>, high part returned and low part in <paramref name="lo"/>.</summary>
  private static double SinhMulDDD(double xh, double xl, double c, double[] lo) {
    var h = xh * c;
    lo[0] = Math.FusedMultiplyAdd(xh, c, -h) + xl * c;
    return h;
  }

  /// <summary><c>x + y</c> with its rounding error in <paramref name="lo"/>, for <c>|x| &gt;= |y|</c>; shared by the hyperbolic functions.</summary>
  private static double SinhFastTwoSum(double x, double y, double[] lo) {
    var s = x + y;
    lo[0] = y - (s - x);
    return s;
  }

}
