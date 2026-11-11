#!/usr/bin/env bash
set -euo pipefail

_validate_ubuntu_mirrors() {
    local mirrors=$1 fallback=$2 line host line_number=0
    local canonical='^https?://(azure\.archive\.ubuntu\.com|archive\.ubuntu\.com|security\.ubuntu\.com)/ubuntu/?[[:blank:]]+priority:[1-9][0-9]*[[:blank:]]*$'
    local ignored='^[[:blank:]]*(#.*)?$'
    local -A seen=()
    while IFS= read -r line || [[ -n "$line" ]]; do
        line_number=$((line_number + 1))
        if [[ "$line" =~ $ignored ]]; then
            continue
        elif [[ "$line" =~ $canonical ]]; then
            seen["${BASH_REMATCH[1]}"]=1
        elif [[ $line_number == 1 && "$line" == "$fallback" ]]; then
            continue
        else
            echo 'Ubuntu mirror list has an unsupported entry or priority.' >&2
            return 1
        fi
    done < "$mirrors"
    for host in azure.archive.ubuntu.com archive.ubuntu.com security.ubuntu.com; do
        if [[ ${seen[$host]:-0} != 1 ]]; then
            echo 'Ubuntu mirror list does not contain the expected canonical mirrors.' >&2
            return 1
        fi
    done
}

# Private fixture paths are accepted only when this function is sourced by tests.
# The executable entry point below always supplies the fixed system paths.
_initialize_apt() (
    set -euo pipefail
    local os_release=$1 mirrors=$2 keyring=$3 host fallback scratch first_line
    local ID= VERSION_CODENAME=
    if [[ ! -r "$os_release" ]]; then
        echo 'Ubuntu operating-system identity is unavailable.' >&2
        return 1
    fi
    # shellcheck disable=SC1090
    . "$os_release"
    if [[ "$ID" != ubuntu || ! "$VERSION_CODENAME" =~ ^[a-z][a-z0-9-]*$ ]]; then
        echo 'APT recovery requires a supported Ubuntu identity and release codename.' >&2
        return 1
    fi
    for host in azure.archive.ubuntu.com archive.ubuntu.com security.ubuntu.com; do
        if timeout --kill-after=2s 5s getent ahostsv4 "$host" >/dev/null; then
            echo 'A canonical Ubuntu mirror resolves; retaining the existing APT configuration.'
            return 0
        fi
    done
    if [[ ! -f "$mirrors" || -L "$mirrors" || ! -r "$mirrors" || ! -s "$mirrors" ]]; then
        echo 'Ubuntu APT does not expose the supported regular mirror list.' >&2
        return 1
    fi
    printf -v fallback 'https://mirrors.edge.kernel.org/ubuntu/\tpriority:0'
    _validate_ubuntu_mirrors "$mirrors" "$fallback" || return 1
    if [[ ! -r "$keyring" || ! -s "$keyring" ]]; then
        echo 'The installed Ubuntu archive keyring is unavailable.' >&2
        return 1
    fi
    scratch=$(mktemp -d)
    trap 'rm -rf -- "$scratch"' EXIT
    timeout --kill-after=5s 60s curl --fail --silent --show-error --location \
        --proto '=https' --proto-redir '=https' --tlsv1.2 \
        --connect-timeout 10 --max-time 30 --retry 1 --retry-max-time 45 \
        --output "$scratch/InRelease" \
        "https://mirrors.edge.kernel.org/ubuntu/dists/$VERSION_CODENAME/InRelease" || return 1
    timeout --kill-after=2s 10s gpgv --keyring "$keyring" "$scratch/InRelease" || return 1
    cp -- "$mirrors" "$scratch/original"
    IFS= read -r first_line < "$mirrors"
    if [[ "$first_line" != "$fallback" ]]; then
        { printf '%s\n' "$fallback"; cat -- "$scratch/original"; } > "$scratch/recovered"
        sudo install -o root -g root -m 644 -- "$scratch/recovered" "$mirrors" || return 1
    fi
    if ! sudo timeout --kill-after=10s 150s apt-get \
        -o APT::Update::Error-Mode=any -o Acquire::Retries=1 \
        -o Acquire::http::Timeout=10 -o Acquire::https::Timeout=10 update; then
        sudo install -o root -g root -m 644 -- "$scratch/original" "$mirrors" || \
            echo 'APT refresh failed and the original mirror list could not be restored.' >&2
        echo 'APT refresh failed; Ubuntu package prerequisites remain unavailable.' >&2
        return 1
    fi
    echo 'Verified Ubuntu fallback mirror and refreshed signed APT indexes successfully.'
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    if (( $# != 0 )); then
        echo 'APT initialization accepts no runtime arguments.' >&2
        exit 2
    fi
    _initialize_apt /etc/os-release /etc/apt/apt-mirrors.txt /usr/share/keyrings/ubuntu-archive-keyring.gpg
fi
