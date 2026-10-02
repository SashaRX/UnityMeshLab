#!/usr/bin/env bash
# Sonar fixer on SUBSCRIPTIONS — no pay-per-request model API anywhere.
#
#   bash Tools~/sonar-fix.sh <prompt-file>
#   bash Tools~/sonar-fix.sh --check       # which providers are usable right now; no edits
#
# Ported from SashaRX/Space (tools/sonar-fix.sh); the Sonar runbook in SashaRX/Space (docs/sonar-autofix.md; here the tools live in Tools~/ and the config in ~/.config/meshlab/) is the runbook.
#
# The prompt file is what Tools~/sonar-pr-check.mjs writes (prompt.md of a findings
# bundle). Providers are tried in SONAR_FIXERS order (default claude,codex,zcode); the
# first one that runs to completion wins. A provider that is missing, not logged in
# with a SUBSCRIPTION, out of quota, timed out or failing is skipped, and its partial
# edits are discarded before the next one starts.
#
# The CLIs must already be installed on the runner: this job holds the subscription
# logins and a write token, so it downloads no executable at run time (no npx).
#
#   claude  Claude Code on a Claude subscription: the CLI's own claude.ai login (on the
#           self-hosted runner: the runner user's `claude` login) or
#           CLAUDE_CODE_OAUTH_TOKEN (from `claude setup-token`). An API-key login
#           (api_key / apiKeyHelper) is refused.
#   codex   Codex CLI on a ChatGPT subscription: the runner user's `codex login`, else
#           CODEX_AUTH_JSON (the contents of ~/.codex/auth.json) in a throwaway
#           CODEX_HOME. An API-key login is refused.
#   zcode   Z.ai's GLM Coding Plan through Claude Code — the harness Z.ai documents for
#           the plan: ZAI_API_KEY → ZAI_BASE_URL (default https://api.z.ai/api/anthropic;
#           each ZAI_* from the env, else ~/.config/meshlab/sonar-fix.env), in its own
#           CLAUDE_CONFIG_DIR so no Claude login is visible to it. The key must carry a
#           GLM Coding Plan package: the ZCode app's own Start Plan is a different
#           entitlement, and Z.ai answers its keys with 1113 ("no resource package").
#
# PRE-CHECK. Each provider is checked before it gets the prompt, and one that cannot
# work is skipped at once with the reason in the summary: claude / codex by their
# login status, zcode by a 1-token request to ZAI_BASE_URL (Claude Code itself retries
# a refused key for minutes). Claude and Codex report an exhausted quota within seconds
# on their own, so only zcode needs the request. A run that still fails is labelled
# from its JSON result (quota / auth / max-turns / no-result / timeout / failed(rc)).
# Needs node for that (the repo's toolchain; the autofix workflow sets it up).
#
# ANTHROPIC_API_KEY / OPENAI_API_KEY / CODEX_API_KEY are stripped from every provider's
# environment, so nothing can fall back to per-request billing.
#
# The agents only read and edit files — Claude: Read/Glob/Grep/Edit/Write, user
# settings and MCP servers not loaded; Codex: the workspace-write sandbox (no network).
# The caller owns validation and git. The tree must be clean on entry: a failed
# provider's edits are discarded with a hard reset.
#
# Writes provider=<name|none> and tried=<summary> to $GITHUB_OUTPUT when set; exits 0
# when a provider completed, 1 when none did.
set -uo pipefail

MODE=fix
if [[ "${1:-}" = "--check" ]]; then
  MODE=check
else
  PROMPT_FILE="${1:?usage: sonar-fix.sh <prompt-file> | --check}"
  if [[ ! -f "$PROMPT_FILE" ]]; then
    echo "[sonar-fix] no prompt file: $PROMPT_FILE" >&2
    exit 2
  fi
  PROMPT_FILE="$(cd "$(dirname "$PROMPT_FILE")" && pwd)/$(basename "$PROMPT_FILE")"
fi

