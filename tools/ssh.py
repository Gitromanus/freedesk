"""SSH/SFTP-хелпер для выкладки сайта: python tools/ssh.py run "команда" | put <local> <remote> | get <remote> <local>"""
import sys
import os
import paramiko

HOST = 'netesnl9.beget.tech'
USER = 'netesnl9_fd'
PASSWORD = os.environ.get('FD_SSH_PASSWORD', '')


def connect():
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, username=USER, password=PASSWORD, timeout=30, banner_timeout=30, auth_timeout=30)
    return client


def run(client, command):
    stdin, stdout, stderr = client.exec_command(command, timeout=120)
    out = stdout.read().decode('utf-8', 'replace')
    err = stderr.read().decode('utf-8', 'replace')
    code = stdout.channel.recv_exit_status()
    if out:
        sys.stdout.write(out)
    if err:
        sys.stderr.write(err)
    return code


def main():
    if not PASSWORD:
        print('FD_SSH_PASSWORD не задан', file=sys.stderr)
        return 2

    client = connect()
    try:
        action = sys.argv[1] if len(sys.argv) > 1 else 'run'

        if action == 'run':
            return run(client, sys.argv[2])

        if action == 'put':
            local, remote = sys.argv[2], sys.argv[3]
            sftp = client.open_sftp()
            sftp.put(local, remote)
            sftp.close()
            print(f'put {local} -> {remote}')
            return 0

        if action == 'rm':
            return run(client, f'rm -f {sys.argv[2]}')

        if action == 'ls':
            return run(client, f'ls -la {sys.argv[2]}')

        print(f'неизвестное действие: {action}', file=sys.stderr)
        return 2
    finally:
        client.close()


if __name__ == '__main__':
    sys.exit(main())
