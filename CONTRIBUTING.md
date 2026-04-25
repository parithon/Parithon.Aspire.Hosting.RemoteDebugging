# Contributing

Thank you for your interest in contributing! This document covers how to build, test, and submit changes.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (version pinned in `global.json`)
- [Git](https://git-scm.com/)
- SSH access to a Windows or Linux remote host (optional, needed for integration testing)

---

## Getting Started

```bash
git clone https://github.com/parithon/Aspire.Hosting.RemoteDebugging.git
cd Aspire.Hosting.RemoteDebugging
dotnet restore
dotnet build
dotnet test tests/Aspire.Hosting.RemoteDebugging.Tests
```

---

## Project Structure

```
src/
  Aspire.Hosting.RemoteDebugging/          # Main library
  Aspire.Hosting.RemoteDebugging.Sidecar/  # Remote sidecar agent (gRPC)
samples/
  Sample.AppHost/                          # Example Aspire AppHost
  Sample.WorkerApp/                        # Example .NET Worker deployed remotely
tests/
  Aspire.Hosting.RemoteDebugging.Tests/    # Unit and integration tests
```

---

## Branch Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Latest release — do not target directly |
| `develop` | Integration branch — all PRs target this |
| `feature/*` | Feature branches cut from `develop` |

**Always branch from and PR into `develop`.**

---

## Workflow

This project follows a **TDD-first** model:

1. Open (or pick) an issue
2. Create a feature branch from `develop`: `git checkout -b feature/my-feature develop`
3. Write **failing tests first** (red)
4. Implement the minimum code to pass (green)
5. Refactor with all tests green
6. Open a PR targeting `develop`

---

## Coding Standards

- Target `net10.0`; use C# 13 features where appropriate
- Enable nullable reference types (`<Nullable>enable</Nullable>`) — no `#nullable disable`
- Use `async`/`await` for all I/O-bound operations
- Internal helpers that need test coverage must be exposed via `InternalsVisibleTo` in the `.csproj`, not made public
- Comment only code that genuinely needs clarification; avoid noise comments

### Naming conventions

- Test methods: `MethodName_Condition_ExpectedResult()` (MSTest + FluentAssertions)
- Annotations: suffix with `Annotation` (e.g., `LoggingSupportAnnotation`)
- Extension methods: grouped in `*ResourceExtensions.cs` files

---

## Tests

Run the full test suite:

```bash
dotnet test tests/Aspire.Hosting.RemoteDebugging.Tests
```

- Framework: **MSTest** + **FluentAssertions** + **Moq**
- Minimum coverage target: **85%** for domain and application layers
- Tests live alongside the code they cover under `tests/Aspire.Hosting.RemoteDebugging.Tests/`

---

## Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(windows-service): add WithLoggingSupport for log file tailing
fix(transport): handle SSH reconnect on timeout
docs: update README with Windows Service example
ci: add release workflow for GitHub Packages
```

Common scopes: `transport`, `sidecar`, `windows-service`, `remote-project`, `otel`, `ci`, `docs`.

---

## Pull Requests

- One PR per issue / milestone — keep scope tight
- Include a brief description of what changed and why
- Paste test output or a short verification snippet in the PR description
- Ensure `ci.yml` is green before requesting review

---

## Releasing

Releases are automated via the `release.yml` workflow. Maintainers tag a version on `main` after merging from `develop`:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The workflow builds, tests, packs, publishes to GitHub Packages, and creates a GitHub Release automatically. Versioning is driven by [MinVer](https://github.com/adamralph/minver) from git tags.

---

## Reporting Issues

Please open a [GitHub Issue](https://github.com/parithon/Aspire.Hosting.RemoteDebugging/issues) and include:

- .NET SDK version (`dotnet --version`)
- Aspire version
- Remote host OS and version
- Minimal reproduction steps or a sample AppHost snippet
