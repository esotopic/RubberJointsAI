using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using RubberJointsAI.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddMemoryCache();
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
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

// Suppress antiforgery cookie — we have IgnoreAntiforgeryTokenAttribute globally so it's unused
builder.Services.AddAntiforgery(options =>
{
    options.SuppressXFrameOptionsHeader = true;
    options.Cookie.Name = "__af";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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
    app.UseHsts();
}

app.UseHttpsRedirection();

// Add security headers + strip unnecessary cookies
app.Use(async (context, next) =>
{
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; connect-src 'self'; font-src 'self'; frame-ancestors 'self'";

    // Strip Azure ARRAffinity cookies and antiforgery cookie from responses
    context.Response.OnStarting(() =>
    {
        if (context.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
        {
            var filtered = cookies.Where(c =>
                c != null
                && !c.StartsWith("ARRAffinity=", StringComparison.OrdinalIgnoreCase)
                && !c.StartsWith("ARRAffinitySameSite=", StringComparison.OrdinalIgnoreCase)
                && !c.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase)
                && !c.StartsWith("__af=", StringComparison.OrdinalIgnoreCase)
            ).ToArray();
            context.Response.Headers.Remove("Set-Cookie");
            foreach (var cookie in filtered)
                context.Response.Headers.Append("Set-Cookie", cookie);
        }
        return Task.CompletedTask;
    });

    await next();
});

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

// Redirect bare "/" to "/AI" for authenticated users.
// The Today page is accessed via "/Index" (nav links, date links all use /Index).
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var path = context.Request.Path.Value ?? "";

        // Redirect bare "/" to AI page
        if (path == "/")
        {
            context.Response.Redirect("/AI");
            return;
        }

        // During onboarding, lock all pages except AI, Login, Logout, and API endpoints
        var lockedPaths = new[] { "/Index", "/Week", "/Progress", "/Settings" };
        if (lockedPaths.Any(lp => path.Equals(lp, StringComparison.OrdinalIgnoreCase)))
        {
            var repo = context.RequestServices.GetRequiredService<RubberJointsAI.Data.RubberJointsAIRepository>();
            var uid = context.User.Identity?.Name ?? "default";
            try
            {
                var prefs = await repo.GetUserPreferencesAsync(uid);
                if (prefs == null || prefs.OnboardingStep < 7)
                {
                    context.Response.Redirect("/AI");
                    return;
                }
            }
            catch { /* allow through on DB error */ }
        }
    }
    await next();
});

app.MapRazorPages();

// ── Minimal API endpoints (bypass Razor Pages routing for reliable JSON responses) ──

// ── Lightweight stats for AI page (exercise/supplement counts only) ──
app.MapGet("/api/ai-stats", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";
    var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
    string todayDate = pacificNow.ToString("yyyy-MM-dd");

    try
    {
        var planTask = repository.GetUserDailyPlanAsync(userId, todayDate);
        var checksTask = repository.GetDailyChecksAsync(userId, todayDate);
        var supplementsTask = repository.GetUserSupplementsForDateAsync(userId, todayDate);

        await Task.WhenAll(planTask, checksTask, supplementsTask);

        var plan = await planTask;
        var checks = await checksTask;
        var supplements = await supplementsTask;

        return Results.Json(new
        {
            success = true,
            exercisesTotal = plan.Count,
            exercisesDone = checks.Count(c => c.ItemType == "step" && c.Checked),
            supplementsTotal = supplements.Count,
            supplementsDone = checks.Count(c => c.ItemType == "supplement" && c.Checked)
        });
    }
    catch
    {
        return Results.Json(new { success = false });
    }
});

