using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.Cpp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Cpp;

[TestFixture]
public sealed class CppFrontendTests {

  private static object? Call(IrModule module, string function, params object[] args)
    => IrInterpreter.Call(module, function, args);

  private static IrModule ImportVerified(string source, string fileName = "sample.cpp", CppImportOptions? options = null) {
    var result = CppFrontend.Import(fileName, source, options);
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    Assert.That(result.Module, Is.Not.Null);
    var verify = IrVerifier.Verify(result.Module!);
    Assert.That(verify, Is.Empty, () => string.Join("\n", verify));
    return result.Module!;
  }

  [Test]
  public void Options_And_ModuleNaming_Work() {
    const string src = "int answer() { return 42; }";
    foreach (var std in new string?[] { null, "cpp17", "cpp20", "cpp23" }) {
      var mod = ImportVerified(src, "123-my.mod.cpp", std is null ? null : new CppImportOptions { Standard = std });
      Assert.That(mod.Name, Is.EqualTo("_123_my_mod"));
      Assert.That(Call(mod, "answer"), Is.EqualTo(42));
    }

    var emptyStem = ImportVerified(src, ".cpp");
    Assert.That(emptyStem.Name, Is.EqualTo("CppModule"));
    var crlfMod = ImportVerified("#include \\\r\n  <cmath>\r\nint crlf() { return 7; }\r\n", "crlf.cpp");
    Assert.That(Call(crlfMod, "crlf"), Is.EqualTo(7));
    Assert.Throws<ArgumentNullException>(() => CppFrontend.Import(null!, src));
    Assert.Throws<ArgumentNullException>(() => CppFrontend.Import("a.cpp", null!));
  }

  [Test]
  public void Preprocessor_Comments_Literals_And_AllPrimitiveTypes() {
    const string src = """
      #pragma once
      #include <cstdint>
      #include <cstddef>
      #include \
        <cmath>
      // single-line comment
      /* multi-line
         comment */
      using namespace std;
      using u32 = std::uint32_t;

      int32_t literals_test() {
        int dec = 1'000;
        int hex = 0x1A;
        int bin = 0b1010;
        int oct = 012;
        uint32_t u = 10u;
        int64_t l = 20ll;
        uint64_t ul = 30ull;
        uint64_t bigHex = 0x1'0000'0000;
        uint32_t highHex = 0x80000000;
        int64_t bigDec = 3000000000;
        uint64_t hugeDec = 10000000000000000000ULL;
        char c1 = 'A';
        char c2 = '\n';
        char c3 = '\r';
        char c4 = '\t';
        char c5 = '\\';
        char c6 = '\'';
        char c7 = '\"';
        char c8 = '\0';
        char c9 = '\x42';
        return dec + hex + bin + oct + (int)u + (int)l + (int)ul
          + (bigHex > 0 ? 1 : 0) + (highHex > 0u ? 1 : 0) + (bigDec > 0 ? 1 : 0) + (hugeDec > 0ull ? 1 : 0)
          + (int)c1 + (int)c2 + (int)c3 + (int)c4 + (int)c5 + (int)c6 + (int)c7 + (int)c8 + (int)c9;
      }

      double all_types(
        bool b, char c, signed char sc, unsigned char uc,
        short s, unsigned short us, int i, unsigned int ui,
        long l, unsigned long ul, long long ll, unsigned long long ull,
        float f, double d, std::size_t sz, std::int8_t i8, std::uint8_t u8,
        std::int16_t i16, std::uint16_t u16, std::int32_t i32, u32 uu32,
        std::int64_t i64, std::uint64_t u64) {
        double sum = 0.0;
        if (b) sum += 1.0;
        sum += (double)c + (double)sc + (double)uc + (double)s + (double)us;
        sum += (double)i + (double)ui + (double)l + (double)ul + (double)ll + (double)ull;
        sum += (double)f + d + (double)sz + (double)i8 + (double)u8;
        sum += (double)i16 + (double)u16 + (double)i32 + (double)uu32 + (double)i64 + (double)u64;
        return sum;
      }

      uint64_t sizes_sum() {
        return sizeof(bool) + sizeof(int8_t) + sizeof(int16_t) + sizeof(int32_t)
          + sizeof(int64_t) + sizeof(float) + sizeof(double) + sizeof(std::string);
      }
      """;
    var mod = ImportVerified(src);
    var litExpected = 1000 + 26 + 10 + 10 + 10 + 20 + 30 + 4 + 65 + 10 + 13 + 9 + 92 + 39 + 34 + 0 + 66;
    Assert.That(Call(mod, "literals_test"), Is.EqualTo(litExpected));
    Assert.That(Call(mod, "sizes_sum"), Is.EqualTo(1UL + 1 + 2 + 4 + 8 + 4 + 8 + 8));
    var sum = (double)Call(mod, "all_types",
      true, (sbyte)1, (sbyte)2, (byte)3,
      (short)4, (ushort)5, 6, 7u,
      8L, 9UL, 10L, 11UL,
      1.5f, 2.5, 12UL, (sbyte)1, (byte)2,
      (short)3, (ushort)4, 5, 6u,
      7L, 8UL)!;
    Assert.That(sum, Is.EqualTo(119.0));
  }

