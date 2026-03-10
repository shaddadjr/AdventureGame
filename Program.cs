using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{renderPort}");
}

var sseJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<LlmGameClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<GameSessionStore>();

var app = builder.Build();

var staticRoot = FindStaticRoot(app.Environment.ContentRootPath, AppContext.BaseDirectory);
if (staticRoot is not null)
{
    var staticProvider = new PhysicalFileProvider(staticRoot);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = staticProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = staticProvider
    });
}
else
{
    app.MapGet("/", () => Results.Text("Web UI files not found. Ensure wwwroot is present next to the app binary or run from the project directory."));
}

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

app.MapPost("/api/game/start", async (StartRequest request, LlmGameClient llm, GameSessionStore store) =>
{
    var language = NormalizeLanguage(request.Language);
    var session = store.CreateSession(language);
    try
    {
        var turn = await llm.NextTurnAsync(session.Messages, session.Language);
        session.ApplyTurn(turn);
        return Results.Ok(ToTurnDto(session, turn));
    }
    catch (Exception ex)
    {
        store.RemoveSession(session.Id);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/game/start-stream", async (HttpContext context, StartRequest request, LlmGameClient llm, GameSessionStore store) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    var language = NormalizeLanguage(request.Language);
    var session = store.CreateSession(language);

    try
    {
        var turn = await llm.StreamTurnAsync(
            session.Messages,
            session.Language,
            async delta => await WriteSseAsync(context.Response, "chunk", new { text = delta }, context.RequestAborted),
            context.RequestAborted);

        session.ApplyTurn(turn);
        await WriteSseAsync(context.Response, "done", ToTurnDto(session, turn), context.RequestAborted);

        if (session.IsFinished)
        {
            store.RemoveSession(session.Id);
        }
    }
    catch (Exception ex)
    {
        store.RemoveSession(session.Id);
        await WriteSseAsync(context.Response, "error", new { error = ex.Message }, context.RequestAborted);
    }
});

app.MapPost("/api/game/turn", async (TurnRequest request, LlmGameClient llm, GameSessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId) || !store.TryGetSession(request.SessionId, out var session))
    {
        return Results.BadRequest(new { error = "Invalid or expired session. Start a new game." });
    }

    if (session.IsFinished)
    {
        store.RemoveSession(session.Id);
        return Results.BadRequest(new { error = "This game is finished. Start a new game." });
    }

    var userAction = string.IsNullOrWhiteSpace(request.Action) ? "wait and observe" : request.Action.Trim();
    session.Messages.Add(ChatMessage.Assistant(session.LastAssistantJson));
    session.Messages.Add(ChatMessage.User(userAction));

    try
    {
        var turn = await llm.NextTurnAsync(session.Messages, session.Language);
        session.ApplyTurn(turn);

        if (session.IsFinished)
        {
            store.RemoveSession(session.Id);
        }

        return Results.Ok(ToTurnDto(session, turn));
    }
    catch (Exception ex)
    {
        store.RemoveSession(session.Id);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/game/turn-stream", async (HttpContext context, TurnRequest request, LlmGameClient llm, GameSessionStore store) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    if (string.IsNullOrWhiteSpace(request.SessionId) || !store.TryGetSession(request.SessionId, out var session))
    {
        await WriteSseAsync(context.Response, "error", new { error = "Invalid or expired session. Start a new game." }, context.RequestAborted);
        return;
    }

    if (session.IsFinished)
    {
        store.RemoveSession(session.Id);
        await WriteSseAsync(context.Response, "error", new { error = "This game is finished. Start a new game." }, context.RequestAborted);
        return;
    }

    var userAction = string.IsNullOrWhiteSpace(request.Action) ? "wait and observe" : request.Action.Trim();
    session.Messages.Add(ChatMessage.Assistant(session.LastAssistantJson));
    session.Messages.Add(ChatMessage.User(userAction));

    try
    {
        var turn = await llm.StreamTurnAsync(
            session.Messages,
            session.Language,
            async delta => await WriteSseAsync(context.Response, "chunk", new { text = delta }, context.RequestAborted),
            context.RequestAborted);

        session.ApplyTurn(turn);
        await WriteSseAsync(context.Response, "done", ToTurnDto(session, turn), context.RequestAborted);

        if (session.IsFinished)
        {
            store.RemoveSession(session.Id);
        }
    }
    catch (Exception ex)
    {
        store.RemoveSession(session.Id);
        await WriteSseAsync(context.Response, "error", new { error = ex.Message }, context.RequestAborted);
    }
});

