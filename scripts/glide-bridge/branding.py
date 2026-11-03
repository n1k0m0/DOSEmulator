# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Install our MiniGlide presentation without modifying provider DLLs."""

from __future__ import annotations

import re
from pathlib import Path

from install_guest import read_guest_file, replace_file


def configure_host(host: Path) -> Path | None:
    """Use windowed output, guest-owned mouse input, and our own branding."""
    if host.name.lower() != "glide2x-dgvoodoo-x64.dll":
        return None
    config = host.with_name("dgVoodoo.conf")
    before = config.read_bytes() if config.exists() else None
    content = before.removeprefix(b"\xef\xbb\xbf").lstrip() if before is not None else None
    if content is not None and (b"\0" in content or not content.startswith((b";", b"Version", b"["))):
        raise ValueError(f"Expected a text provider configuration: {config}")
    text = before.decode("utf-8-sig") if before is not None else "Version = 0x287\n"
    general = re.search(r"(?ims)^\[General\][ \t]*\r?\n(.*?)(?=^\[|\Z)", text)
    general_body = general[1] if general else ""
    for key, value in {"FullScreenMode": "false", "CaptureMouse": "false"}.items():
        line = re.compile(r"(?im)^" + re.escape(key) + r"[ \t]*=[^\r\n]*")
        if line.search(general_body):
            general_body = line.sub(f"{key} = {value}", general_body)
        else:
            general_body = general_body.rstrip() + f"\n{key} = {value}\n"
    if general:
        text = text[:general.start(1)] + general_body + text[general.end(1):]
    else:
        text = text.rstrip() + "\n\n[General]\n" + general_body.lstrip()
    section = re.search(r"(?ims)^\[Glide\][ \t]*\r?\n(.*?)(?=^\[|\Z)", text)
    settings = {"3DfxWatermark": "false", "3DfxSplashScreen": "false"}
    body = section[1] if section else ""
    for key, value in settings.items():
        line = re.compile(r"(?im)^" + re.escape(key) + r"[ \t]*=[^\r\n]*")
        if line.search(body):
            body = line.sub(f"{key} = {value}", body)
        else:
            body = body.rstrip() + f"\n{key} = {value}\n"
    if section:
        text = text[:section.start(1)] + body + text[section.end(1):]
    else:
        text = text.rstrip() + "\n\n[Glide]\n" + body.lstrip()
    after = text.encode("utf-8")
    if before != after:
        backup = config.with_name("dgVoodoo.before-miniglide.conf")
        if before is not None and not backup.exists():
            backup.write_bytes(before)
        config.write_bytes(after)
    return config


def brand_renderer_descriptions(branch: Path) -> list[str]:
    """Name the installed Unreal renderer MiniGlide in its localized selection UI."""
    installed = []
    for extension in ("INT", "DET"):
        name = f"UNREAL/SYSTEM/GLIDEDRV.{extension}"
        before = read_guest_file(branch, name)
        if before is None:
            continue
        section = re.search(rb"(?ims)^\[GlideRenderDevice\][ \t]*\r?\n(.*?)(?=^\[|\Z)", before)
        if section is None:
            raise ValueError(f"Missing renderer description section in {name}")
        german = extension == "DET"
        labels = {
            "ClassCaption": '"MiniGlide für Windows"' if german else '"MiniGlide for Windows"',
            "AskInstalled": "Ist MiniGlide installiert?" if german else "Is MiniGlide installed?",
            "AskUse": "Soll Unreal MiniGlide verwenden?" if german else "Do you want Unreal to use MiniGlide?",
        }
        body = section[1]
        for key, value in labels.items():
            body, count = re.subn(rb"(?im)^" + key.encode("ascii") + rb"=[^\r\n]*",
                                 f"{key}={value}".encode("cp1252"), body)
            if count != 1:
                raise ValueError(f"Expected one renderer label {key} in {name}, found {count}")
        after = before[:section.start(1)] + body + before[section.end(1):]
        caption = "MiniGlide-Unterstützung" if german else "MiniGlide support"
        after, count = re.subn(rb'Preferences=\(Caption="[^"]*"(?=,[^\r\n]*Class=GlideDrv\.GlideRenderDevice)',
                               ('Preferences=(Caption="' + caption + '"').encode("cp1252"), after)
        if count != 1:
            raise ValueError(f"Expected one renderer preference caption in {name}, found {count}")
        if before != after:
            backup = branch / f"before-miniglide-GlideDrv.{extension.lower()}"
            if not backup.exists():
                backup.write_bytes(before)
            replace_file(branch, name, after)
        installed.append(name)
    return installed