  [Test]
  public void Operators_Assignments_Casts_And_ControlFlow() {
    const string src = """
      #include <cstdint>

      int arithmetic_and_bitwise(int a, int b) {
        int x = +a;
        x = -x;
        x = ~x;
        bool flag = !(a == b) && (a != 0 || b != 0);
        int r = flag ? (a + b) : (a - b);
        r += 3;
        r -= 1;
        r *= 2;
        r /= 2;
        r %= 100;
        r &= 0xFF;
        r |= 0x01;
        r ^= 0x02;
        r <<= 1;
        r >>= 1;
        int post = r++;
        int pre = ++r;
        int dpost = r--;
        int dpre = --r;
        uint32_t neg_u = -static_cast<uint32_t>(5u);
        return x + post + pre + dpost + dpre + (neg_u > 0u ? 1 : 0);
      }

      int loops_and_switch(int n) {
        int total = 0;
        int i = 0;
        while (i < n) {
          ++i;
          if (i == 2) continue;
          if (i == 7) break;
          total += i;
        }
        int j = 0;
        do {
          total += j;
          j++;
        } while (j < 3);
        for (int k = 0; k < 4; ++k) {
          switch (k) {
            case 0:
            case 1:
              total += 10;
              break;
            case 2:
              total += 20;
              break;
            default:
              total += 30;
              break;
          }
        }
        int assigned = 0;
        int v = (assigned = 5) + assigned;
        return total + v;
      }

      int empty_for_and_if_constexpr(int x) {
        int acc = 0;
        if constexpr (true) {
          for (;;) {
            acc += x;
            if (acc >= 10) break;
          }
        }
        return acc;
      }
      """;
    var mod = ImportVerified(src);
    Assert.That(Call(mod, "arithmetic_and_bitwise", 10, 4), Is.TypeOf<int>());
    // while: i=1,3,4,5,6 => 19; do-while: j=0,1,2 => 3; switch: 10+10+20+30 = 70; assigned: 10 => 102
    Assert.That(Call(mod, "loops_and_switch", 10), Is.EqualTo(102));
    Assert.That(Call(mod, "empty_for_and_if_constexpr", 4), Is.EqualTo(12));
  }

