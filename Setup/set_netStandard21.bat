@ECHO OFF

::
:: set_netStandard21.bat --
::
:: Written by Joe Mistachkin.
:: Released to the public domain, use at your own risk!
::

IF NOT DEFINED ISNETFX2 (
  SET ISNETFX2=False
)

IF NOT DEFINED VCRUNTIME (
  SET VCRUNTIME=2019_VCU1
)

IF NOT DEFINED CONFIGURATION (
  SET CONFIGURATION=Release
)

IF NOT DEFINED PLATFORM (
  SET PLATFORM=Win32
)

IF NOT DEFINED PROCESSOR (
  SET PROCESSOR=x86
)

IF NOT DEFINED YEAR (
  SET YEAR=2019
)

IF NOT DEFINED FRAMEWORK (
  SET FRAMEWORK=netStandard21
)

:end_of_file
