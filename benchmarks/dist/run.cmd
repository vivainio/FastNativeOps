@echo off
rem Runs the FastNativeOps vs System.IO benchmarks (needs the .NET 8+ runtime). Runs in the current directory.
rem   run.cmd                      all benchmarks, short job
rem   run.cmd --job default        full run
rem   run.cmd --dir D:\tmp         create test files there
dotnet "%~dp0Bench.dll" %*
