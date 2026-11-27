Original ELF fixture inputs (LGPL-3.0-or-later).
Assembler and C sources are compiled during acceptance. Corrupt files were constructed for these tests.

a56c85682f20a4aaa90413ff42fdba4a918f32067a75dc955b9099877433c7b0  src/calls.s
c0f6a676a3aeaef5d218f79932f1be05fcf2c3455acbcd78aaeb14608640fa59  src/imports.c
feea5e689072e428b4d59398535eb9e80f78284b61d0dd2459cc07af601e90d2  src/leaf.s
a6ba13e60647bc024821a1e9a1dc50ab0df04c1b57fe2cf4f08e869011d5e8a3  src/thumb.s
dbca77e43e9109e3e62fabe243b8d0b5e29d66338590aeb611cca06c6fa41833  src/tls.s
0bf56411373743f8ab62c3ba6b99060bdeddabc425dcfa1b130c04072c339945  src/unsupported.s
c05214731d76b03cba270b37104563e042a91993f2954a178b071b9335d14884  corrupt/out-of-file-section.elf
  ELF64 section claims offset 4000 and size 200 in a 4096-byte file.
4de71b5632e99c8704b1d947d55a1da1b6a0278c2efd2595beb8dde91268b314  corrupt/truncated64.elf
  ELF64 header missing the final byte.
9c91efc930eab98f4ddab02e5145e59b3e10e8d78cb8a80717730846875a8ae9  corrupt/unsupported-machine.elf
  ELF32 header with e_machine = 8 (MIPS).
