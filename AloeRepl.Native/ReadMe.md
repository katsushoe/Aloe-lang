# AloeRepl.Native

Native console host for Aloe REPL.

## Run with .NET

```text
dotnet run --project AloeRepl.Native
```

## Publish as a native executable (NativeAOT)

Example for Windows x64:

```text
dotnet publish AloeRepl.Native -c Release -r win-x64 /p:PublishAot=true
```

Examples for other RIDs include `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

The project itself stays a normal `net8.0` console project so development does not require NativeAOT on every build; NativeAOT is selected at publish time.
