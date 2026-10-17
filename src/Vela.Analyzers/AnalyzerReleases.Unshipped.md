; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VELA020 | Vela.Usage | Error | MissingFilterAnalyzer
VELA021 | Vela.Usage | Warning | ImageProjectionGenerator
VELA022 | Vela.Reliability | Warning | SetStateStepAnalyzer
VELA023 | Vela.Usage | Error | TypedTriggerRequiredAnalyzer
VELA024 | Vela.Reliability | Warning | ParallelExecutionAnalyzer
VELA025 | Vela.Reliability | Warning | IpAddressCallAnalyzer
VELA026 | Vela.Reliability | Warning | HttpClientTimeoutAnalyzer
VELA027 | Vela.Reliability | Warning | EmptyCatchAnalyzer

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VELA006 | Vela.Usage | Warning | PreferTypedMessageAnalyzer
