using System.Security.Cryptography;
using RMS_BACKEND.Models;
using RMS_BACKEND.Security;
using Xunit;

namespace RMS_BACKEND.Tests;

public class DemoDataSeederTests
{
    [Fact]
    public void Demo_plan_has_six_unique_accounts_and_eight_pending_requests()
    {
        Assert.Equal(6, DemoDataSeeder.PlannedEmployeeCount);
        Assert.Equal(8, DemoDataSeeder.PlannedRequestCount);
        Assert.Equal(6, DemoDataSeeder.PlannedEmployeeCodes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=RMS;Trusted_Connection=True", true)]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Other;Trusted_Connection=True", true)]
    [InlineData(@"Server=localhost;Database=RMS;Trusted_Connection=True", true)]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=RMS;Trusted_Connection=True", false)]
    public void Target_guard_allows_only_development_localdb_rms(string connectionString, bool isDevelopment)
    {
        if (connectionString.Contains(@"Server=(localdb)\MSSQLLocalDB;Database=RMS", StringComparison.Ordinal) && isDevelopment)
            DemoDataSeeder.ValidateTarget(connectionString, isDevelopment);
        else
            Assert.Throws<InvalidOperationException>(() => DemoDataSeeder.ValidateTarget(connectionString, isDevelopment));
    }

    [Fact]
    public void Demo_password_is_hashed_and_verifiable_without_storing_plaintext()
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var employee = new Employee { Code = "RMS-DEMO-V1-TEST" };
        employee.Password = PasswordProtector.Hash(employee, password);

        Assert.NotEqual(password, employee.Password);
        Assert.StartsWith("AQAAAA", employee.Password, StringComparison.Ordinal);
        Assert.True(PasswordProtector.Verify(employee, password));
        Assert.False(PasswordProtector.Verify(employee, password + "x"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Empty_or_complete_demo_set_is_safe_to_process(int existingCount) =>
        DemoDataSeeder.ValidateExistingDemoEmployeeCount(existingCount);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void Partial_or_unexpected_demo_set_is_rejected(int existingCount) =>
        Assert.Throws<InvalidOperationException>(() => DemoDataSeeder.ValidateExistingDemoEmployeeCount(existingCount));
}
