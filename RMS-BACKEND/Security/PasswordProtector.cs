using Microsoft.AspNetCore.Identity;
using RMS_BACKEND.Models;

namespace RMS_BACKEND.Security;

/// <summary>Hashes new credentials and verifies both hashed credentials and legacy plaintext rows.</summary>
public static class PasswordProtector
{
    private static readonly PasswordHasher<Employee> Hasher = new();

    public static string Hash(Employee employee, string password) => Hasher.HashPassword(employee, password);

    public static bool Verify(Employee employee, string password)
    {
        if (employee.Password.StartsWith("AQAAAA", StringComparison.Ordinal))
            return Hasher.VerifyHashedPassword(employee, employee.Password, password) is
                PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;

        // Existing accounts are intentionally left untouched by demo seeding.
        return string.Equals(employee.Password, password, StringComparison.Ordinal);
    }
}
