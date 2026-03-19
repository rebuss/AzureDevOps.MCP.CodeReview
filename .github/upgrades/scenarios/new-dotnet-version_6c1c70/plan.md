# .NET 10.0 Upgrade Plan — REBUSS.Pure

## Table of Contents

- [1. Executive Summary](#1-executive-summary)
- [2. Migration Strategy](#2-migration-strategy)
- [3. Detailed Dependency Analysis](#3-detailed-dependency-analysis)
- [4. Project-by-Project Plans](#4-project-by-project-plans)
  - [4.1 REBUSS.Pure](#41-rebusspure)
  - [4.2 REBUSS.Pure.Tests](#42-rebusspuretests)
- [5. Package Update Reference](#5-package-update-reference)
- [6. Breaking Changes Catalog](#6-breaking-changes-catalog)
- [7. Risk Management](#7-risk-management)
- [8. Testing & Validation Strategy](#8-testing--validation-strategy)
- [9. Complexity & Effort Assessment](#9-complexity--effort-assessment)
- [10. Source Control Strategy](#10-source-control-strategy)
- [11. Success Criteria](#11-success-criteria)

---

## 1. Executive Summary

### Scenario

Upgrade the REBUSS.Pure solution from **.NET 8.0** to **.NET 10.0 (LTS)**.

### Scope

| Metric | Value |
|---|---|
| Total projects | 2 |
| Total NuGet packages | 16 (11 need update) |
| Total code files | 131 |
| Total LOC | 14,331 |
| Files with issues | 23 |
| Total issues | 127 (3 mandatory, 1 source incompatible, 112 behavioral, 2 deprecated packages) |
| Estimated LOC to modify | 114+ (~0.8% of codebase) |

### Selected Strategy

**All-At-Once Strategy** — All projects upgraded simultaneously in a single operation.

**Rationale**:
- Only 2 projects (small solution)
- Simple, linear dependency: `REBUSS.Pure.Tests` → `REBUSS.Pure`
- Both currently on .NET 8.0 (homogeneous)
- Low external dependency complexity — all packages have known target versions
- No security vulnerabilities
- Estimated code impact is minimal (~0.8%)

### Complexity Classification

**Simple** — 2 projects, dependency depth 1, no circular dependencies, no high-risk items, no security vulnerabilities.

### Critical Issues

| Priority | Issue | Location | Impact |
|---|---|---|---|
| 🔴 Mandatory | `OptionsConfigurationServiceCollectionExtensions.Configure` binary incompatible | `Program.cs:149` | Recompilation required; method signature changed |
| 🟡 Source Incompatible | `TimeSpan.FromSeconds(double)` overload resolution change | `GitRemoteDetector.cs:118` | May fail to compile — ambiguous overload in .NET 10 |
| ⚠️ Deprecated | `Azure.Identity` 1.13.2 depends on deprecated MSAL | `REBUSS.Pure.csproj` | Update to 1.19.0 |
| ⚠️ Deprecated | `xunit` 2.9.3 (v2 is deprecated, only security fixes) | `REBUSS.Pure.Tests.csproj` | Keep on v2.9.3 — migration to v3 is a separate effort |

---

## 2. Migration Strategy

### Approach: All-At-Once

All projects are upgraded simultaneously in a single atomic operation. There are no intermediate states — both `REBUSS.Pure` and `REBUSS.Pure.Tests` move to `net10.0` together.

### Rationale

- With only 2 projects and a single dependency edge, incremental phasing provides no benefit.
- The solution is small enough to build and test entirely after the upgrade.
- All NuGet packages have known compatible versions for .NET 10.

### Dependency-Based Ordering

Even in an all-at-once upgrade, the logical order is:

1. **REBUSS.Pure** (Level 0 — foundation, no project dependencies)
2. **REBUSS.Pure.Tests** (Level 1 — depends on REBUSS.Pure)

Both are updated in the same operation, but if any per-file ordering is needed during compilation error fixing, start with `REBUSS.Pure` first.

---

## 3. Detailed Dependency Analysis

### Dependency Graph

```
REBUSS.Pure.Tests.csproj (net8.0 → net10.0)
  └── REBUSS.Pure.csproj (net8.0 → net10.0)
```

### Project Groupings

**Single group** — all projects upgraded atomically:

| Project | Level | Dependencies | Dependants |
|---|---|---|---|
| `REBUSS.Pure\REBUSS.Pure.csproj` | 0 (Foundation) | None | REBUSS.Pure.Tests |
| `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj` | 1 | REBUSS.Pure | None (top-level) |

### Critical Path

`REBUSS.Pure` → `REBUSS.Pure.Tests` (single linear path, no parallelism needed).

### Circular Dependencies

None.

---

## 4. Project-by-Project Plans

### 4.1 REBUSS.Pure

**Current State**

| Attribute | Value |
|---|---|
| Target Framework | `net8.0` |
| Project Type | DotNetCoreApp (Console App / MCP Server) |
| SDK-style | Yes |
| Files | 101 |
| LOC | 7,361 |
| NuGet Packages | 12 (9 need upgrade, 1 deprecated, 2 compatible) |
| API Issues | 1 binary incompatible, 1 source incompatible, 44 behavioral |
| Risk Level | Low |

**Target State**

| Attribute | Value |
|---|---|
| Target Framework | `net10.0` |
| Updated Packages | 10 |

**Migration Steps**

1. **Update TargetFramework**: Change `<TargetFramework>net8.0</TargetFramework>` to `<TargetFramework>net10.0</TargetFramework>` in `REBUSS.Pure\REBUSS.Pure.csproj`.

2. **Update Package References** (in `REBUSS.Pure\REBUSS.Pure.csproj`):

   | Package | Current | Target |
   |---|---|---|
   | Microsoft.Extensions.Configuration | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Configuration.EnvironmentVariables | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Configuration.Json | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.DependencyInjection | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Http | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Logging | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Logging.Console | 9.0.0 | 10.0.5 |
   | Microsoft.Extensions.Options.ConfigurationExtensions | 9.0.0 | 10.0.5 |
   | System.Text.Json | 9.0.0 | 10.0.5 |
   | Azure.Identity | 1.13.2 | 1.19.0 |

   **No change needed** (already compatible):
   - `Microsoft.Extensions.Http.Resilience` 10.4.0

3. **Address Breaking Changes** (see [§6 Breaking Changes Catalog](#6-breaking-changes-catalog)):
   - `Program.cs:149` — `services.Configure<T>(IConfiguration)` binary incompatible. Recompilation should resolve this since the method signature was updated in `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.5 and the source code calling pattern remains the same.
   - `GitRemoteDetector.cs:118` — `TimeSpan.FromSeconds(double)` source incompatible. In .NET 10, `TimeSpan.FromSeconds` has an `int` overload that makes the `double` overload ambiguous for integer literals. If the call uses a literal `5`, it may need an explicit cast or suffix: `TimeSpan.FromSeconds(5.0)` or `TimeSpan.FromSeconds((double)5)`.
   - `Program.cs:141` — `AddConsole(Action<ConsoleLoggerOptions>)` behavioral change. Verify the `LogToStandardErrorThreshold` property still exists and behaves the same.

4. **Validation**:
   - [ ] Project builds without errors
   - [ ] No new warnings introduced

---

### 4.2 REBUSS.Pure.Tests

**Current State**

| Attribute | Value |
|---|---|
| Target Framework | `net8.0` |
| Project Type | DotNetCoreApp (xUnit Test Project) |
| SDK-style | Yes |
| Files | 34 |
| LOC | 6,970 |
| NuGet Packages | 5 (1 deprecated, 4 compatible) |
| API Issues | 68 behavioral (mostly `JsonDocument`, `Uri` usage) |
| Risk Level | Low |

**Target State**

| Attribute | Value |
|---|---|
| Target Framework | `net10.0` |
| Updated Packages | 0 (all test packages are compatible or intentionally kept) |

**Migration Steps**

1. **Update TargetFramework**: Change `<TargetFramework>net8.0</TargetFramework>` to `<TargetFramework>net10.0</TargetFramework>` in `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj`.

2. **Package References** — No package version changes required:

   | Package | Current | Action |
   |---|---|---|
   | coverlet.collector | 6.0.4 | ✅ Compatible — no change |
   | Microsoft.NET.Test.Sdk | 17.14.1 | ✅ Compatible — no change |
   | NSubstitute | 5.3.0 | ✅ Compatible — no change |
   | xunit | 2.9.3 | ⚠️ Deprecated but functional — keep as-is (v3 migration is separate effort) |
   | xunit.runner.visualstudio | 3.1.4 | ✅ Compatible — no change |

3. **Address Breaking Changes**: No mandatory or source-incompatible issues in the test project. The 68 behavioral changes are all in `System.Text.Json.JsonDocument` and `System.Uri` categories — these are runtime behavioral changes that should be validated by running the test suite itself.

4. **Validation**:
   - [ ] Project builds without errors
   - [ ] All tests pass

---

## 5. Package Update Reference

### Common Package Updates (REBUSS.Pure — 10 packages)

| Package | Current | Target | Reason |
|---|---|---|---|
| Microsoft.Extensions.Configuration | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Configuration.Json | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.DependencyInjection | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Http | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Logging | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Logging.Console | 9.0.0 | 10.0.5 | Framework compatibility |
| Microsoft.Extensions.Options.ConfigurationExtensions | 9.0.0 | 10.0.5 | Framework compatibility |
| System.Text.Json | 9.0.0 | 10.0.5 | Framework compatibility |
| Azure.Identity | 1.13.2 | 1.19.0 | Deprecated — depends on deprecated MSAL version |

### Already-Compatible Packages (no change needed)

| Package | Version | Project |
|---|---|---|
| Microsoft.Extensions.Http.Resilience | 10.4.0 | REBUSS.Pure |
| coverlet.collector | 6.0.4 | REBUSS.Pure.Tests |
| Microsoft.NET.Test.Sdk | 17.14.1 | REBUSS.Pure.Tests |
| NSubstitute | 5.3.0 | REBUSS.Pure.Tests |
| xunit.runner.visualstudio | 3.1.4 | REBUSS.Pure.Tests |

### Deprecated Packages (kept as-is)

| Package | Version | Project | Reason to Keep |
|---|---|---|---|
| xunit | 2.9.3 | REBUSS.Pure.Tests | Functional for .NET 10; migration to v3 is a separate effort with its own breaking changes |

---

## 6. Breaking Changes Catalog

### 🔴 Binary Incompatible (1 issue)

#### `OptionsConfigurationServiceCollectionExtensions.Configure<T>(IServiceCollection, IConfiguration)`

- **File**: `REBUSS.Pure\Program.cs`
- **Line**: 149
- **Code**: `services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));`
- **Impact**: The method signature has changed in `Microsoft.Extensions.Options.ConfigurationExtensions` 10.x. Existing compiled binaries would fail. However, since we are recompiling with the updated package (10.0.5), the source code calling pattern (`services.Configure<T>(IConfiguration)`) should continue to compile and work correctly.
- **Action**: Recompile with updated packages. If a compilation error occurs, check for new overloads or namespace changes in `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.5.

### 🟡 Source Incompatible (1 issue)

#### `TimeSpan.FromSeconds(double)` — Ambiguous Overload

- **File**: `REBUSS.Pure\AzureDevOpsIntegration\Configuration\GitRemoteDetector.cs`
- **Line**: 118
- **Code**: `process.WaitForExit(TimeSpan.FromSeconds(5));`
- **Impact**: .NET 10 introduces `TimeSpan.FromSeconds(int)` overload. When passing an integer literal like `5`, the compiler may report an ambiguous call between `FromSeconds(double)` and `FromSeconds(int)`.
- **Action**: If compilation fails, change to `TimeSpan.FromSeconds(5.0)` or simply let the `int` overload resolve (which is actually more correct). Test to verify behavior is unchanged.

### 🔵 Behavioral Changes (112 issues — Low Impact)

These are runtime behavioral changes that do not require code modifications but should be validated by running the test suite.

| API | Occurrences | Files Affected | Description |
|---|---|---|---|
| `System.Text.Json.JsonDocument` | 55 | Parsers, MCP server tests, InitCommand | Minor behavioral changes in JSON parsing edge cases |
| `System.Uri` | 28 | McpWorkspaceRootProvider, tests | URI parsing normalization changes |
| `System.Net.Http.HttpContent` | 17 | AzureDevOpsApiClient, tests | HTTP content handling subtle changes |
| `Uri.#ctor(string)` | 6 | Tests | Constructor behavior changes |
| `Uri.AbsoluteUri` | 3 | Tests | Property value normalization changes |
| `Uri.#ctor(Uri, string)` | 1 | Source | Two-arg constructor changes |
| `Uri.TryCreate` | 1 | Source | TryCreate behavior changes |
| `ConsoleLoggerExtensions.AddConsole` | 1 | Program.cs:141 | Logging console behavioral change |

**Mitigation**: All 112 behavioral changes are validated by the existing test suite. If tests pass after the upgrade, these behavioral changes are confirmed to not impact the application.

---

## 7. Risk Management

### Risk Assessment

| Project | Risk Level | Justification |
|---|---|---|
| REBUSS.Pure | 🟢 Low | Small codebase (7.3k LOC), few mandatory issues (2), well-tested |
| REBUSS.Pure.Tests | 🟢 Low | Test project, no mandatory issues, all behavioral |

### High-Risk Changes

| Change | Risk | Description | Mitigation |
|---|---|---|---|
| `Configure<T>` binary incompatible | Medium | Method signature changed | Recompilation should resolve; if not, check for API changes in 10.0.5 |
| `TimeSpan.FromSeconds` overload | Low | May cause ambiguous call | Simple fix: use `5.0` or let `int` overload resolve |
| `Azure.Identity` 1.13.2 → 1.19.0 | Low | Major version jump in MSAL dependency | No API surface changes expected for `DefaultAzureCredential` usage pattern |
| 112 behavioral changes | Low | Runtime behavior differences | Validated by existing test suite |

### Contingency Plans

- **If `Configure<T>` breaks**: Check the updated `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.5 API surface. The `Configure<T>(IServiceCollection, IConfiguration)` extension may have moved to a different method or require a different binding call.
- **If `Azure.Identity` 1.19.0 introduces issues**: Fall back to the latest non-deprecated 1.x version that works with .NET 10.
- **If tests fail due to behavioral changes**: Investigate failing tests individually; behavioral changes in `JsonDocument` and `Uri` are usually edge cases in normalization that may require updating test expectations.

---

## 8. Testing & Validation Strategy

### Build Validation

After the atomic upgrade:
- [ ] `dotnet restore` succeeds for entire solution
- [ ] Solution builds with 0 errors
- [ ] No new warnings related to the upgrade

### Test Execution

Test project to run: `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj`

- [ ] All unit tests pass
- [ ] All integration tests pass (EndToEndTests)

### Behavioral Change Validation

The 112 behavioral changes (JsonDocument, Uri, HttpContent) are implicitly validated by the test suite. Specifically:
- `McpServerTests` validates JsonDocument parsing behavior
- `McpWorkspaceRootProviderTests` validates Uri handling
- `AzureDevOpsApiClientTests` validates HttpContent handling
- `EndToEndTests` validates the full pipeline

---

## 9. Complexity & Effort Assessment

| Project | Complexity | Package Updates | API Issues | Risk |
|---|---|---|---|---|
| REBUSS.Pure | Low | 10 packages | 2 mandatory + 44 behavioral | 🟢 Low |
| REBUSS.Pure.Tests | Low | 0 packages | 68 behavioral | 🟢 Low |

### Overall Assessment

This is a straightforward upgrade:
- The solution is small (2 projects, ~14k LOC)
- Only 2 mandatory code issues to address
- All NuGet packages have known target versions
- No technology migrations or feature changes required
- Comprehensive test coverage exists to validate behavioral changes

---

## 10. Source Control Strategy

### Branching

- **Source branch**: `master`
- **Upgrade branch**: `upgrade-to-NET10`

### Commit Strategy

**Single commit** for the entire atomic upgrade:
- All project file changes (TargetFramework + package versions)
- All code fixes (if any compilation errors arise)
- Commit message: `Upgrade solution to .NET 10.0`

### Review & Merge

- Create PR from `upgrade-to-NET10` → `master`
- Verify CI build passes
- Verify all tests pass
- Merge via squash or regular merge

---

## 11. Success Criteria

### Technical Criteria

- [ ] Both projects target `net10.0`
- [ ] All 10 package updates applied (9 Microsoft.Extensions.* + Azure.Identity)
- [ ] Solution builds with 0 errors
- [ ] Solution builds with no new warnings
- [ ] All tests pass
- [ ] No package dependency conflicts

### Quality Criteria

- [ ] Code quality maintained (no workarounds or hacks)
- [ ] Test coverage unchanged
- [ ] No functionality regression

### Process Criteria

- [ ] All-At-Once strategy followed
- [ ] Single commit for entire upgrade
- [ ] Upgrade branch created from `master`
