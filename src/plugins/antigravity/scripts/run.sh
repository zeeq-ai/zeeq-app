#!/bin/sh
node_bin=${ZEEQ_NODE:-}
if [ -n "$node_bin" ] && [ ! -x "$node_bin" ]; then node_bin=; fi
if [ -z "$node_bin" ]; then node_bin=$(command -v node); fi
if [ -z "$node_bin" ]; then
  for candidate in "$HOME/.local/share/mise/shims/node" "$HOME/.nvm/current/bin/node" /opt/homebrew/bin/node /usr/local/bin/node; do
    if [ -x "$candidate" ]; then node_bin=$candidate; break; fi
  done
fi
if [ -z "$node_bin" ]; then
  if [ "$1" = Stop ]; then printf '%s\n' '{"decision":"stop"}'; else printf '%s\n' '{}'; fi
  exit 0
fi
exec "$node_bin" ./scripts/hook.mjs "$@"
