# A Mac mini as a dedicated perf runner

## Context

The perf workflows ([perf.yml](../.github/workflows/perf.yml),
[perf-extreme.yml](../.github/workflows/perf-extreme.yml)) run on GitHub-hosted `ubuntu-latest`
runners. Each job gets its own fresh 4-vCPU VM, so jobs never compete with each other. The VMs
themselves are the problem:

- **The hardware changes between runs.** `ubuntu-latest` is a pool of VMs on shared Azure hosts.
  The CPU model, and the load from other tenants on the same host, vary from run to run. That
  run-to-run noise is why perf.yml only reports and doesn't gate: the regression check needs
  tolerances of 20% (msg/s) to 100% (p99) just to stay quiet.
- **4 vCPUs is small for `extreme`.** The stack (3 API replicas, 3 sidecars, 3 schedulers,
  Postgres, nginx) and the load generator all share them, so ramps may flatten because the
  machine runs out of CPU, not because DaprMQ does.

A Mac mini on the home network can give the perf suites a fixed, quiet, bigger machine: the same
hardware every night, nothing else running, and enough cores for `extreme` scale. This proposal
covers how to set it up safely, and how the workflows would use it.

## Goals and non-goals

**Goals**
- Nightly and on-demand perf runs on fixed hardware, recorded under their own env label so they
  form their own trend lines.
- Results low-noise enough that `--gate` can be turned on for this runner's series.
- No risk to the home network from code in a public repo.

**Non-goals**
- Replacing GitHub-hosted runners for PRs. PR perf runs, integration tests and everything else stay
  on `ubuntu-latest`.
- Comparing the Mac's numbers with `ubuntu-latest` numbers. They are different series and stay that
  way (the report already groups runs by env label).

## The security problem comes first

**This repo is public.** GitHub's own guidance is not to attach self-hosted runners to public
repos, because a pull request from a fork can run code on them. Concretely:

- A fork's PR runs **the fork's version** of a `pull_request` workflow. Anyone can open a PR that
  adds `runs-on: [self-hosted, ...]` to a workflow and runs whatever they like on the Mac.
- A runner registered on a personal-account repo is available to **every workflow in that repo**.
  Runner groups, which restrict a runner to named workflows on named branches, exist only for
  organisations.
- Whatever runs on the runner can reach anything the runner can reach: the router's admin page,
  the NAS, other machines on the LAN.

The design below answers this with four layers. Any one of them failing should not be enough.

1. **Nothing untrusted can be scheduled on it.** The repo setting *Actions → General → Fork pull
   request workflows → Require approval for all external contributors* is turned on. This means a
   fork PR's workflows don't run until you approve them, and you review any change to
   `.github/workflows/` before you do. The workflows that use the Mac are triggered only by
   `schedule`, `workflow_dispatch` and `push` to `main`, never by `pull_request`,
   `pull_request_target` or `issue_comment`.
2. **Jobs run inside a disposable Linux VM, not on macOS.** A job that escapes its container lands
   in a throwaway VM with no credentials in it, not in your macOS account.
3. **The VM can't see the LAN.** Its firewall allows outbound HTTPS to the internet and drops
   everything bound for private address ranges. Alternatively, the Mac sits on an isolated guest
   network or VLAN if the router supports one.
4. **(Phase 2) Every job gets a fresh VM.** The runner is ephemeral: one job per VM, which is
   deleted afterwards, so nothing a job leaves behind survives to the next one.

