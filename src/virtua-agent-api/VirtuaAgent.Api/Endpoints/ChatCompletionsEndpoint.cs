using System.Text.Json;
using VirtuaAgent.OpenAi;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.Orchestration;
using VirtuaAgent.PipelineModels;
using VirtuaAgent.Tracing;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.Endpoints;

public static class ChatCompletionsEndpoint
{
    public static async Task<IResult> HandleAsync(
        ChatCompletionRequest request,
        HttpContext httpContext,
        IOpenAiCompatibleUpstreamClient upstreamClient,
        IModelEndpointStore modelEndpointStore,
        PipelineExecutor pipelineExecutor,
        PipelinePresetCatalog presetCatalog,
        ITraceStore traceStore,
        ActiveTraceHub traceHub,
        CancellationToken cancellationToken)
    {
        var runId = "run_" + Guid.NewGuid().ToString("N");
        var requestId = httpContext.Request.Headers.TryGetValue("Virtua-Agent-Request-Id", out var requestHeader)
            ? requestHeader.ToString()
            : "req_" + Guid.NewGuid().ToString("N");
        var clientId = httpContext.Request.Headers.TryGetValue("Virtua-Agent-Client-Id", out var clientHeader)
            ? clientHeader.ToString()
            : null;
        var traceUrl = $"/v1/orchestrations/{runId}/events";
        var store = request.Orchestration?.Store != false;

        AddVirtuaAgentHeaders(httpContext.Response, runId, traceUrl);
        if (request.Stream == true)
        {
            httpContext.Response.ContentType = "text/event-stream";
        }

        var requestJson = JsonSerializer.Serialize(RedactRequestForTrace(request), JsonOptions.Default);
        var run = RunRecord.Started(runId, requestId, clientId, PreviewFrom(request), store) with
        {
            RequestJson = requestJson
        };
        await traceStore.CreateRunAsync(run, cancellationToken);
        await PublishAsync(traceStore, traceHub, runId, "run_started", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);

        try
        {
            request = await ApplyPresetPipelineAsync(request, presetCatalog, cancellationToken);
            ModelEndpointDefinition? endpoint = null;
            if (request.Orchestration?.Pipeline is null)
            {
                endpoint = await ResolveEndpointAsync(request.EndpointId, modelEndpointStore, cancellationToken);
                if (endpoint?.Type == ModelEndpointTypes.CodexSubscription)
                {
                    throw new PipelineValidationException(
                        "Codex subscription endpoints can only be used by Virtua Agent pipeline stages.",
                        "endpoint_id",
                        "codex_pipeline_only");
                }
            }

            if (request.Stream == true)
            {
                await httpContext.Response.StartAsync(cancellationToken);
            }

            if (request.Orchestration?.Pipeline is not null)
            {
                await PipelineModelValidator.EnsureNoNestedPipelineModelsAsync(request.Orchestration.Pipeline, presetCatalog, cancellationToken: cancellationToken);
                var pipelineResponse = request.Stream == true
                    ? await pipelineExecutor.ExecuteStreamingAsync(runId, request, store, httpContext.Response.Body, cancellationToken)
                    : await pipelineExecutor.ExecuteAsync(runId, request, store, cancellationToken);
                var pipelineApiResponse = request.Orchestration.IncludeVirtuaAgent
                    ? pipelineResponse with { VirtuaAgent = new VirtuaAgentResponseDto { RunId = runId, TraceUrl = traceUrl } }
                    : pipelineResponse;
                var pipelineResponseJson = JsonSerializer.Serialize(pipelineApiResponse, JsonOptions.Default);

                await traceStore.CompleteRunAsync(runId, pipelineResponseJson, cancellationToken);
                await PublishAsync(traceStore, traceHub, runId, "run_completed", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);

                if (request.Stream == true)
                {
                    await WriteFinalAnswerStreamAsync(httpContext.Response, pipelineApiResponse, cancellationToken);
                    return StartedResponseResult.Instance;
                }

                return Results.Json(pipelineApiResponse, JsonOptions.Default);
            }

            var upstreamRequest = request with { EndpointId = null };
            if (request.Stream == true)
            {
                if (endpoint is null)
                {
                    await upstreamClient.StreamChatAsync(upstreamRequest, httpContext.Response.Body, cancellationToken);
                }
                else
                {
                    await upstreamClient.StreamChatAsync(upstreamRequest, httpContext.Response.Body, endpoint, cancellationToken);
                }

                await traceStore.CompleteRunAsync(runId, """{"streamed":true}""", cancellationToken);
                await PublishAsync(traceStore, traceHub, runId, "run_completed", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);
                return StartedResponseResult.Instance;
            }

            var upstreamResponse = endpoint is null
                ? await upstreamClient.ChatAsync(upstreamRequest, cancellationToken)
                : await upstreamClient.ChatAsync(upstreamRequest, endpoint, cancellationToken);
            var response = request.Orchestration?.IncludeVirtuaAgent == true
                ? upstreamResponse with { VirtuaAgent = new VirtuaAgentResponseDto { RunId = runId, TraceUrl = traceUrl } }
                : upstreamResponse;
            var responseJson = JsonSerializer.Serialize(response, JsonOptions.Default);

            await traceStore.CompleteRunAsync(runId, responseJson, cancellationToken);
            await PublishAsync(traceStore, traceHub, runId, "run_completed", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);

            return Results.Json(response, JsonOptions.Default);
        }
        catch (PipelineValidationException ex)
        {
            if (httpContext.Response.HasStarted)
            {
                await WriteErrorStreamAsync(httpContext.Response, ex.Message, cancellationToken);
                return StartedResponseResult.Instance;
            }

            var error = new OpenAiErrorResponse(new OpenAiError
            {
                Message = ex.Message,
                Type = "invalid_request_error",
                Param = ex.Param,
                Code = ex.Code,
                VirtuaAgent = new { run_id = runId }
            });
            var errorJson = JsonSerializer.Serialize(error, JsonOptions.Default);
            await traceStore.FailRunAsync(runId, errorJson, cancellationToken);
            await PublishAsync(traceStore, traceHub, runId, "run_failed", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);

            return Results.Json(error, JsonOptions.Default, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            if (httpContext.Response.HasStarted)
            {
                await WriteErrorStreamAsync(httpContext.Response, ErrorMessageFrom(ex), cancellationToken);
                return StartedResponseResult.Instance;
            }

            var error = new OpenAiErrorResponse(new OpenAiError
            {
                Message = ErrorMessageFrom(ex),
                Type = "server_error",
                Code = "upstream_error",
                VirtuaAgent = new { run_id = runId }
            });
            var errorJson = JsonSerializer.Serialize(error, JsonOptions.Default);
            await traceStore.FailRunAsync(runId, errorJson, cancellationToken);
            await PublishAsync(traceStore, traceHub, runId, "run_failed", $$"""{"run_id":"{{runId}}"}""", store, cancellationToken);

            return Results.Json(error, JsonOptions.Default, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task WriteFinalAnswerStreamAsync(HttpResponse response, ChatCompletionResponse completion, CancellationToken cancellationToken)
    {
        var answer = completion.Choices.FirstOrDefault()?.Message.Content.AsText() ?? "";
        var chunk = new
        {
            id = completion.Id,
            @object = "chat.completion.chunk",
            created = completion.Created,
            model = completion.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content = answer },
                    finish_reason = (string?)null
                }
            }
        };
        await response.WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonOptions.Default)}\n\n", cancellationToken);
        await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }

    private static async Task WriteErrorStreamAsync(HttpResponse response, string message, CancellationToken cancellationToken)
    {
        var chunk = new
        {
            error = new
            {
                message,
                type = "server_error",
                code = "upstream_error"
            }
        };
        await response.WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonOptions.Default)}\n\n", cancellationToken);
        await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }

    private static void AddVirtuaAgentHeaders(HttpResponse response, string runId, string traceUrl)
    {
        response.Headers["Virtua-Agent-Run-Id"] = runId;
        response.Headers["Link"] = $"<{traceUrl}>; rel=\"monitor\"";
    }

    private static string PreviewFrom(ChatCompletionRequest request)
    {
        var userMessage = request.Messages.FirstOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        if (userMessage is null) return "";
        var content = userMessage.Content.AsText();
        return content.Length <= 200 ? content : content[..200];
    }

    private static ChatCompletionRequest RedactRequestForTrace(ChatCompletionRequest request) =>
        request with
        {
            Messages = request.Messages
                .Select(message => message with { Content = message.Content.RedactMedia() })
                .ToList()
        };

    private static string ErrorMessageFrom(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException)
        {
            return "Upstream request timed out or was canceled. If this is the first request after loading or switching a model, the upstream may still be warming the model.";
        }

        return ex.Message;
    }

    private static async Task<ModelEndpointDefinition?> ResolveEndpointAsync(
        string? endpointId,
        IModelEndpointStore modelEndpointStore,
        CancellationToken cancellationToken)
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
                "endpoint_id",
                "invalid_endpoint");
        }

        return endpoint;
    }

    private static async Task<ChatCompletionRequest> ApplyPresetPipelineAsync(
        ChatCompletionRequest request,
        PipelinePresetCatalog presetCatalog,
        CancellationToken cancellationToken)
    {
        if (request.Orchestration?.Pipeline is not null)
        {
            return request;
        }

        var preset = await presetCatalog.FindAsync(request.Model, cancellationToken);
        if (preset?.Pipeline is null)
        {
            return request;
        }

        return request with
        {
            Orchestration = new OrchestrationRequestDto
            {
                IncludeVirtuaAgent = request.Orchestration?.IncludeVirtuaAgent ?? false,
                Store = request.Orchestration?.Store,
                Pipeline = preset.Pipeline
            }
        };
    }

    private sealed class StartedResponseResult : IResult
    {
        public static readonly StartedResponseResult Instance = new();

        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    private static async Task PublishAsync(
        ITraceStore traceStore,
        ActiveTraceHub traceHub,
        string runId,
        string type,
        string json,
        bool store,
        CancellationToken cancellationToken)
    {
        var traceEvent = TraceEventRecord.Create(type, json);
        if (store)
        {
            await traceStore.AppendEventAsync(runId, traceEvent, cancellationToken);
        }

        await traceHub.PublishAsync(runId, traceEvent);
    }
}