app.Run();

static string NormalizeLanguage(string? language)
{
    var value = language?.Trim().ToLowerInvariant();
    return value switch
    {
        "spanish" or "es" or "espanol" or "español" => "spanish",
        _ => "english"
    };
}

async Task WriteSseAsync<T>(HttpResponse response, string eventName, T payload, CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(payload, sseJsonOptions);
    await response.WriteAsync($"event: {eventName}\n", cancellationToken);
    await response.WriteAsync($"data: {json}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static string? FindStaticRoot(string contentRoot, string baseDir)
{
    var directCandidates = new[]
    {
        Path.Combine(contentRoot, "wwwroot"),
        Path.Combine(baseDir, "wwwroot")
    };

    foreach (var candidate in directCandidates)
    {
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    var probe = new DirectoryInfo(baseDir);
    for (var depth = 0; depth < 8 && probe is not null; depth++)
    {
        var candidate = Path.Combine(probe.FullName, "wwwroot");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        probe = probe.Parent;
    }

    return null;
}

static TurnResponse ToTurnDto(GameSession session, GameTurnResponse turn)
{
    var scene = FormatScene(turn.Scene);
    var status = session.IsTimeOut ? "lose" : turn.Status ?? "ongoing";
    var endingLabel = status switch
    {
        "win" when session.Language == "spanish" => "FINAL DE VICTORIA",
        "lose" when session.Language == "spanish" => "FINAL DE DERROTA",
        "win" => "SUCCESS ENDING",
        "lose" => "FAILURE ENDING",
        _ => null
    };

    return new TurnResponse(
        SessionId: session.Id,
        Language: session.Language,
        Title: turn.Title,
        EpisodeTitle: turn.EpisodeTitle,
        EpisodeNumber: turn.EpisodeNumber,
        Scene: scene,
        ChoicesHint: turn.ChoicesHint ?? new List<string>(),
        Status: status,
        EndingLabel: endingLabel,
        TurnsRemaining: Math.Max(0, session.MaxTurns - session.TurnCount));
}

static string FormatScene(string scene)
{
    if (string.IsNullOrWhiteSpace(scene))
    {
        return string.Empty;
    }

    var words = scene.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (words.Length <= 70)
    {
        return scene.Trim();
    }

    return string.Join(' ', words.Take(70)).Trim() + "...";
}

sealed class GameSessionStore
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();

    public GameSession CreateSession(string language)
    {
        var session = GameSession.Create(language);
        _sessions[session.Id] = session;
        return session;
    }

    public bool TryGetSession(string id, out GameSession session) =>
        _sessions.TryGetValue(id, out session!);

    public void RemoveSession(string id) =>
        _sessions.TryRemove(id, out _);
}

sealed class GameSession
{
    private GameSession(string id, List<ChatMessage> messages, string language)
    {
        Id = id;
        Messages = messages;
        Language = language;
    }

    public string Id { get; }
    public string Language { get; }
    public List<ChatMessage> Messages { get; }
    public string LastAssistantJson { get; private set; } = "{}";
    public int TurnCount { get; private set; }
    public int MaxTurns { get; private set; } = 7;
    public bool IsTimeOut { get; private set; }
    public bool IsFinished { get; private set; }

    public static GameSession Create(string language)
    {
        var seed = Guid.NewGuid().ToString("N");
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Prompts.SystemPrompt(language)),
            ChatMessage.User(Prompts.StartPrompt(seed, language))
        };
        return new GameSession(Guid.NewGuid().ToString("N"), messages, language);
    }

    public void ApplyTurn(GameTurnResponse response)
    {
        TurnCount++;
        LastAssistantJson = response.RawAssistantText;

        if (response.TurnsRemaining is > 0 and < 99)
        {
            MaxTurns = Math.Min(MaxTurns, response.TurnsRemaining.Value + TurnCount);
        }

        var normalizedStatus = response.Status ?? "ongoing";
        if (normalizedStatus is "win" or "lose")
        {
            IsFinished = true;
            return;
        }

        if (TurnCount >= MaxTurns)
        {
            IsTimeOut = true;
            IsFinished = true;
        }
    }
}

