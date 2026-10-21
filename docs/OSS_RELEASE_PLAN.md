# Open-source release readiness — Avantage MCP

**Status: NOT APPROVED FOR PUBLICATION.** Prepared 2026-10-21 for an internal review. The existing GitHub repository remains private.

## Release architecture

A local Windows MCP adapter for Thermo Scientific Avantage spectrum processing and native peak fitting. Independent, unofficial software requiring a properly licensed Avantage installation.

Keep the three repositories separate; use [ECOSYSTEM.md](ECOSYSTEM.md) to document the connections. The proposed primary Codex for OSS application repository is `origin-spectra-mcp`, contingent on an actual clean public release and real maintenance evidence.

## Mandatory publication gate

1. Inventory **all committed files and commit history**, including deleted files, commit messages and tags. Current-file redaction does not erase prior snapshots.
2. Inspect for secrets, keys, tokens, passwords, absolute machine paths, local network addresses, usernames, datasets, identifiable samples and unpublished internal project notes. Rotate any credential that has been exposed.
3. Resolve license ownership, derivative-code attribution, asset licenses and proprietary software EULA obligations. Two sibling repositories do not presently include a LICENSE file.
4. Create a **new clean-history public export** from an audited allowlist. Do not simply change the visibility of this historical private repository. Include no Git history, binary artifacts, local configuration or generated logs from the private workspace.
5. Validate setup on a clean Windows environment, run tests, issue a real signed/tagged release if applicable, and keep an accurate test-status statement.
6. Review GitHub Actions security (minimal permissions, no untrusted pull-request command execution), maintainers, contributions, issue templates and security reporting.
7. Publish only after a file-by-file review and a provenance/licensing decision. Record public URLs and releases only once they actually exist.

## Project-specific issues

- Obtain a legal review of license/EULA restrictions before publishing any code that investigates undocumented vendor interfaces.
- Exclude internal coordination and machine-specific notes under `docs/` (especially DSH handoffs, local investigation and live-write research logs) from a public export.
- Review `native/inprocess/` experimental code separately; it is not a production feature.
- Never redistribute Thermo binaries, interop DLLs, license files, spectrum datasets, recovery snapshots or user results.
- Check absolute paths, COM interface names/IDs, SHA-256 checks, diagnostics and test fixtures before public release.

## Evidence for Codex for OSS

- Claim **primary maintainer** only if it accurately describes the applicant's role.
- Link the actual public repo, trustworthy release assets, recent substantive commits, CI and maintenance/issue discussions.
- Explain scientific interoperability and the work needed to review contributions, prevent destructive updates and keep Origin/Avantage compatibility.
- Never create fake stars, download counts, PRs or adoption claims.
- The program asks for the email on the applicant's ChatGPT account and an OpenAI Organization ID; neither is inferred from the GitHub profile.
- An application can be drafted in advance; submit it only after the linked repository is genuinely public and the applicant has reviewed the terms.
