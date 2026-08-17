# Code Comment Policy

## Rule

Application source code must not contain ordinary line or block comments.

## Allowed documentation

Existing XML summaries are retained. Maintain them only when their public
contract changes. Each summary must be one short, direct sentence of at most
90 characters. Do not introduce ordinary source comments.

## Required alternatives

- Express behavior through clear names, small cohesive methods, and focused types.
- Record architectural, operational, and historical decisions in an ADR or a
  document under `docs/`.
- Capture non-obvious invariants in tests.
- Use structured logs and runbooks for operational guidance.

## Review checklist

- Do not add `//` or `/* ... */` comments to application source.
- Do not encode implementation history or bug narratives in source files.
- Preserve and update XML summaries when a public contract changes.
- Update the relevant architecture or operations document when an explanation
  must survive code refactoring.
