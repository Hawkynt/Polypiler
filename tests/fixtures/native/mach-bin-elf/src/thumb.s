.syntax unified
.text
.arm
.globl entry
.type entry,%function
entry:
  bx lr
.size entry, . - entry

.thumb
.globl tfn
.type tfn,%function
.thumb_func
tfn:
  bx lr
.size tfn, . - tfn

.align 2
.word 42
