@Layer.core @EndToEnd
Feature: Batch FOR L integer ranges

  Scenario: A descending loop sums its inclusive range
    Given a scalar Batch kernel initializes total to 0
    And the kernel runs FOR /L %%I in (5,-2,1) DO SET /A "total=total+%%I"
    When it is lifted in the cmd profile
    Then native cmd.exe and the translated kernel both return 9

  Scenario: A prohibited step is rejected
    Given a scalar Batch kernel runs FOR /L %%I in (1,0,3) DO SET /A "total=total+%%I"
    When it is lifted in the cmd profile
    Then it reports PLY2802 and emits no output