  [Test]
  public void Structs_And_Enums_Work() {
    const string src = """
      #include <cstdint>

      enum class Color : uint8_t {
        Red = 0,
        Green = 1,
        Blue = 1 << 2
      };

      enum Flags {
        None = 0,
        A = 1,
        B = 2,
        AB = A | B
      };

      struct Point {
        int x = 0;
        int y = 0;
        bool operator==(const Point&) const = default;
      };

      int struct_ops(Point p) {
        Point q{1, 2};
        Point r(3, 4);
        Point z{};
        Point partial{7};
        q.x += 5;
        int eq = (p == q) ? 1 : 0;
        int neq = (p != r) ? 1 : 0;
        return q.x + q.y + r.x + r.y + z.x + partial.x + partial.y + eq + neq + (int)sizeof(Point);
      }

      int enum_ops(Color c) {
        int code = static_cast<int>(c);
        Color back = static_cast<Color>(code);
        int extra = (c == back && c != Color::Blue) ? 10 : 0;
        switch (c) {
          case Color::Red: return code + extra + 1 + static_cast<int>(AB);
          case Color::Green: return code + extra + 2;
          default: return code + extra + 3;
        }
      }
      """;
    var mod = ImportVerified(src);
    // p = {6, 2} == q {6, 2} => eq=1, neq=1; q=6+2=8, r=3+4=7, z=0, partial=7+0=7, sizeof(Point)=8 => 32
    Assert.That(Call(mod, "struct_ops", [new object[] { 6, 2 }]), Is.EqualTo(32));
    // Red=0: code=0, extra=10, +1 + AB(3) = 14
    Assert.That(Call(mod, "enum_ops", (byte)0), Is.EqualTo(14));
    // Green=1: code=1, extra=10, +2 = 13
    Assert.That(Call(mod, "enum_ops", (byte)1), Is.EqualTo(13));
    // Blue=4: code=4, extra=0, +3 = 7
    Assert.That(Call(mod, "enum_ops", (byte)4), Is.EqualTo(7));
  }

  [Test]
  public void Optionals_And_Exceptions_Work() {
    const string src = """
      #include <optional>
      #include <stdexcept>
      #include <string>

      std::optional<int> safe_div(int a, int b) {
        if (b == 0) return std::nullopt;
        return a / b;
      }

      int use_optional(int a, int b) {
        std::optional<int> res = safe_div(a, b);
        std::optional<int> empty{};
        std::optional<int> one{5};
        auto made = std::make_optional(3);
        int deref = *made;
        int check = (empty == std::nullopt && std::nullopt != one) ? 1 : 0;
        int val = res ? res.value() : res.value_or(-1);
        auto cond = (b == 0) ? std::nullopt : std::optional<int>{a};
        auto cond2 = (b != 0) ? std::optional<int>{a} : std::nullopt;
        return val + (res.has_value() ? 100 : 0) + deref + check + cond.value_or(0) + cond2.value_or(0);
      }

      void may_throw(int mode) {
        if (mode == 1) throw std::out_of_range("oob");
        if (mode == 2) throw std::invalid_argument("bad arg");
        if (mode == 3) throw std::overflow_error("ovf");
        if (mode == 4) throw std::domain_error("dom");
        if (mode == 5) throw std::runtime_error("run");
        if (mode == 6) throw std::exception();
      }

      int catch_modes(int mode) {
        try {
          try {
            may_throw(mode);
            return 0;
          } catch (const std::out_of_range& e) {
            return e.what() == "oob" ? 10 : -1;
          } catch (const std::invalid_argument& e) {
            return 20;
          } catch (const std::overflow_error& e) {
            return 30;
          } catch (const std::domain_error& e) {
            throw;
          }
        } catch (const std::exception& e) {
          if (e.what() == "run") return 50;
          return 40;
        } catch (...) {
          return 99;
        }
      }
      """;
    var mod = ImportVerified(src);
    Assert.That(Call(mod, "safe_div", 10, 0), Is.Null);
    Assert.That(Call(mod, "safe_div", 10, 2), Is.EqualTo(5));
    // a=10, b=2: val=5, has_value=100, deref=3, check=1, cond=10, cond2=10 => 129
    Assert.That(Call(mod, "use_optional", 10, 2), Is.EqualTo(129));
    // a=10, b=0: val=-1, has_value=0, deref=3, check=1, cond=0, cond2=0 => 3
    Assert.That(Call(mod, "use_optional", 10, 0), Is.EqualTo(3));
    Assert.That(Call(mod, "catch_modes", 0), Is.EqualTo(0));
    Assert.That(Call(mod, "catch_modes", 1), Is.EqualTo(10));
    Assert.That(Call(mod, "catch_modes", 2), Is.EqualTo(20));
    Assert.That(Call(mod, "catch_modes", 3), Is.EqualTo(30));
    Assert.That(Call(mod, "catch_modes", 4), Is.EqualTo(40));
    Assert.That(Call(mod, "catch_modes", 5), Is.EqualTo(50));
    Assert.That(Call(mod, "catch_modes", 6), Is.EqualTo(40));
  }

