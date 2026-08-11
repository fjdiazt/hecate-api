import { useEffect, useState } from 'react';
import { Alert, Badge, Box, Button, Group, Loader, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconArrowsExchange,
  IconBrandOpenai,
  IconCopy,
  IconExternalLink,
  IconLogout,
  IconRefresh,
  IconX
} from '@tabler/icons-react';
import {
  cancelCodexLogin,
  getCodexAccount,
  logoutCodexAccount,
  startCodexLogin
} from './api';
import type { CodexAccountState, CodexAccountStatus } from './types';

type AccountCommand = 'connect' | 'cancel' | 'logout' | 'switch';
type Confirmation = 'logout' | 'switch';

const pollIntervalMs = 2000;

export function CodexAccountPanel() {
  const [account, setAccount] = useState<CodexAccountState | null>(null);
  const [command, setCommand] = useState<AccountCommand | null>(null);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let active = true;
    void getCodexAccount()
      .then((state) => { if (active) setAccount(state); })
      .catch((error) => { if (active) setAccount(unavailable(error)); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (account?.status !== 'connecting' || command) return;
    let active = true;
    let timer = window.setTimeout(poll, pollIntervalMs);

    async function poll() {
      try {
        const state = await getCodexAccount();
        if (!active) return;
        setAccount(state);
        if (state.status === 'connecting') timer = window.setTimeout(poll, pollIntervalMs);
      } catch (error) {
        if (active) setAccount(unavailable(error));
      }
    }

    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [account?.status, command]);

  async function refresh() {
    setCommand('connect');
    try {
      setAccount(await getCodexAccount());
    } catch (error) {
      setAccount(unavailable(error));
    } finally {
      setCommand(null);
    }
  }

  async function connect() {
    setCommand('connect');
    setCopied(false);
    try {
      setAccount(await startCodexLogin());
    } catch (error) {
      setAccount(unavailable(error));
    } finally {
      setCommand(null);
    }
  }

  async function cancel() {
    setCommand('cancel');
    try {
      setAccount(await cancelCodexLogin());
    } catch (error) {
      setAccount(unavailable(error));
    } finally {
      setCommand(null);
    }
  }

  async function confirmAccountAction() {
    const action = confirmation;
    if (!action) return;
    setConfirmation(null);
    setCommand(action);
    try {
      let state = await logoutCodexAccount();
      if (action === 'switch' && state.status === 'disconnected') {
        state = await startCodexLogin();
      }
      setCopied(false);
      setAccount(state);
    } catch (error) {
      setAccount(unavailable(error));
    } finally {
      setCommand(null);
    }
  }

  async function copyCode() {
    if (!account?.user_code) return;
    try {
      if (!navigator.clipboard) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(account.user_code);
      setCopied(true);
      notifications.show({ color: 'green', message: 'Code copied' });
    } catch {
      notifications.show({ color: 'red', message: 'Copy unavailable. Select the code and copy it manually.' });
    }
  }

  const status = account?.status;
  const busy = command !== null;

  return (
    <>
      <Paper withBorder p="lg">
        <Stack>
          <Group justify="space-between" align="flex-start">
            <Box>
              <Title order={3}>Codex account</Title>
              {status === 'connected' && (
                <Text c="dimmed" size="sm">{account?.email ?? 'ChatGPT account'}</Text>
              )}
            </Box>
            {status ? <Badge color={statusColor(status)} variant="light">{status}</Badge> : <Loader size="sm" />}
          </Group>

          {status === 'unavailable' && (
            <Alert color="red" title="Codex unavailable">
              <Stack gap="sm">
                <Text size="sm">{account?.error ?? 'Codex sidecar is unavailable.'}</Text>
                <Group><Button leftSection={<IconRefresh size={16} />} loading={busy} onClick={() => void refresh()}>Retry</Button></Group>
              </Stack>
            </Alert>
          )}

          {status === 'disconnected' && (
            <Group><Button leftSection={<IconBrandOpenai size={16} />} loading={command === 'connect'} onClick={() => void connect()}>Connect ChatGPT</Button></Group>
          )}

          {status === 'connecting' && (
            <Group justify="space-between">
              <Text c="dimmed" size="sm">Waiting for device authorization.</Text>
              <Button color="red" variant="light" leftSection={<IconX size={16} />} loading={command === 'cancel'} onClick={() => void cancel()}>Cancel</Button>
            </Group>
          )}

          {status === 'connected' && (
            <Group className="codex-account-meta" justify="space-between" align="flex-end">
              <Box>
                <Text size="xs" c="dimmed">Plan</Text>
                <Text>{account?.plan_type ?? 'Unknown'}</Text>
              </Box>
              <Group>
                <Button variant="light" leftSection={<IconArrowsExchange size={16} />} loading={command === 'switch'} onClick={() => setConfirmation('switch')}>Switch account</Button>
                <Button color="red" variant="subtle" leftSection={<IconLogout size={16} />} loading={command === 'logout'} onClick={() => setConfirmation('logout')}>Sign out</Button>
              </Group>
            </Group>
          )}

          {status === 'error' && (
            <Alert color="red" title="Codex login failed">
              <Stack gap="sm">
                <Text size="sm">{account?.error ?? 'Codex login failed.'}</Text>
                <Group><Button leftSection={<IconRefresh size={16} />} loading={busy} onClick={() => void connect()}>Retry</Button></Group>
              </Stack>
            </Alert>
          )}
        </Stack>
      </Paper>

      <Modal
        opened={status === 'connecting'}
        onClose={() => undefined}
        title="Connect ChatGPT"
        centered
        closeOnClickOutside={false}
        closeOnEscape={false}
        withCloseButton={false}
      >
        <Stack>
          {account?.user_code
            ? <Text className="codex-device-code" aria-label="Device code">{account.user_code}</Text>
            : <Loader size="sm" />}
          <Group grow>
            <Button variant="light" leftSection={<IconCopy size={16} />} onClick={() => void copyCode()}>{copied ? 'Copied' : 'Copy code'}</Button>
            <Button
              component="a"
              href={account?.verification_url ?? undefined}
              disabled={!account?.verification_url}
              target="_blank"
              rel="noopener noreferrer"
              leftSection={<IconExternalLink size={16} />}
            >
              Open login page
            </Button>
          </Group>
          <Button color="red" variant="subtle" leftSection={<IconX size={16} />} loading={command === 'cancel'} onClick={() => void cancel()}>Cancel</Button>
        </Stack>
      </Modal>

      <Modal
        opened={confirmation !== null}
        onClose={() => setConfirmation(null)}
        title={confirmation === 'switch' ? 'Switch ChatGPT account?' : 'Sign out of ChatGPT?'}
        centered
      >
        <Stack>
          <Text size="sm">{confirmation === 'switch' ? 'The current account will be signed out before connecting another account.' : 'Codex models will remain unavailable until another account is connected.'}</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmation(null)}>Cancel</Button>
            <Button color="red" onClick={() => void confirmAccountAction()}>{confirmation === 'switch' ? 'Switch account' : 'Sign out'}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

function unavailable(error: unknown): CodexAccountState {
  return {
    status: 'unavailable',
    email: null,
    plan_type: null,
    verification_url: null,
    user_code: null,
    error: error instanceof Error ? error.message : 'Codex sidecar is unavailable.'
  };
}

function statusColor(status: CodexAccountStatus) {
  if (status === 'connected') return 'green';
  if (status === 'unavailable' || status === 'error') return 'red';
  if (status === 'connecting') return 'blue';
  return 'gray';
}
