.text
.globl entry
.type entry,%function
entry:
  ret
.size entry, . - entry

.section .tbss,"awT",@nobits
.globl thread_data
.type thread_data,@tls_object
thread_data:
  .space 8192
.size thread_data, . - thread_data

.section .init_array,"aw",@init_array
  .quad entry
