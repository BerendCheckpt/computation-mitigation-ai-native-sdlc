---
name: security-policy
description: Organisation security, privacy and compliance standards for CheckNet United. Apply whenever writing a spec, plan or code that handles authentication, authorization, personal data, secrets, external integrations, retailer data or file imports. Owner - security policy owner. Source of truth - this file (versioned in git).
---

# security-policy

Advisory control. The deterministic layer behind it: `no-secrets` and `protect-paths` hooks, `policy.yaml`,
branch protection and required human review for `critical`/`regulated` (REVIEW.md).

## Standards
1. **Secrets** never in code, config or logs. Use user-secrets locally, Key Vault / GitHub secrets elsewhere.
2. **AuthN/AuthZ**: every endpoint has an explicit authorization policy or an explicit, justified `AllowAnonymous`.
3. **Input**: validate at the boundary; parameterized queries only; no string-built SQL, shell or XPath.
4. **Files & imports** (retailer XML/CSV): size limits, schema validation, no external entity resolution (XXE off).
5. **Personal data**: minimise, never log it, document retention in the spec.
6. **Dependencies**: no new package without license and maintenance check; pin versions.
7. **Errors**: no stack traces or internal identifiers in responses.

## In a spec or plan
Add a "Security & privacy" section that states which standards apply and how they are met. Anything you cannot
resolve becomes a numbered concern addressed to the security policy owner — and the PR gets label `regulated`.