FIXERS="${SONAR_FIXERS:-claude,codex,zcode}"
TIMEOUT="${SONAR_FIX_TIMEOUT:-25m}"
MAX_TURNS="${SONAR_FIX_MAX_TURNS:-60}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# Stopped from outside (the autofix workflow's stale-head watcher, a cancelled job, a
# plain kill): stop the provider still running, then exit through the EXIT trap so
# $WORK goes too. The tree is walked, deepest first, rather than a process group
# signalled — `timeout` puts the CLI in a process group of its own.
stop_children() {
  local parent="$1" child
  for child in $(pgrep -P "$parent" 2>/dev/null); do
    stop_children "$child"
    kill -TERM "$child" 2>/dev/null || true
  done
  return 0
}
trap 'stop_children $$; exit 143' TERM INT HUP

note() { echo "[sonar-fix] $*" >&2; }
# A pre-check that fails records its reason for the summary, then returns 3.
skip() {
  local reason="$1" message="$2"
  printf '%s' "$reason" > "$WORK/reason"
  note "$message"
  return 0
}

if [[ "$MODE" = fix ]] && [[ -n "$(git status --porcelain)" ]]; then
  note "the working tree is not clean — commit or stash first (a failed provider's edits are reset)"
  exit 2
fi

# A self-hosted runner may keep the GLM Coding Plan settings in the runner user's own
# config instead of GitHub secrets/env (each read in a subshell: nothing else leaks in).
ENV_FILE="${SONAR_FIX_ENV_FILE:-$HOME/.config/meshlab/sonar-fix.env}"
if [[ -f "$ENV_FILE" ]]; then
  for v in ZAI_API_KEY ZAI_BASE_URL ZAI_MODEL ZAI_FAST_MODEL; do
    if [[ -z "${!v:-}" ]]; then
      # shellcheck disable=SC1090 # the path is the runner user's own config file
      val="$(. "$ENV_FILE" >/dev/null 2>&1; printf '%s' "${!v:-}")"
      if [[ -n "$val" ]]; then printf -v "$v" '%s' "$val"; fi
    fi
  done
fi
ZAI_BASE_URL="${ZAI_BASE_URL:-https://api.z.ai/api/anthropic}"
ZAI_MAIN_MODEL="${ZAI_MODEL:-glm-5.3}"

# Empty secrets arrive as empty variables; treat them as absent.
for v in CLAUDE_CODE_OAUTH_TOKEN CODEX_AUTH_JSON ZAI_API_KEY; do
  if [[ -z "${!v:-}" ]]; then unset "$v"; fi
done

