using System;

internal static class Extensions
{
	public static string Escape(this string text)
	{
		// best effort to escape relevant platforms.
		if (Environment.OSVersion.Platform == PlatformID.Unix)
			return text.Replace("\"", "\\\"").Replace(" ", "\\ "); // bash escaping.
		else
			return text.Replace("\"", "\\`\"").Replace(" ", "\\ "); // powershell escaping.
	}
}