  [Test]
  public void Containers_Spans_Arrays_Vectors_Strings_And_MemoryIntrinsics() {
    const string src = """
      #include <array>
      #include <cstdint>
      #include <cstring>
      #include <algorithm>
      #include <span>
      #include <string>
      #include <vector>

      int span_and_pointer_ops(std::span<const int> ro, std::span<int> rw, const int* p, int* out_p) {
        int s = 0;
        if (!ro.empty()) {
          s += ro.front() + ro.back();
          auto sub = ro.subspan(1);
          auto sub2 = ro.subspan(0, 2);
          auto fst = ro.first(1);
          auto lst = ro.last(1);
          s += (int)sub.size() + (int)sub2.size() + fst[0] + lst[0];
        }
        for (auto x : ro) s += x;
        rw[0] = *p + 1;
        out_p[0] = rw[0];
        return s + (int)rw.size();
      }

      std::vector<int> vector_ops(const std::vector<int>& input) {
        std::vector<int> v;
        std::vector<int> init = {10, 20};
        for (auto x : input) v.push_back(x * 2);
        v.push_back(99);
        v.pop_back();
        v[0] += init.front() + init.back() + (init.empty() ? 1 : 0);
        int check = v.at(0) + v.back() + (int)v.size();
        init.clear();
        v.push_back(check);
        return v;
      }

      int array_and_memory_ops() {
        std::array<int, 4> a = {1, 2};
        std::array<int, 4> b;
        b.fill(5);
        std::copy(a.begin(), a.end(), b.begin());
        std::fill(a.begin(), a.end(), 7);
        int raw[4] = {10, 20, 30, 40};
        int dst[4];
        std::memcpy(dst, raw, 4 * sizeof(int));
        std::array<uint8_t, 4> b1 = {1, 2, 3, 4};
        std::array<uint8_t, 4> b2;
        std::memcpy(b2.data(), b1.data(), 4);
        std::span<uint8_t> sp1 = b1;
        std::span<uint8_t> sp2 = b2;
        std::ranges::fill(sp1, 9);
        std::ranges::copy(sp1, sp2);
        std::memcpy(sp2.data(), sp1.data(), 2);
        return a.front() + a.back() + b.at(0) + b.at(1) + (int)a.size() + (a.empty() ? 1 : 0)
          + dst[0] + dst[3] + (int)b2[0] + (int)b2[3];
      }

      int string_ops(const std::string& a, std::string b) {
        std::string combined = a + " " "world";
        combined += b;
        int score = (int)combined.size() + (int)combined.length();
        if (!combined.empty()) score += (int)combined[0] + (int)combined.at(1);
        if (combined.starts_with("hi")) score += 100;
        if (combined.ends_with("!")) score += 200;
        if (combined.contains("world")) score += 300;
        if (combined.find("world") != std::string::npos) score += 400;
        if (combined.find("world", 2) != std::string::npos) score += 500;
        if (combined.substr(0, 2) == "hi" && combined.substr(3) != "x") score += 600;
        if (a < b && a <= b && b > a && b >= a && a.compare(b) < 0) score += 700;
        std::string empty_s;
        if (empty_s.empty() && a != nullptr) score += 800;
        return score;
      }
      """;
    var mod = ImportVerified(src);
    var rw = new[] { 0, 0 };
    var outP = new[] { 0 };
    var spanRes = (int)Call(mod, "span_and_pointer_ops", new[] { 10, 20, 30 }, rw, new[] { 41 }, outP)!;
    Assert.That(rw[0], Is.EqualTo(42));
    Assert.That(outP[0], Is.EqualTo(42));
    Assert.That(spanRes, Is.GreaterThan(0));

    var vecOut = (int[])Call(mod, "vector_ops", [new[] { 1, 2 }])!;
    // v starts as [2, 4], v[0] += 30 => [32, 4], check = 32 + 4 + 2 = 38 => [32, 4, 38]
    Assert.That(vecOut, Is.EqualTo(new[] { 32, 4, 38 }));

    // a=[7,7,7,7], b=[1,2,0,0], dst=[10,20,30,40], b2=[9,9,9,9]
    // 7+7+1+2+4+0+10+40+9+9 = 89
    Assert.That(Call(mod, "array_and_memory_ops"), Is.EqualTo(89));

    var strScore = (int)Call(mod, "string_ops", "hi", "!")!;
    Assert.That(strScore, Is.GreaterThan(3000));
  }

