#!/usr/bin/env python3
"""Prepare a reusable Ubuntu Server autoinstall ISO (issue #321).

Ubuntu 24.04's stock ISO ships a GRUB menu whose kernel line carries no
``autoinstall`` parameter:

    linux   /casper/vmlinuz  ---

A NoCloud ``CIDATA`` seed supplies the autoinstall ANSWERS, but without the
``autoinstall`` kernel parameter subiquity still stops at an interactive consent
gate and prints:

    Confirmation is required to continue.
    Add 'autoinstall' to your kernel command line to avoid this
    Continue with autoinstall? (yes|no)

That prompt is why an otherwise-correct unattended install never completes.

This script rewrites the kernel line ONCE, offline, producing an ISO that can be
reused for every subsequent install. It is deliberately NOT part of the MCP
server: remastering needs xorriso (via WSL on Windows), and making that a runtime
dependency of every Hyper-V host is unacceptable. The server simply consumes the
prepared ISO.

Windows usage (run from the repo root):

    python scripts/prepare-ubuntu-autoinstall-iso.py ^
        --source C:\\HyperMCPTestResources\\ISOs\\ubuntu-24.04-live-server-amd64.iso ^
        --output C:\\HyperMCPTestResources\\ISOs\\ubuntu-24.04-autoinstall-amd64.iso

The source ISO is never modified.

The repack keeps the ISO's Joliet tree (``-joliet on``). Windows mounts that
tree; without it only uppercase 8.3 names remain, the server cannot read
``.disk/info``, and the media is rejected as ``OS_NOT_SUPPORTED``.

Publication is fail-closed: xorriso writes a staging file beside the destination,
the produced bytes are verified (Joliet descriptor present, top-level ``casper``
directory, and a ``.disk/info`` readable through the Joliet tree naming 24.04),
and only a passing artefact is moved into place. A failed verification leaves NO
output file and exits non-zero naming the failed check.

Two xorriso warnings are expected and benign: volume id too long for Joliet, and
symbolic links omitted from the Joliet tree.
"""

from __future__ import annotations

import argparse
import base64
import os
import re
import shutil
import struct
import subprocess
import sys
from pathlib import Path

GRUB_CFG_PATH = "/boot/grub/grub.cfg"

# ISO 9660 volume-descriptor sequence layout (ECMA-119 6.2.1).
VOLUME_DESCRIPTOR_START_OFFSET = 32768
SECTOR_SIZE = 2048
PRIMARY_VOLUME_DESCRIPTOR_TYPE = 1
SUPPLEMENTARY_VOLUME_DESCRIPTOR_TYPE = 2
VOLUME_DESCRIPTOR_TERMINATOR_TYPE = 255
VOLUME_DESCRIPTOR_STANDARD_IDENTIFIER = b"CD001"
VOLUME_DESCRIPTOR_VERSION = 1
JOLIET_ESCAPE_SEQUENCE_OFFSET = 88
DIRECTORY_RECORD_HEADER_LENGTH = 33

# A supplementary descriptor is a Joliet one only when its escape-sequences field
# selects a UCS-2 level; other escapes denote an unrelated supplementary volume.
JOLIET_ESCAPE_SEQUENCES = (b"%/@", b"%/C", b"%/E")

# The descriptor sequence of a real ISO is a handful of sectors; a stream that
# never terminates within this many is malformed, and scanning it to the end of a
# multi-gigabyte file would be pointless work.
MAX_SCANNED_VOLUME_DESCRIPTORS = 64

# Directory extents here are the Joliet root and '.disk', both far below this;
# the bound exists so a bogus 32-bit length cannot demand a 4 GiB allocation.
MAX_DIRECTORY_EXTENT_BYTES = 4 * 1024 * 1024

# '.disk/info' is a single ~67-byte line of text.
MAX_DISK_INFO_BYTES = 64 * 1024

# Windows mounts the Joliet tree, and the server's recognition probe reads
# '.disk/info' from it; see
# internal documentation
DISK_INFO_DIRECTORY_NAME = ".disk"
DISK_INFO_FILE_NAME = "info"
DISK_INFO_VERSION_PATTERN = re.compile(r"24\.04")

# The server refuses media whose mounted root has no top-level 'casper'
# directory (NO_CASPER) before it ever reads '.disk/info'; see
# src/HyperV.Mcp.Server/Infrastructure/IsoInspector.cs.
CASPER_DIRECTORY_NAME = "casper"

