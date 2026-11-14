// Adler-32 of "abc" (RFC 1950 section 8.2): prints 38600999, or 1 for the empty input (-DEMPTY).
#include <cstdint>
#include <cstdio>
#include <string_view>

namespace {

constexpr std::uint32_t Modulus = 65521;

constexpr std::uint32_t Adler32(std::string_view data) {
  std::uint32_t a = 1, b = 0;
  for (const auto byte : data) {
    a = (a + static_cast<unsigned char>(byte)) % Modulus;
    b = (b + a) % Modulus;
  }
  return (b << 16) | a;
}

static_assert(Adler32("abc") == 0x024D0127u, "RFC 1950 section 8.2");

}

int main() {
#ifdef EMPTY
  constexpr std::string_view text = "";
#else
  constexpr std::string_view text = "abc";
#endif
  std::printf("%lu\n", static_cast<unsigned long>(Adler32(text)));
  return 0;
}
