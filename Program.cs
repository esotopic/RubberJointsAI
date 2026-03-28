using Microsoft.AspNetCore.Authentication.Cookies;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using RubberJointsAI.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute());
});

// Register RubberJointsAI Repository with connection string (120s timeout for Azure SQL Serverless cold start)
var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "SET_IN_AZURE_APP_SETTINGS";
var connBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(rawConnectionString)
{
    ConnectTimeout = 120
};
builder.Services.AddSingleton(new RubberJointsAI.Data.RubberJointsAIRepository(connBuilder.ConnectionString));

// Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

// HttpClient for Anthropic API
builder.Services.AddHttpClient("Anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// Auth middleware - order matters!
app.UseAuthentication();
app.UseAuthorization();

// DB initialization with retry (Azure SQL Serverless may be paused)
{
    var repository = app.Services.GetRequiredService<RubberJointsAI.Data.RubberJointsAIRepository>();
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        try
        {
            repository.InitializeAsync().GetAwaiter().GetResult();
            app.Logger.LogInformation("Database initialized successfully on attempt {Attempt}.", attempt);
            break;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "DB init attempt {Attempt} failed.", attempt);
            if (attempt < 3) Thread.Sleep(5000);
        }
    }
}

app.MapRazorPages();

// ── Minimal API endpoints (bypass Razor Pages routing for reliable JSON responses) ──

app.MapPost("/api/check", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string itemType = root.GetProperty("itemType").GetString() ?? "";
        string itemId = root.GetProperty("itemId").GetString() ?? "";
        int stepIndex = root.TryGetProperty("stepIndex", out var si) ? si.GetInt32() : 0;
        bool checkedState = root.TryGetProperty("checked", out var cp) && cp.GetBoolean();
        // Allow specifying a date for checking past days; default to today
        string date = root.TryGetProperty("date", out var dp) ? dp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(date)) date = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await repository.SetCheckAsync(userId, date, itemType, itemId, stepIndex, checkedState);

        return Results.Json(new { success = true, userId, date, itemType, itemId, stepIndex, checkedState });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── Add exercise to daily plan ──
app.MapPost("/api/plan/add", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string userId = context.User.Identity?.Name ?? "default";
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;

        string date = root.GetProperty("date").GetString() ?? "";
        string exerciseId = root.GetProperty("exerciseId").GetString() ?? "";
        string category = root.GetProperty("category").GetString() ?? "";

        if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(exerciseId) || string.IsNullOrEmpty(category))
            return Results.Json(new { success = false, error = "Missing required fields" }, statusCode: 400);

        int newId = await repository.AddManualPlanEntryWithFutureAsync(userId, date, exerciseId, category);
        if (newId == -1)
            return Results.Json(new { success = false, error = "Exercise already in plan or no enrollment" }, statusCode: 409);

        return Results.Json(new { success = true, id = newId });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── Get exercises by category (for picker) ──
