.text
.globl entry
.type entry,%function
entry:
#if defined(__arm__) || defined(__aarch64__)
  bl leaf
#else
  call leaf
#endif
.size entry, . - entry

.globl leaf
.type leaf,%function
leaf:
#if defined(__arm__)
  bx lr
#else
  ret
#endif
.size leaf, . - leaf
