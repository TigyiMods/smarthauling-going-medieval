# SmartHauling Repository Instructions

## Branch and Pull Request Workflow

- `main` is treated as a protected branch.
- Create a feature branch from `main` for every change.
- Open a pull request back to `main`.
- Every pull request should have at least 1 approving review before merge.
- Request review from Liubomyr Bilak when possible.

## Commit Attribution

When a change is materially co-authored, add one or more `Co-authored-by` trailers:

```text
Co-authored-by: Full Name <email@example.com>
```

Example used in this repository:

```text
Co-authored-by: Codex (OpenAI) <noreply@openai.com>
```

## AI Assistance Disclosure

If AI agents or tools materially contributed to a change:

- Mention this in the pull request description under an `AI Assistance` section.
- Apply the `ai-assisted` label to the pull request and related issue when applicable.
- Briefly describe what the AI helped with and what was verified manually.
- Add an appropriate `Co-authored-by` trailer when the contribution was substantial.
- Human review and approval are still required before merge.

## Repository Hygiene

- Do not commit decompiled game code, proprietary game assets, or copied game binaries.
- Do not commit one-off local inspection scripts unless they are sanitized, reusable, and clearly justified.
- Keep public documentation in English.
- Prefer stable, source-controlled build and test steps over machine-specific helper scripts.
- Keep local paths, local logs, and machine-specific configuration out of the repository.

## Validation Expectations

- Run `dotnet build` before committing code changes.
- Run `dotnet test` when changing planner, policy, or other test-covered logic.
- Avoid large refactors without preserving behavior and adding or updating tests where practical.

## Test Standards

- Use xUnit for unit tests in this repository.
- Follow the AAA pattern explicitly: Arrange, Act, Assert.
- Prefer method names in the `MethodName_Scenario_ExpectedOutcome` style.
- Use FakeItEasy when isolating collaborators or interface-based dependencies.
- Do not force mocks into pure-function tests; prefer direct inputs there.
- Prefer deterministic tests with one clear Act step and behavior-focused assertions.
