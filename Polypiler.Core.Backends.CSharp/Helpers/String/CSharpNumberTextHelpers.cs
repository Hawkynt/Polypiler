namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the conversions between numbers and text: what .NET Framework and .NET Standard
/// lack of .NET Core 3.0's rules, and the rules of ECMA-262, which no framework has. They are C# 2
/// for .NET Framework 2.0 like every helper and compute the same whatever overflow checking the
/// consuming project sets. <see cref="NumberText"/> finds the shortest digits with Ryu (Ulf Adams,
/// Apache-2.0, THIRD-PARTY-NOTICE.Ryu.txt), converted from <c>d2s.c</c> with the tables
/// of <c>d2s_small_table.h</c>, copied exactly; its fixed notation and <see cref="ParseText"/> work on
/// the exact value with integers of many digits, and the radices other than 2, 4, 8, 10, 16 and 32 of
/// <c>parseInt</c> follow V8's approximation (BSD-3-Clause, THIRD-PARTY-NOTICE.V8.txt).
/// </summary>
internal static class CSharpNumberTextHelpers {

  /// <summary>
  /// A number as text by a rule: 0 .NET's shortest digits of a <c>double</c>, 1 of a <c>float</c>
  /// (given as the <c>double</c> it is), 2 ECMA-262's <c>Number::toString</c>, 3 <c>toFixed</c> and 4
  /// .NET's <c>"F&lt;n&gt;"</c>, the last two with the digits after the point.
  /// </summary>
  public static CSharpHelper NumberText { get; } = new("NumberText", "string", "double value, int mode, int digits", Lines("""
    unchecked {
      char zero = "0"[0];
      long bits = global::System.BitConverter.DoubleToInt64Bits(value);
      bool negative = bits < 0;
      if (double.IsNaN(value)) return "NaN";
      if (mode == 3 && (value >= 1e21 || value <= -1e21)) mode = 2;
      if (value == double.PositiveInfinity || value == double.NegativeInfinity) return negative ? "-Infinity" : "Infinity";
      string sign = negative && (mode != 2 && mode != 3 || value != 0) ? "-" : "";
      ulong mantissa = (ulong)bits & 0xFFFFFFFFFFFFFUL;
      int exponent = (int)((bits >> 52) & 0x7FF);
      string text;
      if (mode >= 3) {
        if (exponent == 0) exponent = 1;
        else mantissa |= 1UL << 52;
        int shift = exponent - 1075;
        uint[] limbs = new uint[90];
        limbs[0] = (uint)(mantissa % 1000000000UL);
        limbs[1] = (uint)(mantissa / 1000000000UL % 1000000000UL);
        limbs[2] = (uint)(mantissa / 1000000000000000000UL);
        int used = 3;
        int left = shift < 0 ? -shift : shift;
        while (left > 0) {
          int step = shift < 0 ? (left < 13 ? left : 13) : (left < 30 ? left : 30);
          ulong factor = 1;
          for (int index = 0; index < step; ++index) factor *= shift < 0 ? 5UL : 2UL;
          ulong carry = 0;
          for (int index = 0; index < used; ++index) {
            ulong product = limbs[index] * factor + carry;
            limbs[index] = (uint)(product % 1000000000UL);
            carry = product / 1000000000UL;
          }
          while (carry != 0) {
            limbs[used++] = (uint)(carry % 1000000000UL);
            carry /= 1000000000UL;
          }
          left -= step;
        }
        global::System.Text.StringBuilder exact = new global::System.Text.StringBuilder();
        exact.Append(limbs[used - 1].ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        for (int index = used - 2; index >= 0; --index) exact.Append(limbs[index].ToString("D9", global::System.Globalization.CultureInfo.InvariantCulture));
        exact.Append(new string(zero, digits));
        int dropped = shift < 0 ? -shift : 0;
        if (exact.Length <= dropped + digits) exact.Insert(0, "0", dropped + digits + 1 - exact.Length);
        int kept = exact.Length - dropped;
        bool up = false;
        if (dropped > 0) {
          int first = "0123456789".IndexOf(exact[kept]);
          bool rest = false;
          for (int index = kept + 1; index < exact.Length && !rest; ++index) rest = exact[index] != zero;
          up = first > 5 || first == 5 && (rest || mode == 3 || "0123456789".IndexOf(exact[kept - 1]) % 2 == 1);
        }
        exact.Length = kept;
        for (int index = kept - 1; up && index >= 0; --index) {
          if (exact[index] == "9"[0]) {
            exact[index] = zero;
          } else {
            exact[index] = (char)(exact[index] + 1);
            up = false;
          }
        }
        if (up) exact.Insert(0, "1");
        text = exact.ToString().TrimStart(zero);
        if (text.Length <= digits) text = new string(zero, digits + 1 - text.Length) + text;
        return sign + (digits == 0 ? text : text.Substring(0, text.Length - digits) + "." + text.Substring(text.Length - digits));
      }
      if (value == 0) return sign + "0";
      ulong[] pow5 = new ulong[] {
        1UL, 5UL, 25UL, 125UL, 625UL, 3125UL, 15625UL, 78125UL,
        390625UL, 1953125UL, 9765625UL, 48828125UL, 244140625UL, 1220703125UL, 6103515625UL, 30517578125UL,
        152587890625UL, 762939453125UL, 3814697265625UL, 19073486328125UL, 95367431640625UL, 476837158203125UL, 2384185791015625UL, 11920928955078125UL,
        59604644775390625UL, 298023223876953125UL
      };
      ulong[] m2;
      int e2;
      int mmShift;
      ulong output;
      int removed = 0;
      int decimalExponent;
      if (mode == 1) {
        int single = global::System.BitConverter.ToInt32(global::System.BitConverter.GetBytes((float)value), 0);
        int singleMantissa = single & 0x7FFFFF;
        int singleExponent = (single >> 23) & 0xFF;
        m2 = new ulong[] { singleExponent == 0 ? (ulong)singleMantissa : (ulong)(singleMantissa | 0x800000) };
        e2 = (singleExponent == 0 ? 1 : singleExponent) - 127 - 23 - 2;
        mmShift = singleMantissa != 0 || singleExponent <= 1 ? 1 : 0;
      } else {
        m2 = new ulong[] { exponent == 0 ? mantissa : mantissa | 1UL << 52 };
        e2 = (exponent == 0 ? 1 : exponent) - 1023 - 52 - 2;
        mmShift = mantissa != 0 || exponent <= 1 ? 1 : 0;
      }
      ulong fraction = mode == 1 || e2 + 2 > 0 || e2 + 2 < -52 ? 1UL : m2[0] & ((1UL << -(e2 + 2)) - 1);
      if (fraction == 0) {
        output = m2[0] >> -(e2 + 2);
        while (output % 10 == 0) {
          output /= 10;
          ++removed;
        }
        decimalExponent = removed;
      } else {
        bool even = (m2[0] & 1) == 0;
        ulong mv = 4 * m2[0];
        ulong[] split;
        int q;
        int j;
        bool vmIsTrailingZeros = false;
        bool vrIsTrailingZeros = false;
        int power;
        bool inverse = e2 >= 0;
        if (inverse) {
          q = (int)(((uint)e2 * 78913) >> 18) - (e2 > 3 ? 1 : 0);
          decimalExponent = q;
          power = q;
          j = -e2 + q + 125 + (int)(((uint)q * 1217359) >> 19);
        } else {
          q = (int)(((uint)-e2 * 732923) >> 20) - (-e2 > 1 ? 1 : 0);
          decimalExponent = q + e2;
          power = -e2 - q;
          j = q - (int)(((uint)power * 1217359) >> 19) - 1 + 125;
        }
        int baseIndex = inverse ? (power + 25) / 26 : power / 26;
        int offset = inverse ? baseIndex * 26 - power : power - baseIndex * 26;
        ulong[] table = inverse ? new ulong[] {
          1UL, 2305843009213693952UL, 5955668970331000884UL, 1784059615882449851UL, 8982663654677661702UL, 1380349269358112757UL, 7286864317269821294UL, 2135987035920910082UL,
          7005857020398200553UL, 1652639921975621497UL, 17965325103354776697UL, 1278668206209430417UL, 8928596168509315048UL, 1978643211784836272UL, 10075671573058298858UL, 1530901034580419511UL,
          597001226353042382UL, 1184477304306571148UL, 1527430471115325346UL, 1832889850782397517UL, 12533209867169019542UL, 1418129833677084982UL, 5577825024675947042UL, 2194449627517475473UL,
          11006974540203867551UL, 1697873161311732311UL, 10313493231639821582UL, 1313665730009899186UL, 12701016819766672773UL, 2032799256770390445UL
        } : new ulong[] {
          0UL, 1152921504606846976UL, 0UL, 1490116119384765625UL, 1032610780636961552UL, 1925929944387235853UL, 7910200175544436838UL, 1244603055572228341UL,
          16941905809032713930UL, 1608611746708759036UL, 13024893955298202172UL, 2079081953128979843UL, 6607496772837067824UL, 1343575221513417750UL, 17332926989895652603UL, 1736530273035216783UL,
          13037379183483547984UL, 2244412773384604712UL, 1605989338741628675UL, 1450417759929778918UL, 9630225068416591280UL, 1874621017369538693UL, 665883850346957067UL, 1211445438634777304UL,
          14931890668723713708UL, 1565756531257009982UL
        };
        uint[] offsets = inverse ? new uint[] {
          0x54544554, 0x04055545, 0x10041000, 0x00400414, 0x40010000, 0x41155555, 0x00000454, 0x00010044,
          0x40000000, 0x44000041, 0x50454450, 0x55550054, 0x51655554, 0x40004000, 0x01000001, 0x00010500,
          0x51515411, 0x05555554, 0x50411500, 0x40040000, 0x05040110, 0x00000000
        } : new uint[] {
          0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x40000000, 0x59695995, 0x55545555, 0x56555515,
          0x41150504, 0x40555410, 0x44555145, 0x44504540, 0x45555550, 0x40004000, 0x96440440, 0x55565565,
          0x54454045, 0x40154151, 0x55559155, 0x51405555, 0x00000105
        };
        split = new ulong[] { table[2 * baseIndex], table[2 * baseIndex + 1] };
        if (offset != 0) {
          ulong[] product = new ulong[4];
          for (int half = 0; half < 2; ++half) {
            ulong a = pow5[offset];
            ulong b = half == 0 && inverse ? split[0] - 1 : split[half];
            ulong b00 = (a & 0xFFFFFFFFUL) * (b & 0xFFFFFFFFUL);
            ulong b01 = (a & 0xFFFFFFFFUL) * (b >> 32);
            ulong b10 = (a >> 32) * (b & 0xFFFFFFFFUL);
            ulong b11 = (a >> 32) * (b >> 32);
            ulong mid1 = b10 + (b00 >> 32);
            ulong mid2 = b01 + (mid1 & 0xFFFFFFFFUL);
            product[2 * half] = (mid2 << 32) | (b00 & 0xFFFFFFFFUL);
            product[2 * half + 1] = b11 + (mid1 >> 32) + (mid2 >> 32);
          }
          ulong sum = product[1] + product[2];
          ulong high = product[3] + (sum < product[1] ? 1UL : 0UL);
          int baseBits = (int)(((uint)(baseIndex * 26) * 1217359) >> 19) + 1;
          int bitsOf = (int)(((uint)power * 1217359) >> 19) + 1;
          int delta = inverse ? baseBits - bitsOf : bitsOf - baseBits;
          split[0] = ((sum << (64 - delta)) | (product[0] >> delta)) + (inverse ? 1UL : 0UL) + ((offsets[power / 16] >> ((power % 16) << 1)) & 3);
          split[1] = (high << (64 - delta)) | (sum >> delta);
        }
        ulong[] scaled = new ulong[] { mv, mv + 2, mv - 1 - (ulong)mmShift };
        for (int which = 0; which < 3; ++which) {
          ulong[] product = new ulong[4];
          for (int half = 0; half < 2; ++half) {
            ulong a = scaled[which];
            ulong b = split[half];
            ulong b00 = (a & 0xFFFFFFFFUL) * (b & 0xFFFFFFFFUL);
            ulong b01 = (a & 0xFFFFFFFFUL) * (b >> 32);
            ulong b10 = (a >> 32) * (b & 0xFFFFFFFFUL);
            ulong b11 = (a >> 32) * (b >> 32);
            ulong mid1 = b10 + (b00 >> 32);
            ulong mid2 = b01 + (mid1 & 0xFFFFFFFFUL);
            product[2 * half] = (mid2 << 32) | (b00 & 0xFFFFFFFFUL);
            product[2 * half + 1] = b11 + (mid1 >> 32) + (mid2 >> 32);
          }
          ulong sum = product[1] + product[2];
          ulong high = product[3] + (sum < product[1] ? 1UL : 0UL);
          scaled[which] = (high << (128 - j)) | (sum >> (j - 64));
        }
        ulong vr = scaled[0];
        ulong vp = scaled[1];
        ulong vm = scaled[2];
        if (inverse) {
          if (q <= 21) {
            ulong candidate = mv % 5 == 0 ? mv : even ? mv - 1 - (ulong)mmShift : mv + 2;
            int factor = 0;
            while (candidate % 5 == 0) {
              candidate /= 5;
              ++factor;
            }
            if (mv % 5 == 0) vrIsTrailingZeros = factor >= q;
            else if (even) vmIsTrailingZeros = factor >= q;
            else if (factor >= q) --vp;
          }
        } else if (q <= 1) {
          vrIsTrailingZeros = true;
          if (even) vmIsTrailingZeros = mmShift == 1;
          else --vp;
        } else if (q < 63) {
          vrIsTrailingZeros = (mv & ((1UL << q) - 1)) == 0;
        }
        int lastRemovedDigit = 0;
        if (vmIsTrailingZeros || vrIsTrailingZeros) {
          while (vp / 10 > vm / 10) {
            vmIsTrailingZeros &= vm % 10 == 0;
            vrIsTrailingZeros &= lastRemovedDigit == 0;
            lastRemovedDigit = (int)(vr % 10);
            vr /= 10;
            vp /= 10;
            vm /= 10;
            ++removed;
          }
          if (vmIsTrailingZeros) {
            while (vm % 10 == 0) {
              vrIsTrailingZeros &= lastRemovedDigit == 0;
              lastRemovedDigit = (int)(vr % 10);
              vr /= 10;
              vp /= 10;
              vm /= 10;
              ++removed;
            }
          }
          if (vrIsTrailingZeros && lastRemovedDigit == 5 && vr % 2 == 0) lastRemovedDigit = 4;
          output = vr + ((vr == vm && (!even || !vmIsTrailingZeros)) || lastRemovedDigit >= 5 ? 1UL : 0UL);
        } else {
          bool roundUp = false;
          if (vp / 100 > vm / 100) {
            roundUp = vr % 100 >= 50;
            vr /= 100;
            vp /= 100;
            vm /= 100;
            removed += 2;
          }
          while (vp / 10 > vm / 10) {
            roundUp = vr % 10 >= 5;
            vr /= 10;
            vp /= 10;
            vm /= 10;
            ++removed;
          }
          output = vr + (vr == vm || roundUp ? 1UL : 0UL);
        }
        decimalExponent += removed;
      }
      text = output.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
      int leading = decimalExponent + text.Length - 1;
      text = text.TrimEnd(zero);
      bool ecma = mode == 2 || mode == 3;
      if (ecma ? leading < -6 || leading >= 21 : leading < -4 || leading >= (mode == 1 ? 9 : 17)) {
        string power10 = (leading < 0 ? -leading : leading).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        return sign + text.Substring(0, 1) + (text.Length > 1 ? "." + text.Substring(1) : "") + (ecma ? "e" : "E") + (leading < 0 ? "-" : "+") + (ecma || power10.Length > 1 ? power10 : "0" + power10);
      }
      if (leading < 0) return sign + "0." + new string(zero, -leading - 1) + text;
      if (text.Length <= leading + 1) return sign + text + new string(zero, leading + 1 - text.Length);
      return sign + text.Substring(0, leading + 1) + "." + text.Substring(leading + 1);
    }
    """));

