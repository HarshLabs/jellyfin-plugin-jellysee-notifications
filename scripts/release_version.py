"""Revision zero is the release tag; revision one is its Jellyfin 12 build."""
import re


def artifact_version(release, abi):
    if not re.fullmatch(r'\d+\.\d+\.\d+\.0', release) or any(int(p) > 65534 for p in release.split('.')):
        raise ValueError('Release version must have four assembly-version parts and revision zero')
    if abi not in ('10.11.0.0', '12.0.0.0'):
        raise ValueError('Unsupported ABI')
    return release if abi == '10.11.0.0' else release[:-1] + '1'