# ds=nocloud accompanies the autoinstall flag so subiquity binds to the CIDATA
# seed deterministically instead of relying on discovery order across the two
# attached DVDs. The trailing ';' inside the value is required by cloud-init's
# kernel-cmdline parser and MUST survive quoting.
DEFAULT_KERNEL_ARGS = "autoinstall ds=nocloud;s=/cdrom/"

# Matches the stock "linux /casper/vmlinuz ---" form, capturing the trailing
# '---' separator so injected args land BEFORE it. Arguments after '---' are
# passed to the booted system rather than consumed by the installer, so
# appending there would silently defeat the fix.
LINUX_LINE_PATTERN = re.compile(
    r"^(?P<indent>\s*)(?P<cmd>linux(?:16|efi)?)\s+(?P<kernel>\S+)(?P<middle>.*?)(?P<sep>---\s*)$"
)


class PrepError(RuntimeError):
    """A step failed in a way the operator must see verbatim."""


def to_wsl_path(windows_path: Path) -> str:
    """Translate ``C:\\dir\\file`` into ``/mnt/c/dir/file`` for the WSL tool."""
    resolved = windows_path.resolve()
    drive = resolved.drive.rstrip(":").lower()
    if not drive:
        raise PrepError(f"Path is not an absolute Windows path: {windows_path}")
    tail = str(resolved)[len(resolved.drive):].replace("\\", "/").lstrip("/")
    return f"/mnt/{drive}/{tail}"


def build_wsl_command(distro: str | None, script: str,
                      script_args: list[str] | None = None) -> list[str]:
    command = ["wsl.exe"]
    if distro:
        command += ["-d", distro]
    # -u root: xorriso must read the source ISO and write into /tmp unimpeded.
    command += ["-u", "root", "-e", "bash", "-c", script]
    # Values reach Bash as positional parameters, never as command text. A path
    # containing an apostrophe, '$', or ';' is therefore data and cannot close a
    # quote or inject a command into this root shell.
    command += ["bash"] + list(script_args or [])
    return command


