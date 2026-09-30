@echo off
rem Runs the FastNativeOps vs System.IO benchmarks. No .NET installation needed.
rem   run.cmd                      all benchmarks, short job
rem   run.cmd --job default        full run
rem   set FASTNATIVEOPS_BENCH_DIR=D:\tmp
"%~dp0Bench.exe" %*
