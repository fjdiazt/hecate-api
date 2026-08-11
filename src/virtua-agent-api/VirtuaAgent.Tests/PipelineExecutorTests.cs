using System.Text.Json;
using VirtuaAgent.Codex;
using VirtuaAgent.OpenAi;
using VirtuaAgent.ModelEndpoints;
using VirtuaAgent.Orchestration;
using VirtuaAgent.PipelineModels;
using VirtuaAgent.Settings;
using VirtuaAgent.Tracing;
using VirtuaAgent.Upstream;

namespace VirtuaAgent.Tests;

public sealed class PipelineExecutorTests
{
    [Fact]
    public void ChatRequestDeserializesOpenAiMultimodalContent()
    {
        const string json = """
            {
              "model": "vision-model",
              "messages": [
                {
                  "role": "user",
                  "content": [
                    { "type": "text", "text": "Describe this image." },
                    { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAABASE64" } }
                  ]
                }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonOptions.Default)!;

        var content = request.Messages[0].Content;
        Assert.True(content.IsParts);
        Assert.Contains(content.Parts, part => part.Type == "text" && part.Text == "Describe this image.");
        Assert.Contains(content.Parts, part => part.Type == "image_url" && part.ImageUrl?.Url == "data:image/png;base64,AAAABASE64");

        var roundTrip = JsonSerializer.Serialize(request, JsonOptions.Default);
        Assert.Contains("\"content\":[{\"type\":\"text\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"image_url\":{\"url\":\"data:image/png;base64,AAAABASE64\"}", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstStageWithoutInstructionsSendsOriginalConversationUnchanged()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages =
            [
                new ChatMessageDto { Role = "system", Content = "Be direct." },
                new ChatMessageDto { Role = "user", Content = "write answer" }
            ],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages = [new PipelineStageRequestDto { Type = "single_agent" }]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Single(upstream.Requests);
        Assert.Equal(request.Messages, upstream.Requests[0].Messages);
    }

    [Fact]
    public async Task FirstStageWithInstructionsReceivesOriginalConversationAndStageInstruction()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Use bullet points."
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(2, upstream.Requests[0].Messages.Count);
        Assert.Equal(request.Messages[0], upstream.Requests[0].Messages[0]);
        var packaged = upstream.Requests[0].Messages[1];
        Assert.Equal("user", packaged.Role);
        Assert.Contains("Pipeline protocol:", packaged.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Original conversation:", packaged.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Prior stage output:", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Stage instruction:", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Use bullet points.", packaged.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstStageWithInstructionsPreservesMultimodalOriginalMessage()
    {
        var upstream = new RecordingUpstreamClient("image observations");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "vision-model",
            Messages =
            [
                new ChatMessageDto
                {
                    Role = "user",
                    Content = ChatMessageContent.FromParts(
                    [
                        ChatMessageContentPart.FromText("Describe this image."),
                        ChatMessageContentPart.FromImageUrl("data:image/png;base64,AAAABASE64")
                    ])
                }
            ],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Analyze only visible image details."
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(2, upstream.Requests[0].Messages.Count);
        Assert.True(upstream.Requests[0].Messages[0].Content.IsParts);
        Assert.Contains(upstream.Requests[0].Messages[0].Content.Parts, part => part.Type == "image_url" && part.ImageUrl?.Url == "data:image/png;base64,AAAABASE64");
        Assert.False(upstream.Requests[0].Messages[1].Content.IsParts);
        Assert.Contains("Analyze only visible image details.", upstream.Requests[0].Messages[1].Content.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstStageDefaultInputPreservesOriginalMultimodalRequest()
    {
        var upstream = new RecordingUpstreamClient("observations");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "vision-model",
            Messages =
            [
                new ChatMessageDto
                {
                    Role = "user",
                    Content = ChatMessageContent.FromParts(
                    [
                        ChatMessageContentPart.FromText("Describe this image."),
                        ChatMessageContentPart.FromImageUrl("data:image/png;base64,AAAABASE64")
                    ])
                }
            ],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Name = "Analyze image",
                            Instructions = "Analyze the attached image."
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(2, upstream.Requests[0].Messages.Count);
        Assert.True(upstream.Requests[0].Messages[0].Content.IsParts);
        Assert.Contains(upstream.Requests[0].Messages[0].Content.Parts, part => part.Type == "image_url");
        Assert.Contains("Analyze the attached image.", upstream.Requests[0].Messages[1].Content.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterStageWithInstructionsReceivesPipelineProtocolAndLabeledPromptSections()
    {
        var upstream = new RecordingUpstreamClient("draft answer", "corrected answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Correct spelling only."
                        }
                    ]
                }
            }
        };

        var response = await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal("corrected answer", response.Choices[0].Message.Content);
        Assert.Equal(2, upstream.Requests.Count);
        var packaged = Assert.Single(upstream.Requests[1].Messages);
        Assert.Equal("user", packaged.Role);
        Assert.Contains("Pipeline protocol:", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Treat prior stage output as input data, not as your own prior message.", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Use the stage instruction as your task.", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Use the original conversation only as context.", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Return only this stage's output.", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Original conversation:", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("user: write answer", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Prior stage output from \"Stage 1\":", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("draft answer", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Stage instruction:", packaged.Content, StringComparison.Ordinal);
        Assert.Contains("Correct spelling only.", packaged.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Revise and improve", packaged.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterStageReceivesTextOnlyPriorOutputForMultimodalRequest()
    {
        var upstream = new RecordingUpstreamClient("image observations", "final description");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "vision-model",
            Messages =
            [
                new ChatMessageDto
                {
                    Role = "user",
                    Content = ChatMessageContent.FromParts(
                    [
                        ChatMessageContentPart.FromText("Describe this image."),
                        ChatMessageContentPart.FromImageUrl("data:image/png;base64,AAAABASE64")
                    ])
                }
            ],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Write the final description."
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.True(upstream.Requests[0].Messages[0].Content.IsParts);
        var packaged = Assert.Single(upstream.Requests[1].Messages);
        Assert.False(packaged.Content.IsParts);
        Assert.Contains("Prior stage output from \"Stage 1\":", packaged.Content.AsText(), StringComparison.Ordinal);
        Assert.Contains("image observations", packaged.Content.AsText(), StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/png;base64,AAAABASE64", packaged.Content.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterStageCanReceiveOnlyPriorStageOutput()
    {
        var upstream = new RecordingUpstreamClient("visual observations", "draft description");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "vision-model",
            Messages =
            [
                new ChatMessageDto
                {
                    Role = "user",
                    Content = ChatMessageContent.FromParts(
                    [
                        ChatMessageContentPart.FromText("Describe this image."),
                        ChatMessageContentPart.FromImageUrl("data:image/png;base64,AAAABASE64")
                    ])
                }
            ],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent", Name = "Analyze image" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Name = "Draft",
                            Instructions = "Use the prior observations to draft a description.",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "none",
                                PriorStageOutput = "last"
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        var packaged = Assert.Single(upstream.Requests[1].Messages);
        var text = packaged.Content.AsText();
        Assert.Contains("Prior stage output from \"Analyze image\":", text, StringComparison.Ordinal);
        Assert.Contains("visual observations", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Describe this image.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAABASE64", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad", "none", "orchestration.pipeline.stages[0].input.original_messages")]
    [InlineData("full", "bad", "orchestration.pipeline.stages[0].input.prior_stage_output")]
    public async Task InvalidStageInputSelectorIsRejected(string originalMessages, string priorStageOutput, string param)
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = originalMessages,
                                PriorStageOutput = priorStageOutput
                            }
                        }
                    ]
                }
            }
        };

        var ex = await Assert.ThrowsAsync<PipelineValidationException>(
            () => executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("invalid_stage_input", ex.Code);
        Assert.Equal(param, ex.Param);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task FirstExecutionCannotRequestPriorStageOutput()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "none",
                                PriorStageOutput = "last"
                            }
                        }
                    ]
                }
            }
        };

        var ex = await Assert.ThrowsAsync<PipelineValidationException>(
            () => executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("invalid_stage_input", ex.Code);
        Assert.Equal("orchestration.pipeline.stages[0].input.prior_stage_output", ex.Param);
    }

    [Fact]
    public async Task PartialStageInputMergesWithExecutionDefault()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Answer directly.",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "text"
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        var text = Assert.Single(upstream.Requests[0].Messages).Content.AsText();
        Assert.Contains("Original conversation:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Prior stage output", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineProtocolOverridesBuiltInProtocol()
    {
        var upstream = new RecordingUpstreamClient("draft", "final");
        var executor = CreateExecutor(upstream, "Settings-level protocol.");
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Protocol = "Pipeline-wide protocol.",
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Finalize.",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "none",
                                PriorStageOutput = "last"
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        var text = Assert.Single(upstream.Requests[1].Messages).Content.AsText();
        Assert.Contains("Pipeline-wide protocol.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings-level protocol.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("You are executing one stage in a pipeline.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsPipelineProtocolOverridesBuiltInProtocol()
    {
        var upstream = new RecordingUpstreamClient("draft", "final");
        var executor = CreateExecutor(upstream, "Settings-level protocol.");
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Instructions = "Finalize.",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "none",
                                PriorStageOutput = "last"
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        var text = Assert.Single(upstream.Requests[1].Messages).Content.AsText();
        Assert.Contains("Settings-level protocol.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("You are executing one stage in a pipeline.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageProtocolOverridesPipelineProtocol()
    {
        var upstream = new RecordingUpstreamClient("draft", "final");
        var executor = CreateExecutor(upstream, "Settings-level protocol.");
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Protocol = "Pipeline-wide protocol.",
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Protocol = "Stage-specific protocol.",
                            Instructions = "Finalize.",
                            Input = new PipelineStageInputRequestDto
                            {
                                OriginalMessages = "none",
                                PriorStageOutput = "last"
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        var text = Assert.Single(upstream.Requests[1].Messages).Content.AsText();
        Assert.Contains("Stage-specific protocol.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pipeline-wide protocol.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings-level protocol.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterStageWithoutInstructionsIsRejected()
    {
        var upstream = new RecordingUpstreamClient("first answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages =
                    [
                        new PipelineStageRequestDto { Type = "single_agent" },
                        new PipelineStageRequestDto { Type = "single_agent" }
                    ]
                }
            }
        };

        var ex = await Assert.ThrowsAsync<PipelineValidationException>(
            () => executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("orchestration.pipeline.stages[1].instructions", ex.Param);
        Assert.Equal("instructions_required", ex.Code);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task StageWithoutModelUsesPipelineDefaultModel()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultModel = "local-model",
                    Stages = [new PipelineStageRequestDto { Type = "single_agent" }]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal("local-model", upstream.Requests[0].Model);
    }

    [Fact]
    public async Task StageWithEndpointIdUsesSelectedEndpoint()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var endpointStore = new FakeModelEndpointStore(new ModelEndpointDefinition
        {
            Id = "llamacpp",
            Name = "llama.cpp",
            BaseUrl = "http://llama.test"
        });
        var executor = CreateExecutor(upstream, endpointStore: endpointStore);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultModel = "local-model",
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Agent = new AgentRequestDto { EndpointId = "llamacpp" }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal("llamacpp", upstream.EndpointIds.Single());
        Assert.Null(upstream.Requests[0].EndpointId);
    }

    [Fact]
    public async Task StageWithCodexEndpointUsesCodexClient()
    {
        var upstream = new RecordingUpstreamClient("unused");
        var codex = new FakeCodexAppServerClient();
        var endpointStore = new FakeModelEndpointStore(new ModelEndpointDefinition
        {
            Id = "codex",
            Name = "Codex",
            Type = ModelEndpointTypes.CodexSubscription
        });
        var executor = CreateExecutor(upstream, endpointStore: endpointStore, codex: codex);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultModel = "gpt-5.3-codex",
                    Stages = [new PipelineStageRequestDto { Type = "single_agent", Agent = new AgentRequestDto { EndpointId = "codex" } }]
                }
            }
        };

        var response = await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal("codex answer", response.Choices[0].Message.Content.AsText());
        Assert.Equal(1, codex.Turns);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("top_p")]
    [InlineData("top_k")]
    [InlineData("min_p")]
    [InlineData("repeat_penalty")]
    [InlineData("max_tokens")]
    public async Task CodexStageIgnoresUnsupportedOptions(string parameter)
    {
        var upstream = new RecordingUpstreamClient("unused");
        var codex = new FakeCodexAppServerClient();
        var endpointStore = new FakeModelEndpointStore(new ModelEndpointDefinition
        {
            Id = "codex",
            Type = ModelEndpointTypes.CodexSubscription
        });
        var executor = CreateExecutor(upstream, endpointStore: endpointStore, codex: codex);
        var request = new ChatCompletionRequest
        {
            Model = "gpt-5.3-codex",
            Temperature = parameter == "temperature" ? 0.2 : null,
            TopP = parameter == "top_p" ? 0.9 : null,
            TopK = parameter == "top_k" ? 40 : null,
            MinP = parameter == "min_p" ? 0.05 : null,
            RepeatPenalty = parameter == "repeat_penalty" ? 1.1 : null,
            MaxTokens = parameter == "max_tokens" ? 100 : null,
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages = [new PipelineStageRequestDto { Type = "single_agent", Agent = new AgentRequestDto { EndpointId = "codex" } }]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(1, codex.Turns);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task CodexStageRejectsUnknownRequestFields()
    {
        var codex = new FakeCodexAppServerClient();
        var endpointStore = new FakeModelEndpointStore(new ModelEndpointDefinition
        {
            Id = "codex",
            Type = ModelEndpointTypes.CodexSubscription
        });
        var executor = CreateExecutor(new RecordingUpstreamClient("unused"), endpointStore: endpointStore, codex: codex);
        var request = new ChatCompletionRequest
        {
            Model = "gpt-5.3-codex",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            ExtraFields = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["unsupported"] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone()
            },
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages = [new PipelineStageRequestDto { Type = "single_agent", Agent = new AgentRequestDto { EndpointId = "codex" } }]
                }
            }
        };

        var error = await Assert.ThrowsAsync<PipelineValidationException>(() =>
            executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("request", error.Param);
        Assert.Equal("codex_parameter_unsupported", error.Code);
        Assert.Equal(0, codex.Turns);
    }

    [Fact]
    public async Task StageWithMissingEndpointIdIsRejected()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultModel = "local-model",
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Agent = new AgentRequestDto { EndpointId = "missing" }
                        }
                    ]
                }
            }
        };

        var ex = await Assert.ThrowsAsync<PipelineValidationException>(
            () => executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("invalid_endpoint", ex.Code);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task StageDefaultModelSentinelUsesPipelineDefaultModel(string stageModel)
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultModel = "local-model",
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Agent = new AgentRequestDto { Model = stageModel }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal("local-model", upstream.Requests[0].Model);
    }

    [Fact]
    public async Task StageWithoutOverridesUsesPipelineDefaultOptions()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Temperature = 0.9,
            TopP = 0.95,
            TopK = 80,
            MinP = 0.1,
            RepeatPenalty = 1.3,
            MaxTokens = 4096,
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultTemperature = 0.2,
                    DefaultTopP = 0.8,
                    DefaultTopK = 40,
                    DefaultMinP = 0.05,
                    DefaultRepeatPenalty = 1.1,
                    DefaultMaxTokens = 128,
                    Stages = [new PipelineStageRequestDto { Type = "single_agent" }]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(0.2, upstream.Requests[0].Temperature);
        Assert.Equal(0.8, upstream.Requests[0].TopP);
        Assert.Equal(40, upstream.Requests[0].TopK);
        Assert.Equal(0.05, upstream.Requests[0].MinP);
        Assert.Equal(1.1, upstream.Requests[0].RepeatPenalty);
        Assert.Equal(128, upstream.Requests[0].MaxTokens);
    }

    [Fact]
    public async Task StageAgentSamplingOptionsOverridePipelineDefaults()
    {
        var upstream = new RecordingUpstreamClient("answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "virtua-agent/editor",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    DefaultTemperature = 0.2,
                    DefaultTopP = 0.8,
                    DefaultTopK = 40,
                    DefaultMinP = 0.05,
                    DefaultRepeatPenalty = 1.1,
                    DefaultMaxTokens = 128,
                    Stages =
                    [
                        new PipelineStageRequestDto
                        {
                            Type = "single_agent",
                            Agent = new AgentRequestDto
                            {
                                Temperature = 0.4,
                                TopP = 0.7,
                                TopK = 20,
                                MinP = 0.02,
                                RepeatPenalty = 1.2,
                                MaxTokens = 256
                            }
                        }
                    ]
                }
            }
        };

        await executor.ExecuteAsync("run_test", request, store: true);

        Assert.Equal(0.4, upstream.Requests[0].Temperature);
        Assert.Equal(0.7, upstream.Requests[0].TopP);
        Assert.Equal(20, upstream.Requests[0].TopK);
        Assert.Equal(0.02, upstream.Requests[0].MinP);
        Assert.Equal(1.2, upstream.Requests[0].RepeatPenalty);
        Assert.Equal(256, upstream.Requests[0].MaxTokens);
    }

    [Fact]
    public async Task RepeatedStageWithoutSecondExecutionInstructionsIsRejected()
    {
        var upstream = new RecordingUpstreamClient("first answer");
        var executor = CreateExecutor(upstream);
        var request = new ChatCompletionRequest
        {
            Model = "local-model",
            Messages = [new ChatMessageDto { Role = "user", Content = "write answer" }],
            Orchestration = new OrchestrationRequestDto
            {
                Pipeline = new PipelineRequestDto
                {
                    Stages = [new PipelineStageRequestDto { Type = "single_agent", Repeat = 2 }]
                }
            }
        };

        var ex = await Assert.ThrowsAsync<PipelineValidationException>(
            () => executor.ExecuteAsync("run_test", request, store: true));

        Assert.Equal("orchestration.pipeline.stages[0].instructions", ex.Param);
        Assert.Equal("instructions_required", ex.Code);
        Assert.Empty(upstream.Requests);
    }

    private sealed class RecordingUpstreamClient(params string[] answers) : IOpenAiCompatibleUpstreamClient
    {
        private int _index;

        public List<ChatCompletionRequest> Requests { get; } = [];
        public List<string> EndpointIds { get; } = [];

        public Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelListResponse());

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = answers[_index++];
            return Task.FromResult(new ChatCompletionResponse
            {
                Id = "chatcmpl_test",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = request.Model ?? "",
                Choices =
                [
                    new ChatCompletionChoiceDto
                    {
                        Index = 0,
                        Message = new ChatMessageDto { Role = "assistant", Content = answer },
                        FinishReason = "stop"
                    }
                ]
            });
        }

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, ModelEndpointDefinition endpoint, CancellationToken cancellationToken = default)
        {
            EndpointIds.Add(endpoint.Id);
            return ChatAsync(request, cancellationToken);
        }

        public Task StreamChatAsync(ChatCompletionRequest request, Stream output, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static PipelineExecutor CreateExecutor(
        IOpenAiCompatibleUpstreamClient upstream,
        string? pipelineProtocol = null,
        IModelEndpointStore? endpointStore = null,
        ICodexAppServerClient? codex = null) =>
        new(
            upstream,
            new ModelEndpointDispatcher(upstream, new CodexSubscriptionClient(codex ?? new FakeCodexAppServerClient())),
            endpointStore ?? new FakeModelEndpointStore(),
            new FakePipelineSettingsStore(pipelineProtocol),
            new NoopTraceStore(),
            new ActiveTraceHub());

    private sealed class FakeCodexAppServerClient : ICodexAppServerClient
    {
        public int Turns { get; private set; }

        public Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexModel>>([new CodexModel("gpt-5.3-codex")]);

        public Task<CodexTurnResult> RunTurnAsync(string? model, IReadOnlyList<CodexInputItem> input, Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null, CancellationToken cancellationToken = default)
        {
            Turns++;
            return Task.FromResult(new CodexTurnResult
            {
                Id = "turn_test",
                Model = model ?? "codex",
                Content = "codex answer"
            });
        }
    }

    private sealed class FakeModelEndpointStore(params ModelEndpointDefinition[] endpoints) : IModelEndpointStore
    {
        private readonly Dictionary<string, ModelEndpointDefinition> _endpoints = endpoints.ToDictionary(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ModelEndpointDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelEndpointDefinition>>(_endpoints.Values.ToList());

        public Task<ModelEndpointDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_endpoints.GetValueOrDefault(id));

        public Task<ModelEndpointDefinition> SaveAsync(SaveModelEndpointRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePipelineSettingsStore(string? pipelineProtocol = null) : IPipelineSettingsStore
    {
        public Task<PipelineSettingsDefinition> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PipelineSettingsDefinition { PipelineProtocol = pipelineProtocol });

        public Task<PipelineSettingsDefinition> SaveAsync(
            SavePipelineSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopTraceStore : ITraceStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateRunAsync(RunRecord run, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendEventAsync(string runId, TraceEventRecord traceEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendReasoningAsync(string runId, ReasoningRecord reasoning, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, string responseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailRunAsync(string runId, string errorJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<RunRecord?>(null);
        public Task<IReadOnlyList<RunRecord>> ListRunsAsync(string? status, string? clientId, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunRecord>>([]);
        public Task<int> ClearRunsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
