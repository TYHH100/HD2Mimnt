
#!/usr/bin/env python3
"""
GitHub -> Codeberg Release Mirror

One-way synchronization:
- Release metadata
- Draft / published / prerelease status
- Release attachments
- Deleted releases and stale attachments

Codeberg is treated as a mirror, never as a source.
"""

import os
import sys
import hashlib
import tempfile
from pathlib import Path
from urllib.parse import quote

import requests


# ---------------------------------------------------------
# Configuration
# ---------------------------------------------------------

GH_TOKEN = os.environ["GH_TOKEN"]
CB_TOKEN = os.environ["CODEBERG_TOKEN"]

GH_OWNER, GH_REPO = os.environ["GITHUB_REPOSITORY"].split("/", 1)

CB_OWNER = os.environ["CODEBERG_OWNER"]
CB_REPO = os.environ["CODEBERG_REPO"]
CB_URL = os.environ.get("CODEBERG_URL", "https://codeberg.org").rstrip("/")

GH_API = "https://api.github.com"
CB_API = f"{CB_URL}/api/v1"

TIMEOUT = 120

GH_HEADERS = {
    "Authorization": f"Bearer {GH_TOKEN}",
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
}

CB_HEADERS = {
    "Authorization": f"token {CB_TOKEN}",
    "Accept": "application/json",
}

session = requests.Session()


# ---------------------------------------------------------
# HTTP helpers
# ---------------------------------------------------------

def request(method, url, **kwargs):
    response = session.request(
        method,
        url,
        timeout=TIMEOUT,
        **kwargs
    )

    if not response.ok:
        raise RuntimeError(
            f"HTTP {response.status_code}: "
            f"{method} {url}\n{response.text[:2000]}"
        )

    return response



def get_all(url, headers, params=None, page_size_param="per_page"):
    results = []
    page = 1

    while True:
        query = dict(params or {})
        query.update({
            "page": page,
            page_size_param: 100,
        })

        response = request(
            "GET",
            url,
            headers=headers,
            params=query
        )

        batch = response.json()

        if not isinstance(batch, list):
            raise RuntimeError(
                f"Expected API list from {url}"
            )

        results.extend(batch)

        if not batch or len(batch) < 100:
            break

        page += 1

    return results



# ---------------------------------------------------------
# GitHub
# ---------------------------------------------------------

def github_releases():
    url = (
        f"{GH_API}/repos/{GH_OWNER}/{GH_REPO}/releases"
    )

    releases = get_all(url, GH_HEADERS)

    print(f"[GitHub] Found {len(releases)} releases")

    return releases


def download_github_asset(asset, destination):
    """Download an asset from GitHub."""

    response = request(
        "GET",
        asset["browser_download_url"],
        headers=GH_HEADERS,
        stream=True
    )

    digest = hashlib.sha256()

    with open(destination, "wb") as output:
        for chunk in response.iter_content(1024 * 1024):
            if chunk:
                output.write(chunk)
                digest.update(chunk)

    return digest.hexdigest()


# ---------------------------------------------------------
# Codeberg / Forgejo
# ---------------------------------------------------------

def codeberg_releases():
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}/releases"
    )

    releases = get_all(url, CB_HEADERS)

    print(f"[Codeberg] Found {len(releases)} releases")

    return releases


def create_release(source):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}/releases"
    )

    payload = {
        "tag_name": source["tag_name"],
        "target_commitish": source.get(
            "target_commitish", "main"
        ),
        "name": source.get("name") or source["tag_name"],
        "body": source.get("body") or "",
        "draft": source.get("draft", False),
        "prerelease": source.get("prerelease", False),
    }

    response = request(
        "POST",
        url,
        headers=CB_HEADERS,
        json=payload
    )

    release = response.json()

    print(
        f"[Create] {payload['tag_name']} "
        f"(ID: {release['id']})"
    )

    return release


