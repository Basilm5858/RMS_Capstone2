using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using RMS_BACKEND.Data;
using RMS_BACKEND.Models;
using RMS_BACKEND.Repositories;
using RMS_BACKEND.Services;
using Xunit;

namespace RMS_BACKEND.Tests;

public class LeaveBalanceBehaviorTests
{
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static ApplicationDbContext NewContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(@"Server=(localdb)\MSSQLLocalDB;Database=RMS;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options);

    [Trait("Category", "LeaveBalance")]
    [Theory]
    [InlineData(4, "2026-09-15", "2026-09-15", "2026-10-04", 1.0)]
    [InlineData(2, "2025-12-30", "2026-01-03", "2026-01-02", -3.0)]
    [InlineData(2, "2026-09-29", "2026-10-05", "2026-10-02", -7.0)]
    public async Task Approved_request_reserves_all_signed_days_in_selected_year_when_final(
        int typeId, string start, string end, string asOf, double expectedChange)
    {
        await using var db = NewContext();
        Assert.Equal("RMS", db.Database.GetDbConnection().Database);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var balance = new LeaveBalanceService(db, new LeaveCalculationService());
            var before = await balance.GetLeaveBalanceAsync(4, DateTime.Parse(asOf));
            db.Transactions.Add(new Transaction
            {
                Id = 2000000000,
                EmployeeId = 4,
                TransactionTypesID = typeId,
                StartDate = DateTime.Parse(start),
                EndDate = DateTime.Parse(end),
                StatusID = TransactionStatus.ApprovedByHR,
                CreationDate = DateTime.Parse("2025-12-01")
            });
            await db.SaveChangesAsync();
            var after = await balance.GetLeaveBalanceAsync(4, DateTime.Parse(asOf));
            Assert.Equal(expectedChange, after.LeaveBalance - before.LeaveBalance, precision: 5);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Trait("Category", "LeaveBalance")]
    [Fact]
    public async Task Final_approval_immediately_reduces_balance_once_while_nonfinal_and_final_rejections_do_not()
    {
        await using var db = NewContext();
        const int requestId = 2000000100;
        try
        {
            const int employeeId = 4;
            var balance = new LeaveBalanceService(db, new LeaveCalculationService());
            var before = await balance.GetLeaveBalanceAsync(employeeId, new DateTime(2026, 11, 22));
            db.Transactions.AddRange(
                new Transaction { Id = requestId, EmployeeId = employeeId, TransactionTypesID = 2,
                    StartDate = new DateTime(2026, 12, 20), EndDate = new DateTime(2026, 12, 21),
                    StatusID = TransactionStatus.Pending, CreationDate = DateTime.UtcNow, LeaveRationale = "RMS_AUTOTEST_BALANCE_APPROVAL" },
                new Transaction { Id = requestId + 1, EmployeeId = employeeId, TransactionTypesID = 2,
                    StartDate = new DateTime(2026, 12, 22), EndDate = new DateTime(2026, 12, 22),
                    StatusID = TransactionStatus.RejectedByHR, CreationDate = DateTime.UtcNow, LeaveRationale = "RMS_AUTOTEST_BALANCE_REJECTED" },
                new Transaction { Id = requestId + 2, EmployeeId = employeeId, TransactionTypesID = 2,
                    StartDate = new DateTime(2026, 12, 23), EndDate = new DateTime(2026, 12, 23),
                    StatusID = TransactionStatus.CancelledByEmployee, CreationDate = DateTime.UtcNow, LeaveRationale = "RMS_AUTOTEST_BALANCE_CANCELLED" });
            await db.SaveChangesAsync();
            Assert.Equal(before.LeaveBalance, (await balance.GetLeaveBalanceAsync(employeeId, new DateTime(2026, 11, 22))).LeaveBalance);

            var service = new TransactionService(new TransactionRepository(db), new EmployeeRepository(db),
                new RequestStateMachineService(), new LeaveCalculationService(),
                new TransactionRequestValidator(db, balance, new LeaveCalculationService()), db);
            await service.ApproveTransactionAsync(1, requestId, "final approval");
            var after = await balance.GetLeaveBalanceAsync(employeeId, new DateTime(2026, 11, 22));
            Assert.Equal(-2.0, after.LeaveBalance - before.LeaveBalance, precision: 5);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveTransactionAsync(1, requestId, "repeat"));
            Assert.Equal(after.LeaveBalance, (await balance.GetLeaveBalanceAsync(employeeId, new DateTime(2026, 11, 22))).LeaveBalance);
        }
        finally
        {
            await db.RequestDecisionAudit.Where(a => a.TransactionId == requestId).ExecuteDeleteAsync();
            await db.Transactions.Where(t => t.Id >= requestId && t.Id <= requestId + 2).ExecuteDeleteAsync();
        }
    }

    [Trait("Category", "LeaveBalance")]
    [Fact]
    public async Task New_hire_in_probation_has_no_accrual()
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var employee = new Employee
            {
                Id = 2000000000,
                Code = "RMS_TEST_PROBATION",
                Name = "Temporary test employee",
                DepartmentID = 7,
                EmployeeLevelId = 1,
                EmployeeRole = EmployeeRole.Employee,
                DateOfEmployment = new DateTime(2026, 7, 1)
            };
            employee.Password = "synthetic-test-password";
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            var balance = await new LeaveBalanceService(db, new LeaveCalculationService())
                .GetLeaveBalanceAsync(employee.Id, new DateTime(2026, 10, 4));
            Assert.True(balance.IsInProbation);
            Assert.Equal(0, balance.TotalAccruedLeave);
            Assert.Equal(0, balance.LeaveBalance);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Trait("Category", "Performance")]
    [Fact]
    public async Task All_leave_balances_use_a_bounded_number_of_database_queries()
    {
        var counter = new CountingCommandInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(@"Server=(localdb)\MSSQLLocalDB;Database=RMS;Trusted_Connection=True;TrustServerCertificate=True;")
            .AddInterceptors(counter)
            .Options;
        await using var db = new ApplicationDbContext(options);
        Assert.Equal("RMS", db.Database.GetDbConnection().Database);

        var balances = await new LeaveBalanceService(db, new LeaveCalculationService())
            .GetAllLeaveBalancesAsync(new DateTime(2026, 10, 5));

        Assert.True(balances.Count >= 5);
        Assert.InRange(counter.CommandCount, 1, 3);
    }
}
