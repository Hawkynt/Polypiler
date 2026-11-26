using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Cpp;

/// <summary>
/// Minimal, warning-free, UB-free static C++ helper definitions emitted into the anonymous namespace
/// of generated <c>.cpp</c> files only when referenced.
/// </summary>
internal static class CppHelpers {

  public const string CheckedAdd = "ply_checked_add";
  public const string CheckedSub = "ply_checked_sub";
  public const string CheckedMul = "ply_checked_mul";
  public const string CheckedNeg = "ply_checked_neg";
  public const string IntDiv = "ply_div";
  public const string IntRem = "ply_rem";
  public const string ArithShiftRight = "ply_ashr";
  public const string CheckedConv = "ply_checked_conv";
  public const string FloatToInt = "ply_float_to_int";
  public const string FloatMin = "ply_fmin";
  public const string FloatMax = "ply_fmax";
  public const string IntAbs = "ply_abs";
  public const string FloatSign = "ply_fsign";
  public const string IntSign = "ply_isign";
  public const string BitCast17 = "ply_bit_cast";
  public const string Rotl17 = "ply_rotl";
  public const string Rotr17 = "ply_rotr";
  public const string Clz17 = "ply_clz";
  public const string Ctz17 = "ply_ctz";
  public const string PopCount17 = "ply_popcount";
  public const string ByteSwap = "ply_byteswap";
  public const string LoadEndian = "ply_load_endian";
  public const string StoreEndian = "ply_store_endian";
  public const string NewArray = "ply_new_array";
  public const string ArrayAt = "ply_at";
  public const string ArrayCopy = "ply_array_copy";
  public const string Unwrap = "ply_unwrap";
  public const string NonNull = "ply_nonnull";
  public const string Cast = "ply_cast";
  public const string ScopeGuard = "ply_finally";
  public const string StrCodeUnitAt = "ply_str_at";
  public const string StrFromCodeUnits = "ply_str_from_units";
  public const string StrToCodeUnits = "ply_str_to_units";
  public const string StrRepeat = "ply_str_repeat";
  public const string StrSubstring = "ply_str_sub";
  public const string StrCompare = "ply_str_cmp";
  public const string StrIndexOf = "ply_str_index_of";
  public const string StrLastIndexOf = "ply_str_last_index_of";
  public const string StrStartsWith = "ply_str_starts_with";
  public const string StrEndsWith = "ply_str_ends_with";
  public const string StrReplaceAll = "ply_str_replace_all";
  public const string StrReplaceFirst = "ply_str_replace_first";
  public const string StrSplit = "ply_str_split";
  public const string StrJoin = "ply_str_join";
  public const string StrTrim = "ply_str_trim";
  public const string StrPad = "ply_str_pad";
  public const string ListRemoveLast = "ply_list_pop";
  public const string ListInsert = "ply_list_insert";
  public const string ListRemoveAt = "ply_list_remove_at";
  public const string ListIndexOf = "ply_list_index_of";
  public const string GenericCompare = "ply_compare";
  public const string FormatFloat = "ply_format_float";
  public const string ToUtf16 = "ply_to_u16";
  public const string PlyTask = "PlyTask";
  public const string PlySpan = "PlySpan";
  public const string PlyMap = "PlyMap";
  public const string PlySet = "PlySet";
  public const string WhenAll = "ply_when_all_list";
  public const string NonNullFn = "ply_nonnull_fn";
  public const string ViewOf = "ply_view_of";
  public const string ViewSlice = "ply_view_slice";
  public const string ViewCopy = "ply_view_copy";
  public const string ViewTryCopy = "ply_view_try_copy";
  public const string ViewEqual = "ply_view_equal";
  public const string ViewReinterpret = "ply_view_reinterpret";
  public const string Pin = "ply_pin";
  public const string PtrLoad = "ply_ptr_load";
  public const string PtrStore = "ply_ptr_store";
  public const string PtrAdd = "ply_ptr_add";
  public const string Pext = "ply_pext";
  public const string Pdep = "ply_pdep";
  public const string ListRemoveLastRef = "ply_list_pop_ref";

  public sealed record HelperSpec(string Code, string[] Headers);

  public static IReadOnlyList<string> Order { get; } = [
    PlyTask, PlySpan, PlyMap, PlySet,
    CheckedAdd, CheckedSub, CheckedMul, CheckedNeg, IntDiv, IntRem, ArithShiftRight,
    CheckedConv, FloatToInt, FloatMin, FloatMax, IntAbs, FloatSign, IntSign,
    BitCast17, Rotl17, Rotr17, Clz17, Ctz17, PopCount17, ByteSwap, Pext, Pdep,
    LoadEndian, StoreEndian, NewArray, ArrayAt, ArrayCopy,
    Unwrap, NonNull, NonNullFn, Cast, ScopeGuard, WhenAll,
    ViewOf, ViewSlice, ViewCopy, ViewTryCopy, ViewEqual, ViewReinterpret,
    Pin, PtrLoad, PtrStore, PtrAdd,
    StrCodeUnitAt, StrFromCodeUnits, StrToCodeUnits, StrRepeat, StrSubstring,
    StrCompare, StrIndexOf, StrLastIndexOf, StrStartsWith, StrEndsWith,
    StrReplaceAll, StrReplaceFirst, StrSplit, StrJoin, StrTrim, StrPad,
    ListRemoveLast, ListRemoveLastRef, ListInsert, ListRemoveAt, ListIndexOf,
    GenericCompare, FormatFloat, ToUtf16
  ];

