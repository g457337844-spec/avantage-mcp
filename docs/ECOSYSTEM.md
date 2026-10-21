# Related projects and boundaries

These are three separate tools, not one merged codebase:

| Repository | Role | Expected integration |
| --- | --- | --- |
| [origin-spectra-mcp](https://github.com/g457337844-spec/origin-spectra-mcp) | General Origin automation and editable XPS/XRD plot workflows | Main MCP option for scripting Origin |
| [plotter](https://github.com/g457337844-spec/plotter) | Windows GUI and optional MCP interface for plotting | Standalone alternative for interactive use |
| [avantage-mcp](https://github.com/g457337844-spec/avantage-mcp) | Native Avantage peak fitting and spectrum export | May export curves as CSV for subsequent plotting in Origin |

## Typical scientific workflow

1. Process and fit spectra in a licensed Thermo Scientific Avantage installation using **avantage-mcp**, or prepare already-fitted spectra independently.
2. Export intensity/background/component curves as CSV, where supported.
3. Use **origin-spectra-mcp** or **plotter** to produce editable figures in a licensed Origin/OriginPro installation.
4. Inspect peak assignment and scientific conclusions manually. Plotting does not establish chemical identity and numerical convergence does not establish a valid peak model.

The three repositories have different runtimes and vendor dependencies. None requires installing all three. Cross-project descriptions are not a claim of a tested end-to-end integration.

## Attribution and vendor dependencies

**origin-spectra-mcp** includes code derived from [Ge-Shun/origin-mcp](https://github.com/Ge-Shun/origin-mcp); preserve upstream license and attribution. Origin and Avantage are proprietary third-party applications, not included with these projects. The repositories are independent integrations and are not affiliated with or endorsed by the vendors.

## Publishing status

This document is an architecture description prepared in a private review branch. Public links may not resolve until each repository has been sanitized, licensed appropriately and deliberately published.