sealed class LlmGameClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    public LlmGameClient(HttpClient http)
    {
        _http = http;
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5-mini";
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
        _endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
    }

    public async Task<GameTurnResponse> NextTurnAsync(List<ChatMessage> messages, string language)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Missing OPENAI_API_KEY on the server.");
        }

        var payload = new
        {
            model = _model,
            response_format = new { type = "json_object" },
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"LLM request failed ({(int)response.StatusCode}): {responseBody}");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Empty LLM completion response.");

        var content = completion.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned no content.");
        }

        return ParseGameResponse(content, language);
    }

    public async Task<GameTurnResponse> StreamTurnAsync(
        List<ChatMessage> messages,
        string language,
        Func<string, Task> onSceneDelta,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Missing OPENAI_API_KEY on the server.");
        }

        var payload = new
        {
            model = _model,
            stream = true,
            response_format = new { type = "json_object" },
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"LLM request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var raw = new StringBuilder();
        var lastScene = string.Empty;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            ChatCompletionStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionStreamChunk>(data, JsonOptions);
            }
            catch
            {
                continue;
            }

            var piece = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            raw.Append(piece);
            var sceneSoFar = TryExtractSceneFromJson(raw.ToString());
            if (sceneSoFar.Length <= lastScene.Length || !sceneSoFar.StartsWith(lastScene, StringComparison.Ordinal))
            {
                continue;
            }

            var delta = sceneSoFar[lastScene.Length..];
            lastScene = sceneSoFar;
            if (!string.IsNullOrEmpty(delta))
            {
                await onSceneDelta(delta);
            }
        }

        return ParseGameResponse(raw.ToString(), language);
    }

    private static GameTurnResponse ParseGameResponse(string content, string language)
    {
        var jsonText = ExtractJsonObject(content);
        try
        {
            var parsed = JsonSerializer.Deserialize<GameTurnResponse>(jsonText, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Scene))
            {
                throw new JsonException("Invalid game response payload.");
            }

            parsed.RawAssistantText = jsonText;
            parsed.Status = NormalizeStatus(parsed.Status);
            return parsed;
        }
        catch
        {
            var isSpanish = language == "spanish";
            return new GameTurnResponse
            {
                Title = null,
                EpisodeTitle = isSpanish ? "Caminos Inciertos" : "Unwritten Paths",
                EpisodeNumber = 1,
                Scene = content.Trim(),
                Status = "ongoing",
                ChoicesHint = isSpanish
                    ? new List<string> { "atacar", "negociar", "explorar las ruinas" }
                    : new List<string> { "strike", "parley", "explore the ruins" },
                TurnsRemaining = 4,
                RawAssistantText = content
            };
        }
    }

    private static string NormalizeStatus(string? status)
    {
        var value = status?.Trim().ToLowerInvariant();
        return value switch
        {
            "win" => "win",
            "lose" => "lose",
            _ => "ongoing"
        };
    }

    private static string ExtractJsonObject(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return value[start..(end + 1)];
        }

        return value;
    }

    private static string TryExtractSceneFromJson(string jsonText)
    {
        var keyIndex = jsonText.IndexOf("\"scene\"", StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return string.Empty;
        }

        var colonIndex = jsonText.IndexOf(':', keyIndex);
        if (colonIndex < 0)
        {
            return string.Empty;
        }

        var i = colonIndex + 1;
        while (i < jsonText.Length && char.IsWhiteSpace(jsonText[i]))
        {
            i++;
        }

        if (i >= jsonText.Length || jsonText[i] != '"')
        {
            return string.Empty;
        }

        i++;
        var result = new StringBuilder();
        var escaped = false;
        while (i < jsonText.Length)
        {
            var c = jsonText[i++];
            if (escaped)
            {
                switch (c)
                {
                    case '"':
                        result.Append('"');
                        break;
                    case '\\':
                        result.Append('\\');
                        break;
                    case '/':
                        result.Append('/');
                        break;
                    case 'b':
                        result.Append('\b');
                        break;
                    case 'f':
                        result.Append('\f');
                        break;
                    case 'n':
                        result.Append('\n');
                        break;
                    case 'r':
                        result.Append('\r');
                        break;
                    case 't':
                        result.Append('\t');
                        break;
                    case 'u':
                        if (i + 3 < jsonText.Length)
                        {
                            var hex = jsonText.Substring(i, 4);
                            if (Regex.IsMatch(hex, "^[0-9a-fA-F]{4}$"))
                            {
                                result.Append((char)Convert.ToInt32(hex, 16));
                                i += 4;
                            }
                        }
                        break;
                    default:
                        result.Append(c);
                        break;
                }

                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                break;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}

static class Prompts
{
    public static string SystemPrompt(string language) => $$"""
You are the game engine for a short episodic fantasy RPG.
Generate a fresh, random story each playthrough. Use the player's free-text actions to branch the plot.

Rules:
- The whole game should finish in 4 to 7 turns.
- Genre: heroic fantasy adventure.
- The protagonist is always Arthus, a newly trained warrior leaving formal training to face the real world.
- Keep the narrative hero-centered: Arthus drives every major decision.
- Structure each turn as an episode beat in a mini-campaign.
- Accept creative actions and adapt rather than rejecting.
- Write for middle to high school readers.
- Keep each scene concise (1 to 3 sentences, around 35 to 70 words).
- Use clear language with a few vivid details, not long descriptions.
- Every response MUST be valid JSON object only.
- Include meaningful win or lose outcome based on player choices.
- Do not reveal hidden future events unless discovered.
- Output language: {{(language == "spanish" ? "Spanish" : "English")}}.
- Keep JSON keys in English exactly as specified.

Output JSON schema:
{
  "title": "string or null",
  "episodeTitle": "string",
  "episodeNumber": number,
  "scene": "string",
  "choicesHint": ["string", "string", "string"],
  "status": "ongoing|win|lose",
  "turnsRemaining": number
}

Set "title" only on first turn, otherwise null.
When status is win or lose, provide a clear ending in "scene".
""";

    public static string StartPrompt(string seed, string language) =>
        language == "spanish"
            ? $"Comienza una nueva aventura episódica de Arthus con semilla aleatoria {seed}. Presenta a Arthus, su primera misión en el mundo real y un desafío inmediato."
            : $"Start a new Arthus episode run with random seed {seed}. Introduce Arthus, his first quest in the wider world, and an immediate opening challenge.";
}

sealed class ChatMessage
{
    private ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    public string Role { get; }
    public string Content { get; }

    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

sealed class GameTurnResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("episodeTitle")]
    public string EpisodeTitle { get; set; } = "The First Steps";

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; } = 1;

    [JsonPropertyName("scene")]
    public string Scene { get; set; } = string.Empty;

    [JsonPropertyName("choicesHint")]
    public List<string>? ChoicesHint { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("turnsRemaining")]
    public int? TurnsRemaining { get; set; }

    [JsonIgnore]
    public string RawAssistantText { get; set; } = string.Empty;
}

sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice>? Choices { get; set; }
}

sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatChoiceMessage? Message { get; set; }
}

sealed class ChatChoiceMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

sealed class ChatCompletionStreamChunk
{
    [JsonPropertyName("choices")]
    public List<ChatStreamChoice>? Choices { get; set; }
}

sealed class ChatStreamChoice
{
    [JsonPropertyName("delta")]
    public ChatStreamDelta? Delta { get; set; }
}

sealed class ChatStreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

sealed record StartRequest(string? Language);

sealed record TurnRequest(string SessionId, string Action);

sealed record TurnResponse(
    string SessionId,
    string Language,
    string? Title,
    string EpisodeTitle,
    int EpisodeNumber,
    string Scene,
    List<string> ChoicesHint,
    string Status,
    string? EndingLabel,
    int TurnsRemaining);
