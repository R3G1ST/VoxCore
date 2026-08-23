[System.Threading.Thread]::CurrentThread.CurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
[System.Globalization.CultureInfo]::DefaultThreadCurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
[System.Globalization.CultureInfo]::DefaultThreadCurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
& dotnet build "C:\Users\R3G1S\OneDrive\Документы\Default Project\VoxCore\VoxCore.Client\VoxCore.Client.csproj" -p:Platform=x64 -c Release