The stronger fix for layer 1 is to move the repo into a free GitHub organisation. That makes
runner groups available, so the runner can be restricted to `perf-home.yml` on `main` only. This
is worth doing if the repo starts getting outside contributors. See [Open questions](#open-questions).

The runner itself needs no inbound ports. It makes an outbound HTTPS connection and polls GitHub
for work, so nothing on the router changes.

## Options for where jobs run

| | A. Runner directly on macOS | B. Runner in a Linux VM on the Mac (recommended) |
|---|---|---|
| Containers | Docker Desktop or OrbStack, which is itself a hidden Linux VM | Docker Engine running natively in the VM |
| Hot path | SDK client on macOS → port forward into the hidden VM → containers. That's an extra hop on every request, and it isn't in production | Client and containers in the same Linux kernel, the same shape as `ubuntu-latest` and production |
| Workflow steps | Bash steps and `setup-*` actions mostly work, but anything assuming Linux needs checking | Unchanged: it's Ubuntu |
| CPU and memory | Shared with macOS, and Docker Desktop's VM limits are set loosely | Fixed vCPUs and RAM for the VM: the same machine every run |
| Isolation | The job runs as your macOS user | The job is confined to a VM you can firewall and throw away |

Option B wins on every row that matters for perf data and safety.

## Proposal

### Hardware and host setup

- **The Mac mini**, ideally on wired Ethernet. An M4 with 10 cores and 24–32 GB gives the VM
  8 vCPUs and 20–24 GB while leaving headroom for macOS.
- **macOS settings** so it behaves like a server:
  - `sudo pmset -a sleep 0 disksleep 0 displaysleep 0` stops it sleeping.
  - `sudo pmset -a autorestart 1` restarts it after a power cut.
  - Log in automatically, or run the VM as a launch daemon, so it comes back after a reboot.
  - Schedule macOS updates for a set maintenance window rather than letting them install
    whenever. An update changes the machine, so note it next to the trend lines.
  - Leave the Mac doing nothing else. Spotlight indexing, Time Machine and browsers all add noise.

### The VM (phase 1: one persistent VM)

Use [Lima](https://lima-vm.io) with Apple's Virtualization framework (`vz`), which gives
near-native arm64 Linux:

```bash
brew install lima
limactl create --name=perf-runner --vm-type=vz --cpus=8 --memory=24 --disk=150 template://ubuntu-lts
limactl start perf-runner
limactl shell perf-runner
```

Inside the VM:

```bash
# Docker Engine (not Desktop)
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"

# Toolchains the workflows install themselves via setup-* actions; only git and build basics needed here
sudo apt-get install -y git build-essential

# Block the LAN, allow the internet. ufw applies the first matching rule, so the LAN denies go
# first. DNS is allowed to the router only (replace 192.168.1.1 with yours).
sudo ufw default deny incoming
sudo ufw default deny outgoing
sudo ufw allow out to 192.168.1.1 port 53
sudo ufw deny out to 10.0.0.0/8
sudo ufw deny out to 172.16.0.0/12
sudo ufw deny out to 192.168.0.0/16
sudo ufw allow out 53
sudo ufw allow out 443/tcp
sudo ufw allow out 80/tcp
sudo ufw enable
sudo ufw status numbered   # check the order
```

Docker publishes container ports by writing its own iptables rules, which bypass `ufw`. That's
fine here, because those rules only let the job reach its own containers on `localhost`, but check
that the Testcontainers port mappings still work after enabling `ufw`. Lima's own host-to-VM
networking also uses a private address, so check `limactl shell` still works too.

Then register the runner. In GitHub, go to *Settings → Actions → Runners → New self-hosted runner*,
choose *Linux* and *ARM64*, and use the commands it shows, adding labels:

```bash
./config.sh --url https://github.com/olitomlinson/dapr-mq --token <registration token> \
  --name mac-mini-perf --labels daprmq-perf --work _work
sudo ./svc.sh install && sudo ./svc.sh start
```

The runner then advertises `self-hosted, Linux, ARM64, daprmq-perf`, and the workflows target that
full set.

### The VM (phase 2: a fresh VM per job)

Once phase 1 has proven the numbers are useful, make the runner ephemeral:

1. Keep the phase-1 VM as a golden image (Docker, toolchains and firewall installed, runner
   binaries unpacked but not registered).
2. A small script on the macOS host loops forever, doing for each job:
   - Ask GitHub for a just-in-time runner config. This is a
     `POST /repos/olitomlinson/dapr-mq/actions/runners/generate-jitconfig` call made with a
     fine-grained token that has *Administration: write* on this repo only.
   - Clone the golden VM (`limactl clone`, or [Tart](https://tart.run), which is built for exactly
     this on Apple Silicon).
   - Boot the clone and start `./run.sh --jitconfig <config>` in it. A JIT runner takes exactly one
     job, then exits.
   - Delete the clone.

The token lives only on the macOS host, never in a VM, so a job can't steal it. Each job also
starts from the same disk state, which helps the perf numbers too: no Docker build cache, image
layers or Postgres data left behind by the previous job.

### Workflow changes

A new `perf-home.yml`, separate from perf.yml, so the two series and their triggers stay obviously
apart:

```yaml
name: Performance (home runner)

on:
  schedule:
    - cron: '0 2 * * *'
  push:
    branches: [main]
    paths: ['server/**', 'sdks/**']
  workflow_dispatch:
    inputs:
      suite:
        description: pr or extreme
        default: pr

# One run at a time: there is one machine, and the results branch must not race.
concurrency:
  group: perf-home
  cancel-in-progress: false

permissions:
  contents: write

env:
  ENV_LABEL: home-mac-mini-m4
  TESTCONTAINERS_RYUK_DISABLED: 'true'

jobs:
  perf:
    runs-on: [self-hosted, Linux, ARM64, daprmq-perf]
    timeout-minutes: 360
    steps:
      - uses: actions/checkout@v4
      # Built here, not downloaded: the hosted build-image job produces amd64, and emulating it
      # on arm64 would wreck the numbers.
      - run: docker build -t daprmq-api:test -f server/Dockerfile server
      # ...setup-dotnet / setup-python / setup-node / setup-java, then each SDK's harness in turn
      # with --env-label "$ENV_LABEL" --out perf-results --suite <suite>...
      # ...then the existing publish-perf-results action.
```

Points worth noting:

- **One job, SDKs run one after another.** There's one machine, so running SDKs in parallel would
  make them share CPUs and contaminate each other's numbers. A single job runs .NET, Python,
  TypeScript and Java back to back. With the `pr` suite that's roughly an hour.
- **Images are built on the runner** for arm64 rather than downloaded from the hosted build job.
  The stack's third-party images (Dapr on GHCR, Postgres and nginx on ECR) are already multi-arch.
  The .NET server fixture's WireMock image should be checked for an arm64 build before relying on
  it; the perf topology doesn't use WireMock, but the fixture starts it.
- **Its own env label** (`home-mac-mini-m4`). The report and regression check already group by env
  label, so these runs form their own series and baseline. Change the label whenever the hardware
  or the VM's size changes.
- **Gate this series only.** After a couple of weeks of nightly runs, add `--gate` to this
  workflow's harness calls. perf.yml on `ubuntu-latest` stays report-only.

### Record more about the machine

Each harness's `environment` block should gain the CPU model (`/proc/cpuinfo` on Linux) and total
memory. That's a small change in all four harnesses and the schema. It lets the report flag runs
whose hardware differs from the series they're in, which also helps the `ubuntu-latest` series.

## Operations

- **If the Mac is off,** queued jobs wait for up to 24 hours, then fail. Nightly runs simply don't
  happen until it's back. Nothing else depends on this runner.
- **GitHub shows the runner's status** under *Settings → Actions → Runners*. If missed nights
  matter, a scheduled hosted job can check the runner's status through the API and open an issue
  when it has been offline for a day.
- **Disk.** Phase 1 needs a nightly `docker system prune -af --volumes` (a cron job in the VM) so
  images and volumes don't fill the disk. Phase 2 doesn't, because every VM is new.
- **Runner updates.** The runner updates itself. In phase 2, refresh the golden image monthly so
  each new VM doesn't have to update on its first boot.
- **Cost.** Self-hosted jobs don't use Actions minutes. A Mac mini idles at a few watts.

## Rollout

1. Turn on *Require approval for all external contributors* for fork PR workflows. This is worth
   doing regardless.
2. Set up the Mac and the phase-1 VM, apply the firewall, and register the runner.
3. Add `perf-home.yml`, triggered by `workflow_dispatch` only, and run the `pr` suite by hand a few
   times. Compare the run-to-run spread with `ubuntu-latest`'s.
4. Add the nightly schedule and the push trigger. Add the CPU model to the records.
5. After about two weeks of history, turn on `--gate` for this series, and start running `extreme`
   here instead of on hosted runners.
6. Move to phase 2 (ephemeral VMs) once the setup has settled.

## Open questions

- **Which Mac mini?** The chip and the RAM decide the VM size. Fewer than 16 GB would be tight for
  the `extreme` stack.
- **Move the repo to an organisation?** That makes runner groups available, so the runner can be
  restricted to `perf-home.yml` on `main`. It's the strongest guard against a workflow change
  reaching the Mac, and it's free, but it changes the repo's URL (GitHub redirects the old one).
- **Can the router isolate the Mac** on a guest network or VLAN? If so, use that as well as the
  VM firewall rather than instead of it.
- **Should PRs ever use it?** This proposal says no. A label-triggered run on a maintainer's
  own PR is possible later, but it widens what can be scheduled on the Mac.