// ── Prefetch Today page data into memory cache (called from AI page in background) ──
app.MapGet("/api/today-prefetch", async (HttpContext context, RubberJointsAIRepository repository, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";
    var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
    string todayDate = pacificNow.ToString("yyyy-MM-dd");

    try
    {
        // Run all queries in parallel — same ones Index.cshtml.cs needs
        var enrollmentTask = repository.GetActiveEnrollmentAsync(userId);
        var planTask = repository.GetUserDailyPlanAsync(userId, todayDate);
        var settingsTask = repository.GetUserSettingsAsync(userId);
        var exercisesTask = repository.GetAllExercisesAsync();
        var checksTask = repository.GetDailyChecksAsync(userId, todayDate);
        var supplementsTask = repository.GetUserSupplementsForDateAsync(userId, todayDate);

        await Task.WhenAll(enrollmentTask, planTask, settingsTask, exercisesTask, checksTask, supplementsTask);

        // Store raw query results in cache keyed by userId
        string cacheKey = $"today-prefetch:{userId}:{todayDate}";
        var cachedData = new Dictionary<string, object?>
        {
            ["enrollment"] = await enrollmentTask,
            ["plan"] = await planTask,
            ["settings"] = await settingsTask,
            ["exercises"] = await exercisesTask,
            ["checks"] = await checksTask,
            ["supplements"] = await supplementsTask,
            ["todayDate"] = todayDate
        };

        cache.Set(cacheKey, cachedData, TimeSpan.FromSeconds(60));
        return Results.Json(new { success = true, cached = true });
    }
    catch
    {
        return Results.Json(new { success = false });
    }
});

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