  [Test]
  public void StdIntrinsics_BitCast_Templates_Namespaces_And_Globals() {
    const string src = """
      #include <bit>
      #include <cmath>
      #include <cstdint>
      #include <algorithm>
      #include <array>
      #include <string>

      constexpr int kBase = 10;
      constexpr double kScale = 2.0;
      constexpr bool kFlag = true;
      static const std::string kTag = "ok";
      constexpr std::array<uint32_t, 4> kTable = {1u, 2u, 3u, 4u};
      static const std::string kWords[] = {"a", "b"};
      static int gCounter = 5;

      namespace {
        int hidden_helper(int x) { return x + kBase; }
      }

      namespace math {
        int add(int a, int b) { return a + b; }
      }

      namespace other {
        int add(int a, int b) { return a + b + 1; }
      }

      template<typename T>
      T generic_add(T a, T b) {
        return a + b;
      }

      template<typename T>
      T generic_first(std::span<const T> s) {
        return s[0];
      }

      int forward_declared(int x);

      int forward_declared(int x) { return x * 2; }

      double math_intrinsics(double d, float f) {
        double r = std::abs(-d) + std::fabs(-3) + std::sqrt(d) + std::floor(d) + std::ceil(d) + std::trunc(d) + std::round(d);
        r += std::copysign(d, -1.0) + (double)std::copysign(f, -1.0f);
        r += std::min(d, 10.0) + std::max<double>(d, 1.0) + std::clamp(d, 0.0, 5.0);
        r += std::clamp<int>(10, 0, 5);
        int flags = (std::isnan(d) ? 1 : 0) + (std::isinf(d) ? 2 : 0) + (std::isfinite(d) ? 4 : 0) + (std::isnormal(d) ? 8 : 0);
        return r + flags + (kFlag ? kScale : 0.0);
      }

      uint64_t bit_intrinsics(uint32_t x, float f, double d) {
        uint32_t r1 = std::rotl(x, 4);
        uint32_t r2 = std::rotr(r1, 4);
        int lz = std::countl_zero(x);
        int tz = std::countr_zero(x);
        int pc = std::popcount(x);
        uint32_t bs = std::byteswap(x);
        uint32_t fb = std::bit_cast<uint32_t>(f);
        float bf = std::bit_cast<float>(fb);
        int32_t fi = std::bit_cast<int32_t>(bf);
        float bf2 = std::bit_cast<float>(fi);
        uint64_t db = std::bit_cast<uint64_t>(d);
        double bd = std::bit_cast<double>(db);
        int64_t di = std::bit_cast<int64_t>(bd);
        double bd2 = std::bit_cast<double>(di);
        int32_t same = std::bit_cast<int32_t>(fi);
        uint32_t i2u = std::bit_cast<uint32_t>(same);
        uint32_t rc = reinterpret_cast<uint32_t>(bf2);
        return (uint64_t)r2 + (uint64_t)lz + (uint64_t)tz + (uint64_t)pc + (uint64_t)bs
          + (uint64_t)i2u + (uint64_t)rc + (bd2 > 0.0 ? 1ull : 0ull);
      }

      int run_all(std::span<const int> sp) {
        gCounter += 1;
        int a = generic_add(3, 4);
        double b = generic_add<double>(1.5, 2.5);
        int c = generic_first(sp);
        int d = math::add(1, 2) + other::add(1, 2) + hidden_helper(0) + forward_declared(2);
        return a + (int)b + c + d + gCounter + (int)kTable[2] + (kTag == "ok" && kWords[0] == "a" ? 1 : 0);
      }
      """;
    var mod = ImportVerified(src);
    Assert.That(Call(mod, "math_intrinsics", 4.0, 2.0f), Is.TypeOf<double>());
    Assert.That(Call(mod, "bit_intrinsics", 0x12345678u, 1.5f, 2.5), Is.TypeOf<ulong>());
    // gCounter=6, a=7, b=4, c=99, d=3+4+10+4=21, kTable[2]=3, tag=1 => 141
    Assert.That(Call(mod, "run_all", [new[] { 99 }]), Is.EqualTo(141));
  }

