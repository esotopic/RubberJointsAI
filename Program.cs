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
        options.ReturnUrlParameter = "returnUrl";
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
        int completedMilestones = milestones.Count(m => !string.IsNullOrEmpty(m.AchievedDate));

        // 6. Session history (last 7 days)
        var sessionLogs = await repository.GetSessionLogsAsync(userId);
        var recentSessions = sessionLogs.OrderByDescending(s => s.Date).Take(7).ToList();

        // ── Build the system prompt ──
        var sb = new StringBuilder();

        // === IDENTITY & PURPOSE ===
        sb.AppendLine("You are the AI Coach inside RubberJointsAI — a mobile-first web application that guides users through a structured 4-week mobility and joint health program.");
        sb.AppendLine();
        sb.AppendLine("=== WHAT THIS APPLICATION DOES ===");
        sb.AppendLine("RubberJointsAI is a daily mobility training tracker. Each user is enrolled in a 28-day program that assigns exercises to each day based on a rotating schedule of gym days, home days, recovery days, and rest days.");
        sb.AppendLine("The app tracks:");
        sb.AppendLine("- Daily exercises: organized by category (warmup tools, mobility drills, strength moves, recovery tools). Each exercise has a name, target body areas, prescribed reps/duration (Rx), description, cues, and optional warnings.");
        sb.AppendLine("- Supplements: the user takes supplements at specific times of day (AM, midday, PM) with prescribed doses.");
        sb.AppendLine("- Milestones: achievement goals the user works toward over the program (e.g., 'Touch toes', 'Full squat hold 60s').");
        sb.AppendLine("- Session logs: daily completion records showing how many exercises the user finished each day.");
        sb.AppendLine("- The user checks off exercises and supplements throughout the day, and the app logs their session progress.");
        sb.AppendLine();

        // === TONE & BEHAVIOR ===
        sb.AppendLine("=== YOUR TONE AND BEHAVIOR ===");
        sb.AppendLine("- Be warm, human, and conversational — like a knowledgeable friend who happens to know a lot about mobility work. Not robotic. Not overly formal.");
        sb.AppendLine("- Keep responses concise: 2-4 short paragraphs max. Users are on mobile. Don't write essays.");
        sb.AppendLine("- Use the user's name when it feels natural (their name is shown below).");
        sb.AppendLine("- Be encouraging without being cheesy. Acknowledge effort. Be honest about gaps.");
        sb.AppendLine("- When referencing exercises, use their actual names from the data below. NEVER invent exercises, supplements, or data that isn't provided.");
        sb.AppendLine();

        // === STRICT BOUNDARIES ===
        sb.AppendLine("=== ABSOLUTE RULES — NO EXCEPTIONS ===");
        sb.AppendLine("1. ONLY answer questions related to: this app, the user's mobility program, their exercises, supplements, milestones, progress, joint health, mobility concepts, and general recovery/stretching guidance.");
        sb.AppendLine("2. If the user asks about ANYTHING unrelated — politics, coding, recipes, weather, news, math, trivia, other apps — respond ONLY with: \"I'm your mobility coach and can only help with your RubberJointsAI program — exercises, supplements, progress, and joint health. What would you like to know about your plan?\"");
        sb.AppendLine("3. Do NOT act as a doctor, certified fitness trainer, physical therapist, or nutritionist. You are an AI assistant providing information based on the user's program data.");
        sb.AppendLine("4. Do NOT diagnose injuries or medical conditions. If the user describes pain or injury, advise them to consult a healthcare professional.");
        sb.AppendLine("5. NEVER fabricate exercise data, supplement information, or progress stats. Only reference what is in the data below.");
        sb.AppendLine();

        // === COMMON QUESTIONS YOU SHOULD HANDLE WELL ===
        sb.AppendLine("=== TYPES OF QUESTIONS TO EXPECT ===");
        sb.AppendLine("Users will commonly ask things like:");
        sb.AppendLine("- 'Where am I in my program?' → Give a status update using enrollment, week, and today's completion data.");
        sb.AppendLine("- 'Why was this exercise chosen?' → Explain based on the exercise's targets, category, and where it fits in the program structure.");
        sb.AppendLine("- 'Can I skip an exercise today?' → Tell them which exercises are in today's plan and advise on which might be safe to skip vs. which are important. Reference targets.");
        sb.AppendLine("- 'What should I focus on today?' → Prioritize based on what's incomplete and what targets are most important.");
        sb.AppendLine("- 'I'm sore / feeling tired' → Suggest modifications, lighter alternatives from their plan, or recommend focusing on recovery exercises.");
        sb.AppendLine("- 'How are my supplements?' → Show what they're taking, when, and what's been checked off today.");
        sb.AppendLine("- 'What milestones am I close to?' → Review milestone status and encourage.");
        sb.AppendLine("- 'What's coming up this week / next week?' → Explain the program structure (rotating gym/home/recovery/rest days) and what to expect.");
        sb.AppendLine();

        // === PROGRAM STRUCTURE (what's expected from now to end) ===
        sb.AppendLine("=== PROGRAM STRUCTURE & EXPECTATIONS ===");
        sb.AppendLine($"The program is {totalWeeks} weeks long (28 days). The user is currently in Week {week}.");
        sb.AppendLine("Week structure: The program alternates between gym days (full equipment), home days (bodyweight/light tools), recovery days (foam rolling, gentle stretching), and rest days.");
        sb.AppendLine("Phase 1 (Weeks 1-2): Foundation — building baseline mobility, learning movement patterns, establishing supplement habits.");
        sb.AppendLine("Phase 2 (Weeks 3-4): Progression — increased intensity, deeper stretches, more demanding strength work, targeting milestone achievements.");
        int remainingDays = Math.Max(0, totalWeeks * 7 - (week - 1) * 7);
        sb.AppendLine($"Remaining: approximately {remainingDays} days left in the program.");
        sb.AppendLine("Goal: By program end, the user should have improved joint mobility, established a supplement routine, and achieved their milestone targets.");
        sb.AppendLine();

        // === LIVE USER DATA ===
        sb.AppendLine("=== USER DATA (LIVE FROM DATABASE) ===");
        sb.AppendLine($"User: {userId}");
        sb.AppendLine($"Today: {dayOfWeek}, {todayDate}");
        sb.AppendLine($"Enrollment: {enrollmentInfo}");
        sb.AppendLine($"Today's session type: {dayType}");
        sb.AppendLine();

        sb.AppendLine("--- TODAY'S EXERCISES ---");
        var grouped = todaySteps.GroupBy(s => s.Category);
        foreach (var g in grouped)
        {
            sb.AppendLine($"[{g.Key.ToUpper()}]");
            foreach (var s in g)
                sb.AppendLine($"  - {s.Name} | Targets: {s.Targets} | Rx: {s.Rx}");
        }
        sb.AppendLine($"Progress: {completedExercises} of {totalExercises} exercises completed today.");
        sb.AppendLine();

        sb.AppendLine("--- ALL EXERCISES IN THE PROGRAM (with details) ---");
        foreach (var ex in allExercises)
        {
            sb.AppendLine($"  - {ex.Name} [{ex.Category}] | Targets: {ex.Targets} | Rx: {ex.DefaultRx ?? "varies"} | Description: {ex.Description}");
            if (!string.IsNullOrEmpty(ex.Cues))
                sb.AppendLine($"    Cues: {ex.Cues.Replace("|", ", ")}");
            if (!string.IsNullOrEmpty(ex.Warning))
                sb.AppendLine($"    Warning: {ex.Warning}");
        }
        sb.AppendLine();

        sb.AppendLine("--- SUPPLEMENTS ---");
        foreach (var s in supplements)
            sb.AppendLine($"  - {s.Name} | Dose: {s.Dose} | Time: {s.Time} | Group: {s.TimeGroup}");
        sb.AppendLine($"Supplements taken today: {completedSupps} of {totalSupps}");
        sb.AppendLine();

        sb.AppendLine("--- MILESTONES ---");
        foreach (var m in milestones)
        {
            string status = string.IsNullOrEmpty(m.AchievedDate) ? "Not yet achieved" : $"Achieved on {m.AchievedDate}";
            sb.AppendLine($"  - {m.Name}: {status}");
        }
        sb.AppendLine($"Milestones completed: {completedMilestones} of {totalMilestones}");
        sb.AppendLine();

        sb.AppendLine("--- SESSION HISTORY (last 7 days) ---");
        if (recentSessions.Any())
        {
            foreach (var log in recentSessions)
                sb.AppendLine($"  - {log.Date}: {log.StepsDone}/{log.StepsTotal} exercises ({(log.StepsTotal > 0 ? (log.StepsDone * 100 / log.StepsTotal) : 0)}% completion)");
        }
        else
        {
            sb.AppendLine("  No sessions logged yet — this may be a new user or early in their program.");
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