# Only a CLI already INSTALLED on the runner is run. This job holds the subscription
# logins and a write token, so it never downloads an executable at run time (the
# `npx --yes <package>` fallback it used to have): a missing CLI skips the provider.
CLAUDE_CMD=(claude)
CODEX_CMD=(codex)
have_cli() {
  local cli="$1"
  if command -v "$cli" >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

# Edit-only, deterministic: no user settings (a user-level z.ai env block must not
# re-route the claude provider), no MCP servers, no shell, no web. JSON output: in text
# mode a run that ends on the turn limit prints NOTHING (its result is empty), so the
# log could not say why it failed; the JSON result names it (subtype error_max_turns).
CLAUDE_ARGS=(-p --output-format json --setting-sources project --strict-mcp-config
  --no-session-persistence --permission-mode acceptEdits --max-turns "$MAX_TURNS"
  --allowedTools "Read,Glob,Grep,Edit,Write" --disallowedTools "Bash,WebFetch,WebSearch")

check_claude() {
  local status
  if ! have_cli claude; then
    skip no-cli "claude: no claude CLI installed on this runner (nothing is downloaded at run time) — skipped"
    return 3
  fi
  if ! have_cli node; then
    skip no-node "claude: node is not on PATH (it reads the CLI's JSON result; without it a finished fix could not be told from a failed one) — skipped"
    return 3
  fi
  status="$(env -u ANTHROPIC_API_KEY -u ANTHROPIC_AUTH_TOKEN -u ANTHROPIC_BASE_URL \
    "${CLAUDE_CMD[@]}" auth status --json 2>/dev/null)" || true
  if ! grep -Eq '"loggedIn": *true' <<<"$status" \
    || ! grep -Eq '"authMethod": *"(claude\.ai|oauth_token)"' <<<"$status"; then
    skip no-subscription-login "claude: no Claude subscription login (claude.ai / CLAUDE_CODE_OAUTH_TOKEN) — skipped"
    return 3
  fi
}

run_claude() {
  check_claude || return
  timeout "$TIMEOUT" env -u ANTHROPIC_API_KEY -u ANTHROPIC_AUTH_TOKEN -u ANTHROPIC_BASE_URL \
    -u ZAI_API_KEY -u CODEX_AUTH_JSON \
    "${CLAUDE_CMD[@]}" "${CLAUDE_ARGS[@]}" < "$PROMPT_FILE"
}

check_codex() {
  local status
  if ! have_cli codex; then
    skip no-cli "codex: no codex CLI installed on this runner (nothing is downloaded at run time) — skipped"
    return 3
  fi
  status="$(env -u OPENAI_API_KEY -u CODEX_API_KEY "${CODEX_CMD[@]}" login status 2>&1)" || true
  if ! grep -q "Logged in using ChatGPT" <<<"$status" && [[ -n "${CODEX_AUTH_JSON:-}" ]]; then
    export CODEX_HOME="$WORK/codex-home"
    mkdir -p "$CODEX_HOME"
    (umask 077 && printf '%s' "$CODEX_AUTH_JSON" > "$CODEX_HOME/auth.json")
    status="$(env -u OPENAI_API_KEY -u CODEX_API_KEY "${CODEX_CMD[@]}" login status 2>&1)" || true
  fi
  if ! grep -q "Logged in using ChatGPT" <<<"$status"; then
    skip no-subscription-login "codex: no ChatGPT subscription login (codex login / CODEX_AUTH_JSON) — skipped"
    return 3
  fi
}

run_codex() {
  check_codex || return
  # Codex can run commands in its sandbox: no other provider's secret may be visible there.
  timeout "$TIMEOUT" env -u OPENAI_API_KEY -u CODEX_API_KEY -u CODEX_AUTH_JSON \
    -u CLAUDE_CODE_OAUTH_TOKEN -u ZAI_API_KEY "${CODEX_CMD[@]}" exec \
    --sandbox workspace-write -c 'approval_policy="never"' --ephemeral --color never \
    --output-last-message "$WORK/codex-last-message.txt" - < "$PROMPT_FILE"
}

# One 1-token request with the key and model the real run would use. Only a DEFINITE
# refusal skips: no curl, a network error, a 5xx or any other answer falls through to
# the real run, which then reports for itself. The key goes in a 0600 header file, never
# on a command line other users of the machine could read.
check_zcode() {
  if ! have_cli claude; then
    skip no-cli "zcode: no claude CLI installed on this runner (zcode runs through it; nothing is downloaded at run time) — skipped"
    return 3
  fi
  if ! have_cli node; then
    skip no-node "zcode: node is not on PATH (it reads the CLI's JSON result) — skipped"
    return 3
  fi
  if [[ -z "${ZAI_API_KEY:-}" ]]; then
    skip no-key "zcode: no ZAI_API_KEY (GLM Coding Plan key) — skipped"
    return 3
  fi
  # The key goes wherever ZAI_BASE_URL points (the probe below and the run itself):
  # only over TLS, so a typo in the env file cannot send it in the clear. A loopback
  # http:// stand-in is allowed for tests — matched as a whole URL (host, an optional
  # numeric port, an optional path), so `http://127.0.0.1:1@evil` (userinfo) or
  # `http://localhost.evil` cannot pass as loopback.
  if [[ "$ZAI_BASE_URL" != https://* ]] \
    && ! [[ "$ZAI_BASE_URL" =~ ^http://(127\.0\.0\.1|localhost)(:[0-9]{1,5})?(/[^@]*)?$ ]]; then
    skip insecure-url "zcode: ZAI_BASE_URL is not https:// ($ZAI_BASE_URL) — the key would travel in the clear — skipped"
    return 3
  fi
  # $WORK/probe is what --check prints for a key that was not refused.
  if ! command -v curl >/dev/null 2>&1; then
    printf 'not probed: no curl' > "$WORK/probe"
    return 0
  fi
  case "$ZAI_MAIN_MODEL" in
    *[!A-Za-z0-9._-]*)
      printf 'not probed: unusual model name' > "$WORK/probe"
      return 0
      ;;
    *) ;;
  esac
  (umask 077 && printf 'Authorization: Bearer %s\n' "$ZAI_API_KEY" > "$WORK/zai-auth-header")
  local http body detail zcode
  if ! http="$(curl -sS -o "$WORK/zai-probe.json" -w '%{http_code}' --max-time 30 \
    -H @"$WORK/zai-auth-header" -H 'anthropic-version: 2023-06-01' \
    -H 'content-type: application/json' \
    --data "{\"model\":\"$ZAI_MAIN_MODEL\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}" \
    "${ZAI_BASE_URL%/}/v1/messages" 2>/dev/null)"; then
    rm -f "$WORK/zai-auth-header"
    printf 'probe failed (network) — the real run decides' > "$WORK/probe"
    return 0
  fi
  rm -f "$WORK/zai-auth-header"
  body="$(head -c 600 "$WORK/zai-probe.json" 2>/dev/null)"
  detail="$(grep -o '"message": *"[^"]*"' <<<"$body" | head -n 1 | sed 's/^"message": *"//; s/"$//')"
  zcode="$(grep -o '"code": *"[0-9]*"' <<<"$body" | head -n 1 | grep -o '[0-9]\+')"
  if [[ "$zcode" = 1113 ]]; then
    skip no-coding-plan "zcode: the key has no GLM Coding Plan package (Z.ai 1113${detail:+: $detail}) — a ZCode app Start Plan key is not one — skipped"
    return 3
  fi
  case "$http" in
    401 | 403)
      skip auth-rejected "zcode: ${ZAI_BASE_URL} refused the key (HTTP $http${detail:+: $detail}) — skipped"
      return 3
      ;;
    429)
      skip quota "zcode: out of quota or rate-limited (HTTP 429${zcode:+, code $zcode}${detail:+: $detail}) — skipped"
      return 3
      ;;
    200) printf 'accepted' > "$WORK/probe" ;;
    *) printf 'HTTP %s%s — the real run decides' "$http" "${detail:+: $detail}" > "$WORK/probe" ;;
  esac
}

