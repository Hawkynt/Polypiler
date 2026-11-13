import math
def t(f):
    try: return repr(f())
    except Exception as e: return type(e).__name__
print("add:", 2147483647 + 1, " mul:", 65536 * 65536, " 2**64 + 1:", 2**64 + 1)
print("1 << 32:", 1 << 32, " 1 << 33:", 1 << 33, " 1 << -1:", t(lambda: 1 << -1), " -8 >> 33:", -8 >> 33, " 8 >> 33:", 8 >> 33, " 1 >> -1:", t(lambda: 1 >> -1))
print("7 // 0:", t(lambda: 7 // 0), " 7 % 0:", t(lambda: 7 % 0), " 7 / 0:", t(lambda: 7 / 0), " 7.0 / 0.0:", t(lambda: 7.0 / 0.0), " 7.0 % 0.0:", t(lambda: 7.0 % 0.0))
print("MIN // -1:", -2147483648 // -1, " MIN % -1:", -2147483648 % -1)
print("-7 // 3:", -7 // 3, " -7 % 3:", -7 % 3, " 7 % -3:", 7 % -3, " 7 // -3:", 7 // -3, " divmod(-7, 3):", divmod(-7, 3), " math.fmod(-7, 3):", math.fmod(-7, 3), " -7.5 % 2:", -7.5 % 2)
print("int(3e9):", int(3e9), " int(-3e9):", int(-3e9), " int(nan):", t(lambda: int(float('nan'))), " int(inf):", t(lambda: int(float('inf'))), " int(2.7):", int(2.7), " int(-2.7):", int(-2.7))
print("round(2.5):", round(2.5), " round(3.5):", round(3.5), " round(-2.5):", round(-2.5))
