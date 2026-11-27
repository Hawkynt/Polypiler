/* SPDX-License-Identifier: LGPL-3.0-or-later */
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>

#define REPORT_WRITE(root, alias, move, initial, operand) do { \
  uint64_t result; \
  __asm__ volatile("movabs $" #initial ", %%" #root "\n\t" \
                   #move " $" #operand ", %%" #alias "\n\t" \
                   "mov %%" #root ", %0" \
                   : "=r"(result) : : #root); \
  printf("%s %s %016" PRIX64 " %016" PRIX64 " %016" PRIX64 "\n", \
         #root, #alias, UINT64_C(initial), UINT64_C(operand), result); \
} while (0)

int main(void) {
  REPORT_WRITE(rax, eax, movl, 0x1122334455667788, 0x89ABCDEF);
  REPORT_WRITE(rcx, ecx, movl, 0x0123456789ABCDEF, 0xFFFFFFFF);
  REPORT_WRITE(r15, r15d, movl, 0xFFFFFFFFFFFFFFFF, 0);
  REPORT_WRITE(rax, ax, movw, 0x1122334455667788, 0xBEEF);
  REPORT_WRITE(rax, ah, movb, 0x1122334455667788, 0xAB);
  REPORT_WRITE(rax, al, movb, 0x1122334455667788, 0xDE);
  REPORT_WRITE(r8, r8w, movw, 0xFFFFFFFFFFFFFFFF, 0);
  REPORT_WRITE(r15, r15b, movb, 0xFFFFFFFFFFFFFFFF, 0xFE);
  REPORT_WRITE(rsi, sil, movb, 0x1122334455667788, 0xAB);
  return 0;
}
