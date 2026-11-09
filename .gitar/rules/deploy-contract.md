---
title: "Deploy contract"
description: "Keep the behaviour that the Kubernetes manifests and deploy checks depend on"
when: "PRs modifying app/Program.cs, Dockerfile or files under deploy/"
actions: "Fail the check and comment with what broke"
type: check
---

# Deploy contract

The change must keep all of the following:

- The app listens on port 8080, behind Service `app` on port 80.
- `/healthz` and `/readyz` answer 200 and don't depend on an external provider.
- `/` prints `version <sha>` and `/api/sessions` returns a `version` field.
- The image runs as a numeric non-root user and still accepts the `APP_VERSION` build argument.
- No credential appears in the repository, the Dockerfile, a build argument or a manifest. Credentials come only from Secret `app-credentials`.
- `newName` and `newTag` in `deploy/kustomization.yaml` are not edited by hand.
