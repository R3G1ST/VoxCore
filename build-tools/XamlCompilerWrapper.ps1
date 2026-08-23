$toolsDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk.winui\2.3.6\tools\net472"
$compilerExe = Join-Path $toolsDir "XamlCompiler.exe"

# Set en-US culture before launch
$originalUICulture = [System.Globalization.CultureInfo]::CurrentUICulture
$originalCulture = [System.Globalization.CultureInfo]::CurrentCulture

[System.Globalization.CultureInfo]::DefaultThreadCurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
[System.Globalization.CultureInfo]::DefaultThreadCurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")

# Launch XamlCompiler.exe with args passed to this script
$argsStr = $args -join ' '
& $compilerExe $argsStr

exit $LASTEXITCODE
