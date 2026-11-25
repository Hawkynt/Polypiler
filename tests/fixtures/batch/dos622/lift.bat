@ECHO OFF
SET RESULT=00000000
GOTO middle
:abcdefgh1
SET RESULT=0000002A
GOTO finish
:middle
SET PAD=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
IF NOT "%RESULT%"=="00000000" GOTO finish
GOTO ABCDEFGH9
:abcdefgh2
SET RESULT=00000001
:finish
ECHO PLYRESULT=%RESULT%
