using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RMS_BACKEND.Data;
using RMS_BACKEND.Models;
using RMS_BACKEND.Services;

namespace RMS_BACKEND.Security;

public static class DemoDataSeeder
{
    public const string Marker = "[RMS-DEMO-V1]";
    private const string ManagerCode = "RMS-DEMO-V1-MGR";
    private static readonly string[] EmployeeCodes = [
        "RMS-DEMO-V1-E01", "RMS-DEMO-V1-E02", "RMS-DEMO-V1-E03", "RMS-DEMO-V1-E04", "RMS-DEMO-V1-E05"
    ];

    private sealed record EmployeeSeed(string Code, string Name, bool IsManager);
    private sealed record RequestSeed(string EmployeeCode, int TypeId, int StartOffsetDays, int EndOffsetDays, string Scenario);

    private static readonly EmployeeSeed[] Employees = [
        new(ManagerCode, $"{Marker} Demo Manager", true),
        new(EmployeeCodes[0], $"{Marker} Demo Employee 01", false),
        new(EmployeeCodes[1], $"{Marker} Demo Employee 02", false),
        new(EmployeeCodes[2], $"{Marker} Demo Employee 03", false),
        new(EmployeeCodes[3], $"{Marker} Demo Employee 04", false),
        new(EmployeeCodes[4], $"{Marker} Demo Employee 05", false),
    ];

    // Pending-only records deliberately avoid fabricating approval/rejection history or audit actors.
    private static readonly RequestSeed[] Requests = [
        new(ManagerCode, 2, 14, 15, "annual leave"),
        new(EmployeeCodes[0], 2, 21, 22, "annual leave"),
        new(EmployeeCodes[1], 3, 28, 28, "half-day leave"),
        new(EmployeeCodes[2], 5, 35, 35, "unpaid leave"),
        new(EmployeeCodes[3], 4, 42, 42, "bonus leave"),
        new(EmployeeCodes[4], 2, 49, 49, "annual leave"),
        new(EmployeeCodes[0], 4, 56, 56, "bonus leave follow-up request"),
        new(EmployeeCodes[2], 3, 63, 63, "half-day leave follow-up"),
    ];

    public static int PlannedEmployeeCount => Employees.Length;
    public static int PlannedRequestCount => Requests.Length;
    public static IReadOnlyList<string> PlannedEmployeeCodes => Employees.Select(employee => employee.Code).ToArray();

