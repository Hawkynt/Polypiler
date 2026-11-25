@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "result=3"
call :assignmentChain
if %errorlevel% NEQ 7 goto failure
if %result% NEQ 3 goto failure
call :transferChain 42
if %errorlevel% NEQ 7 goto failure
if %result% NEQ 42 goto failure
call :explicit
if %errorlevel% NEQ -3 goto failure
if %result% NEQ 42 goto failure
exit /b %result%

:assignmentChain
setlocal EnableExtensions DisableDelayedExpansion
set "result=17"
call :seed
set result=%result% && endlocal && goto :eof
exit /b 91

:transferChain
setlocal EnableExtensions DisableDelayedExpansion
set "result=%1"
call :seed
endlocal & set "result=%result%"
goto :eof

:explicit
setlocal EnableExtensions DisableDelayedExpansion
set "result=19"
exit /b -3

:seed
exit /b 7

:failure
exit /b 99