  /// <summary>
  /// A text as a number by a grammar: 0 .NET's <c>double.Parse</c> and 1 <c>float.Parse</c> with
  /// <c>NumberStyles.Float | NumberStyles.AllowThousands</c> and the invariant culture, which throw a
  /// <c>FormatException</c> for a malformed text; 2 ECMA-262's <c>Number(s)</c>, 3 <c>parseFloat</c>
  /// and 4 <c>parseInt</c> with a radix, 0 for none, which return a NaN.
  /// </summary>
  public static CSharpHelper ParseText { get; } = new("ParseText", "double", "string text, int mode, int radix", Lines("""
    unchecked {
      string white = mode < 2 ? "\u0009\u000A\u000B\u000C\u000D " : "\u0009\u000A\u000B\u000C\u000D \u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
      if (mode < 2) {
        string special = text.Trim("\u0009\u000A\u000B\u000C\u000D \u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000".ToCharArray());
        string symbol = special.Length > 0 && "+-".IndexOf(special[0]) >= 0 ? special.Substring(1) : special;
        if (string.Compare(symbol, "Infinity", global::System.StringComparison.OrdinalIgnoreCase) == 0) return special[0] == "-"[0] ? double.NegativeInfinity : double.PositiveInfinity;
        if (string.Compare(symbol, "NaN", global::System.StringComparison.OrdinalIgnoreCase) == 0) return double.NaN;
      }
      string table = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
      int at = 0;
      int end = text.Length;
      while (at < end && white.IndexOf(text[at]) >= 0) ++at;
      if (mode < 2) {
        while (end > at && text[end - 1] == 0) --end;
      }
      if (mode < 3) {
        while (end > at && white.IndexOf(text[end - 1]) >= 0) --end;
      }
      if (mode == 2 && at == end) return 0.0;
      uint[] number = new uint[140];
      uint[] divisor = new uint[140];
      divisor[0] = 1;
      bool negative = false;
      bool overflow = false;
      int count = 0;
      if (mode == 2 && end - at > 2 && text[at] == table[0] && "xXoObB".IndexOf(text[at + 1]) >= 0) {
        radix = "xX".IndexOf(text[at + 1]) >= 0 ? 16 : "oO".IndexOf(text[at + 1]) >= 0 ? 8 : 2;
        at += 2;
      } else {
        if (at < end && "+-".IndexOf(text[at]) >= 0) {
          negative = text[at] == "-"[0];
          ++at;
        }
        string rest = text.Substring(at, end - at);
        if (mode == 2 && rest == "Infinity" || mode == 3 && rest.StartsWith("Infinity", global::System.StringComparison.Ordinal)) {
          return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (mode == 4) {
          if (radix != 0 && (radix < 2 || radix > 36)) return double.NaN;
          if ((radix == 0 || radix == 16) && end - at >= 2 && text[at] == table[0] && "xX".IndexOf(text[at + 1]) >= 0) {
            radix = 16;
            at += 2;
          } else if (radix == 0) {
            radix = 10;
          }
        } else {
          radix = 10;
        }
      }
      if (mode == 4 && radix != 2 && radix != 4 && radix != 8 && radix != 10 && radix != 16 && radix != 32) {
        int stop = at;
        while (stop < end && table.IndexOf(text[stop]) >= 0 && table.IndexOf(text[stop]) % 36 + table.IndexOf(text[stop]) / 36 * 10 < radix) ++stop;
        if (stop == at) return double.NaN;
        while (at < stop && text[at] == table[0]) ++at;
        double approximation = 0.0;
        while (at < stop) {
          uint part = 0;
          uint multiplier = 1;
          for (; at < stop && multiplier * (uint)radix <= 119304647U; ++at) {
            part = part * (uint)radix + (uint)(table.IndexOf(text[at]) % 36 + table.IndexOf(text[at]) / 36 * 10);
            multiplier *= (uint)radix;
          }
          approximation = approximation * multiplier + part;
        }
        return negative ? -approximation : approximation;
      }
      global::System.Text.StringBuilder digits = new global::System.Text.StringBuilder();
      int exponent = 0;
      bool sticky = false;
      bool point = false;
      while (at < end) {
        char unit = text[at];
        int digit = table.IndexOf(unit) < 0 ? 99 : table.IndexOf(unit) % 36 + table.IndexOf(unit) / 36 * 10;
        if (digit < radix) {
          ++count;
          if (radix != 10) {
            if (number[36] != 0) overflow = true;
            if (!overflow) {
              ulong carry = (ulong)digit;
              for (int index = 0; index < 140; ++index) {
                ulong product = (ulong)number[index] * (ulong)radix + carry;
                number[index] = (uint)product;
                carry = product >> 32;
              }
            }
          } else if (digits.Length == 0 && digit == 0) {
            if (point) --exponent;
          } else if (digits.Length < 800) {
            digits.Append(unit);
            if (point) --exponent;
          } else {
            sticky |= digit != 0;
            if (!point) ++exponent;
          }
        } else if (unit == "."[0] && !point && radix == 10 && mode != 4) {
          point = true;
        } else if (unit == ","[0] && mode < 2 && !point && count > 0) {
        } else {
          break;
        }
        ++at;
      }
      if (count > 0 && radix == 10 && mode != 4 && at < end && "eE".IndexOf(text[at]) >= 0) {
        int mark = at + 1;
        bool down = false;
        if (mark < end && "+-".IndexOf(text[mark]) >= 0) {
          down = text[mark] == "-"[0];
          ++mark;
        }
        int power = 0;
        int first = mark;
        while (mark < end && table.IndexOf(text[mark]) >= 0 && table.IndexOf(text[mark]) < 10) {
          if (power < 100000) power = power * 10 + table.IndexOf(text[mark]);
          ++mark;
        }
        if (mark > first) {
          exponent += down ? -power : power;
          at = mark;
        }
      }
      if (count == 0 || at < end && mode < 3) {
        if (mode < 2) throw new global::System.FormatException();
        return double.NaN;
      }
      int precision = mode == 1 ? 24 : 53;
      int least = mode == 1 ? -126 : -1022;
      double result = 0.0;
      if (radix == 10 && digits.Length > 0 && exponent + digits.Length > (mode == 1 ? 40 : 310)) {
        result = double.PositiveInfinity;
      } else if (overflow) {
        result = double.PositiveInfinity;
      } else if (radix != 10 || digits.Length > 0 && exponent + digits.Length >= (mode == 1 ? -46 : -325)) {
        for (int step = 0; step < digits.Length + (exponent < 0 ? -exponent : exponent); ++step) {
          bool digitStep = step < digits.Length;
          uint[] target = digitStep || exponent > 0 ? number : divisor;
          ulong carry = digitStep ? (ulong)table.IndexOf(digits[step]) : 0UL;
          for (int index = 0; index < 140; ++index) {
            ulong product = (ulong)target[index] * 10UL + carry;
            target[index] = (uint)product;
            carry = product >> 32;
          }
        }
        int numberBits = 0;
        int divisorBits = 0;
        for (int index = 139; index >= 0 && (numberBits == 0 || divisorBits == 0); --index) {
          for (int bit = 31; bit >= 0; --bit) {
            if (numberBits == 0 && (number[index] >> bit & 1) != 0) numberBits = index * 32 + bit + 1;
            if (divisorBits == 0 && (divisor[index] >> bit & 1) != 0) divisorBits = index * 32 + bit + 1;
          }
        }
        if (numberBits > 0) {
          int shift = precision + 3 - (numberBits - divisorBits);
          for (int which = 0; which < 2; ++which) {
            uint[] target = which == 0 ? number : divisor;
            int by = which == 0 ? (shift > 0 ? shift : 0) : (shift < 0 ? -shift : 0) + 60;
            for (int index = 139; index >= 0; --index) {
              int from = index - by / 32;
              ulong high = from >= 0 ? (ulong)target[from] << (by % 32) : 0UL;
              ulong low = from >= 1 && by % 32 != 0 ? (ulong)target[from - 1] >> (32 - by % 32) : 0UL;
              target[index] = (uint)(high | low);
            }
          }
          ulong quotient = 0;
          for (int bit = 60; bit >= 0; --bit) {
            int order = 0;
            for (int index = 139; index >= 0 && order == 0; --index) {
              if (number[index] != divisor[index]) order = number[index] > divisor[index] ? 1 : -1;
            }
            if (order >= 0) {
              long borrow = 0;
              for (int index = 0; index < 140; ++index) {
                long difference = (long)number[index] - divisor[index] - borrow;
                number[index] = (uint)difference;
                borrow = difference < 0 ? 1 : 0;
              }
              quotient |= 1UL << bit;
            }
            for (int index = 0; index < 140; ++index) {
              divisor[index] = (divisor[index] >> 1) | (index < 139 ? divisor[index + 1] << 31 : 0U);
            }
          }
          for (int index = 0; index < 140; ++index) sticky |= number[index] != 0;
          int quotientBits = 0;
          while (quotientBits < 64 && quotient >> quotientBits != 0) ++quotientBits;
          int leading = quotientBits - 1 - shift;
          int keep = leading < least ? precision - (least - leading) : precision;
          if (keep >= 0) {
            int drop = quotientBits - keep;
            ulong mantissa = quotient >> drop;
            ulong rest = quotient & ((1UL << drop) - 1);
            ulong half = 1UL << (drop - 1);
            if (rest > half || rest == half && (sticky || (mantissa & 1) != 0)) ++mantissa;
            int power2 = drop - shift;
            if (mantissa >= 1UL << precision) {
              mantissa >>= 1;
              ++power2;
            }
            int biased = mantissa < 1UL << (precision - 1) ? 0 : power2 + precision - 1 + (mode == 1 ? 127 : 1023);
            if (biased >= (mode == 1 ? 255 : 2047)) {
              result = double.PositiveInfinity;
            } else if (mode == 1) {
              int single = biased == 0 ? (int)mantissa : biased << 23 | (int)(mantissa & 0x7FFFFF);
              result = global::System.BitConverter.ToSingle(global::System.BitConverter.GetBytes(single), 0);
            } else {
              result = global::System.BitConverter.Int64BitsToDouble(biased == 0 ? (long)mantissa : (long)biased << 52 | (long)(mantissa & 0xFFFFFFFFFFFFFUL));
            }
          }
        }
      }
      return negative ? -result : result;
    }
    """));

  /// <summary>
  /// An integer in a radix, padded with zeros: the 64 bits as unsigned, or as signed with a minus
  /// sign in front of the magnitude.
  /// </summary>
  public static CSharpHelper FormatRadix { get; } = new("FormatRadix", "string", "long value, bool signed, int radix, int minDigits, bool upper", Lines("""
    unchecked {
      bool negative = signed && value < 0;
      ulong magnitude = negative ? (ulong)-value : (ulong)value;
      string digits = "";
      do {
        digits = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(magnitude % (ulong)radix)] + digits;
        magnitude /= (ulong)radix;
      } while (magnitude != 0);
      digits = digits.PadLeft(minDigits, "0"[0]);
      if (upper) digits = digits.ToUpper(global::System.Globalization.CultureInfo.InvariantCulture);
      return negative ? "-" + digits : digits;
    }
    """));

  private static string[] Lines(string text) => text.Split('\n');

}
