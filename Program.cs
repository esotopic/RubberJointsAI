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
        //  ONBOARDING MODE — AI-driven personalized questionnaire
        // ═══════════════════════════════════════════════════════════
        if (isOnboarding)
        {
            if (prefs == null)
                prefs = new RubberJointsAI.Models.UserPreferences { UserId = userId, OnboardingStep = 0 };

            // Process selection-based steps (buttons/pickers)
            if (selectionStep.HasValue)
            {
                switch (selectionStep.Value)
                {
                    case 0: // Welcome acknowledged → move to step 1 (AI questionnaire)
                        prefs.OnboardingStep = 1;
                        break;
                    case 2: // Generate or Customize choice
                    {
                        int selectedDayCount = prefs.DaysPerWeek > 0 ? prefs.DaysPerWeek : 4;
                        if (selectionData.TryGetProperty("days_per_week", out var dpw))
                            selectedDayCount = dpw.GetInt32();

                        bool isQuickStart = selectionData.TryGetProperty("quick_start", out var qs) && qs.GetBoolean();
                        if (isQuickStart)
                        {
                            // Generate plan with ALL exercises
                            var autoExercises = await repository.GetAllExercisesAsync();
                            prefs.SelectedExercises = string.Join(",", autoExercises.Select(e => e.Id));
                            prefs.DaysPerWeek = selectedDayCount;
                            prefs.SelectedSupplements = "";
                            prefs.OnboardingStep = 7;
                            await repository.SaveUserPreferencesAsync(prefs);
                            await repository.GenerateCustomPlanAsync(userId, prefs);
                            var qsPrompt = $"You are the AI Coach for a hilariously serious joint & mobility workout program. The user '{userId}' just did a quick setup! Their 4-week plan has been auto-generated: {selectedDayCount} days/week using all available exercises! Celebrate! Keep it to 2-3 sentences. Make a funny joint/mobility joke. Tell them to tap START TRAINING to begin. Mention they can remove exercises they don't want from Settings.";
                            var qsText = await CallClaudeAsync(httpFactory, apiKey, qsPrompt, "Quick start!", history);
                            return Results.Json(new { success = true, response = qsText, onboarding_step = 7, onboarding_complete = true });
                        }
                        // Customize path: go to step 3 (warmup picker)
                        prefs.DaysPerWeek = selectedDayCount;
                        prefs.OnboardingStep = 3;
                        break;
                    }
                    case 3: // Warm-up customization
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        prefs.SelectedExercises = string.Join(",", keptIds);
                        prefs.OnboardingStep = 4;
                        break;
                    }
                    case 4: // Mobility customization
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        var existing = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        existing.AddRange(keptIds);
                        prefs.SelectedExercises = string.Join(",", existing);
                        prefs.OnboardingStep = 5;
                        break;
                    }
                    case 5: // Recovery customization
                    {
                        var keptIds = new List<string>();
                        if (selectionData.TryGetProperty("kept_ids", out var keptEl))
                            foreach (var id in keptEl.EnumerateArray()) keptIds.Add(id.GetString() ?? "");
                        var existing = (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        existing.AddRange(keptIds);
                        prefs.SelectedExercises = string.Join(",", existing);
                        prefs.OnboardingStep = 6;
                        break;
                    }
                    case 6: // Supplements (opt-in)
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
                        var custPrompt = $"You are the AI Coach for a hilariously serious joint & mobility workout program. The user '{userId}' just finished customizing their plan! They selected {exCount} exercises and {suppCount} supplements. Their 4-week plan has been generated: {prefs.DaysPerWeek} days/week. Celebrate! Keep it to 2-3 sentences. Make a funny joint/mobility joke. Tell them to tap START TRAINING to begin.";
                        var custText = await CallClaudeAsync(httpFactory, apiKey, custPrompt, "Plan customized!", history);
                        return Results.Json(new { success = true, response = custText, onboarding_step = 7, onboarding_complete = true });
                    }
                }
                await repository.SaveUserPreferencesAsync(prefs);
            }

            int step = prefs.OnboardingStep;

            // Step 0: Show welcome + Let's Go button
            if (step == 0)
            {
                string welcomePrompt = $@"You are the AI Coach for a hilariously serious joint & mobility workout program — because your joints deserve better than cracking every time you stand up.
The user '{userId}' just arrived for the very first time.

YOUR JOB: Welcome them warmly! This is a personalized 4-week joint health & mobility program. Tell them you're going to ask a few quick questions to build a plan just for them. The app will show a 'Let's Go' button below. Keep it to 2-3 sentences. Make a funny joint/mobility joke.

RULES:
- Keep responses SHORT (2-3 sentences max). Mobile users.
- Be warm, funny, encouraging. Joint/mobility humor is great.
- Do NOT use bullet points or numbered lists.";
                string aiResponse0 = await CallClaudeAsync(httpFactory, apiKey, welcomePrompt,
                    string.IsNullOrEmpty(userMessage) ? "Hello!" : userMessage, history);
                object uiComp0 = new { type = "welcome_start", id = "welcome" };
                return Results.Json(new { success = true, response = aiResponse0, onboarding_step = 0, ui_component = uiComp0 });
            }

            // Steps 2-6: Generate/Customize choice and category pickers
            if (step >= 2 && step <= 6)
            {
                var allExercisesOb = await repository.GetAllExercisesAsync();

                string stepContext = step switch
                {
                    2 => "The app will show buttons below: 'Generate My Plan' (uses all exercises) or 'Customize First' (lets them pick). Keep it to 2 sentences. Say something encouraging about their plan being ready.",
                    3 => "Time to pick warm-up exercises! Everything is turned on by default — just tap to remove any warm-ups that don't work for you. 1-2 sentences, keep it light.",
                    4 => "Now for mobility exercises! Same deal — everything's enabled, just turn off what you don't want. 1-2 sentences, be encouraging about mobility.",
                    5 => "Last exercise category: recovery tools! Turn off anything you don't have access to. 1-2 sentences, say something fun about recovery.",
                    6 => "Final step: supplements! These are all OFF by default — turn on any you want to track. Totally optional. 1-2 sentences, be chill about it.",
                    _ => ""
                };

                string obStepPrompt = $@"You are the AI Coach for a hilariously serious joint & mobility workout program.
The user '{userId}' is in setup step {step}. {stepContext}
RULES: Keep responses SHORT (2-3 sentences max). Mobile users. Be warm, funny, encouraging. The app shows interactive UI below your message — do NOT list exercises or options. Do NOT use bullet points.";

                string obStepText = await CallClaudeAsync(httpFactory, apiKey, obStepPrompt,
                    string.IsNullOrEmpty(userMessage) ? "Next step!" : userMessage, history);

                object? obUiComp = null;
                var allSuppsOb = step == 6 ? await repository.GetSupplementsAsync() : new List<RubberJointsAI.Models.Supplement>();
                switch (step)
                {
                    case 2:
                        obUiComp = new { type = "plan_or_customize", id = "plan_choice" };
                        break;
                    case 3:
                    {
                        var items = allExercisesOb.Where(e => e.Category == "warmup_tool")
                            .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                        obUiComp = new { type = "category_deselect_picker", id = "warmup_picker", category_label = "🔥 Warm-Up Exercises", items, mode = "deselect" };
                        break;
                    }
                    case 4:
                    {
                        var items = allExercisesOb.Where(e => e.Category == "mobility")
                            .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                        obUiComp = new { type = "category_deselect_picker", id = "mobility_picker", category_label = "🧘 Mobility Exercises", items, mode = "deselect" };
                        break;
                    }
                    case 5:
                    {
                        var items = allExercisesOb.Where(e => e.Category == "recovery_tool")
                            .Select(e => new { id = e.Id, name = e.Name, description = e.Targets, rx = e.DefaultRx ?? "" }).ToList();
                        obUiComp = new { type = "category_deselect_picker", id = "recovery_picker", category_label = "🧊 Recovery Tools", items, mode = "deselect" };
                        break;
                    }
                    case 6:
                    {
                        var items = allSuppsOb
                            .Select(s => new { id = s.Id, name = s.Name, description = s.Dose ?? "", rx = s.Time ?? "", timeGroup = s.TimeGroup ?? "am" }).ToList();
                        obUiComp = new { type = "category_deselect_picker", id = "supplement_picker", category_label = "💊 Supplements (Optional)", items, mode = "select" };
                        break;
                    }
                }
                return Results.Json(new { success = true, response = obStepText, onboarding_step = step, ui_component = obUiComp });
            }

            // Step 1: AI-driven conversational questionnaire with tool use
            var allExercises = await repository.GetAllExercisesAsync();
            var exerciseList = string.Join(", ", allExercises.Select(e => $"{e.Name} [{e.Category}]"));

            string questionnaireSystem = $@"You are the AI Coach for a hilariously serious joint & mobility workout program — because your joints called and they want better treatment.