app.MapGet("/api/exercises", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string category = context.Request.Query["category"].ToString();
        if (string.IsNullOrEmpty(category))
            return Results.Json(new { success = false, error = "Missing category" }, statusCode: 400);

        var exercises = await repository.GetExercisesByCategoryAsync(category);
        return Results.Json(exercises.Select(e => new {
            id = e.Id,
            name = e.Name,
            targets = e.Targets,
            defaultRx = e.DefaultRx ?? ""
        }));
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── Get available supplements to add (not yet in user's time group) ──
app.MapGet("/api/supplements/available", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string userId = context.User.Identity?.Name ?? "default";
        string timeGroup = context.Request.Query["timeGroup"].ToString();
        if (string.IsNullOrEmpty(timeGroup))
            return Results.Json(new { success = false, error = "Missing timeGroup" }, statusCode: 400);

        var supplements = await repository.GetAvailableSupplementsForGroupAsync(userId, timeGroup);
        return Results.Json(supplements.Select(s => new {
            id = s.Id,
            name = s.Name,
            dose = s.Dose ?? "",
            time = s.Time ?? "",
            timeGroup = s.TimeGroup
        }));
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── Add supplement to user's active list (with time group) ──
app.MapPost("/api/supplements/add", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string userId = context.User.Identity?.Name ?? "default";
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;

        string supplementId = root.GetProperty("supplementId").GetString() ?? "";
        string date = root.GetProperty("date").GetString() ?? "";
        string timeGroup = root.GetProperty("timeGroup").GetString() ?? "";

        if (string.IsNullOrEmpty(supplementId) || string.IsNullOrEmpty(date) || string.IsNullOrEmpty(timeGroup))
            return Results.Json(new { success = false, error = "Missing required fields" }, statusCode: 400);

        bool added = await repository.AddUserSupplementAsync(userId, supplementId, timeGroup, date);
        if (!added)
            return Results.Json(new { success = false, error = "Supplement already in this time group" }, statusCode: 409);

        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/debug", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";
    string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

    try
    {
        var checks = await repository.GetDailyChecksAsync(userId, todayDate);
        return Results.Json(new
        {
            userId,
            todayDate,
            utcNow = DateTime.UtcNow.ToString("o"),
            checksCount = checks.Count,
            checks = checks.Select(c => new { c.ItemType, c.ItemId, c.StepIndex, c.Checked })
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/milestone", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string id = root.GetProperty("id").GetString() ?? "";
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await repository.CompleteUserMilestoneAsync(userId, id, today);
        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/logsession", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Json(new { success = false, error = "not authenticated" });

    string userId = context.User.Identity?.Name ?? "default";
    string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

    try
    {
        // Use UserDailyPlan instead of SessionSteps
        var planEntries = await repository.GetUserDailyPlanAsync(userId, todayDate);
        var settings = await repository.GetUserSettingsAsync(userId);
        var disabledToolIds = (settings?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        int totalSteps = planEntries.Count(e => !disabledToolIds.Contains(e.ExerciseId));
        var dailyChecks = await repository.GetDailyChecksAsync(userId, todayDate);
        int completedSteps = dailyChecks.Count(c => c.ItemType == "step" && c.Checked);

        await repository.LogSessionAsync(userId, todayDate, completedSteps, totalSteps);
        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── AI Chat endpoint - calls Anthropic Claude API with full program context ──
app.MapPost("/api/ai/chat", async (HttpContext context, RubberJointsAIRepository repository, IHttpClientFactory httpFactory, IConfiguration config) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";
    string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
    string dayOfWeek = DateTime.UtcNow.DayOfWeek.ToString();

    try
    {
        // Parse user message
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;
        string userMessage = root.GetProperty("message").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(userMessage))
            return Results.Json(new { success = false, error = "Empty message" }, statusCode: 400);

        // ── Gather full program context for the system prompt ──

        // 1. Enrollment & week
        var enrollment = await repository.GetActiveEnrollmentAsync(userId);
        string enrollmentInfo = "No active enrollment.";
        int week = 1;
        int totalWeeks = 4;
        if (enrollment != null)
        {
            var enrollStart = DateTime.Parse(enrollment.StartDate);
            int daysSince = (DateTime.UtcNow.Date - enrollStart.Date).Days;
            week = Math.Max(1, daysSince / 7 + 1);
            enrollmentInfo = $"Program: {enrollment.ProgramName}, Started: {enrollment.StartDate}, Week {week} of {totalWeeks}";
        }

        // 2. Today's daily plan
        var planEntries = await repository.GetUserDailyPlanAsync(userId, todayDate);
        var allExercises = await repository.GetAllExercisesAsync();
        var exerciseMap = allExercises.ToDictionary(e => e.Id, e => e);
        var settings = await repository.GetUserSettingsAsync(userId);
        var disabledIds = (settings?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        string dayType = planEntries.FirstOrDefault()?.DayType ?? "unknown";
        var todaySteps = planEntries
            .Where(p => !disabledIds.Contains(p.ExerciseId))
            .Select(p => {
                var ex = exerciseMap.GetValueOrDefault(p.ExerciseId);
                return new { Name = ex?.Name ?? p.ExerciseId, Category = p.Category, Rx = p.Rx ?? ex?.DefaultRx ?? "", Targets = ex?.Targets ?? "" };
            }).ToList();

        // 3. Today's completion status
        var dailyChecks = await repository.GetDailyChecksAsync(userId, todayDate);
        var completedIds = dailyChecks.Where(c => c.Checked && c.ItemType == "step").Select(c => c.ItemId).ToHashSet();
        int totalExercises = todaySteps.Count;
        int completedExercises = todaySteps.Count(s => completedIds.Contains(exerciseMap.FirstOrDefault(e => e.Value.Name == s.Name).Key ?? ""));

        // 4. Supplements status
        var supplements = await repository.GetUserSupplementsForDateAsync(userId, todayDate);
        var suppChecks = dailyChecks.Where(c => c.ItemType == "supplement").ToList();
        int totalSupps = supplements.Count;
        int completedSupps = suppChecks.Count(c => c.Checked);

        // 5. Milestones
        var milestones = await repository.GetUserMilestonesAsync(userId);
        int totalMilestones = milestones.Count;
        int completedMilestones = milestones.Count(m => !string.IsNullOrEmpty(m.CompletedDate));

        // 6. Session history (last 7 days)
        var sessionLogs = await repository.GetSessionLogsAsync(userId);
        var recentSessions = sessionLogs.OrderByDescending(s => s.Date).Take(7).ToList();

        // ── Build the system prompt ──
        var sb = new StringBuilder();
        sb.AppendLine("You are the AI Coach for RubberJointsAI, a mobility and joint health tracking application.");
        sb.AppendLine("You help users with their mobility program by answering questions about their exercises, progress, supplements, and training plan.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- ONLY respond about topics related to this mobility/fitness application, the user's program, exercises, supplements, milestones, and general mobility/joint health.");
        sb.AppendLine("- If the user asks about anything unrelated (politics, coding, recipes, etc.), politely redirect: \"I'm your mobility coach — I can help with your exercises, progress, and training plan. What would you like to know about your program?\"");
        sb.AppendLine("- Keep responses concise (2-4 short paragraphs max). Use a friendly, encouraging coaching tone.");
        sb.AppendLine("- Never invent exercise data. Only reference what's in the user's actual program below.");
        sb.AppendLine("- If the user asks to change their plan, explain what adjustments are possible and encourage them.");
        sb.AppendLine();
        sb.AppendLine("=== USER CONTEXT ===");
        sb.AppendLine($"User: {userId}");
        sb.AppendLine($"Today: {dayOfWeek}, {todayDate}");
        sb.AppendLine($"Enrollment: {enrollmentInfo}");
        sb.AppendLine($"Day Type: {dayType}");
        sb.AppendLine();

        sb.AppendLine("=== TODAY'S EXERCISES ===");
        var grouped = todaySteps.GroupBy(s => s.Category);
        foreach (var g in grouped)
        {
            sb.AppendLine($"[{g.Key.ToUpper()}]");
            foreach (var s in g)
                sb.AppendLine($"  - {s.Name} ({s.Targets}) — {s.Rx}");
        }
        sb.AppendLine($"Completion: {completedExercises}/{totalExercises} exercises done today");
        sb.AppendLine();

        sb.AppendLine("=== SUPPLEMENTS ===");
        foreach (var s in supplements)
            sb.AppendLine($"  - {s.Name} ({s.Dose}) — {s.TimeGroup}");
        sb.AppendLine($"Supplements taken: {completedSupps}/{totalSupps}");
        sb.AppendLine();

        sb.AppendLine("=== MILESTONES ===");
        foreach (var m in milestones)
        {
            string status = string.IsNullOrEmpty(m.CompletedDate) ? "Not yet" : $"Done {m.CompletedDate}";
            sb.AppendLine($"  - {m.Name}: {status}");
        }
        sb.AppendLine($"Milestones completed: {completedMilestones}/{totalMilestones}");
        sb.AppendLine();

        sb.AppendLine("=== RECENT SESSION HISTORY ===");
        if (recentSessions.Any())
        {
            foreach (var log in recentSessions)
                sb.AppendLine($"  - {log.Date}: {log.CompletedSteps}/{log.TotalSteps} steps ({(log.TotalSteps > 0 ? (log.CompletedSteps * 100 / log.TotalSteps) : 0)}%)");
        }
        else
        {
            sb.AppendLine("  No sessions logged yet.");
        }

        string systemPrompt = sb.ToString();

        // ── Call Anthropic Claude API ──
        string apiKey = config["Anthropic:ApiKey"] ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return Results.Json(new { success = false, error = "AI not configured" }, statusCode: 500);

        var client = httpFactory.CreateClient("Anthropic");
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);

        var requestBody = new
        {
            model = "claude-haiku-4-5-20251001",
            max_tokens = 800,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userMessage } }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("v1/messages", jsonContent);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            app.Logger.LogError("Anthropic API error: {Status} {Body}", response.StatusCode, responseBody);
            return Results.Json(new { success = false, error = $"AI error: {response.StatusCode}" }, statusCode: 500);
        }

        using var responseDoc = JsonDocument.Parse(responseBody);
        var content = responseDoc.RootElement.GetProperty("content");
        string aiText = "";
        foreach (var block in content.EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "text")
                aiText += block.GetProperty("text").GetString();
        }

        return Results.Json(new { success = true, response = aiText });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "AI chat error");
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

app.Run();
