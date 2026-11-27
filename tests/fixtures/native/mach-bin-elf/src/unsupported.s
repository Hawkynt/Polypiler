.text
.globl entry
.type entry,%function
entry:
  .long state@NTPOFF
  ret
.size entry, . - entry

.section .tdata,"awT",@progbits
.globl state
.type state,@tls_object
state:
  .long 1
.size state, . - state
