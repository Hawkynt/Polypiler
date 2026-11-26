@Layer.core
Feature: Select a container that shares the DOS signature

  Scenario: The PE marker beyond the codec prefix selects PE
    Given MZ bytes with PE signature at 0x78 and registered pe and mz codecs
    When the registry automatically selects a container
    Then it selects the pe codec

  Scenario: A DOS MZ file cannot fall through to a Windows codec
    Given the pinned DOS MZ bytes and a registered pe codec only
    When the registry automatically selects a container
    Then it reports PLY4059 naming MZ
