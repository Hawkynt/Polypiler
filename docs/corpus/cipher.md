# Cipher corpus

Every algorithm file of `Cipher/algorithms` of `Hawkynt/Hawkynt.github.io` at `48928fc1013dc7d3d42f76b05d0f200dbae23dd1`, translated from
JavaScript and checked on the known-answer tests the file itself carries (#1365). Written by
`polypiler-tooling cipher kat --all --report docs/corpus/cipher.md` from the golden files of
`tests/fixtures/corpus/cipher`, which node records with `tooling/cipher/record-kats.mjs`.

## Summary

| Target | Files | Translated | Built and run | Every checked KAT passes | KATs failing in translated files |
| --- | ---: | ---: | ---: | ---: | ---: |
| csharp | 933 | 37 | 35 | 34 | 5 |
| rust | 933 | 0 | 0 | 0 | 0 |

4958 known-answer tests in 933 files, 4840 of them checkable on a translation (node passes it, every value is data and the result is bytes; without an expected result node's own, where it is the same on every run).

## Refusal reasons

The first diagnostic of every refused file, the quoted names left out, by how many files it blocks.

### csharp

| Files | First diagnostic |
| ---: | --- |
| 93 | PLY1102: 'float64 where int32 is expected' is not supported yet |
| 61 | PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| 59 | PLY1102: 'a spread argument' is not supported yet |
| 56 | PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| 55 | PLY1102: 'a property added outside the constructor' is not supported yet |
| 35 | PLY1102: 'a call of ….freeze' is not supported yet |
| 32 | PLY1102: 'float64 where uint32 is expected' is not supported yet |
| 31 | PLY1101: parameter '…' of '…' has no supported JSDoc type |
| 25 | PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| 23 | PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| 18 | PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| 16 | PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| 13 | PLY1102: 'a call of ….from' is not supported yet |
| 13 | PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| 12 | PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| 354 | 169 other reasons |

### rust

| Files | First diagnostic |
| ---: | --- |
| 93 | PLY1102: 'float64 where int32 is expected' is not supported yet |
| 61 | PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| 59 | PLY1102: 'a spread argument' is not supported yet |
| 56 | PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| 55 | PLY1102: 'a property added outside the constructor' is not supported yet |
| 37 | PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile '…' (in the global '…') |
| 35 | PLY1102: 'a call of ….freeze' is not supported yet |
| 32 | PLY1102: 'float64 where uint32 is expected' is not supported yet |
| 31 | PLY1101: parameter '…' of '…' has no supported JSDoc type |
| 25 | PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| 23 | PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| 18 | PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| 16 | PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| 13 | PLY1102: 'a call of ….from' is not supported yet |
| 13 | PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| 366 | 170 other reasons |

## Files

| File | KATs checkable / all | csharp | rust |
| --- | ---: | --- | --- |
| `aead/ace.js` | 8 / 8 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/aes-ccm.js` | 4 / 4 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `aead/ascon.js` | 10 / 10 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `aead/chacha20-poly1305.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `aead/comet-cham.js` | 7 / 7 | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } |
| `aead/comet64-cham.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } |
| `aead/comet64-speck.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, associatedData, expected } and { text, uri, key, nonce, input, associatedData, expected } |
| `aead/deoxys-ii.js` | 14 / 14 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `aead/elephant.js` | 8 / 8 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `aead/estate-twegift.js` | 7 / 7 | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet |
| `aead/forkae.js` | 3 / 3 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `aead/gascon.js` | 10 / 10 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `aead/gift-cofb.js` | 7 / 7 | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet |
| `aead/gimli24.js` | 12 / 12 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/grain128aead.js` | 5 / 5 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `aead/hyena.js` | 6 / 6 | refused: PLY1102: 'a call of ConstantTimeCompare with 2 arguments for 3 parameters' is not supported yet | refused: PLY1102: 'a call of ConstantTimeCompare with 2 arguments for 3 parameters' is not supported yet |
| `aead/isap.js` | 5 / 5 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `aead/ketje.js` | 6 / 6 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `aead/keyak.js` | 5 / 5 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `aead/knot-aead.js` | 16 / 16 | refused: PLY1105: 'state' holds both uint8[] and uint8[] | refused: PLY1105: 'state' holds both uint8[] and uint8[] |
| `aead/locus-aead.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/lotus-aead.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/orange.js` | 8 / 8 | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet |
| `aead/oribatida.js` | 10 / 10 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `aead/paef-forkskinny.js` | 8 / 8 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `aead/photon-beetle.js` | 10 / 10 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `aead/pyjamask96.js` | 5 / 5 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `aead/romulus.js` | 7 / 7 | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet |
| `aead/saturnin.js` | 7 / 7 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `aead/skinny-aead.js` | 8 / 8 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `aead/sparkle.js` | 20 / 20 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/spix.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/spoc.js` | 11 / 11 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `aead/spook.js` | 24 / 24 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `aead/subterranean.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `aead/sundae-gift.js` | 8 / 8 | refused: PLY1102: 'uint8 view where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint8 view where uint8[] is expected' is not supported yet |
| `aead/tiaoxin.js` | 9 / 9 | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet | refused: PLY1102: 'an array of the length uint8[] or null' is not supported yet |
| `aead/tinyjambu.js` | 21 / 21 | refused: PLY1102: 'a call of ConstantTimeCompare with 2 arguments for 3 parameters' is not supported yet | refused: PLY1102: 'a call of ConstantTimeCompare with 2 arguments for 3 parameters' is not supported yet |
| `aead/wage.js` | 8 / 8 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `aead/xoodyak.js` | 6 / 6 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `asymmetric/bike.js` | 15 / 15 | refused: PLY1102: 'BIKE_L1' is not supported yet | refused: PLY1102: 'BIKE_L1' is not supported yet |
| `asymmetric/classic-mceliece.js` | 12 / 12 | refused: PLY1102: 'MCELIECE_348864' is not supported yet | refused: PLY1102: 'MCELIECE_348864' is not supported yet |
| `asymmetric/cross.js` | 30 / 30 | refused: PLY1102: 'a call with 1 arguments for 3 parameters' is not supported yet | refused: PLY1102: 'a call with 1 arguments for 3 parameters' is not supported yet |
| `asymmetric/diffie-hellman.js` | 12 / 12 | refused: PLY1102: 'a call of DHGroup.ctor, which cannot be imported' is not supported yet | refused: PLY1102: 'a call of DHGroup.ctor, which cannot be imported' is not supported yet |
| `asymmetric/dilithium.js` | 14 / 14 | refused: PLY1102: 'ML_DSA_44' is not supported yet | refused: PLY1102: 'ML_DSA_44' is not supported yet |
| `asymmetric/dsa.js` | 11 / 11 | refused: PLY1105: 'test' holds both { text, uri, p, q, g, x, hashAlgorithm, input, expected } and { text, uri, p, q, g, y, hashAlgorithm, signature, input, expected } | refused: PLY1105: 'test' holds both { text, uri, p, q, g, x, hashAlgorithm, input, expected } and { text, uri, p, q, g, y, hashAlgorithm, signature, input, expected } |
| `asymmetric/ecdsa.js` | 12 / 12 | refused: PLY1105: '_publicKey' holds both ECPoint and number | refused: PLY1105: '_publicKey' holds both ECPoint and number |
| `asymmetric/ed25519.js` | 3 / 3 | refused: PLY1103: the result of 'OpCodes.ShiftLn(1n, 255n) - 19n' has no bounded type | refused: PLY1103: the result of 'OpCodes.ShiftLn(1n, 255n) - 19n' has no bounded type |
| `asymmetric/elgamal.js` | 0 / 3 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `asymmetric/esign.js` | 4 / 4 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `asymmetric/faest.js` | 40 / 40 | refused: PLY1105: 'test' holds both { text, uri, parameterSet, drbgSeed, key, input, expected } and { text, uri, parameterSet, inverse, publicKey, input, expected } | refused: PLY1105: 'test' holds both { text, uri, parameterSet, drbgSeed, key, input, expected } and { text, uri, parameterSet, inverse, publicKey, input, expected } |
| `asymmetric/falcon.js` | 9 / 9 | refused: PLY1105: 'test' holds both { text, uri, inverse, publicKey, input, expected } and { text, uri, inverse, publicKey, message, input, expected } | refused: PLY1105: 'test' holds both { text, uri, inverse, publicKey, input, expected } and { text, uri, inverse, publicKey, message, input, expected } |
| `asymmetric/frodokem.js` | 0 / 3 | refused: PLY1102: 'int32[] where uint16[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint16[] is expected' is not supported yet |
| `asymmetric/hawk.js` | 27 / 27 | refused: PLY1102: 'a call of HawkParams.ctor, which cannot be imported' is not supported yet | refused: PLY1102: 'a call of HawkParams.ctor, which cannot be imported' is not supported yet |
| `asymmetric/hqc.js` | 15 / 15 | refused: PLY1102: 'HQC_128' is not supported yet | refused: PLY1102: 'HQC_128' is not supported yet |
| `asymmetric/luc.js` | 0 / 2 | refused: PLY1101: parameter 'value' of 'ShiftRn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftRn' has no supported JSDoc type |
| `asymmetric/lwe-signature.js` | 0 / 3 | refused: PLY1102: 'SBOUND' is not supported yet | refused: PLY1102: 'SBOUND' is not supported yet |
| `asymmetric/mayo.js` | 8 / 8 | refused: PLY1105: '_key' holds both MayoPublicKey and number | refused: PLY1105: '_key' holds both MayoPublicKey and number |
| `asymmetric/ml-dsa.js` | 16 / 16 | refused: PLY1102: 'PRE_HASH_SHA2_256' is not supported yet | refused: PLY1102: 'PRE_HASH_SHA2_256' is not supported yet |
| `asymmetric/ntru.js` | 12 / 12 | refused: PLY1102: 'NTRU_HPS_2048_509' is not supported yet | refused: PLY1102: 'NTRU_HPS_2048_509' is not supported yet |
| `asymmetric/perk.js` | 20 / 20 | refused: PLY1105: 'test' holds both { text, uri, keyGeneration, parameterSet, keyGenerationOutput, input, expected } and { text, uri, parameterSet, privateKey, signingRandomness, input, expected } | refused: PLY1105: 'test' holds both { text, uri, keyGeneration, parameterSet, keyGenerationOutput, input, expected } and { text, uri, parameterSet, privateKey, signingRandomness, input, expected } |
| `asymmetric/rabin-williams.js` | 0 / 2 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `asymmetric/rabin.js` | 0 / 2 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `asymmetric/rainbow.js` | 7 / 7 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `asymmetric/rsa.js` | 0 / 2 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `asymmetric/schnorr.js` | 4 / 4 | refused: PLY1105: '_publicKey' holds both uint8[] and number | refused: PLY1105: '_publicKey' holds both uint8[] and number |
| `asymmetric/sike.js` | 18 / 18 | refused: PLY1102: 'SIKE_P434' is not supported yet | refused: PLY1102: 'SIKE_P434' is not supported yet |
| `asymmetric/slh-dsa.js` | 12 / 12 | refused: PLY1102: 'SLH_DSA_SHA2_128S' is not supported yet | refused: PLY1102: 'SLH_DSA_SHA2_128S' is not supported yet |
| `asymmetric/sphincs-plus.js` | 12 / 12 | refused: PLY1105: 'test' holds both { text, uri, parameterSet, input, expected } and { text, uri, parameterSet, key, optRand, input, expected } | refused: PLY1105: 'test' holds both { text, uri, parameterSet, input, expected } and { text, uri, parameterSet, key, optRand, input, expected } |
| `asymmetric/sqisign.js` | 26 / 26 | refused: PLY1101: parameter 'value' of 'BitCountN' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'BitCountN' has no supported JSDoc type |
| `asymmetric/x25519.js` | 18 / 18 | refused: PLY1105: 'test' holds both { text, uri, privateKey, publicKey, otherPublicKey, expected, input } and { text, uri, privateKey, otherPublicKey, expected, input } | refused: PLY1105: 'test' holds both { text, uri, privateKey, publicKey, otherPublicKey, expected, input } and { text, uri, privateKey, otherPublicKey, expected, input } |
| `block/3des.js` | 8 / 8 | refused: PLY1105: '_subKeys' holds both TripleDESSubKeys and { k1, k2, k3 } | refused: PLY1105: '_subKeys' holds both TripleDESSubKeys and { k1, k2, k3 } |
| `block/anubis.js` | 10 / 10 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/aria.js` | 6 / 6 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/baseking.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/bassomatic.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, key, input, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, key, input, expected } |
| `block/blowfish.js` | 8 / 8 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/camellia.js` | 6 / 6 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `block/cascade.js` | 6 / 6 | refused: PLY1102: 'the property cipher1Name of Algorithm, which it does not have' is not supported yet | refused: PLY1102: 'the property cipher1Name of Algorithm, which it does not have' is not supported yet |
| `block/cast.js` | 9 / 9 | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet |
| `block/cham.js` | 1 / 1 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet |
| `block/clefia.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { input, key, expected, text, uri } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { input, key, expected, text, uri } |
| `block/crypton.js` | 6 / 6 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-3newde.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-anubis256.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-anubis320.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-bbc.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `block/darkcrypt-bj256.js` | 6 / 6 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-breakme.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-c2.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-cartman2x.js` | 3 / 3 | refused: PLY1105: 'IBlockCipherInstance.get_key' holds both uint8[] and boolean | refused: PLY1105: 'IBlockCipherInstance.get_key' holds both uint8[] and boolean |
| `block/darkcrypt-cast256.js` | 4 / 4 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-chaos.js` | 3 / 3 | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet |
| `block/darkcrypt-cobra.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-cobra128576.js` | 5 / 5 | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet |
| `block/darkcrypt-crypton.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-cscipher.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-curupira1.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-curupira2.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-daginda.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-deal.js` | 3 / 3 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `block/darkcrypt-desx.js` | 3 / 3 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `block/darkcrypt-dfc.js` | 4 / 4 | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet |
| `block/darkcrypt-diamond2.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-e2.js` | 4 / 4 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `block/darkcrypt-eksloki89.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-enrupt.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-fcrypt.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-fnam2.js` | 6 / 6 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-frog.js` | 4 / 4 | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet |
| `block/darkcrypt-gost-ede.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-gost.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-gtea.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-gtea21.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-gtea21ks.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-hierocrypt-l1.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-hierocrypt3.js` | 4 / 4 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-hpc.js` | 3 / 3 | refused: PLY1102: 'the value of m64(PI19 + 128n), a call that only throws' is not supported yet | refused: PLY1102: 'the value of m64(PI19 + 128n), a call that only throws' is not supported yet |
| `block/darkcrypt-idea.js` | 4 / 4 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-ideanxt.js` | 3 / 3 | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet |
| `block/darkcrypt-iraqi.js` | 3 / 3 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/darkcrypt-ixchel.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-kairakan.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-kameko.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-karla.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-keeloq.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-khufu512.js` | 3 / 3 | refused: PLY1105: 'arr' holds both uint8[] and a list of number | refused: PLY1105: 'arr' holds both uint8[] and a list of number |
| `block/darkcrypt-kinebick.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-lamecrypt.js` | 3 / 3 | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet |
| `block/darkcrypt-letsief3.js` | 6 / 6 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-leviathan.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-lja1.js` | 6 / 6 | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet |
| `block/darkcrypt-loki91.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-loki97.js` | 4 / 4 | refused: PLY1102: 'boolean where float64 is expected' is not supported yet | refused: PLY1102: 'boolean where float64 is expected' is not supported yet |
| `block/darkcrypt-lucifer.js` | 3 / 3 | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet |
| `block/darkcrypt-macguffin.js` | 3 / 3 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `block/darkcrypt-magenta.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `block/darkcrypt-mars-1248.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-mars-256.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-mars-512.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-maskenoza.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-mbc2.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-md4.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-md5.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-mdckarn.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-mercy.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `block/darkcrypt-messa.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `block/darkcrypt-misty1.js` | 4 / 4 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-mmb.js` | 4 / 4 | refused: PLY1101: parameter 'a' of 'MulModN' has no supported JSDoc type | refused: PLY1101: parameter 'a' of 'MulModN' has no supported JSDoc type |
| `block/darkcrypt-mmb2.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-mpj2.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-multiswap.js` | 3 / 3 | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet |
| `block/darkcrypt-newdes.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-newdes96.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-newtea.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-noekeon-indirect.js` | 4 / 4 | refused: PLY1102: 'int32[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint32[] is expected' is not supported yet |
| `block/darkcrypt-nsea.js` | 3 / 3 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `block/darkcrypt-nush.js` | 4 / 4 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-paranoia.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-pes.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-phantom.js` | 3 / 3 | refused: PLY1102: 'PHANTOM_SBOX_HEX' is not supported yet | refused: PLY1102: 'PHANTOM_SBOX_HEX' is not supported yet |
| `block/darkcrypt-pikachu.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-q128.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-raiden.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-raiden32.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-rainbow.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-rc2.js` | 3 / 3 | refused: PLY1102: 'uint32 where uint16 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint16 is expected' is not supported yet |
| `block/darkcrypt-rc5.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-rc6-512.js` | 3 / 3 | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet |
| `block/darkcrypt-redoc2.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-redoc3.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-rijndael256.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-rtea.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-safer-sk128.js` | 3 / 3 | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet |
| `block/darkcrypt-saferpp.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-sc2000.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-sc6b.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-seed.js` | 4 / 4 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-serpent.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-sha1.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-shark-a.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-shark-e.js` | 3 / 3 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/darkcrypt-shebamik.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-shogashi.js` | 3 / 3 | refused: PLY1105: 'b' holds both uint8[] and uint8[] | refused: PLY1105: 'b' holds both uint8[] and uint8[] |
| `block/darkcrypt-simplicity.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-sinople.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-snappy.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-sobbikashi.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-sonjitege.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-speed.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-tc18.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-threefish1024.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `block/darkcrypt-threefish512tw.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `block/darkcrypt-twopes.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/darkcrypt-umchak.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-unicorn-a.js` | 3 / 3 | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet |
| `block/darkcrypt-unicorn-e.js` | 3 / 3 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `block/darkcrypt-vemokwana.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'ShiftRn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftRn' has no supported JSDoc type |
| `block/darkcrypt-vsen.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-vsen10.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-wabasso.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/darkcrypt-webino.js` | 3 / 3 | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet | refused: PLY1102: 'uint32 where int32 is expected' is not supported yet |
| `block/darkcrypt-wicker98.js` | 6 / 6 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xtea-tw.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xtea.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xtea1.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xtea3.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xxtea-tw.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/darkcrypt-xxtea.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/deal.js` | 3 / 3 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/des-x.js` | 5 / 5 | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet |
| `block/des.js` | 5 / 5 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/dfc.js` | 1 / 1 | refused: PLY1105: 'keySchedule' holds both a list of uint8[] and number | refused: PLY1105: 'keySchedule' holds both a list of uint8[] and number |
| `block/diamond2.js` | 25 / 25 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/doubleking.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/enrupt.js` | 3 / 3 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `block/feal-nx.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/feal.js` | 1 / 1 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/ff.js` | 28 / 28 | refused: PLY1105: '_key' holds both uint8[] and uint8[] | refused: PLY1105: '_key' holds both uint8[] and uint8[] |
| `block/forkskinny.js` | 4 / 4 | refused: PLY1102: 'outputLeft.concat of a list' is not supported yet | refused: PLY1102: 'outputLeft.concat of a list' is not supported yet |
| `block/frog.js` | 10 / 10 | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet |
| `block/gift128.js` | 5 / 5 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/gost.js` | 5 / 5 | refused: PLY1105: '_key' holds both uint8[] and number | refused: PLY1105: '_key' holds both uint8[] and number |
| `block/grand-cru.js` | 4 / 4 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `block/hierocrypt-l1.js` | 1 / 1 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/hierocrypt3.js` | 1 / 1 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `block/hight.js` | 3 / 3 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/hpc.js` | 5 / 5 | refused: PLY1102: 'a call of OpCodes.RotL64n' is not supported yet | refused: PLY1102: 'a call of OpCodes.RotL64n' is not supported yet |
| `block/hurricane.js` | 4 / 4 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `block/ice.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/idea.js` | 6 / 6 | refused: PLY1102: 'int32[] where uint16[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint16[] is expected' is not supported yet |
| `block/kalyna.js` | 2 / 2 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `block/kasumi.js` | 11 / 11 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/keeloq.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/khazad.js` | 9 / 9 | refused: PLY1102: 'an override with another number of parameters' is not supported yet | refused: PLY1102: 'an override with another number of parameters' is not supported yet |
| `block/khufu.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, rounds, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, rounds, expected } |
| `block/kuznyechik.js` | 2 / 2 | refused: PLY1102: 'a statement at the top level of the module' is not supported yet | refused: PLY1102: 'a statement at the top level of the module' is not supported yet |
| `block/lblock.js` | 2 / 2 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/lea.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/lion.js` | 1 / 1 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `block/loki.js` | 4 / 4 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet |
| `block/lucifer.js` | 6 / 6 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/macguffin.js` | 5 / 5 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/magenta.js` | 9 / 9 | refused: PLY1105: 'processedBlock' holds both uint8[] and number | refused: PLY1105: 'processedBlock' holds both uint8[] and number |
| `block/magma.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, expected, inverse } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, expected, inverse } |
| `block/mantis.js` | 1 / 1 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/mars.js` | 10 / 10 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/mercy.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `block/midori.js` | 4 / 4 | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet |
| `block/misty.js` | 8 / 8 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/multi2.js` | 2 / 2 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/newdes.js` | 3 / 3 | refused: PLY1102: 'an override with another number of parameters' is not supported yet | refused: PLY1102: 'an override with another number of parameters' is not supported yet |
| `block/noekeon.js` | 6 / 6 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet |
| `block/present.js` | 8 / 8 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `block/pride.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/pyjamask128.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/rc.js` | 11 / 11 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/redoc.js` | 13 / 13 | refused: PLY1102: ''redoc2Tables', a { perm, sub, inv, encl } that may be null' is not supported yet | refused: PLY1102: ''redoc2Tables', a { perm, sub, inv, encl } that may be null' is not supported yet |
| `block/rhx.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/rijndael.js` | 9 / 9 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `block/safer.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, rounds, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, rounds, expected } |
| `block/saferp.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/sc2000.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/seed.js` | 5 / 5 | 5 / 5 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `block/serpent.js` | 6 / 6 | refused: PLY1102: 'an assignment inside an expression' is not supported yet | refused: PLY1102: 'an assignment inside an expression' is not supported yet |
| `block/shacal-1.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/shacal-2.js` | 1 / 1 | refused: PLY1102: 'a destructuring declaration' is not supported yet | refused: PLY1102: 'a destructuring declaration' is not supported yet |
| `block/shark-cboxes.data.js` | 0 / 0 (registers no algorithm) | refused: PLY1102: 'a file without a function' is not supported yet | refused: PLY1102: 'a file without a function' is not supported yet |
| `block/shark.js` | 7 / 7 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/shx.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/simeck.js` | 20 / 20 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `block/simon.js` | 4 / 4 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet |
| `block/skinny128.js` | 3 / 3 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, blockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, blockSize)' is not supported yet |
| `block/skip32.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/skipjack.js` | 5 / 5 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `block/sm4.js` | 3 / 3 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/speck.js` | 4 / 4 | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet | refused: PLY1102: 'the value of this.inputBuffer.splice(0, this.BlockSize)' is not supported yet |
| `block/speed.js` | 3 / 3 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/square.js` | 9 / 9 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/tea.js` | 5 / 5 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/threefish.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/thx.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `block/tnepres.js` | 13 / 13 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `block/twine.js` | 2 / 2 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `block/twofish.js` | 7 / 7 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `block/unicorn-a.js` | 1 / 1 | refused: PLY1102: 'float64[] where uint8[] list list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8[] list list is expected' is not supported yet |
| `block/xtea.js` | 2 / 2 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `block/xxtea.js` | 3 / 3 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `checksum/aba-routing.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/adler.js` | 11 / 11 | 11 / 11 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/bsd-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/check-digit.js` | 12 / 12 | 12 / 12 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/complement.js` | 8 / 8 | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet |
| `checksum/constant-weight.js` | 4 / 4 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `checksum/crc.js` | 60 / 60 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `checksum/cusip-checksum.js` | 3 / 3 | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet |
| `checksum/damm.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/ean13-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/ean8-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/fletcher.js` | 15 / 15 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `checksum/gtin-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/iban-checksum.js` | 3 / 3 | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet |
| `checksum/iccid-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/imei-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/internet-checksum.js` | 4 / 4 | refused: PLY1102: 'a spread argument' is not supported yet | refused: PLY1102: 'a spread argument' is not supported yet |
| `checksum/isbn.js` | 6 / 6 | 6 / 6 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/isin-checksum.js` | 3 / 3 | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet |
| `checksum/issn-checksum.js` | 3 / 3 | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet |
| `checksum/lrc.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/luhn.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/modulo.js` | 9 / 9 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `checksum/nmea-0183.js` | 3 / 3 | refused: PLY1101: parameter 'a' of 'XorN' has no supported JSDoc type | refused: PLY1101: parameter 'a' of 'XorN' has no supported JSDoc type |
| `checksum/npi-checksum.js` | 3 / 3 | refused: PLY1102: 'fullNumber.concat of a list' is not supported yet | refused: PLY1102: 'fullNumber.concat of a list' is not supported yet |
| `checksum/parity.js` | 12 / 12 | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet |
| `checksum/planet-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/postnet-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/sedol-checksum.js` | 3 / 3 | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet |
| `checksum/sum-checksum.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `checksum/sysv-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/unix-sum.js` | 8 / 8 | 8 / 8 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/upc-ean.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/upca-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/verhoeff.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/vin-checksum.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `checksum/xor-checksum.js` | 3 / 3 | refused: PLY1101: parameter 'a' of 'XorN' has no supported JSDoc type | refused: PLY1101: parameter 'a' of 'XorN' has no supported JSDoc type |
| `classical/affine.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `classical/al-kindi-frequency.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, input, expected, language } and { text, uri, input, key, expected } | refused: PLY1105: 'test' holds both { text, uri, input, expected, language } and { text, uri, input, key, expected } |
| `classical/autokey.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/bazeries.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/beaufort.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/bifid.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/cadaenus.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `classical/caesar.js` | 5 / 5 | 5 / 5 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `classical/columnar.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/enigma.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/foursquare.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/gronsfeld.js` | 5 / 5 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/hill.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/jefferson-wheel.js` | 2 / 2 | refused: PLY1102: 'a call with 2 arguments for 1 parameters' is not supported yet | refused: PLY1102: 'a call with 2 arguments for 1 parameters' is not supported yet |
| `classical/nihilist.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/phillips.js` | 2 / 2 | refused: PLY1102: 'string[] where string list is expected' is not supported yet | refused: PLY1102: 'string[] where string list is expected' is not supported yet |
| `classical/pigpen.js` | 2 / 2 | refused: PLY1102: 'a call with 2 arguments for 1 parameters' is not supported yet | refused: PLY1102: 'a call with 2 arguments for 1 parameters' is not supported yet |
| `classical/playfair.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/polybius.js` | 3 / 3 | refused: PLY1102: 'string[] list where string list list is expected' is not supported yet | refused: PLY1102: 'string[] list where string list list is expected' is not supported yet |
| `classical/porta.js` | 3 / 3 | refused: PLY1102: 'replace with a regular expression' is not supported yet | refused: PLY1102: 'replace with a regular expression' is not supported yet |
| `classical/railfence.js` | 5 / 5 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/scytale.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/solitaire.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, key, expected } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, key, expected } |
| `classical/trifid.js` | 3 / 3 | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet | refused: PLY1102: 'toUpperCase, which is case mapping' is not supported yet |
| `classical/twosquare.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `classical/vigenere.js` | 5 / 5 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `compression/ace-archiver.js` | 4 / 4 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/adaptive-huffman.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/ans.js` | 9 / 9 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/aplib.js` | 5 / 5 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/arithmetic.js` | 4 / 4 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/arj.js` | 4 / 4 | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet |
| `compression/balz.js` | 6 / 6 | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet |
| `compression/bcj-arm-thumb.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-arm.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-arm64.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-ia64.js` | 2 / 2 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `compression/bcj-powerpc.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-riscv.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-sparc.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcj-x86.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/bcm.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/bpe.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/brieflz.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/brotli-dictionary.data.js` | 0 / 0 (registers no algorithm) | refused: PLY1102: 'DICT_HEX' is not supported yet | refused: PLY1102: 'DICT_HEX' is not supported yet |
| `compression/brotli.js` | 8 / 8 | refused: PLY1102: 'the module 'BrotliDictionary' as a value' is not supported yet | refused: PLY1102: 'the module 'BrotliDictionary' as a value' is not supported yet |
| `compression/bsc.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/bwt-advanced.js` | 6 / 6 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `compression/bwt.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/bzip2.js` | 6 / 6 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `compression/cmix.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/crush.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet |
| `compression/csc.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/ctw-willems.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet |
| `compression/ctw.js` | 8 / 8 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `compression/deflate-simple.js` | 7 / 7 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `compression/deflate.js` | 3 / 3 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/deflate64.js` | 3 / 3 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/delta-filter.js` | 4 / 4 | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet |
| `compression/delta.js` | 3 / 3 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `compression/density.js` | 6 / 6 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/dmc.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/dna-compression.js` | 4 / 4 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `compression/doublespace.js` | 5 / 5 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `compression/dpcm.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/drivespace.js` | 5 / 5 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `compression/ds-lz77-doublespace.js` | 7 / 7 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `compression/ds-lz77.js` | 3 / 3 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/elias-delta.js` | 2 / 2 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/elias-gamma.js` | 2 / 2 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/exp-golomb.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/fastlz.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/fibonacci.js` | 8 / 8 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/fse.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/golomb-bitstream.js` | 5 / 5 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/golomb.js` | 9 / 9 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/huffman-code-lengths.data.js` | 0 / 0 (registers no algorithm) | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/huffman.js` | 2 / 2 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/ibm842.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/implode.js` | 5 / 5 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/levenshtein-coding.js` | 4 / 4 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/lizard.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lz4-frame.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lz4.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lz77-optimal.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lz77.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lz78.js` | 7 / 7 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/lzap.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzav.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/lzf.js` | 11 / 11 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/lzfse.js` | 7 / 7 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `compression/lzfx.js` | 6 / 6 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/lzg.js` | 9 / 9 | refused: the translation timed out | refused: the translation timed out |
| `compression/lzh.js` | 8 / 8 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/lzham.js` | 5 / 5 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/lzjb.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lzma.js` | 9 / 9 | refused: the translation timed out | refused: the translation timed out |
| `compression/lzmat.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lzms.js` | 6 / 6 | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet |
| `compression/lzmw.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzo.js` | 4 / 4 | refused: PLY1102: 'an array of the length a list of number' is not supported yet | refused: PLY1102: 'an array of the length a list of number' is not supported yet |
| `compression/lzp.js` | 5 / 5 | refused: PLY1102: 'uint8[] where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64 list is expected' is not supported yet |
| `compression/lzrle.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lzrw1.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `compression/lzrw3.js` | 8 / 8 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/lzs.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzss.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzturbo.js` | 9 / 9 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzvn.js` | 9 / 9 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/lzw.js` | 6 / 6 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `compression/lzwl.js` | 6 / 6 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/lzx.js` | 8 / 8 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/mcm.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/ms-lzh.js` | 8 / 8 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/mtf.js` | 7 / 7 | refused: PLY1102: 'int32 list where float64 list is expected' is not supported yet | refused: PLY1102: 'int32 list where float64 list is expected' is not supported yet |
| `compression/neural-compression.js` | 4 / 4 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `compression/nrv2d.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/nrv2e.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/omega.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet |
| `compression/packbits.js` | 8 / 8 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/paq.js` | 5 / 5 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/paq8hp.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/pithy.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/ppm.js` | 8 / 8 | refused: PLY1101: parameter 'excluded' of 'Context.effectiveTotals' has no supported JSDoc type | refused: PLY1101: parameter 'excluded' of 'Context.effectiveTotals' has no supported JSDoc type |
| `compression/ppmd.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `compression/quantum.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/quicklz.js` | 8 / 8 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/range-coding.js` | 7 / 7 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/rans.js` | 9 / 9 | refused: PLY1102: 'Infinity' is not supported yet | refused: PLY1102: 'Infinity' is not supported yet |
| `compression/rar.js` | 4 / 4 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/rar5.js` | 7 / 7 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/reduce.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/repair.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/rle.js` | 3 / 3 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/rolz.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `compression/rzip.js` | 7 / 7 | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where float64 list is expected' is not supported yet |
| `compression/salvador.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } |
| `compression/sequitur.js` | 8 / 8 | refused: PLY1102: 'this.referrers.length = this.referrers.length - 1, a length other than 0' is not supported yet | refused: PLY1102: 'this.referrers.length = this.referrers.length - 1, a length other than 0' is not supported yet |
| `compression/shannon-fano.js` | 3 / 3 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/shoco.js` | 4 / 4 | refused: PLY1102: 'TRAINING_CORPUS' is not supported yet | refused: PLY1102: 'TRAINING_CORPUS' is not supported yet |
| `compression/shrink.js` | 5 / 5 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `compression/snappy.js` | 8 / 8 | refused: PLY1102: 'an array of the length a list of number' is not supported yet | refused: PLY1102: 'an array of the length a list of number' is not supported yet |
| `compression/sqx.js` | 4 / 4 | refused: PLY1102: 'MAX_ORDINARY_SPAN' is not supported yet | refused: PLY1102: 'MAX_ORDINARY_SPAN' is not supported yet |
| `compression/suffix-tree.js` | 3 / 3 | refused: PLY1102: 'uint8[] where float64 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64 list is expected' is not supported yet |
| `compression/tans.js` | 8 / 8 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `compression/tunstall.js` | 4 / 4 | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet | refused: PLY1102: 'new OpCodes._BitStream' is not supported yet |
| `compression/ucl.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } |
| `compression/unary.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } |
| `compression/xpress.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } |
| `compression/xz-lzma2.js` | 7 / 7 | refused: PLY1105: 'low32' holds both number and bigint | refused: PLY1105: 'low32' holds both number and bigint |
| `compression/zling.js` | 6 / 6 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/zopfli.js` | 3 / 3 | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet | refused: PLY1102: '!== on float64[] and float64[]' is not supported yet |
| `compression/zpaq.js` | 7 / 7 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `compression/zstd.js` | 6 / 6 | refused: PLY1101: parameter 'a' of 'AndN' has no supported JSDoc type | refused: PLY1101: parameter 'a' of 'AndN' has no supported JSDoc type |
| `compression/zx0.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, roundTripOnly } |
| `crypto/aeswrap.js` | 6 / 6 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `crypto/aeswrappad.js` | 2 / 2 | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint32 list where uint8 list is expected' is not supported yet |
| `crypto/ariawrap.js` | 5 / 5 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `crypto/ariawrappad.js` | 2 / 2 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `crypto/camelliawrap.js` | 1 / 1 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `crypto/camelliawrappad.js` | 2 / 2 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `crypto/cryptoprowrap.js` | 1 / 1 | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8[] is expected' is not supported yet |
| `crypto/desedewrap.js` | 1 / 1 | refused: PLY1102: 'a call of temp2.slice().reverse' is not supported yet | refused: PLY1102: 'a call of temp2.slice().reverse' is not supported yet |
| `crypto/gost28147wrap.js` | 1 / 1 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `crypto/kalynawrap.js` | 4 / 4 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `crypto/rfc3211wrap.js` | 2 / 2 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `crypto/seedwrap.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `crypto/seedwrappad.js` | 2 / 2 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `ecc/2d-parity.js` | 4 / 4 | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet |
| `ecc/alamouti-code.js` | 8 / 8 | refused: PLY1102: 'float64 list where int32 list is expected' is not supported yet | refused: PLY1102: 'float64 list where int32 list is expected' is not supported yet |
| `ecc/algebraic-geometry-code.js` | 8 / 8 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `ecc/bacon-shor-code.js` | 3 / 3 | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32 list where uint8 list is expected' is not supported yet |
| `ecc/batched-sparse-code.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/bch-code.js` | 3 / 3 | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet |
| `ecc/bch.js` | 2 / 2 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/berger-code.js` | 8 / 8 | refused: PLY1105: 'result' holds both uint8[] and number | refused: PLY1105: 'result' holds both uint8[] and number |
| `ecc/bicycle-code.js` | 4 / 4 | refused: PLY0001: internal compiler error: NullReferenceException: Object reference not set to an instance of an object. | refused: PLY0001: internal compiler error: NullReferenceException: Object reference not set to an instance of an object. |
| `ecc/biorthogonal-code.js` | 4 / 4 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `ecc/cat-code.js` | 9 / 9 | refused: PLY1105: 'test' holds both { text, uri, alpha, input, expected } and { text, uri, alpha, parityMeasurement, input, expected } | refused: PLY1105: 'test' holds both { text, uri, alpha, input, expected } and { text, uri, alpha, parityMeasurement, input, expected } |
| `ecc/color-code.js` | 2 / 2 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/concatenated.js` | 2 / 2 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/constant-weight-balanced.js` | 4 / 4 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `ecc/convolutional-viterbi.js` | 4 / 4 | refused: PLY1102: 'float64[] where float64 list is expected' is not supported yet | refused: PLY1102: 'float64[] where float64 list is expected' is not supported yet |
| `ecc/cortex-code.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/css-quantum-code.js` | 2 / 2 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/dna-storage-code.js` | 6 / 6 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `ecc/dual-hamming.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/even-weight-code.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `ecc/expander-code.js` | 4 / 4 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `ecc/extended-golay.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/extended-self-dual.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/fire-code.js` | 2 / 2 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/folded-reed-solomon.js` | 5 / 5 | refused: PLY1102: 'uint32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where float64[] is expected' is not supported yet |
| `ecc/fountain-foundation.data.js` | 0 / 0 (registers no algorithm) | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `ecc/gabidulin-code.js` | 6 / 6 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `ecc/gkp-code.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, logicalState, gridSize, latticeSpacing, input, expected } and { text, uri, logicalState, gridSize, displacementType, displacementAmount, input, expected } | refused: PLY1105: 'test' holds both { text, uri, logicalState, gridSize, latticeSpacing, input, expected } and { text, uri, logicalState, gridSize, displacementType, displacementAmount, input, expected } |
| `ecc/golay.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/goppa-code.js` | 4 / 4 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `ecc/hadamard-code.js` | 4 / 4 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/hamming.js` | 7 / 7 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `ecc/hermitian-code.js` | 6 / 6 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `ecc/hsiao-code.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/interleaver.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `ecc/justesen-code.js` | 4 / 4 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/kerdock-code.js` | 4 / 4 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `ecc/ldpc.js` | 2 / 2 | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet |
| `ecc/levenshtein-code.js` | 4 / 4 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `ecc/lexicographic-code.js` | 4 / 4 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/locally-recoverable-code.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/lrc-pyramid-code.js` | 6 / 6 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `ecc/lt-codes.js` | 2 / 2 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/luby-transform-enhanced.js` | 3 / 3 | refused: PLY1102: 'float64[] where int32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where int32[] list is expected' is not supported yet |
| `ecc/multi-edge-ldpc.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `ecc/nordstrom-robinson.js` | 4 / 4 | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet |
| `ecc/online-code.js` | 4 / 4 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/parvaresh-vardy-code.js` | 4 / 4 | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet |
| `ecc/plotkin-code.js` | 14 / 14 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/polar-code.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `ecc/preparata-code.js` | 4 / 4 | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint32 list is expected' is not supported yet |
| `ecc/product-code.js` | 3 / 3 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `ecc/protograph-ldpc.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input } |
| `ecc/quadratic-residue-code.js` | 4 / 4 | refused: PLY1102: 'int32 where uint8 is expected' is not supported yet | refused: PLY1102: 'int32 where uint8 is expected' is not supported yet |
| `ecc/quantum-ldpc.js` | 2 / 2 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/raptor-codes.js` | 2 / 2 | refused: PLY1102: 'float64[] where int32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where int32[] list is expected' is not supported yet |
| `ecc/raptor-enhanced.js` | 3 / 3 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `ecc/raptorq-codes.js` | 2 / 2 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `ecc/reed-muller.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/reed-solomon.js` | 3 / 3 | refused: PLY1102: 'uint32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where float64[] is expected' is not supported yet |
| `ecc/repeat-accumulate-code.js` | 5 / 5 | refused: PLY1102: 'uint8[] where int32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where int32[] is expected' is not supported yet |
| `ecc/repetition-code.js` | 4 / 4 | refused: PLY1102: 'a call of console.log' is not supported yet | refused: PLY1102: 'a call of console.log' is not supported yet |
| `ecc/repetition.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `ecc/secded.js` | 4 / 4 | refused: PLY1102: 'a destructuring declaration' is not supported yet | refused: PLY1102: 'a destructuring declaration' is not supported yet |
| `ecc/simplex-code.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/single-parity-check.js` | 4 / 4 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `ecc/singleton-bound-code.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/space-time-block-code.js` | 0 / 8 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `ecc/spatially-coupled-ldpc.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `ecc/spinal-code.js` | 4 / 4 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `ecc/stabilizer-quantum-code.js` | 3 / 3 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `ecc/surface-code.js` | 7 / 7 | refused: PLY1105: 'test' holds both { text, uri, distance, input, expected } and { text, uri, distance, errorType, input, expected } | refused: PLY1105: 'test' holds both { text, uri, distance, input, expected } and { text, uri, distance, errorType, input, expected } |
| `ecc/tail-biting-convolutional.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32 list is expected' is not supported yet |
| `ecc/ternary-golay.js` | 4 / 4 | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where float64[] is expected' is not supported yet |
| `ecc/tornado-code.js` | 4 / 4 | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet | refused: PLY1102: 'int32[] where int32 list is expected' is not supported yet |
| `ecc/trellis-coded-modulation.js` | 4 / 4 | refused: PLY1102: 'float64 list list where TrellisTransition list list is expected' is not supported yet | refused: PLY1102: 'float64 list list where TrellisTransition list list is expected' is not supported yet |
| `ecc/triple-modular-redundancy.js` | 4 / 4 | refused: PLY1102: 'a call of console.log' is not supported yet | refused: PLY1102: 'a call of console.log' is not supported yet |
| `ecc/turbo-code.js` | 4 / 4 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `ecc/varshamov-tenengolts.js` | 4 / 4 | refused: PLY1102: 'a call of console.warn' is not supported yet | refused: PLY1102: 'a call of console.warn' is not supported yet |
| `ecc/wozencraft-ensemble.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `ecc/zigzag-code.js` | 5 / 5 | 0 / 5 pass; Zigzag Code #1: Result threw IndexOutOfRangeException: Index was outside the bounds of the array. | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `encoding/atbash.js` | 3 / 3 | refused: PLY1102: 'null' is not supported yet | refused: PLY1102: 'null' is not supported yet |
| `encoding/base16.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/base32.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/base58.js` | 7 / 7 | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet | refused: PLY1102: 'a part of a typed array whose array or positions are more than variables, constants and arithmetic of them' is not supported yet |
| `encoding/base62.js` | 6 / 6 | refused: PLY1102: 'charAt of an operand with an effect, which its translation reads twice' is not supported yet | refused: PLY1102: 'charAt of an operand with an effect, which its translation reads twice' is not supported yet |
| `encoding/base64.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/base85.js` | 9 / 9 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `encoding/base91.js` | 7 / 7 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `encoding/baudot.js` | 4 / 4 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/binhex.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/bubblebabble.js` | 6 / 6 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `encoding/koremutake.js` | 6 / 6 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `encoding/manchester.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/morse.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/pem.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/rot.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/uuencode.js` | 7 / 7 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `encoding/xxencode.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/yenc.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `encoding/z85.js` | 4 / 4 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `hash/ascon-hash.js` | 21 / 21 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/blake.js` | 11 / 11 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `hash/blake2.js` | 16 / 16 | refused: PLY1102: 'BigInt of string that is no proven integer' is not supported yet | refused: PLY1102: 'BigInt of string that is no proven integer' is not supported yet |
| `hash/blake3-enhanced.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, outputSize, expected } |
| `hash/blake3.js` | 11 / 11 | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, expected } and { text, uri, input, outputSize, expected } |
| `hash/chc.js` | 1 / 1 | refused: PLY1105: 'IAlgorithmInstance.Result' holds both a list of number and number | refused: PLY1105: 'IAlgorithmInstance.Result' holds both a list of number and number |
| `hash/cityhash.js` | 13 / 13 | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type |
| `hash/comb4p.js` | 2 / 2 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `hash/cshake.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, customization, outputSize, expected } and { text, uri, input, functionName, customization, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, customization, outputSize, expected } and { text, uri, input, functionName, customization, outputSize, expected } |
| `hash/cubehash.js` | 6 / 6 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `hash/darkcrypt-keccak.js` | 6 / 6 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/darkcrypt-md6.js` | 5 / 5 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `hash/darkcrypt-skein.js` | 7 / 7 | refused: PLY1102: 'the value of OpCodes.ShiftLn(1n, 62), a call that only throws' is not supported yet | refused: PLY1102: 'the value of OpCodes.ShiftLn(1n, 62), a call that only throws' is not supported yet |
| `hash/drygascon-hash.js` | 12 / 12 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `hash/dstu7564.js` | 15 / 15 | refused: PLY1101: parameter 'mask' of 'swapMask64' has no supported JSDoc type | refused: PLY1101: parameter 'mask' of 'swapMask64' has no supported JSDoc type |
| `hash/echo.js` | 10 / 10 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `hash/esch256.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/esch384.js` | 10 / 10 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/fnv.js` | 3 / 3 | refused: PLY1102: 'concat of uint8[] with nothing' is not supported yet | refused: PLY1102: 'concat of uint8[] with nothing' is not supported yet |
| `hash/fugue.js` | 8 / 8 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/gimli24-hash.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `hash/gost3411.js` | 4 / 4 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `hash/groestl.js` | 6 / 6 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `hash/hamsi.js` | 24 / 24 | refused: PLY1105: 'test' holds both { text, uri, variant, input, expected } and { text, uri, variant, input, expected } | refused: PLY1105: 'test' holds both { text, uri, variant, input, expected } and { text, uri, variant, input, expected } |
| `hash/haraka.js` | 2 / 2 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/haval.js` | 19 / 19 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `hash/highway-hash.js` | 24 / 24 | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type |
| `hash/isap-hash.js` | 6 / 6 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/jh.js` | 28 / 28 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `hash/kangaroo.js` | 21 / 21 | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, expected } and { text, uri, input, personalization, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, expected } and { text, uri, input, personalization, outputSize, expected } |
| `hash/keccak.js` | 16 / 16 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/knot-hash.js` | 36 / 36 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `hash/kupyna.js` | 12 / 12 | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotR64n' has no supported JSDoc type |
| `hash/lsh.js` | 20 / 20 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `hash/luffa.js` | 20 / 20 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/md.js` | 13 / 13 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/mdc2.js` | 2 / 2 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `hash/murmurhash3.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `hash/panama.js` | 12 / 12 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `hash/parallelhash.js` | 8 / 8 | refused: PLY1105: 'test' holds both { text, uri, input, blockSize, customization, outputSize, expected } and { text, uri, input, blockSize, customization, outputSize, xofMode, expected } | refused: PLY1105: 'test' holds both { text, uri, input, blockSize, customization, outputSize, expected } and { text, uri, input, blockSize, customization, outputSize, xofMode, expected } |
| `hash/photon-beetle-hash.js` | 7 / 7 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `hash/radiogatun.js` | 3 / 3 | refused: PLY1102: 'an assignment inside an expression' is not supported yet | refused: PLY1102: 'an assignment inside an expression' is not supported yet |
| `hash/ripemd.js` | 23 / 23 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `hash/sha1.js` | 5 / 5 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/sha256.js` | 6 / 6 | 6 / 6 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `hash/sha3.js` | 15 / 15 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/sha512.js` | 12 / 12 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/shabal.js` | 29 / 29 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/shake.js` | 7 / 7 | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint32[] list is expected' is not supported yet |
| `hash/siphash.js` | 7 / 7 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `hash/skein.js` | 6 / 6 | refused: PLY1102: 'the value of OpCodes.ShiftLn(1n, 62), a call that only throws' is not supported yet | refused: PLY1102: 'the value of OpCodes.ShiftLn(1n, 62), a call that only throws' is not supported yet |
| `hash/skinny-hash.js` | 12 / 12 | refused: PLY0100: ill-typed IR in 'SKINNYtk3HashInstance.Feed': the arguments of the call of '$step0' do not match its parameters | refused: PLY0100: ill-typed IR in 'SKINNYtk3HashInstance.Feed': the arguments of the call of '$step0' do not match its parameters |
| `hash/sm3.js` | 6 / 6 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/sparkle-hash.js` | 7 / 7 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `hash/streebog.js` | 16 / 16 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/subterranean-hash.js` | 6 / 6 | refused: PLY1102: 'a call of OpCodes.Or32' is not supported yet | refused: PLY1102: 'a call of OpCodes.Or32' is not supported yet |
| `hash/tiger.js` | 14 / 14 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/tuplehash.js` | 10 / 10 | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, customization, tuples, expected } and { text, uri, input, outputSize, xofMode, customization, tuples, expected } | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, customization, tuples, expected } and { text, uri, input, outputSize, xofMode, customization, tuples, expected } |
| `hash/whirlpool.js` | 10 / 10 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `hash/xoodyak-hash.js` | 10 / 10 | 10 / 10 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `hash/xxhash.js` | 17 / 17 | refused: PLY1105: '_acc3' holds both bigint and number | refused: PLY1105: '_acc3' holds both bigint and number |
| `hash/xxhash3.js` | 30 / 30 | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, expected } and { text, uri, input, seed, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, outputSize, expected } and { text, uri, input, seed, outputSize, expected } |
| `hash/xxhash32.js` | 16 / 16 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `kdf/argon2.js` | 9 / 9 | refused: PLY1102: 'BigInt of string that is no proven integer' is not supported yet | refused: PLY1102: 'BigInt of string that is no proven integer' is not supported yet |
| `kdf/balloon.js` | 9 / 9 | refused: PLY1102: 'typeof of module, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of module, whose kind is not known' is not supported yet |
| `kdf/bcrypt-pbkdf.js` | 7 / 7 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/bcrypt.js` | 15 / 15 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `kdf/concat-kdf.js` | 6 / 6 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/hkdf.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/kdf1.js` | 10 / 10 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/kdf2.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/pbkdf1.js` | 11 / 11 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/pbkdf2.js` | 8 / 8 | refused: PLY1105: '_hashFunction' holds both string and number | refused: PLY1105: '_hashFunction' holds both string and number |
| `kdf/scrypt.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/sp800-108-counter.js` | 10 / 10 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/sp800-108-feedback.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/sp800-108-pipeline.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/sp800-56a.js` | 15 / 15 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `kdf/sp800-56c.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/tls-prf.js` | 18 / 18 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `kdf/x963kdf.js` | 7 / 7 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `mac/blake2bmac.js` | 9 / 9 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `mac/blake3mac.js` | 9 / 9 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, outputSize, expected } |
| `mac/cbcmac.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, key, macSize, expected } and { text, uri, input, key, iv, macSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, macSize, expected } and { text, uri, input, key, iv, macSize, expected } |
| `mac/cfbmac.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `mac/cmac.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `mac/dmac.js` | 1 / 1 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `mac/dstu7624mac.js` | 1 / 1 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `mac/f9.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `mac/gmac.js` | 5 / 5 | refused: PLY1102: 'BigInt of number that is no proven integer' is not supported yet | refused: PLY1102: 'BigInt of number that is no proven integer' is not supported yet |
| `mac/gost28147mac.js` | 1 / 1 | refused: PLY1102: 'a call of Array.from' is not supported yet | refused: PLY1102: 'a call of Array.from' is not supported yet |
| `mac/hmac-sha256.js` | 6 / 6 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `mac/hmac.js` | 2 / 2 | refused: PLY1105: '_hashFunction' holds both string and number | refused: PLY1105: '_hashFunction' holds both string and number |
| `mac/iso9797alg3.js` | 1 / 1 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `mac/kmac.js` | 8 / 8 | refused: PLY1102: 'result.unshift(OpCodes.And32(value, 0xFF)) on a list' is not supported yet | refused: PLY1102: 'result.unshift(OpCodes.And32(value, 0xFF)) on a list' is not supported yet |
| `mac/omac.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `mac/pelican.js` | 5 / 5 | refused: PLY1102: 'a statement at the top level of the module' is not supported yet | refused: PLY1102: 'a statement at the top level of the module' is not supported yet |
| `mac/pmac.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `mac/poly1305.js` | 1 / 1 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `mac/siphash128.js` | 7 / 7 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `mac/skeinmac512.js` | 9 / 9 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `mac/ttmac.js` | 8 / 8 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `mac/vmac.js` | 16 / 16 | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'ShiftLn' has no supported JSDoc type |
| `mac/vmpcmac.js` | 1 / 1 | refused: PLY1102: 'Array.isArray of uint8[], which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[], which JSDoc may have meant as an Array' is not supported yet |
| `mac/x919-mac.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `mac/xcbc.js` | 5 / 5 | refused: PLY1102: 'uint8 list list where uint8[] list is expected' is not supported yet | refused: PLY1102: 'uint8 list list where uint8[] list is expected' is not supported yet |
| `mac/zuc128mac.js` | 4 / 4 | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet | refused: PLY1102: 'typeof of require, whose kind is not known' is not supported yet |
| `modes/cbc.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/ccm.js` | 0 / 3 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/cfb.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/cmc.js` | 0 / 1 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/ctr.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/cts.js` | 0 / 6 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/eax.js` | 0 / 5 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/ecb.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/ede.js` | 0 / 2 | translated; no known-answer test a translation can be checked on | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `modes/eee.js` | 0 / 3 | translated; no known-answer test a translation can be checked on | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `modes/eme.js` | 0 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `modes/f8.js` | 0 / 1 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/ffx.js` | 0 / 5 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/fpe.js` | 0 / 5 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/gcm-siv.js` | 0 / 8 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/gcm.js` | 0 / 5 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/ige.js` | 0 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/kw.js` | 0 / 6 | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet | refused: PLY1102: 'new Array of a length that is no proven integer, which is number' is not supported yet |
| `modes/kwp.js` | 0 / 2 | refused: PLY1102: 'concat of uint8[] with a list of number' is not supported yet | refused: PLY1102: 'concat of uint8[] with a list of number' is not supported yet |
| `modes/lrw.js` | 0 / 7 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/ocb.js` | 0 / 9 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/ofb.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/pcbc.js` | 0 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `modes/siv.js` | 0 / 2 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `modes/xex.js` | 0 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `modes/xts.js` | 0 / 5 | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet | refused: PLY1102: 'uint32[] where uint8[] is expected' is not supported yet |
| `padding/ansi-x923.js` | 3 / 3 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `padding/bit-padding.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `padding/iso10126.js` | 1 / 1 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `padding/iso7816-4.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `padding/no-padding.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `padding/oaep.js` | 1 / 1 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `padding/oneandzeros.js` | 3 / 3 | 3 / 3 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `padding/pkcs.js` | 8 / 8 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `padding/pss.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `padding/random.js` | 1 / 1 | refused: PLY1102: 'new RangeError' is not supported yet | refused: PLY1102: 'new RangeError' is not supported yet |
| `padding/zero.js` | 3 / 3 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `permutation/simpira-v2.js` | 6 / 6 | refused: PLY1102: 'float64[] where int32 list is expected' is not supported yet | refused: PLY1102: 'float64[] where int32 list is expected' is not supported yet |
| `pqc/ml-kem.js` | 2 / 2 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `random/acrng.js` | 6 / 6 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ars.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `random/biski64.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/blum-micali.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/blumblumshub.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/chacha.js` | 8 / 8 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/cmwc.js` | 5 / 5 | refused: PLY1105: '_carry' holds both bigint and number | refused: PLY1105: '_carry' holds both bigint and number |
| `random/combined-lcg.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/dsfmt.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize } and { text, uri, input, seed, skipBytes, outputSize } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize } and { text, uri, input, seed, skipBytes, outputSize } |
| `random/fcsr.js` | 7 / 7 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/fortuna.js` | 2 / 2 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/gfsr.js` | 6 / 6 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/icg.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/isaac.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/jsf.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/kiss.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/knuth-b.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } |
| `random/lagged-fibonacci.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/lcg.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, seed, multiplier, increment, modulo, outputSize, expected } and { text, uri, input, seed, multiplier, increment, modulo, count, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, multiplier, increment, modulo, outputSize, expected } and { text, uri, input, seed, multiplier, increment, modulo, count, outputSize, expected } |
| `random/lehmer.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } |
| `random/lehmer128.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, state, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, state, outputSize, expected } |
| `random/lehmer64.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, state, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, state, outputSize, expected } |
| `random/lfib4.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/lfsr.js` | 7 / 7 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/lfsr113.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/lfsr258.js` | 6 / 6 | refused: PLY1105: '_z0' holds both bigint and number | refused: PLY1105: '_z0' holds both bigint and number |
| `random/lfsr88.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/mersenne-twister-64.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mersenne-twister.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/middle-square.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mixmax.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mlcg.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, seed, multiplier, modulo, outputSize, expected } and { text, uri, input, seed, multiplier, modulo, count, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, multiplier, modulo, outputSize, expected } and { text, uri, input, seed, multiplier, modulo, count, outputSize, expected } |
| `random/mrg32k3a.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/msws.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mulberry32.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mwc.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, multiplier, expected } and { text, uri, input, seed, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, multiplier, expected } and { text, uri, input, seed, outputSize, expected } |
| `random/mwc256.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/mwc64x.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/pcg-rxs-m-xs.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, sequence, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, sequence, outputSize, expected } |
| `random/pcg-xsh-rs.js` | 2 / 2 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/pcg-xsl-rr.js` | 1 / 1 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/pcg.js` | 1 / 1 | refused: PLY1105: '_state' holds both bigint and number | refused: PLY1105: '_state' holds both bigint and number |
| `random/pcg64-dxsm.js` | 1 / 1 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/philox.js` | 5 / 5 | refused: PLY1102: 'null' is not supported yet | refused: PLY1102: 'null' is not supported yet |
| `random/ran0.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, count, outputSize, expected } |
| `random/ran1.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ran2.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ran3.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ranlux.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ranlux24.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/ranlux48.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } |
| `random/ranshi.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/romu.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/romuduo.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/romumono.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/romuquad.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/self-shrinking-generator.js` | 5 / 5 | refused: PLY1105: 'expected' holds both uint8[] and uint8[] | refused: PLY1105: 'expected' holds both uint8[] and uint8[] |
| `random/sfc.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/sfc64.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, input, seed, skip, outputSize, expected } and { text, uri, input, seed, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, skip, outputSize, expected } and { text, uri, input, seed, outputSize, expected } |
| `random/sfmt.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } |
| `random/shioi128.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/shishua.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/splitmix32.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/splitmix64.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/squares.js` | 6 / 6 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `random/swb.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/tea-prng.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/threefry.js` | 3 / 3 | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type | refused: PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type |
| `random/tinymt.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, skipBytes, outputSize, expected } |
| `random/well.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/wichmann-hill.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/wyrand.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xor4096.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/xoroshiro128plus.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoroshiro128plusplus.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoroshiro128starstar.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoroshiro256plusplus.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoroshiro256starstar.js` | 4 / 4 | refused: PLY1105: '_s0' holds both bigint and number | refused: PLY1105: '_s0' holds both bigint and number |
| `random/xorshift-plus.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xorshift-star.js` | 6 / 6 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xorshift.js` | 5 / 5 | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } | refused: PLY1105: 'test' holds both { text, uri, input, seed, outputSize, expected } and { text, uri, input, seed, outputSize, skip, expected } |
| `random/xorshift1024star.js` | 6 / 6 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xorshift32.js` | 6 / 6 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xorshift64.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xorwow.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoshiro-plusplus.js` | 7 / 7 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoshiro128starstar.js` | 4 / 4 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoshiro256plus.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xoshiro256starstar.js` | 3 / 3 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/xxhash32.js` | 5 / 5 | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet | refused: PLY1102: 'Array.isArray of uint8[] or null, which JSDoc may have meant as an Array' is not supported yet |
| `random/yarrow.js` | 1 / 1 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `special/3way.js` | 2 / 2 | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'int32[] where uint8 list is expected' is not supported yet |
| `special/aes-siv.js` | 2 / 2 | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8 list is expected' is not supported yet |
| `special/fiat-shamir.js` | 1 / 1 | refused: PLY1102: 'null' is not supported yet | refused: PLY1102: 'null' is not supported yet |
| `special/fish.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `special/gimli.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `special/hotp.js` | 5 / 5 | refused: PLY1105: '_counter' holds both number and bigint | refused: PLY1105: '_counter' holds both number and bigint |
| `special/rc2wrap.js` | 1 / 1 | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where int32 list is expected' is not supported yet |
| `special/shamir-secret-sharing.js` | 2 / 2 | refused: PLY1102: 'int32 list where float64 list is expected' is not supported yet | refused: PLY1102: 'int32 list where float64 list is expected' is not supported yet |
| `special/time-lock-puzzle.js` | 1 / 1 | refused: PLY1102: 'null' is not supported yet | refused: PLY1102: 'null' is not supported yet |
| `special/totp.js` | 5 / 5 | refused: PLY1102: 'a call of Date.now' is not supported yet | refused: PLY1102: 'a call of Date.now' is not supported yet |
| `special/xchacha20-poly1305.js` | 2 / 2 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/a5.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/achterbahn.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/acorn.js` | 3 / 3 | refused: PLY1102: 'an assignment inside an expression' is not supported yet | refused: PLY1102: 'an assignment inside an expression' is not supported yet |
| `stream/aegis-128.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/aes-gcm-siv.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/beth-piper.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/caracachs.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/chacha20.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/crypto1.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-a5.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/darkcrypt-chacha8.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, expected } and { text, uri, input, key, nonce, expected } | refused: PLY1105: 'test' holds both { text, uri, key, nonce, input, expected } and { text, uri, input, key, nonce, expected } |
| `stream/darkcrypt-cryptmt3.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `stream/darkcrypt-decim.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `stream/darkcrypt-dicing.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-dragon.js` | 4 / 4 | refused: PLY1102: 'a call of kwords.slice(0, 8).concat(KxorIV.slice(0, 8)).concat(notKxorIV.slice(0, 8)).concat' is not supported yet | refused: PLY1102: 'a call of kwords.slice(0, 8).concat(KxorIV.slice(0, 8)).concat(notKxorIV.slice(0, 8)).concat' is not supported yet |
| `stream/darkcrypt-edon80.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-ffcsr.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-fubuki.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-hermes.js` | 3 / 3 | refused: PLY1102: '>>> on bigint and number' is not supported yet | refused: PLY1102: '>>> on bigint and number' is not supported yet |
| `stream/darkcrypt-isaac.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-konton.js` | 2 / 2 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `stream/darkcrypt-lex2.js` | 3 / 3 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `stream/darkcrypt-lili.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/darkcrypt-lili2.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-mdc.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-mickey.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-mir.js` | 3 / 3 | refused: PLY1103: the result of 'OpCodes.ShiftLn(1n, 64) - 1n' has no bounded type | refused: PLY1103: the result of 'OpCodes.ShiftLn(1n, 64) - 1n' has no bounded type |
| `stream/darkcrypt-moustique.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-nls2.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-panama.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-pike.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/darkcrypt-pomaranch.js` | 3 / 3 | refused: PLY1102: 'boolean where float64 is expected' is not supported yet | refused: PLY1102: 'boolean where float64 is expected' is not supported yet |
| `stream/darkcrypt-py.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-py6.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-pypy.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-qcypher.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/darkcrypt-rabbit.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, key, input, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, key, input, expected } |
| `stream/darkcrypt-rc4drop.js` | 2 / 2 | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet |
| `stream/darkcrypt-scop.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-seal2.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-sfinks.js` | 3 / 3 | refused: PLY1102: 'GF_TABLES' is not supported yet | refused: PLY1102: 'GF_TABLES' is not supported yet |
| `stream/darkcrypt-shannon.js` | 3 / 3 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `stream/darkcrypt-sn3.js` | 2 / 2 | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet | refused: PLY1102: 'int32[] where float64[] is expected' is not supported yet |
| `stream/darkcrypt-snow2.js` | 3 / 3 | refused: PLY1102: 'int32 where uint8 is expected' is not supported yet | refused: PLY1102: 'int32 where uint8 is expected' is not supported yet |
| `stream/darkcrypt-sosemanuk.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-sss.js` | 3 / 3 | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, key, iv, input, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-tpy.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-tpy6.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-tpypy.js` | 2 / 2 | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, iv, input, key, expected } and { text, uri, input, key, iv, expected } |
| `stream/darkcrypt-trivium.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `stream/darkcrypt-turing.js` | 3 / 3 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/darkcrypt-wake.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-wg.js` | 3 / 3 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/darkcrypt-yamb.js` | 3 / 3 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `stream/darkcrypt-zkcrypt3.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/dragon.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/e0.js` | 1 / 1 | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet | refused: PLY1102: 'float64[] where uint8[] list is expected' is not supported yet |
| `stream/e2.js` | 1 / 1 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/f-fcsr.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/geffe.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/grain.js` | 5 / 5 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/hc.js` | 5 / 5 | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet | refused: PLY1102: 'int32 where uint32 is expected' is not supported yet |
| `stream/leviathan.js` | 2 / 2 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/mickey.js` | 2 / 2 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/miller.js` | 1 / 1 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `stream/morus.js` | 2 / 2 | refused: PLY1102: 'a call of Object.freeze' is not supported yet | refused: PLY1102: 'a call of Object.freeze' is not supported yet |
| `stream/mugi.js` | 1 / 1 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/norx.js` | 4 / 4 | refused: PLY1102: 'a property added outside the constructor' is not supported yet | refused: PLY1102: 'a property added outside the constructor' is not supported yet |
| `stream/phelix-simple.js` | 4 / 4 | 4 / 4 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/pike.js` | 4 / 4 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/pomaranch.js` | 4 / 4 | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet | refused: PLY1102: 'uint8 list where uint32 list is expected' is not supported yet |
| `stream/rabbit.js` | 6 / 6 | refused: PLY1105: 'test' holds both { text, uri, key, input, expected } and { text, uri, key, iv, input, expected } | refused: PLY1105: 'test' holds both { text, uri, key, input, expected } and { text, uri, key, iv, input, expected } |
| `stream/rc4.js` | 3 / 3 | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet |
| `stream/rule30.js` | 2 / 2 | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint32[] is expected' is not supported yet |
| `stream/salsa20.js` | 2 / 2 | refused: PLY1102: 'an assignment inside an expression' is not supported yet | refused: PLY1102: 'an assignment inside an expression' is not supported yet |
| `stream/seal.js` | 3 / 3 | refused: PLY1102: 'this.tests.concat of a list' is not supported yet | refused: PLY1102: 'this.tests.concat of a list' is not supported yet |
| `stream/shrinking-generator.js` | 3 / 3 | refused: PLY1105: 'isInverse' holds both boolean and number | refused: PLY1105: 'isInverse' holds both boolean and number |
| `stream/snow3g.js` | 4 / 4 | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet |
| `stream/sober128.js` | 4 / 4 | refused: PLY1105: 'test' holds both { text, uri, input, key, iv, expected } and { text, uri, input, key, iv, expected } | refused: PLY1105: 'test' holds both { text, uri, input, key, iv, expected } and { text, uri, input, key, iv, expected } |
| `stream/sosemanuk.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/spritz.js` | 1 / 1 | refused: PLY1102: 'float64 where int32 is expected' is not supported yet | refused: PLY1102: 'float64 where int32 is expected' is not supported yet |
| `stream/trivium.js` | 2 / 2 | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet | refused: PLY1102: 'float64 where uint32 is expected' is not supported yet |
| `stream/tsc-4.js` | 2 / 2 | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet | refused: PLY1102: 'float64 where uint8 is expected' is not supported yet |
| `stream/vest.js` | 27 / 27 | refused: PLY1105: 'test' holds both { text, uri, key, input, expected } and { text, uri, key, iv, input, expected } | refused: PLY1105: 'test' holds both { text, uri, key, input, expected } and { text, uri, key, iv, input, expected } |
| `stream/vmpc.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/vmpcksa3.js` | 6 / 6 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/wake.js` | 2 / 2 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
| `stream/xchacha20.js` | 5 / 5 | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet | refused: PLY1102: 'uint8[] where uint8 list is expected' is not supported yet |
| `stream/xsalsa20.js` | 5 / 5 | 5 / 5 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/zuc.js` | 2 / 2 | 2 / 2 pass | refused: PLY1550: 'a global of the type list<Algorithm>' cannot be expressed in Rust profile 'rust2024' (in the global 'Algorithms') |
| `stream/zuc256.js` | 2 / 2 | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet | refused: PLY1102: 'uint32 where uint8 is expected' is not supported yet |
