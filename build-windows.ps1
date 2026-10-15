$ErrorActionPreference = 'Stop'

dotnet restore .\EventDeck.csproj
dotnet publish .\EventDeck.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\publish\win-x64

Write-Host "Build lista: $((Resolve-Path .\publish\win-x64).Path)\EventDeck.exe"
