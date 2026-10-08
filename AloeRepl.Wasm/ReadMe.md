# AloeRepl.Wasm

Browser-hosted Aloe REPL written in C# with Blazor WebAssembly.

The browser host and native host use the same `AloeRepl.Core` session engine.

## Run

A .NET 8 SDK with the WebAssembly workload is required.

```text
dotnet workload install wasm-tools
dotnet run --project AloeRepl.Wasm
```

The application contains no custom JavaScript execution engine. Aloe parsing, compilation, REPL state management, and VM execution remain C# code running under .NET WebAssembly. The only HTML/JS bootstrapping is the standard Blazor WebAssembly host.