  private static readonly Dictionary<string, HelperSpec> _specs = new(StringComparer.Ordinal) {
    [CheckedAdd] = new("""
      template <typename T>
      T ply_checked_add(T a, T b) {
        if constexpr (std::is_signed_v<T>) {
          if ((b > 0 && a > std::numeric_limits<T>::max() - b) ||
              (b < 0 && a < std::numeric_limits<T>::min() - b))
            throw std::overflow_error("integer overflow");
          using U = std::make_unsigned_t<T>;
          return static_cast<T>(static_cast<U>(a) + static_cast<U>(b));
        } else {
          if (a > std::numeric_limits<T>::max() - b)
            throw std::overflow_error("integer overflow");
          return static_cast<T>(a + b);
        }
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [CheckedSub] = new("""
      template <typename T>
      T ply_checked_sub(T a, T b) {
        if constexpr (std::is_signed_v<T>) {
          if ((b < 0 && a > std::numeric_limits<T>::max() + b) ||
              (b > 0 && a < std::numeric_limits<T>::min() + b))
            throw std::overflow_error("integer overflow");
          using U = std::make_unsigned_t<T>;
          return static_cast<T>(static_cast<U>(a) - static_cast<U>(b));
        } else {
          if (a < b)
            throw std::overflow_error("integer overflow");
          return static_cast<T>(a - b);
        }
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [CheckedMul] = new("""
      template <typename T>
      T ply_checked_mul(T a, T b) {
        if constexpr (std::is_signed_v<T>) {
          constexpr T hi = std::numeric_limits<T>::max();
          constexpr T lo = std::numeric_limits<T>::min();
          if (a > 0) {
            if (b > 0 ? (a > hi / b) : (b < lo / a))
              throw std::overflow_error("integer overflow");
          } else if (a < 0) {
            if (b > 0 ? (a < lo / b) : (b < 0 && a < hi / b))
              throw std::overflow_error("integer overflow");
          }
          using U = std::make_unsigned_t<T>;
          return static_cast<T>(static_cast<U>(a) * static_cast<U>(b));
        } else {
          if (b != 0 && a > std::numeric_limits<T>::max() / b)
            throw std::overflow_error("integer overflow");
          return static_cast<T>(a * b);
        }
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [CheckedNeg] = new("""
      template <typename T>
      T ply_checked_neg(T a) {
        if (a == std::numeric_limits<T>::min())
          throw std::overflow_error("integer overflow");
        return static_cast<T>(-a);
      }
      """, ["<limits>", "<stdexcept>"]),

    [IntDiv] = new("""
      template <typename T>
      T ply_div(T a, T b) {
        if (b == 0)
          throw std::domain_error("divide by zero");
        if constexpr (std::is_signed_v<T>) {
          if (a == std::numeric_limits<T>::min() && b == static_cast<T>(-1))
            throw std::overflow_error("integer overflow");
        }
        return static_cast<T>(a / b);
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [IntRem] = new("""
      template <typename T>
      T ply_rem(T a, T b) {
        if (b == 0)
          throw std::domain_error("divide by zero");
        if constexpr (std::is_signed_v<T>) {
          if (a == std::numeric_limits<T>::min() && b == static_cast<T>(-1))
            throw std::overflow_error("integer overflow");
        }
        return static_cast<T>(a % b);
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [ArithShiftRight] = new("""
      template <typename T>
      T ply_ashr(T a, std::int32_t b) {
        using U = std::make_unsigned_t<T>;
        constexpr unsigned bits = static_cast<unsigned>(sizeof(T) * 8);
        const unsigned s = static_cast<unsigned>(b) & (bits - 1u);
        if (s == 0u)
          return a;
        const U u = static_cast<U>(a);
        return a < 0
          ? static_cast<T>((u >> s) | (static_cast<U>(~U{0}) << (bits - s)))
          : static_cast<T>(u >> s);
      }
      """, ["<cstdint>", "<type_traits>"]),

    [CheckedConv] = new("""
      template <typename Dst, typename Src>
      Dst ply_checked_conv(Src value) {
        if constexpr (std::is_signed_v<Src> == std::is_signed_v<Dst>) {
          if (value < std::numeric_limits<Dst>::min() || value > std::numeric_limits<Dst>::max())
            throw std::overflow_error("integer overflow");
        } else if constexpr (std::is_signed_v<Src>) {
          if (value < 0)
            throw std::overflow_error("integer overflow");
          using USrc = std::make_unsigned_t<Src>;
          if (static_cast<USrc>(value) > std::numeric_limits<Dst>::max())
            throw std::overflow_error("integer overflow");
        } else {
          using UDst = std::make_unsigned_t<Dst>;
          if (value > static_cast<UDst>(std::numeric_limits<Dst>::max()))
            throw std::overflow_error("integer overflow");
        }
        return static_cast<Dst>(value);
      }
      """, ["<limits>", "<stdexcept>", "<type_traits>"]),

    [FloatToInt] = new("""
      template <typename Dst, bool Trap, typename Src>
      Dst ply_float_to_int(Src value) {
        if (std::isnan(value)) {
          if constexpr (Trap)
            throw std::overflow_error("integer overflow");
          return Dst{0};
        }
        const double t = std::trunc(static_cast<double>(value));
        constexpr int bits = static_cast<int>(sizeof(Dst) * 8);
        constexpr double lo = std::is_signed_v<Dst> ? -static_cast<double>(1ULL << (bits - 1)) : 0.0;
        constexpr double hi_excl = std::is_signed_v<Dst>
          ? static_cast<double>(1ULL << (bits - 1))
          : (bits == 64 ? 18446744073709551616.0 : static_cast<double>(1ULL << bits));
        if (t < lo) {
          if constexpr (Trap)
            throw std::overflow_error("integer overflow");
          return std::numeric_limits<Dst>::min();
        }
        if (t >= hi_excl) {
          if constexpr (Trap)
            throw std::overflow_error("integer overflow");
          return std::numeric_limits<Dst>::max();
        }
        return static_cast<Dst>(t);
      }
      """, ["<cmath>", "<limits>", "<stdexcept>", "<type_traits>"]),

    [FloatMin] = new("""
      template <typename T>
      T ply_fmin(T a, T b) {
        if (std::isnan(a)) return a;
        if (std::isnan(b)) return b;
        if (a < b) return a;
        if (b < a) return b;
        return std::signbit(a) ? a : b;
      }
      """, ["<cmath>"]),

    [FloatMax] = new("""
      template <typename T>
      T ply_fmax(T a, T b) {
        if (std::isnan(a)) return a;
        if (std::isnan(b)) return b;
        if (a > b) return a;
        if (b > a) return b;
        return std::signbit(b) ? a : b;
      }
      """, ["<cmath>"]),

    [IntAbs] = new("""
      template <typename T>
      T ply_abs(T a) {
        if (a == std::numeric_limits<T>::min())
          throw std::overflow_error("integer overflow");
        return a < 0 ? static_cast<T>(-a) : a;
      }
      """, ["<limits>", "<stdexcept>"]),

    [FloatSign] = new("""
      template <typename T>
      std::int32_t ply_fsign(T a) {
        if (std::isnan(a))
          throw std::domain_error("NaN has no sign");
        return a < T{0} ? -1 : (a > T{0} ? 1 : 0);
      }
      """, ["<cmath>", "<cstdint>", "<stdexcept>"]),

    [IntSign] = new("""
      template <typename T>
      std::int32_t ply_isign(T a) {
        if constexpr (std::is_signed_v<T>)
          return a < 0 ? -1 : (a > 0 ? 1 : 0);
        else
          return a > 0 ? 1 : 0;
      }
      """, ["<cstdint>", "<type_traits>"]),

    [BitCast17] = new("""
      template <typename Dst, typename Src>
      Dst ply_bit_cast(Src value) {
        static_assert(sizeof(Dst) == sizeof(Src));
        Dst result{};
        std::memcpy(&result, &value, sizeof(Dst));
        return result;
      }
      """, ["<cstring>"]),

    [Rotl17] = new("""
      template <typename T>
      T ply_rotl(T value, std::int32_t count) {
        using U = std::make_unsigned_t<T>;
        constexpr unsigned bits = static_cast<unsigned>(sizeof(T) * 8);
        const unsigned s = static_cast<unsigned>(count) & (bits - 1u);
        const U u = static_cast<U>(value);
        return s == 0u ? value : static_cast<T>((u << s) | (u >> (bits - s)));
      }
      """, ["<cstdint>", "<type_traits>"]),

    [Rotr17] = new("""
      template <typename T>
      T ply_rotr(T value, std::int32_t count) {
        using U = std::make_unsigned_t<T>;
        constexpr unsigned bits = static_cast<unsigned>(sizeof(T) * 8);
        const unsigned s = static_cast<unsigned>(count) & (bits - 1u);
        const U u = static_cast<U>(value);
        return s == 0u ? value : static_cast<T>((u >> s) | (u << (bits - s)));
      }
      """, ["<cstdint>", "<type_traits>"]),

    [Clz17] = new("""
      template <typename T>
      std::int32_t ply_clz(T value) {
        using U = std::make_unsigned_t<T>;
        constexpr int bits = static_cast<int>(sizeof(T) * 8);
        U u = static_cast<U>(value);
        if (u == 0)
          return bits;
        int count = 0;
        const U msb = U{1} << (bits - 1);
        while ((u & msb) == 0) {
          u <<= 1;
          ++count;
        }
        return count;
      }
      """, ["<cstdint>", "<type_traits>"]),

    [Ctz17] = new("""
      template <typename T>
      std::int32_t ply_ctz(T value) {
        using U = std::make_unsigned_t<T>;
        constexpr int bits = static_cast<int>(sizeof(T) * 8);
        U u = static_cast<U>(value);
        if (u == 0)
          return bits;
        int count = 0;
        while ((u & U{1}) == 0) {
          u >>= 1;
          ++count;
        }
        return count;
      }
      """, ["<cstdint>", "<type_traits>"]),

    [PopCount17] = new("""
      template <typename T>
      std::int32_t ply_popcount(T value) {
        using U = std::make_unsigned_t<T>;
        U u = static_cast<U>(value);
        int count = 0;
        while (u != 0) {
          u &= static_cast<U>(u - 1);
          ++count;
        }
        return count;
      }
      """, ["<cstdint>", "<type_traits>"]),

    [ByteSwap] = new("""
      template <typename T>
      T ply_byteswap(T value) {
        using U = std::make_unsigned_t<T>;
        U u = static_cast<U>(value);
        U out = 0;
        for (std::size_t i = 0; i < sizeof(T); ++i)
          out = static_cast<U>((out << 8) | ((u >> (i * 8)) & U{0xFF}));
        return static_cast<T>(out);
      }
      """, ["<cstddef>", "<type_traits>"]),

    [LoadEndian] = new("""
      template <typename T, bool Little, typename Buf>
      T ply_load_endian(const Buf& buf, std::int32_t offset) {
        if (offset < 0 || static_cast<std::size_t>(offset) + sizeof(T) > buf.size())
          throw std::out_of_range("index out of range");
        using U = std::conditional_t<sizeof(T) == 2, std::uint16_t,
                  std::conditional_t<sizeof(T) == 4, std::uint32_t, std::uint64_t>>;
        U bits = 0;
        for (std::size_t i = 0; i < sizeof(T); ++i) {
          const std::size_t idx = static_cast<std::size_t>(offset) + (Little ? i : (sizeof(T) - 1 - i));
          bits = static_cast<U>(bits | (static_cast<U>(buf[idx]) << (i * 8)));
        }
        if constexpr (std::is_floating_point_v<T>) {
          T result{};
          std::memcpy(&result, &bits, sizeof(T));
          return result;
        } else {
          return static_cast<T>(bits);
        }
      }
      """, ["<cstddef>", "<cstdint>", "<cstring>", "<stdexcept>", "<type_traits>"]),

    [StoreEndian] = new("""
      template <typename T, bool Little, typename Buf>
      void ply_store_endian(Buf&& buf, std::int32_t offset, T value) {
        if (offset < 0 || static_cast<std::size_t>(offset) + sizeof(T) > buf.size())
          throw std::out_of_range("index out of range");
        using U = std::conditional_t<sizeof(T) == 2, std::uint16_t,
                  std::conditional_t<sizeof(T) == 4, std::uint32_t, std::uint64_t>>;
        U bits = 0;
        if constexpr (std::is_floating_point_v<T>)
          std::memcpy(&bits, &value, sizeof(T));
        else
          bits = static_cast<U>(value);
        for (std::size_t i = 0; i < sizeof(T); ++i) {
          const std::size_t idx = static_cast<std::size_t>(offset) + (Little ? i : (sizeof(T) - 1 - i));
          buf[idx] = static_cast<std::uint8_t>((bits >> (i * 8)) & U{0xFF});
        }
      }
      """, ["<cstddef>", "<cstdint>", "<cstring>", "<stdexcept>", "<type_traits>"]),

    [NewArray] = new("""
      template <typename T>
      std::vector<T> ply_new_array(std::int32_t length) {
        if (length < 0)
          throw std::overflow_error("negative array length");
        return std::vector<T>(static_cast<std::size_t>(length));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<vector>"]),

    [ArrayAt] = new("""
      template <typename C>
      auto ply_at(C& arr, std::int32_t index) -> decltype(arr[static_cast<std::size_t>(0)]) {
        if (index < 0 || static_cast<std::size_t>(index) >= arr.size())
          throw std::out_of_range("index out of range");
        return arr[static_cast<std::size_t>(index)];
      }
      template <typename C>
      auto ply_at(const C& arr, std::int32_t index) -> decltype(arr[static_cast<std::size_t>(0)]) {
        if (index < 0 || static_cast<std::size_t>(index) >= arr.size())
          throw std::out_of_range("index out of range");
        return arr[static_cast<std::size_t>(index)];
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>"]),

    [ArrayCopy] = new("""
      template <typename T>
      void ply_array_copy(const std::vector<T>& src, std::int32_t src_idx, std::vector<T>& dst, std::int32_t dst_idx, std::int32_t length) {
        if (length < 0 || src_idx < 0 || dst_idx < 0 ||
            static_cast<std::size_t>(src_idx) + static_cast<std::size_t>(length) > src.size() ||
            static_cast<std::size_t>(dst_idx) + static_cast<std::size_t>(length) > dst.size())
          throw std::out_of_range("index out of range");
        if (length == 0)
          return;
        const auto s0 = static_cast<std::size_t>(src_idx);
        const auto d0 = static_cast<std::size_t>(dst_idx);
        const auto n = static_cast<std::size_t>(length);
        if (&src == &dst && d0 > s0)
          std::copy_backward(src.begin() + static_cast<std::ptrdiff_t>(s0), src.begin() + static_cast<std::ptrdiff_t>(s0 + n), dst.begin() + static_cast<std::ptrdiff_t>(d0 + n));
        else
          std::copy(src.begin() + static_cast<std::ptrdiff_t>(s0), src.begin() + static_cast<std::ptrdiff_t>(s0 + n), dst.begin() + static_cast<std::ptrdiff_t>(d0));
      }
      """, ["<algorithm>", "<cstddef>", "<cstdint>", "<stdexcept>", "<vector>"]),

    [Unwrap] = new("""
      template <typename T>
      T ply_unwrap(const std::optional<T>& opt) {
        if (!opt.has_value())
          throw std::logic_error("missing value");
        return *opt;
      }
      """, ["<optional>", "<stdexcept>"]),

    [NonNull] = new("""
      template <typename T>
      const std::shared_ptr<T>& ply_nonnull(const std::shared_ptr<T>& ptr) {
        if (!ptr)
          throw std::runtime_error("null reference");
        return ptr;
      }
      """, ["<memory>", "<stdexcept>"]),

    [Cast] = new("""
      template <typename Dst, typename Src>
      std::shared_ptr<Dst> ply_cast(const std::shared_ptr<Src>& ptr) {
        if (!ptr)
          return nullptr;
        auto result = std::dynamic_pointer_cast<Dst>(ptr);
        if (!result)
          throw std::bad_cast();
        return result;
      }
      """, ["<memory>", "<typeinfo>"]),

    [ScopeGuard] = new("""
      template <typename F>
      struct PlyScopeGuard {
        F fn;
        explicit PlyScopeGuard(F f) : fn(std::move(f)) {}
        ~PlyScopeGuard() noexcept(false) { fn(); }
        PlyScopeGuard(const PlyScopeGuard&) = delete;
        PlyScopeGuard& operator=(const PlyScopeGuard&) = delete;
      };
      template <typename F>
      PlyScopeGuard<F> ply_finally(F f) { return PlyScopeGuard<F>(std::move(f)); }
      """, ["<utility>"]),

    [StrCodeUnitAt] = new("""
      std::uint16_t ply_str_at(const std::string& s, std::int32_t index) {
        if (index < 0 || static_cast<std::size_t>(index) >= s.size())
          throw std::out_of_range("index out of range");
        return static_cast<std::uint16_t>(static_cast<unsigned char>(s[static_cast<std::size_t>(index)]));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>"]),

    [StrFromCodeUnits] = new("""
      std::string ply_str_from_units(const std::vector<std::uint16_t>& units, std::int32_t start, std::int32_t length) {
        if (start < 0 || length < 0 || static_cast<std::size_t>(start) + static_cast<std::size_t>(length) > units.size())
          throw std::out_of_range("index out of range");
        std::string out;
        out.reserve(static_cast<std::size_t>(length));
        for (std::int32_t i = 0; i < length; ++i)
          out.push_back(static_cast<char>(units[static_cast<std::size_t>(start + i)] & 0xFFu));
        return out;
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>", "<vector>"]),

    [StrToCodeUnits] = new("""
      std::vector<std::uint16_t> ply_str_to_units(const std::string& s) {
        std::vector<std::uint16_t> out;
        out.reserve(s.size());
        for (char c : s)
          out.push_back(static_cast<std::uint16_t>(static_cast<unsigned char>(c)));
        return out;
      }
      """, ["<cstdint>", "<string>", "<vector>"]),

    [StrRepeat] = new("""
      std::string ply_str_repeat(const std::string& s, std::int32_t count) {
        if (count < 0)
          throw std::overflow_error("negative repeat count");
        std::string out;
        out.reserve(s.size() * static_cast<std::size_t>(count));
        for (std::int32_t i = 0; i < count; ++i)
          out.append(s);
        return out;
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>"]),

    [StrSubstring] = new("""
      std::string ply_str_sub(const std::string& s, std::int32_t start, std::int32_t length) {
        if (start < 0 || length < 0 || static_cast<std::size_t>(start) + static_cast<std::size_t>(length) > s.size())
          throw std::out_of_range("index out of range");
        return s.substr(static_cast<std::size_t>(start), static_cast<std::size_t>(length));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>"]),

    [StrCompare] = new("""
      std::int32_t ply_str_cmp(const std::string& a, const std::string& b) {
        const int c = a.compare(b);
        return c < 0 ? -1 : (c > 0 ? 1 : 0);
      }
      """, ["<cstdint>", "<string>"]),

    [StrIndexOf] = new("""
      std::int32_t ply_str_index_of(const std::string& s, const std::string& needle, std::int32_t from) {
        if (from < 0 || static_cast<std::size_t>(from) > s.size())
          throw std::out_of_range("index out of range");
        const auto pos = s.find(needle, static_cast<std::size_t>(from));
        return pos == std::string::npos ? -1 : static_cast<std::int32_t>(pos);
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>"]),

    [StrLastIndexOf] = new("""
      std::int32_t ply_str_last_index_of(const std::string& s, const std::string& needle, std::int32_t from) {
        if (from < 0 || static_cast<std::size_t>(from) > s.size())
          throw std::out_of_range("index out of range");
        if (needle.size() > s.size())
          return -1;
        const auto pos = s.rfind(needle, static_cast<std::size_t>(from));
        return pos == std::string::npos ? -1 : static_cast<std::int32_t>(pos);
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<string>"]),

    [StrStartsWith] = new("""
      bool ply_str_starts_with(const std::string& s, const std::string& prefix) {
        return s.size() >= prefix.size() && s.compare(0, prefix.size(), prefix) == 0;
      }
      """, ["<string>"]),

    [StrEndsWith] = new("""
      bool ply_str_ends_with(const std::string& s, const std::string& suffix) {
        return s.size() >= suffix.size() && s.compare(s.size() - suffix.size(), suffix.size(), suffix) == 0;
      }
      """, ["<string>"]),

    [StrReplaceAll] = new("""
      std::string ply_str_replace_all(const std::string& s, const std::string& needle, const std::string& replacement) {
        if (needle.empty())
          throw std::invalid_argument("empty needle");
        std::string out;
        std::size_t pos = 0;
        while (true) {
          const auto found = s.find(needle, pos);
          if (found == std::string::npos) {
            out.append(s, pos, std::string::npos);
            break;
          }
          out.append(s, pos, found - pos);
          out.append(replacement);
          pos = found + needle.size();
        }
        return out;
      }
      """, ["<cstddef>", "<stdexcept>", "<string>"]),

    [StrReplaceFirst] = new("""
      std::string ply_str_replace_first(const std::string& s, const std::string& needle, const std::string& replacement) {
        const auto found = s.find(needle);
        if (found == std::string::npos)
          return s;
        std::string out = s;
        out.replace(found, needle.size(), replacement);
        return out;
      }
      """, ["<string>"]),

    [StrSplit] = new("""
      std::vector<std::string> ply_str_split(const std::string& s, const std::string& sep, std::int32_t limit) {
        std::vector<std::string> parts;
        if (sep.empty()) {
          for (char c : s)
            parts.emplace_back(1, c);
        } else {
          std::size_t pos = 0;
          while (true) {
            const auto found = s.find(sep, pos);
            if (found == std::string::npos) {
              parts.push_back(s.substr(pos));
              break;
            }
            parts.push_back(s.substr(pos, found - pos));
            pos = found + sep.size();
          }
        }
        if (limit >= 0 && static_cast<std::size_t>(limit) < parts.size())
          parts.resize(static_cast<std::size_t>(limit));
        return parts;
      }
      """, ["<cstddef>", "<cstdint>", "<string>", "<vector>"]),

    [StrJoin] = new("""
      std::string ply_str_join(const std::string& sep, const std::vector<std::string>& parts) {
        std::string out;
        for (std::size_t i = 0; i < parts.size(); ++i) {
          if (i > 0)
            out.append(sep);
          out.append(parts[i]);
        }
        return out;
      }
      """, ["<cstddef>", "<string>", "<vector>"]),

    [StrTrim] = new("""
      std::string ply_str_trim(const std::string& s, bool trim_start, bool trim_end) {
        auto is_ws = [](unsigned char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'; };
        std::size_t first = 0;
        std::size_t last = s.size();
        while (trim_start && first < last && is_ws(static_cast<unsigned char>(s[first])))
          ++first;
        while (trim_end && last > first && is_ws(static_cast<unsigned char>(s[last - 1])))
          --last;
        return s.substr(first, last - first);
      }
      """, ["<cstddef>", "<string>"]),

    [StrPad] = new("""
      std::string ply_str_pad(const std::string& s, std::int32_t length, const std::string& filler, bool at_start) {
        if (length <= static_cast<std::int32_t>(s.size()) || filler.empty())
          return s;
        const auto need = static_cast<std::size_t>(length) - s.size();
        std::string pad;
        pad.reserve(need);
        while (pad.size() < need)
          pad.append(filler);
        pad.resize(need);
        return at_start ? (pad + s) : (s + pad);
      }
      """, ["<cstddef>", "<cstdint>", "<string>"]),

    [ListRemoveLast] = new("""
      template <typename T>
      std::optional<T> ply_list_pop(std::vector<T>& list) {
        if (list.empty())
          return std::nullopt;
        T value = std::move(list.back());
        list.pop_back();
        return value;
      }
      """, ["<optional>", "<utility>", "<vector>"]),

    [ListInsert] = new("""
      template <typename T>
      void ply_list_insert(std::vector<T>& list, std::int32_t index, T value) {
        if (index < 0 || static_cast<std::size_t>(index) > list.size())
          throw std::out_of_range("index out of range");
        list.insert(list.begin() + static_cast<std::ptrdiff_t>(index), std::move(value));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<utility>", "<vector>"]),

    [ListRemoveAt] = new("""
      template <typename T>
      void ply_list_remove_at(std::vector<T>& list, std::int32_t index) {
        if (index < 0 || static_cast<std::size_t>(index) >= list.size())
          throw std::out_of_range("index out of range");
        list.erase(list.begin() + static_cast<std::ptrdiff_t>(index));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<vector>"]),

    [ListIndexOf] = new("""
      template <typename T>
      std::int32_t ply_list_index_of(const std::vector<T>& list, const T& value) {
        for (std::size_t i = 0; i < list.size(); ++i) {
          if constexpr (std::is_floating_point_v<T>) {
            if (list[i] == value || (std::isnan(list[i]) && std::isnan(value)))
              return static_cast<std::int32_t>(i);
          } else {
            if (list[i] == value)
              return static_cast<std::int32_t>(i);
          }
        }
        return -1;
      }
      """, ["<cmath>", "<cstddef>", "<cstdint>", "<type_traits>", "<vector>"]),

    [GenericCompare] = new("""
      template <typename T>
      std::int32_t ply_compare(const T& a, const T& b) {
        if constexpr (std::is_floating_point_v<T>) {
          if (a < b) return -1;
          if (a > b) return 1;
          if (a == b) return 0;
          return std::isnan(a) ? (std::isnan(b) ? 0 : -1) : 1;
        } else if constexpr (std::is_same_v<T, std::string>) {
          const int c = a.compare(b);
          return c < 0 ? -1 : (c > 0 ? 1 : 0);
        } else if constexpr (std::is_same_v<T, std::optional<std::string>>) {
          if (!a.has_value()) return b.has_value() ? -1 : 0;
          if (!b.has_value()) return 1;
          const int c = a->compare(*b);
          return c < 0 ? -1 : (c > 0 ? 1 : 0);
        } else if constexpr (std::is_integral_v<T> && !std::is_same_v<T, bool> && sizeof(T) < 4) {
          return static_cast<std::int32_t>(a) - static_cast<std::int32_t>(b);
        } else {
          return a < b ? -1 : (a > b ? 1 : 0);
        }
      }
      """, ["<cmath>", "<cstdint>", "<optional>", "<string>", "<type_traits>"]),

    [FormatFloat] = new("""
      template <typename T, bool DotNet>
      std::string ply_format_float(T value) {
        if (std::isnan(value))
          return "NaN";
        if (std::isinf(value))
          return value < T{0} ? "-Infinity" : "Infinity";
        if (value == T{0})
          return (DotNet && std::signbit(value)) ? "-0" : "0";
        std::ostringstream oss;
        oss.imbue(std::locale::classic());
        oss << std::setprecision(std::is_same_v<T, float> ? 7 : 15) << value;
        std::string out = oss.str();
        if constexpr (DotNet) {
          for (char& ch : out)
            if (ch == 'e')
              ch = 'E';
        }
        return out;
      }
      """, ["<cmath>", "<iomanip>", "<locale>", "<sstream>", "<string>", "<type_traits>"]),

    [ToUtf16] = new("""
      std::u16string ply_to_u16(const std::string& s) {
        std::u16string out;
        out.reserve(s.size());
        std::size_t i = 0;
        while (i < s.size()) {
          const auto b0 = static_cast<unsigned char>(s[i++]);
          std::uint32_t cp = b0;
          if ((b0 & 0xF8u) == 0xF0u && i + 2 < s.size()
              && (static_cast<unsigned char>(s[i]) & 0xC0u) == 0x80u
              && (static_cast<unsigned char>(s[i + 1]) & 0xC0u) == 0x80u
              && (static_cast<unsigned char>(s[i + 2]) & 0xC0u) == 0x80u) {
            cp = ((b0 & 0x07u) << 18) | ((static_cast<unsigned char>(s[i]) & 0x3Fu) << 12) | ((static_cast<unsigned char>(s[i + 1]) & 0x3Fu) << 6) | (static_cast<unsigned char>(s[i + 2]) & 0x3Fu);
            i += 3;
          } else if ((b0 & 0xF0u) == 0xE0u && i + 1 < s.size()
              && (static_cast<unsigned char>(s[i]) & 0xC0u) == 0x80u
              && (static_cast<unsigned char>(s[i + 1]) & 0xC0u) == 0x80u) {
            cp = ((b0 & 0x0Fu) << 12) | ((static_cast<unsigned char>(s[i]) & 0x3Fu) << 6) | (static_cast<unsigned char>(s[i + 1]) & 0x3Fu);
            i += 2;
          } else if ((b0 & 0xE0u) == 0xC0u && i < s.size()
              && (static_cast<unsigned char>(s[i]) & 0xC0u) == 0x80u) {
            cp = ((b0 & 0x1Fu) << 6) | (static_cast<unsigned char>(s[i]) & 0x3Fu);
            i += 1;
          }
          if (cp > 0xFFFFu) {
            cp -= 0x10000u;
            out.push_back(static_cast<char16_t>(0xD800u | (cp >> 10)));
            out.push_back(static_cast<char16_t>(0xDC00u | (cp & 0x3FFu)));
          } else {
            out.push_back(static_cast<char16_t>(cp));
          }
        }
        return out;
      }
      """, ["<cstddef>", "<cstdint>", "<string>"]),

    [PlyTask] = new("""
      template <typename T>
      struct PlyTask {
        std::optional<T> value_{};
        std::exception_ptr error_{};

        static PlyTask from_result(T value) {
          PlyTask task;
          task.value_ = std::move(value);
          return task;
        }

        static PlyTask from_exception(std::exception_ptr err) {
          PlyTask task;
          task.error_ = std::move(err);
          return task;
        }

        T await() const {
          if (this->error_)
            std::rethrow_exception(this->error_);
          return *this->value_;
        }
      };

      template <>
      struct PlyTask<void> {
        std::exception_ptr error_{};

        static PlyTask completed() { return PlyTask{}; }

        static PlyTask from_exception(std::exception_ptr err) {
          PlyTask task;
          task.error_ = std::move(err);
          return task;
        }

        void await() const {
          if (this->error_)
            std::rethrow_exception(this->error_);
        }
      };
      """, ["<exception>", "<optional>", "<utility>"]),

    [WhenAll] = new("""
      template <typename T>
      PlyTask<std::vector<T>> ply_when_all_list(std::initializer_list<PlyTask<T>> tasks) {
        std::vector<T> out;
        out.reserve(tasks.size());
        for (const auto& t : tasks) {
          if (t.error_)
            return PlyTask<std::vector<T>>::from_exception(t.error_);
          out.push_back(*t.value_);
        }
        return PlyTask<std::vector<T>>::from_result(std::move(out));
      }

      inline PlyTask<void> ply_when_all_void(std::initializer_list<PlyTask<void>> tasks) {
        for (const auto& t : tasks) {
          if (t.error_)
            return PlyTask<void>::from_exception(t.error_);
        }
        return PlyTask<void>::completed();
      }
      """, ["<initializer_list>", "<utility>", "<vector>"]),

    [PlySpan] = new("""
      template <typename T>
      struct PlySpan {
        T* data_ = nullptr;
        std::size_t size_ = 0;

        PlySpan() = default;
        PlySpan(T* data, std::size_t size) : data_(data), size_(size) {}
        template <typename U, typename = std::enable_if_t<std::is_convertible_v<U (*)[], T (*)[]>>>
        PlySpan(std::vector<U>& vec) : data_(vec.data()), size_(vec.size()) {}
        template <typename U, typename = std::enable_if_t<std::is_convertible_v<const U (*)[], T (*)[]>>>
        PlySpan(const std::vector<U>& vec) : data_(vec.data()), size_(vec.size()) {}
        template <typename U, typename = std::enable_if_t<std::is_convertible_v<U (*)[], T (*)[]>>>
        PlySpan(const PlySpan<U>& other) : data_(other.data()), size_(other.size()) {}

        T* data() const { return this->data_; }
        std::size_t size() const { return this->size_; }
        bool empty() const { return this->size_ == 0; }
        T* begin() const { return this->data_; }
        T* end() const { return this->data_ + this->size_; }
        T& operator[](std::size_t index) const { return this->data_[index]; }
        PlySpan subspan(std::size_t offset, std::size_t count) const { return PlySpan(this->data_ + offset, count); }
      };
      """, ["<cstddef>", "<type_traits>", "<vector>"]),

    [PlyMap] = new("""
      template <typename K, typename V, bool SlotReuse>
      struct PlyMap {
        struct Entry {
          bool occupied = false;
          K key{};
          V value{};
        };

        std::vector<Entry> entries{};
        std::vector<std::size_t> free_list{};
        std::size_t count_ = 0;

        static bool key_eq(const K& a, const K& b) {
          if constexpr (std::is_floating_point_v<K>)
            return (a == b) || (std::isnan(a) && std::isnan(b));
          else
            return a == b;
        }

        std::size_t find_slot(const K& key) const {
          for (std::size_t i = 0; i < this->entries.size(); ++i)
            if (this->entries[i].occupied && key_eq(this->entries[i].key, key))
              return i;
          return static_cast<std::size_t>(-1);
        }

        std::int32_t count() const { return static_cast<std::int32_t>(this->count_); }

        bool contains_key(const K& key) const { return this->find_slot(key) != static_cast<std::size_t>(-1); }

        V& get(const K& key) {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            throw std::out_of_range("key not found");
          return this->entries[slot].value;
        }

        const V& get(const K& key) const {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            throw std::out_of_range("key not found");
          return this->entries[slot].value;
        }

        std::optional<V> try_get_opt(const K& key) const {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            return std::nullopt;
          return this->entries[slot].value;
        }

        V try_get_ref(const K& key) const {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            return V{};
          return this->entries[slot].value;
        }

        void insert_new(const K& key, V value) {
          if constexpr (SlotReuse) {
            if (!this->free_list.empty()) {
              const auto slot = this->free_list.back();
              this->free_list.pop_back();
              this->entries[slot] = Entry{true, key, std::move(value)};
              ++this->count_;
              return;
            }
          }
          this->entries.push_back(Entry{true, key, std::move(value)});
          ++this->count_;
        }

        void put(const K& key, V value) {
          const auto slot = this->find_slot(key);
          if (slot != static_cast<std::size_t>(-1)) {
            this->entries[slot].value = std::move(value);
            return;
          }
          this->insert_new(key, std::move(value));
        }

        void add(const K& key, V value) {
          if (this->find_slot(key) != static_cast<std::size_t>(-1))
            throw std::invalid_argument("duplicate key");
          this->insert_new(key, std::move(value));
        }

        bool try_add(const K& key, V value) {
          if (this->find_slot(key) != static_cast<std::size_t>(-1))
            return false;
          this->insert_new(key, std::move(value));
          return true;
        }

        bool remove(const K& key) {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            return false;
          this->entries[slot].occupied = false;
          this->entries[slot].key = K{};
          this->entries[slot].value = V{};
          if constexpr (SlotReuse)
            this->free_list.push_back(slot);
          --this->count_;
          return true;
        }

        void clear() {
          this->entries.clear();
          this->free_list.clear();
          this->count_ = 0;
        }
      };
      """, ["<cmath>", "<cstddef>", "<cstdint>", "<optional>", "<stdexcept>", "<type_traits>", "<utility>", "<vector>"]),

    [PlySet] = new("""
      template <typename K, bool SlotReuse>
      struct PlySet {
        struct Entry {
          bool occupied = false;
          K key{};
        };

        std::vector<Entry> entries{};
        std::vector<std::size_t> free_list{};
        std::size_t count_ = 0;

        static bool key_eq(const K& a, const K& b) {
          if constexpr (std::is_floating_point_v<K>)
            return (a == b) || (std::isnan(a) && std::isnan(b));
          else
            return a == b;
        }

        std::size_t find_slot(const K& key) const {
          for (std::size_t i = 0; i < this->entries.size(); ++i)
            if (this->entries[i].occupied && key_eq(this->entries[i].key, key))
              return i;
          return static_cast<std::size_t>(-1);
        }

        std::int32_t count() const { return static_cast<std::int32_t>(this->count_); }

        bool contains(const K& key) const { return this->find_slot(key) != static_cast<std::size_t>(-1); }

        bool add(const K& key) {
          if (this->find_slot(key) != static_cast<std::size_t>(-1))
            return false;
          if constexpr (SlotReuse) {
            if (!this->free_list.empty()) {
              const auto slot = this->free_list.back();
              this->free_list.pop_back();
              this->entries[slot] = Entry{true, key};
              ++this->count_;
              return true;
            }
          }
          this->entries.push_back(Entry{true, key});
          ++this->count_;
          return true;
        }

        bool remove(const K& key) {
          const auto slot = this->find_slot(key);
          if (slot == static_cast<std::size_t>(-1))
            return false;
          this->entries[slot].occupied = false;
          this->entries[slot].key = K{};
          if constexpr (SlotReuse)
            this->free_list.push_back(slot);
          --this->count_;
          return true;
        }

        void clear() {
          this->entries.clear();
          this->free_list.clear();
          this->count_ = 0;
        }
      };
      """, ["<cmath>", "<cstddef>", "<cstdint>", "<type_traits>", "<vector>"]),

    [NonNullFn] = new("""
      template <typename F>
      const F& ply_nonnull_fn(const F& fn) {
        if (!fn)
          throw std::runtime_error("null reference");
        return fn;
      }
      """, ["<stdexcept>"]),

    [ViewOf] = new("""
      template <typename SpanT, typename Src>
      SpanT ply_view_of(Src&& src) {
        using Elem = std::remove_pointer_t<decltype(SpanT{}.data())>;
        return SpanT(const_cast<Elem*>(src.data()), src.size());
      }
      template <typename SpanT, typename Src>
      SpanT ply_view_of(Src&& src, std::int32_t start) {
        if (start < 0 || static_cast<std::size_t>(start) > src.size())
          throw std::out_of_range("slice out of range");
        using Elem = std::remove_pointer_t<decltype(SpanT{}.data())>;
        const auto s = static_cast<std::size_t>(start);
        return SpanT(const_cast<Elem*>(src.data()) + s, src.size() - s);
      }
      template <typename SpanT, typename Src>
      SpanT ply_view_of(Src&& src, std::int32_t start, std::int32_t length) {
        if (start < 0 || length < 0 || static_cast<std::size_t>(start) + static_cast<std::size_t>(length) > src.size())
          throw std::out_of_range("slice out of range");
        using Elem = std::remove_pointer_t<decltype(SpanT{}.data())>;
        return SpanT(const_cast<Elem*>(src.data()) + static_cast<std::size_t>(start), static_cast<std::size_t>(length));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>", "<type_traits>"]),

    [ViewSlice] = new("""
      template <typename SpanT>
      SpanT ply_view_slice(SpanT view, std::int32_t start) {
        if (start < 0 || static_cast<std::size_t>(start) > view.size())
          throw std::out_of_range("slice out of range");
        const auto s = static_cast<std::size_t>(start);
        return view.subspan(s, view.size() - s);
      }
      template <typename SpanT>
      SpanT ply_view_slice(SpanT view, std::int32_t start, std::int32_t length) {
        if (start < 0 || length < 0 || static_cast<std::size_t>(start) + static_cast<std::size_t>(length) > view.size())
          throw std::out_of_range("slice out of range");
        return view.subspan(static_cast<std::size_t>(start), static_cast<std::size_t>(length));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>"]),

    [ViewCopy] = new("""
      template <typename SrcSpan, typename DstSpan>
      void ply_view_copy(SrcSpan src, DstSpan dst) {
        if (src.size() > dst.size())
          throw std::invalid_argument("destination too short");
        if (src.size() > 0)
          std::memmove(dst.data(), src.data(), src.size() * sizeof(*src.data()));
      }
      """, ["<cstring>", "<stdexcept>"]),

    [ViewTryCopy] = new("""
      template <typename SrcSpan, typename DstSpan>
      bool ply_view_try_copy(SrcSpan src, DstSpan dst) {
        if (src.size() > dst.size())
          return false;
        if (src.size() > 0)
          std::memmove(dst.data(), src.data(), src.size() * sizeof(*src.data()));
        return true;
      }
      """, ["<cstring>"]),

    [ViewEqual] = new("""
      template <typename LeftSpan, typename RightSpan>
      bool ply_view_equal(LeftSpan a, RightSpan b) {
        if (a.size() != b.size())
          return false;
        for (std::size_t i = 0; i < a.size(); ++i) {
          using E = std::decay_t<decltype(a[i])>;
          if constexpr (std::is_floating_point_v<E>) {
            if (!(a[i] == b[i] || (std::isnan(a[i]) && std::isnan(b[i]))))
              return false;
          } else {
            if (a[i] != b[i])
              return false;
          }
        }
        return true;
      }
      """, ["<cmath>", "<cstddef>", "<type_traits>"]),

    [ViewReinterpret] = new("""
      template <typename DstSpan, typename SrcSpan>
      DstSpan ply_view_reinterpret(SrcSpan src) {
        using DstElem = std::remove_pointer_t<decltype(DstSpan{}.data())>;
        using SrcElem = std::remove_pointer_t<decltype(src.data())>;
        const std::size_t total_bytes = src.size() * sizeof(SrcElem);
        return DstSpan(reinterpret_cast<DstElem*>(src.data()), total_bytes / sizeof(DstElem));
      }
      """, ["<cstddef>", "<type_traits>"]),

    [Pin] = new("""
      template <typename T, typename Src>
      T* ply_pin(Src&& src) {
        return src.empty() ? nullptr : const_cast<T*>(src.data());
      }
      template <typename T, typename Src>
      T* ply_pin(Src&& src, std::int32_t index) {
        if (index < 0 || static_cast<std::size_t>(index) >= src.size())
          throw std::out_of_range("index out of range");
        return const_cast<T*>(src.data() + static_cast<std::size_t>(index));
      }
      """, ["<cstddef>", "<cstdint>", "<stdexcept>"]),

    [PtrLoad] = new("""
      template <typename T>
      T ply_ptr_load(const T* ptr) {
        if (ptr == nullptr)
          throw std::runtime_error("null reference");
        return *ptr;
      }
      """, ["<stdexcept>"]),

    [PtrStore] = new("""
      template <typename T>
      void ply_ptr_store(T* ptr, T value) {
        if (ptr == nullptr)
          throw std::runtime_error("null reference");
        *ptr = value;
      }
      """, ["<stdexcept>"]),

    [PtrAdd] = new("""
      template <typename T>
      T* ply_ptr_add(T* ptr, std::int32_t offset) {
        return offset == 0 ? ptr : ptr + static_cast<std::ptrdiff_t>(offset);
      }
      """, ["<cstddef>", "<cstdint>"]),

    [Pext] = new("""
      template <typename T>
      T ply_pext(T source, T mask) {
        using U = std::make_unsigned_t<T>;
        const U src = static_cast<U>(source);
        U m = static_cast<U>(mask);
        U res = 0;
        U bit = 1;
        while (m != 0) {
          const U lsb = m & static_cast<U>(U{0} - m);
          if ((src & lsb) != 0)
            res |= bit;
          m ^= lsb;
          bit <<= 1;
        }
        return static_cast<T>(res);
      }
      """, ["<type_traits>"]),

    [Pdep] = new("""
      template <typename T>
      T ply_pdep(T source, T mask) {
        using U = std::make_unsigned_t<T>;
        U src = static_cast<U>(source);
        U m = static_cast<U>(mask);
        U res = 0;
        while (m != 0) {
          const U lsb = m & static_cast<U>(U{0} - m);
          if ((src & U{1}) != 0)
            res |= lsb;
          m ^= lsb;
          src >>= 1;
        }
        return static_cast<T>(res);
      }
      """, ["<type_traits>"]),

    [ListRemoveLastRef] = new("""
      template <typename T>
      T ply_list_pop_ref(std::vector<T>& list) {
        if (list.empty())
          return nullptr;
        T value = std::move(list.back());
        list.pop_back();
        return value;
      }
      """, ["<utility>", "<vector>"])
  };

  /// <summary>Returns the helper specification for a helper name.</summary>
  public static HelperSpec Get(string name) => _specs[name];

}
