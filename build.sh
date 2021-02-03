#!/bin/bash
if test "$OS" = "Windows_NT"
then
  # use .Net

  .paket/paket.bootstrapper.exe
  exit_code=$?
  if [ $exit_code -ne 0 ]; then
  	exit $exit_code
  fi

  .paket/paket.exe restore
  exit_code=$?
  if [ $exit_code -ne 0 ]; then
  	exit $exit_code
  fi

  packages/FAKE/tools/FAKE.exe $@ --fsiargs build.fsx
else
  # use mono
#   mono .paket/paket.bootstrapper.exe
#   exit_code=$?
#   if [ $exit_code -ne 0 ]; then
#   	exit $exit_code
#   fi

  dotnet paket restore
  exit_code=$?
  if [ $exit_code -ne 0 ]; then
  	exit $exit_code
  fi
  dotnet paket generate-load-scripts --type fsx -f net5.0 -v
  dotnet fsi build.fsx
fi