The user '{userId}' is new and you're getting to know them to build a personalized plan.

=== YOUR MISSION ===
Have a NATURAL, FRIENDLY conversation to learn about the user. Ask ONE question at a time. Be conversational — this is a chat, not a form.

=== WHAT YOU NEED TO LEARN (in roughly this order) ===
1. What brought them here? (joint pain, stiffness, injury recovery, prevention, general mobility, athletic performance)
2. Problem areas — which joints/body parts bother them most? (knees, back, shoulders, hips, neck, wrists, ankles, etc.)
3. Current activity level (sedentary, lightly active, moderately active, very active)
4. What equipment/tools do they have? (foam roller, resistance bands, yoga mat, lacrosse ball, pull-up bar, cold plunge, sauna, etc.) — or are they starting from scratch?
5. How many days per week can they commit? (2-6)
6. Any injuries, surgeries, or conditions to be careful about?

=== CONVERSATION RULES ===
- Ask ONE question at a time. Wait for their answer before asking the next.
- Keep each message SHORT (2-3 sentences max). Mobile users.
- Be warm, funny, encouraging. Joint/mobility humor welcome.
- Acknowledge their answers before asking the next question (e.g., 'Oh man, stiff hips are the WORST' or 'Nice — a foam roller is a game changer!').
- Do NOT use bullet points or numbered lists.
- If the user gives a long answer that covers multiple questions, great — skip ahead to what you don't know yet.
- After you have enough info (usually 4-6 exchanges), call the finalize_onboarding tool with a summary.

