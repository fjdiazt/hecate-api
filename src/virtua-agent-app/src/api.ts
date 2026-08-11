import type {
  CodexAccountState,
  ModelDto,
  ModelEndpoint,
  ModelListResponse,
  PipelineSettings,
  SaveModelEndpointRequest,
  SavePipelineSettingsRequest,
  VirtuaAgentModel
} from './types';

const modelDiscoveryTimeoutMs = 8000;

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text();
    throw new Error(errorMessageFrom(text, response.status));
  }

  return response.json() as Promise<T>;
}

function errorMessageFrom(text: string, status: number) {
  if (!text) return `HTTP ${status}`;

  try {
    const parsed = JSON.parse(text);
    if (typeof parsed?.error?.message === 'string') {
      return parsed.error.message;
    }
  } catch {
    return text;
  }

  return text;
}

async function readAccountState(response: Response): Promise<CodexAccountState> {
  const text = await response.text();
  try {
    const state = JSON.parse(text) as CodexAccountState;
    if ((response.ok || response.status === 503) && typeof state.status === 'string') return state;
  } catch {
    // Fall through to the shared HTTP error formatter.
  }

  throw new Error(errorMessageFrom(text, response.status));
}

export async function getCodexAccount(): Promise<CodexAccountState> {
  return readAccountState(await fetch('/v1/codex/account'));
}

export async function startCodexLogin(): Promise<CodexAccountState> {
  return readAccountState(await fetch('/v1/codex/account/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: '{}'
  }));
}

export async function cancelCodexLogin(): Promise<CodexAccountState> {
  return readAccountState(await fetch('/v1/codex/account/login', { method: 'DELETE' }));
}

export async function logoutCodexAccount(): Promise<CodexAccountState> {
  return readAccountState(await fetch('/v1/codex/account', { method: 'DELETE' }));
}

async function fetchModelDiscovery(url: string): Promise<Response> {
  const controller = new AbortController();
  const timeoutId = window.setTimeout(() => controller.abort(), modelDiscoveryTimeoutMs);

  try {
    return await fetch(url, { signal: controller.signal });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error(`Model discovery timed out after ${modelDiscoveryTimeoutMs / 1000} seconds.`);
    }

    throw error;
  } finally {
    window.clearTimeout(timeoutId);
  }
}

export async function listUpstreamModels(): Promise<string[]> {
  const models = await listModelDtos();
  return models
    .filter((model) => model.owned_by !== 'virtua-agent')
    .map((model) => model.id)
    .filter(Boolean);
}

export async function listEndpointModels(endpointId: string): Promise<string[]> {
  const response = await fetchModelDiscovery(`/v1/model-endpoints/${endpointId}/models`);
  const body = await readJson<ModelListResponse>(response);
  return body.data.map((model) => model.id).filter(Boolean);
}

async function listModelDtos(): Promise<ModelDto[]> {
  const response = await fetchModelDiscovery('/v1/models');
  const body = await readJson<ModelListResponse>(response);
  return body.data;
}

export async function listVirtuaAgentModels(): Promise<VirtuaAgentModel[]> {
  const response = await fetch('/v1/pipeline-models');
  return readJson<VirtuaAgentModel[]>(response);
}

export async function listModelEndpoints(): Promise<ModelEndpoint[]> {
  const response = await fetch('/v1/model-endpoints');
  return readJson<ModelEndpoint[]>(response);
}

export async function getPipelineSettings(): Promise<PipelineSettings> {
  const response = await fetch('/v1/settings');
  return readJson<PipelineSettings>(response);
}

export async function savePipelineSettings(settings: SavePipelineSettingsRequest): Promise<PipelineSettings> {
  const response = await fetch('/v1/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings)
  });
  return readJson<PipelineSettings>(response);
}

export async function saveModelEndpoint(endpoint: SaveModelEndpointRequest): Promise<ModelEndpoint> {
  const response = await fetch('/v1/model-endpoints', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(endpoint)
  });
  return readJson<ModelEndpoint>(response);
}

export async function deleteModelEndpoint(id: string): Promise<void> {
  const response = await fetch(`/v1/model-endpoints/${id}`, { method: 'DELETE' });
  if (!response.ok) {
    throw new Error(errorMessageFrom(await response.text(), response.status));
  }
}

export async function saveVirtuaAgentModel(model: VirtuaAgentModel): Promise<VirtuaAgentModel> {
  const response = await fetch('/v1/pipeline-models', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(model)
  });
  return readJson<VirtuaAgentModel>(response);
}

export async function deleteVirtuaAgentModel(id: string): Promise<void> {
  const response = await fetch(`/v1/pipeline-models/${id}`, { method: 'DELETE' });
  if (!response.ok) {
    throw new Error(errorMessageFrom(await response.text(), response.status));
  }
}
