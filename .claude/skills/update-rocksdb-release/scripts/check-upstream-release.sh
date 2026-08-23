#!/usr/bin/env bash
#
# Prints the newest rocksdb release upstream, the one this repository pins, and
# whether there is anything to do.
#
# Usage: .claude/skills/update-rocksdb-release/scripts/check-upstream-release.sh
#
# Tags are read with git ls-remote rather than the releases API: ls-remote needs
# no token and no API host, which matters because sandboxed sessions often reach
# github.com over git while api.github.com is refused by egress policy.
#
# Exit status is 0 whether or not an update is available -- read the verdict.
# A non-zero status means the check itself could not be carried out.

set -u

REPO_ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
UPSTREAM="https://github.com/facebook/rocksdb"

test -f "${REPO_ROOT}/rocksdbversion" || {
    >&2 echo "cannot find ${REPO_ROOT}/rocksdbversion"
    exit 1
}

PINNED_FULL="$(tr -d ' \r\n' < "${REPO_ROOT}/rocksdbversion")"
# rocksdbversion is "<x>.<y>.<z>.<our build revision>"; only the first three
# components name an upstream tag.
PINNED="$(cut -d. -f1-3 <<< "$PINNED_FULL")"

TAGS="$(git ls-remote --tags --refs "$UPSTREAM" 2>/dev/null \
        | awk '{print $2}' | sed 's|refs/tags/||' \
        | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//')"

test -n "$TAGS" || {
    >&2 echo "could not list tags from ${UPSTREAM}"
    exit 1
}

# sort -V, never a plain sort: lexically, 11.10.0 sorts below 11.9.0 and the
# check would report an update as already applied.
LATEST="$(sort -V <<< "$TAGS" | tail -1)"

echo "upstream latest : ${LATEST}"
echo "pinned here     : ${PINNED}  (rocksdbversion = ${PINNED_FULL})"
echo

if [ "$LATEST" = "$PINNED" ]; then
    echo "VERDICT: up to date, nothing to do."
    echo
    echo "Say so and stop. An empty or invented commit is worse than no commit."
elif [ "$LATEST" = "$(printf '%s\n%s\n' "$LATEST" "$PINNED" | sort -V | head -1)" ]; then
    echo "VERDICT: this repository pins ${PINNED}, which is NEWER than the newest"
    echo "release tag found (${LATEST}). Do not downgrade -- work out why first."
else
    echo "VERDICT: ${LATEST} is available. Set rocksdbversion to ${LATEST}.1 and"
    echo "continue with the skill."
    echo
    echo "Releases in between, oldest first:"
    sort -V <<< "$TAGS" | awk -v p="$PINNED" -v l="$LATEST" '
        $0 == p { seen = 1; next } seen { print "    " $0 }'
fi