  [Test]
  public void Transpile_Cpp_To_JavaScript_Succeeds() {
    const string src = """
      #include <cstdint>

      uint32_t adler32(std::span<const uint8_t> data) {
        uint32_t a = 1u;
        uint32_t b = 0u;
        for (std::size_t i = 0; i < data.size(); ++i) {
          a = (a + static_cast<uint32_t>(data[i])) % 65521u;
          b = (b + a) % 65521u;
        }
        return (b << 16) | a;
      }
      """;
    var mod = ImportVerified(src, "adler.cpp");
    var profile = JavaScriptProfile.Default;
    var legalized = JavaScriptEmitter.Legalize(mod, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var files = JavaScriptEmitter.Emit(legalized, profile);
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Content, Does.Contain("export function adler32"));
    var bytes = System.Text.Encoding.ASCII.GetBytes("Wikipedia");
    Assert.That(Call(mod, "adler32", [bytes]), Is.EqualTo(0x11E60398u));
  }

  [Test]
  public void Coverage_Globals_Conversions_RangeFor_And_ConstExpressions() {
    const string src = """
      #include <array>
      #include <cstdint>
      #include <optional>
      #include <span>
      #include <string>
      #include <vector>

      ;
      namespace ns {
        ;
        constexpr int kNegInt = -7;
        constexpr double kNegDbl = -2.5;
        constexpr double kIntAsDbl = 3;
        constexpr char kChar = 'Z';
        static int gDefaultInt;
        static int gDefaultArr[3];
        static std::string gDefaultStr;
      }

      enum ExprEnum {
        E0 = 'A',
        E1 = +1 + 2 - 1,
        E2 = -(-2) * 3,
        E3 = (1 << 2) | 1
      };

      enum ExprEnum2 {
        F0 = ~0,
        F1 = E1 + 1
      };

      constexpr ExprEnum kEnumConst = E1;
      const int kDynArr[2] = { 1, 2 + 3 };
      const std::string kDynStrs[1] = { ns::gDefaultStr };

      struct HasDefaults {
        public:
        ;
        int a;
        double b;
        bool c;
        std::array<int, 2> arr;
      };

      struct Simple {
        int v;
      };

      struct Nested {
        Simple s;
        bool b;
        double d;
      };

      int take_span(std::span<const int> sp) {
        int s = 0;
        for (int x : sp.subspan(0)) s += x;
        return s;
      }

      template<typename T>
      T unwrap_opt(std::optional<T> o) {
        return o.value_or(0);
      }

      template<typename T>
      T read_ptr(const T* p) {
        return p[0];
      }

      int edge_conversions(bool flag, int x, uint32_t u, float f, std::string str, const std::vector<int>& vecParam) {
        int fromBool = flag;
        double dblFromBool = flag;
        bool fromInt = x;
        bool fromStr = str;
        bool bEq = (flag == fromInt) && (flag != fromStr);
        int a = +flag;
        int i32PlusU32 = static_cast<int>(x + u);
        float fPlusI = f + x;
        std::vector<int> emptyVec{};
        std::vector<int> fromParam = vecParam;
        int listSum = 0;
        for (auto item : fromParam) listSum += item;
        for (int item : emptyVec) listSum += item;
        int fromVecSpan = take_span(fromParam);

        Simple sDef;
        std::optional<int> oDef;
        int scalarBrace{};
        int scalarBrace1{4};
        Nested nested{Simple{1}};
        int uninit;
        uninit = 5;

        std::optional<double> optD = 3;
        int optVal = unwrap_opt(std::optional<int>{9});
        int ptrVal = read_ptr(vecParam.data());

        ExprEnum ee = static_cast<ExprEnum>(1);
        int eeBack = static_cast<int>(ee);
        int eeProm = +ee;
        int arrIdx = vecParam[ee];

        int acc = 0;
        for (acc = 0; acc < 2; acc += 1) { }

        double fCasted = static_cast<double>(kEnumConst);
        int gRead = ns::kNegInt + static_cast<int>(ns::kNegDbl) + static_cast<int>(ns::kIntAsDbl)
          + static_cast<int>(ns::kChar) + ns::gDefaultInt + ns::gDefaultArr[0] + static_cast<int>(ns::gDefaultStr.size())
          + kDynArr[1] + static_cast<int>(kDynStrs[0].size());

        std::span<const uint32_t> u32sp = std::array<uint32_t, 2>{1u, 2u};
        auto reint = reinterpret_cast<std::span<const uint8_t>>(u32sp);

        return fromBool + (int)dblFromBool + (bEq ? 1 : 0) + a + i32PlusU32 + (int)fPlusI
          + listSum + fromVecSpan + sDef.v + oDef.value_or(1) + scalarBrace + scalarBrace1
          + nested.s.v + uninit + (int)optD.value() + optVal + ptrVal + eeBack + eeProm
          + arrIdx + acc + (int)fCasted + gRead + (int)reint.size() + static_cast<int>(E0) + static_cast<int>(F0) + static_cast<int>(F1);
      }
      """;
    var mod = ImportVerified(src);
    var res = Call(mod, "edge_conversions", true, 2, 3u, 1.5f, "hi", new[] { 10, 20 });
    Assert.That(res, Is.TypeOf<int>());
  }

