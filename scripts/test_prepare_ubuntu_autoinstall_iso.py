#!/usr/bin/env python3
"""Deterministic tests for prepare-ubuntu-autoinstall-iso.py (issue #321 review).

Run:  python scripts/test_prepare_ubuntu_autoinstall_iso.py
"""

from __future__ import annotations

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).with_name("prepare-ubuntu-autoinstall-iso.py")
_spec = importlib.util.spec_from_file_location("prep_iso", SCRIPT_PATH)
prep = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(prep)

ARGS = prep.DEFAULT_KERNEL_ARGS
STOCK = "\tlinux\t/casper/vmlinuz  ---\n\tinitrd\t/casper/initrd\n"


class PatchGrubCfgTests(unittest.TestCase):
    def test_injects_before_the_separator(self):
        patched, count = prep.patch_grub_cfg(STOCK, ARGS)
        self.assertEqual(count, 1)
        line = patched.splitlines()[0]
        self.assertIn(ARGS, line)
        self.assertLess(line.index("autoinstall"), line.index("---"),
                        "args after '---' go to the installed system, not the installer")

    def test_patches_every_menuentry(self):
        _, count = prep.patch_grub_cfg(STOCK + STOCK + STOCK, ARGS)
        self.assertEqual(count, 3)

    def test_memtest_line_without_separator_is_untouched(self):
        source = "\tlinux16\t/boot/memtest86+.bin\n"
        patched, count = prep.patch_grub_cfg(source, ARGS)
        self.assertEqual(count, 0)
        self.assertEqual(patched, source)

    def test_already_patched_line_is_idempotent(self):
        once, _ = prep.patch_grub_cfg(STOCK, ARGS)
        twice, count = prep.patch_grub_cfg(once, ARGS)
        self.assertEqual(count, 0)
        self.assertEqual(once, twice)

    def test_false_token_lines_are_still_patched(self):
        # Issue #289 class: a substring is not a token. Each of these lines LACKS the real
        # 'autoinstall' argument and must therefore be patched, not skipped.
        for decoy in ("noautoinstall", "autoinstall-disabled", "foo=autoinstall",
                      "autoinstaller=off", "xautoinstally"):
            with self.subTest(decoy=decoy):
                source = f"\tlinux\t/casper/vmlinuz {decoy} ---\n"
                patched, count = prep.patch_grub_cfg(source, ARGS)
                self.assertEqual(count, 1, f"{decoy!r} must not satisfy the autoinstall token")
                self.assertIn(decoy, patched, "the pre-existing argument must be preserved")
                self.assertTrue(prep.has_all_tokens(patched.splitlines()[0], ARGS.split()))


class VerifyGrubCfgTests(unittest.TestCase):
    def test_verifies_a_correctly_patched_config(self):
        patched, _ = prep.patch_grub_cfg(STOCK + STOCK, ARGS)
        verified, offending = prep.verify_grub_cfg(patched, ARGS)
        self.assertEqual(verified, 2)
        self.assertEqual(offending, [])

    def test_reports_a_skipped_entry_even_when_another_is_patched(self):
        # The old `grep -c autoinstall` returned non-zero here and wrongly passed.
        patched, _ = prep.patch_grub_cfg(STOCK, ARGS)
        mixed = patched + "\tlinux\t/casper/vmlinuz noautoinstall ---\n"
        verified, offending = prep.verify_grub_cfg(mixed, ARGS)
        self.assertEqual(verified, 1)
        self.assertEqual(len(offending), 1)

    def test_comment_prose_does_not_verify(self):
        source = "# choose autoinstall here\n\tlinux\t/casper/vmlinuz ---\n"
        verified, offending = prep.verify_grub_cfg(source, ARGS)
        self.assertEqual(verified, 0)
        self.assertEqual(len(offending), 1)


