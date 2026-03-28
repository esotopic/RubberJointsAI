using Microsoft.Data.SqlClient;
using RubberJointsAI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RubberJointsAI.Data
{
    public class RubberJointsAIRepository
    {
        private readonly string _connectionString;

        public RubberJointsAIRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Creates all required tables if they don't exist and seeds initial data.
        /// </summary>
        public async Task EnsureTablesExistAsync()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                // Create Users table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users')
                        BEGIN
                            CREATE TABLE Users (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                Username NVARCHAR(100) NOT NULL UNIQUE,
                                PasswordHash NVARCHAR(500) NOT NULL,
                                Salt NVARCHAR(500) NOT NULL,
                                CreatedDate NVARCHAR(10) NOT NULL
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create Exercises table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Exercises')
                        BEGIN
                            CREATE TABLE Exercises (
                                Id NVARCHAR(50) PRIMARY KEY,
                                Name NVARCHAR(255) NOT NULL,
                                Category NVARCHAR(50) NOT NULL,
                                Targets NVARCHAR(500) NOT NULL,
                                Description NVARCHAR(MAX),
                                Cues NVARCHAR(MAX),
                                Explanation NVARCHAR(MAX),
                                Warning NVARCHAR(MAX),
                                Phases NVARCHAR(50),
                                DefaultRx NVARCHAR(255)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Add DefaultRx column if missing (upgrade path)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Exercises' AND COLUMN_NAME='DefaultRx')
                            ALTER TABLE Exercises ADD DefaultRx NVARCHAR(255)";
                    await command.ExecuteNonQueryAsync();
                }

                // Create Supplements table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Supplements')
                        BEGIN
                            CREATE TABLE Supplements (
                                Id NVARCHAR(50) PRIMARY KEY,
                                Name NVARCHAR(255) NOT NULL,
                                Dose NVARCHAR(255),
                                Time NVARCHAR(255),
                                TimeGroup NVARCHAR(50)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create SessionSteps table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SessionSteps')
                        BEGIN
                            CREATE TABLE SessionSteps (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                DayType NVARCHAR(50) NOT NULL,
                                ExerciseId NVARCHAR(50) NOT NULL,
                                Phase1Rx NVARCHAR(255),
                                Phase2Rx NVARCHAR(255),
                                PhaseOnly INT NULL,
                                Section NVARCHAR(50),
                                SortOrder INT,
                                FOREIGN KEY (ExerciseId) REFERENCES Exercises(Id)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create DailyChecks table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'DailyChecks')
                        BEGIN
                            CREATE TABLE DailyChecks (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                UserId NVARCHAR(50) NOT NULL,
                                Date NVARCHAR(10) NOT NULL,
                                ItemType NVARCHAR(50) NOT NULL,
                                ItemId NVARCHAR(50) NOT NULL,
                                StepIndex INT,
                                Checked BIT DEFAULT 0
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create Milestones table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Milestones')
                        BEGIN
                            CREATE TABLE Milestones (
                                Id NVARCHAR(50) PRIMARY KEY,
                                Name NVARCHAR(255) NOT NULL,
                                Done BIT DEFAULT 0,
                                AchievedDate NVARCHAR(10)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create SessionLogs table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SessionLogs')
                        BEGIN
                            CREATE TABLE SessionLogs (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                UserId NVARCHAR(50) NOT NULL,
                                Date NVARCHAR(10) NOT NULL,
                                StepsDone INT,
                                StepsTotal INT
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserSettings table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserSettings')
                        BEGIN
                            CREATE TABLE UserSettings (
                                UserId NVARCHAR(50) PRIMARY KEY,
                                StartDate NVARCHAR(10),
                                DisabledTools NVARCHAR(MAX)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserMilestones table (per-user milestone tracking)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserMilestones')
                        BEGIN
                            CREATE TABLE UserMilestones (
                                UserId NVARCHAR(100) NOT NULL,
                                MilestoneId NVARCHAR(50) NOT NULL,
                                Done BIT DEFAULT 0,
                                AchievedDate NVARCHAR(10),
                                PRIMARY KEY (UserId, MilestoneId)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create Programs table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Programs')
                        BEGIN
                            CREATE TABLE Programs (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                Name NVARCHAR(255) NOT NULL,
                                DurationDays INT NOT NULL,
                                Description NVARCHAR(MAX)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create ProgramTemplate table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ProgramTemplate')
                        BEGIN
                            CREATE TABLE ProgramTemplate (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                ProgramId INT NOT NULL,
                                DayNumber INT NOT NULL,
                                DayType NVARCHAR(50) NOT NULL,
                                ExerciseId NVARCHAR(50) NOT NULL,
                                Category NVARCHAR(50) NOT NULL,
                                SortOrder INT NOT NULL,
                                Rx NVARCHAR(255),
                                FOREIGN KEY (ProgramId) REFERENCES Programs(Id),
                                FOREIGN KEY (ExerciseId) REFERENCES Exercises(Id)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserEnrollments table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserEnrollments')
                        BEGIN
                            CREATE TABLE UserEnrollments (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                UserId NVARCHAR(100) NOT NULL,
                                ProgramId INT NOT NULL,
                                StartDate NVARCHAR(10) NOT NULL,
                                Status NVARCHAR(20) NOT NULL DEFAULT 'active',
                                FOREIGN KEY (ProgramId) REFERENCES Programs(Id)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserDailyPlan table
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserDailyPlan')
                        BEGIN
                            CREATE TABLE UserDailyPlan (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                UserId NVARCHAR(100) NOT NULL,
                                ProgramId INT NOT NULL,
                                Date NVARCHAR(10) NOT NULL,
                                DayType NVARCHAR(50) NOT NULL,
                                ExerciseId NVARCHAR(50) NOT NULL,
                                Category NVARCHAR(50) NOT NULL,
                                SortOrder INT NOT NULL,
                                Rx NVARCHAR(255),
                                AiAdjusted BIT DEFAULT 0,
                                IsManual BIT DEFAULT 0,
                                FOREIGN KEY (ProgramId) REFERENCES Programs(Id),
                                FOREIGN KEY (ExerciseId) REFERENCES Exercises(Id)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Add IsManual column if missing (upgrade path)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='UserDailyPlan' AND COLUMN_NAME='IsManual')
                            ALTER TABLE UserDailyPlan ADD IsManual BIT DEFAULT 0";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserSupplements table (per-user active supplement tracking)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserSupplements')
                        BEGIN
                            CREATE TABLE UserSupplements (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                UserId NVARCHAR(100) NOT NULL,
                                SupplementId NVARCHAR(50) NOT NULL,
                                TimeGroup NVARCHAR(50) NOT NULL DEFAULT 'am',
                                AddedDate NVARCHAR(10) NOT NULL,
                                FOREIGN KEY (SupplementId) REFERENCES Supplements(Id),
                                UNIQUE (UserId, SupplementId, TimeGroup)
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Add TimeGroup column to UserSupplements if missing (upgrade path)
                // Step 1: Add column
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='UserSupplements' AND COLUMN_NAME='TimeGroup')
                            ALTER TABLE UserSupplements ADD TimeGroup NVARCHAR(50) NOT NULL CONSTRAINT DF_US_TimeGroup DEFAULT 'am'";
                    await command.ExecuteNonQueryAsync();
                }
                // Step 2: Drop old unique constraint if it doesn't include TimeGroup
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        DECLARE @cname NVARCHAR(255);
                        SELECT @cname = CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                        WHERE TABLE_NAME='UserSupplements' AND CONSTRAINT_TYPE='UNIQUE';
                        IF @cname IS NOT NULL
                        BEGIN
                            -- Check if existing unique constraint has TimeGroup
                            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE
                                           WHERE TABLE_NAME='UserSupplements' AND CONSTRAINT_NAME=@cname AND COLUMN_NAME='TimeGroup')
                            BEGIN
                                EXEC('ALTER TABLE UserSupplements DROP CONSTRAINT [' + @cname + ']');
                                ALTER TABLE UserSupplements ADD CONSTRAINT UQ_UserSupplements_User_Supp_TG UNIQUE (UserId, SupplementId, TimeGroup);
                            END
                        END";
                    await command.ExecuteNonQueryAsync();
                }
                // Step 3: Update existing rows to use the supplement's default TimeGroup
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        UPDATE us SET us.TimeGroup = s.TimeGroup
                        FROM UserSupplements us JOIN Supplements s ON us.SupplementId = s.Id
                        WHERE us.TimeGroup = 'am' AND s.TimeGroup <> 'am'";
                    await command.ExecuteNonQueryAsync();
                }

                // Create UserPreferences table (onboarding + equipment selections)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserPreferences')
                        BEGIN
                            CREATE TABLE UserPreferences (
                                UserId NVARCHAR(100) PRIMARY KEY,
                                HasGym BIT DEFAULT 0,
                                DaysPerWeek INT DEFAULT 3,
                                OnboardingStep INT DEFAULT 0,
                                SelectedExercises NVARCHAR(MAX) DEFAULT '',
                                SelectedSupplements NVARCHAR(MAX) DEFAULT ''
                            )
                        END";
                    await command.ExecuteNonQueryAsync();
                }

                // Seed Exercises
                await SeedExercisesAsync(connection);

                // Seed Supplements
                await SeedSupplementsAsync(connection);

                // Seed Milestones
                await SeedMilestonesAsync(connection);

                // Seed SessionSteps
                await SeedSessionStepsAsync(connection);

                // Seed Programs
                await SeedProgramsAsync(connection);
            }
        }

        public async Task InitializeAsync()
        {
            await EnsureTablesExistAsync();
            await RunMigrationsAsync();
        }

        private async Task RunMigrationsAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            // Rename program from "RubberJointsAI" to "4-Week Joint & Mobility Program"
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"UPDATE Programs SET Name = '4-Week Joint & Mobility Program',
                    Description = 'A hilariously serious program to get your joints moving like they should.'
                    WHERE Name = 'RubberJointsAI'";
                await cmd.ExecuteNonQueryAsync();
            }

            // Remove strength exercises — this is a joint/mobility program, not strength training
            using (var cmd2 = connection.CreateCommand())
            {
                cmd2.CommandText = @"DELETE FROM Exercises WHERE Category = 'strength';
                    DELETE FROM ProgramExercises WHERE Category = 'strength';
                    DELETE FROM UserDailyPlan WHERE Category = 'strength';";
                await cmd2.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Emergency method to add TimeGroup column if migration failed at startup.
        /// </summary>
        public async Task AddTimeGroupColumnAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='UserSupplements' AND COLUMN_NAME='TimeGroup')
                    ALTER TABLE UserSupplements ADD TimeGroup NVARCHAR(50) NOT NULL CONSTRAINT DF_US_TimeGroup DEFAULT 'am'";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task SeedExercisesAsync(SqlConnection connection)
        {
            var exercises = new List<(string id, string name, string category, string targets, string description, string cues, string explanation, string warning, string phases, string defaultRx)>
            {
                ("hot-tub", "Hot Tub", "warmup_tool", "Full Body", "Warm up in hot water to increase circulation", "2-5 min | No excessive heat", "Heat increases tissue elasticity and blood flow", "Avoid if pregnant or have heart conditions", "1,2", "5 min"),
                ("vibration-plate", "Vibration Plate", "warmup_tool", "Full Body", "Stand on vibration plate for neuromuscular activation", "Feet hip-width | Slight knee bend | 30-60 sec", "Vibration activates muscles and improves proprioception", "Not for acute injuries or pregnancy", "1,2", "1 min"),
                ("cars-routine", "CARs Routine", "mobility", "All Joints", "Controlled Articular Rotations for joint mobility", "Slow circles | Full range | No momentum", "CARs improve joint health and body awareness", "Move within pain-free range only", "1,2", "5 min"),
                ("90-90-hip-switch", "90/90 Hip Switch", "mobility", "Hips", "Switch legs in 90/90 position for hip mobility", "Chest upright | Slow switch | 30 sec each side", "Improves hip external and internal rotation", "Stop if sharp pain occurs", "1,2", "30 sec each"),
                ("shinbox-getup", "Shinbox Get-Up", "mobility", "Hips,Core", "Get up from shinbox position without using hands", "Controlled movement | No hand assist | 5 reps", "Builds hip mobility and core stability", "Requires significant hip mobility", "2", "5 reps"),
                ("worlds-greatest-stretch", "World's Greatest Stretch", "mobility", "Hips,T-Spine,Ankles", "Dynamic stretch targeting multiple areas", "Lunge | Rotate | Reach | 8-10 reps each side", "Comprehensive dynamic mobility for warm-up", "Avoid with acute injuries", "1,2", "8 reps each"),
                ("deep-squat-hold", "Deep Squat Hold", "mobility", "Hips,Knees,Ankles", "Hold bottom of squat position", "Feet shoulder-width | Upright torso | Hold 30-60 sec", "Improves squat mechanics and ankle mobility", "Use support if needed for balance", "1,2", "60 sec"),
                ("couch-stretch", "Couch Stretch", "mobility", "Hips,Quads", "Quad stretch on couch or box", "Back knee elevated | Gentle forward lean | 90 sec each", "Improves hip and quad flexibility", "Can be intense; progress gradually", "1,2", "90 sec each"),
                ("wall-ankle-mob", "Wall Ankle Mobilization", "mobility", "Ankles", "Mobilize ankle with wall for dorsiflexion", "Shin against wall | Lean forward | 90 sec each", "Improves ankle dorsiflexion and calf mobility", "Stop if pain in ankle", "1,2", "90 sec each"),
                ("open-book", "Open Book (T-Spine)", "mobility", "T-Spine", "Thoracic spine rotation from side-lying", "Side-lying | Controlled rotation | 10 reps each side", "Improves thoracic rotation and spinal mobility", "Avoid jerky movements", "1,2", "10 reps each"),
                ("dead-hang", "Dead Hang", "mobility", "Shoulders,Spine,Grip", "Hang from bar with full body relaxed", "Full grip | Shoulders engaged | 20-60 sec", "Decompresses spine and improves shoulder mobility", "Build up duration gradually", "1,2", "30 sec"),
                ("quadruped-rocking", "Quadruped Rocking", "mobility", "Hips,Ankles", "Rock back and forth on hands and knees", "Hands under shoulders | Slow rocks | 20 reps", "Improves hip and ankle mobility", "Keep core engaged", "1,2", "20 reps"),
                ("hip-flexor-pails-rails", "Hip Flexor PAILs/RAILs", "mobility", "Hips", "Proprioceptive stretching for hip flexors", "Hold position | Contract | Relax | 8-10 reps", "Increases hip flexor mobility and stability", "Requires space and understanding of PAILs/RAILs", "2", "8 reps"),
                ("90-90-pails-rails", "90/90 PAILs/RAILs", "mobility", "Hips", "Proprioceptive stretching in 90/90 position", "Hold | Contract | Relax | 8-10 reps each side", "Improves hip external rotation", "Advanced technique; build foundation first", "2", "8 reps each"),
                ("ankle-pails-rails", "Ankle PAILs/RAILs", "mobility", "Ankles", "Proprioceptive stretching for ankle mobility", "Hold position | Contract | Relax | 8-10 reps", "Improves ankle dorsiflexion and control", "Requires proprioceptive understanding", "2", "8 reps"),
                ("hydro-massager", "Hydro Massager", "recovery_tool", "Full Body", "Use hydro massager for muscle recovery", "Various speeds | Target muscles | 5-10 min", "Increases circulation and aids recovery", "Avoid over-sensitive areas", "1,2", "5 min"),
                ("steam-sauna", "Steam Sauna", "recovery_tool", "Full Body", "Relax in steam sauna for recovery", "10-20 min | Stay hydrated | Moderate temperature", "Promotes relaxation and circulation", "Avoid if pregnant or have heart conditions", "1,2", "15 min"),
                ("dry-sauna", "Dry Sauna", "recovery_tool", "Full Body", "Relax in dry sauna for muscle recovery", "10-20 min | Stay hydrated | Moderate temperature", "Reduces muscle soreness and promotes recovery", "Stay well hydrated", "1,2", "15 min"),
                ("compex-warmup", "Compex — Warmup", "recovery_tool", "Quads,Glutes", "Use Compex muscle stimulator for warm-up", "Warmup setting | 10-15 min | Quads and glutes", "Prepares muscles for training", "Follow device instructions", "1,2", "10 min"),
                ("compex-recovery", "Compex — Recovery", "recovery_tool", "Quads,Glutes,Calves", "Use Compex for post-workout recovery", "Recovery setting | 15-20 min | Multiple muscles", "Aids muscle recovery and reduces soreness", "Follow device instructions", "1,2", "15 min"),
                ("compression-boots", "Compression Boots", "recovery_tool", "Legs", "Use compression boots for leg recovery", "15-30 min | Moderate compression | Legs only", "Improves circulation and reduces leg soreness", "Start with shorter durations", "1,2", "20 min"),
                // Home-friendly recovery tools
                ("foam-roller", "Foam Roller", "recovery_tool", "Full Body", "Roll out tight muscles for myofascial release", "Slow rolls | 30-60 sec per area | Pause on tender spots", "Breaks up adhesions and improves tissue quality", "Avoid rolling directly on joints or spine", "1,2", "10 min"),
                ("lacrosse-ball", "Lacrosse Ball Release", "recovery_tool", "Shoulders,Hips,Feet", "Targeted deep tissue release with a lacrosse ball", "Pin and move | 30-60 sec per spot | Breathe through it", "Reaches deeper tissue than foam roller", "Avoid bony areas and nerves", "1,2", "8 min"),
                ("contrast-shower", "Contrast Shower", "recovery_tool", "Full Body", "Alternate hot and cold water for circulation boost", "3 min hot | 1 min cold | Repeat 3x | End on cold", "Promotes blood flow and reduces inflammation", "Start gradually if new to cold exposure", "1,2", "12 min"),
                ("yoga-cooldown", "Yoga Cool-Down Flow", "recovery_tool", "Full Body", "Gentle yoga sequence for recovery and relaxation", "Child's pose | Pigeon | Supine twist | Savasana | 5 breaths each", "Calms nervous system and promotes recovery", "Move gently; never force a stretch", "1,2", "15 min"),
                ("self-massage", "Self-Massage (Hands/Stick)", "recovery_tool", "Calves,Forearms,Neck", "Manual massage using hands or a massage stick", "Slow strokes | Moderate pressure | 2-3 min per area", "Improves circulation and reduces muscle tension", "Avoid inflamed or injured areas", "1,2", "10 min"),
                ("epsom-bath", "Epsom Salt Bath", "recovery_tool", "Full Body", "Soak in warm water with Epsom salts for recovery", "2 cups Epsom salt | Warm water | 15-20 min soak", "Magnesium absorption helps muscle relaxation", "Stay hydrated; avoid if you have low blood pressure", "1,2", "20 min")
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    MERGE INTO Exercises AS target
                    USING (VALUES
                        (@id, @name, @category, @targets, @description, @cues, @explanation, @warning, @phases, @defaultRx)
                    ) AS source (Id, Name, Category, Targets, Description, Cues, Explanation, Warning, Phases, DefaultRx)
                    ON target.Id = source.Id
                    WHEN MATCHED THEN
                        UPDATE SET DefaultRx = source.DefaultRx
                    WHEN NOT MATCHED THEN
                        INSERT (Id, Name, Category, Targets, Description, Cues, Explanation, Warning, Phases, DefaultRx)
                        VALUES (source.Id, source.Name, source.Category, source.Targets, source.Description, source.Cues, source.Explanation, source.Warning, source.Phases, source.DefaultRx);";

                foreach (var exercise in exercises)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@id", exercise.id);
                    command.Parameters.AddWithValue("@name", exercise.name);
                    command.Parameters.AddWithValue("@category", exercise.category);
                    command.Parameters.AddWithValue("@targets", exercise.targets);
                    command.Parameters.AddWithValue("@description", exercise.description ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@cues", exercise.cues ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@explanation", exercise.explanation ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@warning", exercise.warning ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@phases", exercise.phases);
                    command.Parameters.AddWithValue("@defaultRx", exercise.defaultRx ?? (object)DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task SeedSupplementsAsync(SqlConnection connection)
        {
            var supplements = new List<(string id, string name, string dose, string time, string timeGroup)>
            {
                ("supp-collagen", "Collagen + Vitamin C", "10-15g + 50mg C", "AM / Pre-workout", "am"),
                ("supp-omega3", "Omega-3 Fish Oil", "~1500mg EPA+DHA", "AM with food", "am"),
                ("supp-vitamind", "Vitamin D3 + K2", "2000-4000 IU + 100mcg K2", "AM with food", "am"),
                ("supp-creatine", "Creatine Monohydrate", "3-5g", "AM", "am"),
                ("supp-curcumin", "Curcumin (w/ piperine)", "500-1500mg", "With lunch", "mid"),
                ("supp-omega3b", "Omega-3 (2nd dose)", "~1500mg EPA+DHA", "PM with dinner", "pm"),
                ("supp-mag", "Magnesium Glycinate", "300-400mg", "Before bed", "pm"),
                // Additional supplement options
                ("supp-zinc", "Zinc", "15-30mg", "With food", "am"),
                ("supp-ashwagandha", "Ashwagandha", "300-600mg", "AM or PM", "am"),
                ("supp-glucosamine", "Glucosamine + Chondroitin", "1500mg + 1200mg", "With food", "am"),
                ("supp-bcomplex", "B-Complex", "1 capsule", "AM with food", "am"),
                ("supp-probiotics", "Probiotics", "10-50 billion CFU", "AM empty stomach", "am"),
                ("supp-coq10", "CoQ10", "100-200mg", "With food", "am"),
                ("supp-glutamine", "L-Glutamine", "5g", "Post-workout", "mid"),
                ("supp-electrolytes", "Electrolytes", "1 packet", "During workout", "mid"),
                ("supp-melatonin", "Melatonin", "0.5-3mg", "30 min before bed", "pm"),
                ("supp-tart-cherry", "Tart Cherry Extract", "500mg", "Before bed", "pm"),
                ("supp-iron", "Iron", "18-27mg", "AM empty stomach", "am"),
                ("supp-vitaminc", "Vitamin C", "500-1000mg", "AM with food", "am")
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    MERGE INTO Supplements AS target
                    USING (VALUES
                        (@id, @name, @dose, @time, @timeGroup)
                    ) AS source (Id, Name, Dose, Time, TimeGroup)
                    ON target.Id = source.Id
                    WHEN NOT MATCHED THEN
                        INSERT (Id, Name, Dose, Time, TimeGroup)
                        VALUES (source.Id, source.Name, source.Dose, source.Time, source.TimeGroup);";

                foreach (var supplement in supplements)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@id", supplement.id);
                    command.Parameters.AddWithValue("@name", supplement.name);
                    command.Parameters.AddWithValue("@dose", supplement.dose ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@time", supplement.time ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@timeGroup", supplement.timeGroup);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task SeedMilestonesAsync(SqlConnection connection)
        {
            var milestones = new List<(string id, string name)>
            {
                ("kneel", "Kneel without discomfort"),
                ("squat60", "Deep squat hold — 60 sec"),
                ("squat120", "Deep squat hold — 2 min"),
                ("hang30", "Dead hang — 30 sec"),
                ("hang60", "Dead hang — 60 sec"),
                ("shinbox", "Shinbox get-up without hands"),
                ("tgu-kb", "Turkish get-up with KB"),
                ("cossack-floor", "Cossack squat — touch floor"),
                ("floor-nohand", "Floor to standing — no hands")
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    MERGE INTO Milestones AS target
                    USING (VALUES
                        (@id, @name)
                    ) AS source (Id, Name)
                    ON target.Id = source.Id
                    WHEN NOT MATCHED THEN
                        INSERT (Id, Name, Done)
                        VALUES (source.Id, source.Name, 0);";

                foreach (var milestone in milestones)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@id", milestone.id);
                    command.Parameters.AddWithValue("@name", milestone.name);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task SeedSessionStepsAsync(SqlConnection connection)
        {
            var steps = new List<(string dayType, string exerciseId, string phase1Rx, string phase2Rx, int? phaseOnly, string section, int sortOrder)>
            {
                // GYM sessions
                ("gym", "hot-tub", "5 min", "5 min", null, "warmup", 1),
                ("gym", "vibration-plate", "1 min", "1 min", null, "warmup", 2),
                ("gym", "cars-routine", "10 min", "10 min", null, "warmup", 3),
                ("gym", "deep-squat-hold", "60 sec", "90 sec", null, "mobility", 4),
                ("gym", "dead-hang", "30 sec x3", "60 sec x3", null, "mobility", 5),
                ("gym", "worlds-greatest-stretch", "8 reps each", "10 reps each", null, "mobility", 6),
                ("gym", "hydro-massager", "5 min", "5 min", null, "recovery", 7),
                ("gym", "compex-recovery", "15 min", "15 min", null, "recovery", 8),

                // HOME sessions
                ("home", "cars-routine", "5 min", "5 min", null, "mobility", 1),
                ("home", "90-90-hip-switch", "30 sec each", "30 sec each", null, "mobility", 2),
                ("home", "couch-stretch", "90 sec each", "90 sec each", null, "mobility", 3),
                ("home", "wall-ankle-mob", "90 sec each", "90 sec each", null, "mobility", 4),
                ("home", "open-book", "10 reps each", "10 reps each", null, "mobility", 5),
                ("home", "quadruped-rocking", "20 reps", "20 reps", null, "mobility", 6),
                ("home", "90-90-pails-rails", null, "8 reps each", 2, "mobility", 7),
                ("home", "hip-flexor-pails-rails", null, "8 reps", 2, "mobility", 8),
                ("home", "ankle-pails-rails", null, "8 reps", 2, "mobility", 9),

                // RECOVERY sessions
                ("recovery", "steam-sauna", "15 min", "15 min", null, "recovery", 1),
                ("recovery", "dry-sauna", "15 min", "15 min", null, "recovery", 2),
                ("recovery", "compression-boots", "20 min", "20 min", null, "recovery", 3),
                ("recovery", "hydro-massager", "10 min", "10 min", null, "recovery", 4),
                ("recovery", "compex-warmup", "10 min", "10 min", null, "recovery", 5),

                // REST sessions (typically just supplements and light mobility)
                ("rest", "cars-routine", "5 min", "5 min", null, "mobility", 1),
                ("rest", "dead-hang", "20 sec", "20 sec", null, "mobility", 2),
                ("rest", "deep-squat-hold", "30 sec", "30 sec", null, "mobility", 3)
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    IF NOT EXISTS (SELECT 1 FROM SessionSteps)
                    BEGIN
                        INSERT INTO SessionSteps (DayType, ExerciseId, Phase1Rx, Phase2Rx, PhaseOnly, Section, SortOrder)
                        VALUES (@dayType, @exerciseId, @phase1Rx, @phase2Rx, @phaseOnly, @section, @sortOrder)
                    END";

                foreach (var step in steps)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@dayType", step.dayType);
                    command.Parameters.AddWithValue("@exerciseId", step.exerciseId);
                    command.Parameters.AddWithValue("@phase1Rx", step.phase1Rx ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@phase2Rx", step.phase2Rx ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@phaseOnly", step.phaseOnly ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@section", step.section ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@sortOrder", step.sortOrder);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task SeedProgramsAsync(SqlConnection connection)
        {
            // Check if Programs table already has data
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM Programs";
                var count = (int)await command.ExecuteScalarAsync();
                if (count > 0)
                    return; // Already seeded
            }

            // Insert the RubberJointsAI program
            int programId;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO Programs (Name, DurationDays, Description)
                    OUTPUT INSERTED.Id
                    VALUES ('4-Week Joint & Mobility Program', 28, 'A hilariously serious program to get your joints moving like they should.')";
                programId = (int)await command.ExecuteScalarAsync();
            }

            // Build the 28-day program template
            // Weekly pattern repeats 4 times:
            // Day 1 (Mon) = gym, Day 2 (Tue) = home, Day 3 (Wed) = gym,
            // Day 4 (Thu) = home, Day 5 (Fri) = gym, Day 6 (Sat) = recovery, Day 7 (Sun) = rest

            var dayTypes = new[] { "gym", "home", "gym", "home", "gym", "recovery", "rest" };
            var programTemplate = new List<(int dayNumber, string dayType, string exerciseId, string category, int sortOrder, string rx)>();

            // Build template for 28 days (4 weeks)
            for (int week = 0; week < 4; week++)
            {
                for (int dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++)
                {
                    int dayNumber = week * 7 + dayOfWeek + 1;
                    string dayType = dayTypes[dayOfWeek];

                    // Get exercises for this day type from SessionSteps data
                    var exercises = GetExercisesForDayType(dayType);
                    foreach (var (exerciseId, category, sortOrder, rx) in exercises)
                    {
                        programTemplate.Add((dayNumber, dayType, exerciseId, category, sortOrder, rx));
                    }
                }
            }

            // Insert all template entries
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO ProgramTemplate (ProgramId, DayNumber, DayType, ExerciseId, Category, SortOrder, Rx)
                    VALUES (@programId, @dayNumber, @dayType, @exerciseId, @category, @sortOrder, @rx)";

                foreach (var template in programTemplate)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@programId", programId);
                    command.Parameters.AddWithValue("@dayNumber", template.dayNumber);
                    command.Parameters.AddWithValue("@dayType", template.dayType);
                    command.Parameters.AddWithValue("@exerciseId", template.exerciseId);
                    command.Parameters.AddWithValue("@category", template.category);
                    command.Parameters.AddWithValue("@sortOrder", template.sortOrder);
                    command.Parameters.AddWithValue("@rx", (object?)template.rx ?? DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Gets exercises for a specific day type, filtering out those without Phase1Rx
        /// </summary>
        private List<(string exerciseId, string category, int sortOrder, string rx)> GetExercisesForDayType(string dayType)
        {
            var exercises = new List<(string exerciseId, string category, int sortOrder, string rx)>();

            // Gym exercises
            if (dayType == "gym")
            {
                exercises.Add(("hot-tub", "warmup_tool", 1, "5 min"));
                exercises.Add(("vibration-plate", "warmup_tool", 2, "1 min"));
                exercises.Add(("cars-routine", "mobility", 3, "10 min"));
                exercises.Add(("deep-squat-hold", "mobility", 4, "60 sec"));
                exercises.Add(("dead-hang", "mobility", 5, "30 sec x3"));
                exercises.Add(("worlds-greatest-stretch", "mobility", 6, "8 reps each"));
                // Skip goblet-squat (no Phase1Rx)
                // Skip turkish-getup (no Phase1Rx)
                exercises.Add(("hydro-massager", "recovery_tool", 9, "5 min"));
                exercises.Add(("compex-recovery", "recovery_tool", 10, "15 min"));
            }
            // Home exercises
            else if (dayType == "home")
            {
                exercises.Add(("cars-routine", "mobility", 1, "5 min"));
                exercises.Add(("90-90-hip-switch", "mobility", 2, "30 sec each"));
                exercises.Add(("couch-stretch", "mobility", 3, "90 sec each"));
                exercises.Add(("wall-ankle-mob", "mobility", 4, "90 sec each"));
                exercises.Add(("open-book", "mobility", 5, "10 reps each"));
                exercises.Add(("quadruped-rocking", "mobility", 6, "20 reps"));
                // Skip 90-90-pails-rails (no Phase1Rx)
                // Skip hip-flexor-pails-rails (no Phase1Rx)
                // Skip ankle-pails-rails (no Phase1Rx)
                // Skip cossack-squat (no Phase1Rx)
                // Skip jefferson-curl (no Phase1Rx)
            }
            // Recovery exercises
            else if (dayType == "recovery")
            {
                exercises.Add(("steam-sauna", "recovery_tool", 1, "15 min"));
                exercises.Add(("dry-sauna", "recovery_tool", 2, "15 min"));
                exercises.Add(("compression-boots", "recovery_tool", 3, "20 min"));
                exercises.Add(("hydro-massager", "recovery_tool", 4, "10 min"));
                exercises.Add(("compex-warmup", "recovery_tool", 5, "10 min"));
            }
            // Rest exercises
            else if (dayType == "rest")
            {
                exercises.Add(("cars-routine", "mobility", 1, "5 min"));
                exercises.Add(("dead-hang", "mobility", 2, "20 sec"));
                exercises.Add(("deep-squat-hold", "mobility", 3, "30 sec"));
            }

            return exercises;
        }

        /// <summary>
        /// Gets all exercises from the database.
        /// </summary>
        public async Task<List<Exercise>> GetAllExercisesAsync()
        {
            var exercises = new List<Exercise>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Name, Category, Targets, Description, Cues, Explanation, Warning, Phases, DefaultRx FROM Exercises ORDER BY Name";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            exercises.Add(new Exercise
                            {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Category = reader.GetString(2),
                                Targets = reader.GetString(3),
                                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                                Cues = reader.IsDBNull(5) ? null : reader.GetString(5),
                                Explanation = reader.IsDBNull(6) ? null : reader.GetString(6),
                                Warning = reader.IsDBNull(7) ? null : reader.GetString(7),
                                Phases = reader.GetString(8),
                                DefaultRx = reader.IsDBNull(9) ? null : reader.GetString(9)
                            });
                        }
                    }
                }
            }

            return exercises;
        }

        /// <summary>
        /// Gets a single exercise by ID.
        /// </summary>
        public async Task<Exercise> GetExerciseAsync(string id)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Name, Category, Targets, Description, Cues, Explanation, Warning, Phases FROM Exercises WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", id);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new Exercise
                            {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Category = reader.GetString(2),
                                Targets = reader.GetString(3),
                                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                                Cues = reader.IsDBNull(5) ? null : reader.GetString(5),
                                Explanation = reader.IsDBNull(6) ? null : reader.GetString(6),
                                Warning = reader.IsDBNull(7) ? null : reader.GetString(7),
                                Phases = reader.GetString(8)
                            };
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets all supplements.
        /// </summary>
        public async Task<List<Supplement>> GetSupplementsAsync()
        {
            var supplements = new List<Supplement>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Name, Dose, Time, TimeGroup FROM Supplements ORDER BY TimeGroup, Name";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            supplements.Add(new Supplement
                            {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Dose = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Time = reader.IsDBNull(3) ? null : reader.GetString(3),
                                TimeGroup = reader.GetString(4)
                            });
                        }
                    }
                }
            }

            return supplements;
        }

        /// <summary>
        /// Gets all session steps for a specific day type.
        /// </summary>
        public async Task<List<SessionStep>> GetSessionStepsAsync(string dayType)
        {
            var steps = new List<SessionStep>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT Id, DayType, ExerciseId, Phase1Rx, Phase2Rx, PhaseOnly, Section, SortOrder
                        FROM SessionSteps
                        WHERE DayType = @dayType
                        ORDER BY SortOrder";
                    command.Parameters.AddWithValue("@dayType", dayType);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            steps.Add(new SessionStep
                            {
                                Id = reader.GetInt32(0),
                                DayType = reader.GetString(1),
                                ExerciseId = reader.GetString(2),
                                Phase1Rx = reader.IsDBNull(3) ? null : reader.GetString(3),
                                Phase2Rx = reader.IsDBNull(4) ? null : reader.GetString(4),
                                PhaseOnly = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                                Section = reader.IsDBNull(6) ? null : reader.GetString(6),
                                SortOrder = reader.GetInt32(7)
                            });
                        }
                    }
                }
            }

            return steps;
        }

        /// <summary>
        /// Gets all daily checks for a specific user and date.
        /// </summary>
        public async Task<List<DailyCheck>> GetDailyChecksAsync(string userId, string date)
        {
            var checks = new List<DailyCheck>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT Id, UserId, Date, ItemType, ItemId, StepIndex, Checked
                        FROM DailyChecks
                        WHERE UserId = @userId AND Date = @date
                        ORDER BY ItemType, ItemId, StepIndex";
                    command.Parameters.AddWithValue("@userId", userId);
                    command.Parameters.AddWithValue("@date", date);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            checks.Add(new DailyCheck
                            {
                                Id = reader.GetInt32(0),
                                UserId = reader.GetString(1),
                                Date = reader.GetString(2),
                                ItemType = reader.GetString(3),
                                ItemId = reader.GetString(4),
                                StepIndex = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                                Checked = reader.GetBoolean(6)
                            });
                        }
                    }
                }
            }

            return checks;
        }

        /// <summary>
        /// Toggles a check for a specific daily item.
        /// </summary>
        public async Task ToggleCheckAsync(string userId, string date, string itemType, string itemId, int stepIndex)
        {
            await SetCheckAsync(userId, date, itemType, itemId, stepIndex, true);
        }

        /// <summary>
        /// Explicitly sets a check to a specific state (checked or unchecked).
        /// Uses MERGE to handle both insert and update idempotently.
        /// </summary>
        public async Task SetCheckAsync(string userId, string date, string itemType, string itemId, int stepIndex, bool checkedState)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        MERGE INTO DailyChecks AS target
                        USING (SELECT @userId AS UserId, @date AS Date, @itemType AS ItemType, @itemId AS ItemId, @stepIndex AS StepIndex) AS source
                        ON target.UserId = source.UserId AND target.Date = source.Date AND target.ItemType = source.ItemType AND target.ItemId = source.ItemId AND target.StepIndex = source.StepIndex
                        WHEN MATCHED THEN
                            UPDATE SET Checked = @checked
                        WHEN NOT MATCHED THEN
                            INSERT (UserId, Date, ItemType, ItemId, StepIndex, Checked)
                            VALUES (@userId, @date, @itemType, @itemId, @stepIndex, @checked);";

                    command.Parameters.AddWithValue("@userId", userId);
                    command.Parameters.AddWithValue("@date", date);
                    command.Parameters.AddWithValue("@itemType", itemType);
                    command.Parameters.AddWithValue("@itemId", itemId);
                    command.Parameters.AddWithValue("@stepIndex", stepIndex);
                    command.Parameters.AddWithValue("@checked", checkedState);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Gets user settings or creates default if not exists.
        /// </summary>
        public async Task<UserSettings> GetUserSettingsAsync(string userId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT UserId, StartDate, DisabledTools FROM UserSettings WHERE UserId = @userId";
                    command.Parameters.AddWithValue("@userId", userId);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new UserSettings
                            {
                                UserId = reader.GetString(0),
                                StartDate = reader.IsDBNull(1) ? null : reader.GetString(1),
                                DisabledTools = reader.IsDBNull(2) ? null : reader.GetString(2)
                            };
                        }
                    }
                }

                // Create default settings if not exists
                var defaultSettings = new UserSettings { UserId = userId, StartDate = null, DisabledTools = null };
                await SaveUserSettingsAsync(defaultSettings);
                return defaultSettings;
            }
        }

        /// <summary>
        /// Saves or updates user settings.
        /// </summary>
        public async Task SaveUserSettingsAsync(UserSettings settings)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        MERGE INTO UserSettings AS target
                        USING (VALUES (@userId, @startDate, @disabledTools)) AS source (UserId, StartDate, DisabledTools)
                        ON target.UserId = source.UserId
                        WHEN MATCHED THEN
                            UPDATE SET StartDate = @startDate, DisabledTools = @disabledTools
                        WHEN NOT MATCHED THEN
                            INSERT (UserId, StartDate, DisabledTools)
                            VALUES (@userId, @startDate, @disabledTools);";

                    command.Parameters.AddWithValue("@userId", settings.UserId);
                    command.Parameters.AddWithValue("@startDate", settings.StartDate ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@disabledTools", settings.DisabledTools ?? (object)DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Gets all milestones.
        /// </summary>
        public async Task<List<Milestone>> GetMilestonesAsync()
        {
            var milestones = new List<Milestone>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Name, Done, AchievedDate FROM Milestones ORDER BY Name";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            milestones.Add(new Milestone
                            {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Done = reader.GetBoolean(2),
                                AchievedDate = reader.IsDBNull(3) ? null : reader.GetString(3)
                            });
                        }
                    }
                }
            }

            return milestones;
        }

        /// <summary>
        /// Marks a milestone as completed.
        /// </summary>
        public async Task CompleteMilestoneAsync(string id, string date)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        UPDATE Milestones
                        SET Done = 1, AchievedDate = @date
                        WHERE Id = @id";

                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@date", date);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Gets all session logs for a specific user.
        /// </summary>
        public async Task<List<SessionLog>> GetSessionLogsAsync(string userId)
        {
            var logs = new List<SessionLog>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT Id, UserId, Date, StepsDone, StepsTotal
                        FROM SessionLogs
                        WHERE UserId = @userId
                        ORDER BY Date DESC";

                    command.Parameters.AddWithValue("@userId", userId);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            logs.Add(new SessionLog
                            {
                                Id = reader.GetInt32(0),
                                UserId = reader.GetString(1),
                                Date = reader.GetString(2),
                                StepsDone = reader.GetInt32(3),
                                StepsTotal = reader.GetInt32(4)
                            });
                        }
                    }
                }
            }

            return logs;
        }

        /// <summary>
        /// Logs a session for a specific user and date. Inserts if not exists for that date.
        /// </summary>
        public async Task LogSessionAsync(string userId, string date, int done, int total)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM SessionLogs WHERE UserId = @userId AND Date = @date)
                        BEGIN
                            INSERT INTO SessionLogs (UserId, Date, StepsDone, StepsTotal)
                            VALUES (@userId, @date, @stepsDone, @stepsTotal)
                        END
                        ELSE
                        BEGIN
                            UPDATE SessionLogs
                            SET StepsDone = @stepsDone, StepsTotal = @stepsTotal
                            WHERE UserId = @userId AND Date = @date
                        END";

                    command.Parameters.AddWithValue("@userId", userId);
                    command.Parameters.AddWithValue("@date", date);
                    command.Parameters.AddWithValue("@stepsDone", done);
                    command.Parameters.AddWithValue("@stepsTotal", total);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Gets the session log for a specific user and date.
        /// </summary>
        public async Task<SessionLog> GetSessionLogForDateAsync(string userId, string date)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT Id, UserId, Date, StepsDone, StepsTotal
                        FROM SessionLogs
                        WHERE UserId = @userId AND Date = @date";

                    command.Parameters.AddWithValue("@userId", userId);
                    command.Parameters.AddWithValue("@date", date);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new SessionLog
                            {
                                Id = reader.GetInt32(0),
                                UserId = reader.GetString(1),
                                Date = reader.GetString(2),
                                StepsDone = reader.GetInt32(3),
                                StepsTotal = reader.GetInt32(4)
                            };
                        }
                    }
                }
            }

            return null;
        }

        // ============ AUTH METHODS ============

        public async Task<AppUser?> CreateUserAsync(string username, string password)
        {
            // Generate salt and hash
            byte[] saltBytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes);
            }
            string salt = Convert.ToBase64String(saltBytes);
            string hash = HashPassword(password, salt);
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = @username)
                        BEGIN
                            INSERT INTO Users (Username, PasswordHash, Salt, CreatedDate)
                            OUTPUT INSERTED.Id
                            VALUES (@username, @hash, @salt, @today)
                        END";
                    command.Parameters.AddWithValue("@username", username);
                    command.Parameters.AddWithValue("@hash", hash);
                    command.Parameters.AddWithValue("@salt", salt);
                    command.Parameters.AddWithValue("@today", today);

                    var result = await command.ExecuteScalarAsync();
                    if (result == null) return null; // Username already exists

                    return new AppUser
                    {
                        Id = (int)result,
                        Username = username,
                        PasswordHash = hash,
                        Salt = salt,
                        CreatedDate = today
                    };
                }
            }
        }

        public async Task<AppUser?> ValidateUserAsync(string username, string password)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Username, PasswordHash, Salt, CreatedDate FROM Users WHERE Username = @username";
                    command.Parameters.AddWithValue("@username", username);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var user = new AppUser
                            {
                                Id = reader.GetInt32(0),
                                Username = reader.GetString(1),
                                PasswordHash = reader.GetString(2),
                                Salt = reader.GetString(3),
                                CreatedDate = reader.GetString(4)
                            };

                            string hash = HashPassword(password, user.Salt);
                            if (hash == user.PasswordHash)
                                return user;
                        }
                    }
                }
            }
            return null;
        }

        private static string HashPassword(string password, string salt)
        {
            using (var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(
                password,
                Convert.FromBase64String(salt),
                100000,
                System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                byte[] hash = pbkdf2.GetBytes(32);
                return Convert.ToBase64String(hash);
            }
        }

        // ============ PER-USER MILESTONES ============

        public async Task<List<Milestone>> GetUserMilestonesAsync(string userId)
        {
            var milestones = new List<Milestone>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT m.Id, m.Name, ISNULL(um.Done, 0) AS Done, um.AchievedDate
                        FROM Milestones m
                        LEFT JOIN UserMilestones um ON m.Id = um.MilestoneId AND um.UserId = @userId
                        ORDER BY m.Name";
                    command.Parameters.AddWithValue("@userId", userId);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            milestones.Add(new Milestone
                            {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Done = reader.GetBoolean(2),
                                AchievedDate = reader.IsDBNull(3) ? null : reader.GetString(3)
                            });
                        }
                    }
                }
            }

            return milestones;
        }

        public async Task CompleteUserMilestoneAsync(string userId, string milestoneId, string date)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        MERGE INTO UserMilestones AS target
                        USING (VALUES (@userId, @milestoneId)) AS source (UserId, MilestoneId)
                        ON target.UserId = source.UserId AND target.MilestoneId = source.MilestoneId
                        WHEN MATCHED THEN
                            UPDATE SET Done = 1, AchievedDate = @date
                        WHEN NOT MATCHED THEN
                            INSERT (UserId, MilestoneId, Done, AchievedDate)
                            VALUES (@userId, @milestoneId, 1, @date);";
                    command.Parameters.AddWithValue("@userId", userId);
                    command.Parameters.AddWithValue("@milestoneId", milestoneId);
                    command.Parameters.AddWithValue("@date", date);

                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        // ── Program & Enrollment Methods ──

        public async Task<List<TrainingProgram>> GetProgramsAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, DurationDays, Description FROM Programs ORDER BY Name";
            var programs = new List<TrainingProgram>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                programs.Add(new TrainingProgram
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DurationDays = reader.GetInt32(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3)
                });
            }
            return programs;
        }

        public async Task<UserEnrollment?> GetActiveEnrollmentAsync(string userId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT e.Id, e.UserId, e.ProgramId, e.StartDate, e.Status, p.Name
                FROM UserEnrollments e
                JOIN Programs p ON e.ProgramId = p.Id
                WHERE e.UserId = @userId AND e.Status = 'active'";
            command.Parameters.AddWithValue("@userId", userId);
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserEnrollment
                {
                    Id = reader.GetInt32(0),
                    UserId = reader.GetString(1),
                    ProgramId = reader.GetInt32(2),
                    StartDate = reader.GetString(3),
                    Status = reader.GetString(4),
                    ProgramName = reader.GetString(5)
                };
            }
            return null;
        }

        public async Task EnrollUserAsync(string userId, int programId, string startDate)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Mark any existing active enrollment as completed
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE UserEnrollments SET Status = 'completed' WHERE UserId = @userId AND Status = 'active'";
                cmd.Parameters.AddWithValue("@userId", userId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Create new enrollment
            int enrollmentId;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO UserEnrollments (UserId, ProgramId, StartDate, Status)
                    OUTPUT INSERTED.Id
                    VALUES (@userId, @programId, @startDate, 'active')";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@programId", programId);
                cmd.Parameters.AddWithValue("@startDate", startDate);
                enrollmentId = (int)await cmd.ExecuteScalarAsync();
            }

            // Get program template
            var template = new List<ProgramTemplate>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Id, ProgramId, DayNumber, DayType, ExerciseId, Category, SortOrder, Rx
                    FROM ProgramTemplate WHERE ProgramId = @programId ORDER BY DayNumber, SortOrder";
                cmd.Parameters.AddWithValue("@programId", programId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    template.Add(new ProgramTemplate
                    {
                        Id = reader.GetInt32(0),
                        ProgramId = reader.GetInt32(1),
                        DayNumber = reader.GetInt32(2),
                        DayType = reader.GetString(3),
                        ExerciseId = reader.GetString(4),
                        Category = reader.GetString(5),
                        SortOrder = reader.GetInt32(6),
                        Rx = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }
            }

            // Delete any existing daily plan for this user+program
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM UserDailyPlan WHERE UserId = @userId AND ProgramId = @programId";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@programId", programId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Stamp out template into UserDailyPlan with real dates
            var start = DateTime.Parse(startDate);
            foreach (var t in template)
            {
                var date = start.AddDays(t.DayNumber - 1).ToString("yyyy-MM-dd");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO UserDailyPlan (UserId, ProgramId, Date, DayType, ExerciseId, Category, SortOrder, Rx, AiAdjusted)
                    VALUES (@userId, @programId, @date, @dayType, @exerciseId, @category, @sortOrder, @rx, 0)";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@programId", programId);
                cmd.Parameters.AddWithValue("@date", date);
                cmd.Parameters.AddWithValue("@dayType", t.DayType);
                cmd.Parameters.AddWithValue("@exerciseId", t.ExerciseId);
                cmd.Parameters.AddWithValue("@category", t.Category);
                cmd.Parameters.AddWithValue("@sortOrder", t.SortOrder);
                cmd.Parameters.AddWithValue("@rx", (object?)t.Rx ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<UserDailyPlanEntry>> GetUserDailyPlanAsync(string userId, string date)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT p.Id, p.UserId, p.ProgramId, p.Date, p.DayType, p.ExerciseId, p.Category, p.SortOrder, p.Rx, p.AiAdjusted
                FROM UserDailyPlan p
                JOIN UserEnrollments e ON p.UserId = e.UserId AND p.ProgramId = e.ProgramId
                WHERE p.UserId = @userId AND p.Date = @date AND e.Status = 'active'
                ORDER BY p.SortOrder";
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@date", date);
            var entries = new List<UserDailyPlanEntry>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new UserDailyPlanEntry
                {
                    Id = reader.GetInt32(0),
                    UserId = reader.GetString(1),
                    ProgramId = reader.GetInt32(2),
                    Date = reader.GetString(3),
                    DayType = reader.GetString(4),
                    ExerciseId = reader.GetString(5),
                    Category = reader.GetString(6),
                    SortOrder = reader.GetInt32(7),
                    Rx = reader.IsDBNull(8) ? null : reader.GetString(8),
                    AiAdjusted = reader.GetBoolean(9)
                });
            }
            return entries;
        }

        public async Task<List<UserDailyPlanEntry>> GetUserDailyPlanRangeAsync(string userId, string startDate, string endDate)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT p.Id, p.UserId, p.ProgramId, p.Date, p.DayType, p.ExerciseId, p.Category, p.SortOrder, p.Rx, p.AiAdjusted
                FROM UserDailyPlan p
                JOIN UserEnrollments e ON p.UserId = e.UserId AND p.ProgramId = e.ProgramId
                WHERE p.UserId = @userId AND p.Date >= @startDate AND p.Date <= @endDate AND e.Status = 'active'
                ORDER BY p.Date, p.SortOrder";
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@startDate", startDate);
            command.Parameters.AddWithValue("@endDate", endDate);
            var entries = new List<UserDailyPlanEntry>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new UserDailyPlanEntry
                {
                    Id = reader.GetInt32(0),
                    UserId = reader.GetString(1),
                    ProgramId = reader.GetInt32(2),
                    Date = reader.GetString(3),
                    DayType = reader.GetString(4),
                    ExerciseId = reader.GetString(5),
                    Category = reader.GetString(6),
                    SortOrder = reader.GetInt32(7),
                    Rx = reader.IsDBNull(8) ? null : reader.GetString(8),
                    AiAdjusted = reader.GetBoolean(9)
                });
            }
            return entries;
        }

        /// <summary>
        /// Adds a manually added exercise to the user's daily plan.
        /// Returns the new entry's Id, or -1 if the exercise is already in the plan for that date.
        /// </summary>
        public async Task<int> AddManualPlanEntryAsync(string userId, string date, string exerciseId, string category)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if exercise already exists in plan for this date
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(*) FROM UserDailyPlan WHERE UserId = @userId AND Date = @date AND ExerciseId = @exerciseId";
                checkCmd.Parameters.AddWithValue("@userId", userId);
                checkCmd.Parameters.AddWithValue("@date", date);
                checkCmd.Parameters.AddWithValue("@exerciseId", exerciseId);
                var count = (int)await checkCmd.ExecuteScalarAsync();
                if (count > 0) return -1; // Already exists
            }

            // Get the active enrollment to find ProgramId and DayType
            var enrollment = await GetActiveEnrollmentAsync(userId);
            if (enrollment == null) return -1;

            // Get DayType from existing plan entries for this date, or default to "gym"
            string dayType = "gym";
            using (var dtCmd = connection.CreateCommand())
            {
                dtCmd.CommandText = "SELECT TOP 1 DayType FROM UserDailyPlan WHERE UserId = @userId AND Date = @date";
                dtCmd.Parameters.AddWithValue("@userId", userId);
                dtCmd.Parameters.AddWithValue("@date", date);
                var result = await dtCmd.ExecuteScalarAsync();
                if (result != null) dayType = result.ToString()!;
            }

            // Get the exercise's DefaultRx
            string? defaultRx = null;
            using (var rxCmd = connection.CreateCommand())
            {
                rxCmd.CommandText = "SELECT DefaultRx FROM Exercises WHERE Id = @id";
                rxCmd.Parameters.AddWithValue("@id", exerciseId);
                var result = await rxCmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value) defaultRx = result.ToString();
            }

            // Get max sort order for this date
            int maxSort = 0;
            using (var sortCmd = connection.CreateCommand())
            {
                sortCmd.CommandText = "SELECT ISNULL(MAX(SortOrder), 0) FROM UserDailyPlan WHERE UserId = @userId AND Date = @date";
                sortCmd.Parameters.AddWithValue("@userId", userId);
                sortCmd.Parameters.AddWithValue("@date", date);
                maxSort = (int)await sortCmd.ExecuteScalarAsync();
            }

            // Insert the manual entry
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO UserDailyPlan (UserId, ProgramId, Date, DayType, ExerciseId, Category, SortOrder, Rx, AiAdjusted, IsManual)
                OUTPUT INSERTED.Id
                VALUES (@userId, @programId, @date, @dayType, @exerciseId, @category, @sortOrder, @rx, 0, 1)";
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@programId", enrollment.ProgramId);
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@dayType", dayType);
            cmd.Parameters.AddWithValue("@exerciseId", exerciseId);
            cmd.Parameters.AddWithValue("@category", category);
            cmd.Parameters.AddWithValue("@sortOrder", maxSort + 1);
            cmd.Parameters.AddWithValue("@rx", (object?)defaultRx ?? DBNull.Value);
            return (int)await cmd.ExecuteScalarAsync();
        }

        /// <summary>
        /// Gets all exercises for a given category (for the add-exercise picker).
        /// </summary>
        public async Task<List<Exercise>> GetExercisesByCategoryAsync(string category)
        {
            var exercises = new List<Exercise>();
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, Category, Targets, Description, Cues, Explanation, Warning, Phases, DefaultRx FROM Exercises WHERE Category = @category ORDER BY Name";
            command.Parameters.AddWithValue("@category", category);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                exercises.Add(new Exercise
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Category = reader.GetString(2),
                    Targets = reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Cues = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Explanation = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Warning = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Phases = reader.GetString(8),
                    DefaultRx = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }
            return exercises;
        }

        // ── UserSupplements Methods ──

        /// <summary>
        /// Ensures the user has UserSupplements rows. If none exist, seeds with the 7 default supplements.
        /// Returns the user's active supplements for the given date (where AddedDate <= date).
        /// Uses the TimeGroup from UserSupplements (not the supplement's default) to allow per-section placement.
        /// </summary>
        public async Task<List<Supplement>> GetUserSupplementsForDateAsync(string userId, string date)
        {
            var supplements = new List<Supplement>();
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if user has any UserSupplements rows at all
            int count;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM UserSupplements WHERE UserId = @userId";
                cmd.Parameters.AddWithValue("@userId", userId);
                count = (int)await cmd.ExecuteScalarAsync();
            }

            // Auto-seed defaults ONLY for legacy users who never went through onboarding.
            // Users who completed onboarding (step 7) with empty SelectedSupplements explicitly chose none.
            if (count == 0)
            {
                // Check if user completed onboarding — if so, respect their choice (no auto-seed)
                bool skipAutoSeed = false;
                using (var prefCmd = connection.CreateCommand())
                {
                    prefCmd.CommandText = "SELECT OnboardingStep, SelectedSupplements FROM UserPreferences WHERE UserId = @userId";
                    prefCmd.Parameters.AddWithValue("@userId", userId);
                    using var prefReader = await prefCmd.ExecuteReaderAsync();
                    if (await prefReader.ReadAsync())
                    {
                        int step = prefReader.GetInt32(0);
                        string selSupps = prefReader.IsDBNull(1) ? "" : prefReader.GetString(1);
                        // If onboarding is complete and supplements are empty, user chose none
                        if (step >= 7 && string.IsNullOrWhiteSpace(selSupps))
                            skipAutoSeed = true;
                    }
                }

                if (!skipAutoSeed)
                {
                    string startDate = date;
                    var enrollment = await GetActiveEnrollmentAsync(userId);
                    if (enrollment != null) startDate = enrollment.StartDate;

                    var defaultIds = new[] {
                        "supp-collagen", "supp-omega3", "supp-vitamind", "supp-creatine",
                        "supp-curcumin", "supp-omega3b", "supp-mag"
                    };
                    foreach (var suppId in defaultIds)
                    {
                        using var seedCmd = connection.CreateCommand();
                        seedCmd.CommandText = @"
                            IF EXISTS (SELECT 1 FROM Supplements WHERE Id = @suppId)
                                INSERT INTO UserSupplements (UserId, SupplementId, TimeGroup, AddedDate)
                                SELECT @userId, @suppId, TimeGroup, @addedDate FROM Supplements WHERE Id = @suppId";
                        seedCmd.Parameters.AddWithValue("@userId", userId);
                        seedCmd.Parameters.AddWithValue("@suppId", suppId);
                        seedCmd.Parameters.AddWithValue("@addedDate", startDate);
                        await seedCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            // Fetch user's supplements for the given date, using UserSupplements.TimeGroup for grouping
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT s.Id, s.Name, s.Dose, s.Time, us.TimeGroup
                    FROM UserSupplements us
                    JOIN Supplements s ON us.SupplementId = s.Id
                    WHERE us.UserId = @userId AND us.AddedDate <= @date
                    ORDER BY us.TimeGroup, s.Name";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@date", date);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    supplements.Add(new Supplement
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Dose = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Time = reader.IsDBNull(3) ? null : reader.GetString(3),
                        TimeGroup = reader.GetString(4)
                    });
                }
            }

            return supplements;
        }

        /// <summary>
        /// Gets all supplements (for the picker). Any supplement can be added to any time group.
        /// Excludes supplements already in the specified timeGroup for this user.
        /// </summary>
        public async Task<List<Supplement>> GetAvailableSupplementsForGroupAsync(string userId, string timeGroup)
        {
            var supplements = new List<Supplement>();
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.Id, s.Name, s.Dose, s.Time, s.TimeGroup
                FROM Supplements s
                WHERE s.Id NOT IN (
                    SELECT SupplementId FROM UserSupplements WHERE UserId = @userId AND TimeGroup = @timeGroup
                )
                ORDER BY s.Name";
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@timeGroup", timeGroup);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                supplements.Add(new Supplement
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Dose = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Time = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TimeGroup = reader.GetString(4)
                });
            }
            return supplements;
        }

        /// <summary>
        /// Adds a supplement to a specific time group for the user.
        /// Same supplement can be in multiple time groups.
        /// </summary>
        public async Task<bool> AddUserSupplementAsync(string userId, string supplementId, string timeGroup, string addedDate)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if already added to this time group
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(*) FROM UserSupplements WHERE UserId = @userId AND SupplementId = @suppId AND TimeGroup = @timeGroup";
                checkCmd.Parameters.AddWithValue("@userId", userId);
                checkCmd.Parameters.AddWithValue("@suppId", supplementId);
                checkCmd.Parameters.AddWithValue("@timeGroup", timeGroup);
                if ((int)await checkCmd.ExecuteScalarAsync() > 0) return false;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO UserSupplements (UserId, SupplementId, TimeGroup, AddedDate) VALUES (@userId, @suppId, @timeGroup, @addedDate)";
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@suppId", supplementId);
            cmd.Parameters.AddWithValue("@timeGroup", timeGroup);
            cmd.Parameters.AddWithValue("@addedDate", addedDate);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }

        /// <summary>
        /// Adds a manual exercise to the specified date AND all future dates in the plan.
        /// </summary>
        public async Task<int> AddManualPlanEntryWithFutureAsync(string userId, string date, string exerciseId, string category)
        {
            // First add to the specified date (existing logic)
            int newId = await AddManualPlanEntryAsync(userId, date, exerciseId, category);
            if (newId == -1) return -1;

            // Now also add to all future dates in the plan
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var enrollment = await GetActiveEnrollmentAsync(userId);
            if (enrollment == null) return newId;

            // Get the exercise's DefaultRx
            string? defaultRx = null;
            using (var rxCmd = connection.CreateCommand())
            {
                rxCmd.CommandText = "SELECT DefaultRx FROM Exercises WHERE Id = @id";
                rxCmd.Parameters.AddWithValue("@id", exerciseId);
                var result = await rxCmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value) defaultRx = result.ToString();
            }

            // Get all distinct future dates in the plan
            var futureDates = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT DISTINCT Date FROM UserDailyPlan
                    WHERE UserId = @userId AND ProgramId = @programId AND Date > @date
                    ORDER BY Date";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@programId", enrollment.ProgramId);
                cmd.Parameters.AddWithValue("@date", date);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    futureDates.Add(reader.GetString(0));
                }
            }

            // Insert into each future date (skip if already exists)
            foreach (var futureDate in futureDates)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    IF NOT EXISTS (SELECT 1 FROM UserDailyPlan WHERE UserId = @userId AND Date = @date AND ExerciseId = @exerciseId)
                    BEGIN
                        INSERT INTO UserDailyPlan (UserId, ProgramId, Date, DayType, ExerciseId, Category, SortOrder, Rx, AiAdjusted, IsManual)
                        SELECT @userId, @programId, @date,
                               ISNULL((SELECT TOP 1 DayType FROM UserDailyPlan WHERE UserId = @userId AND Date = @date), 'gym'),
                               @exerciseId, @category,
                               ISNULL((SELECT MAX(SortOrder) FROM UserDailyPlan WHERE UserId = @userId AND Date = @date), 0) + 1,
                               @rx, 0, 1
                    END";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@programId", enrollment.ProgramId);
                cmd.Parameters.AddWithValue("@date", futureDate);
                cmd.Parameters.AddWithValue("@exerciseId", exerciseId);
                cmd.Parameters.AddWithValue("@category", category);
                cmd.Parameters.AddWithValue("@rx", (object?)defaultRx ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            return newId;
        }

        // ── UserPreferences CRUD ──

        public async Task<UserPreferences?> GetUserPreferencesAsync(string userId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT UserId, HasGym, DaysPerWeek, OnboardingStep, SelectedExercises, SelectedSupplements FROM UserPreferences WHERE UserId = @userId";
            cmd.Parameters.AddWithValue("@userId", userId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserPreferences
                {
                    UserId = reader.GetString(0),
                    HasGym = reader.GetBoolean(1),
                    DaysPerWeek = reader.GetInt32(2),
                    OnboardingStep = reader.GetInt32(3),
                    SelectedExercises = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    SelectedSupplements = reader.IsDBNull(5) ? "" : reader.GetString(5)
                };
            }
            return null;
        }

        public async Task SaveUserPreferencesAsync(UserPreferences prefs)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                MERGE INTO UserPreferences AS target
                USING (VALUES (@userId, @hasGym, @daysPerWeek, @onboardingStep, @selectedExercises, @selectedSupplements))
                    AS source (UserId, HasGym, DaysPerWeek, OnboardingStep, SelectedExercises, SelectedSupplements)
                ON target.UserId = source.UserId
                WHEN MATCHED THEN UPDATE SET
                    HasGym = source.HasGym, DaysPerWeek = source.DaysPerWeek,
                    OnboardingStep = source.OnboardingStep, SelectedExercises = source.SelectedExercises,
                    SelectedSupplements = source.SelectedSupplements
                WHEN NOT MATCHED THEN INSERT (UserId, HasGym, DaysPerWeek, OnboardingStep, SelectedExercises, SelectedSupplements)
                    VALUES (source.UserId, source.HasGym, source.DaysPerWeek, source.OnboardingStep, source.SelectedExercises, source.SelectedSupplements);";
            cmd.Parameters.AddWithValue("@userId", prefs.UserId);
            cmd.Parameters.AddWithValue("@hasGym", prefs.HasGym);
            cmd.Parameters.AddWithValue("@daysPerWeek", prefs.DaysPerWeek);
            cmd.Parameters.AddWithValue("@onboardingStep", prefs.OnboardingStep);
            cmd.Parameters.AddWithValue("@selectedExercises", prefs.SelectedExercises ?? "");
            cmd.Parameters.AddWithValue("@selectedSupplements", prefs.SelectedSupplements ?? "");
            await cmd.ExecuteNonQueryAsync();
        }

        // ── Reset all user data for re-onboarding ──

        public async Task ResetAllUserDataAsync(string userId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var tables = new[]
            {
                "DELETE FROM DailyChecks WHERE UserId = @u",
                "DELETE FROM SessionLogs WHERE UserId = @u",
                "DELETE FROM UserDailyPlan WHERE UserId = @u",
                "DELETE FROM UserEnrollments WHERE UserId = @u",
                "DELETE FROM UserSupplements WHERE UserId = @u",
                "DELETE FROM UserMilestones WHERE UserId = @u",
                "DELETE FROM UserSettings WHERE UserId = @u",
                "DELETE FROM UserPreferences WHERE UserId = @u"
            };

            foreach (var sql in tables)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@u", userId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── Generate custom plan based on user preferences ──

        public async Task GenerateCustomPlanAsync(string userId, UserPreferences prefs)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get or create the program
            int programId;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 Id FROM Programs";
                var result = await cmd.ExecuteScalarAsync();
                programId = result != null ? (int)result : 1;
            }

            // Mark old enrollments as completed
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE UserEnrollments SET Status = 'completed' WHERE UserId = @u AND Status = 'active'";
                cmd.Parameters.AddWithValue("@u", userId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Delete old plan data
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM UserDailyPlan WHERE UserId = @u";
                cmd.Parameters.AddWithValue("@u", userId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Create enrollment starting today (Pacific time)
            var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
            string startDate = pacificNow.ToString("yyyy-MM-dd");

            int enrollmentId;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO UserEnrollments (UserId, ProgramId, StartDate, Status)
                    OUTPUT INSERTED.Id VALUES (@u, @p, @s, 'active')";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@p", programId);
                cmd.Parameters.AddWithValue("@s", startDate);
                enrollmentId = (int)await cmd.ExecuteScalarAsync();
            }

            // Save start date in UserSettings too
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    MERGE INTO UserSettings AS t USING (VALUES (@u, @s, '')) AS s (UserId, StartDate, DisabledTools)
                    ON t.UserId = s.UserId
                    WHEN MATCHED THEN UPDATE SET StartDate = s.StartDate
                    WHEN NOT MATCHED THEN INSERT (UserId, StartDate, DisabledTools) VALUES (s.UserId, s.StartDate, s.DisabledTools);";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@s", startDate);
                await cmd.ExecuteNonQueryAsync();
            }

            // Build selected exercise sets
            var selectedIds = new HashSet<string>((prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));

            // Load exercise metadata for category info
            var allExercises = new Dictionary<string, Exercise>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Name, Category, Targets, DefaultRx FROM Exercises";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    allExercises[reader.GetString(0)] = new Exercise
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Category = reader.GetString(2),
                        Targets = reader.GetString(3),
                        DefaultRx = reader.IsDBNull(4) ? null : reader.GetString(4)
                    };
                }
            }

            // Determine weekly pattern
            string[] weeklyPattern = GetWeeklyPattern(prefs.HasGym, prefs.DaysPerWeek);

            // Generate 28 days
            var start = DateTime.Parse(startDate);
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = @"INSERT INTO UserDailyPlan (UserId, ProgramId, Date, DayType, ExerciseId, Category, SortOrder, Rx, AiAdjusted)
                VALUES (@u, @p, @d, @dt, @eid, @cat, @so, @rx, 0)";

            for (int day = 0; day < 28; day++)
            {
                string dayType = weeklyPattern[day % 7];
                string date = start.AddDays(day).ToString("yyyy-MM-dd");
                var dayExercises = GetExercisesForDayTypeCustom(dayType, selectedIds, allExercises, prefs.HasGym);

                int sortOrder = 1;
                foreach (var (exId, category, rx) in dayExercises)
                {
                    insertCmd.Parameters.Clear();
                    insertCmd.Parameters.AddWithValue("@u", userId);
                    insertCmd.Parameters.AddWithValue("@p", programId);
                    insertCmd.Parameters.AddWithValue("@d", date);
                    insertCmd.Parameters.AddWithValue("@dt", dayType);
                    insertCmd.Parameters.AddWithValue("@eid", exId);
                    insertCmd.Parameters.AddWithValue("@cat", category);
                    insertCmd.Parameters.AddWithValue("@so", sortOrder++);
                    insertCmd.Parameters.AddWithValue("@rx", (object?)rx ?? DBNull.Value);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            // Set up user supplements
            var selectedSuppIds = (prefs.SelectedSupplements ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            // Clear existing
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM UserSupplements WHERE UserId = @u";
                cmd.Parameters.AddWithValue("@u", userId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Load supplement metadata for time groups
            var suppMeta = new Dictionary<string, Supplement>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Name, Dose, Time, TimeGroup FROM Supplements";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    suppMeta[reader.GetString(0)] = new Supplement
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Dose = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Time = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        TimeGroup = reader.IsDBNull(4) ? "am" : reader.GetString(4)
                    };
                }
            }

            foreach (var suppId in selectedSuppIds)
            {
                if (!suppMeta.ContainsKey(suppId)) continue;
                var supp = suppMeta[suppId];
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    IF NOT EXISTS (SELECT 1 FROM UserSupplements WHERE UserId=@u AND SupplementId=@s AND TimeGroup=@tg)
                    INSERT INTO UserSupplements (UserId, SupplementId, TimeGroup, AddedDate) VALUES (@u, @s, @tg, @d)";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@s", suppId);
                cmd.Parameters.AddWithValue("@tg", supp.TimeGroup);
                cmd.Parameters.AddWithValue("@d", startDate);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Exercises that require gym equipment — excluded from home-only plans
        private static readonly HashSet<string> GymOnlyExercises = new()
        {
            "hot-tub", "vibration-plate",                          // warmup tools (gym equipment)
            "dead-hang",                                            // needs pull-up bar
            "hydro-massager", "steam-sauna", "dry-sauna",          // gym recovery
            "compex-warmup", "compex-recovery", "compression-boots" // specialized recovery equipment
        };

        public static bool IsHomeAppropriate(string exerciseId) => !GymOnlyExercises.Contains(exerciseId);

        private string[] GetWeeklyPattern(bool hasGym, int daysPerWeek)
        {
            string g = hasGym ? "gym" : "home";
            return daysPerWeek switch
            {
                2 => new[] { g, "rest", "rest", g, "rest", "recovery", "rest" },
                3 => new[] { g, "rest", g, "rest", g, "recovery", "rest" },
                4 => new[] { g, "home", "rest", g, "home", "recovery", "rest" },
                5 => new[] { g, "home", g, "home", g, "recovery", "rest" },
                6 => new[] { g, "home", g, "home", g, "home", "recovery" },
                7 => new[] { g, "home", g, "home", g, "recovery", "rest" },
                _ => new[] { g, "rest", g, "rest", g, "recovery", "rest" },
            };
        }

        private List<(string exId, string category, string? rx)> GetExercisesForDayTypeCustom(
            string dayType, HashSet<string> selectedIds, Dictionary<string, Exercise> allExercises, bool hasGym)
        {
            var result = new List<(string exId, string category, string? rx)>();

            if (dayType == "rest")
            {
                // Rest: minimal mobility only (home-safe picks)
                foreach (var id in new[] { "cars-routine", "deep-squat-hold", "quadruped-rocking" })
                {
                    if (selectedIds.Contains(id) && allExercises.ContainsKey(id))
                        result.Add((id, "mobility", allExercises[id].DefaultRx));
                }
                return result;
            }

            if (dayType == "recovery")
            {
                // Recovery: recovery_tool exercises, filtered for home if no gym
                foreach (var id in selectedIds)
                {
                    if (allExercises.TryGetValue(id, out var ex) && ex.Category == "recovery_tool")
                    {
                        // If no gym, skip gym-only recovery tools
                        if (!hasGym && !IsHomeAppropriate(id)) continue;
                        result.Add((id, ex.Category, ex.DefaultRx));
                    }
                }
                // For home recovery days, also add a light mobility routine
                if (!hasGym)
                {
                    foreach (var id in new[] { "cars-routine", "open-book", "quadruped-rocking" })
                    {
                        if (selectedIds.Contains(id) && allExercises.ContainsKey(id) && !result.Any(r => r.exId == id))
                            result.Add((id, "mobility", allExercises[id].DefaultRx));
                    }
                }
                return result;
            }

            // Gym or Home training day
            foreach (var id in selectedIds)
            {
                if (!allExercises.TryGetValue(id, out var ex)) continue;

                if (dayType == "gym")
                {
                    // Gym: warmup + mobility + recovery tools (no strength — this is a joint/mobility program)
                    if (ex.Category == "warmup_tool" || ex.Category == "mobility" || ex.Category == "recovery_tool")
                        result.Add((id, ex.Category, ex.DefaultRx));
                }
                else if (dayType == "home")
                {
                    // Home: mobility only, home-appropriate exercises
                    if (ex.Category == "mobility" && IsHomeAppropriate(id))
                        result.Add((id, ex.Category, ex.DefaultRx));
                }
            }

            // Sort: warmup_tool → mobility → recovery_tool
            var catOrder = new Dictionary<string, int> { ["warmup_tool"] = 0, ["mobility"] = 1, ["recovery_tool"] = 2 };
            result.Sort((a, b) => catOrder.GetValueOrDefault(a.category, 9).CompareTo(catOrder.GetValueOrDefault(b.category, 9)));

            return result;
        }
    }
}
