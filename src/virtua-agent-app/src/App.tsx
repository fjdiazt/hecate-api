import { type ChangeEvent, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActionIcon,
  Alert,
  AppShell,
  Badge,
  Box,
  Burger,
  Button,
  Card,
  Divider,
  Group,
  Loader,
  Modal,
  NavLink,
  NumberInput,
  Paper,
  Select,
  Stack,
  Tabs,
  Table,
  Text,
  Textarea,
  TextInput,
  Title
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconActivity,
  IconArrowDown,
  IconArrowUp,
  IconBook2,
  IconBrandOpenai,
  IconChevronRight,
  IconCopy,
  IconDownload,
  IconFileImport,
  IconPlus,
  IconSettings,
  IconTrash
} from '@tabler/icons-react';
import { useMediaQuery } from '@mantine/hooks';
import { Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import logoUrl from '../../../assets/logo.png';
import { CodexAccountPanel } from './CodexAccountPanel';
import {
  deleteModelEndpoint,
  deleteVirtuaAgentModel,
  getPipelineSettings,
  listEndpointModels,
  listModelEndpoints,
  listVirtuaAgentModels,
  listUpstreamModels,
  saveModelEndpoint,
  savePipelineSettings,
  saveVirtuaAgentModel
} from './api';
import type {
  ModelEndpoint,
  ModelEndpointType,
  PipelineSettings,
  PipelineStage,
  PipelineStageInput,
  SaveModelEndpointRequest,
  VirtuaAgentModel
} from './types';

const defaultEndpointValue = '__default__';
const originalMessageInputData = [
  { value: 'full', label: 'Original messages: full' },
  { value: 'text', label: 'Original messages: text only' },
  { value: 'none', label: 'Original messages: none' }
];
const priorStageOutputInputData = [
  { value: 'none', label: 'Prior output: none' },
  { value: 'last', label: 'Prior output: last stage' }
];

function endpointSelectData(endpoints: ModelEndpoint[]) {
  return [
    { value: defaultEndpointValue, label: 'Default upstream' },
    ...endpoints.map((endpoint) => ({ value: endpoint.id, label: endpoint.name }))
  ];
}

function endpointValue(endpointId?: string | null) {
  return endpointId ?? defaultEndpointValue;
}

function endpointIdFromValue(value: string | null) {
  return value === defaultEndpointValue ? null : value;
}

const emptyStage = (): PipelineStage => ({
  type: 'single_agent',
  repeat: 1,
  name: '',
  instructions: '',
  protocol: null,
  input: null,
  agent: {
    endpoint_id: null,
    model: null,
    temperature: null,
    top_p: null,
    top_k: null,
    min_p: null,
    repeat_penalty: null,
    max_tokens: null
  }
});

function defaultStageInput(index: number): PipelineStageInput {
  return index === 0
    ? { original_messages: 'full', prior_stage_output: 'none' }
    : { original_messages: 'text', prior_stage_output: 'last' };
}

function hasProtocol(value?: string | null) {
  return Boolean(value?.trim());
}

function modelProtocolSource(protocol?: string | null, settings?: PipelineSettings | null) {
  if (hasProtocol(protocol)) return 'Using model protocol override.';
  if (hasProtocol(settings?.pipeline_protocol)) return 'Using pipeline protocol from Settings.';
  return 'Using built-in protocol.';
}

function stageProtocolSource(stage: PipelineStage, modelProtocol?: string | null, settings?: PipelineSettings | null) {
  if (hasProtocol(stage.protocol)) return 'Using stage protocol override.';
  return modelProtocolSource(modelProtocol, settings);
}

const emptyModel = (baseModel?: string): VirtuaAgentModel => ({
  id: 'virtua-agent/new-model',
  ownedBy: 'virtua-agent',
  pipeline: {
    default_endpoint_id: null,
    default_model: baseModel ?? null,
    default_temperature: 0.2,
    default_top_p: null,
    default_top_k: null,
    default_min_p: null,
    default_repeat_penalty: null,
    default_max_tokens: 512,
    protocol: null,
    stages: [emptyStage()]
  }
});

type ModelLoadError = {
  endpointId: string | null;
  label: string;
  message: string;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function importedModelsFromJson(value: unknown): VirtuaAgentModel[] {
  const models = Array.isArray(value) ? value : [value];
  if (models.length === 0) {
    throw new Error('Import file must contain one model or an array of models.');
  }

  models.forEach((model, index) => {
    if (!isRecord(model) || typeof model.id !== 'string' || !isRecord(model.pipeline)) {
      throw new Error(`Import item ${index + 1} must have id and pipeline.`);
    }

    if (!Array.isArray(model.pipeline.stages)) {
      throw new Error(`Import item ${index + 1} pipeline must have stages.`);
    }
  });

  return models as VirtuaAgentModel[];
}

function importSuccessMessage(models: VirtuaAgentModel[], created: number, updated: number) {
  if (models.length === 1) {
    return updated === 1 ? `Updated ${models[0].id}` : `Imported ${models[0].id}`;
  }

  const counts = [
    updated > 0 ? `${updated} updated` : null,
    created > 0 ? `${created} created` : null
  ].filter(Boolean);

  return `Imported ${models.length} models: ${counts.join(', ')}`;
}

function safeJsonFilename(value: string) {
  return value.trim().replace(/[^a-z0-9._-]+/gi, '-').replace(/^-+|-+$/g, '') || 'virtua-agent-model';
}

function downloadJson(filename: string, value: unknown) {
  const blob = new Blob([`${JSON.stringify(value, null, 2)}\n`], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

function nullableNumber(value: string | number | null | undefined) {
  return value === '' || value === null || value === undefined ? null : Number(value);
}

export function App() {
  const location = useLocation();
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const nav = [
    { label: 'Models', icon: IconBrandOpenai, to: '/models' },
    { label: 'Runs', icon: IconActivity, to: '/runs' },
    { label: 'Settings', icon: IconSettings, to: '/settings' }
  ];

  return (
    <AppShell
      header={{ height: { base: 56, sm: 0 } }}
      navbar={{ width: 260, breakpoint: 'sm', collapsed: { mobile: !mobileNavOpen } }}
      padding="lg"
      className="shell"
    >
      <AppShell.Header hiddenFrom="sm">
        <Group h="100%" px="md" justify="space-between">
          <Group gap="sm">
            <img className="mobile-brand-logo" src={logoUrl} alt="Virtua Agent" />
            <Text fw={600}>Virtua Agent</Text>
          </Group>
          <Burger
            opened={mobileNavOpen}
            onClick={() => setMobileNavOpen((opened) => !opened)}
            size="sm"
            aria-label="Toggle navigation"
          />
        </Group>
      </AppShell.Header>
      <AppShell.Navbar p="md">
        <Stack className="brand-block" gap={4} mb="xl">
          <img className="brand-logo" src={logoUrl} alt="Virtua Agent" />
          <Text size="xs" c="dimmed">API workbench</Text>
        </Stack>
        <Stack gap={4}>
          {nav.map((item) => (
            <NavLink
              key={item.to}
              component={Link}
              to={item.to}
              active={location.pathname === item.to}
              label={item.label}
              leftSection={<item.icon size={18} />}
              rightSection={<IconChevronRight size={14} />}
              onClick={() => setMobileNavOpen(false)}
            />
          ))}
          <NavLink
            component="a"
            href="/swagger"
            label="Swagger"
            leftSection={<IconBook2 size={18} />}
            onClick={() => setMobileNavOpen(false)}
          />
        </Stack>
      </AppShell.Navbar>
      <AppShell.Main>
        <Routes>
          <Route path="/" element={<Navigate to="/models" replace />} />
          <Route path="/models" element={<ModelsPage />} />
          <Route path="/runs" element={<RunsPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  );
}

function ModelsPage() {
  const isSmall = useMediaQuery('(max-width: 700px)');
  const importInputRef = useRef<HTMLInputElement>(null);
  const [endpoints, setEndpoints] = useState<ModelEndpoint[]>([]);
  const [endpointModels, setEndpointModels] = useState<Record<string, string[]>>({});
  const [upstreamModels, setUpstreamModels] = useState<string[]>([]);
  const [modelLoadErrors, setModelLoadErrors] = useState<Record<string, ModelLoadError>>({});
  const [items, setItems] = useState<VirtuaAgentModel[]>([]);
  const [pipelineSettings, setPipelineSettings] = useState<PipelineSettings | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [importText, setImportText] = useState('');
  const [importing, setImporting] = useState(false);
  const [draft, setDraft] = useState<VirtuaAgentModel>(() => emptyModel());

  const selected = useMemo(
    () => items.find((item) => item.id === selectedId),
    [items, selectedId]
  );

  useEffect(() => {
    void refresh();
  }, []);

  useEffect(() => {
    pruneModelLoadErrorsForModel(selected);

    if (selected) {
      setDraft(structuredClone(selected));
      void loadModelsForEndpoint(selected.pipeline.default_endpoint_id);
      selected.pipeline.stages.forEach((stage) => {
        void loadModelsForEndpoint(stage.agent?.endpoint_id);
      });
    }
  }, [selected]);

  async function refresh() {
    const savedModelsPromise = listVirtuaAgentModels();
    const savedEndpointsPromise = listModelEndpoints();
    const settingsPromise = getPipelineSettings();

    const [VirtuaAgentModels, savedEndpoints, settings] = await Promise.all([savedModelsPromise, savedEndpointsPromise, settingsPromise]);
    setItems(VirtuaAgentModels);
    setEndpoints(savedEndpoints);
    setPipelineSettings(settings);

    if (!selectedId && VirtuaAgentModels[0]) {
      setSelectedId(VirtuaAgentModels[0].id);
    }

    const sourceModels = await loadModelsForEndpoint(null, { force: true });
    if (!selectedId && !VirtuaAgentModels[0]) setDraft(emptyModel(sourceModels[0]));
  }

  async function loadModelsForEndpoint(endpointId?: string | null, options?: { force?: boolean }) {
    const key = endpointValue(endpointId);
    if (!options?.force && endpointModels[key]) return endpointModels[key];

    try {
      const models = endpointId ? await listEndpointModels(endpointId) : await listUpstreamModels();
      setEndpointModels((current) => ({ ...current, [key]: models }));
      if (!endpointId) setUpstreamModels(models);
      setModelLoadErrors((current) => {
        const next = { ...current };
        delete next[key];
        return next;
      });
      return models;
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to load models';
      setModelLoadErrors((current) => ({
        ...current,
        [key]: {
          endpointId: endpointId ?? null,
          label: endpointLabel(endpointId),
          message
        }
      }));
      return endpointModels[key] ?? [];
    }
  }

  function referencedEndpointKeys(model?: VirtuaAgentModel | null) {
    const keys = new Set<string>([defaultEndpointValue]);
    if (!model) return keys;

    keys.add(endpointValue(model.pipeline.default_endpoint_id));
    model.pipeline.stages.forEach((stage) => {
      keys.add(endpointValue(stage.agent?.endpoint_id));
    });
    return keys;
  }

  function pruneModelLoadErrorsForModel(model?: VirtuaAgentModel | null) {
    const referencedKeys = referencedEndpointKeys(model);
    setModelLoadErrors((current) => Object.fromEntries(
      Object.entries(current).filter(([key]) => referencedKeys.has(key))
    ));
  }

  function endpointLabel(endpointId?: string | null) {
    if (!endpointId) return 'Default upstream';
    return endpoints.find((endpoint) => endpoint.id === endpointId)?.name ?? endpointId;
  }

  function modelsForEndpoint(endpointId?: string | null, selectedModel?: string | null) {
    const models = endpointModels[endpointValue(endpointId)] ?? (endpointId ? [] : upstreamModels);
    return selectedModel && !models.includes(selectedModel) ? [selectedModel, ...models] : models;
  }

  async function retryModelDiscovery() {
    const referencedKeys = referencedEndpointKeys(selected);
    const errors = Object.values(modelLoadErrors)
      .filter((error) => referencedKeys.has(endpointValue(error.endpointId)));
    pruneModelLoadErrorsForModel(selected);
    await Promise.all(errors.map((error) => loadModelsForEndpoint(error.endpointId, { force: true })));
  }

  function updateStage(index: number, stage: PipelineStage) {
    setDraft((current) => ({
      ...current,
      pipeline: {
        ...current.pipeline,
        stages: current.pipeline.stages.map((item, itemIndex) => itemIndex === index ? stage : item)
      }
    }));
  }

  function nextCloneId(id: string) {
    const ids = new Set(items.map((item) => item.id));
    const base = `${id.trim() || 'virtua-agent-model'}-copy`;
    let next = base;
    for (let index = 2; ids.has(next); index += 1) next = `${base}-${index}`;
    return next;
  }

  function cloneModel(model: VirtuaAgentModel) {
    const clone = structuredClone(model);
    const cloneId = nextCloneId(model.id);
    clone.id = cloneId;
    setSelectedId(null);
    setDraft(clone);
    setEditorOpen(true);
    notifications.show({ color: 'blue', message: `Cloned ${model.id || 'model'} as ${cloneId}. Save model to persist.` });
    void loadModelsForEndpoint(clone.pipeline.default_endpoint_id);
    clone.pipeline.stages.forEach((stage) => {
      void loadModelsForEndpoint(stage.agent?.endpoint_id);
    });
  }

  function moveStage(index: number, direction: -1 | 1) {
    setDraft((current) => {
      const nextIndex = index + direction;
      if (nextIndex < 0 || nextIndex >= current.pipeline.stages.length) return current;
      const stages = [...current.pipeline.stages];
      [stages[index], stages[nextIndex]] = [stages[nextIndex], stages[index]];
      return {
        ...current,
        pipeline: { ...current.pipeline, stages }
      };
    });
  }

  function openModel(model: VirtuaAgentModel) {
    setSelectedId(model.id);
    setDraft(structuredClone(model));
    setEditorOpen(true);
    void loadModelsForEndpoint(model.pipeline.default_endpoint_id);
    model.pipeline.stages.forEach((stage) => {
      void loadModelsForEndpoint(stage.agent?.endpoint_id);
    });
  }

  function newModel() {
    setSelectedId(null);
    setDraft(emptyModel(upstreamModels[0]));
    setEditorOpen(true);
  }

  async function save() {
    const saved = await saveVirtuaAgentModel(draft);
    notifications.show({ color: 'green', message: `Saved ${saved.id}` });
    await refresh();
    setSelectedId(saved.id);
  }

  async function remove() {
    if (!draft.id) return;
    await deleteVirtuaAgentModel(draft.id);
    notifications.show({ color: 'green', message: `Deleted ${draft.id}` });
    setSelectedId(null);
    setDraft(emptyModel(upstreamModels[0]));
    setEditorOpen(false);
    await refresh();
  }

  function exportAllModels() {
    if (items.length === 0) return;
    downloadJson('virtua-agent-models.json', items);
  }

  function exportDraftModel() {
    downloadJson(`${safeJsonFilename(draft.id)}.json`, draft);
  }

  async function readImportFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = '';
    if (!file) return;

    setImportText(await file.text());
  }

  async function importModels() {
    if (!importText.trim()) {
      notifications.show({ color: 'red', message: 'Paste JSON or browse a file.' });
      return;
    }

    setImporting(true);
    try {
      const imported = importedModelsFromJson(JSON.parse(importText));
      const existingIds = new Set(items.map((item) => item.id));
      let created = 0;
      let updated = 0;

      for (const model of imported) {
        if (existingIds.has(model.id)) {
          updated++;
        } else {
          created++;
          existingIds.add(model.id);
        }

        await saveVirtuaAgentModel(model);
      }

      notifications.show({
        color: 'green',
        message: importSuccessMessage(imported, created, updated)
      });
      setImportOpen(false);
      setImportText('');
      await refresh();
      setSelectedId(imported[0]?.id ?? null);
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to import JSON' });
    } finally {
      setImporting(false);
    }
  }

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Box>
          <Title order={2}>Virtua Agent Models</Title>
          <Text c="dimmed">Pipeline-backed models exposed through `/v1/models`.</Text>
        </Box>
        <Group>
          <input
            ref={importInputRef}
            type="file"
            accept="application/json,.json"
            hidden
            onChange={(event) => void readImportFile(event)}
          />
          <Button
            variant="light"
            leftSection={<IconFileImport size={16} />}
            onClick={() => setImportOpen(true)}
          >
            Import JSON
          </Button>
          <Button
            variant="light"
            disabled={items.length === 0}
            leftSection={<IconDownload size={16} />}
            onClick={exportAllModels}
          >
            Export all
          </Button>
          <Button leftSection={<IconPlus size={16} />} onClick={newModel}>
            New model
          </Button>
        </Group>
      </Group>

      <Modal
        opened={importOpen}
        onClose={() => {
          if (!importing) setImportOpen(false);
        }}
        title="Import pipeline models"
        size="lg"
        centered={!isSmall}
      >
        <Stack>
          <Textarea
            label="JSON"
            autosize
            minRows={10}
            maxRows={20}
            value={importText}
            onChange={(event) => setImportText(event.currentTarget.value)}
          />
          <Group justify="space-between">
            <Button
              variant="light"
              leftSection={<IconFileImport size={16} />}
              onClick={() => importInputRef.current?.click()}
            >
              Browse file
            </Button>
            <Group>
              <Button variant="subtle" disabled={importing} onClick={() => setImportOpen(false)}>
                Cancel
              </Button>
              <Button loading={importing} leftSection={<IconFileImport size={16} />} onClick={() => void importModels()}>
                Import
              </Button>
            </Group>
          </Group>
        </Stack>
      </Modal>

      {Object.keys(modelLoadErrors).length > 0 && (
        <Alert color="yellow" title="Model discovery unavailable">
          <Stack gap="xs">
            <Text size="sm">Saved Virtua Agent models are still available. Live model lists could not be loaded.</Text>
            {Object.values(modelLoadErrors).map((error) => (
              <Text key={endpointValue(error.endpointId)} size="sm">
                {error.label}: {error.message}
              </Text>
            ))}
            <Group>
              <Button size="xs" variant="light" onClick={() => void retryModelDiscovery()}>
                Retry
              </Button>
            </Group>
          </Stack>
        </Alert>
      )}

      <Paper withBorder>
        <Box className="table-scroll">
          <Table verticalSpacing="sm" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Model</Table.Th>
                <Table.Th>Default model</Table.Th>
                <Table.Th>Stages</Table.Th>
                <Table.Th className="optional-column">Default endpoint</Table.Th>
                <Table.Th>Actions</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((item) => (
                <Table.Tr key={item.id} className="clickable-row" onClick={() => openModel(item)}>
                  <Table.Td><Text fw={600} className="cell-wrap">{item.id}</Text></Table.Td>
                  <Table.Td><Text className="cell-wrap">{item.pipeline.default_model ?? 'Unset'}</Text></Table.Td>
                  <Table.Td><Badge variant="light">{item.pipeline.stages.length}</Badge></Table.Td>
                  <Table.Td className="optional-column"><Text className="cell-wrap">{endpointLabel(item.pipeline.default_endpoint_id)}</Text></Table.Td>
                  <Table.Td onClick={(event) => event.stopPropagation()}>
                    <Button size="xs" variant="subtle" leftSection={<IconCopy size={14} />} onClick={() => cloneModel(item)}>
                      Clone
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
              {items.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={5}><Text c="dimmed" p="sm">No Virtua Agent Models saved.</Text></Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Box>
      </Paper>

      <Modal
        opened={editorOpen}
        onClose={() => setEditorOpen(false)}
        title={selectedId ? `Edit ${selectedId}` : draft.id ? `New ${draft.id}` : 'New Virtua Agent model'}
        size="95%"
        fullScreen={isSmall}
        centered={!isSmall}
      >
        <Stack>
            <Group grow align="end">
              <TextInput
                label="Model id"
                value={draft.id}
                onChange={(event) => setDraft({ ...draft, id: event.currentTarget.value })}
              />
              <Select
                label="Default endpoint"
                data={endpointSelectData(endpoints)}
                value={endpointValue(draft.pipeline.default_endpoint_id)}
                searchable
                onChange={(value) => {
                  const nextEndpointId = endpointIdFromValue(value);
                  setDraft({ ...draft, pipeline: { ...draft.pipeline, default_endpoint_id: nextEndpointId, default_model: null } });
                  void loadModelsForEndpoint(nextEndpointId);
                }}
              />
              <Select
                label="Default model"
                data={modelsForEndpoint(draft.pipeline.default_endpoint_id, draft.pipeline.default_model)}
                value={draft.pipeline.default_model ?? null}
                searchable
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_model: value } })}
              />
            </Group>
            <Box className="sampling-grid">
              <NumberInput
                label="Default temperature"
                min={0}
                max={2}
                step={0.1}
                value={draft.pipeline.default_temperature ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_temperature: nullableNumber(value) } })}
              />
              <NumberInput
                label="Default top P"
                min={0}
                max={1}
                step={0.05}
                value={draft.pipeline.default_top_p ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_top_p: nullableNumber(value) } })}
              />
              <NumberInput
                label="Default top K"
                min={0}
                step={1}
                value={draft.pipeline.default_top_k ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_top_k: nullableNumber(value) } })}
              />
              <NumberInput
                label="Default min P"
                min={0}
                max={1}
                step={0.01}
                value={draft.pipeline.default_min_p ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_min_p: nullableNumber(value) } })}
              />
              <NumberInput
                label="Default repeat penalty"
                min={0}
                step={0.05}
                value={draft.pipeline.default_repeat_penalty ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_repeat_penalty: nullableNumber(value) } })}
              />
              <NumberInput
                label="Default max tokens"
                min={1}
                value={draft.pipeline.default_max_tokens ?? undefined}
                onChange={(value) => setDraft({ ...draft, pipeline: { ...draft.pipeline, default_max_tokens: nullableNumber(value) } })}
              />
            </Box>
            <Textarea
              label="Protocol override"
              description={modelProtocolSource(draft.pipeline.protocol, pipelineSettings)}
              autosize
              minRows={2}
              maxRows={10}
              value={draft.pipeline.protocol ?? ''}
              onChange={(event) => setDraft({
                ...draft,
                pipeline: { ...draft.pipeline, protocol: event.currentTarget.value || null }
              })}
            />

            <Divider label="Pipeline" />
            <Stack>
              {draft.pipeline.stages.map((stage, index) => (
                <Card withBorder key={index} radius="sm">
                  <Stack>
                    <Group justify="space-between">
                      <Text fw={600}>Stage {index + 1}</Text>
                      <Group gap="xs">
                        <ActionIcon
                          variant="subtle"
                          aria-label="Move stage up"
                          disabled={index === 0}
                          onClick={() => moveStage(index, -1)}
                        >
                          <IconArrowUp size={18} />
                        </ActionIcon>
                        <ActionIcon
                          variant="subtle"
                          aria-label="Move stage down"
                          disabled={index === draft.pipeline.stages.length - 1}
                          onClick={() => moveStage(index, 1)}
                        >
                          <IconArrowDown size={18} />
                        </ActionIcon>
                        <ActionIcon
                          variant="subtle"
                          color="red"
                          aria-label="Remove stage"
                          onClick={() => setDraft({
                            ...draft,
                            pipeline: {
                              ...draft.pipeline,
                              stages: draft.pipeline.stages.filter((_, stageIndex) => stageIndex !== index)
                            }
                          })}
                        >
                          <IconTrash size={18} />
                        </ActionIcon>
                      </Group>
                    </Group>
                    <Box className="stage-primary-grid">
                      <TextInput
                        label="Stage name"
                        value={stage.name ?? ''}
                        onChange={(event) => updateStage(index, { ...stage, name: event.currentTarget.value })}
                      />
                      <Select
                        label="Stage endpoint"
                        data={endpointSelectData(endpoints)}
                        value={endpointValue(stage.agent?.endpoint_id)}
                        searchable
                        onChange={(value) => {
                          const nextEndpointId = endpointIdFromValue(value);
                          updateStage(index, { ...stage, agent: { ...stage.agent, endpoint_id: nextEndpointId, model: null } });
                          void loadModelsForEndpoint(nextEndpointId);
                        }}
                      />
                      <Select
                        label="Stage model"
                        placeholder="Use default model"
                        clearable
                        searchable
                        data={modelsForEndpoint(stage.agent?.endpoint_id, stage.agent?.model)}
                        value={stage.agent?.model ?? null}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, model: value } })}
                      />
                      <NumberInput
                        label="Repeat"
                        min={1}
                        value={stage.repeat}
                        onChange={(value) => updateStage(index, { ...stage, repeat: Number(value) || 1 })}
                      />
                    </Box>
                    <Box className="stage-secondary-grid">
                      <NumberInput
                        label="Temperature"
                        min={0}
                        max={2}
                        step={0.1}
                        value={stage.agent?.temperature ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, temperature: nullableNumber(value) } })}
                      />
                      <NumberInput
                        label="Top P"
                        min={0}
                        max={1}
                        step={0.05}
                        value={stage.agent?.top_p ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, top_p: nullableNumber(value) } })}
                      />
                      <NumberInput
                        label="Top K"
                        min={0}
                        step={1}
                        value={stage.agent?.top_k ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, top_k: nullableNumber(value) } })}
                      />
                      <NumberInput
                        label="Min P"
                        min={0}
                        max={1}
                        step={0.01}
                        value={stage.agent?.min_p ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, min_p: nullableNumber(value) } })}
                      />
                      <NumberInput
                        label="Repeat penalty"
                        min={0}
                        step={0.05}
                        value={stage.agent?.repeat_penalty ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, repeat_penalty: nullableNumber(value) } })}
                      />
                      <NumberInput
                        label="Max tokens"
                        min={1}
                        value={stage.agent?.max_tokens ?? undefined}
                        onChange={(value) => updateStage(index, { ...stage, agent: { ...stage.agent, max_tokens: nullableNumber(value) } })}
                      />
                    </Box>
                    <Group grow>
                      <Select
                        label="Original messages"
                        data={originalMessageInputData}
                        value={stage.input?.original_messages ?? defaultStageInput(index).original_messages}
                        onChange={(value) => updateStage(index, {
                          ...stage,
                          input: {
                            ...(stage.input ?? {}),
                            original_messages: (value ?? defaultStageInput(index).original_messages) as 'none' | 'text' | 'full'
                          }
                        })}
                      />
                      <Select
                        label="Prior output"
                        data={priorStageOutputInputData}
                        value={stage.input?.prior_stage_output ?? defaultStageInput(index).prior_stage_output}
                        onChange={(value) => updateStage(index, {
                          ...stage,
                          input: {
                            ...(stage.input ?? {}),
                            prior_stage_output: (value ?? defaultStageInput(index).prior_stage_output) as 'none' | 'last'
                          }
                        })}
                      />
                    </Group>
                    <Textarea
                      label="Protocol override"
                      description={stageProtocolSource(stage, draft.pipeline.protocol, pipelineSettings)}
                      autosize
                      minRows={2}
                      maxRows={10}
                      value={stage.protocol ?? ''}
                      onChange={(event) => updateStage(index, { ...stage, protocol: event.currentTarget.value || null })}
                    />
                    <Textarea
                      label="Instructions"
                      autosize
                      minRows={2}
                      maxRows={10}
                      value={stage.instructions ?? ''}
                      onChange={(event) => updateStage(index, { ...stage, instructions: event.currentTarget.value })}
                    />
                  </Stack>
                </Card>
              ))}
            </Stack>
            <Group justify="space-between">
              <Button variant="light" leftSection={<IconPlus size={16} />} onClick={() => setDraft({ ...draft, pipeline: { ...draft.pipeline, stages: [...draft.pipeline.stages, emptyStage()] } })}>
                Add stage
              </Button>
              <Group>
                <Button variant="light" leftSection={<IconCopy size={16} />} onClick={() => cloneModel(draft)}>
                  Clone
                </Button>
                <Button variant="light" leftSection={<IconDownload size={16} />} onClick={exportDraftModel}>
                  Export JSON
                </Button>
                <Button variant="subtle" color="red" leftSection={<IconTrash size={16} />} onClick={() => void remove()}>
                  Delete
                </Button>
                <Button leftSection={<IconSettings size={16} />} onClick={() => void save()}>
                  Save model
                </Button>
              </Group>
            </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function SettingsPage() {
  const [endpoints, setEndpoints] = useState<ModelEndpoint[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draft, setDraft] = useState<SaveModelEndpointRequest>({
    name: '',
    type: 'openai_compatible',
    base_url: '',
    api_key: ''
  });
  const [pipelineSettings, setPipelineSettings] = useState<PipelineSettings | null>(null);
  const [pipelineProtocolDraft, setPipelineProtocolDraft] = useState('');
  const [settingsLoadError, setSettingsLoadError] = useState<string | null>(null);
  const [savingPipelineProtocol, setSavingPipelineProtocol] = useState(false);
  const [models, setModels] = useState<string[]>([]);
  const [loadingEndpoints, setLoadingEndpoints] = useState(false);
  const [endpointLoadError, setEndpointLoadError] = useState<string | null>(null);
  const [loadingModels, setLoadingModels] = useState(false);

  const selected = useMemo(
    () => endpoints.find((endpoint) => endpoint.id === selectedId),
    [endpoints, selectedId]
  );

  useEffect(() => {
    void refresh();
    void refreshPipelineSettings();
  }, []);

  useEffect(() => {
    if (!selected) return;
    setDraft({
      id: selected.id,
      name: selected.name,
      type: selected.type,
      base_url: selected.base_url,
      api_key: ''
    });
    setModels([]);
  }, [selected]);

  async function refresh() {
    setLoadingEndpoints(true);
    try {
      const items = await listModelEndpoints();
      setEndpoints(items);
      setEndpointLoadError(null);
      if (!selectedId && items[0]) setSelectedId(items[0].id);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to load endpoints';
      setEndpointLoadError(message);
      notifications.show({ color: 'red', message });
    } finally {
      setLoadingEndpoints(false);
    }
  }

  async function refreshPipelineSettings() {
    try {
      const settings = await getPipelineSettings();
      setPipelineSettings(settings);
      setPipelineProtocolDraft(settings.pipeline_protocol ?? settings.built_in_pipeline_protocol);
      setSettingsLoadError(null);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to load pipeline protocol';
      setSettingsLoadError(message);
      notifications.show({ color: 'red', message });
    }
  }

  async function savePipelineProtocol() {
    setSavingPipelineProtocol(true);
    try {
      const nextProtocol = pipelineProtocolDraft.trim();
      const builtInProtocol = pipelineSettings?.built_in_pipeline_protocol.trim() ?? '';
      const saved = await savePipelineSettings({
        pipeline_protocol: nextProtocol && nextProtocol !== builtInProtocol ? nextProtocol : null
      });
      setPipelineSettings(saved);
      setPipelineProtocolDraft(saved.pipeline_protocol ?? saved.built_in_pipeline_protocol);
      notifications.show({ color: 'green', message: 'Pipeline protocol saved' });
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to save pipeline protocol' });
    } finally {
      setSavingPipelineProtocol(false);
    }
  }

  async function resetPipelineProtocol() {
    setSavingPipelineProtocol(true);
    try {
      const saved = await savePipelineSettings({ pipeline_protocol: null });
      setPipelineSettings(saved);
      setPipelineProtocolDraft(saved.built_in_pipeline_protocol);
      notifications.show({ color: 'green', message: 'Pipeline protocol reset to default' });
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to reset pipeline protocol' });
    } finally {
      setSavingPipelineProtocol(false);
    }
  }

  async function refreshModels(endpointId: string) {
    setLoadingModels(true);
    try {
      setModels(await listEndpointModels(endpointId));
    } catch (error) {
      setModels([]);
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to load models' });
    } finally {
      setLoadingModels(false);
    }
  }

  async function saveEndpoint() {
    try {
      const apiKey = draft.api_key?.trim();
      const saved = await saveModelEndpoint(draft.type === 'codex_subscription'
        ? { id: draft.id, name: draft.name, type: draft.type, base_url: null, api_key: null }
        : { ...draft, api_key: apiKey ? apiKey : undefined });
      notifications.show({ color: 'green', message: `Saved ${saved.name}` });
      await refresh();
      setSelectedId(saved.id);
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to save endpoint' });
    }
  }

  async function removeEndpoint() {
    if (!selectedId) return;
    try {
      await deleteModelEndpoint(selectedId);
      notifications.show({ color: 'green', message: 'Endpoint deleted' });
      setSelectedId(null);
      setDraft({ name: '', type: 'openai_compatible', base_url: '', api_key: '' });
      setModels([]);
      await refresh();
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof Error ? error.message : 'Failed to delete endpoint' });
    }
  }

  return (
    <Stack gap="lg">
      <Box>
        <Title order={2}>Settings</Title>
        <Text c="dimmed">Pipeline protocol, Codex account, and model endpoints.</Text>
      </Box>

      {endpointLoadError && (
        <Alert color="red" title="Endpoint list unavailable">
          Saved endpoints could not be loaded from the local database. {endpointLoadError}
        </Alert>
      )}

      <Paper withBorder p="lg">
        <Stack>
          <Group justify="space-between">
            <Title order={3}>Pipeline protocol</Title>
            <Badge variant="light">{hasProtocol(pipelineSettings?.pipeline_protocol) ? 'Custom' : 'Default'}</Badge>
          </Group>
          {settingsLoadError && (
            <Alert color="red" title="Pipeline protocol unavailable">
              {settingsLoadError}
            </Alert>
          )}
          <Textarea
            label="Pipeline protocol"
            autosize
            maxRows={10}
            minRows={2}
            value={pipelineProtocolDraft}
            onChange={(event) => setPipelineProtocolDraft(event.currentTarget.value)}
          />
          <Group justify="space-between">
            <Group>
              <Button
                variant="subtle"
                disabled={!pipelineSettings?.built_in_pipeline_protocol}
                onClick={() => void resetPipelineProtocol()}
              >
                Reset to default
              </Button>
            </Group>
            <Button loading={savingPipelineProtocol} leftSection={<IconSettings size={16} />} onClick={() => void savePipelineProtocol()}>
              Save protocol
            </Button>
          </Group>
        </Stack>
      </Paper>

      <CodexAccountPanel />

      <Box className="models-grid">
        <Paper withBorder p="sm">
          <Stack gap="sm">
            <Button fullWidth leftSection={<IconPlus size={16} />} onClick={() => { setSelectedId(null); setDraft({ name: '', type: 'openai_compatible', base_url: '', api_key: '' }); setModels([]); }}>
              New endpoint
            </Button>
            {loadingEndpoints && endpoints.length === 0 && <Loader size="sm" />}
            {endpoints.map((endpoint) => (
              <NavLink
                key={endpoint.id}
                active={endpoint.id === selectedId}
                label={endpoint.name}
                description={endpoint.type === 'codex_subscription' ? 'Codex subscription' : endpoint.base_url}
                leftSection={<IconSettings size={18} />}
                onClick={() => setSelectedId(endpoint.id)}
              />
            ))}
            {endpoints.length === 0 && <Text c="dimmed" p="sm">No endpoints saved.</Text>}
          </Stack>
        </Paper>

        <Paper withBorder p="lg">
          <Stack>
            {selected && (
              <TextInput
                label="Endpoint id"
                value={selected.id}
                readOnly
              />
            )}
            <Box className="endpoint-fields">
              <TextInput
                label="Name"
                value={draft.name}
                onChange={(event) => setDraft({ ...draft, name: event.currentTarget.value })}
              />
              <Select
                label="Type"
                data={[
                  { value: 'openai_compatible', label: 'OpenAI-compatible' },
                  { value: 'codex_subscription', label: 'Codex subscription' }
                ]}
                value={draft.type}
                allowDeselect={false}
                onChange={(value) => setDraft({
                  ...draft,
                  type: value as ModelEndpointType,
                  base_url: value === 'codex_subscription' ? null : draft.base_url ?? '',
                  api_key: value === 'codex_subscription' ? null : draft.api_key ?? ''
                })}
              />
            </Box>
            {draft.type === 'openai_compatible' && (
              <Box className="endpoint-fields">
              <TextInput
                label="Base URL"
                placeholder="http://localhost:8080"
                value={draft.base_url ?? ''}
                onChange={(event) => setDraft({ ...draft, base_url: event.currentTarget.value })}
              />
              <TextInput
                label="API key"
                placeholder={selected?.has_api_key ? 'Leave blank to keep saved key' : 'Optional'}
                value={draft.api_key ?? ''}
                onChange={(event) => setDraft({ ...draft, api_key: event.currentTarget.value })}
              />
              </Box>
            )}
            <Group justify="space-between">
              <Button
                variant="light"
                loading={loadingModels}
                disabled={!selectedId}
                onClick={() => selectedId && void refreshModels(selectedId)}
              >
                Refresh models
              </Button>
              <Group>
                <Button variant="subtle" color="red" leftSection={<IconTrash size={16} />} disabled={!selectedId} onClick={() => void removeEndpoint()}>
                  Delete
                </Button>
                <Button leftSection={<IconSettings size={16} />} onClick={() => void saveEndpoint()}>
                  Save endpoint
                </Button>
              </Group>
            </Group>
            <Divider label="Models" />
            <Stack gap={6}>
              {models.map((model) => <Badge key={model} variant="light">{model}</Badge>)}
              {models.length === 0 && <Text c="dimmed">No models loaded.</Text>}
            </Stack>
          </Stack>
        </Paper>
      </Box>
    </Stack>
  );
}