class WslCommandQuotingTests(unittest.TestCase):
    HOSTILE = "/mnt/c/It's $HOME; $(touch /tmp/pwned)/ubuntu.iso"

    def test_paths_travel_as_positional_arguments_not_command_text(self):
        command = prep.build_wsl_command(None, 'xorriso -indev "$1"', [self.HOSTILE])
        script_index = command.index('xorriso -indev "$1"')
        self.assertNotIn(self.HOSTILE, command[script_index])
        self.assertIn(self.HOSTILE, command[script_index + 1:])
        self.assertEqual(command[script_index + 1], "bash",
                         "bash -c needs an argv[0] before the positional parameters")

    def test_no_interpolated_script_contains_a_raw_path(self):
        command = prep.build_wsl_command("Ubuntu", 'set -e; cat "$1/grub.cfg"', ["/tmp/work"])
        self.assertEqual(command[:5], ["wsl.exe", "-d", "Ubuntu", "-u", "root"])


class WslAvailabilityGuardTests(unittest.TestCase):
    def test_missing_wsl_is_fatal_on_every_platform(self):
        # The guard runs inside main() past argparse and real-ISO checks, so it is
        # asserted at the source level; a win32-only gate lets non-Windows hosts
        # reach subprocess and die with a raw FileNotFoundError instead.
        source = Path(prep.__file__).read_text(encoding="utf-8")
        self.assertIn('if shutil.which("wsl.exe") is None:', source)
        self.assertNotIn('sys.platform == "win32"', source)


class ToWslPathTests(unittest.TestCase):
    def test_translates_a_windows_drive_path(self):
        if sys.platform != "win32":
            self.skipTest("Windows path translation")
        self.assertEqual(prep.to_wsl_path(Path(r"C:\ISOs\ubuntu.iso")), "/mnt/c/ISOs/ubuntu.iso")