    public static void ValidateTarget(string connectionString, bool isDevelopment)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!isDevelopment)
            throw new InvalidOperationException("Demo seeding is restricted to the Development environment.");
        if (!string.Equals(builder.DataSource, @"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(builder.InitialCatalog, "RMS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Demo seeding is restricted to (localdb)\\MSSQLLocalDB / RMS.");
    }

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is missing.");
        ValidateTarget(connectionString, environment.IsDevelopment());

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!string.Equals(db.Database.GetDbConnection().Database, "RMS", StringComparison.Ordinal))
            throw new InvalidOperationException("Demo seeding is restricted to RMS.");

        await EnsurePrerequisitesAsync(db);
        var existing = await db.Employees.Where(e => Employees.Select(seed => seed.Code).Contains(e.Code))
            .ToDictionaryAsync(e => e.Code, StringComparer.Ordinal);
        ValidateExistingDemoEmployeeCount(existing.Count);

        if (existing.Count == 0)
        {
            if (Console.IsInputRedirected)
                throw new InvalidOperationException("An interactive console is required to set the demo password.");
            var password = ReadAndConfirmPassword();
            await CreateEmployeesAsync(db, password);
            existing = await db.Employees.Where(e => Employees.Select(seed => seed.Code).Contains(e.Code))
                .ToDictionaryAsync(e => e.Code, StringComparer.Ordinal);
        }

        var transactionService = scope.ServiceProvider.GetRequiredService<ITransactionService>();
        await CreateMissingPendingRequestsAsync(db, transactionService, existing);
        Console.WriteLine($"Demo seed complete: {existing.Count} demo accounts and {Requests.Length} planned pending requests. Passwords were not displayed.");
        foreach (var employee in Employees)
            Console.WriteLine($"{employee.Code} — {(employee.IsManager ? "Manager" : "Employee")}");
    }

    private static async Task EnsurePrerequisitesAsync(ApplicationDbContext db)
    {
        var hasQuality = await db.Statuses.AnyAsync(s => s.Id == 7 && s.StatusType == "Department");
        var hasPending = await db.Statuses.AnyAsync(s => s.Id == TransactionStatus.Pending && s.StatusType == "TransactionStatus");
        var hasLevel = await db.EmployeeLevels.AnyAsync(l => l.Id == 2);
        var requiredTypes = new[] { 2, 3, 4, 5 };
        var availableTypes = await db.TransactionTypes.Where(t => requiredTypes.Contains(t.Id)).Select(t => t.Id).ToListAsync();
        if (!hasQuality || !hasPending || !hasLevel || availableTypes.Count != requiredTypes.Length)
            throw new InvalidOperationException("Required RMS lookup data is missing; run the documented database setup first.");
    }

    public static void ValidateExistingDemoEmployeeCount(int count)
    {
        if (count is > 0 and < 6)
            throw new InvalidOperationException("Only part of the RMS demo employee set exists; refusing to alter it.");
        if (count > 6)
            throw new InvalidOperationException("Unexpected RMS demo employee records were found; refusing to alter them.");
    }

    private static async Task CreateEmployeesAsync(ApplicationDbContext db, string password)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var manager = new Employee
        {
            Id = await SequenceIdAllocator.NextEmployeeIdAsync(db), Code = ManagerCode,
            Name = Employees[0].Name, DepartmentID = 7, EmployeeLevelId = 2,
            EmployeeRole = EmployeeRole.Manager, DateOfEmployment = DateTime.Today.AddYears(-2), IsDeleted = false
        };
        manager.Password = PasswordProtector.Hash(manager, password);
        db.Employees.Add(manager);
        await db.SaveChangesAsync();

        foreach (var seed in Employees.Skip(1))
        {
            var employee = new Employee
            {
                Id = await SequenceIdAllocator.NextEmployeeIdAsync(db), Code = seed.Code,
                Name = seed.Name, DepartmentID = 7, EmployeeLevelId = 2, ManagerId = manager.Id,
                EmployeeRole = EmployeeRole.Employee, DateOfEmployment = DateTime.Today.AddYears(-2), IsDeleted = false
            };
            employee.Password = PasswordProtector.Hash(employee, password);
            db.Employees.Add(employee);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task CreateMissingPendingRequestsAsync(ApplicationDbContext db, ITransactionService service,
        IReadOnlyDictionary<string, Employee> employees)
    {
        foreach (var seed in Requests)
        {
            var rationale = $"{Marker} Pending {seed.Scenario}";
            var employee = employees[seed.EmployeeCode];
            if (await db.Transactions.AnyAsync(t => t.EmployeeId == employee.Id && t.LeaveRationale == rationale)) continue;
            var start = DateTime.Today.AddDays(seed.StartOffsetDays);
            await service.CreateTransactionAsync(employee.Id, new DTOs.CreateTransactionRequestDto
            {
                TransactionTypesID = seed.TypeId, StartDate = start, EndDate = DateTime.Today.AddDays(seed.EndOffsetDays),
                LeaveRationale = rationale
            });
        }
    }

    private static string ReadAndConfirmPassword()
    {
        Console.Write("Shared demo password (9–128 characters; hidden): ");
        var password = ReadSecret();
        Console.Write("Confirm shared demo password (hidden): ");
        var confirmation = ReadSecret();
        if (password.Length is < 9 or > 128 || password != confirmation)
            throw new InvalidOperationException("Passwords differ or violate the 9–128 character requirement.");
        return password;
    }

    private static string ReadSecret()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar) && chars.Count < 128) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray());
    }
}