type RunTraceEvent = {
  type: string;
  json: string;
  createdAt?: string;
};

type RunReasoning = {
  stageIndex: number;
  executionIndex: number;
  iterationIndex: number;
  label: string;
  content: string;
};

type RunStageOutput = {
  stageIndex: number;
  label: string;
  content: string;
  model?: string | null;
  endpointId?: string | null;
  createdAt?: string;
};

type RunRecord = {
  runId: string;
  requestId?: string;
  clientId?: string | null;
  status: string;
  preview?: string;
  createdAt?: string;
  updatedAt?: string;
  requestJson?: string | null;
  responseJson?: string | null;
  events?: RunTraceEvent[];
  reasonings?: RunReasoning[];
};

function formatJson(value?: string | null) {
  if (!value) return 'Not stored.';

  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function parseTracePayload(event: RunTraceEvent) {
  try {
    const parsed = JSON.parse(event.json);
    return parsed && typeof parsed === 'object' ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

function stageOutputsFromEvents(events: RunTraceEvent[] = []) {
  const labels = new Map<number, string>();
  const requests = new Map<number, { model?: string | null; endpointId?: string | null }>();
  const outputs: RunStageOutput[] = [];

  for (const event of events) {
    const payload = parseTracePayload(event);
    if (!payload) continue;

    const stageIndex = typeof payload.stage_index === 'number' ? payload.stage_index : null;
    if (stageIndex === null) continue;

    if (event.type === 'stage_started') {
      const stageName = typeof payload.stage_name === 'string' ? payload.stage_name.trim() : '';
      labels.set(stageIndex, stageName || `Stage ${stageIndex + 1}`);
      continue;
    }

    if (event.type === 'agent_request') {
      requests.set(stageIndex, {
        model: typeof payload.model === 'string' ? payload.model : null,
        endpointId: typeof payload.endpoint_id === 'string' ? payload.endpoint_id : null
      });
      continue;
    }

    if (event.type === 'agent_response') {
      const request = requests.get(stageIndex);
      const content = typeof payload.content === 'string'
        ? payload.content
        : JSON.stringify(payload.content ?? payload, null, 2);
      outputs.push({
        stageIndex,
        label: labels.get(stageIndex) ?? `Stage ${stageIndex + 1}`,
        content,
        model: request?.model,
        endpointId: request?.endpointId,
        createdAt: event.createdAt
      });
    }
  }

  return outputs;
}

function RunsPage() {
  const isSmall = useMediaQuery('(max-width: 700px)');
  const [runs, setRuns] = useState<RunRecord[]>([]);
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [runDialogOpen, setRunDialogOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const selectedRun = useMemo(
    () => runs.find((run) => run.runId === selectedRunId) ?? null,
    [runs, selectedRunId]
  );
  const selectedRunOutputs = useMemo(
    () => stageOutputsFromEvents(selectedRun?.events ?? []),
    [selectedRun?.events]
  );

  async function load() {
    setLoading(true);
    try {
      const response = await fetch('/v1/orchestrations');
      const body = await response.json();
      const items: RunRecord[] = body.runs ?? body ?? [];
      setRuns(items);
      setSelectedRunId((current) => current && items.some((run) => run.runId === current) ? current : items[0]?.runId ?? null);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function clear() {
    await fetch('/v1/orchestrations', { method: 'DELETE' });
    setSelectedRunId(null);
    setRunDialogOpen(false);
    await load();
  }

  function openRun(runId: string) {
    setSelectedRunId(runId);
    setRunDialogOpen(true);
  }

  return (
    <Stack>
      <Group justify="space-between">
        <Box>
          <Title order={2}>Runs</Title>
          <Text c="dimmed">Stored orchestration traces.</Text>
        </Box>
        <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={() => void clear()}>
          Clear runs
        </Button>
      </Group>
      <Paper withBorder>
        {loading ? (
          <Box p="lg"><Loader size="sm" /></Box>
        ) : (
          <Box className="table-scroll">
            <Table verticalSpacing="sm" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Run</Table.Th>
                  <Table.Th>Status</Table.Th>
                  <Table.Th>Preview</Table.Th>
                  <Table.Th className="optional-column">Client</Table.Th>
                  <Table.Th className="optional-column">Updated</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {runs.map((run) => (
                  <Table.Tr key={run.runId} className="clickable-row" onClick={() => openRun(run.runId)}>
                    <Table.Td><Text fw={600} className="cell-wrap">{run.runId}</Text></Table.Td>
                    <Table.Td><Badge variant="light">{run.status}</Badge></Table.Td>
                    <Table.Td><Text c="dimmed" className="cell-wrap">{run.preview}</Text></Table.Td>
                    <Table.Td className="optional-column"><Text className="cell-wrap">{run.clientId ?? 'n/a'}</Text></Table.Td>
                    <Table.Td className="optional-column"><Text>{run.updatedAt ? new Date(run.updatedAt).toLocaleString() : 'n/a'}</Text></Table.Td>
                  </Table.Tr>
                ))}
                {runs.length === 0 && (
                  <Table.Tr>
                    <Table.Td colSpan={5}><Text c="dimmed" p="sm">No runs stored.</Text></Table.Td>
                  </Table.Tr>
                )}
              </Table.Tbody>
            </Table>
          </Box>
        )}
      </Paper>

      <Modal
        opened={runDialogOpen}
        onClose={() => setRunDialogOpen(false)}
        title={selectedRun?.runId ?? 'Run detail'}
        size="95%"
        fullScreen={isSmall}
        centered={!isSmall}
      >
        {selectedRun && (
          <Stack>
            <Group justify="space-between" align="start">
              <Box>
                <Text c="dimmed">{selectedRun.preview}</Text>
              </Box>
              <Badge variant="light">{selectedRun.status}</Badge>
            </Group>

            <Box className="run-meta-grid">
              <Box>
                <Text size="xs" c="dimmed" tt="uppercase">Request</Text>
                <Text className="cell-wrap">{selectedRun.requestId ?? 'n/a'}</Text>
              </Box>
              <Box>
                <Text size="xs" c="dimmed" tt="uppercase">Client</Text>
                <Text className="cell-wrap">{selectedRun.clientId ?? 'n/a'}</Text>
              </Box>
              <Box>
                <Text size="xs" c="dimmed" tt="uppercase">Created</Text>
                <Text>{selectedRun.createdAt ? new Date(selectedRun.createdAt).toLocaleString() : 'n/a'}</Text>
              </Box>
              <Box>
                <Text size="xs" c="dimmed" tt="uppercase">Updated</Text>
                <Text>{selectedRun.updatedAt ? new Date(selectedRun.updatedAt).toLocaleString() : 'n/a'}</Text>
              </Box>
            </Box>

            <Tabs defaultValue="summary">
              <Tabs.List>
                <Tabs.Tab value="summary">Summary</Tabs.Tab>
                <Tabs.Tab value="request">Request</Tabs.Tab>
                <Tabs.Tab value="response">Response</Tabs.Tab>
                <Tabs.Tab value="events">Events</Tabs.Tab>
                <Tabs.Tab value="reasoning">Reasoning</Tabs.Tab>
                <Tabs.Tab value="outputs">Outputs</Tabs.Tab>
              </Tabs.List>

              <Tabs.Panel value="summary" pt="md">
                <Stack>
                  <Text>{selectedRun.preview}</Text>
                  <Text c="dimmed">
                    {(selectedRun.events ?? []).length} events, {(selectedRun.reasonings ?? []).length} reasoning records, {selectedRunOutputs.length} stage outputs
                  </Text>
                </Stack>
              </Tabs.Panel>
              <Tabs.Panel value="request" pt="md">
                <pre className="run-json">{formatJson(selectedRun.requestJson)}</pre>
              </Tabs.Panel>
              <Tabs.Panel value="response" pt="md">
                <pre className="run-json">{formatJson(selectedRun.responseJson)}</pre>
              </Tabs.Panel>
              <Tabs.Panel value="events" pt="md">
                <Stack gap="xs">
                  {(selectedRun.events ?? []).map((event, index) => (
                    <Box key={`${event.type}-${index}`} className="run-detail-item">
                      <Group justify="space-between">
                        <Text fw={600}>{event.type}</Text>
                        {event.createdAt && <Text size="xs" c="dimmed">{new Date(event.createdAt).toLocaleString()}</Text>}
                      </Group>
                      <pre className="run-json">{formatJson(event.json)}</pre>
                    </Box>
                  ))}
                  {(selectedRun.events ?? []).length === 0 && <Text c="dimmed">No trace events stored.</Text>}
                </Stack>
              </Tabs.Panel>
              <Tabs.Panel value="reasoning" pt="md">
                <Stack gap="xs">
                  {(selectedRun.reasonings ?? []).map((reasoning) => (
                    <Box
                      key={`${reasoning.stageIndex}-${reasoning.executionIndex}-${reasoning.iterationIndex}-${reasoning.label}`}
                      className="run-detail-item"
                    >
                      <Text fw={600}>{reasoning.label}</Text>
                      <Text size="xs" c="dimmed">
                        Stage {reasoning.stageIndex + 1}, execution {reasoning.executionIndex + 1}, iteration {reasoning.iterationIndex + 1}
                      </Text>
                      <pre className="run-json">{reasoning.content}</pre>
                    </Box>
                  ))}
                  {(selectedRun.reasonings ?? []).length === 0 && <Text c="dimmed">No reasoning stored.</Text>}
                </Stack>
              </Tabs.Panel>
              <Tabs.Panel value="outputs" pt="md">
                <Stack gap="xs">
                  {selectedRunOutputs.map((output) => (
                    <Box key={`${output.stageIndex}-${output.createdAt ?? output.label}`} className="run-detail-item">
                      <Group justify="space-between" align="start">
                        <Box>
                          <Text fw={600}>{output.label}</Text>
                          <Text size="xs" c="dimmed">
                            Execution {output.stageIndex + 1}
                            {output.model ? `, model ${output.model}` : ''}
                            {output.endpointId ? `, endpoint ${output.endpointId}` : ''}
                          </Text>
                        </Box>
                        {output.createdAt && <Text size="xs" c="dimmed">{new Date(output.createdAt).toLocaleString()}</Text>}
                      </Group>
                      <pre className="run-json">{output.content || 'No output.'}</pre>
                    </Box>
                  ))}
                  {selectedRunOutputs.length === 0 && <Text c="dimmed">No stage outputs stored.</Text>}
                </Stack>
              </Tabs.Panel>
            </Tabs>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
