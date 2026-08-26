NODE_VERSION="${NODE_VERSION:-24.19.0}"
DSH_VERSION="${DSH_VERSION:-0.1.1-rc.2}"
"$PNPM_BIN" add "dshmarket@1.29.2" --prod --store-dir "$PNPM_STORE_DIR"
curl -fsSL "https://registry.npmjs.org/dshmarket/-/dshmarket-1.29.2.tgz" -o "$DEST/dshmarket.tgz"
