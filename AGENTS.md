# AGENTS.md

## Review guidelines

Review for:
- Architecture boundary violations, including forbidden imports or layer crossings.
- Dead code, unreachable code, unused config, unused feature flags, and unused helpers.
- Duplicated logic that should be centralized, or premature abstractions that are not reused.
- Broad catch-all fallbacks, silent exception swallowing, fake compatibility paths, and “just in case” code.
- Generated-looking comments that restate the code instead of explaining intent.
- Tests that only assert mocks, snapshots, or implementation details without behavior coverage.
- Large functions, ambiguous names, hidden side effects, and avoidable cognitive complexity.

Do not approve changes that:
- Add a fallback without naming the exact failure mode, telemetry, test, and owner.
- Add a new abstraction used by only one call site unless there is a documented reason.
- Preserve obsolete behavior only because the previous code did.
- Increase dependency direction violations between modules.

Validation:
- Run: <lint command>
- Run: <typecheck command>
- Run: <unit/integration test command>
- Run architecture checks: <dependency-cruiser / ArchUnit / NDepend / custom script>