run_zcode() {
  check_zcode || return
  mkdir -p "$WORK/zai-claude"
  timeout "$TIMEOUT" env -u ANTHROPIC_API_KEY -u CLAUDE_CODE_OAUTH_TOKEN -u CODEX_AUTH_JSON \
    CLAUDE_CONFIG_DIR="$WORK/zai-claude" \
    ANTHROPIC_BASE_URL="$ZAI_BASE_URL" \
    ANTHROPIC_AUTH_TOKEN="$ZAI_API_KEY" \
    ANTHROPIC_DEFAULT_OPUS_MODEL="$ZAI_MAIN_MODEL" \
    ANTHROPIC_DEFAULT_SONNET_MODEL="$ZAI_MAIN_MODEL" \
    ANTHROPIC_DEFAULT_HAIKU_MODEL="${ZAI_FAST_MODEL:-glm-5.3-flash}" \
    API_TIMEOUT_MS=3000000 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    "${CLAUDE_CMD[@]}" "${CLAUDE_ARGS[@]}" < "$PROMPT_FILE"
}

# The Claude / ZCode CLI ends a JSON-mode run with ONE result object on stdout
# (type "result"; an API failure is subtype "success" with is_error true). The
# verdict is read from that object alone — never from a grep over the log, whose
# stderr and agent text could carry the same words. Prints
# subtype<TAB>is_error<TAB>result-text (whitespace-collapsed); fails when the log
# holds no result object.
cli_result() {
  local log="$1"
  if node -e '
    const fs = require("fs");
    const lines = fs.readFileSync(process.argv[1], "utf8").split("\n");
    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i].trim();
      if (!line.startsWith("{")) continue;
      let o = null;
      try { o = JSON.parse(line); } catch { continue; }
      if (o && o.type === "result") {
        const text = String(o.result ?? "").replace(/\s+/g, " ");
        process.stdout.write([o.subtype ?? "", o.is_error ? "true" : "false", text].join("\t"));
        process.exit(0);
      }
    }
    process.exit(2);
  ' "$log"; then
    return 0
  fi
  return 1
}

