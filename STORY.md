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

## AI investigation

Run 2026-11-09 from 15:01 to 15:11 MST on Deployment `team-roscoe/app`, on image `2cabd28`. [Open it in Radar Cloud](https://app.radarhq.io/c/7U9MH7kBYpY/investigations/chat_7R3uEQvdPzUibsjp?org=org_NCJbssDGEBsU). The run is private, so only the person who started it can open it unless it's shared.

**First assessment:** healthy, with 2 of 2 pods ready and no restarts. It treated the logged startup error as a single failed attempt that recovered on retry.

**Question we asked next:** why do new pods log Elasticsearch errors with a blank HTTP status soon after starting?

**Finding (likely, 60% confidence):** the first DNS lookup for the Elasticsearch hostname can time out on a new pod. A blank status fits this, because the request never got far enough to receive an HTTP response.

**Ruled out by the investigation:** a NetworkPolicy, the CoreDNS rewrite rule and a sidecar proxy.

**What we checked ourselves in Prometheus:** pod `app-cbc57c7bb-th7ll` has one `try_again` lookup for the Elasticsearch host, lasting 5.006 seconds, and it logged the startup error. Pod `app-cbc57c7bb-shkvg` has no failed lookup and no startup error, so this affects some new pods, not all of them.

**What we did:**

- Talk indexing already retries, and it recovered on both pods.
- A failure during first-time setup had left the venue's lines missing. [Pull request 7](https://github.com/JRoscoeM/cloudnative-roscoe/pull/7) makes setup resume until every default is saved.
- We avoid deploying right before presenting, because a request in the first few seconds on a new pod can still fail.

**Still unknown:** which network hop drops the query, whether CPU throttling at startup plays a part, and whether DNS also explains a save that failed about 30 seconds after one pod started.
