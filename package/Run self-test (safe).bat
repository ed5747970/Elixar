@echo off
rem Safe self-test: works only in its own temp folder, measures your real items read-only,
rem opens the window with cleaning switched off, then saves selftest-report.txt here.
start "" "%~dp0Elixar.exe" --selftest
