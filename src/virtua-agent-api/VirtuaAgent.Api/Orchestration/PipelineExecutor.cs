using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using VirtuaAgent.OpenAi;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.PipelineModels;
using VirtuaAgent.Settings;
using VirtuaAgent.Tracing;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.Orchestration;

public sealed class PipelineExecutor(
    IOpenAiCompatibleUpstreamClient upstreamClient,
    ModelEndpointDispatcher endpointDispatcher,
    IModelEndpointStore modelEndpointStore,
    IPipelineSettingsStore pipelineSettingsStore,
    ITraceStore traceStore,
    ActiveTraceHub traceHub)
{
    public async Task<ChatCompletionResponse> ExecuteAsync(
        string runId,
        ChatCompletionRequest request,
        bool store,
        CancellationToken cancellationToken = default)
    {
        var pipeline = Compile(request);
        var settings = await pipelineSettingsStore.GetAsync(cancellationToken);
        var context = new PipelineContext(runId, request.Messages);
        ChatCompletionResponse? lastResponse = null;
        var executionIndex = 0;

        for (var stageIndex = 0; stageIndex < pipeline.Stages.Count; stageIndex++)
        {
            var stage = pipeline.Stages[stageIndex];
            var random = stage.Seed is null ? Random.Shared : new Random(stage.Seed.Value);
            for (var repeatIndex = 0; repeatIndex < stage.Repeat; repeatIndex++)
            {
                await PublishAsync(runId, "stage_started", new { stage_index = executionIndex, stage_type = stage.Type, stage_name = stage.Name }, store, cancellationToken);
                var agent = SelectAgent(stage, random);
                var stageRequest = BuildSingleAgentRequest(request, context, pipeline, stage, agent, executionIndex, settings.PipelineProtocol);
                var endpoint = await ResolveEndpointAsync(agent?.EndpointId ?? pipeline.DefaultEndpointId, $"orchestration.pipeline.stages[{stageIndex}].agent.endpoint_id", cancellationToken);
                ValidateCodexRequest(stageRequest, endpoint);
                await PublishAsync(runId, "agent_request", new
                {
                    stage_index = executionIndex,
                    endpoint_id = endpoint?.Id,
                    model = stageRequest.Model,
                    instructions_preview = PreviewInstructions(stage.Instructions)
                }, store, cancellationToken);

                lastResponse = endpoint is null
                    ? await upstreamClient.ChatAsync(stageRequest, cancellationToken)
                    : await endpointDispatcher.ChatAsync(stageRequest, endpoint, cancellationToken);
                context.CurrentAnswer = lastResponse.Choices.FirstOrDefault()?.Message.Content.AsText() ?? "";
                context.CurrentAnswerLabel = BuildStageOutputLabel(stageIndex, repeatIndex, stage);

                await PublishAsync(runId, "agent_response", new { stage_index = executionIndex, content = context.CurrentAnswer }, store, cancellationToken);
                await PublishAsync(runId, "stage_completed", new { stage_index = executionIndex }, store, cancellationToken);
                executionIndex++;
            }
        }

        if (lastResponse is null)
        {
            throw new PipelineValidationException("Pipeline must contain at least one executable stage.", "orchestration.pipeline.stages", "empty_pipeline");
        }

        return lastResponse;
    }

    public async Task<ChatCompletionResponse> ExecuteStreamingAsync(
        string runId,
        ChatCompletionRequest request,
        bool store,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        var pipeline = Compile(request);
        var settings = await pipelineSettingsStore.GetAsync(cancellationToken);
        var context = new PipelineContext(runId, request.Messages);
        ChatCompletionResponse? lastResponse = null;
        var executionIndex = 0;

        for (var stageIndex = 0; stageIndex < pipeline.Stages.Count; stageIndex++)
        {
            var stage = pipeline.Stages[stageIndex];
            var random = stage.Seed is null ? Random.Shared : new Random(stage.Seed.Value);
            for (var repeatIndex = 0; repeatIndex < stage.Repeat; repeatIndex++)
            {
                await PublishAsync(runId, "stage_started", new { stage_index = executionIndex, stage_type = stage.Type, stage_name = stage.Name }, store, cancellationToken);
                var agent = SelectAgent(stage, random);
                var stageRequest = BuildSingleAgentRequest(request, context, pipeline, stage, agent, executionIndex, settings.PipelineProtocol) with { Stream = true };
                var endpoint = await ResolveEndpointAsync(agent?.EndpointId ?? pipeline.DefaultEndpointId, $"orchestration.pipeline.stages[{stageIndex}].agent.endpoint_id", cancellationToken);
                ValidateCodexRequest(stageRequest, endpoint);
                await PublishAsync(runId, "agent_request", new
                {
                    stage_index = executionIndex,
                    endpoint_id = endpoint?.Id,
                    model = stageRequest.Model,
                    instructions_preview = PreviewInstructions(stage.Instructions)
                }, store, cancellationToken);

                var stageContent = "";
                var responseId = "";
                var responseCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var responseModel = stageRequest.Model ?? "";
                string? finishReason = null;
                var thinkExtractor = new ThinkTagStreamExtractor();
                var reasoningMetadata = BuildReasoningMetadata(stageIndex, executionIndex, repeatIndex, stage);
                var wroteReasoningStageHeader = false;

                async Task WriteReasoningStageHeaderAsync(CancellationToken token)
                {
                    if (wroteReasoningStageHeader)
                    {
                        return;
                    }

                    wroteReasoningStageHeader = true;
                    await WriteReasoningChunkAsync(output, responseId, responseCreated, responseModel, BuildReasoningStageHeader(reasoningMetadata), token);
                }

                var onDataAsync = async (string data, CancellationToken token) =>
                {
                    var delta = OpenAiStreamData.ParseDelta(data);
                    if (delta is null)
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(delta.Id))
                    {
                        responseId = delta.Id;
                    }

                    if (delta.Created is not null)
                    {
                        responseCreated = delta.Created.Value;
                    }

                    if (!string.IsNullOrEmpty(delta.Model))
                    {
                        responseModel = delta.Model;
                    }

                    if (!string.IsNullOrEmpty(delta.FinishReason))
                    {
                        finishReason = delta.FinishReason;
                    }

                    if (!string.IsNullOrEmpty(delta.Reasoning))
                    {
                        await WriteReasoningStageHeaderAsync(token);
                        await AppendAndWriteReasoningAsync(runId, store, output, responseId, responseCreated, responseModel, reasoningMetadata, delta.Reasoning, token);
                    }

                    if (!string.IsNullOrEmpty(delta.Content))
                    {
                        var extracted = thinkExtractor.Add(delta.Content);
                        stageContent += extracted.Answer;
                        foreach (var reasoning in extracted.Reasonings)
                        {
                            await WriteReasoningStageHeaderAsync(token);
                            await AppendAndWriteReasoningAsync(runId, store, output, responseId, responseCreated, responseModel, reasoningMetadata, reasoning, token);
                        }
                    }
                };

                if (endpoint is null)
                {
                    await upstreamClient.StreamChatAsync(stageRequest, onDataAsync, cancellationToken);
                }
                else
                {
                    await endpointDispatcher.StreamChatAsync(stageRequest, endpoint, onDataAsync, cancellationToken);
                }

                stageContent += thinkExtractor.Complete();
                context.CurrentAnswer = stageContent.Trim();
                context.CurrentAnswerLabel = BuildStageOutputLabel(stageIndex, repeatIndex, stage);
                await WriteReasoningChunkAsync(output, responseId, responseCreated, responseModel, BuildReasoningStageOutput(context.CurrentAnswer), cancellationToken);
                lastResponse = new ChatCompletionResponse
                {
                    Id = string.IsNullOrWhiteSpace(responseId) ? "chatcmpl_" + Guid.NewGuid().ToString("N") : responseId,
                    Created = responseCreated,
                    Model = responseModel,
                    Choices =
                    [
                        new ChatCompletionChoiceDto
                        {
                            Index = 0,
                            Message = new ChatMessageDto { Role = "assistant", Content = context.CurrentAnswer },
                            FinishReason = finishReason ?? "stop"
                        }
                    ]
                };

                await PublishAsync(runId, "agent_response", new { stage_index = executionIndex, content = context.CurrentAnswer }, store, cancellationToken);
                await PublishAsync(runId, "stage_completed", new { stage_index = executionIndex }, store, cancellationToken);
                executionIndex++;
            }
        }

        if (lastResponse is null)
        {
            throw new PipelineValidationException("Pipeline must contain at least one executable stage.", "orchestration.pipeline.stages", "empty_pipeline");
        }

        return lastResponse;
    }

    private static PipelineDefinition Compile(ChatCompletionRequest request)
    {
        var stages = request.Orchestration?.Pipeline?.Stages;
        if (stages is null || stages.Count == 0)
        {
            throw new PipelineValidationException("Pipeline must contain at least one stage.", "orchestration.pipeline.stages", "empty_pipeline");
        }

        var definitions = new List<PipelineStageDefinition>();
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            if (!stage.Type.Equals("single_agent", StringComparison.OrdinalIgnoreCase))
            {
                throw new PipelineValidationException(
                    $"{stage.Type} stage is not supported in MVP.",
                    $"orchestration.pipeline.stages[{index}].type",
                    "stage_not_supported");
            }

            if (stage.Repeat < 1)
            {
                throw new PipelineValidationException(
                    "Stage repeat must be at least 1.",
                    $"orchestration.pipeline.stages[{index}].repeat",
                    "invalid_repeat");
            }

            definitions.Add(new PipelineStageDefinition(
                stage.Type,
                stage.Name,
                stage.Repeat,
                stage.Instructions,
                stage.Protocol,
                CompileInput(stage.Input, index),
                stage.Agent,
                stage.AgentSelection,
                stage.Seed,
                stage.Agents));
        }

        ValidateExpandedInstructions(definitions);

        return new PipelineDefinition(
            request.Orchestration?.Pipeline?.DefaultEndpointId,
            request.Orchestration?.Pipeline?.DefaultModel,
            request.Orchestration?.Pipeline?.DefaultTemperature,
            request.Orchestration?.Pipeline?.DefaultTopP,
            request.Orchestration?.Pipeline?.DefaultTopK,
            request.Orchestration?.Pipeline?.DefaultMinP,
            request.Orchestration?.Pipeline?.DefaultRepeatPenalty,
            request.Orchestration?.Pipeline?.DefaultMaxTokens,
            request.Orchestration?.Pipeline?.Protocol,
            definitions);
    }

    private static void ValidateExpandedInstructions(List<PipelineStageDefinition> stages)
    {
        var executionIndex = 0;
        for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            var stage = stages[stageIndex];
            for (var repeatIndex = 0; repeatIndex < stage.Repeat; repeatIndex++)
            {
                if (executionIndex > 0 && string.IsNullOrWhiteSpace(stage.Instructions))
                {
                    throw new PipelineValidationException(
                        "Only the first pipeline execution may omit stage instructions.",
                        $"orchestration.pipeline.stages[{stageIndex}].instructions",
                        "instructions_required");
                }

                var input = PipelineStageInputDefinition.Resolve(stage.Input, executionIndex);
                if (executionIndex == 0 && input.PriorStageOutput == "last")
                {
                    throw new PipelineValidationException(
                        "The first pipeline execution cannot include prior stage output.",
                        $"orchestration.pipeline.stages[{stageIndex}].input.prior_stage_output",
                        "invalid_stage_input");
                }

                executionIndex++;
            }
        }
    }

    private static PipelineStageInputDefinition? CompileInput(PipelineStageInputRequestDto? input, int stageIndex)
    {
        if (input is null)
        {
            return null;
        }

        var originalMessages = NormalizeSelector(
            input.OriginalMessages,
            ["none", "text", "full"],
            $"orchestration.pipeline.stages[{stageIndex}].input.original_messages");

        var priorStageOutput = NormalizeSelector(
            input.PriorStageOutput,
            ["none", "last"],
            $"orchestration.pipeline.stages[{stageIndex}].input.prior_stage_output");

        return originalMessages is null && priorStageOutput is null
            ? null
            : new PipelineStageInputDefinition(originalMessages, priorStageOutput);
    }

    private static string? NormalizeSelector(string? value, string[] allowed, string param)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (allowed.Contains(normalized))
        {
            return normalized;
        }

        throw new PipelineValidationException(
            $"{param} must be one of: {string.Join(", ", allowed)}.",
            param,
            "invalid_stage_input");
    }

    private static AgentRequestDto? SelectAgent(PipelineStageDefinition stage, Random random)
    {
        if (!string.Equals(stage.AgentSelection, "random", StringComparison.OrdinalIgnoreCase))
        {
            return stage.Agent;
        }

        if (stage.Agents.Count == 0)
        {
            throw new PipelineValidationException("Random agent selection requires agents.", "orchestration.pipeline.stages[].agents", "agents_required");
        }

        return stage.Agents[random.Next(stage.Agents.Count)];
    }

    private static ChatCompletionRequest BuildSingleAgentRequest(
        ChatCompletionRequest originalRequest,
        PipelineContext context,
        PipelineDefinition pipeline,
        PipelineStageDefinition stage,
        AgentRequestDto? agent,
        int executionIndex,
        string? settingsPipelineProtocol)
    {
        var messages = PipelineStagePromptComposer.Compose(
            context,
            stage,
            executionIndex,
            stage.Protocol ?? pipeline.Protocol ?? settingsPipelineProtocol);

        return originalRequest with
        {
            Model = NormalizeModel(agent?.Model) ?? NormalizeModel(pipeline.DefaultModel) ?? originalRequest.Model,
            EndpointId = null,
            Temperature = agent?.Temperature ?? pipeline.DefaultTemperature ?? originalRequest.Temperature,
            TopP = agent?.TopP ?? pipeline.DefaultTopP ?? originalRequest.TopP,
            TopK = agent?.TopK ?? pipeline.DefaultTopK ?? originalRequest.TopK,
            MinP = agent?.MinP ?? pipeline.DefaultMinP ?? originalRequest.MinP,
            RepeatPenalty = agent?.RepeatPenalty ?? pipeline.DefaultRepeatPenalty ?? originalRequest.RepeatPenalty,
            MaxTokens = agent?.MaxTokens ?? pipeline.DefaultMaxTokens ?? originalRequest.MaxTokens,
            Stream = false,
            Orchestration = null,
            Messages = messages
        };
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return model;
    }

    private static void ValidateCodexRequest(ChatCompletionRequest request, ModelEndpointDefinition? endpoint)
    {
        if (endpoint?.Kind != ModelEndpointKinds.CodexSubscription) return;

        var unsupported = request.Temperature is not null ? "temperature"
            : request.TopP is not null ? "top_p"
            : request.TopK is not null ? "top_k"
            : request.MinP is not null ? "min_p"
            : request.RepeatPenalty is not null ? "repeat_penalty"
            : request.MaxTokens is not null ? "max_tokens"
            : request.ExtraFields is { Count: > 0 } ? "request"
            : null;
        if (unsupported is not null)
        {
            throw new PipelineValidationException(
                $"Codex subscription stages do not expose '{unsupported}'.",
                unsupported,
                "codex_parameter_unsupported");
        }
    }

    private async Task<ModelEndpointDefinition?> ResolveEndpointAsync(string? endpointId, string param, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        var endpoint = await modelEndpointStore.GetAsync(endpointId, cancellationToken);
        if (endpoint is null)
        {
            throw new PipelineValidationException(
                $"Model endpoint '{endpointId}' was not found.",
                param,
                "invalid_endpoint");
        }

        return endpoint;
    }

    private static string? PreviewInstructions(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return null;
        }

        return instructions.Length <= 200 ? instructions : instructions[..200] + "...";
    }

    private static ReasoningMetadata BuildReasoningMetadata(int stageIndex, int executionIndex, int repeatIndex, PipelineStageDefinition stage)
    {
        var label = BuildStageOutputLabel(stageIndex, repeatIndex, stage);

        return new ReasoningMetadata
        {
            StageIndex = stageIndex,
            ExecutionIndex = executionIndex,
            IterationIndex = repeatIndex,
            Label = label,
            StageName = stage.Name
        };
    }

    private static string BuildStageOutputLabel(int stageIndex, int repeatIndex, PipelineStageDefinition stage)
    {
        var baseLabel = string.IsNullOrWhiteSpace(stage.Name) ? $"Stage {stageIndex + 1}" : stage.Name;
        return stage.Repeat > 1
            ? $"{baseLabel} #{repeatIndex + 1}"
            : baseLabel;
    }

    private static string BuildReasoningStageHeader(ReasoningMetadata metadata)
    {
        var prefix = metadata.ExecutionIndex == 0 ? "" : "\n\n";
        return $"{prefix}===============\nStage: {metadata.Label}\n===============\n\n";
    }

    private static string BuildReasoningStageOutput(string content) =>
        $"\n\n===============\nStage output:\n{content}\n===============";

    private async Task AppendAndWriteReasoningAsync(
        string runId,
        bool store,
        Stream output,
        string id,
        long created,
        string model,
        ReasoningMetadata metadata,
        string reasoning,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(reasoning))
        {
            return;
        }

        if (store)
        {
            await traceStore.AppendReasoningAsync(
                runId,
                ReasoningRecord.Create(metadata.StageIndex, metadata.ExecutionIndex, metadata.IterationIndex, metadata.Label, reasoning),
                cancellationToken);
        }

        await WriteReasoningChunkAsync(output, id, created, model, reasoning, cancellationToken);
    }

    private static async Task WriteReasoningChunkAsync(
        Stream output,
        string id,
        long created,
        string model,
        string reasoning,
        CancellationToken cancellationToken)
    {
        var chunk = new
        {
            id = string.IsNullOrWhiteSpace(id) ? "chatcmpl_virtua_agent" : id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { reasoning },
                    finish_reason = (string?)null
                }
            }
        };

        var bytes = Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(chunk, JsonOptions.Default)}\n\n");
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private sealed record ReasoningMetadata
    {
        [JsonPropertyName("stage_index")]
        public int StageIndex { get; init; }

        [JsonPropertyName("execution_index")]
        public int ExecutionIndex { get; init; }

        [JsonPropertyName("iteration_index")]
        public int IterationIndex { get; init; }

        public string Label { get; init; } = "";

        [JsonPropertyName("stage_name")]
        public string? StageName { get; init; }
    }

    private async Task PublishAsync(string runId, string type, object payload, bool store, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        var traceEvent = TraceEventRecord.Create(type, json);
        if (store)
        {
            await traceStore.AppendEventAsync(runId, traceEvent, cancellationToken);
        }

        await traceHub.PublishAsync(runId, traceEvent);
    }
}
