#requires -Version 7.4
#requires -PSEdition Core

& (Join-Path $PSScriptRoot "publish.ps1") @args
exit $LASTEXITCODE