=== AVAILABLE EXERCISES IN OUR SYSTEM ===
{exerciseList}

=== WHEN TO FINALIZE ===
Call finalize_onboarding when you know at least: (1) their main goal/motivation, (2) problem areas, (3) days per week. Equipment and activity level are nice-to-have but not required — if they seem eager to start, don't drag it out. When in doubt, lean toward finishing sooner. The user can always adjust later from Settings.

DO NOT tell the user you're calling a tool. Just say something celebratory and mention their plan is being built.";

            // Onboarding questionnaire tools
            var onboardingTools = new object[]
            {
                new { name = "finalize_onboarding", description = "Call this when you've gathered enough info about the user. Saves their profile and generates a personalized plan.",
                    input_schema = new { type = "object", properties = new {
                        profile_summary = new { type = "string", description = "A concise summary of what you learned: goals, problem areas, activity level, equipment, injuries/cautions. This will be stored and used in all future conversations." },
                        days_per_week = new { type = "integer", description = "How many days per week the user wants to train (2-6). Default 4 if not explicitly stated." },
                        problem_areas = new { type = "string", description = "Comma-separated list of problem areas (e.g. 'knees,lower back,hips')" },
                        has_equipment = new { type = "boolean", description = "Whether user has recovery/mobility equipment" }
                    }, required = new[] { "profile_summary", "days_per_week" } } }
            };

            // Build messages
            var onboardMessages = new List<object>();
            foreach (var h in history) onboardMessages.Add(h);
            if (!string.IsNullOrWhiteSpace(userMessage))
                onboardMessages.Add(new { role = "user", content = userMessage });

            if (onboardMessages.Count == 0)
                onboardMessages.Add(new { role = "user", content = "Let's go!" });

            // Tool use loop for onboarding
            var obClient = httpFactory.CreateClient("Anthropic");
            obClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            string obAiText = "";
            bool onboardingComplete = false;
            int obMaxLoops = 5;

            for (int loop = 0; loop < obMaxLoops; loop++)
            {
                var obReqBody = new { model = "claude-haiku-4-5-20251001", max_tokens = 600, system = questionnaireSystem, tools = onboardingTools, messages = onboardMessages };
                var obJsonContent = new StringContent(JsonSerializer.Serialize(obReqBody), Encoding.UTF8, "application/json");
                var obResponse = await obClient.PostAsync("v1/messages", obJsonContent);
                var obResponseBody = await obResponse.Content.ReadAsStringAsync();

                if (!obResponse.IsSuccessStatusCode)
                {
                    app.Logger.LogError("Anthropic API error (onboarding): {Status} {Body}", obResponse.StatusCode, obResponseBody);
                    return Results.Json(new { success = false, error = $"AI error: {obResponse.StatusCode}" }, statusCode: 500);
                }

                using var obDoc = JsonDocument.Parse(obResponseBody);
                var obRoot = obDoc.RootElement;
                string obStopReason = obRoot.GetProperty("stop_reason").GetString() ?? "";
                var obContentBlocks = obRoot.GetProperty("content");

                var obTextParts = new List<string>();
                var obToolCalls = new List<(string id, string name, JsonElement input)>();
                var obAssistantBlocks = new List<object>();

                foreach (var block in obContentBlocks.EnumerateArray())
                {
                    string bt = block.GetProperty("type").GetString() ?? "";
                    if (bt == "text")
                    {
                        string t = block.GetProperty("text").GetString() ?? "";
                        obTextParts.Add(t);
                        obAssistantBlocks.Add(new { type = "text", text = t });
                    }
                    else if (bt == "tool_use")
                    {
                        string tid = block.GetProperty("id").GetString() ?? "";
                        string tn = block.GetProperty("name").GetString() ?? "";
                        var ti = block.GetProperty("input").Clone();
                        obToolCalls.Add((tid, tn, ti));
                        obAssistantBlocks.Add(new { type = "tool_use", id = tid, name = tn, input = ti });
                    }
                }

                obAiText = string.Join("", obTextParts);

                if (obStopReason != "tool_use" || obToolCalls.Count == 0)
                    break;

                // Execute finalize_onboarding tool
                onboardMessages.Add(new { role = "assistant", content = obAssistantBlocks });
                var obToolResults = new List<object>();

                foreach (var (tid, tn, ti) in obToolCalls)
                {
                    if (tn == "finalize_onboarding")
                    {
                        string profileSummary = ti.TryGetProperty("profile_summary", out var ps) ? ps.GetString() ?? "" : "";
                        int daysPerWeek = ti.TryGetProperty("days_per_week", out var dpw2) ? dpw2.GetInt32() : 4;
                        string problemAreas = ti.TryGetProperty("problem_areas", out var pa) ? pa.GetString() ?? "" : "";
                        bool hasEquipment = ti.TryGetProperty("has_equipment", out var he) && he.GetBoolean();

                        // Save profile and move to step 2 (Generate/Customize choice)
                        prefs.ProfileNotes = profileSummary;
                        prefs.DaysPerWeek = daysPerWeek;
                        prefs.HasGym = hasEquipment;
                        prefs.OnboardingStep = 2;
                        await repository.SaveUserPreferencesAsync(prefs);

                        onboardingComplete = false; // Not done yet — still need to choose generate/customize
                        obToolResults.Add(new { type = "tool_result", tool_use_id = tid, content = $"Profile saved! Days/week: {daysPerWeek}. Problem areas: {problemAreas}. Now the app will show the user a choice to either generate their plan immediately or customize which exercises to include. Tell them something encouraging — the app will show buttons below your message." });
                    }
                    else
                    {
                        obToolResults.Add(new { type = "tool_result", tool_use_id = tid, content = "Unknown tool" });
                    }
                }
                onboardMessages.Add(new { role = "user", content = obToolResults });
            }

            // After AI questionnaire completes and sets step=2, return with plan_or_customize UI
            if (prefs.OnboardingStep == 2)
            {
                return Results.Json(new { success = true, response = obAiText, onboarding_step = 2, ui_component = new { type = "plan_or_customize", id = "plan_choice" } });
            }
            return Results.Json(new { success = true, response = obAiText, onboarding_step = onboardingComplete ? 7 : 1, onboarding_complete = onboardingComplete });
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
        sb2.AppendLine("When adding exercises, use get_all_exercises first to see what's available. If the item is already in the catalog, use add_exercise_to_plan or add_supplement. If it's NOT in the catalog, use create_custom_exercise or create_custom_supplement.");
        sb2.AppendLine();
        sb2.AppendLine("=== ADDING CUSTOM ITEMS TO PLAN ===");
        sb2.AppendLine("Users can ask to add exercises, recovery tools, or supplements not yet in the system. Follow this flow:");
        sb2.AppendLine("1. FIRST check if the item already exists using get_all_exercises or get_all_supplements.");
        sb2.AppendLine("2. If it exists, enable it with add_exercise_to_plan or add_supplement.");
        sb2.AppendLine("3. If it does NOT exist, confirm the CATEGORY with the user before creating. Suggest the right category and ask for confirmation.");
        sb2.AppendLine("   - For exercises: suggest warmup_tool, mobility, or recovery_tool. Say: 'I think [item] fits best under [Recovery/Warm-Up/Mobility]. Sound right?'");
        sb2.AppendLine("   - For supplements: ask about timing. Say: 'When do you take [supplement]? Morning, midday, or evening?'");
        sb2.AppendLine("4. After confirmation, use create_custom_exercise or create_custom_supplement to add it.");
        sb2.AppendLine("5. Confirm it was added and mention they can toggle it on/off in Settings.");
        sb2.AppendLine();
        sb2.AppendLine("=== CRITICAL SAFETY RULES FOR CUSTOM ITEMS ===");
        sb2.AppendLine("This is a JOINT HEALTH and MOBILITY program. You must REJECT requests to add items that don't belong. Be firm but friendly.");
        sb2.AppendLine();
        sb2.AppendLine("ALLOWED categories ONLY: warmup_tool, mobility, recovery_tool, or supplement.");
        sb2.AppendLine("NEVER create new categories. NEVER allow items outside joint health/mobility/recovery/wellness.");
        sb2.AppendLine();
        sb2.AppendLine("REJECT with a friendly explanation:");
        sb2.AppendLine("- Strength/gym exercises: bench press, deadlift, squats (barbell), pull-ups, bicep curls, shoulder press, etc. Say: 'This is a joint mobility program — we focus on keeping your joints happy, not bodybuilding! Try a dedicated gym app for strength training.'");
        sb2.AppendLine("- Cardio machines used for cardio (not recovery): running, sprinting, cycling for fitness. Exception: gentle warm-up versions (brisk walking, light cycling) ARE fine.");
        sb2.AppendLine("- Weapons, firearms, or anything dangerous: 'Nice try, but a gun won't help your joints! 😄'");
        sb2.AppendLine("- Illegal substances, recreational drugs: 'That's not in our supplement catalog and never will be! Stick to the legal stuff.'");
        sb2.AppendLine("- Food items, meals, snacks: 'We track supplements, not meals. Your joints appreciate the thought though!'");
        sb2.AppendLine("- Random objects or joke items: 'Ha! Creative, but [item] isn't going to help your hip flexors. 😄'");
        sb2.AppendLine("- Any item that could cause harm or injury");
        sb2.AppendLine();
        sb2.AppendLine("ALLOWED exercise examples: ice bath, cold plunge, sauna, infrared sauna, percussion massage, TENS unit, inversion table, resistance bands, foam roller, lacrosse ball, yoga, tai chi, pilates, swimming (recovery), stretching tools, balance board, wobble board, etc.");
        sb2.AppendLine("ALLOWED supplement examples: collagen, MSM, turmeric, omega-3, glucosamine, vitamin D, magnesium, hyaluronic acid, boswellia, CBD oil, tart cherry, bromelain, etc.");
        sb2.AppendLine();

        // Include user profile if available (from onboarding questionnaire)
        var userProfile = prefs?.ProfileNotes ?? "";
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            sb2.AppendLine("=== USER PROFILE (gathered during onboarding) ===");
            sb2.AppendLine(userProfile);
            sb2.AppendLine("Use this context to personalize your responses — reference their goals, problem areas, and situation when relevant.");
            sb2.AppendLine();
        }

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
                input_schema = new { type = "object", properties = new { days_per_week = new { type = "integer" } }, required = new[] { "days_per_week" } } },
            new { name = "create_custom_exercise", description = "Create a new exercise and add it to the user's plan. ONLY for joint health items: warm-up, mobility, or recovery tools. Categories must be warmup_tool, mobility, or recovery_tool. NEVER for strength/gym exercises.",
                input_schema = new { type = "object",
                    properties = new {
                        name = new { type = "string", description = "Display name (e.g. 'Ice Bath')" },
                        category = new { type = "string", description = "Must be: warmup_tool, mobility, or recovery_tool" },
                        targets = new { type = "string", description = "Body areas targeted (e.g. 'Full Body')" },
                        default_rx = new { type = "string", description = "Recommended prescription (e.g. '10 min', '30 sec each')" }
                    },
                    required = new[] { "name", "category", "targets", "default_rx" } } },
            new { name = "create_custom_supplement", description = "Create a new supplement and add it to the user's daily routine. ONLY for supplements related to joint health, recovery, or general wellness.",
                input_schema = new { type = "object",
                    properties = new {
                        name = new { type = "string", description = "Supplement name (e.g. 'Hyaluronic Acid')" },
                        time = new { type = "string", description = "When to take it (e.g. 'AM with food', 'Post-workout')" },
                        time_group = new { type = "string", description = "Must be: am, mid, or pm" }
                    },
                    required = new[] { "name", "time", "time_group" } } }
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
                    var toolInput = block.GetProperty("input").Clone();
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
                        case "create_custom_exercise":
                        {
                            string ceName = toolInput.GetProperty("name").GetString() ?? "";
                            string ceCat = toolInput.GetProperty("category").GetString() ?? "";
                            string ceTargets = toolInput.GetProperty("targets").GetString() ?? "";
                            string ceRx = toolInput.GetProperty("default_rx").GetString() ?? "";
                            // Validate category
                            var validCats = new HashSet<string> { "warmup_tool", "mobility", "recovery_tool" };
                            if (!validCats.Contains(ceCat))
                            {
                                toolResult = $"ERROR: Invalid category '{ceCat}'. Must be warmup_tool, mobility, or recovery_tool.";
                                break;
                            }
                            // Generate ID from name
                            string ceId = string.Join("-", ceName.ToLower().Trim().Split(new[] { ' ', '/', '\\', '(', ')', ',', '.', '!', '?', '&', '+', '—', '–' }, StringSplitOptions.RemoveEmptyEntries));
                            // Check if it already exists in catalog
                            var existingEx = allExercises2.FirstOrDefault(e => e.Id == ceId || e.Name.Equals(ceName, StringComparison.OrdinalIgnoreCase));
                            if (existingEx != null)
                            {
                                // Already exists — just toggle it on
                                var tPrefs = await repository.GetUserPreferencesAsync(userId);
                                if (tPrefs != null)
                                {
                                    var tIds = (tPrefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                                    if (!tIds.Contains(existingEx.Id))
                                    {
                                        tIds.Add(existingEx.Id);
                                        tPrefs.SelectedExercises = string.Join(",", tIds);
                                        await repository.SaveUserPreferencesAsync(tPrefs);
                                        await repository.RegenerateFuturePlanAsync(userId, tPrefs);
                                    }
                                }
                                toolResult = $"Found existing exercise '{existingEx.Name}' (id: {existingEx.Id}) and enabled it in the plan.";
                            }
                            else
                            {
                                await repository.CreateCustomExerciseAsync(userId, ceId, ceName, ceCat, ceTargets, ceRx);
                                toolResult = $"Created new exercise '{ceName}' (id: {ceId}, category: {ceCat}) and added to plan. It will appear in Settings and future workouts.";
                            }
                            break;
                        }
                        case "create_custom_supplement":
                        {
                            string csName = toolInput.GetProperty("name").GetString() ?? "";
                            string csTime = toolInput.GetProperty("time").GetString() ?? "";
                            string csTg = toolInput.GetProperty("time_group").GetString() ?? "am";
                            // Validate time_group
                            var validTgs = new HashSet<string> { "am", "mid", "pm" };
                            if (!validTgs.Contains(csTg)) csTg = "am";
                            // Generate ID from name
                            string csId = string.Join("-", csName.ToLower().Trim().Split(new[] { ' ', '/', '\\', '(', ')', ',', '.', '!', '?', '&', '+', '—', '–' }, StringSplitOptions.RemoveEmptyEntries));
                            // Check if it already exists
                            var existingSupp = supplements2.FirstOrDefault(s => s.Id == csId || s.Name.Equals(csName, StringComparison.OrdinalIgnoreCase));
                            if (existingSupp != null)
                            {
                                // Already exists — just toggle it on
                                var tPrefs = await repository.GetUserPreferencesAsync(userId);
                                if (tPrefs != null)
                                {
                                    var tIds = (tPrefs.SelectedSupplements ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                                    if (!tIds.Contains(existingSupp.Id))
                                    {
                                        tIds.Add(existingSupp.Id);
                                        tPrefs.SelectedSupplements = string.Join(",", tIds);
                                        await repository.SaveUserPreferencesAsync(tPrefs);
                                    }
                                    await repository.AddUserSupplementAsync(userId, existingSupp.Id, csTg, todayDate);
                                }
                                toolResult = $"Found existing supplement '{existingSupp.Name}' (id: {existingSupp.Id}) and enabled it.";
                            }
                            else
                            {
                                await repository.CreateCustomSupplementAsync(userId, csId, csName, "", csTime, csTg);
                                toolResult = $"Created new supplement '{csName}' (id: {csId}, timing: {csTime}) and added to daily routine. It will appear in Settings.";
                            }
                            break;
                        }
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

// ── Chat history persistence (session-scoped, in-memory) ──
app.MapGet("/api/ai/history", (HttpContext context, IMemoryCache cache) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();
    string userId = context.User.Identity?.Name ?? "default";
    string cacheKey = $"chat-history:{userId}";
    var history = cache.Get<List<Dictionary<string, string>>>(cacheKey) ?? new List<Dictionary<string, string>>();
    return Results.Json(new { success = true, history });
});

app.MapPost("/api/ai/history", async (HttpContext context, IMemoryCache cache) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();
    string userId = context.User.Identity?.Name ?? "default";
    string cacheKey = $"chat-history:{userId}";

    using var doc = await JsonDocument.ParseAsync(context.Request.Body);
    var root = doc.RootElement;

    var history = cache.Get<List<Dictionary<string, string>>>(cacheKey) ?? new List<Dictionary<string, string>>();

    if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
    {
        foreach (var msg in msgs.EnumerateArray())
        {
            string role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            string content = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(content))
                history.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = content });
        }
    }

    // Keep max 50 messages to avoid memory bloat
    if (history.Count > 50)
        history = history.Skip(history.Count - 50).ToList();

    cache.Set(cacheKey, history, TimeSpan.FromHours(8));
    return Results.Json(new { success = true, count = history.Count });
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
