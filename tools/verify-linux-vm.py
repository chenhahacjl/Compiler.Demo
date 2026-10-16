#!/usr/bin/env python3
"""T3 gate 3: verify a linux-x64 native product actually executes on a real Linux VM.

Deterministic paramiko-based verification: sftp upload + exec chmod & run; the ELF's
exit code propagates to this script's exit code. No Windows OpenSSH askpass/console flakiness.

Usage:
    python tools\\verify-linux-vm.py <elf-path>

Env overrides: COCOA_VM_HOST (default 192.168.200.139), COCOA_VM_USER (default root),
COCOA_VM_PASS (default 123456). Requires `pip install paramiko`.
"""

import os
import sys
import uuid

def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify-linux-vm.py <elf-path>")
        return 1
    elf = os.path.abspath(sys.argv[1])
    if not os.path.isfile(elf):
        print(f"error: {elf} not found")
        return 1

    host = os.environ.get("COCOA_VM_HOST", "192.168.200.139")
    user = os.environ.get("COCOA_VM_USER", "root")
    password = os.environ.get("COCOA_VM_PASS", "123456")

    import paramiko

    remote = f"/tmp/cocoa-verify-{uuid.uuid4().hex[:16]}"
    try:
        client = paramiko.SSHClient()
        client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        client.connect(host, username=user, password=password, timeout=15)

        sftp = client.open_sftp()
        sftp.put(elf, remote)
        sftp.close()

        stdin, stdout, stderr = client.exec_command(f"chmod +x {remote}; {remote}")
        # 仅关闭 stdin 写半（EOF）：即使产物含 Console.ReadLine，也不会因等输入挂死；
        # 不能 close()（会连同 stdout/stderr 通道一起关，远端被 SIGHUP）。
        stdin.channel.shutdown_write()
        out = stdout.read().decode("utf-8", "replace")
        err = stderr.read().decode("utf-8", "replace")
        exit_code = stdout.channel.recv_exit_status()
        client.exec_command(f"rm -f {remote}")
        client.close()

        if exit_code == 0:
            print(out, end="")
            print("COCOA_VERIFY_OK")
            return 0

        print(out, end="")
        if err:
            print(err, end="")
        print(f"COCOA_VERIFY_FAIL: {elf} exited {exit_code} on {user}@{host}")
        return 1
    except ImportError:
        print("error: paramiko not installed (`pip install paramiko`)")
        return 1
    except Exception as exc:  # noqa: BLE001 - network/auth failure surface
        print(f"COCOA_VERIFY_FAIL: {exc}")
        return 1

if __name__ == "__main__":
    sys.exit(main())