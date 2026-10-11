using System.Security.Cryptography;
using System.Text;
using Nighty.Models;

namespace Nighty.Services;

/// <summary>
/// Owner sign-in. Nothing readable is stored: the user name and password are never in the app, only slow salted hashes
/// (PBKDF2-SHA256, 250,000 rounds) of "user + password", so they can neither be read nor guessed quickly from the file.
/// A repeated wrong sign-in is made to wait longer each time. "Stay signed in" keeps a token encrypted with Windows' own
/// per-user protection, which cannot be copied to another account or PC.
/// This guards features inside a program that runs on the owner's own machine, so it keeps casual users out; it is not a
/// defence against someone who rewrites the program itself.
/// </summary>
public static class OwnerService
{
    private const int Rounds = 250_000;
    private static readonly byte[] Salt = Convert.FromBase64String("q8fbf8nNysXIRzAIlCawnA==");
    private static readonly byte[][] Accepted =
    {
        Convert.FromBase64String("g8NAw/gRqntv6ojNqm01RsdT4S3ooMYgQVIl4TCwMLo="),
        Convert.FromBase64String("9B7kknxvV7L7ZsgpRmJdgEE76F5FJc5oMloealWjL5w="),
    };

    private static int _failures;
    private static DateTime _lockedUntil = DateTime.MinValue;

    public static bool IsOwner { get; private set; }
    public static event Action? Changed;

    /// <summary>Seconds left on the wrong-password wait, or 0.</summary>
    public static int LockSeconds => Math.Max(0, (int)Math.Ceiling((_lockedUntil - DateTime.UtcNow).TotalSeconds));

    private static byte[] Derive(string user, string password)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(user.Trim().ToLowerInvariant() + "\u001F" + password), Salt, Rounds, HashAlgorithmName.SHA256, 32);

    private static bool Matches(byte[] hash)
    {
        bool ok = false;
        foreach (var a in Accepted) ok |= CryptographicOperations.FixedTimeEquals(a, hash);   // no early exit
        return ok;
    }

    /// <summary>Checks the details. Returns null on success, otherwise a message to show.</summary>
    public static string? SignIn(string user, string password, bool stay)
    {
        if (LockSeconds > 0) return $"Too many tries. Wait {LockSeconds} s.";
        var hash = Derive(user ?? "", password ?? "");
        if (!Matches(hash))
        {
            _failures++;
            if (_failures >= 3) _lockedUntil = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, _failures - 3)));
            Log.Warn("Owner sign-in failed");
            return "That user name or password is not right.";
        }
        _failures = 0;
        IsOwner = true;
        Svc.S.General.OwnerToken = stay ? Protect(hash) : null;
        Svc.Settings.Save();
        Log.Info("Owner signed in");
        Changed?.Invoke();
        return null;
    }

    public static void SignOut(bool forget = true)
    {
        IsOwner = false;
        if (forget) Svc.S.General.OwnerToken = null;
        Svc.Settings.Save();
        Changed?.Invoke();
        Seasons.Roll();   // a seasonal theme the owner was using out of season goes back right away
    }

    /// <summary>At start-up: a valid protected token from "Stay signed in" signs the owner straight back in.</summary>
    public static void RestoreRemembered()
    {
        var token = Svc.S.General.OwnerToken;
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(token), Salt, DataProtectionScope.CurrentUser);
            IsOwner = Matches(plain);
        }
        catch { IsOwner = false; Svc.S.General.OwnerToken = null; }
    }

    private static string Protect(byte[] hash) => Convert.ToBase64String(ProtectedData.Protect(hash, Salt, DataProtectionScope.CurrentUser));
}
