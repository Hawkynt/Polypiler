.text
.globl entry
.type entry,%function
entry:
#if defined(__arm__)
  bx lr
#else
  ret
#endif
.size entry, . - entry

.bss
.globl state
.type state,%object
state:
  .space 16
.size state, . - state
