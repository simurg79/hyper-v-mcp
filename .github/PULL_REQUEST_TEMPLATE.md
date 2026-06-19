<!--
Thanks for your contribution! Please fill out the sections below.
For anything security-related, do NOT open a public PR or issue — follow
SECURITY.md and use the repository's Security tab instead.
-->

## Summary

<!-- Describe the change and the motivation behind it. -->

## Linked issue(s)

<!-- e.g. Closes #123. Use "Closes #" to auto-close on merge, or "Refs #". -->

Closes #

## Type of change

<!-- Check all that apply; mirrors the branch-name prefixes. -->

- [ ] `feat/` — new functionality
- [ ] `fix/` — bug fix
- [ ] `docs/` — documentation only
- [ ] `chore/` — tooling, build, or housekeeping

## Testing

- [ ] `dotnet build` passes locally.
- [ ] The host-independent test suite passes:
      `dotnet test --filter "Category!=RequiresHyperV&Category!=LiveE2E"`
- [ ] I ran the Hyper-V-dependent tests (`Category=RequiresHyperV` and/or
      `Category=LiveE2E`) on an elevated Hyper-V host.
- [ ] N/A — this change does not require the Hyper-V-dependent tests.

<!--
The RequiresHyperV and LiveE2E categories need an elevated (administrator)
process on a Windows host with the Hyper-V role enabled.
-->

## Checklist

- [ ] I added or updated tests for any behavior change.
- [ ] I confirmed no secrets, passwords, or credentials are included in this
      change (including in tests, logs, or fixtures).
- [ ] I understand that security issues go through
      [SECURITY.md](../blob/main/SECURITY.md), not public PRs or issues.
