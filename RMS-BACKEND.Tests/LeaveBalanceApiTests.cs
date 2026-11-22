using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RMS_BACKEND.Data;
using RMS_BACKEND.DTOs;
using RMS_BACKEND.Models;
using RMS_BACKEND.Security;
using RMS_BACKEND.Services;
using Xunit;

namespace RMS_BACKEND.Tests;

public class LeaveBalanceApiTests
{
    private sealed class RmsFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        @"Server=(localdb)\MSSQLLocalDB;Database=RMS;Trusted_Connection=True;TrustServerCertificate=True;"
                }));
        }
    }

    [Fact]
    public async Task Manager_then_HR_final_approval_refreshes_the_employee_balance_API_once()
    {
        const int requestId = 2000000200;
        const int employeeId = 4;
        using var factory = new RmsFactory();
        using var employeeClient = factory.CreateClient();
        using var managerClient = factory.CreateClient();
        using var hrClient = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var sessionIds = new List<Guid>();
        try
        {
            db.Transactions.Add(new Transaction
            {
                Id = requestId, EmployeeId = employeeId, TransactionTypesID = 2,
                StartDate = new DateTime(2026, 12, 28), EndDate = new DateTime(2026, 12, 28),
                StatusID = TransactionStatus.Pending, CreationDate = DateTime.UtcNow,
                LeaveRationale = "RMS_AUTOTEST_BALANCE_API"
            });
            await db.SaveChangesAsync();

            var employeeToken = await sessions.IssueAsync(employeeId);
            var managerToken = await sessions.IssueAsync(3);
            var hrToken = await sessions.IssueAsync(1);
            sessionIds.Add(await SessionIdForTokenAsync(db, employeeToken));
            sessionIds.Add(await SessionIdForTokenAsync(db, managerToken));
            sessionIds.Add(await SessionIdForTokenAsync(db, hrToken));
            employeeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", employeeToken);
            managerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            hrClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hrToken);

            var before = await employeeClient.GetFromJsonAsync<LeaveBalanceDto>("/api/leavebalance/my-balance?asOfDate=2026-11-22");
            Assert.NotNull(before);
            using var managerApproval = await managerClient.PostAsJsonAsync($"/api/transactions/{requestId}/approve", new { responseMessage = "manager" });
            Assert.Equal(HttpStatusCode.OK, managerApproval.StatusCode);
            var afterManager = await employeeClient.GetFromJsonAsync<LeaveBalanceDto>("/api/leavebalance/my-balance?asOfDate=2026-11-22");
            Assert.Equal(before!.LeaveBalance, afterManager!.LeaveBalance);

            using var hrApproval = await hrClient.PostAsJsonAsync($"/api/transactions/{requestId}/approve", new { responseMessage = "HR final" });
            Assert.Equal(HttpStatusCode.OK, hrApproval.StatusCode);
            var afterHr = await employeeClient.GetFromJsonAsync<LeaveBalanceDto>("/api/leavebalance/my-balance?asOfDate=2026-11-22");
            Assert.Equal(-1.0, afterHr!.LeaveBalance - before.LeaveBalance, precision: 5);

            using var duplicate = await hrClient.PostAsJsonAsync($"/api/transactions/{requestId}/approve", new { responseMessage = "repeat" });
            Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
            var afterDuplicate = await employeeClient.GetFromJsonAsync<LeaveBalanceDto>("/api/leavebalance/my-balance?asOfDate=2026-11-22");
            Assert.Equal(afterHr.LeaveBalance, afterDuplicate!.LeaveBalance);
        }
        finally
        {
            await db.RequestDecisionAudit.Where(a => a.TransactionId == requestId).ExecuteDeleteAsync();
            await db.Transactions.Where(t => t.Id == requestId).ExecuteDeleteAsync();
            await db.AuthSessions.Where(s => sessionIds.Contains(s.Id)).ExecuteDeleteAsync();
        }
    }

    private static async Task<Guid> SessionIdForTokenAsync(ApplicationDbContext db, string token)
    {
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') +
            new string('=', (4 - token.Length % 4) % 4));
        var hash = SHA256.HashData(bytes);
        return await db.AuthSessions.Where(session => session.TokenHash == hash).Select(session => session.Id).SingleAsync();
    }
}
