# Aloe-lang: Codex collaboration memory

Updated: 2026-10-21. Source: the user's explicit request to record ComputerUse failure diagnosis and prevention.

- When an operation fails, investigate the cause, apply an authorized remedy, and verify the result before stopping at a symptom report.
- Distinguish failures in Codex's sandbox, proxy, permissions, or execution environment from failures in the user's PC, project, or external service. State only what the evidence establishes.
- If a sandbox restriction blocks necessary work, use the supported approval/escalation route when authorized. Do not bypass restrictions or wait for the user to suggest routine diagnosis.
- If work remains blocked after investigation, report the attempted remedies, the specific unresolved cause, and the concrete condition needed to continue.
- Before computer-use work, identify the platform and use the runtime explicitly prescribed by the applicable ComputerUse skill. Do not infer that a platform is unavailable from a different connector's disabled or empty API surface.
- If the selected connector returns no apps/windows or times out, first check its documented capabilities, configured runtime, and connection state. Treat an empty inventory as evidence about that connector's view only; do not conclude the user's app is closed or ask the user to foreground/restart it until the supported runtime has been checked.
- After an identical failure, change the diagnostic step instead of repeating the same call or reset. Verify the target with a fresh read-only inventory/state capture, then use only fresh window handles and state. Record which route failed and which route succeeded so later turns do not repeat the diagnosis.
- For Windows ComputerUse in this environment, the skill's supported route is `node_repl` with `@oai/sky`; `cua_repl` may expose browser-only capabilities and must not be used to infer that native Windows apps are missing.
