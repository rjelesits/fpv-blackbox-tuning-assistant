# Beta workflow checks

Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project .\verification\BetaWorkflowChecks\BetaWorkflowChecks.csproj -- "C:\path\to\dump all.txt" ["C:\path\to\flight.BBL"]
```

The checks parse the real CLI snapshot passed on the command line, exercise the near-full-stick measurement gate using synthetic Acro CSV data, verify blocking conditions, exercise the two-view manual review gate, save and verify the exact CLI draft bytes (including rejecting a `save` command), and round-trip the recommendation/export audit record through the JSON report store. When a BBL path is supplied, they also run the bundled decoder and mode-specific analyzers against that log, verify its firmware and board against the CLI snapshot, and print the recommendation result for the two ACTUAL rate axes. They do not connect to or modify a flight controller.
