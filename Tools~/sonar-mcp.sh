#!/usr/bin/env bash
# SonarQube MCP server (github.com/SonarSource/sonarqube-mcp-server) against OUR
# self-hosted SonarQube, READ-ONLY, for the subscription agents — Claude Code, Codex,
# ZCode — over stdio. Registration (the Sonar runbook in SashaRX/Space (docs/sonar-autofix.md; here the tools live in Tools~/ and the config in ~/.config/meshlab/) has the full recipes):
#
#   claude mcp add sonarqube -- bash /abs/path/to/UnityMeshLab/Tools~/sonar-mcp.sh
#   ~/.codex/config.toml:
#     [mcp_servers.sonarqube]
#     command = "bash"
#     args = ["/abs/path/to/UnityMeshLab/Tools~/sonar-mcp.sh"]
#
# Credentials never go into a tracked file or a command line: SONAR_HOST_URL and
# SONAR_API_TOKEN (a USER token — the MCP server rejects project/global analysis
# tokens) come from the environment or from ~/.config/meshlab/sonar.env (chmod 600).
#
# READ-ONLY is not optional: the server's write tools can mark issues accepted or
# false-positive, which this repo's policy forbids an agent to do (findings are fixed
# or left alone, never suppressed). Needs Docker, and SonarQube 25.1+ on the server.
set -euo pipefail

ENV_FILE="${SONAR_ENV_FILE:-$HOME/.config/meshlab/sonar.env}"
if [ -f "$ENV_FILE" ]; then
  # shellcheck disable=SC1090 # the path is the user's own config file
  . "$ENV_FILE"
fi

if [ -z "${SONAR_HOST_URL:-}" ]; then
  echo "sonar-mcp: SONAR_HOST_URL is not set (env or $ENV_FILE)" >&2
  exit 1
fi
SONARQUBE_TOKEN="${SONAR_API_TOKEN:-${SONARQUBE_TOKEN:-}}"
if [ -z "$SONARQUBE_TOKEN" ]; then
  echo "sonar-mcp: SONAR_API_TOKEN (a USER token) is not set (env or $ENV_FILE)" >&2
  exit 1
fi
if ! command -v docker >/dev/null 2>&1; then
  echo "sonar-mcp: docker is required to run the sonarsource/sonarqube-mcp image" >&2
  exit 1
fi

export SONARQUBE_TOKEN
export SONARQUBE_URL="${SONAR_HOST_URL%/}"
export SONARQUBE_READ_ONLY=true
export SONARQUBE_TOOLSETS="${SONAR_MCP_TOOLSETS:-issues,rules,quality-gates,measures,security-hotspots,sources,duplications}"
export SONARQUBE_LOG_TO_FILE_DISABLED=true

# -e NAME (no value) passes the variable through from this environment, so the token
# never shows up in the process list.
exec docker run --init --pull=always -i --rm \
  -e SONARQUBE_TOKEN -e SONARQUBE_URL -e SONARQUBE_READ_ONLY -e SONARQUBE_TOOLSETS \
  -e SONARQUBE_LOG_TO_FILE_DISABLED \
  "${SONAR_MCP_IMAGE:-sonarsource/sonarqube-mcp}"