# The CLIs exit 1 for everything; name the cause in the summary the PR comment
# carries. <text> is the result text for Claude / ZCode, the log tail for Codex.
failure_reason() {
  local rc="$1" subtype="$2" text="$3"
  if [[ "$rc" -eq 124 ]]; then
    echo timeout
    return
  fi
  if [[ "$subtype" = error_max_turns ]]; then
    echo max-turns
    return
  fi
  if grep -Eiq 'hit your (session |usage |weekly )?limit|usage limit|insufficient balance|resource package|quota' <<<"$text"; then
    echo quota
  elif grep -Eiq 'authentication error|failed to authenticate|token expired|invalid api key|unauthori[sz]ed|not logged in|please run /login' <<<"$text"; then
    echo auth
  else
    echo "failed($rc)"
  fi
}

IFS=',' read -r -a order <<<"$FIXERS"

if [[ "$MODE" = check ]]; then
  ready=0
  for provider in "${order[@]}"; do
    provider="${provider// /}"
    case "$provider" in
      claude | codex | zcode) ;;
      '') continue ;;
      *) echo "$provider: unknown provider"; continue ;;
    esac
    rm -f "$WORK/reason" "$WORK/probe"
    if "check_$provider"; then
      ready=1
      if [[ "$provider" = zcode ]] && [[ "$(cat "$WORK/probe" 2>/dev/null)" = accepted ]]; then
        echo "zcode: ready (the probe request was accepted)"
      elif [[ "$provider" = zcode ]]; then
        echo "zcode: not refused ($(cat "$WORK/probe" 2>/dev/null || echo 'not probed'))"
      else
        echo "$provider: ready (logged in with a subscription; an exhausted quota only shows when it runs)"
      fi
    else
      echo "$provider: skipped — $(cat "$WORK/reason" 2>/dev/null || echo unknown)"
    fi
  done
  [[ "$ready" -eq 1 ]]
  exit
fi

tried=()
winner="none"
for provider in "${order[@]}"; do
  provider="${provider// /}"
  case "$provider" in
    claude | codex | zcode) ;;
    '') continue ;;
    *) note "unknown provider '$provider' — skipped"; tried+=("$provider:unknown"); continue ;;
  esac
  note "trying $provider"
  rm -f "$WORK/reason" "$WORK/$provider.rc"
  # A background pipeline waited on, not a foreground one: bash holds a trapped
  # signal until a foreground command ends, but `wait` returns at once, so a TERM
  # stops the provider while it is still running. Its exit status rides a file
  # (PIPESTATUS does not exist for a background pipeline).
  { "run_$provider" 2>&1; echo $? > "$WORK/$provider.rc"; } | tee "$WORK/$provider.log" &
  wait $!
  rc="$(cat "$WORK/$provider.rc" 2>/dev/null || echo 1)"
  subtype=""
  text=""
  if [[ "$provider" = codex ]]; then
    text="$(tail -n 60 "$WORK/$provider.log" 2>/dev/null)"
  elif [[ "$rc" -ne 3 && "$rc" -ne 124 ]]; then
    # A run that ended on an error result (the turn limit, an API failure) is a
    # failure whatever its exit status: its edits are partial and must not be
    # validated and pushed as a finished fix. So is exit 0 with no result at all.
    if verdict="$(cli_result "$WORK/$provider.log")"; then
      IFS=$'\t' read -r subtype is_error text <<<"$verdict"
      if [[ "$is_error" = true || "$subtype" = error_* ]]; then
        rc=1
      fi
    elif [[ "$rc" -eq 0 ]]; then
      tried+=("$provider:no-result")
      git reset -q --hard HEAD
      git clean -fdq
      continue
    else
      text="$(tail -n 60 "$WORK/$provider.log" 2>/dev/null)"
    fi
  fi
  if [[ "$rc" -eq 0 ]]; then
    tried+=("$provider:ok")
    winner="$provider"
    break
  fi
  if [[ "$rc" -eq 3 ]] && [[ -f "$WORK/reason" ]]; then
    tried+=("$provider:$(cat "$WORK/reason")")
  else
    tried+=("$provider:$(failure_reason "$rc" "$subtype" "$text")")
  fi
  git reset -q --hard HEAD
  git clean -fdq
done

summary="$(IFS=' '; echo "${tried[*]}")"
note "result: $winner — $summary"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "provider=$winner"
    echo "tried=$summary"
  } >> "$GITHUB_OUTPUT"
fi
[[ "$winner" != "none" ]]
