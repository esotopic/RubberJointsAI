namespace RubberJointsAI.Models;

public class Exercise
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = ""; // warmup_tool, mobility, strength, recovery_tool
    public string Targets { get; set; } = "";   // comma-separated
    public string Description { get; set; } = "";
    public string Cues { get; set; } = "";       // pipe-separated
    public string Explanation { get; set; } = "";
    public string? Warning { get; set; }
    public string Phases { get; set; } = "1,2";  // comma-separated phase numbers
    public string? DefaultRx { get; set; }         // e.g., "5 min", "30 sec each"
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string CreatedDate { get; set; } = "";
}

public class Supplement
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Dose { get; set; } = "";
    public string Time { get; set; } = "";
    public string TimeGroup { get; set; } = ""; // am, mid, pm
}

public class SessionStep
{
    public int Id { get; set; }
    public string DayType { get; set; } = "";   // gym, home, recovery, rest
    public string ExerciseId { get; set; } = "";
    public string? Phase1Rx { get; set; }
    public string? Phase2Rx { get; set; }
    public int? PhaseOnly { get; set; }          // null=both, 1=phase1 only, 2=phase2 only
    public string? Section { get; set; }
    public int SortOrder { get; set; }
}

public class DailyCheck
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default";
    public string Date { get; set; } = "";       // yyyy-MM-dd
    public string ItemType { get; set; } = "";   // step, supplement
    public string ItemId { get; set; } = "";
    public int? StepIndex { get; set; }
    public bool Checked { get; set; }
}

public class Milestone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Done { get; set; }
    public string? AchievedDate { get; set; }
}

public class SessionLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default";
    public string Date { get; set; } = "";
    public int StepsDone { get; set; }
    public int StepsTotal { get; set; }
}

public class UserSettings
{
    public string UserId { get; set; } = "default";
    public string? StartDate { get; set; }
    public string DisabledTools { get; set; } = ""; // comma-separated exercise IDs
}

// View models
public class TodayViewModel
{
    public int Week { get; set; } = 1;
    public int Phase { get; set; } = 1;
    public string DayName { get; set; } = "";
    public string SessionType { get; set; } = "";
    public string DayKey { get; set; } = "";
    public int EstMinutes { get; set; }
    public List<TodayStep> Steps { get; set; } = new();
    public List<SupplementCheck> Supplements { get; set; } = new();
    public string? ErrorMessage { get; set; }

    // Day navigation
    public string SelectedDate { get; set; } = "";       // yyyy-MM-dd being viewed
    public string TodayDate { get; set; } = "";           // actual today yyyy-MM-dd
    public string SelectedDateLabel { get; set; } = "";   // "Friday, Mar 27"
    public string PrevDate { get; set; } = "";            // yyyy-MM-dd for < arrow
    public string NextDate { get; set; } = "";            // yyyy-MM-dd for > arrow
    public bool IsFuture { get; set; }                    // true = read-only, no checkboxes
    public bool IsToday { get; set; } = true;
}

public class TodayStep
{
    public int Index { get; set; }
    public int SessionStepId { get; set; }  // The DB primary key for SessionSteps
    public Exercise Exercise { get; set; } = new();
    public string Rx { get; set; } = "";
    public string? Section { get; set; }
    public bool Checked { get; set; }
}

public class SupplementCheck
{
    public Supplement Supplement { get; set; } = new();
    public bool Checked { get; set; }
}

public class ProgressViewModel
{
    public int Week { get; set; }
    public int Phase { get; set; }
    public int SessionsThisWeek { get; set; }
    public int SessionsTotal { get; set; }
    public int TodayStepsDone { get; set; }
    public int TodayStepsTotal { get; set; }
    public int TodaySuppsDone { get; set; }
    public int TodaySuppsTotal { get; set; }
    public List<Milestone> Milestones { get; set; } = new();
    public bool TodayLogged { get; set; }
    public string? ErrorMessage { get; set; }
}

// ── Program / Enrollment / Daily Plan models ──

public class TrainingProgram
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DurationDays { get; set; }
    public string Description { get; set; } = "";
}

public class ProgramTemplate
{
    public int Id { get; set; }
    public int ProgramId { get; set; }
    public int DayNumber { get; set; }          // 1-28 for a 4-week program
    public string DayType { get; set; } = "";   // gym, home, recovery, rest
    public string ExerciseId { get; set; } = "";
    public string Category { get; set; } = "";
    public int SortOrder { get; set; }
    public string? Rx { get; set; }
}

public class UserEnrollment
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ProgramId { get; set; }
    public string StartDate { get; set; } = "";     // yyyy-MM-dd
    public string Status { get; set; } = "active";  // active, completed, paused
    public string? ProgramName { get; set; }         // joined from Programs
}

public class UserDailyPlanEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ProgramId { get; set; }
    public string Date { get; set; } = "";          // yyyy-MM-dd
    public string DayType { get; set; } = "";       // gym, home, recovery, rest
    public string ExerciseId { get; set; } = "";
    public string Category { get; set; } = "";
    public int SortOrder { get; set; }
    public string? Rx { get; set; }
    public bool AiAdjusted { get; set; }
    public bool IsManual { get; set; }
}