def run_in_wsl(distro: str | None, script: str, *, what: str,
               script_args: list[str] | None = None) -> str:
    completed = subprocess.run(
        build_wsl_command(distro, script, script_args),
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        raise PrepError(
            f"{what} failed (exit {completed.returncode}).\n"
            f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
        )
    return completed.stdout


def ensure_xorriso(distro: str | None) -> None:
    probe = "command -v xorriso >/dev/null 2>&1 && echo PRESENT || echo MISSING"
    if "PRESENT" in run_in_wsl(distro, probe, what="xorriso probe"):
        return
    raise PrepError(
        "xorriso is not available in the WSL distro.\n"
        "Install it, e.g.:  wsl -d <distro> -u root tdnf install -y xorriso\n"
        "                or wsl -d <distro> -u root apt-get install -y xorriso"
    )


def kernel_arg_tokens(line: str) -> list[str]:
    """Return the whitespace-separated kernel arguments that precede ``---``."""
    match = LINUX_LINE_PATTERN.match(line)
    if not match:
        return []
    return match.group("middle").split()


def has_all_tokens(line: str, tokens: list[str]) -> bool:
    """Token-exact membership test (issue #289 class: substrings are not tokens).

    See src/HyperV.Mcp.Server/Infrastructure/TokenMatcher.cs for the C# analogue:
    'noautoinstall' and 'foo=autoinstall' must NOT satisfy 'autoinstall'.
    """
    present = kernel_arg_tokens(line)
    return all(token in present for token in tokens)


def patch_grub_cfg(original: str, kernel_args: str) -> tuple[str, int]:
    """Inject ``kernel_args`` before the ``---`` separator on each linux line."""
    wanted = kernel_args.split()
    patched_lines: list[str] = []
    patched_count = 0

    for line in original.splitlines():
        match = LINUX_LINE_PATTERN.match(line)
        if not match or (wanted and has_all_tokens(line, wanted)):
            patched_lines.append(line)
            continue
        middle = match.group("middle").rstrip()
        rebuilt = (
            f"{match.group('indent')}{match.group('cmd')} {match.group('kernel')}"
            f"{(' ' + middle) if middle else ''} {kernel_args} {match.group('sep').strip()}"
        )
        patched_lines.append(rebuilt)
        patched_count += 1

    # memtest's linux16 line has no '---' and is intentionally left untouched.
    return "\n".join(patched_lines) + "\n", patched_count


def verify_grub_cfg(content: str, kernel_args: str) -> tuple[int, list[str]]:
    """Return (verified line count, offending lines) for every targeted linux line."""
    wanted = kernel_args.split()
    verified = 0
    offending: list[str] = []
    for line in content.splitlines():
        if not LINUX_LINE_PATTERN.match(line):
            continue
        if has_all_tokens(line, wanted):
            verified += 1
        else:
            offending.append(line.strip())
    return verified, offending


class IsoFormatError(ValueError):
    """The media fails a structural condition verification requires.

    Carries the unsatisfied condition verbatim so the publication gate of
    internal documentation (FR-29) can
    name it instead of passing a malformed image or leaking a traceback.
    """


def find_joliet_descriptor(iso_file) -> bytes | None:
    """Return the Joliet supplementary volume descriptor, or None if absent.

    Absent means a well-formed descriptor sequence that terminates without a
    Joliet entry. Anything structurally unsound raises IsoFormatError: unless the
    sequence is proven sound first, a truncated non-ISO stream looks the same as
    a valid ISO that simply lacks Joliet.
    """
    joliet_descriptor: bytes | None = None
    primary_seen = False
    for sector_index in range(MAX_SCANNED_VOLUME_DESCRIPTORS):
        offset = VOLUME_DESCRIPTOR_START_OFFSET + sector_index * SECTOR_SIZE
        iso_file.seek(offset)
        descriptor = iso_file.read(SECTOR_SIZE)
        if len(descriptor) < SECTOR_SIZE:
            raise IsoFormatError(
                "the volume-descriptor sequence is truncated: only "
                f"{len(descriptor)} of {SECTOR_SIZE} bytes are readable at offset "
                f"{offset}, so the media is not a complete ISO 9660 image"
            )
        standard_identifier = descriptor[1:6]
        if standard_identifier != VOLUME_DESCRIPTOR_STANDARD_IDENTIFIER:
            raise IsoFormatError(
                "the volume descriptor at offset "
                f"{offset} carries standard identifier {standard_identifier!r} "
                f"instead of {VOLUME_DESCRIPTOR_STANDARD_IDENTIFIER!r}, so the "
                "media is not an ISO 9660 image"
            )
        if descriptor[6] != VOLUME_DESCRIPTOR_VERSION:
            raise IsoFormatError(
                f"the volume descriptor at offset {offset} declares version "
                f"{descriptor[6]} instead of {VOLUME_DESCRIPTOR_VERSION}"
            )
        descriptor_type = descriptor[0]
        if descriptor_type == VOLUME_DESCRIPTOR_TERMINATOR_TYPE:
            # ECMA-119 8.1 makes the primary descriptor mandatory: a sequence
            # without one is not an ISO 9660 descriptor set, however well its
            # per-sector markers read.
            if not primary_seen:
                raise IsoFormatError(
                    "the volume-descriptor sequence terminates without the "
                    "mandatory primary volume descriptor (type "
                    f"{PRIMARY_VOLUME_DESCRIPTOR_TYPE}), so the media is not an "
                    "ISO 9660 image"
                )
            return joliet_descriptor
        if descriptor_type == PRIMARY_VOLUME_DESCRIPTOR_TYPE:
            primary_seen = True
        if descriptor_type == SUPPLEMENTARY_VOLUME_DESCRIPTOR_TYPE and joliet_descriptor is None:
            escape_start = JOLIET_ESCAPE_SEQUENCE_OFFSET
            escapes = descriptor[escape_start:escape_start + 3]
            if escapes in JOLIET_ESCAPE_SEQUENCES:
                joliet_descriptor = descriptor
    raise IsoFormatError(
        "no volume-descriptor set terminator was found within the first "
        f"{MAX_SCANNED_VOLUME_DESCRIPTORS} descriptors, so the descriptor "
        "sequence is malformed"
    )


def parse_directory_records(extent: bytes) -> list[tuple[str, int, int, bool]]:
    """Parse a directory extent into (name, extent LBA, length, is_directory).

    Every slice is bounded against the buffer: a record or identifier running
    past the extent is a structural defect, not trustworthy short-read data.
    """
    entries: list[tuple[str, int, int, bool]] = []
    position = 0
    while position < len(extent):
        record_length = extent[position]
        if record_length == 0:
            # A zero length pads to the end of the sector; the next record, if
            # any, begins at the following sector boundary.
            position = (position // SECTOR_SIZE + 1) * SECTOR_SIZE
            continue
        if record_length < DIRECTORY_RECORD_HEADER_LENGTH:
            raise IsoFormatError(
                f"a directory record at offset {position} declares length "
                f"{record_length}, shorter than the {DIRECTORY_RECORD_HEADER_LENGTH}-byte "
                "ISO 9660 record header"
            )
        if position + record_length > len(extent):
            raise IsoFormatError(
                f"a directory record at offset {position} declares length "
                f"{record_length} but only {len(extent) - position} bytes remain "
                "in the directory extent"
            )
        record = extent[position:position + record_length]
        child_extent = int.from_bytes(record[2:6], "little")
        data_length = int.from_bytes(record[10:14], "little")
        is_directory = bool(record[25] & 0x02)
        identifier_length = record[32]
        if DIRECTORY_RECORD_HEADER_LENGTH + identifier_length > record_length:
            raise IsoFormatError(
                f"a directory record at offset {position} declares a "
                f"{identifier_length}-byte identifier that does not fit in its "
                f"{record_length}-byte record"
            )
        identifier = record[DIRECTORY_RECORD_HEADER_LENGTH:
                            DIRECTORY_RECORD_HEADER_LENGTH + identifier_length]
        # Joliet identifiers are UCS-2 big-endian; the '.' and '..' entries are
        # single bytes that do not decode as such and are not names we resolve.
        try:
            name = identifier.decode("utf-16-be")
        except UnicodeDecodeError:
            name = ""
        entries.append((strip_version_suffix(name), child_extent, data_length, is_directory))
        position += record_length
    return entries


def strip_version_suffix(name: str) -> str:
    """Drop only the ';1' ISO version suffix, per
    internal documentation; a
    semicolon elsewhere is part of a legitimate Joliet file name.
    """
    return name[:-2] if name.endswith(";1") else name


def read_extent(iso_file, extent_lba: int, data_length: int, *,
                maximum_length: int, what: str) -> bytes:
    """Read one extent, refusing implausible or unreadable lengths.

    The length comes from untrusted media, so it is bounded before reaching
    read(); a short read means the extent runs past end of file and is a
    failure, never a complete payload.
    """
    if data_length <= 0:
        raise IsoFormatError(f"{what} declares a non-positive length of {data_length} bytes")
    if data_length > maximum_length:
        raise IsoFormatError(
            f"{what} declares {data_length} bytes, beyond the {maximum_length}-byte "
            "bound this verification accepts"
        )
    file_size = os.fstat(iso_file.fileno()).st_size
    start = extent_lba * SECTOR_SIZE
    if extent_lba < 0 or start + data_length > file_size:
        raise IsoFormatError(
            f"{what} points at bytes {start}..{start + data_length} which lie "
            f"outside the {file_size}-byte image"
        )
    iso_file.seek(start)
    payload = iso_file.read(data_length)
    if len(payload) != data_length:
        raise IsoFormatError(
            f"{what} is truncated: {len(payload)} of {data_length} bytes readable"
        )
    return payload


def read_joliet_root_entries(iso_file, joliet_descriptor: bytes) -> list[tuple[str, int, int, bool]]:
    """Parse the Joliet root directory — the tree Windows actually mounts."""
    root_record = joliet_descriptor[156:190]
    root_extent = int.from_bytes(root_record[2:6], "little")
    root_length = int.from_bytes(root_record[10:14], "little")
    return parse_directory_records(read_extent(
        iso_file, root_extent, root_length,
        maximum_length=MAX_DIRECTORY_EXTENT_BYTES,
        what="the Joliet root directory extent"))


def read_joliet_disk_info(iso_file, root_entries: list[tuple[str, int, int, bool]]) -> str | None:
    """Return '.disk/info' resolved through the Joliet tree, or None if absent."""
    directory_entry = next(
        (entry for entry in root_entries
         if entry[0] == DISK_INFO_DIRECTORY_NAME and entry[3]),
        None,
    )
    if directory_entry is None:
        return None

    disk_entries = parse_directory_records(read_extent(
        iso_file, directory_entry[1], directory_entry[2],
        maximum_length=MAX_DIRECTORY_EXTENT_BYTES,
        what=f"the Joliet '{DISK_INFO_DIRECTORY_NAME}' directory extent"))
    file_entry = next(
        (entry for entry in disk_entries
         if entry[0] == DISK_INFO_FILE_NAME and not entry[3]),
        None,
    )
    if file_entry is None:
        return None

    return read_extent(
        iso_file, file_entry[1], file_entry[2],
        maximum_length=MAX_DISK_INFO_BYTES,
        what=f"the Joliet '{DISK_INFO_DIRECTORY_NAME}/{DISK_INFO_FILE_NAME}' payload",
    ).decode("utf-8", "replace")


def verify_prepared_iso(iso_path: Path) -> str | None:
    """Return the unsatisfied condition of a produced ISO, or None if it passes.

    Reads the artefact's own bytes: asserting on the xorriso command string would
    pass even if the repack dropped the Joliet tree entirely.
    """
    try:
        with open(iso_path, "rb") as iso_file:
            joliet_descriptor = find_joliet_descriptor(iso_file)
            if joliet_descriptor is None:
                return ("no Joliet supplementary volume descriptor is present, so "
                        "Windows would mount uppercase 8.3 names and the media "
                        "would be rejected as OS_NOT_SUPPORTED")
            root_entries = read_joliet_root_entries(iso_file, joliet_descriptor)
            if not any(entry[0] == CASPER_DIRECTORY_NAME and entry[3]
                       for entry in root_entries):
                return (f"no top-level '{CASPER_DIRECTORY_NAME}' directory is "
                        "reachable through the Joliet directory tree, so the "
                        "media would be rejected as NO_CASPER")
            disk_info = read_joliet_disk_info(iso_file, root_entries)
            if disk_info is None:
                return (f"'{DISK_INFO_DIRECTORY_NAME}/{DISK_INFO_FILE_NAME}' is not "
                        "reachable through the Joliet directory tree")
            if not DISK_INFO_VERSION_PATTERN.search(disk_info):
                return ("the Joliet '.disk/info' does not identify Ubuntu 24.04: "
                        f"{disk_info.strip()!r}")
    except IsoFormatError as error:
        return str(error)
    except OSError as error:
        return f"the prepared ISO could not be read: {error}"
    except (ValueError, UnicodeDecodeError, struct.error, IndexError) as error:
        # A parsing defect MUST surface as a named failure, not an escaping
        # traceback, per the publication gate of
        # internal documentation (FR-29).
        return f"the prepared ISO could not be parsed: {type(error).__name__}: {error}"
    return None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Produce a reusable Ubuntu autoinstall ISO by adding the "
                    "'autoinstall' kernel parameter to the ISO's GRUB config. "
                    "The repack keeps the Joliet tree and writes --output only "
                    "after verification passes; a failed check leaves no output "
                    "file and exits non-zero."
    )
    parser.add_argument("--source", required=True, type=Path,
                        help="Path to the stock Ubuntu Server ISO (never modified).")
    parser.add_argument("--output", required=True, type=Path,
                        help="Path of the prepared ISO to create.")
    parser.add_argument("--distro", default=None,
                        help="WSL distro providing xorriso (default: WSL default distro).")
    parser.add_argument("--kernel-args", default=DEFAULT_KERNEL_ARGS,
                        help=f"Kernel arguments to inject (default: {DEFAULT_KERNEL_ARGS!r}).")
    parser.add_argument("--force", action="store_true",
                        help="Overwrite the output ISO when it already exists.")
    args = parser.parse_args()

    if not args.source.is_file():
        print(f"ERROR: source ISO not found: {args.source}", file=sys.stderr)
        return 2
    if args.output.exists() and not args.force:
        print(f"ERROR: output already exists (use --force): {args.output}", file=sys.stderr)
        return 2
    if args.output.exists() and os.path.samefile(args.source, args.output):
        print("ERROR: --source and --output resolve to the same file; "
              "the source ISO must never be modified.", file=sys.stderr)
        return 2
    # The repack path is WSL-only, so a missing wsl.exe is fatal on every host,
    # not just Windows; without this the failure surfaces as a raw FileNotFoundError.
    if shutil.which("wsl.exe") is None:
        print("ERROR: wsl.exe not found; WSL is required to run xorriso.", file=sys.stderr)
        return 2

    # xorriso refuses a non-empty -outdev that differs from -indev ("media holds
    # non-zero data", exit 32), so the repack targets a fresh staging file; the
    # output is replaced only after verification succeeds.
    staging = args.output.with_name(f".{args.output.name}.prep-{os.getpid()}.tmp")
    work: str | None = None

    try:
        ensure_xorriso(args.distro)

        source_wsl = to_wsl_path(args.source)
        staging.parent.mkdir(parents=True, exist_ok=True)
        staging.unlink(missing_ok=True)
        staging_wsl = to_wsl_path(staging)

        # A per-invocation directory keeps concurrent preparations from deleting
        # or overwriting each other's extracted and patched grub.cfg.
        work = run_in_wsl(
            args.distro,
            'umask 077; mktemp -d /tmp/hvmcp-ubuntu-iso-prep.XXXXXXXX',
            what="work directory creation",
        ).strip()
        if not work:
            raise PrepError("Could not allocate a WSL work directory.")

        print(f"[1/4] Extracting {GRUB_CFG_PATH} from {args.source.name} ...")
        run_in_wsl(
            args.distro,
            'set -e; xorriso -osirrox on -indev "$1" '
            f'-extract {GRUB_CFG_PATH} "$2/grub.cfg"',
            script_args=[source_wsl, work],
            what="grub.cfg extraction",
        )
        original = run_in_wsl(
            args.distro, 'cat "$1/grub.cfg"',
            script_args=[work], what="grub.cfg read")

        print("[2/4] Injecting kernel arguments ...")
        patched, count = patch_grub_cfg(original, args.kernel_args)
        if count == 0:
            raise PrepError(
                "No kernel line was patched. The ISO layout may differ from the "
                "expected Ubuntu 24.04 form. Original grub.cfg:\n" + original
            )
        print(f"      patched {count} menuentry kernel line(s) with: {args.kernel_args}")

        # Write via base64 so no quoting/newline mangling can corrupt grub.cfg on
        # the way through the Windows -> WSL command boundary.
        encoded = base64.b64encode(patched.encode("utf-8")).decode("ascii")
        run_in_wsl(
            args.distro,
            'printf %s "$1" | base64 -d > "$2/grub.cfg.new"',
            script_args=[encoded, work],
            what="patched grub.cfg write",
        )

        print("[3/4] Repacking ISO (preserving Joliet tree and UEFI boot records) ...")
        # -joliet on restores the tree Windows mounts; without it '.disk/info' is
        # unreadable and the media is rejected as OS_NOT_SUPPORTED. See
        # internal documentation
        # -boot_image any replay carries the original El Torito / GPT-EFI boot
        # records over; without it the result will not boot a Generation-2 VM.
        run_in_wsl(
            args.distro,
            'set -e; xorriso -indev "$1" -outdev "$2" '
            '-joliet on '
            '-boot_image any replay '
            f'-map "$3/grub.cfg.new" {GRUB_CFG_PATH}',
            script_args=[source_wsl, staging_wsl, work],
            what="ISO repack",
        )

        print("[4/4] Verifying the prepared ISO ...")
        verify_b64 = run_in_wsl(
            args.distro,
            'set -e; rm -f "$2/verify.cfg"; '
            'xorriso -osirrox on -indev "$1" '
            f'-extract {GRUB_CFG_PATH} "$2/verify.cfg" >/dev/null 2>&1; '
            'base64 -w0 "$2/verify.cfg"',
            script_args=[staging_wsl, work],
            what="verification",
        )
        verify_text = base64.b64decode(verify_b64.strip() or "").decode("utf-8", "replace")
        verified, offending = verify_grub_cfg(verify_text, args.kernel_args)
        if verified < 1 or offending:
            raise PrepError(
                "Prepared ISO failed kernel-argument verification "
                f"(verified {verified} line(s)). Lines missing the required tokens "
                f"before '---':\n" + "\n".join(offending)
            )
        print(f"      verified {verified} kernel line(s) carry: {args.kernel_args}")

        unsatisfied = verify_prepared_iso(staging)
        if unsatisfied is not None:
            raise PrepError(f"Prepared ISO failed verification: {unsatisfied}")
        print("      verified the Joliet tree exposes .disk/info for Ubuntu 24.04")

        os.replace(staging, args.output)
        size = args.output.stat().st_size
        print(f"\nSUCCESS: {args.output} ({size} bytes)")
        print("Point the Ubuntu install at this ISO; no further remastering is needed.")
        return 0

    except PrepError as error:
        print(f"\nERROR: {error}", file=sys.stderr)
        return 1

    finally:
        try:
            staging.unlink(missing_ok=True)
        except OSError:
            pass
        if work:
            run_command = build_wsl_command(
                args.distro, 'rm -rf -- "$1"', [work])
            subprocess.run(run_command, capture_output=True, text=True)


if __name__ == "__main__":
    raise SystemExit(main())
