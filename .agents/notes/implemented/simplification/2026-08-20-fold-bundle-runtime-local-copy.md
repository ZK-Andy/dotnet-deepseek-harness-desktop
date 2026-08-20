# Agent Note: Fold bundle-runtime.sh local copy seam into bundle-runtime-ci.sh

Status: implemented

## Problem

`scripts/bundle-runtime.sh` keeps a local `NODE_SRC`/`DSH_SRC` fast-copy branch (`L19-20` defaults `$HOME/.hermes/node/bin/node`, `$HOME/.local/lib/node_modules/@deepseek-ai/dsh`) that `cp -a` the `pnpm` closure. Production consumers 0 outside docs: `grep bundle-runtime.sh` hits only `docs/development.md` and `README.md`; `package-linux.yml` and `ci.yml` always call `bundle-runtime-ci.sh linux-x64`. The branch duplicates `bundle-runtime-ci.sh`'s `cp -a node_modules/.` but omits `dshmarket.tgz` curl+validation, so outputs drift. The fallback `only copy DSH package body` is incomplete.

## Decision

Make `scripts/bundle-runtime.sh` a thin wrapper `exec bash "$(dirname "$0")/bundle-runtime-ci.sh" linux-x64 "$@"` (keep `--from-ci` compat) or delete the file and update `docs/development.md`/`README.md`/`docs/architecture.md` to the single entry `bash scripts/bundle-runtime-ci.sh linux-x64`. Remove `NODE_SRC`/`DSH_SRC`/`NODE_MODULES_SRC` branches.

## Alternatives considered

Fast local iteration without `421M` download / `pnpm` install; offline use. The author's `hermes` path saves `~60s`.

### Verification

* `grep -r "NODE_SRC|DSH_SRC" scripts` returns 0 or only wrapper.
* `bash scripts/bundle-runtime.sh --help` (or wrapper) still produces `resources/runtime` with `node + node_modules/@deepseek-ai/dsh/lib/bin.js + dshmarket.tgz`.
* Docs point to single command.

## Consequences

Low. CI unaffected (never uses file). Local devs lose fast-copy but gain parity; fallback is download. If file deleted, update 3 doc links.