def wsl_xorriso_available() -> bool:
    """True when a real ISO can be authored, so fixtures are genuine media."""
    if shutil.which("wsl.exe") is None:
        return False
    try:
        completed = subprocess.run(
            ["wsl.exe", "-u", "root", "-e", "bash", "-c",
             "command -v xorriso >/dev/null 2>&1 && echo PRESENT || echo MISSING"],
            capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return False
    return completed.returncode == 0 and "PRESENT" in completed.stdout


HAVE_XORRISO = wsl_xorriso_available()
STOCK_GRUB_CFG = (
    "menuentry \"Try or Install Ubuntu Server\" {\n"
    "\tlinux\t/casper/vmlinuz  ---\n"
    "\tinitrd\t/casper/initrd\n"
    "}\n"
)
STOCK_DISK_INFO = (
    "Ubuntu-Server 24.04.4 LTS \"Noble Numbat\" - Release amd64 (20240101)\n"
)


def author_iso(tree: Path, iso_path: Path, *, joliet: bool) -> None:
    """Author a real ISO from `tree` with xorriso, the same tool the helper uses."""
    options = "-J " if joliet else ""
    script = f'set -e; xorriso -as mkisofs {options}-o "$2" "$1"'
    command = prep.build_wsl_command(
        None, script, [prep.to_wsl_path(tree), prep.to_wsl_path(iso_path)])
    completed = subprocess.run(command, capture_output=True, text=True, timeout=300)
    if completed.returncode != 0:
        raise AssertionError(f"fixture authoring failed:\n{completed.stderr}")


def build_ubuntu_like_tree(root: Path, *, with_casper: bool = True,
                           with_disk_info: bool = True,
                           disk_info_text: str = STOCK_DISK_INFO) -> Path:
    """Lay out the few paths the recognition path and the helper actually read."""
    tree = root / "tree"
    (tree / "boot" / "grub").mkdir(parents=True)
    (tree / "boot" / "grub" / "grub.cfg").write_text(STOCK_GRUB_CFG, encoding="utf-8")
    if with_casper:
        (tree / "casper").mkdir()
        (tree / "casper" / "vmlinuz").write_bytes(b"not-a-real-kernel")
    else:
        # Something must occupy the tree so xorriso still authors an image.
        (tree / "boot" / "grub" / "filler").write_bytes(b"x")
    if with_disk_info:
        (tree / ".disk").mkdir()
        # Bytes, not write_text: Windows would translate '\n' to '\r\n' and the
        # read-back assertion must compare the exact payload authored onto the ISO.
        (tree / ".disk" / "info").write_bytes(disk_info_text.encode("utf-8"))
    return tree


def synthetic_descriptor_sequence(*, include_primary: bool, terminate: bool = True) -> bytes:
    """Hand-build a descriptor sequence to exercise conditions xorriso cannot emit."""
    def descriptor(descriptor_type: int) -> bytearray:
        sector = bytearray(prep.SECTOR_SIZE)
        sector[0] = descriptor_type
        sector[1:6] = prep.VOLUME_DESCRIPTOR_STANDARD_IDENTIFIER
        sector[6] = prep.VOLUME_DESCRIPTOR_VERSION
        return sector

    image = bytearray(prep.VOLUME_DESCRIPTOR_START_OFFSET)
    if include_primary:
        image += descriptor(prep.PRIMARY_VOLUME_DESCRIPTOR_TYPE)
    else:
        image += descriptor(prep.SUPPLEMENTARY_VOLUME_DESCRIPTOR_TYPE)
    if terminate:
        image += descriptor(prep.VOLUME_DESCRIPTOR_TERMINATOR_TYPE)
    else:
        # Well-formed but endless: the scan must stop at its own cap rather than
        # run to the end of a multi-gigabyte file.
        for _ in range(prep.MAX_SCANNED_VOLUME_DESCRIPTORS):
            image += descriptor(prep.SUPPLEMENTARY_VOLUME_DESCRIPTOR_TYPE)
    return bytes(image)


@unittest.skipUnless(HAVE_XORRISO, "WSL xorriso is required to author real ISO fixtures")
class VerifyPreparedIsoArtefactTests(unittest.TestCase):
    """Assert on the produced media's own bytes.

    Asserting that '-joliet on' appears in the xorriso command would pass even if
    the image carried no Joliet tree, so every check here reads the artefact; see
    myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md ISO-D42.
    """

    def setUp(self):
        self.workspace = Path(tempfile.mkdtemp(prefix="hvmcp-iso-test-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)

    def author(self, iso_name: str, *, joliet: bool = True, **tree_options) -> Path:
        tree = build_ubuntu_like_tree(self.workspace / iso_name, **tree_options)
        iso_path = self.workspace / f"{iso_name}.iso"
        author_iso(tree, iso_path, joliet=joliet)
        return iso_path

    def test_joliet_media_with_casper_and_disk_info_passes(self):
        self.assertIsNone(prep.verify_prepared_iso(self.author("good")))

    def test_media_authored_without_joliet_is_rejected(self):
        # This is the #358 defect itself, observed on real bytes rather than argv.
        unsatisfied = prep.verify_prepared_iso(self.author("nojoliet", joliet=False))
        self.assertIsNotNone(unsatisfied)
        self.assertIn("Joliet", unsatisfied)

    def test_media_without_a_casper_directory_is_rejected(self):
        unsatisfied = prep.verify_prepared_iso(self.author("nocasper", with_casper=False))
        self.assertIsNotNone(unsatisfied)
        self.assertIn(prep.CASPER_DIRECTORY_NAME, unsatisfied)

    def test_media_without_disk_info_is_rejected(self):
        unsatisfied = prep.verify_prepared_iso(self.author("noinfo", with_disk_info=False))
        self.assertIsNotNone(unsatisfied)
        self.assertIn(".disk/info", unsatisfied)

    def test_disk_info_of_another_release_is_rejected(self):
        unsatisfied = prep.verify_prepared_iso(self.author(
            "wrongrelease", disk_info_text="Ubuntu-Server 22.04.4 LTS - Release amd64\n"))
        self.assertIsNotNone(unsatisfied)
        self.assertIn("24.04", unsatisfied)

    def test_disk_info_is_read_back_through_the_joliet_tree(self):
        # Proves the reader resolves the real Joliet records, not an ISO 9660
        # fallback: the payload must come back byte-for-byte from the artefact.
        iso_path = self.author("readback")
        with open(iso_path, "rb") as iso_file:
            descriptor = prep.find_joliet_descriptor(iso_file)
            self.assertIsNotNone(descriptor)
            root_entries = prep.read_joliet_root_entries(iso_file, descriptor)
            self.assertIn(prep.CASPER_DIRECTORY_NAME,
                          [entry[0] for entry in root_entries])
            self.assertEqual(prep.read_joliet_disk_info(iso_file, root_entries),
                             STOCK_DISK_INFO)


class VerifyPreparedIsoStructuralTests(unittest.TestCase):
    def setUp(self):
        self.workspace = Path(tempfile.mkdtemp(prefix="hvmcp-iso-struct-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)

    def write_image(self, name: str, payload: bytes) -> Path:
        path = self.workspace / name
        path.write_bytes(payload)
        return path

    def test_sequence_without_a_primary_descriptor_is_rejected(self):
        path = self.write_image("nopvd.iso", synthetic_descriptor_sequence(include_primary=False))
        unsatisfied = prep.verify_prepared_iso(path)
        self.assertIsNotNone(unsatisfied)
        self.assertIn("primary volume descriptor", unsatisfied)

    def test_sequence_without_a_terminator_is_rejected(self):
        payload = synthetic_descriptor_sequence(include_primary=True, terminate=False)
        path = self.write_image("noterm.iso", payload)
        unsatisfied = prep.verify_prepared_iso(path)
        self.assertIsNotNone(unsatisfied)
        self.assertIn("terminator", unsatisfied)

    def test_non_iso_bytes_are_named_not_raised(self):
        path = self.write_image("garbage.iso", b"\x00" * (prep.VOLUME_DESCRIPTOR_START_OFFSET + 16))
        unsatisfied = prep.verify_prepared_iso(path)
        self.assertIsNotNone(unsatisfied)
        self.assertIn("not a complete ISO 9660 image", unsatisfied)

    def test_missing_file_is_named_not_raised(self):
        unsatisfied = prep.verify_prepared_iso(self.workspace / "absent.iso")
        self.assertIsNotNone(unsatisfied)
        self.assertIn("could not be read", unsatisfied)


@unittest.skipUnless(HAVE_XORRISO, "WSL xorriso is required to author real ISO fixtures")
class PublicationGateTests(unittest.TestCase):
    """Drive main()'s gate with real media substituted for the WSL repack.

    The WSL calls are replaced so the test controls WHICH artefact the repack
    produces; the gate, the atomic publish, and the staging cleanup are the real
    code paths. See
    myplans/vm-management/iso-installation/iso-installation-spec.md (AC-19) and
    myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md ISO-D42.
    """

    def setUp(self):
        self.workspace = Path(tempfile.mkdtemp(prefix="hvmcp-iso-gate-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)
        self.source = self.workspace / "source.iso"
        self.source.write_bytes(b"stand-in for the stock ISO")
        self.output = self.workspace / "prepared.iso"
        tree = build_ubuntu_like_tree(self.workspace / "good")
        self.good_iso = self.workspace / "good.iso"
        author_iso(tree, self.good_iso, joliet=True)
        bad_tree = build_ubuntu_like_tree(self.workspace / "bad")
        self.bad_iso = self.workspace / "bad.iso"
        author_iso(bad_tree, self.bad_iso, joliet=False)

    def install_fake_wsl(self, repack_product: Path):
        patched, _ = prep.patch_grub_cfg(STOCK_GRUB_CFG, prep.DEFAULT_KERNEL_ARGS)
        import base64

        def fake_run_in_wsl(distro, script, *, what, script_args=None):
            if what == "work directory creation":
                return "/tmp/fake-work\n"
            if what == "ISO repack":
                shutil.copyfile(repack_product, self.staging_path())
                return ""
            if what == "verification":
                return base64.b64encode(patched.encode("utf-8")).decode("ascii")
            if what == "grub.cfg read":
                return STOCK_GRUB_CFG
            return ""

        original_run = prep.run_in_wsl
        original_ensure = prep.ensure_xorriso
        original_subprocess_run = prep.subprocess.run
        prep.run_in_wsl = fake_run_in_wsl
        prep.ensure_xorriso = lambda distro: None
        # The finally arm deletes the WSL work directory through subprocess.
        prep.subprocess.run = lambda *args, **kwargs: subprocess.CompletedProcess([], 0, "", "")
        self.addCleanup(setattr, prep, "run_in_wsl", original_run)
        self.addCleanup(setattr, prep, "ensure_xorriso", original_ensure)
        self.addCleanup(setattr, prep.subprocess, "run", original_subprocess_run)

    def staging_path(self) -> Path:
        return self.output.with_name(f".{self.output.name}.prep-{os.getpid()}.tmp")

    def run_main(self) -> int:
        original_argv = sys.argv
        sys.argv = ["prepare-ubuntu-autoinstall-iso.py",
                    "--source", str(self.source), "--output", str(self.output)]
        try:
            return prep.main()
        finally:
            sys.argv = original_argv

    def leftover_staging_files(self) -> list[str]:
        return [entry.name for entry in self.workspace.iterdir()
                if entry.name.startswith(f".{self.output.name}.prep-")]

    def test_verified_media_is_published(self):
        self.install_fake_wsl(self.good_iso)
        self.assertEqual(self.run_main(), 0)
        self.assertTrue(self.output.is_file())
        self.assertIsNone(prep.verify_prepared_iso(self.output))
        self.assertEqual(self.leftover_staging_files(), [],
                         "a successful publish must leave no staging file")

    def test_unverifiable_media_is_never_published(self):
        self.install_fake_wsl(self.bad_iso)
        self.assertEqual(self.run_main(), 1)
        self.assertFalse(self.output.exists(),
                         "AC-19: a failed gate must leave no output at the destination")
        self.assertEqual(self.leftover_staging_files(), [],
                         "the finally arm must remove the staging artefact")

    def test_a_failed_gate_does_not_replace_pre_existing_output(self):
        self.output.write_bytes(b"previously published media")
        self.install_fake_wsl(self.bad_iso)
        original_argv = sys.argv
        sys.argv = ["prepare-ubuntu-autoinstall-iso.py", "--source", str(self.source),
                    "--output", str(self.output), "--force"]
        try:
            self.assertEqual(prep.main(), 1)
        finally:
            sys.argv = original_argv
        self.assertEqual(self.output.read_bytes(), b"previously published media")


@unittest.skipUnless(HAVE_XORRISO, "WSL xorriso is required to run the real repack")
class RealRepackEndToEndTests(unittest.TestCase):
    """Run the helper's real xorriso repack and verify the media it produced.

    The fakes in PublicationGateTests cannot catch a regression in the repack
    command itself, so this exercises the genuine command path end to end on a
    small synthetic-but-real Ubuntu-shaped ISO; see
    myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md ISO-D41.
    """

    def setUp(self):
        if sys.platform != "win32":
            self.skipTest("the helper's WSL path translation requires a Windows host")
        self.workspace = Path(tempfile.mkdtemp(prefix="hvmcp-iso-e2e-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)
        tree = build_ubuntu_like_tree(self.workspace / "source")
        self.source = self.workspace / "source.iso"
        author_iso(tree, self.source, joliet=True)
        self.output = self.workspace / "prepared.iso"

    def test_real_repack_produces_media_that_passes_the_gate(self):
        original_argv = sys.argv
        sys.argv = ["prepare-ubuntu-autoinstall-iso.py",
                    "--source", str(self.source), "--output", str(self.output)]
        try:
            self.assertEqual(prep.main(), 0)
        finally:
            sys.argv = original_argv
        self.assertIsNone(prep.verify_prepared_iso(self.output),
                          "the repacked artefact must itself satisfy the gate")
        with open(self.output, "rb") as iso_file:
            descriptor = prep.find_joliet_descriptor(iso_file)
            self.assertIsNotNone(descriptor, "the repack must preserve the Joliet tree")
            root_entries = prep.read_joliet_root_entries(iso_file, descriptor)
            self.assertEqual(prep.read_joliet_disk_info(iso_file, root_entries),
                             STOCK_DISK_INFO)


if __name__ == "__main__":
    unittest.main(verbosity=2)
