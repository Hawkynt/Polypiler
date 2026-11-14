@echo off
rem fake-tool <text> [out|err] [seconds] [exit code]: waits the seconds, prints the text on standard
rem output or standard error and exits with the code, as a toolchain's version command would.
if not "%~3"=="" if not "%~3"=="0" powershell -NoProfile -NonInteractive -Command "Start-Sleep -Milliseconds ([int]([double]'%~3' * 1000))"
if "%~2"=="err" (1>&2 echo %~1) else (echo %~1)
if "%~4"=="" exit /b 0
exit /b %~4