// ── Delete exercise from today and all future sessions ──
app.MapPost("/api/plan/remove", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string userId = context.User.Identity?.Name ?? "default";
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;

        string exerciseId = root.GetProperty("exerciseId").GetString() ?? "";
        if (string.IsNullOrEmpty(exerciseId))
            return Results.Json(new { success = false, error = "Missing exerciseId" }, statusCode: 400);

        // Get today's date in Pacific time
        var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
        string today = pacificNow.ToString("yyyy-MM-dd");

        int removed = await repository.RemoveExerciseFromFuturePlanAsync(userId, exerciseId, today);
        return Results.Json(new { success = true, removed });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// ── Unified toggle: add/remove exercise or supplement from user preferences + regenerate plan ──
app.MapPost("/api/preferences/toggle", async (HttpContext context, RubberJointsAIRepository repository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    try
    {
        string userId = context.User.Identity?.Name ?? "default";
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;

        string? exerciseId = root.TryGetProperty("exerciseId", out var eid) ? eid.GetString() : null;
        string? supplementId = root.TryGetProperty("supplementId", out var sid) ? sid.GetString() : null;
        bool enabled = root.TryGetProperty("enabled", out var en) && en.GetBoolean();

        if (string.IsNullOrEmpty(exerciseId) && string.IsNullOrEmpty(supplementId))
            return Results.Json(new { success = false, error = "Missing exerciseId or supplementId" }, statusCode: 400);

        var prefs = await repository.GetUserPreferencesAsync(userId);
        if (prefs == null)
            return Results.Json(new { success = false, error = "No preferences found" }, statusCode: 404);

        if (!string.IsNullOrEmpty(exerciseId))
        {
            var ids = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (enabled && !ids.Contains(exerciseId))
                ids.Add(exerciseId);
            else if (!enabled)
                ids.Remove(exerciseId);
            prefs.SelectedExercises = string.Join(",", ids);
        }

        if (!string.IsNullOrEmpty(supplementId))
        {
            var ids = (prefs.SelectedSupplements ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (enabled && !ids.Contains(supplementId))
                ids.Add(supplementId);
            else if (!enabled)
                ids.Remove(supplementId);
            prefs.SelectedSupplements = string.Join(",", ids);
        }

        await repository.SaveUserPreferencesAsync(prefs);
        await repository.RegenerateFuturePlanAsync(userId, prefs);

        return Results.Json(new { success = true, enabled });
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

// ── AI Chat endpoint - handles onboarding (state machine) + ongoing management (tool use) ──
app.MapPost("/api/ai/chat", async (HttpContext context, RubberJointsAIRepository repository, IHttpClientFactory httpFactory, IConfiguration config) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    string userId = context.User.Identity?.Name ?? "default";
    var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
    string todayDate = pacificNow.ToString("yyyy-MM-dd");
    string dayOfWeek = pacificNow.DayOfWeek.ToString();

    try
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;
        string userMessage = root.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";

        // Parse conversation history
        var history = new List<object>();
        if (root.TryGetProperty("history", out var hp) && hp.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hp.EnumerateArray())
            {
                string role = h.GetProperty("role").GetString() ?? "user";
                string content = h.GetProperty("content").GetString() ?? "";
                history.Add(new { role, content });
            }
        }

        // Parse onboarding selection if provided
        int? selectionStep = null;
        JsonElement selectionData = default;
        if (root.TryGetProperty("selection", out var sp) && sp.ValueKind == JsonValueKind.Object)
        {
            selectionStep = sp.GetProperty("step").GetInt32();
            selectionData = sp.GetProperty("data");
        }

        // Get user preferences (onboarding state)
        var prefs = await repository.GetUserPreferencesAsync(userId);
        bool isOnboarding = prefs == null || prefs.OnboardingStep < 7;

        string apiKey = config["Anthropic:ApiKey"] ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return Results.Json(new { success = false, error = "AI not configured" }, statusCode: 500);

        // ═══════════════════════════════════════════════════════════
        //  ONBOARDING MODE
        // ═══════════════════════════════════════════════════════════
        if (isOnboarding)
        {
            if (prefs == null)
                prefs = new RubberJointsAI.Models.UserPreferences { UserId = userId, OnboardingStep = 0 };

            // Process selection if provided
            if (selectionStep.HasValue)
            {
                switch (selectionStep.Value)
                {
                    case 0: // Welcome acknowledged → move to step 1 (schedule + choice)
                        prefs.OnboardingStep = 1;
                        break;
                    case 1: // days/schedule selection — generate directly or go to customize
                        int selectedDayCount = 4; // default
                        if (selectionData.TryGetProperty("days_per_week", out var dpw))
                            selectedDayCount = dpw.GetInt32();

                        bool isQuickStart = selectionData.TryGetProperty("quick_start", out var qs) && qs.GetBoolean();
                        if (isQuickStart)
                        {
                            // Generate plan with ALL exercises
                            var autoExercises = await repository.GetAllExercisesAsync();
                            var autoIds = autoExercises.Select(e => e.Id).ToList();
                            prefs.SelectedExercises = string.Join(",", autoIds);
                            prefs.DaysPerWeek = selectedDayCount;
                            prefs.SelectedSupplements = "";
                            prefs.OnboardingStep = 7;
                            await repository.SaveUserPreferencesAsync(prefs);
                            await repository.GenerateCustomPlanAsync(userId, prefs);
                            var qsPrompt = $"You are the AI Coach for a hilariously serious joint & mobility workout program — because your joints called and they want better treatment. The user '{userId}' just did a quick setup! Their 4-week plan has been auto-generated: {selectedDayCount} days/week using all available exercises! Each session has 3 sections: a quick warm-up, 10-20 min of targeted mobility (rotated daily — CARs, hip switches, cat-cow, wall slides, deep squats, dead hangs, etc.), and a recovery cool-down (foam roller, yoga flow, cold plunge, etc.). The exercises rotate each day so it stays interesting! Celebrate! Keep it to 2-3 sentences. Make a funny joint/mobility joke. Tell them to tap START TRAINING to begin. Mention they can remove exercises they don't want from the Workout tab.";
                            var qsText = await CallClaudeAsync(httpFactory, apiKey, qsPrompt, "Quick start!", history);
                            return Results.Json(new { success = true, response = qsText, onboarding_step = 7, onboarding_complete = true });
                        }

                        // Customize path: go to step 2 (deselect picker)
                        prefs.DaysPerWeek = selectedDayCount;
                        prefs.OnboardingStep = 2;
                        break;
                    case 2: // Warm-up customization: deselect warmups you don't want
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        // Store warmup selections, move to mobility
                        prefs.SelectedExercises = string.Join(",", keptIds);
                        prefs.OnboardingStep = 3;
                        break;
                    }
                    case 3: // Mobility customization: deselect mobility you don't want
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        // Append mobility to existing warmup selections
                        var existing = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        existing.AddRange(keptIds);
                        prefs.SelectedExercises = string.Join(",", existing);
                        prefs.OnboardingStep = 4;
                        break;
                    }
                    case 4: // Recovery customization: deselect recovery you don't want
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        // Append recovery to existing selections
                        var existing = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        existing.AddRange(keptIds);
                        prefs.SelectedExercises = string.Join(",", existing);
                        prefs.OnboardingStep = 5;
                        break;
                    }
                    case 5: // Supplements: user opts IN to supplements they want
                    {
                        var selectedSuppIds = new List<string>();
                        if (selectionData.TryGetProperty("selected_ids", out var selEl))
                            foreach (var id in selEl.EnumerateArray()) selectedSuppIds.Add(id.GetString() ?? "");
                        prefs.SelectedSupplements = string.Join(",", selectedSuppIds);
                        prefs.OnboardingStep = 7;
                        await repository.SaveUserPreferencesAsync(prefs);
                        await repository.GenerateCustomPlanAsync(userId, prefs);
                        var exIds = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                        int exCount = exIds.Length;
                        int suppCount = selectedSuppIds.Count;
                        var custPrompt = $"You are the AI Coach for a hilariously serious joint & mobility workout program. The user '{userId}' just finished customizing their plan! They selected {exCount} exercises and {suppCount} supplements. Their 4-week plan has been generated: {prefs.DaysPerWeek} days/week, each session has warm-up, mobility, and recovery sections that rotate daily. Week 1 starts gentle to build the habit, then gradually increases. Celebrate! Keep it to 2-3 sentences. Make a funny joint/mobility joke. Tell them to tap START TRAINING to begin.";
                        var custText = await CallClaudeAsync(httpFactory, apiKey, custPrompt, "Plan customized!", history);
                        return Results.Json(new { success = true, response = custText, onboarding_step = 7, onboarding_complete = true });
                    }
                }
                await repository.SaveUserPreferencesAsync(prefs);
            }

            // Build onboarding system prompt for current step
            int step = prefs.OnboardingStep;
            var allExercises = await repository.GetAllExercisesAsync();

            string stepContext = step switch
            {
                0 => "Welcome the user! This is their very first time. Give a warm, short intro to the program: it's a 4-week joint health & mobility program with warm-ups, mobility work, and recovery. The app will show a 'Let's Go' button below. Keep it to 2-3 sentences. Make a funny joint/mobility joke.",
                1 => "The app will show options below: 'Generate My Plan' (uses all exercises, starts right away) or 'Customize First' (lets them remove exercises they don't want). Keep it to 2 sentences. Say something encouraging.",
                2 => "Time to pick warm-up exercises! Everything is turned on by default — just tap to remove any warm-ups that don't work for you. 1-2 sentences, keep it light.",
                3 => "Now for mobility exercises! Same deal — everything's enabled, just turn off what you don't want. 1-2 sentences, be encouraging about mobility.",
                4 => "Last exercise category: recovery tools! Turn off anything you don't have access to. 1-2 sentences, say something fun about recovery.",
                5 => "Final step: supplements! These are all OFF by default — turn on any you want to track. Totally optional. 1-2 sentences, be chill about it.",
                _ => "Onboarding complete."
            };

            string onboardingSystemPrompt = $@"You are the AI Coach for a hilariously serious joint & mobility workout program — because your joints deserve better than cracking every time you stand up.
The user '{userId}' is going through initial setup (onboarding step {step} of 7).

YOUR JOB: {stepContext}

RULES:
- Keep responses SHORT (2-3 sentences max). Mobile users.
- Be warm, funny, encouraging. Joint/mobility humor is great.
- The app shows interactive UI below your message — do NOT list exercises or options.
- Do NOT use bullet points or numbered lists.
- Do NOT say 'I' or refer to yourself excessively.";

            // Get Claude text for this step
            string aiResponse = await CallClaudeAsync(httpFactory, apiKey, onboardingSystemPrompt,
                string.IsNullOrEmpty(userMessage) ? "Let's go!" : userMessage, history);

            // Build UI component for current step
            object? uiComponent = null;
            var allSupplements = step == 5 ? await repository.GetSupplementsAsync() : new List<RubberJointsAI.Models.Supplement>();
            switch (step)
            {
                case 0: // Welcome — simple "Let's Go" button
                    uiComponent = new { type = "welcome_start", id = "welcome" };
                    break;
                case 1: // Generate plan or customize
                    uiComponent = new { type = "plan_or_customize", id = "plan_choice" };
                    break;
                case 2: // Warm-up exercises (deselect what you don't want)
                {
                    var items = allExercises.Where(e => e.Category == "warmup_tool")
                        .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                    uiComponent = new { type = "category_deselect_picker", id = "warmup_picker", category_label = "🔥 Warm-Up Exercises", items, mode = "deselect" };
                    break;
                }
                case 3: // Mobility exercises (deselect what you don't want)
                {
                    var items = allExercises.Where(e => e.Category == "mobility")
                        .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                    uiComponent = new { type = "category_deselect_picker", id = "mobility_picker", category_label = "🧘 Mobility Exercises", items, mode = "deselect" };
                    break;
                }
                case 4: // Recovery exercises (deselect what you don't want)
                {
                    var items = allExercises.Where(e => e.Category == "recovery_tool")
                        .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                    uiComponent = new { type = "category_deselect_picker", id = "recovery_picker", category_label = "🧊 Recovery Tools", items, mode = "deselect" };
                    break;
                }
                case 5: // Supplements (opt-in: all OFF by default)
                {
                    var items = allSupplements
                        .Select(s => new { id = s.Id, name = s.Name, description = s.Dose ?? "", rx = s.Time ?? "", timeGroup = s.TimeGroup ?? "am" }).ToList();
                    uiComponent = new { type = "category_deselect_picker", id = "supplement_picker", category_label = "💊 Supplements (Optional)", items, mode = "select" };
                    break;
                }
            }

            return Results.Json(new { success = true, response = aiResponse, onboarding_step = step, ui_component = uiComponent });
        }

        // ═══════════════════════════════════════════════════════════
        //  REGULAR CHAT MODE (with tool use for ongoing management)
        // ═══════════════════════════════════════════════════════════
        var enrollment = await repository.GetActiveEnrollmentAsync(userId);
        string enrollmentInfo = "No active enrollment.";
        int week2 = 1, totalWeeks2 = 4;
        if (enrollment != null)
        {
            var enrollStart = DateTime.Parse(enrollment.StartDate);
            int daysSince = (pacificNow.Date - enrollStart.Date).Days;
            week2 = Math.Max(1, daysSince / 7 + 1);
            enrollmentInfo = $"Program: {enrollment.ProgramName}, Started: {enrollment.StartDate}, Week {week2} of {totalWeeks2}";
        }

        var planEntries2 = await repository.GetUserDailyPlanAsync(userId, todayDate);
        var allExercises2 = await repository.GetAllExercisesAsync();
        var exerciseMap2 = allExercises2.ToDictionary(e => e.Id, e => e);
        var settings2 = await repository.GetUserSettingsAsync(userId);
        var disabledIds2 = (settings2?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        string dayType2 = planEntries2.FirstOrDefault()?.DayType ?? "unknown";
        var todaySteps2 = planEntries2.Where(p => !disabledIds2.Contains(p.ExerciseId))
            .Select(p => { var ex = exerciseMap2.GetValueOrDefault(p.ExerciseId);
                return new { Name = ex?.Name ?? p.ExerciseId, Category = p.Category, Rx = p.Rx ?? ex?.DefaultRx ?? "", Targets = ex?.Targets ?? "" };
            }).ToList();

        var dailyChecks2 = await repository.GetDailyChecksAsync(userId, todayDate);
        var completedIds2 = dailyChecks2.Where(c => c.Checked && c.ItemType == "step").Select(c => c.ItemId).ToHashSet();
        int totalExercises2 = todaySteps2.Count;
        int completedExercises2 = todaySteps2.Count(s => completedIds2.Contains(exerciseMap2.FirstOrDefault(e => e.Value.Name == s.Name).Key ?? ""));

        var supplements2 = await repository.GetUserSupplementsForDateAsync(userId, todayDate);
        int completedSupps2 = dailyChecks2.Count(c => c.ItemType == "supplement" && c.Checked);

        var milestones2 = await repository.GetUserMilestonesAsync(userId);
        var sessionLogs2 = await repository.GetSessionLogsAsync(userId);
        var recentSessions2 = sessionLogs2.OrderByDescending(s => s.Date).Take(7).ToList();

        var sb2 = new StringBuilder();
        sb2.AppendLine("You are the AI Coach for a hilariously serious joint & mobility workout program — a mobile-first app that helps users stop sounding like a bag of microwave popcorn every time they move. You guide them through a structured 4-week mobility and joint health program.");
        sb2.AppendLine();
        sb2.AppendLine("=== YOUR TONE AND BEHAVIOR ===");
        sb2.AppendLine("- Be warm, human, and conversational. Keep responses concise: 2-4 short paragraphs max. Mobile users.");
        sb2.AppendLine($"- User's name: {userId}. Use it when natural.");
        sb2.AppendLine("- Be ALWAYS encouraging. Celebrate any progress, no matter how small. Never blame the user for skipping exercises or missing days.");
        sb2.AppendLine("- If they didn't complete everything, focus on what they DID do: 'You knocked out 4 exercises today — that's 4 more than yesterday's couch!' Not: 'You skipped 3 exercises.'");
        sb2.AppendLine("- The program starts gentle (Week 1) and builds gradually. Remind users this is by design — consistency matters more than intensity.");
        sb2.AppendLine("- When suggesting plan changes, frame them positively: 'Your joints are ready for more!' not 'You need to do more.'");
        sb2.AppendLine("- NEVER invent exercises, supplements, or data that isn't in the context below.");
        sb2.AppendLine();
        sb2.AppendLine("=== ABSOLUTE RULES ===");
        sb2.AppendLine("1. ONLY answer about: this app, exercises, supplements, milestones, progress, joint health, mobility, recovery/stretching.");
        sb2.AppendLine("2. For ANYTHING unrelated: \"I'm your mobility coach and can only help with your joint workout program — exercises, supplements, progress, and joint health. What would you like to know about your plan?\"");
        sb2.AppendLine("3. Never act as doctor/trainer/PT. Never diagnose injuries.");
        sb2.AppendLine();

        // === TOOL USE INSTRUCTIONS ===
        sb2.AppendLine("=== TOOL USE ===");
        sb2.AppendLine("You have tools to modify the user's plan. Use them when the user asks to add/remove exercises, change supplements, or adjust their schedule.");
        sb2.AppendLine("After using a tool, confirm what changed in a friendly way.");
        sb2.AppendLine("When adding exercises, use get_all_exercises first to see what's available.");
        sb2.AppendLine();

        sb2.AppendLine("=== LIVE USER DATA ===");
        sb2.AppendLine($"User: {userId} | Today: {dayOfWeek}, {todayDate} | Enrollment: {enrollmentInfo} | Session type: {dayType2}");
        sb2.AppendLine();

        sb2.AppendLine("--- TODAY'S EXERCISES ---");
        foreach (var g in todaySteps2.GroupBy(s => s.Category))
        {
            sb2.AppendLine($"[{g.Key.ToUpper()}]");
            foreach (var s in g) sb2.AppendLine($"  - {s.Name} | {s.Targets} | {s.Rx}");
        }
        sb2.AppendLine($"Progress: {completedExercises2}/{totalExercises2} done.");
        sb2.AppendLine();

        sb2.AppendLine("--- ALL EXERCISES ---");
        foreach (var ex in allExercises2)
            sb2.AppendLine($"  - {ex.Name} [{ex.Category}] | {ex.Targets} | {ex.DefaultRx ?? "varies"}");
        sb2.AppendLine();

        sb2.AppendLine("--- SUPPLEMENTS ---");
        foreach (var s in supplements2)
            sb2.AppendLine($"  - {s.Name} | {s.Dose} | {s.Time} | {s.TimeGroup}");
        sb2.AppendLine($"Taken today: {completedSupps2}/{supplements2.Count}");
        sb2.AppendLine();

        sb2.AppendLine("--- MILESTONES ---");
        foreach (var m in milestones2)
            sb2.AppendLine($"  - {m.Name}: {(string.IsNullOrEmpty(m.AchievedDate) ? "Not yet" : m.AchievedDate)}");
        sb2.AppendLine();

        sb2.AppendLine("--- SESSION HISTORY (7 days) ---");
        foreach (var log in recentSessions2)
            sb2.AppendLine($"  - {log.Date}: {log.StepsDone}/{log.StepsTotal}");

        string systemPrompt2 = sb2.ToString();

        // Define tools for ongoing management
        var tools = new object[]
        {
            new { name = "get_all_exercises", description = "Get all available exercises in the system, optionally filtered by category",
                input_schema = new { type = "object", properties = new { category = new { type = "string", description = "Optional: warmup_tool, mobility, recovery_tool" } } } },
            new { name = "add_exercise_to_plan", description = "Add an exercise to the user's plan from today going forward",
                input_schema = new { type = "object", properties = new { exercise_id = new { type = "string" }, category = new { type = "string" } },
                    required = new[] { "exercise_id", "category" } } },
            new { name = "remove_exercise_from_plan", description = "Remove an exercise from the user's future plan days",
                input_schema = new { type = "object", properties = new { exercise_id = new { type = "string" } }, required = new[] { "exercise_id" } } },
            new { name = "add_supplement", description = "Add a supplement to the user's daily routine",
                input_schema = new { type = "object", properties = new { supplement_id = new { type = "string" }, time_group = new { type = "string", description = "am, mid, or pm" } },
                    required = new[] { "supplement_id", "time_group" } } },
            new { name = "remove_supplement", description = "Remove a supplement from the user's routine",
                input_schema = new { type = "object", properties = new { supplement_id = new { type = "string" } }, required = new[] { "supplement_id" } } },
            new { name = "get_all_supplements", description = "Get all available supplements in the system",
                input_schema = new { type = "object", properties = new { } } },
            new { name = "update_training_days", description = "Change how many days per week the user trains and regenerate plan",
                input_schema = new { type = "object", properties = new { days_per_week = new { type = "integer" } }, required = new[] { "days_per_week" } } }
        };

        // Build messages array
        var messages = new List<object>();
        foreach (var h in history)
            messages.Add(h);
        if (!string.IsNullOrWhiteSpace(userMessage))
            messages.Add(new { role = "user", content = userMessage });

        if (messages.Count == 0)
            return Results.Json(new { success = false, error = "No message" }, statusCode: 400);

        // Tool use loop
        var client = httpFactory.CreateClient("Anthropic");
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        string aiText = "";
        int maxLoops = 5;

        for (int loop = 0; loop < maxLoops; loop++)
        {
            var requestBody = new
            {
                model = "claude-haiku-4-5-20251001",
                max_tokens = 1024,
                system = systemPrompt2,
                tools,
                messages
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("v1/messages", jsonContent);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                app.Logger.LogError("Anthropic API error: {Status} {Body}", response.StatusCode, responseBody);
                return Results.Json(new { success = false, error = $"AI error: {response.StatusCode}" }, statusCode: 500);
            }

            using var responseDoc = JsonDocument.Parse(responseBody);
            var responseRoot = responseDoc.RootElement;
            string stopReason = responseRoot.GetProperty("stop_reason").GetString() ?? "";
            var contentBlocks = responseRoot.GetProperty("content");

            // Extract text and tool_use blocks
            var textParts = new List<string>();
            var toolUseCalls = new List<(string id, string name, JsonElement input)>();
            var assistantContentBlocks = new List<object>();

            foreach (var block in contentBlocks.EnumerateArray())
            {
                string blockType = block.GetProperty("type").GetString() ?? "";
                if (blockType == "text")
                {
                    string t = block.GetProperty("text").GetString() ?? "";
                    textParts.Add(t);
                    assistantContentBlocks.Add(new { type = "text", text = t });
                }
                else if (blockType == "tool_use")
                {
                    string toolId = block.GetProperty("id").GetString() ?? "";
                    string toolName = block.GetProperty("name").GetString() ?? "";
                    var toolInput = block.GetProperty("input");
                    toolUseCalls.Add((toolId, toolName, toolInput));
                    assistantContentBlocks.Add(new { type = "tool_use", id = toolId, name = toolName, input = toolInput });
                }
            }

            aiText = string.Join("", textParts);

            if (stopReason != "tool_use" || toolUseCalls.Count == 0)
                break; // Done — return text

            // Execute tools and continue loop
            messages.Add(new { role = "assistant", content = assistantContentBlocks });

            var toolResults = new List<object>();
            foreach (var (toolId, toolName, toolInput) in toolUseCalls)
            {
                string toolResult = "Unknown tool";
                try
                {
                    switch (toolName)
                    {
                        case "get_all_exercises":
                            string? catFilter = toolInput.TryGetProperty("category", out var cf) ? cf.GetString() : null;
                            var exList = allExercises2.Where(e => string.IsNullOrEmpty(catFilter) || e.Category == catFilter)
                                .Select(e => $"{e.Id}: {e.Name} [{e.Category}] - {e.Targets}");
                            toolResult = string.Join("\n", exList);
                            break;
                        case "add_exercise_to_plan":
                            string addExId = toolInput.GetProperty("exercise_id").GetString() ?? "";
                            string addCat = toolInput.GetProperty("category").GetString() ?? "";
                            int newId = await repository.AddManualPlanEntryWithFutureAsync(userId, todayDate, addExId, addCat);
                            toolResult = newId > 0 ? $"Added {addExId} to plan successfully." : "Exercise already in plan or failed.";
                            break;
                        case "remove_exercise_from_plan":
                            string rmExId = toolInput.GetProperty("exercise_id").GetString() ?? "";
                            using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connBuilder.ConnectionString))
                            {
                                await conn.OpenAsync();
                                using var cmd = conn.CreateCommand();
                                cmd.CommandText = "DELETE FROM UserDailyPlan WHERE UserId = @u AND ExerciseId = @e AND Date >= @d";
                                cmd.Parameters.AddWithValue("@u", userId);
                                cmd.Parameters.AddWithValue("@e", rmExId);
                                cmd.Parameters.AddWithValue("@d", todayDate);
                                int removed = await cmd.ExecuteNonQueryAsync();
                                toolResult = $"Removed {rmExId} from {removed} future plan days.";
                            }
                            break;
                        case "add_supplement":
                            string addSuppId = toolInput.GetProperty("supplement_id").GetString() ?? "";
                            string addTg = toolInput.GetProperty("time_group").GetString() ?? "am";
                            bool added = await repository.AddUserSupplementAsync(userId, addSuppId, addTg, todayDate);
                            toolResult = added ? $"Added supplement {addSuppId} to {addTg} group." : "Supplement already in that time group.";
                            break;
                        case "remove_supplement":
                            string rmSuppId = toolInput.GetProperty("supplement_id").GetString() ?? "";
                            using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connBuilder.ConnectionString))
                            {
                                await conn.OpenAsync();
                                using var cmd = conn.CreateCommand();
                                cmd.CommandText = "DELETE FROM UserSupplements WHERE UserId = @u AND SupplementId = @s";
                                cmd.Parameters.AddWithValue("@u", userId);
                                cmd.Parameters.AddWithValue("@s", rmSuppId);
                                int removed = await cmd.ExecuteNonQueryAsync();
                                toolResult = $"Removed supplement {rmSuppId}.";
                            }
                            break;
                        case "get_all_supplements":
                            var suppList = new List<string>();
                            using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connBuilder.ConnectionString))
                            {
                                await conn.OpenAsync();
                                using var cmd = conn.CreateCommand();
                                cmd.CommandText = "SELECT Id, Name, Dose, TimeGroup FROM Supplements ORDER BY Name";
                                using var reader = await cmd.ExecuteReaderAsync();
                                while (await reader.ReadAsync())
                                    suppList.Add($"{reader.GetString(0)}: {reader.GetString(1)} ({reader.GetString(2)}) [{reader.GetString(3)}]");
                            }
                            toolResult = string.Join("\n", suppList);
                            break;
                        case "update_training_days":
                            int newDays = toolInput.GetProperty("days_per_week").GetInt32();
                            var uprefs = await repository.GetUserPreferencesAsync(userId);
                            if (uprefs != null)
                            {
                                uprefs.DaysPerWeek = newDays;
                                await repository.SaveUserPreferencesAsync(uprefs);
                                await repository.GenerateCustomPlanAsync(userId, uprefs);
                                toolResult = $"Updated to {newDays} days/week and regenerated plan.";
                            }
                            else toolResult = "No preferences found.";
                            break;
                    }
                }
                catch (Exception ex) { toolResult = $"Error: {ex.Message}"; }

                toolResults.Add(new { type = "tool_result", tool_use_id = toolId, content = toolResult });
            }

            messages.Add(new { role = "user", content = toolResults });
        }

        return Results.Json(new { success = true, response = aiText, onboarding_step = 7 });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "AI chat error");
        return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
    }
});

// Helper: Simple Claude call for onboarding text
async Task<string> CallClaudeAsync(IHttpClientFactory httpFactory, string apiKey, string systemPrompt, string userMessage, List<object> history)
{
    var client = httpFactory.CreateClient("Anthropic");
    client.DefaultRequestHeaders.Add("x-api-key", apiKey);

    var messages = new List<object>();
    foreach (var h in history) messages.Add(h);
    messages.Add(new { role = "user", content = userMessage });

    var requestBody = new { model = "claude-haiku-4-5-20251001", max_tokens = 400, system = systemPrompt, messages };
    var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
    var response = await client.PostAsync("v1/messages", jsonContent);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode) return "Welcome! Let's get those joints sorted out. 🦴";

    using var responseDoc = JsonDocument.Parse(responseBody);
    var content = responseDoc.RootElement.GetProperty("content");
    var text = "";
    foreach (var block in content.EnumerateArray())
        if (block.GetProperty("type").GetString() == "text")
            text += block.GetProperty("text").GetString();
    return text;
}

app.Run();
