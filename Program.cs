using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;

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

app.MapPost("/api/game/start", async (LlmGameClient llm, GameSessionStore store) =>
{
    var session = store.CreateSession();
    try
    {
        var turn = await llm.NextTurnAsync(session.Messages);
        session.ApplyTurn(turn);
        return Results.Ok(ToTurnDto(session, turn));
    }
    catch (Exception ex)
    {
        store.RemoveSession(session.Id);
        return Results.BadRequest(new { error = ex.Message });
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
        var turn = await llm.NextTurnAsync(session.Messages);
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

app.Run();

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
        "win" => "SUCCESS ENDING",
        "lose" => "FAILURE ENDING",
        _ => null
    };

    return new TurnResponse(
        SessionId: session.Id,
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

    public GameSession CreateSession()
    {
        var session = GameSession.Create();
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
    private GameSession(string id, List<ChatMessage> messages)
    {
        Id = id;
        Messages = messages;
    }

    public string Id { get; }
    public List<ChatMessage> Messages { get; }
    public string LastAssistantJson { get; private set; } = "{}";
    public int TurnCount { get; private set; }
    public int MaxTurns { get; private set; } = 7;
    public bool IsTimeOut { get; private set; }
    public bool IsFinished { get; private set; }

    public static GameSession Create()
    {
        var seed = Guid.NewGuid().ToString("N");
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Prompts.SystemPrompt),
            ChatMessage.User(Prompts.StartPrompt(seed))
        };
        return new GameSession(Guid.NewGuid().ToString("N"), messages);
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

    public async Task<GameTurnResponse> NextTurnAsync(List<ChatMessage> messages)
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

        return ParseGameResponse(content);
    }

    private static GameTurnResponse ParseGameResponse(string content)
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
            return new GameTurnResponse
            {
                Title = null,
                EpisodeTitle = "Unwritten Paths",
                EpisodeNumber = 1,
                Scene = content.Trim(),
                Status = "ongoing",
                ChoicesHint = new List<string> { "strike", "parley", "explore the ruins" },
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
}

static class Prompts
{
    public const string SystemPrompt = """
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

    public static string StartPrompt(string seed) =>
        $"Start a new Arthus episode run with random seed {seed}. Introduce Arthus, his first quest in the wider world, and an immediate opening challenge.";
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

sealed record TurnRequest(string SessionId, string Action);

sealed record TurnResponse(
    string SessionId,
    string? Title,
    string EpisodeTitle,
    int EpisodeNumber,
    string Scene,
    List<string> ChoicesHint,
    string Status,
    string? EndingLabel,
    int TurnsRemaining);