def update_release(target, source):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}"
        f"/releases/{target['id']}"
    )

    payload = {
        "tag_name": source["tag_name"],
        "target_commitish": source.get(
            "target_commitish", "main"
        ),
        "name": source.get("name") or source["tag_name"],
        "body": source.get("body") or "",
        "draft": source.get("draft", False),
        "prerelease": source.get("prerelease", False),
    }

    response = request(
        "PATCH",
        url,
        headers=CB_HEADERS,
        json=payload
    )

    print(f"[Update] {source['tag_name']}")

    return response.json()


def delete_release(target):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}"
        f"/releases/{target['id']}"
    )

    request(
        "DELETE",
        url,
        headers=CB_HEADERS
    )

    print(f"[Delete Release] {target['tag_name']}")


# ---------------------------------------------------------
# Assets
# ---------------------------------------------------------

def release_assets(release_id):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}"
        f"/releases/{release_id}/assets"
    )

    return get_all(url, CB_HEADERS, page_size_param="limit")


def delete_asset(release_id, asset_id):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}"
        f"/releases/{release_id}/assets/{asset_id}"
    )

    return get_all(url, CB_HEADERS, page_size_param="limit")


def upload_asset(release_id, source_asset, filepath):
    url = (
        f"{CB_API}/repos/{CB_OWNER}/{CB_REPO}"
        f"/releases/{release_id}/assets"
    )

    content_type = (
        source_asset.get("content_type")
        or "application/octet-stream"
    )

    with open(filepath, "rb") as file:
        response = request(
            "POST",
            url,
            headers=CB_HEADERS,
            files={
                "attachment": (
                    source_asset["name"],
                    file,
                    content_type
                )
            }
        )

    print(
        f"[Upload] {source_asset['name']} "
        f"({source_asset['size']} bytes)"
    )

    return response.json()


def sync_assets(source, target):
    release_id = target["id"]

    source_assets = source.get("assets", [])
    target_assets = release_assets(release_id)

    source_names = {
        asset["name"] for asset in source_assets
    }

    target_by_name = {
        asset["name"]: asset for asset in target_assets
    }

    # Remove assets which no longer exist on GitHub.
    for name, asset in target_by_name.items():
        if name not in source_names:
            delete_asset(release_id, asset["id"])
            print(f"[Remove Asset] {name}")

    # Synchronize remaining assets.
    for asset in source_assets:
        name = asset["name"]
        existing = target_by_name.get(name)

        # Same name and size: assume unchanged.
        if (
            existing is not None
            and existing.get("size") == asset.get("size")
        ):
            print(f"[Skip] {name}")
            continue

        # Replace outdated attachment.
        if existing is not None:
            delete_asset(release_id, existing["id"])

        safe_name = Path(name).name

        if safe_name != name or safe_name in ("", ".", ".."):
            raise RuntimeError(
                f"Unsafe asset filename: {name}"
            )

        with tempfile.TemporaryDirectory() as directory:
            filepath = Path(directory) / safe_name

            download_github_asset(asset, filepath)

            if filepath.stat().st_size != asset["size"]:
                raise RuntimeError(
                    f"Asset size mismatch: {name}"
                )

            upload_asset(
                release_id,
                asset,
                filepath
            )


# ---------------------------------------------------------
# Main synchronization
# ---------------------------------------------------------

def sync():
    source_releases = github_releases()
    target_releases = codeberg_releases()

    source_by_tag = {
        release["tag_name"]: release
        for release in source_releases
    }

    target_by_tag = {
        release["tag_name"]: release
        for release in target_releases
    }

    print("\n=== Synchronizing releases ===")

    for tag, source in source_by_tag.items():

        target = target_by_tag.get(tag)

        if target is None:
            target = create_release(source)
        else:
            target = update_release(target, source)

        sync_assets(source, target)

    print("\n=== Removing stale releases ===")

    for tag, target in target_by_tag.items():
        if tag not in source_by_tag:
            delete_release(target)

    print("\n=== Mirror completed ===")


if __name__ == "__main__":
    try:
        sync()
    except Exception as error:
        print(f"\n[FATAL] {error}", file=sys.stderr)
        sys.exit(1)
