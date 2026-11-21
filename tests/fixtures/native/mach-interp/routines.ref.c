/* The reference of the machine-state interpreter's fixtures: the routines of README.md, written from
 * their descriptions with uint32_t/uint8_t arithmetic, not from the MachineIR. Reads one vector per
 * line from standard input - "<routine> <name>=<hex> ..." with r0, r1, r2 and "m<address>=<hex bytes>"
 * - and prints the line followed by " => " and the final state: the outcome, r0..r2, the flags c z s o
 * where the routine sets them, every byte stored as "w<address>=<byte>" in store order, and a trap as
 * "trap=<kind>".
 *
 * Build and run:  gcc -std=c11 -O0 -o routines routines.ref.c && ./routines < vectors.txt
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint32_t r[3];
static int has[3];
static uint8_t memory[0x100000];
static int written[0x100000];

static void store(uint32_t address, uint8_t value, char *out) {
  memory[address] = value;
  written[address] = 1;
  sprintf(out + strlen(out), " w%X=%02X", address, value);
}

int main(void) {
  char line[1024];
  while (fgets(line, sizeof line, stdin)) {
    line[strcspn(line, "\r\n")] = 0;
    if (!line[0] || line[0] == '#')
      continue;
    char routine[32], copy[1024], out[1024] = "";
    memset(has, 0, sizeof has);
    memset(written, 0, sizeof written);
    strcpy(copy, line);
    char *token = strtok(copy, " ");
    strcpy(routine, token);
    while ((token = strtok(NULL, " "))) {
      if (token[0] == 'r') {
        int index = token[1] - '0';
        r[index] = (uint32_t)strtoul(token + 3, NULL, 16);
        has[index] = 1;
      } else if (token[0] == 'm') {
        char *equals = strchr(token, '=');
        uint32_t address = (uint32_t)strtoul(token + 1, NULL, 16);
        for (char *hex = equals + 1; hex[0] && hex[1]; hex += 2) {
          char pair[3] = {hex[0], hex[1], 0};
          memory[address] = (uint8_t)strtoul(pair, NULL, 16);
          written[address++] = 1;
        }
      }
    }

    const char *outcome = "returned";
    char flags[64] = "", trap[64] = "";
    if (!strcmp(routine, "addc")) {
      uint32_t a = r[0], b = r[1], sum = a + b;
      int c = sum < a, z = sum == 0, s = (sum >> 31) & 1, o = (int)((~(a ^ b) & (a ^ sum)) >> 31);
      r[0] = sum;
      sprintf(flags, " c=%d z=%d s=%d o=%d", c, z, s, o);
    } else if (!strcmp(routine, "subreg")) {
      r[0] = (r[0] & 0xFFFFFF00u) | (r[1] & 0xFFu);
      r[0] = (r[0] & 0xFFFF00FFu) | ((r[2] & 0xFFu) << 8);
    } else if (!strcmp(routine, "bytesum")) {
      uint32_t sum = 0;
      for (; r[1] != 0; --r[1])
        sum += memory[r[0]++];
      r[2] = sum;
      has[2] = 1;
      r[0] = sum;
    } else if (!strcmp(routine, "ud")) {
      store(0x2000, 0xAA, out);
      outcome = "trapped";
      strcpy(trap, " trap=undefined-instruction");
    } else if (!strcmp(routine, "wild")) {
      /* The pc-vga map: ram 0x00000-0x9FFFF, vga 0xA0000-0xBFFFF, rom 0xF0000-0xFFFFF; nothing else. */
      uint32_t address = r[0];
      int mapped = address <= 0xBFFFFu || (address >= 0xF0000u && address <= 0xFFFFFu);
      if (mapped) {
        store(address, (uint8_t)r[1], out);
      } else {
        outcome = "trapped";
        sprintf(trap, " trap=access-fault@%X", address);
      }
    } else {
      fprintf(stderr, "unknown routine %s\n", routine);
      return 1;
    }

    printf("%s => %s", line, outcome);
    for (int index = 0; index < 3; ++index)
      if (has[index])
        printf(" r%d=%08X", index, r[index]);
    printf("%s%s%s\n", flags, out, trap);
  }

  return 0;
}
