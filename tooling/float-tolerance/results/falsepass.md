### Kernel A: f32 accumulation performed in f64

| Comparison | vectors on which the wrong kernel is accepted | an 8-vector fixture passes | correct kernel rejected |
| --- | ---: | ---: | ---: |
| exact (option 1; option 3 `strict` and `accurate`) | 1948 of 10000 | 0.0002 % | 0 of 10000 |
| ulp 1 (option 2) | 5115 of 10000 | 0.4686 % | 0 of 10000 |
| ulp 4 (option 2) | 8407 of 10000 | 24.9533 % | 0 of 10000 |
| relative 1e-6 (option 2, option 3 `fast`) | 9443 of 10000 | 63.2237 % | 0 of 10000 |
| relative 1e-12 (option 2, option 3 `fast`) | 1948 of 10000 | 0.0002 % | 0 of 10000 |
| absolute 1e-6 (option 2) | 9200 of 10000 | 51.3219 % | 0 of 10000 |
| interval enclosing the exact real result (option 4) | 10000 of 10000 | 100.0000 % | 6244 of 10000 |

The wrong kernel's output differs from the correct one in 8052 of 10000 vectors.

### Kernel B: reassociated sum (a + b) + -a

| Comparison | vectors on which the wrong kernel is accepted | an 8-vector fixture passes | correct kernel rejected |
| --- | ---: | ---: | ---: |
| exact (option 1; option 3 `strict` and `accurate`) | 66 of 10000 | 0.0000 % | 0 of 10000 |
| ulp 1 (option 2) | 66 of 10000 | 0.0000 % | 0 of 10000 |
| ulp 4 (option 2) | 185 of 10000 | 0.0000 % | 0 of 10000 |
| relative 1e-6 (option 2, option 3 `fast`) | 4668 of 10000 | 0.2254 % | 0 of 10000 |
| relative 1e-12 (option 2, option 3 `fast`) | 1623 of 10000 | 0.0000 % | 0 of 10000 |
| absolute 1e-6 (option 2) | 4956 of 10000 | 0.3640 % | 0 of 10000 |
| interval enclosing the exact real result (option 4) | 10000 of 10000 | 100.0000 % | 9934 of 10000 |

The wrong kernel's output differs from the correct one in 9934 of 10000 vectors.

