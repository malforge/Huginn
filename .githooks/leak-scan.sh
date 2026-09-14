#!/bin/sh
# Refuses content that names things outside this repository: customer, employer or
# infrastructure identifiers that have no business in a public history.
#
# Reads text on stdin. Exits non-zero and prints what matched.
#
# The list of real names lives in .git/leak-terms.txt, which is inside .git and therefore
# never committed. That is deliberate: a deny-list of the words you are hiding, committed
# to the repository, publishes exactly what it was meant to protect.

terms_file="$(git rev-parse --git-dir)/leak-terms.txt"

scan_input="$(mktemp)"
cat > "$scan_input"
trap 'rm -f "$scan_input"' EXIT

# Literal matching is done in the shell rather than with "grep -qF", which aborts on some
# Git for Windows builds and takes the whole check down with it, silently passing.
lower="$(tr '[:upper:]' '[:lower:]' < "$scan_input")"

found=""

if [ -f "$terms_file" ]; then
    while IFS= read -r term || [ -n "$term" ]; do
        case "$term" in ''|\#*) continue ;; esac
        lterm="$(printf '%s' "$term" | tr '[:upper:]' '[:lower:]')"
        case "$lower" in
            *"$lterm"*) found="$found
  names a protected term: $term" ;;
        esac
    done < "$terms_file"
fi

# Built-in shapes, worth catching with no local list at all. Any noreply address is
# excluded: git writes them into every trailer and they identify nobody.
address="$(grep -oiE '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' "$scan_input" \
           | grep -viE '(^|[.+-])noreply@|@noreply\.' | head -1)"
[ -n "$address" ] && found="$found
  contains an email address: $address"

if grep -qE '(gh[pousr]_[A-Za-z0-9]{16,}|sntrys_[A-Za-z0-9]{16,}|eyJ[A-Za-z0-9_-]{20,}\.)' "$scan_input"; then
    found="$found
  contains something shaped like a credential"
fi

if [ -n "$found" ]; then
    printf 'Refused: this would publish something internal.%s\n' "$found"
    printf '\nReword it, or add a deliberate exception to .git/leak-terms.txt\n'
    exit 1
fi
exit 0
