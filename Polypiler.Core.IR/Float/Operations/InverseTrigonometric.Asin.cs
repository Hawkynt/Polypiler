using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The arc sine, correctly rounded in both formats. Converted from CORE-MATH's
/// <c>src/binary64/asin/asin.c</c> and <c>src/binary32/asin/asinf.c</c> (MIT); see
/// <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseTrigonometric.txt</c>. The tables of the <c>double</c>
/// algorithm are shared with <see cref="Acos"/>, whose upstream file carries the same ones.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// The coefficients of <c>asin(x) = x * (c0 + c1 + t * P(t))</c> with <c>t = x^2 - j/128</c>, eight
  /// per row <c>j</c> (0 to 32): <c>c0</c> and <c>c1</c> as a double-double, then the six of <c>P</c>.
  /// </summary>
  private static readonly double[] _asinCoefficients = [
    1.0, 0.0, 0.16666666666666666, 0.07500000000000245,
    0.04464285713908024, 0.030381947204790895, 0.022371123953315995, 0.017545030634765875,
    1.001306682371262, -1.0272312753221834e-16, 0.16784677438667686, 0.07605755082168193,
    0.04560611511468846, 0.03127197994665597, 0.023204175080719345, 0.01813702727720864,
    1.0026226493445232, 1.0324808391048493e-17, 0.16904358442931514, 0.07713800795263458,
    0.04659770185494099, 0.032195230967127425, 0.02407353478002872, 0.018962819849856524,
    1.0039480328184027, -4.880658832674078e-17, 0.17025745995687916, 0.07824204769439795,
    0.04761867877111798, 0.033153209921377505, 0.02498263659298405, 0.019833136635432416,
    1.0052829675705146, -5.363981194877168e-17, 0.1714887748961512, 0.07937037178898688,
    0.04867015579810387, 0.03414751078016791, 0.025933632038683794, 0.02075075895904277,
    1.0066275913431422, 2.5043560802184795e-18, 0.1727379143449484, 0.08052370858666096,
    0.049753294022480216, 0.03517981415984476, 0.026928807591782303, 0.02171866611847143,
    1.0079820449321606, -5.1752227787916015e-17, 0.17400527499741067, 0.08170281427717771,
    0.050869308474490754, 0.03625189277972148, 0.02797059432865489, 0.02274005123199257,
    1.009346472279358, -7.629540267778587e-17, 0.17529126558904862, 0.08290847418852791,
    0.05201947109659118, 0.03736561731116814, 0.02906157834900464, 0.02381833849793686,
    1.0107210205683146, 2.633607878967623e-17, 0.17659630736263912, 0.08414150415743897,
    0.053205113901311565, 0.03852296265002882, 0.030204512042283655, 0.024957202004750934,
    1.012105840324008, -7.582727232984953e-17, 0.1779208345561254, 0.08540275197624252,
    0.054427632332191146, 0.03972601464683018, 0.031402326275148516, 0.02616058624522311,
    1.0135010855163171, -8.705804407322068e-17, 0.17926529491375298, 0.08669309892103778,
    0.05568848884267018, 0.04097697733236641, 0.032658143583756905, 0.027432728505230583,
    1.0149069136676179, -5.584635415072094e-17, 0.18063015022175238, 0.08801346136644568,
    0.05698921670905097, 0.04227818067968124, 0.03397529246312682, 0.028778183316017197,
    1.016323485964662, -4.943036930497581e-17, 0.18201587686996573, 0.08936479249264101,
    0.05833142409498014, 0.043632088947248356, 0.03535732285511971, 0.030201849179888156,
    1.01775096737495, 7.704611664224385e-17, 0.1834229664409061, 0.0907480840907769,
    0.05971679838636836, 0.04504130965231789, 0.03680802294698516, 0.03170899780259637,
    1.0191895267678206, -1.0613869097246791e-16, 0.18485192632783798, 0.09216436847337668,
    0.06114711081726455, 0.04650860322798978, 0.03833143740394233, 0.03330530609192075,
    1.020639337040483, 5.046596408196905e-17, 0.18630328038357294, 0.09361472049676994,
    0.06262422140895323, 0.04803689342264397, 0.0399318871721141, 0.034996891211373504,
    1.0221005752492498, -2.632985627554085e-17, 0.18777756960179107, 0.09510025970319369,
    0.06415008424646194, 0.04962927850595579, 0.041613991002434146, 0.03679034901104606,
    1.023573422746221, 4.986625487259925e-17, 0.1892753528328202, 0.09662215259076998,
    0.06572675311876787, 0.05128904335191378, 0.04338268886209552, 0.03869279619479937,
    1.0250580653217043, -7.113659424223031e-17, 0.19079720753593998, 0.098181615020215,
    0.06735638755129844, 0.05301967247610405, 0.045243267417909905, 0.040711916624879975,
    1.0265546933526606, -4.741503323356409e-17, 0.19234373057042067, 0.09977991476783336,
    0.06904125926185173, 0.05482486411210767, 0.047201387795830774, 0.04285601221223663,
    1.0280635019574904, -6.544150189403774e-17, 0.19391553902766198, 0.10141837423511331,
    0.07078375907384295, 0.05670854542026126, 0.04926311584312992, 0.04513405889405559,
    1.0295846911574884, -1.8862205210991434e-17, 0.19551327110696476, 0.10309837332606804,
    0.07258640432384057, 0.058674888931353975, 0.051434955144605846, 0.047555768260165374,
    1.03111846604532, 1.0973504492523829e-16, 0.19713758703764944, 0.10482135250437417,
    0.07445184680371966, 0.060728330338186684, 0.05372388307209112, 0.0501316554579512,
    1.032665036960893, 5.777325521136841e-17, 0.19878917005043129, 0.1065888160433455,
    0.07638288128146625, 0.06287358775942568, 0.05613739017780643, 0.05287311408237694,
    1.034224619675018, 2.3661883234451118e-17, 0.20046872740117394, 0.10840233548285985,
    0.07838245464875056, 0.06511568261298507, 0.05868352327723228, 0.05579249884492122,
    1.0357974355812862, -6.5788579651732e-17, 0.20217699145037338, 0.11026355330853643,
    0.08045367574789515, 0.06745996225042895, 0.061370932606649074, 0.058903216914174855,
    1.0373837118966067, -6.207970775472474e-18, 0.20391472080197204, 0.11217418686975172,
    0.08259982593584134, 0.06991212451977759, 0.06420892348492202, 0.06221982893322902,
    1.038983681870887, 2.310007649575221e-17, 0.20568270150537454, 0.11413603255449556,
    0.08482437044822028, 0.07247824444183808, 0.06720751295915377, 0.06575816084678915,
    1.0405975850063638, 4.0405187993459635e-17, 0.20748174832482794, 0.11615097024061834,
    0.08713097063272394, 0.07516480320499243, 0.07037749197027102, 0.06953542781647264,
    1.042225667287128, 1.0289845986206202e-16, 0.20931270608064942, 0.11822096804471938,
    0.08952349712771514, 0.07797871970553213, 0.07373049363834269, 0.07357037166865335,
    1.0438681814194257, 2.7122202983822705e-17, 0.21117645106712987, 0.12034808739179302,
    0.09200604406949214, 0.08092738488543313, 0.07727906833947219, 0.0778834135085991,
    1.0455253870833516, -7.452575137576243e-17, 0.21307389255231884, 0.1225344884307988,
    0.09458294441992197, 0.08401869914726391, 0.08103676632764859, 0.08249682335111362,
    1.0471975511965979, -1.072081766451091e-16, 0.2150059743653076, 0.12478243582357847,
    0.09725878651537391, 0.08726111315711081, 0.08501822874734656, 0.08743490886564396
  ];

  /// <summary><c>sin(j * pi/64)</c> for <c>j</c> from 0 to 32, as double-doubles with the low part first.</summary>
  private static readonly double[] _asinSines = [
    0.0, 0.0, -6.79610372051828e-19, 0.049067674327418015,
    -1.634582362244256e-18, 0.0980171403295606, 3.726947147046568e-18, 0.14673047445536175,
    -7.991079068461731e-18, 0.19509032201612828, -8.751431529719663e-18, 0.2429801799032639,
    -1.892797870777425e-17, 0.2902846772544624, -4.200094003347509e-19, 0.33688985339222005,
    -1.0050772696461588e-17, 0.3826834323650898, 9.411189816295473e-18, 0.4275550934302821,
    6.516678136069013e-18, 0.47139673682599764, -4.5712707523615624e-17, 0.5141027441932218,
    4.709410940561677e-17, 0.5555702330196022, -1.3438641936579467e-17, 0.5956993044924334,
    1.0420901929280035e-17, 0.6343932841636455, -4.048903774929669e-17, 0.6715589548470184,
    -4.833646656726457e-17, 0.7071067811865476, -1.4708616952297345e-17, 0.7409511253549591,
    -3.256590703364977e-17, 0.773010453362737, -3.306060980481491e-17, 0.8032075314806449,
    1.4073856984728024e-18, 0.8314696123025452, -4.818344793633662e-17, 0.8577286100002721,
    -1.9843248405890562e-17, 0.881921264348355, -6.609754468748431e-18, 0.9039892931234433,
    1.7645047084336677e-17, 0.9238795325112867, -2.789637954769834e-17, 0.9415440651830208,
    4.05538698618757e-17, 0.9569403357322088, 1.8365300348428844e-17, 0.970031253194544,
    1.8546939997825006e-17, 0.9807852804032304, -4.098730993704711e-17, 0.989176509964781,
    -4.248691367830441e-17, 0.9951847266721969, -1.2291693337075465e-17, 0.9987954562051724,
    0.0, 1.0
  ];

  /// <summary>The first five Taylor coefficients of <c>asin</c> at 0, as double-doubles (high part first).</summary>
  private static readonly double[] _asinTaylor = [
    1.0, -6.116918166452386e-33, 0.16666666666666666, 9.251858539842912e-18,
    0.075, 2.7755122382264164e-18, 0.044642857142857144, -3.892844186564838e-19,
    0.030381944444440517, 1.4675605165000499e-18
  ];

  /// <summary>The next three Taylor coefficients of <c>asin</c> at 0, in plain doubles.</summary>
  private static readonly double[] _asinTaylorTail = [
    0.022372159104814857, 0.017352737142070387, 0.013992707164303743
  ];

  /// <summary><c>pi/2</c> as a double-double, then <c>-pi/2</c>.</summary>
  private static readonly double[] _asinHalfPi = [
    1.5707963267948966, 6.123233995736766e-17, -1.5707963267948966, -6.123233995736766e-17
  ];

  /// <summary>
  /// The inputs whose arc sine the refinement cannot round: <c>|x|</c>, then the result as the sum of
  /// two doubles, sorted by <c>|x|</c>.
  /// </summary>
  private static readonly double[] _asinExceptions = [
    2.149119332890821e-08, 2.1491193328908213e-08, -1.6543612251060552e-24,
    2.7077206862451724e-08, 2.7077206862451727e-08, -6.374746099410145e-41,
    1.5728569292736223e-07, 1.5728569292736287e-07, 1.3234889800848441e-23,
    2.952153011168167e-07, 2.95215301116821e-07, -4.109769340539622e-40,
    4.280976477120543e-07, 4.2809764771206737e-07, 4.272035621321839e-39,
    1.5975474262748178e-06, 1.5975474262754973e-06, 1.9876285236124217e-38,
    1.7881393431759567e-05, 1.788139343271248e-05, -2.087620150684472e-37,
    3.046150971890029e-05, 3.046150972361118e-05, -1.6940658945086003e-21,
    7.297794305487601e-05, 7.297794311965344e-05, -6.776263578034402e-21,
    0.00010763481001671434, 0.00010763481022454375, -6.776263578034402e-21,
    0.0033260143335575236, 0.003326020465855661, -1.3907958395387223e-35,
    0.014693647353325203, 0.014694176139126642, -2.088670860122526e-34,
    0.019643316477075002, 0.019644579957771267, -9.012863259990004e-35,
    0.02454281786240139, 0.02454528242451394, -3.7663990512802885e-33,
    0.02515936880750056, 0.025162023851425852, 8.632495344096942e-34,
    0.02869829488520104, 0.02870223562751182, -2.596947303706955e-33,
    0.02993389623219942, 0.029938368354472628, -3.005337275887711e-33,
    0.03604596164296301, 0.03605377203166768, 9.920636807864318e-34,
    0.04412391368975044, 0.04413824386807564, -1.0409160373485304e-34,
    0.05133124278218131, 0.051353811637605475, 3.4694469519536138e-18,
    0.05755380074796938, 0.05758562212214113, 8.030697779203957e-35,
    0.06206990721934187, 0.06210983232298361, -2.864891108246544e-33,
    0.07371552337036429, 0.07378244857821148, -4.281228767365796e-33,
    0.0824731502462773, 0.08256693217450291, -5.800545266499361e-33,
    0.22701926203464254, 0.22901593474400364, -2.3395611233825316e-33,
    0.32280770822479904, 0.32869451468275884, -5.320031207286819e-33,
    0.45208132390987693, 0.46909734667874736, 6.584030156802473e-33,
    0.4631883863152047, 0.48158940553834506, -2.37057545857783e-33,
    0.47810755953933304, 0.49849878588087543, 8.176045348519162e-37
  ];

  /// <summary>The polynomial for <c>|x| &lt; 0x1.7d83a6p+0</c> in <c>float</c>; see <see cref="AsinF"/>.</summary>
  private static readonly double[] _asinFFast = [
    1.000000000000001, 0.16666694674143204, 0.07497112542795417, 0.0458179575336707,
    0.005331008900413985, 0.34410258152367046, -2.680930042099564, 15.541270760972983,
    -63.17329833405016, 184.79515144873312, -390.0198166803775, 589.2790780950768,
    -621.89777643639, 435.8403729646551, -182.48552714860514, 34.63705332873756
  ];

  /// <summary>
  /// The two accurate polynomials of <see cref="AsinF"/>, shared with <see cref="AcosF"/>: from 0 that
  /// of <c>(asin(x) - x) / x^3</c> in <c>x^2</c> for <c>|x| &lt; 0.5</c>; from 12 that in
  /// <c>z = 1 - |x|</c> with <c>asin(|x|) = pi/2 - sqrt(z) * P(z)</c> for <c>|x| &gt;= 0.5</c>.
  /// </summary>
  private static readonly double[] _asinFAccurate = [
    0.1666666666666473, 0.07500000000425495, 0.044642856775806136, 0.030381960865898193,
    0.022371723076598973, 0.01736016508415668, 0.01388117521087077, 0.012193412697105537,
    0.0064317722535114155, 0.019772599269663224, -0.016582844751635805, 0.03214361520381252,
    1.4142135623730947, 0.11785113019794026, 0.026516504277464867, 0.007891817376506467,
    0.0026853981502991025, 0.000988848836905083, 0.00038253952347123667, 0.00015842231966484147,
    5.141249514992934e-05, 5.100236375743145e-05, -1.66352623873716e-05, 2.1931983490736225e-05
  ];

  /// <summary>
  /// The arc sine of <c>x</c> in radians, correctly rounded: <c>x + x 2^-55</c> for <c>|x|</c> below
  /// <c>0x1.7137449123ef6p-26</c>, a NaN outside <c>[-1, 1]</c>. Below 0.5 a polynomial in
  /// <c>x^2 - j/128</c> picked by <c>j = round(128 x^2)</c>; above, the same polynomial on
  /// <c>sqrt((1 - |x|)/2)</c> by <c>asin(x) = pi/2 - 2 asin(sqrt((1 - x)/2))</c>. When that does not
  /// round safely, a rotation by a multiple of <c>pi/64</c> and a Taylor series in double-double
  /// arithmetic decide, and the few inputs even that leaves open come from a table.
  /// </summary>
  /// <param name="x">The sine.</param>
  /// <returns>The angle in <c>[-pi/2, pi/2]</c>.</returns>
  public static double Asin(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = bits << 1;
    double t;
    double z;
    double zl;
    double jd;
    double f0h;
    double f0l;
    double eps;
    if (ax > 0x7fc0000000000000UL) {
      // |x| > 0.5: asin(x) = pi/2 - 2 asin(sqrt((1 - x)/2)), with the sign of x.
      var k = (int)(bits >> 63) * 2;
      f0h = _asinHalfPi[k];
      f0l = _asinHalfPi[k + 1];
      if (ax >= 0x7fe0000000000000UL) {
        if (ax == 0x7fe0000000000000UL)
          return f0h + f0l;

        if (ax > 0xffe0000000000000UL)
          return x + x;

        return 0.0 / 0.0;
      }

      t = 2 - 2 * Math.Abs(x);
      jd = Math.Round(t * 32.0);
      z = CopySign(Math.Sqrt(t), -x);
      zl = Math.FusedMultiplyAdd(z, z, -t) * ((-0.5 / t) * z);
      t = 0.25 * t - jd * 0.0078125;
      eps = Math.Abs(z * t) * 3.5475095083725705e-16;
    } else {
      // Below 0x1.7137449123ef6p-26, x is within half an ulp of asin(x).
      if (ax < 0x7cae26e892247decUL)
        return Math.FusedMultiplyAdd(2.7755575615628914e-17, x, x);

      f0h = 0;
      f0l = 0;
      t = x * x;
      jd = Math.Round(t * 128.0);
      t = Math.FusedMultiplyAdd(x, x, -0.0078125 * jd);
      z = x;
      zl = 0;
      eps = Math.Abs(z * t) * 3.2526065174565133e-16;
    }

    // asin(z + zl) = (z + zl) * (c0 + (c1 + t * P(t))) with t = z^2 - j/128.
    var c = (int)jd * 8;
    var t2 = t * t;
    var d = t * ((_asinCoefficients[c + 2] + t * _asinCoefficients[c + 3])
      + t2 * ((_asinCoefficients[c + 4] + t * _asinCoefficients[c + 5]) + t2 * (_asinCoefficients[c + 6] + t * _asinCoefficients[c + 7])));
    var fh = _asinCoefficients[c];
    var fl = _asinCoefficients[c + 1] + d;

    // (fh, fl) *= (z, zl), then += (f0h, f0l).
    var product = fh * z;
    fl = (fl * z + fh * zl) + Math.FusedMultiplyAdd(fh, z, -product);
    fh = product;
    var sum = f0h + fh;
    var sumError = fh - (sum - f0h);
    fl = (f0l + fl) + sumError;
    fh = sum;

    var lb = fh + (fl - eps);
    var ub = fh + (fl + eps);
    if (lb != ub)
      return AsinRefine(x, lb);

    return lb;
  }

  /// <summary>
  /// The accurate path of <see cref="Asin"/>: with <c>x = sin(phi)</c> and <c>sqrt(1 - x^2)</c> in
  /// double-double, the angle is rotated by the multiple <c>j pi/64</c> nearest to <c>phi</c>, and the
  /// remainder's arc sine summed by its Taylor series.
  /// </summary>
  private static double AsinRefine(double x, double phi) {
    // c2h + c2l = 1 - x^2.
    var s2 = x * x;
    var dx2 = Math.FusedMultiplyAdd(x, x, -s2);
    var c2h = 1.0 - s2;
    var c2l = -s2 - (c2h - 1.0);
    c2l -= dx2;
    var sum = c2h + c2l;
    c2l -= sum - c2h;
    c2h = sum;

    // ch + cl = sqrt(1 - x^2).
    var ch = Math.Sqrt(c2h);
    var cl = (c2l - Math.FusedMultiplyAdd(ch, ch, -c2h)) * (0.5 / ch);

    // j = round(|phi| * 64/pi), 0 <= j <= 32.
    var jf = Math.Round(Math.Abs(phi) * 20.371832715762604);
    var j = (int)jf;
    var cosHigh = _asinSines[(32 - j) * 2 + 1];
    var cosLow = _asinSines[(32 - j) * 2];
    var sinHigh = _asinSines[j * 2 + 1];
    var sinLow = _asinSines[j * 2];

    var ax = Math.Abs(x);
    var dsh = ax - sinHigh;
    var dsl = -sinLow;
    var dch = ch - cosHigh;
    var dcl = cl - cosLow;

    // The products rounded to multiples of 2^-56 through the magic constant, so that v is exact.
    const double Magic = 0.09375;
    var sc = Math.FusedMultiplyAdd(sinHigh, dch, Magic) - Magic;
    var dsc = Math.FusedMultiplyAdd(sinHigh, dch, -sc);
    var cs = Math.FusedMultiplyAdd(cosHigh, dsh, Magic) - Magic;
    var dcs = Math.FusedMultiplyAdd(cosHigh, dsh, -cs);

    var v = cs - sc;
    var dv = (cosHigh * dsl + cosLow * dsh) - (sinHigh * dcl + sinLow * dch) - (dsc - dcs);
    sum = v + dv;
    dv -= sum - v;
    v = sum;
    var sgn = CopySign(1.0, x);
    var jt = jf * sgn;

    // v2 + dv2 = (v + dv)^2.
    var v2 = v * v;
    var dv2 = (dv * v + v * dv) + Math.FusedMultiplyAdd(v, v, -v2);
    v *= sgn;
    dv *= sgn;

    // The Taylor series in v2: the tail in doubles, the head in double-double by Horner.
    var fl = v2 * (_asinTaylorTail[0] + v2 * (_asinTaylorTail[1] + v2 * _asinTaylorTail[2]));
    var fh = _asinTaylor[8] + fl;
    fl -= fh - _asinTaylor[8];
    fl = _asinTaylor[9] + fl;
    for (var i = 3; i >= 0; i--) {
      var high = fh * v2;
      fl = (fl * v2 + fh * dv2) + Math.FusedMultiplyAdd(fh, v2, -high);
      sum = _asinTaylor[2 * i] + high;
      var error = high - (sum - _asinTaylor[2 * i]);
      fl = (_asinTaylor[2 * i + 1] + fl) + error;
      fh = sum;
    }

    var times = fh * v;
    fl = (fl * v + fh * dv) + Math.FusedMultiplyAdd(fh, v, -times);
    fh = times;

    // ph + pl + ps = jt * pi/64, exact products for every integer jt; then add fh + fl.
    var ph = jt * 0.04908738521234035;
    var pl = jt * 1.684469643174416e-16;
    var ps = jt * -3.917809439666406e-31;
    sum = fh + pl;
    var low = pl - (sum - fh);
    ps = (fl + ps) + low;
    pl = sum;
    sum = ph + pl;
    pl -= sum - ph;
    ph = sum;
    sum = pl + ps;
    ps -= sum - pl;
    pl = sum;
    sum = ph + pl;
    pl -= sum - ph;
    ph = sum;
    sum = pl + ps;
    ps -= sum - pl;
    pl = sum;

    // Hard to round when pl is within two ulps of half an ulp of ph, or far below it.
    var tn = ((ulong)BitConverter.DoubleToInt64Bits(ph) & 0x7ffUL << 52) - (53UL << 52);
    var tl = (ulong)BitConverter.DoubleToInt64Bits(pl) & ~0UL >> 1;
    var gap = tn - tl;
    var result = ph + pl;
    if (gap <= 2 || gap >> 52 > 47)
      result = AsinException(x, result);

    return result;
  }

  /// <summary>The correctly rounded arc sine of an input of <see cref="_asinExceptions"/>, otherwise <paramref name="f"/>.</summary>
  private static double AsinException(double x, double f) {
    var ax = Math.Abs(x);
    var a = 0;
    var b = _asinExceptions.Length / 3;
    while (a + 1 < b) {
      var m = (a + b) / 2;
      if (_asinExceptions[3 * m] <= ax)
        a = m;
      else
        b = m;
    }

    if (_asinExceptions[3 * a] != ax)
      return f;

    return x > 0 ? _asinExceptions[3 * a + 1] + _asinExceptions[3 * a + 2] : -_asinExceptions[3 * a + 1] - _asinExceptions[3 * a + 2];
  }

  /// <summary>
  /// The arc sine of a <c>float</c> in radians, correctly rounded: a NaN outside <c>[-1, 1]</c>. A
  /// polynomial in <c>x^2</c> evaluated in doubles answers when its error bounds round alike; otherwise
  /// a more accurate one, near 1 in <c>sqrt(1 - |x|)</c>.
  /// </summary>
  /// <param name="x">The sine.</param>
  /// <returns>The angle in <c>[-pi/2, pi/2]</c>.</returns>
  public static float AsinF(float x) {
    const double HalfPi = 1.5707963267948966;
    var xs = (double)x;
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    var ax = bits << 1;
    if (ax > 0x7fu << 24)
      return ax > 0xffu << 24 ? x + x : (float)(0.0 / 0.0);

    double r;
    if (ax < 0x7ec29000u) {
      // |x| < 0x1.7d83a6p-1; below 2^-12, x + x * 2^-25 rounds like asin(x).
      if (ax < 115u << 24)
        return MathF.FusedMultiplyAdd(x, 2.9802322387695312e-08f, x);

      var z = xs;
      var z2 = z * z;
      var z4 = z2 * z2;
      var z8 = z4 * z4;
      var z16 = z8 * z8;
      r = z * ((((_asinFFast[0] + z2 * _asinFFast[1]) + z4 * (_asinFFast[2] + z2 * _asinFFast[3]))
          + z8 * ((_asinFFast[4] + z2 * _asinFFast[5]) + z4 * (_asinFFast[6] + z2 * _asinFFast[7])))
        + z16 * (((_asinFFast[8] + z2 * _asinFFast[9]) + z4 * (_asinFFast[10] + z2 * _asinFFast[11]))
          + z8 * ((_asinFFast[12] + z2 * _asinFFast[13]) + z4 * (_asinFFast[14] + z2 * _asinFFast[15]))));
      var ub = (float)r;
      var lb = (float)(r - z * 9.015999891115456e-10);
      if (ub == lb)
        return ub;
    }

    if (ax < 0x7eu << 24) {
      var z = xs;
      var z2 = z * z;
      var c0 = AsinFPolynomial12(z2, 0);
      r = z + (z * z2) * c0;
    } else {
      // Upstream also lists |x| = 0x1.2ab445p-1 here, which only a build that contracts the fast
      // polynomial into fused multiply-adds sends this far; without contraction it rounds before.
      if (ax == 0x7e107434u)
        return CopySignF(0.5611220598220825f, x) + CopySignF(1.4901161193847656e-08f, x);

      var bx = Math.Abs(xs);
      var z = 1.0 - bx;
      var s = Math.Sqrt(z);
      r = HalfPi - s * AsinFPolynomial12(z, 12);
      r = CopySign(r, xs);
    }

    return (float)r;
  }

  /// <summary>The polynomial of degree 11 from <paramref name="offset"/> in <see cref="_asinFAccurate"/> at <paramref name="z"/>, in Estrin's scheme.</summary>
  private static double AsinFPolynomial12(double z, int offset) {
    var z2 = z * z;
    var z4 = z2 * z2;
    var c0 = _asinFAccurate[offset + 0] + z * _asinFAccurate[offset + 1];
    var c2 = _asinFAccurate[offset + 2] + z * _asinFAccurate[offset + 3];
    var c4 = _asinFAccurate[offset + 4] + z * _asinFAccurate[offset + 5];
    var c6 = _asinFAccurate[offset + 6] + z * _asinFAccurate[offset + 7];
    var c8 = _asinFAccurate[offset + 8] + z * _asinFAccurate[offset + 9];
    var c10 = _asinFAccurate[offset + 10] + z * _asinFAccurate[offset + 11];
    c0 += c2 * z2;
    c4 += c6 * z2;
    c8 += z2 * c10;
    c0 += z4 * (c4 + z4 * c8);
    return c0;
  }

}