  [Test]
  public void Coverage_RemainingSyntaxAndLoweringBranches() {
    const string src = """
      # include <cmath>
      #include <array>
      #include <cstdint>
      #include <optional>
      #include <span>
      #include <string>
      #include <vector>

      namespace outer {
        namespace inner {
          inline int deep_fn(int const x) { return x + 1; }
        }
      }

      struct Pair {
        int a;
        int b;
      };

      int gBrace{7};
      const int gDynConst = gBrace + 1;

      void early_void(int x) {
        if (x < 0) return;
      }

      int struct_span_and_ptr(std::span<Pair> ps, const Pair* pp, const std::string* sp) {
        return ps[0].a + pp[0].b + (int)sp[0].size();
      }

      double branches_test(int x, std::span<int> rw) {
        early_void(x);
        const auto y = x;
        const int z = 3;
        int dup = 1;
        {
          int dup = 2;
          rw[0] = dup;
        }
        double sci = 1.5e+2 + 2.5e-1 + 3.0L;
        uint64_t big = 10000000000000000000;
        std::array<int, 3> arr = {1, 2, 3, };
        int arrowVal = Pair{4, 5}->a;
        bool cmp = (x <= 10) && (x >= 0) && (x > -1) && (x < 20);
        double tern1 = cmp ? 1 : 2.5;
        auto tern2 = cmp ? std::optional<int>{1} : 2;
        auto tern3 = cmp ? 2 : std::optional<int>{1};
        double mathInt = std::sqrt(9) + (std::isnan(0) ? 1.0 : 0.0) + std::min<int>(1, 2) + std::max(1, 2);
        auto d1 = rw.data();
        auto d2 = arr.data();
        int gRead = ::gBrace + gDynConst + outer::inner::deep_fn(y + z + dup);
        std::vector<std::array<int, 2>> nestedAngle;
        return sci + (big > 0ull ? 1.0 : 0.0) + arrowVal + tern1 + tern2.value() + tern3.value()
          + mathInt + d1[0] + d2[0] + gRead + (int)nestedAngle.size();
      }
      """;
    var mod = ImportVerified(src);
    var rw = new[] { 0 };
    Assert.That(Call(mod, "branches_test", 5, rw), Is.TypeOf<double>());
    Assert.That(rw[0], Is.EqualTo(2));
  }

}
