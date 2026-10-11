namespace Nighty.Services;

/// <summary>Shows a version as 2.1.1, or 2.1.1.1 when it is a hotfix with a fourth number.</summary>
public static class AppVersion
{
    public static string Text(Version? v) => v == null ? "" : v.Revision > 0 ? v.ToString(4) : v.ToString(3);
}
