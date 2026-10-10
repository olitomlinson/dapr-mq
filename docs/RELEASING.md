# Releasing

## What ships

| Asset | Name | Registry | Licence |
|---|---|---|---|
| Server image | `ghcr.io/olitomlinson/daprmq` | GHCR | ELv2 |
| Operator image | `ghcr.io/olitomlinson/daprmq-operator` | GHCR | ELv2 |
| Dashboard image | `ghcr.io/olitomlinson/daprmq-dashboard` | GHCR | ELv2 |
| Helm chart | `oci://ghcr.io/olitomlinson/charts/daprmq` | GHCR (OCI) | ELv2 |
| .NET SDK | `DaprMQ.Client` | NuGet | Apache-2.0 |
| Python SDK | `daprmq-client` (import `daprmq_client`) | PyPI | Apache-2.0 |
| TypeScript SDK | `@daprmq/client` | npm | Apache-2.0 |
| Java SDK | `io.github.olitomlinson:daprmq-client` | Maven Central | Apache-2.0 |
| Go SDK | `github.com/olitomlinson/dapr-mq/sdks/go` | Go module proxy | Apache-2.0 |

## Versioning

Every asset ships at one shared version, set by a single git tag `vX.Y.Z[-pre]`.

Releases go through three stages:

1. **Pre-release (now):** `0.0.0-alpha.N`, with N incrementing per release (`v0.0.0-alpha.1`, `v0.0.0-alpha.2`, ...).
2. **Release candidate:** `X.Y.Z-rc.N`.
3. **Full release:** `X.Y.Z`.

Ecosystems that don't use SemVer pre-release syntax take the equivalent form:

| Ecosystem | `v0.0.0-alpha.1` publishes as |
|---|---|
| NuGet, npm, Maven, Helm, images | `0.0.0-alpha.1` |
| PyPI (PEP 440) | `0.0.0a1` (`rc.N` -> `rcN`) |
| Go | tag `sdks/go/v0.0.0-alpha.1` |

Pre-releases never move the `latest` image tag. On npm, pre-releases publish to `latest` until the first full
release exists, so a plain `npm install` gets the newest one. After that they publish to `next`. A publish can
set only one dist-tag (trusted publishing can't run `npm dist-tag add`), so `next` stays at `0.0.0-alpha.1`
until then.

The versions checked into the repo stay at the placeholder `0.0.0-alpha.0` (`0.0.0a0` for Python). The release
pipeline stamps the real version from the tag.

### Go: retracted v0.1.0

`sdks/go/v0.1.0` was tagged before this scheme existed, and the Go module proxy keeps it forever. To keep
`go get` from preferring it over the pre-releases, `sdks/go/go.mod` retracts `[v0.1.0, v0.1.1]`.

The retraction only takes effect once it is published in a version higher than v0.1.0, so `sdks/go/v0.1.1`
was tagged once (2026-10-10), on a commit that contains the retraction. That tag exists only to carry the
retraction. `@latest` now resolves to the highest `v0.0.0-alpha.N`.

## Cutting a release

Tag a commit that's already on `main` and push the tag:

```sh
git tag v0.0.0-alpha.1 origin/main
git push origin v0.0.0-alpha.1
```

[release.yml](../.github/workflows/release.yml) then checks the tag format and that the commit is on `main`,
stamps the version, runs the unit tests and publishes. So far npm and the Go module are wired up; the Go job
pushes the `sdks/go/vX.Y.Z` tag on the same commit.

### npm

The `npm` GitHub environment only accepts `v*` tags. Until trusted publishing is configured, the job uses the
environment secret `NPM_TOKEN`; npm only allows trusted publishing on a package that already exists. After the
first publish, configure `@daprmq/client` → Settings → Trusted Publisher (`olitomlinson/dapr-mq`,
`release.yml`, environment `npm`). Then set publishing access to "Require 2FA and disallow tokens" and delete
both the token and the secret.
