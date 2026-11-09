# Story

## Before

Recorded 2026-11-09 11:29 MST, namespace `team-roscoe`.

[Radar Cloud Checks](https://app.radarhq.io/checks?scope=7U9MH7kBYpY&namespace=team-roscoe)

Findings by severity: 0 critical, 0 high, 4 medium, 0 low.

- `automountServiceAccountToken` (medium, Security): Deployment/team-roscoe/app
- `readOnlyRootFs` (medium, Security): Deployment/team-roscoe/app
- `missingPDB` (medium, Reliability): Deployment/team-roscoe/app
- `missingTopologySpread` (medium, Reliability): Deployment/team-roscoe/app

## After

Recorded 2026-11-09 14:05 MST, namespace `team-roscoe`, on image `dd2aefa`.

Findings by severity: 0 critical, 0 high, 0 medium, 0 low.

All four were fixed in the deployment manifests, in [pull request 4](https://github.com/JRoscoeM/cloudnative-roscoe/pull/4):

- `automountServiceAccountToken`: turned off, because the app never calls the Kubernetes API.
- `readOnlyRootFs`: the root filesystem is read-only, with a small writable `/tmp`.
- `missingPDB`: a PodDisruptionBudget keeps at least one pod running.
- `missingTopologySpread`: pods prefer separate nodes.
